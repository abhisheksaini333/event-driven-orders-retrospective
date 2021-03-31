using System.Net;
using Microsoft.Extensions.Configuration;
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


    [Fact] public async Task ConfiguredDaprNamesControlWirePaths()
    {
        var configuration = new Microsoft.Extensions.Configuration.ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> {
            ["Dapr:StateStore"] = "alternate-state", ["Dapr:StateKey"] = "alternate-ledger", ["Dapr:PubSub"] = "alternate-bus", ["Dapr:Topic"] = "orders.v2"
        }).Build();
        var paths = new List<string>();
        var clients = new StubClients(new StubHandler(request => { paths.Add(request.RequestUri!.AbsolutePath); return new(HttpStatusCode.NoContent); }));
        var constructor = typeof(DaprLedgerStore).GetConstructors().SingleOrDefault(c => c.GetParameters().Length == 2);
        Assert.NotNull(constructor);
        var store = (DaprLedgerStore)constructor.Invoke(new object[] { clients, configuration });
        await store.Read(default);
        var publisherConstructor = typeof(DaprPublisher).GetConstructors().Single(c => c.GetParameters().Length == 2);
        await ((DaprPublisher)publisherConstructor.Invoke(new object[] { clients, configuration })).Publish(new("event", "order"), default);
        Assert.Equal(new[] { "/v1.0/state/alternate-state/alternate-ledger", "/v1.0/publish/alternate-bus/orders.v2" }, paths);
        configuration["Dapr:StateStore"] = "unsafe/path";
        var error = Assert.Throws<System.Reflection.TargetInvocationException>(() => constructor.Invoke(new object[] { clients, configuration }));
        Assert.IsType<ArgumentException>(error.InnerException);
    }


    [Fact] public async Task TransientStateReadsRecoverWithinBudget()
    {
        var calls = 0;
        var store = new DaprLedgerStore(new StubClients(new StubHandler(_ => {
            calls++;
            if (calls < 3) return new(calls == 1 ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.BadGateway);
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(new Ledger()) };
            response.Headers.TryAddWithoutValidation("ETag", "1"); return response;
        })));
        Assert.Empty((await store.Read(default)).Value.Orders); Assert.Equal(3, calls);
        calls = 0;
        var unavailable = new DaprLedgerStore(new StubClients(new StubHandler(_ => { calls++; return new(HttpStatusCode.ServiceUnavailable); })));
        await Assert.ThrowsAsync<HttpRequestException>(() => unavailable.Read(default)); Assert.Equal(3, calls);
    }


    [Fact] public async Task RetryAfterGuidesSafeReadBackoff()
    {
        var calls = 0; var elapsed = System.Diagnostics.Stopwatch.StartNew();
        var store = new DaprLedgerStore(new StubClients(new StubHandler(_ => {
            if (++calls == 1) {
                var limited = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
                limited.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(1)); return limited;
            }
            return new(HttpStatusCode.NoContent);
        })));
        await store.Read(default);
        Assert.True(elapsed.Elapsed >= TimeSpan.FromMilliseconds(850)); Assert.Equal(2, calls);
    }


    [Fact] public async Task AmbiguousMutationsAreNotRetried()
    {
        var calls = 0;
        var store = new DaprLedgerStore(new StubClients(new StubHandler(_ => { calls++; return new(HttpStatusCode.ServiceUnavailable); })));
        await Assert.ThrowsAsync<HttpRequestException>(() => store.CompareExchange(new Ledger(), "7", default));
        Assert.Equal(1, calls);
    }

// TESTS
}
