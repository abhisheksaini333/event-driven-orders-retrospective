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
        await Assert.ThrowsAnyAsync<HttpRequestException>(() => Responding(document.ToJsonString()).Read(default));
        document.Remove("schemaVersion");
        Assert.Empty((await Responding(document.ToJsonString()).Read(default)).Value.Orders);
    }


    [Fact] public async Task InvalidLedgerShapeIsRejected()
    {
        foreach (var invalid in new[] {
            "{}", "null", "[]",
            "{\"orders\":null,\"requests\":{},\"outbox\":{},\"receipts\":[]}",
            "{\"orders\":{},\"requests\":[],\"outbox\":{},\"receipts\":[]}" })
            await Assert.ThrowsAnyAsync<HttpRequestException>(() => Responding(invalid).Read(default));
    }


    [Fact] public async Task InvalidLedgerReferencesAreRejected()
    {
        var store = new MemoryStore(); var engine = new OrdersEngine(store);
        var order = (await engine.Submit("alice", "integrity-key", new("SKU-1", 1))).Order;
        var valid = (await store.Read(default)).Value;
        async Task Reject(Action<JsonObject> mutate) {
            var node = JsonSerializer.SerializeToNode(valid, WebJson)!.AsObject(); mutate(node);
            await Assert.ThrowsAnyAsync<HttpRequestException>(() => Responding(node.ToJsonString()).Read(default));
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
            await Assert.ThrowsAnyAsync<HttpRequestException>(() => Responding(payload).Read(default));
    }


    [Fact] public async Task OversizedStateIsRejectedWithoutMutation()
    {
        var padding = new string(' ', 16 * 1024 * 1024);
        await Assert.ThrowsAnyAsync<HttpRequestException>(() => Responding(JsonSerializer.Serialize(new Ledger(), WebJson) + padding).Read(default));
        var calls = 0; var store = new DaprLedgerStore(new StubClients(new StubHandler(_ => { calls++; return new(HttpStatusCode.NoContent); })));
        var ledger = new Ledger(); ledger.Orders["large"] = new("large", "alice", padding, 1, "accepted", "event");
        await Assert.ThrowsAnyAsync<HttpRequestException>(() => store.CompareExchange(ledger, "1", default));
        Assert.Equal(0, calls);
    }


    [Fact] public async Task UnexpectedSuccessfulReadStatusFailsClosed()
    {
        foreach (var status in new[] { HttpStatusCode.Created, HttpStatusCode.Accepted, HttpStatusCode.PartialContent })
            await Assert.ThrowsAnyAsync<HttpRequestException>(() => Responding(JsonSerializer.Serialize(new Ledger(), WebJson), status).Read(default));
    }


    [Fact] public async Task MalformedEtagsAreDependencyFailures()
    {
        var payload = JsonSerializer.Serialize(new Ledger(), WebJson);
        foreach (var etag in new[] { "", " ", "0", "-1", "abc", "1,2", "\"1", "\"\"1\"\"", "9223372036854775808" })
            await Assert.ThrowsAnyAsync<HttpRequestException>(() => Responding(payload, etag: etag).Read(default));
        Assert.Equal("42", (await Responding(payload, etag: "\"42\"").Read(default)).ETag);
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(new Ledger()) };
        response.Headers.TryAddWithoutValidation("ETag", new[] { "1", "2" });
        await Assert.ThrowsAnyAsync<HttpRequestException>(() => new DaprLedgerStore(new StubClients(new StubHandler(_ => response))).Read(default));
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
        await Assert.ThrowsAnyAsync<HttpRequestException>(() => unavailable.Read(default)); Assert.Equal(3, calls);
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
        await Assert.ThrowsAnyAsync<HttpRequestException>(() => store.CompareExchange(new Ledger(), "7", default));
        Assert.Equal(1, calls);
    }


    [Fact] public async Task ConditionalStateWireContract()
    {
        var requests = new List<(string Method, string Uri, string Body)>();
        var store = new DaprLedgerStore(new StubClients(new StubHandler(request => {
            requests.Add((request.Method.Method, request.RequestUri!.PathAndQuery, request.Content?.ReadAsStringAsync().GetAwaiter().GetResult() ?? ""));
            return new(HttpStatusCode.NoContent);
        })));
        await store.Read(default); Assert.True(await store.CompareExchange(new Ledger(), "42", default));
        Assert.Equal(("GET", "/v1.0/state/orders-state/orders-ledger-v1?consistency=strong", ""), requests[0]);
        Assert.Equal("POST", requests[1].Method); Assert.Equal("/v1.0/state/orders-state", requests[1].Uri);
        var write = JsonDocument.Parse(requests[1].Body).RootElement[0];
        Assert.Equal("42", write.GetProperty("etag").GetString()); Assert.Equal("orders-ledger-v1", write.GetProperty("key").GetString());
        Assert.Equal("first-write", write.GetProperty("options").GetProperty("concurrency").GetString());
        Assert.Equal("strong", write.GetProperty("options").GetProperty("consistency").GetString());
        Assert.Equal(JsonValueKind.Object, write.GetProperty("value").ValueKind);
    }


    [Fact] public async Task PublisherWireAndAcknowledgmentContract()
    {
        var message = new OrderEvent(Guid.NewGuid().ToString("N"), Guid.NewGuid().ToString("N"));
        var calls = 0;
        var publisher = new DaprPublisher(new StubClients(new StubHandler(request => {
            calls++; Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("/v1.0/publish/orders-pubsub/orders.accepted", request.RequestUri!.AbsolutePath);
            Assert.Equal("application/json", request.Content!.Headers.ContentType!.MediaType);
            var sent = request.Content.ReadFromJsonAsync<OrderEvent>().GetAwaiter().GetResult(); Assert.Equal(message, sent);
            return new(HttpStatusCode.ServiceUnavailable);
        })));
        await Assert.ThrowsAnyAsync<HttpRequestException>(() => publisher.Publish(message, default)); Assert.Equal(1, calls);
    }


    [Fact] public async Task CanceledAdapterCallsDoNotDispatch()
    {
        using var canceled = new CancellationTokenSource(); canceled.Cancel(); var calls = 0;
        var clients = new StubClients(new StubHandler(_ => { calls++; return new(HttpStatusCode.NoContent); }));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new DaprLedgerStore(clients).Read(canceled.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new DaprLedgerStore(clients).CompareExchange(new Ledger(), "1", canceled.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new DaprPublisher(clients).Publish(new("event", "order"), canceled.Token));
        Assert.Equal(0, calls);
    }


    [Fact] public async Task DependencyFailuresHaveSafeOperationMetadata()
    {
        var publisher = new DaprPublisher(new StubClients(new StubHandler(_ => new(HttpStatusCode.Forbidden) { Content = new StringContent("private-response-detail") })));
        var error = await Assert.ThrowsAnyAsync<HttpRequestException>(() => publisher.Publish(new("event", "order"), default));
        Assert.Equal("DaprOperationException", error.GetType().Name);
        Assert.Equal(HttpStatusCode.Forbidden, error.StatusCode);
        Assert.Equal("publish", error.GetType().GetProperty("Operation")!.GetValue(error));
        Assert.Equal(false, error.GetType().GetProperty("Retryable")!.GetValue(error));
        Assert.DoesNotContain("private-response-detail", error.ToString());
    }


    [Fact] public async Task NullStateReferencesAreDependencyFailures()
    {
        var store = new MemoryStore(); var engine = new OrdersEngine(store);
        var order = (await engine.Submit("alice", "null-state-key", new("SKU-1", 1))).Order;
        var ledger = (await store.Read(default)).Value;
        foreach (var collection in new[] { "requests", "outbox" }) {
            var node = JsonSerializer.SerializeToNode(ledger, WebJson)!.AsObject();
            node[collection]!.AsObject().First().Value!["orderId"] = null;
            await Assert.ThrowsAnyAsync<HttpRequestException>(() => Responding(node.ToJsonString()).Read(default));
        }
    }


    private sealed class FixedClients(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }
    [Fact] public async Task StateBodyStreamingHonorsHttpClientTimeout()
    {
        using var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0); listener.Start();
        using var watchdog = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        var responder = Task.Run(async () => {
            try {
                using var socket = await listener.AcceptTcpClientAsync(watchdog.Token);
                await using var stream = socket.GetStream();
                using var reader = new StreamReader(stream, System.Text.Encoding.ASCII, false, 1024, leaveOpen: true);
                while (await reader.ReadLineAsync(watchdog.Token) is { Length: > 0 }) { }
                var headers = System.Text.Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Length: 100\r\nETag: 1\r\nContent-Type: application/json\r\nConnection: close\r\n\r\n");
                await stream.WriteAsync(headers, watchdog.Token); await stream.FlushAsync(watchdog.Token);
                await Task.Delay(Timeout.InfiniteTimeSpan, watchdog.Token);
            } catch (OperationCanceledException) { }
        });
        using var client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}"), Timeout = TimeSpan.FromMilliseconds(200) };
        var elapsed = System.Diagnostics.Stopwatch.StartNew();
        try {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new DaprLedgerStore(new FixedClients(client)).Read(watchdog.Token));
            Assert.True(elapsed.Elapsed < TimeSpan.FromSeconds(1.5), $"Body read exceeded HTTP deadline: {elapsed.Elapsed}");
        } finally { watchdog.Cancel(); await responder; }
    }


    [Fact] public async Task FallbackReadDelayRespectsConfiguredCap()
    {
        var calls = 0;
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> {
            ["Limits:ReadAttempts"] = "8", ["Limits:MaximumReadDelayMs"] = "0"
        }).Build();
        var store = new DaprLedgerStore(new StubClients(new StubHandler(_ => new HttpResponseMessage(++calls < 8 ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.NoContent))), configuration);
        var elapsed = System.Diagnostics.Stopwatch.StartNew();
        Assert.Equal("0", (await store.Read(default)).ETag);
        Assert.Equal(8, calls);
        Assert.True(elapsed.Elapsed < TimeSpan.FromSeconds(1), $"Zero configured delay still backed off: {elapsed.Elapsed}");
    }

// TESTS
}
