using System.Net;
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

// TESTS
}
