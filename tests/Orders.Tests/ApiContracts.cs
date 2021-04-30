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

// TESTS
}
