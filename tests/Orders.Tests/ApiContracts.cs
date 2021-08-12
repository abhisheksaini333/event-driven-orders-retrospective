using System.Net;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Logging.Abstractions;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Microsoft.IdentityModel.Tokens;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Orders;
using Xunit;
namespace Orders.Tests;
public sealed class FailingStore(Exception failure) : ILedgerStore
{
    public Task<Snapshot> Read(CancellationToken ct) => throw failure;
    public Task<bool> CompareExchange(Ledger value, string etag, CancellationToken ct) => throw failure;
}
public class ApiContracts
{
    private static async Task<HttpResponseMessage> FailureResponse(Exception failure)
    {
        using var factory = new OrdersFactory();
        using var configured = factory.WithWebHostBuilder(builder => builder.ConfigureServices(services => {
            services.RemoveAll<ILedgerStore>(); services.AddSingleton<ILedgerStore>(new FailingStore(failure));
        }));
        using var client = configured.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", OrdersFactory.Token("orders_writer"));
        client.DefaultRequestHeaders.Add("Idempotency-Key", "failure-key");
        return await client.PostAsJsonAsync("/orders", new CreateOrder("SKU-1", 1));
    }

    [Fact] public async Task CapacityFailureIsSafeServiceUnavailable()
    {
        using var response = await FailureResponse(new LedgerCapacity());
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("capacity", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("StackTrace", body); Assert.DoesNotContain("System.", body);
    }


    [Fact] public void UnknownRoleFailsStartup()
    {
        using var factory = new OrdersFactory();
        using var configured = factory.WithWebHostBuilder(builder => builder.UseSetting("Role", "wroker"));
        Assert.NotNull(Record.Exception(() => { using var client = configured.CreateClient(); }));
    }


    [Fact] public void InvalidDispatcherBooleanFailsStartup()
    {
        using var factory = new OrdersFactory();
        using var invalid = factory.WithWebHostBuilder(builder => builder.UseSetting("DispatcherEnabled", "sometimes"));
        Assert.NotNull(Record.Exception(() => { using var client = invalid.CreateClient(); }));
        using var disabled = factory.WithWebHostBuilder(builder => builder.UseSetting("DispatcherEnabled", "FALSE"));
        using var healthy = disabled.CreateClient();
        Assert.Equal(HttpStatusCode.OK, healthy.GetAsync("/health/live").GetAwaiter().GetResult().StatusCode);
    }


    [Fact] public void InvalidDaprEndpointFailsStartup()
    {
        foreach (var endpoint in new[] { "file:///tmp/state", "http://user:password@localhost", "http://localhost/?token=bad", "http://localhost/#fragment", "http://localhost/prefix" }) {
            using var factory = new OrdersFactory();
            using var configured = factory.WithWebHostBuilder(builder => builder.UseSetting("Dapr:Endpoint", endpoint));
            Assert.NotNull(Record.Exception(() => { using var client = configured.CreateClient(); }));
        }
    }


    [Fact] public void InsecureProductionIssuerFailsStartup()
    {
        using var factory = new OrdersFactory();
        using var configured = factory.WithWebHostBuilder(builder => builder.UseEnvironment("Production").UseSetting("Auth:Authority", "http://issuer.test").UseSetting("DAPR_API_TOKEN", "test-sidecar-token"));
        Assert.NotNull(Record.Exception(() => { using var client = configured.CreateClient(); }));
        using var malformed = factory.WithWebHostBuilder(builder => builder.UseSetting("Auth:MetadataAddress", "file:///tmp/metadata"));
        Assert.NotNull(Record.Exception(() => { using var client = malformed.CreateClient(); }));
    }


    [Fact] public void BlankWorkerCallbackSecretsFailClosed()
    {
        foreach (var secret in new[] { "", "   " }) {
            using var factory = new OrdersFactory();
            using var configured = factory.WithWebHostBuilder(builder => builder.UseSetting("Role", "worker").UseSetting("APP_API_TOKEN", secret));
            Assert.NotNull(Record.Exception(() => { using var client = configured.CreateClient(); }));
        }
    }


    [Fact] public void ProductionRequiresSidecarAuthentication()
    {
        using var factory = new OrdersFactory();
        using var configured = factory.WithWebHostBuilder(builder => builder.UseEnvironment("Production").UseSetting("Auth:Authority", "https://issuer.test").UseSetting("DAPR_API_TOKEN", ""));
        Assert.NotNull(Record.Exception(() => { using var client = configured.CreateClient(); }));
    }


    [Fact] public async Task WorkerTokenBoundaryContract()
    {
        const string secret = "test-worker-callback-token";
        using var factory = new OrdersFactory();
        using var configured = factory.WithWebHostBuilder(builder => builder.UseSetting("Role", "worker").UseSetting("APP_API_TOKEN", secret));
        using var client = configured.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/dapr/subscribe")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync("/events/orders", new { data = new { } })).StatusCode);
        using var duplicated = new HttpRequestMessage(HttpMethod.Get, "/dapr/subscribe");
        duplicated.Headers.Add("dapr-api-token", new[] { secret, secret });
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.SendAsync(duplicated)).StatusCode);
        client.DefaultRequestHeaders.Add("dapr-api-token", secret);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/dapr/subscribe")).StatusCode);
        var dropped = await client.PostAsJsonAsync("/events/orders", new { data = new { } });
        Assert.Equal("DROP", (await dropped.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("status").GetString());
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/orders/anything")).StatusCode);
    }

