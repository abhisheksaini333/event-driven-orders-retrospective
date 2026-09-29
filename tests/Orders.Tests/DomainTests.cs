using System.Text.Json;
using Orders;
using Xunit;

namespace Orders.Tests;

public sealed class MemoryStore : ILedgerStore
{
    private readonly object gate = new();
    private string value = "{}";
    private int version;
    public Task<Snapshot> Read(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        lock (gate) return Task.FromResult(new Snapshot(JsonSerializer.Deserialize<Ledger>(value)!, version.ToString()));
    }
    public Task<bool> CompareExchange(Ledger ledger, string etag, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        lock (gate)
        {
            if (etag != version.ToString()) return Task.FromResult(false);
            value = JsonSerializer.Serialize(ledger); version++; return Task.FromResult(true);
        }
    }
}
public sealed class RecordingPublisher : IEventPublisher
{
    public bool Fail { get; set; }
    public List<OrderEvent> Messages { get; } = new();
    public Task Publish(OrderEvent message, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (Fail) throw new HttpRequestException("simulated broker failure");
        Messages.Add(message); return Task.CompletedTask;
    }
}
public sealed class DelegatePublisher(Func<OrderEvent, CancellationToken, Task> publish) : IEventPublisher
{
    public Task Publish(OrderEvent message, CancellationToken ct) => publish(message, ct);
}
public class DomainTests
{
    [Fact] public async Task Replayed_key_survives_new_engine_instance()
    {
        var store = new MemoryStore(); var first = await new OrdersEngine(store).Submit("alice", "request-123", new("SKU-1", 2));
        var replay = await new OrdersEngine(store).Submit("alice", "request-123", new("SKU-1", 2));
        Assert.True(first.Created); Assert.False(replay.Created); Assert.Equal(first.Order.Id, replay.Order.Id);
        Assert.Single((await store.Read(default)).Value.Outbox);
    }
    [Fact] public async Task Reusing_key_with_different_content_conflicts()
    {
        var engine = new OrdersEngine(new MemoryStore());
        await engine.Submit("alice", "request-123", new("SKU-1", 2));
        await Assert.ThrowsAsync<IdempotencyConflict>(() => engine.Submit("alice", "request-123", new("SKU-1", 3)));
    }
    [Fact] public async Task Concurrent_replay_creates_one_order()
    {
        var store = new MemoryStore(); var engine = new OrdersEngine(store);
        var results = await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => Task.Run(() => engine.Submit("alice", "request-123", new("SKU-1", 2)))));
        Assert.Single(results, r => r.Created); Assert.Single(results.Select(r => r.Order.Id).Distinct());
    }
    [Fact] public async Task Duplicate_delivery_after_worker_restart_has_one_receipt()
    {
        var store = new MemoryStore(); var result = await new OrdersEngine(store).Submit("alice", "request-123", new("SKU-1", 2));
        var message = new OrderEvent(result.Order.EventId, result.Order.Id);
        Assert.Equal("SUCCESS", await new OrdersEngine(store).Process(message));
        Assert.Equal("SUCCESS", await new OrdersEngine(store).Process(message));
        var ledger = (await store.Read(default)).Value;
        Assert.Single(ledger.Receipts); Assert.Equal("fulfilled", ledger.Orders[result.Order.Id].Status);
    }
    [Fact] public async Task Publication_failure_keeps_outbox_for_restart()
    {
        var store = new MemoryStore(); var engine = new OrdersEngine(store);
        await engine.Submit("alice", "request-123", new("SKU-1", 2)); var publisher = new RecordingPublisher { Fail = true };
        await Assert.ThrowsAsync<HttpRequestException>(() => engine.Flush(publisher));
        Assert.Single((await store.Read(default)).Value.Outbox);
        publisher.Fail = false; Assert.Equal(1, await new OrdersEngine(store).Flush(publisher));
        Assert.Empty((await store.Read(default)).Value.Outbox); Assert.Single(publisher.Messages);
    }
    [Fact] public async Task Canceled_submission_does_not_write()
    {
        var store = new MemoryStore(); using var cts = new CancellationTokenSource(); cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new OrdersEngine(store).Submit("alice", "request-123", new("SKU-1", 2), cts.Token));
        Assert.Empty((await store.Read(default)).Value.Orders);
    }
    [Fact] public async Task Lost_publish_ack_replays_without_a_second_fulfillment()
    {
        var store = new MemoryStore(); var engine = new OrdersEngine(store);
        var accepted = await engine.Submit("alice", "request-123", new("SKU-1", 2));
        var lostAck = new DelegatePublisher(async (message, ct) =>
        {
            await new OrdersEngine(store).Process(message, ct);
            throw new HttpRequestException("broker accepted event but response was lost");
        });
        await Assert.ThrowsAsync<HttpRequestException>(() => engine.Flush(lostAck));
        var beforeRestart = (await store.Read(default)).Value;
        Assert.Single(beforeRestart.Outbox); Assert.Single(beforeRestart.Receipts);
        var recovered = new OrdersEngine(store);
        await recovered.Flush(new DelegatePublisher(async (message, ct) => { await recovered.Process(message, ct); }));
        var after = (await store.Read(default)).Value;
        Assert.Empty(after.Outbox); Assert.Single(after.Receipts);
        Assert.Equal("fulfilled", after.Orders[accepted.Order.Id].Status);
    }
    [Fact] public async Task Canceled_worker_delivery_leaves_order_pending()
    {
        var store = new MemoryStore(); var engine = new OrdersEngine(store);
        var accepted = await engine.Submit("alice", "request-123", new("SKU-1", 2));
        using var cts = new CancellationTokenSource(); cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => engine.Process(new(accepted.Order.EventId, accepted.Order.Id), cts.Token));
        var ledger = (await store.Read(default)).Value;
        Assert.Empty(ledger.Receipts); Assert.Equal("accepted", ledger.Orders[accepted.Order.Id].Status);
    }
    [Theory] [InlineData("", 1)] [InlineData("SKU-1", 0)] [InlineData("SKU-1", 1001)] [InlineData("<script>", 1)]
    public async Task Invalid_input_is_rejected(string sku, int quantity) =>
        await Assert.ThrowsAsync<InvalidOrder>(() => new OrdersEngine(new MemoryStore()).Submit("alice", "request-123", new(sku, quantity)));
    [Fact] public async Task Unauthorized_owner_cannot_read_order()
    {
        var engine = new OrdersEngine(new MemoryStore()); var order = await engine.Submit("alice", "request-123", new("SKU-1", 2));
        Assert.Null(await engine.Get("bob", order.Order.Id));
    }
    [Fact] public async Task Invalid_or_unrecognized_event_is_dropped()
    {
        var engine = new OrdersEngine(new MemoryStore());
        Assert.Equal("DROP", await engine.Process(new("bad", "missing", 2)));
        Assert.Equal("DROP", await engine.Process(new(Guid.NewGuid().ToString("N"), Guid.NewGuid().ToString("N"))));
    }
}
