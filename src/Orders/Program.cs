using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;
using Orders;

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.ConfigureKestrel(options => options.Limits.MaxRequestBodySize = 4096);
var role = builder.Configuration["Role"] ?? "api";
if (role is not ("api" or "worker")) throw new ArgumentException("Role must be api or worker.");
var worker = role == "worker";
if (!bool.TryParse(builder.Configuration["DispatcherEnabled"] ?? "true", out var dispatcherEnabled))
    throw new ArgumentException("DispatcherEnabled must be true or false.");
_ = DaprSettings.Load(builder.Configuration);
builder.Services.AddSingleton<ILedgerStore, DaprLedgerStore>();
builder.Services.AddSingleton<IEventPublisher, DaprPublisher>();
builder.Services.AddSingleton<OrdersEngine>();
var endpointText = builder.Configuration["Dapr:Endpoint"] ?? "http://127.0.0.1:3500";
if (!Uri.TryCreate(endpointText, UriKind.Absolute, out var daprEndpoint) || daprEndpoint.Scheme is not ("http" or "https") || string.IsNullOrEmpty(daprEndpoint.Host) ||
    daprEndpoint.UserInfo.Length != 0 || daprEndpoint.Query.Length != 0 || daprEndpoint.Fragment.Length != 0 || daprEndpoint.AbsolutePath != "/")
    throw new ArgumentException("Dapr Endpoint must be an HTTP(S) origin without credentials, query, fragment or path.");
if (!builder.Environment.IsDevelopment() && string.IsNullOrWhiteSpace(builder.Configuration["DAPR_API_TOKEN"]))
    throw new ArgumentException("DAPR_API_TOKEN is required outside Development.");
builder.Services.AddHttpClient("dapr", client =>
{
    client.BaseAddress = daprEndpoint;
    client.Timeout = TimeSpan.FromSeconds(5);
    var token = builder.Configuration["DAPR_API_TOKEN"];
    if (!string.IsNullOrEmpty(token)) client.DefaultRequestHeaders.Add("dapr-api-token", token);
});
var authority = builder.Configuration["Auth:Authority"] ?? "http://localhost:4322/realms/orders";
var metadataAddress = builder.Configuration["Auth:MetadataAddress"] ?? authority + "/.well-known/openid-configuration";
if (!worker)
{
    foreach (var address in new[] { authority, metadataAddress })
        if (!Uri.TryCreate(address, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") || uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0 ||
            (!builder.Environment.IsDevelopment() && uri.Scheme != "https"))
            throw new ArgumentException("OIDC authority and metadata must be valid HTTPS URLs outside Development.");
}
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer(options =>
{
    options.Authority = authority;
    options.MetadataAddress = metadataAddress;
    options.RequireHttpsMetadata = !builder.Environment.IsDevelopment();
    options.MapInboundClaims = false;
    options.TokenValidationParameters = new TokenValidationParameters
    {
        ValidateIssuer = true, ValidIssuer = options.Authority, ValidateAudience = true, ValidAudience = "orders-api",
        ValidateLifetime = true, ValidateIssuerSigningKey = true, RoleClaimType = "roles", NameClaimType = "sub", ClockSkew = TimeSpan.FromSeconds(15)
    };
});
builder.Services.AddAuthorization(options =>
{
    options.AddPolicy("write", policy => policy.RequireAuthenticatedUser().RequireClaim("sub").RequireRole("orders_writer"));
    options.AddPolicy("read", policy => policy.RequireAuthenticatedUser().RequireClaim("sub").RequireRole("orders_reader", "orders_writer"));
});
if (!worker && dispatcherEnabled) builder.Services.AddHostedService<OutboxDispatcher>();
var app = builder.Build();
app.Use(async (context, next) =>
{
    context.Response.Headers["X-Content-Type-Options"] = "nosniff";
    context.Response.Headers["Cache-Control"] = "no-store";
    try { await next(context); }
    catch (InvalidOrder) { await Problem(context, 400, "Invalid order or Idempotency-Key"); }
    catch (IdempotencyConflict) { await Problem(context, 409, "Idempotency-Key already used for different content"); }
    catch (LedgerCapacity) { await Problem(context, 503, "Order capacity reached; contact the operator"); }
    catch (LedgerBusy) { context.Response.Headers.RetryAfter = "1"; await Problem(context, 503, "State is busy; retry with the same Idempotency-Key"); }
    catch (HttpRequestException) { await Problem(context, 503, "Dependency unavailable; retry with the same Idempotency-Key"); }
    catch (OperationCanceledException) when (!context.RequestAborted.IsCancellationRequested) { await Problem(context, 503, "Dependency timed out; retry with the same Idempotency-Key"); }
});
app.UseAuthentication(); app.UseAuthorization();
app.MapGet("/health/live", () => Results.Ok(new { status = "live", role = worker ? "worker" : "api" }));
app.MapGet("/health/ready", async (ILedgerStore store, CancellationToken ct) => { await store.Read(ct); return Results.Ok(new { status = "ready" }); });
if (worker)
{
    var callbackToken = builder.Configuration["APP_API_TOKEN"];
    if (string.IsNullOrWhiteSpace(callbackToken)) throw new InvalidOperationException("APP_API_TOKEN must be non-empty for worker callbacks");
    app.Use(async (context, next) =>
    {
        if (context.Request.Path.StartsWithSegments("/dapr") || context.Request.Path.StartsWithSegments("/events"))
        {
            var supplied = context.Request.Headers["dapr-api-token"].ToString();
            if (!CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(supplied), Encoding.UTF8.GetBytes(callbackToken)))
            { context.Response.StatusCode = 401; return; }
        }
        await next(context);
    });
    app.MapGet("/dapr/subscribe", () => Results.Json(new[] { new { pubsubname = "orders-pubsub", topic = "orders.accepted", route = "/events/orders" } }));
    app.MapPost("/events/orders", async (JsonElement envelope, OrdersEngine engine, CancellationToken ct) =>
    {
        if (envelope.ValueKind != JsonValueKind.Object || !envelope.TryGetProperty("data", out var data)) return Results.Ok(new { status = "DROP" });
        OrderEvent? message;
        try { message = data.Deserialize<OrderEvent>(new JsonSerializerOptions(JsonSerializerDefaults.Web)); }
        catch (JsonException) { return Results.Ok(new { status = "DROP" }); }
        return Results.Ok(new { status = message is null ? "DROP" : await engine.Process(message, ct) });
    });
}
else
{
    app.MapPost("/orders", async (CreateOrder input, HttpContext context, OrdersEngine engine, CancellationToken ct) =>
    {
        var result = await engine.Submit(context.User.FindFirstValue("sub")!, context.Request.Headers["Idempotency-Key"].ToString(), input, ct);
        return result.Created ? Results.Accepted($"/orders/{result.Order.Id}", result.Order) : Results.Ok(result.Order);
    }).RequireAuthorization("write");
    app.MapGet("/orders/{id}", async (string id, ClaimsPrincipal user, OrdersEngine engine, CancellationToken ct) =>
        await engine.Get(user.FindFirstValue("sub")!, id, ct) is { } order ? Results.Ok(order) : Results.NotFound()).RequireAuthorization("read");
}
app.Run();
static Task Problem(HttpContext context, int status, string title) => Results.Problem(statusCode: status, title: title).ExecuteAsync(context);
public partial class Program { }