    private static string Sign(IEnumerable<Claim> claims, string issuer = "https://issuer.test", DateTime? notBefore = null, bool unsigned = false) =>
        new JwtSecurityTokenHandler().WriteToken(new JwtSecurityToken(issuer, "orders-api", claims,
            notBefore ?? DateTime.UtcNow.AddMinutes(-1), DateTime.UtcNow.AddMinutes(15),
            unsigned ? null : new SigningCredentials(OrdersFactory.Key, SecurityAlgorithms.HmacSha256)));
    private static async Task<HttpResponseMessage> SendOrder(HttpClient client, string token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/orders") { Content = JsonContent.Create(new CreateOrder("SKU-1", 1)) };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Add("Idempotency-Key", "contract-" + Guid.NewGuid().ToString("N"));
        return await client.SendAsync(request);
    }

    [Fact] public async Task InvalidSubjectsAreAuthenticationFailures()
    {
        using var factory = new OrdersFactory(); using var client = factory.CreateClient();
        foreach (var subjects in new[] { Array.Empty<string>(), new[] { "" }, new[] { "alice", "bob" }, new[] { new string('a', 257) } }) {
            var claims = subjects.Select(subject => new Claim("sub", subject)).Append(new Claim("roles", "orders_writer"));
            Assert.Equal(HttpStatusCode.Unauthorized, (await SendOrder(client, Sign(claims))).StatusCode);
        }
    }


    [Fact] public async Task JwtIssuerActivationAndSignatureContracts()
    {
        using var factory = new OrdersFactory(); using var client = factory.CreateClient();
        var claims = new[] { new Claim("sub", "alice"), new Claim("roles", "orders_writer") };
        var tokens = new[] { Sign(claims, issuer: "https://other.test"), Sign(claims, notBefore: DateTime.UtcNow.AddMinutes(10)), Sign(claims, unsigned: true) };
        foreach (var token in tokens) Assert.Equal(HttpStatusCode.Unauthorized, (await SendOrder(client, token)).StatusCode);
    }


