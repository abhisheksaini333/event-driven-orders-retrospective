using System.Net;
using System.Text.Json;
using Orders;
using Xunit;
namespace Orders.Tests;
public partial class MaintenanceContracts
{
    private static Ledger ValidLedger()
    {
        var ledger = new Ledger(); var id = new string('a', 32); var eventId = new string('b', 32);
        ledger.Orders[id] = new(id, "alice", "SKU-1", 1, "accepted", eventId, DateTimeOffset.Parse("2021-01-01T00:00:00Z"));
        return ledger;
    }
    private static DaprLedgerStore Responding(string json)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json) };
        response.Headers.TryAddWithoutValidation("ETag", "1");
        return new(new StubClients(new StubHandler(_ => response)));
    }
    private sealed class NoReads : ILedgerStore
    {
        public Task<Snapshot> Read(CancellationToken ct) => throw new InvalidOperationException("unexpected read");
        public Task<bool> CompareExchange(Ledger value, string etag, CancellationToken ct) => throw new InvalidOperationException("unexpected write");
    }
}

public partial class MaintenanceContracts
{
    [Fact] public void PersistedOrderPayloadMustMeetSubmissionRules()
    {
        foreach (var order in new[] { ValidLedger().Orders.Values.Single() with { Sku = "lowercase" }, ValidLedger().Orders.Values.Single() with { Owner = "a\u0001" } })
        { var ledger = ValidLedger(); ledger.Orders[order.Id] = order; Assert.Throws<HttpRequestException>(() => LedgerIntegrity.Validate(ledger)); }
    }
}
