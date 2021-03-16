using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Orders;
using Xunit;
namespace Orders.Tests;
public class AdapterContracts
{
    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web);
    private static DaprLedgerStore Responding(string json, HttpStatusCode status = HttpStatusCode.OK, string etag = "1")
    {
        var response = new HttpResponseMessage(status) { Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json") };
        response.Headers.TryAddWithoutValidation("ETag", etag);
        return new(new StubClients(new StubHandler(_ => response)));
    }

    [Fact] public async Task UnsupportedLedgerSchemaIsRejected()
    {
        var document = JsonSerializer.SerializeToNode(new Ledger(), WebJson)!.AsObject();
        document["schemaVersion"] = 99;
        await Assert.ThrowsAsync<HttpRequestException>(() => Responding(document.ToJsonString()).Read(default));
        document.Remove("schemaVersion");
        Assert.Empty((await Responding(document.ToJsonString()).Read(default)).Value.Orders);
    }


    [Fact] public async Task InvalidLedgerShapeIsRejected()
    {
        foreach (var invalid in new[] {
            "{}", "null", "[]",
            "{\"orders\":null,\"requests\":{},\"outbox\":{},\"receipts\":[]}",
            "{\"orders\":{},\"requests\":[],\"outbox\":{},\"receipts\":[]}" })
            await Assert.ThrowsAsync<HttpRequestException>(() => Responding(invalid).Read(default));
    }


    [Fact] public async Task InvalidLedgerReferencesAreRejected()
    {
        var store = new MemoryStore(); var engine = new OrdersEngine(store);
        var order = (await engine.Submit("alice", "integrity-key", new("SKU-1", 1))).Order;
        var valid = (await store.Read(default)).Value;
        async Task Reject(Action<JsonObject> mutate) {
            var node = JsonSerializer.SerializeToNode(valid, WebJson)!.AsObject(); mutate(node);
            await Assert.ThrowsAsync<HttpRequestException>(() => Responding(node.ToJsonString()).Read(default));
        }
        await Reject(node => node["orders"]![order.Id]!["status"] = "impossible");
        await Reject(node => node["orders"]![order.Id]!["id"] = Guid.NewGuid().ToString("N"));
        await Reject(node => node["requests"]!.AsObject().First().Value!["orderId"] = Guid.NewGuid().ToString("N"));
        await Reject(node => node["outbox"]![order.EventId]!["eventId"] = Guid.NewGuid().ToString("N"));
        await Reject(node => node["receipts"]!.AsArray().Add(order.EventId));
        Assert.Single((await Responding(JsonSerializer.Serialize(valid, WebJson)).Read(default)).Value.Orders);
    }


    [Fact] public async Task MalformedJsonIsDependencyFailure()
    {
        foreach (var payload in new[] { "{not-json", "", "123", "true", "\"a string\"" })
            await Assert.ThrowsAsync<HttpRequestException>(() => Responding(payload).Read(default));
    }


    [Fact] public async Task OversizedStateIsRejectedWithoutMutation()
    {
        var padding = new string(' ', 16 * 1024 * 1024);
        await Assert.ThrowsAsync<HttpRequestException>(() => Responding(JsonSerializer.Serialize(new Ledger(), WebJson) + padding).Read(default));
        var calls = 0; var store = new DaprLedgerStore(new StubClients(new StubHandler(_ => { calls++; return new(HttpStatusCode.NoContent); })));
        var ledger = new Ledger(); ledger.Orders["large"] = new("large", "alice", padding, 1, "accepted", "event");
        await Assert.ThrowsAsync<HttpRequestException>(() => store.CompareExchange(ledger, "1", default));
        Assert.Equal(0, calls);
    }


    [Fact] public async Task UnexpectedSuccessfulReadStatusFailsClosed()
    {
        foreach (var status in new[] { HttpStatusCode.Created, HttpStatusCode.Accepted, HttpStatusCode.PartialContent })
            await Assert.ThrowsAsync<HttpRequestException>(() => Responding(JsonSerializer.Serialize(new Ledger(), WebJson), status).Read(default));
    }


    [Fact] public async Task MalformedEtagsAreDependencyFailures()
    {
        var payload = JsonSerializer.Serialize(new Ledger(), WebJson);
        foreach (var etag in new[] { "", " ", "0", "-1", "abc", "1,2", "\"1", "\"\"1\"\"", "9223372036854775808" })
            await Assert.ThrowsAsync<HttpRequestException>(() => Responding(payload, etag: etag).Read(default));
        Assert.Equal("42", (await Responding(payload, etag: "\"42\"").Read(default)).ETag);
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(new Ledger()) };
        response.Headers.TryAddWithoutValidation("ETag", new[] { "1", "2" });
        await Assert.ThrowsAsync<HttpRequestException>(() => new DaprLedgerStore(new StubClients(new StubHandler(_ => response))).Read(default));
    }

// TESTS
}