    [Fact] public async Task OrderReadAuthorizationMatrix()
    {
        using var factory = new OrdersFactory(); using var client = factory.CreateClient();
        var submitted = await SendOrder(client, OrdersFactory.Token("orders_writer"));
        var order = await submitted.Content.ReadFromJsonAsync<Order>(); Assert.NotNull(order);
        foreach (var item in new[] {
            ("orders_reader", "alice", HttpStatusCode.OK), ("orders_writer", "alice", HttpStatusCode.OK),
            ("orders_reader", "bob", HttpStatusCode.NotFound), ("unprivileged", "alice", HttpStatusCode.Forbidden) }) {
            using var request = new HttpRequestMessage(HttpMethod.Get, "/orders/" + order.Id);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", OrdersFactory.Token(item.Item1, item.Item2));
            Assert.Equal(item.Item3, (await client.SendAsync(request)).StatusCode);
        }
    }


    [Fact] public async Task ConfiguredClockSkewRejectsExpiredToken()
    {
        using var factory = new OrdersFactory();
        using var configured = factory.WithWebHostBuilder(builder => builder.UseSetting("Auth:ClockSkewSeconds", "0"));
        using var client = configured.CreateClient();
        var token = new JwtSecurityTokenHandler().WriteToken(new JwtSecurityToken("https://issuer.test", "orders-api",
            new[] { new Claim("sub", "alice"), new Claim("roles", "orders_writer") }, DateTime.UtcNow.AddMinutes(-1), DateTime.UtcNow.AddSeconds(-5),
            new SigningCredentials(OrdersFactory.Key, SecurityAlgorithms.HmacSha256)));
        Assert.Equal(HttpStatusCode.Unauthorized, (await SendOrder(client, token)).StatusCode);
        using var invalid = factory.WithWebHostBuilder(builder => builder.UseSetting("Auth:ClockSkewSeconds", "9999"));
        Assert.NotNull(Record.Exception(() => { using var unused = invalid.CreateClient(); }));
    }


