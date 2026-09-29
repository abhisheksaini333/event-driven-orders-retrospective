using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;
using Orders;
using Xunit;

namespace Orders.Tests;
public sealed class OrdersFactory : WebApplicationFactory<Program>
{
    public static readonly SymmetricSecurityKey Key = new(Encoding.UTF8.GetBytes("test-fixture-signing-key-not-a-live-secret-0123456789"));
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("Role", "api"); builder.UseSetting("DispatcherEnabled", "false");
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<ILedgerStore>(); services.AddSingleton<ILedgerStore, MemoryStore>();
            services.PostConfigure<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme, options =>
            {
                options.Authority = null; options.MetadataAddress = "";
                options.Configuration = new OpenIdConnectConfiguration { Issuer = "https://issuer.test" };
                options.Configuration.SigningKeys.Add(Key);
                options.TokenValidationParameters.ValidIssuer = "https://issuer.test";
                options.TokenValidationParameters.ValidAudience = "orders-api";
                options.TokenValidationParameters.IssuerSigningKey = Key;
            });
        });
    }
    public static string Token(string role, string owner = "alice", bool expired = false, string audience = "orders-api") => new JwtSecurityTokenHandler().WriteToken(new JwtSecurityToken(
        issuer: "https://issuer.test", audience: audience, claims: new[] { new Claim("sub", owner), new Claim("roles", role) },
        notBefore: DateTime.UtcNow.AddHours(-1), expires: expired ? DateTime.UtcNow.AddMinutes(-10) : DateTime.UtcNow.AddMinutes(5),
        signingCredentials: new SigningCredentials(Key, SecurityAlgorithms.HmacSha256)));
}
public class HttpTests : IClassFixture<OrdersFactory>
{
    private readonly HttpClient client;
    public HttpTests(OrdersFactory factory) { client = factory.CreateClient(); }
    private async Task<HttpResponseMessage> Submit(string? token, CreateOrder? body = null, string? key = "request-123")
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/orders") { Content = JsonContent.Create(body ?? new("SKU-1", 2)) };
        if (token != null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (key != null) request.Headers.Add("Idempotency-Key", key);
        return await client.SendAsync(request);
    }
    [Fact] public async Task Missing_token_is_401() => Assert.Equal(HttpStatusCode.Unauthorized, (await Submit(null)).StatusCode);
    [Fact] public async Task Malformed_token_is_401() => Assert.Equal(HttpStatusCode.Unauthorized, (await Submit("bad-token")).StatusCode);
    [Fact] public async Task Expired_token_is_401() => Assert.Equal(HttpStatusCode.Unauthorized, (await Submit(OrdersFactory.Token("orders_writer", expired: true))).StatusCode);
    [Fact] public async Task Wrong_audience_is_401() => Assert.Equal(HttpStatusCode.Unauthorized, (await Submit(OrdersFactory.Token("orders_writer", audience: "another-api"))).StatusCode);
    [Fact] public async Task Tampered_signature_is_401()
    {
        var parts = OrdersFactory.Token("orders_writer").Split('.');
        parts[2] = (parts[2][0] == 'A' ? "B" : "A") + parts[2][1..];
        Assert.Equal(HttpStatusCode.Unauthorized, (await Submit(string.Join('.', parts))).StatusCode);
    }
    [Fact] public async Task Authenticated_reader_is_403() => Assert.Equal(HttpStatusCode.Forbidden, (await Submit(OrdersFactory.Token("orders_reader"))).StatusCode);
    [Fact] public async Task Writer_gets_202_and_replay_gets_200()
    {
        var token = OrdersFactory.Token("orders_writer");
        Assert.Equal(HttpStatusCode.Accepted, (await Submit(token)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Submit(token)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await Submit(token, new("SKU-1", 3))).StatusCode);
    }
    [Fact] public async Task Missing_idempotency_key_is_400() => Assert.Equal(HttpStatusCode.BadRequest, (await Submit(OrdersFactory.Token("orders_writer"), key: null)).StatusCode);
    [Fact] public async Task Invalid_payload_is_400() => Assert.Equal(HttpStatusCode.BadRequest, (await Submit(OrdersFactory.Token("orders_writer"), new("SKU-1", -1))).StatusCode);
}
