using System.Net;
using System.Net.Http.Json;
using Orders;
using Xunit;

namespace Orders.Tests;
public sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> response) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => Task.FromResult(response(request));
}
public sealed class StubClients(HttpMessageHandler handler) : IHttpClientFactory
{
    public HttpClient CreateClient(string name) => new(handler, false) { BaseAddress = new Uri("http://dapr.test") };
}
public class DaprAdapterTests
{
    [Fact] public async Task Reads_unquoted_Dapr_numeric_ETag()
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(new Ledger()) };
        response.Headers.TryAddWithoutValidation("ETag", "42");
        var snapshot = await new DaprLedgerStore(new StubClients(new StubHandler(_ => response))).Read(default);
        Assert.Equal("42", snapshot.ETag);
    }
    [Fact] public async Task Missing_state_uses_first_write_version_zero()
    {
        var snapshot = await new DaprLedgerStore(new StubClients(new StubHandler(_ => new(HttpStatusCode.NoContent)))).Read(default);
        Assert.Equal("0", snapshot.ETag); Assert.Empty(snapshot.Value.Orders);
    }
    [Fact] public async Task Conflict_is_retryable_but_server_error_is_not_hidden()
    {
        var conflict = new DaprLedgerStore(new StubClients(new StubHandler(_ => new(HttpStatusCode.Conflict))));
        Assert.False(await conflict.CompareExchange(new Ledger(), "2", default));
        var failure = new DaprLedgerStore(new StubClients(new StubHandler(_ => new(HttpStatusCode.InternalServerError))));
        await Assert.ThrowsAsync<HttpRequestException>(() => failure.CompareExchange(new Ledger(), "2", default));
    }
}