    [Fact] public async Task RuntimeLimitsAreValidatedAndApplied()
    {
        using var factory = new OrdersFactory();
        using var invalid = factory.WithWebHostBuilder(builder => builder.UseSetting("Limits:BatchSize", "0"));
        Assert.NotNull(Record.Exception(() => { using var unused = invalid.CreateClient(); }));
        using var configured = factory.WithWebHostBuilder(builder => builder.UseSetting("Limits:MaximumOrders", "1"));
        using var client = configured.CreateClient();
        Assert.Equal(HttpStatusCode.Accepted, (await SendOrder(client, OrdersFactory.Token("orders_writer"))).StatusCode);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await SendOrder(client, OrdersFactory.Token("orders_writer"))).StatusCode);
    }


    [Fact] public async Task ProblemResponsesHaveStableContracts()
    {
        using var factory = new OrdersFactory(); using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/orders") { Content = JsonContent.Create(new CreateOrder("bad!", 0)) };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", OrdersFactory.Token("orders_writer"));
        request.Headers.Add("Idempotency-Key", "problem-contract");
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType!.MediaType);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("invalid_order", problem.GetProperty("code").GetString());
        Assert.Equal("urn:orders:problem:invalid_order", problem.GetProperty("type").GetString());
        Assert.False(string.IsNullOrEmpty(problem.GetProperty("traceId").GetString()));
        Assert.True(problem.GetProperty("errors").TryGetProperty("sku", out _));
        Assert.True(problem.GetProperty("errors").TryGetProperty("quantity", out _));
    }


    [Fact] public async Task UnexpectedFailuresDoNotDiscloseDetails()
    {
        using var response = await FailureResponse(new InvalidOperationException("sensitive-internal-detail"));
        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("sensitive-internal-detail", body); Assert.DoesNotContain("StackTrace", body);
        Assert.Contains("unexpected_failure", body);
    }

    private static async Task InvokeExceptionMiddleware(HttpContext context, RequestDelegate next)
    {
        var type = typeof(OrdersEngine).Assembly.GetType("Orders.ApiExceptionMiddleware"); Assert.NotNull(type);
        var middleware = Activator.CreateInstance(type, next, NullLoggerFactory.Instance)!;
        await Assert.IsAssignableFrom<Task>(type.GetMethod("InvokeAsync")!.Invoke(middleware, new object[] { context }));
    }

    [Fact] public async Task ClientAbortDoesNotWriteReplacementBody()
    {
        var context = new DefaultHttpContext(); using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        context.RequestAborted = cancellation.Token; context.Response.Body = new MemoryStream();
        await InvokeExceptionMiddleware(context, _ => throw new OperationCanceledException(cancellation.Token));
        Assert.Equal(499, context.Response.StatusCode); Assert.Equal(0, context.Response.Body.Length);
    }


    [Fact] public async Task StartedResponsesAreNotRewritten()
    {
        var context = new DefaultHttpContext(); var response = new StartedResponse(); var lifetime = new ObservedLifetime();
        context.Features.Set<IHttpResponseFeature>(response); context.Features.Set<IHttpRequestLifetimeFeature>(lifetime);
        await InvokeExceptionMiddleware(context, _ => throw new HttpRequestException("late dependency failure"));
        Assert.True(lifetime.Aborted); Assert.Equal(200, response.StatusCode); Assert.Equal(0, response.Body.Length);
    }


    [Fact] public async Task ReadinessReportsItsStateDependencyScope()
    {
        using var factory = new OrdersFactory(); using var client = factory.CreateClient();
        var ready = await client.GetFromJsonAsync<JsonElement>("/health/ready");
        Assert.Equal("state", ready.GetProperty("checked")[0].GetString());
        using var unavailable = factory.WithWebHostBuilder(builder => builder.ConfigureServices(services => {
            services.RemoveAll<ILedgerStore>(); services.AddSingleton<ILedgerStore>(new FailingStore(new HttpRequestException("offline")));
        }));
        using var failedClient = unavailable.CreateClient();
        Assert.Equal(HttpStatusCode.OK, (await failedClient.GetAsync("/health/live")).StatusCode);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await failedClient.GetAsync("/health/ready")).StatusCode);
    }


    [Fact] public async Task RecoverableFailuresHaveRetryAfter()
    {
        foreach (var failure in new Exception[] { new HttpRequestException("offline"), new TaskCanceledException("dependency timeout"), new LedgerBusy() }) {
            using var response = await FailureResponse(failure);
            Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
            Assert.Equal(TimeSpan.FromSeconds(1), response.Headers.RetryAfter?.Delta);
        }
        using var capacity = await FailureResponse(new LedgerCapacity()); Assert.Null(capacity.Headers.RetryAfter);
    }


    [Fact] public async Task MalformedHttpRequestsUseProblemDetails()
    {
        using var factory = new OrdersFactory(); using var client = factory.CreateClient();
        foreach (var item in new[] { ("{broken", "application/json", HttpStatusCode.BadRequest), ("plain", "text/plain", HttpStatusCode.UnsupportedMediaType) }) {
            using var request = new HttpRequestMessage(HttpMethod.Post, "/orders") { Content = new StringContent(item.Item1, System.Text.Encoding.UTF8, item.Item2) };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", OrdersFactory.Token("orders_writer")); request.Headers.Add("Idempotency-Key", "bad-http-key");
            using var response = await client.SendAsync(request); Assert.Equal(item.Item3, response.StatusCode);
            Assert.Equal("application/problem+json", response.Content.Headers.ContentType!.MediaType);
            Assert.Equal("invalid_request", (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
        }
    }


    [Fact] public async Task DuplicateJsonMembersAreRejected()
    {
        using var factory = new OrdersFactory(); using var client = factory.CreateClient();
        foreach (var body in new[] { "{\"sku\":\"SKU-1\",\"quantity\":1,\"quantity\":2}", "{\"sku\":\"SKU-1\",\"SKU\":\"SKU-2\",\"quantity\":1}" }) {
            using var request = new HttpRequestMessage(HttpMethod.Post, "/orders") { Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json") };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", OrdersFactory.Token("orders_writer")); request.Headers.Add("Idempotency-Key", "duplicate-json");
            Assert.Equal(HttpStatusCode.BadRequest, (await client.SendAsync(request)).StatusCode);
        }
    }


    [Fact] public async Task ClientSuppliedOwnerAndStatusAreRejected()
    {
        using var factory = new OrdersFactory(); using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/orders") { Content = JsonContent.Create(new { sku = "SKU-1", quantity = 1, owner = "victim", status = "fulfilled" }) };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", OrdersFactory.Token("orders_writer")); request.Headers.Add("Idempotency-Key", "unknown-field");
        Assert.Equal(HttpStatusCode.BadRequest, (await client.SendAsync(request)).StatusCode);
    }


    [Fact] public async Task KestrelRejectsOversizedBodies()
    {
        using var factory = new OrdersFactory(); factory.UseKestrel(0); using var client = factory.CreateClient();
        foreach (var chunked in new[] { false, true }) {
            using var request = new HttpRequestMessage(HttpMethod.Post, "/orders") { Content = JsonContent.Create(new CreateOrder(new string('A', 6000), 1)) };
            if (chunked) request.Headers.TransferEncodingChunked = true;
            else await request.Content.LoadIntoBufferAsync();
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", OrdersFactory.Token("orders_writer")); request.Headers.Add("Idempotency-Key", "oversized-body");
            Assert.Equal(HttpStatusCode.RequestEntityTooLarge, (await client.SendAsync(request)).StatusCode);
        }
    }


    [Fact] public async Task ReplayMetadataDistinguishesAcceptance()
    {
        using var factory = new OrdersFactory(); using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", OrdersFactory.Token("orders_writer"));
        client.DefaultRequestHeaders.Add("Idempotency-Key", "replay-metadata");
        using var accepted = await client.PostAsJsonAsync("/orders", new CreateOrder("SKU-1", 1));
        using var replay = await client.PostAsJsonAsync("/orders", new CreateOrder("SKU-1", 1));
        Assert.Equal("false", accepted.Headers.GetValues("Idempotency-Replayed").Single());
        Assert.Equal("true", replay.Headers.GetValues("Idempotency-Replayed").Single());
        Assert.True(accepted.Headers.CacheControl!.NoStore); Assert.True(replay.Headers.CacheControl!.NoStore);
    }


    [Fact] public async Task WriteRateLimitIsPartitionedBySubject()
    {
        using var factory = new OrdersFactory();
        using var configured = factory.WithWebHostBuilder(builder => builder.UseSetting("Limits:WritesPerMinute", "2"));
        using var client = configured.CreateClient(); var alice = OrdersFactory.Token("orders_writer");
        Assert.Equal(HttpStatusCode.Accepted, (await SendOrder(client, alice)).StatusCode);
        Assert.Equal(HttpStatusCode.Accepted, (await SendOrder(client, alice)).StatusCode);
        using var limited = await SendOrder(client, alice);
        Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode); Assert.Equal(TimeSpan.FromSeconds(60), limited.Headers.RetryAfter?.Delta);
        Assert.Equal("rate_limited", (await limited.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
        Assert.Equal(HttpStatusCode.Accepted, (await SendOrder(client, OrdersFactory.Token("orders_writer", "bob"))).StatusCode);
    }

// TESTS
}

public sealed class StartedResponse : IHttpResponseFeature
{
    public int StatusCode { get; set; } = 200;
    public string? ReasonPhrase { get; set; }
    public IHeaderDictionary Headers { get; set; } = new HeaderDictionary();
    public Stream Body { get; set; } = new MemoryStream();
    public bool HasStarted => true;
    public void OnStarting(Func<object, Task> callback, object state) { }
    public void OnCompleted(Func<object, Task> callback, object state) { }
}
public sealed class ObservedLifetime : IHttpRequestLifetimeFeature
{
    public CancellationToken RequestAborted { get; set; }
    public bool Aborted { get; private set; }
    public void Abort() => Aborted = true;
}
