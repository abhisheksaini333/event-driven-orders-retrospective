using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Orders;
using Xunit;
namespace Orders.Tests;
public class DomainContracts
{

    [Fact] public async Task OwnerScopedKeys()
    {
        var store = new MemoryStore(); var engine = new OrdersEngine(store);
        var alice = await engine.Submit("alice", "shared-key", new("SKU-1", 1));
        var bob = await engine.Submit("bob", "shared-key", new("SKU-1", 7));
        Assert.NotEqual(alice.Order.Id, bob.Order.Id);
        Assert.Equal(alice.Order.Id, (await engine.Submit("alice", "shared-key", new("SKU-1", 1))).Order.Id);
        Assert.Equal(bob.Order.Id, (await engine.Submit("bob", "shared-key", new("SKU-1", 7))).Order.Id);
        Assert.Equal(2, (await store.Read(default)).Value.Requests.Count);
    }


    [Fact] public async Task ReadAndReplayDoNotWrite()
    {
        var store = new HookStore(); var engine = new OrdersEngine(store);
        var order = await engine.Submit("alice", "read-only-key", new("SKU-1", 1));
        var writes = store.Writes;
        await engine.Submit("alice", "read-only-key", new("SKU-1", 1));
        await engine.Get("alice", order.Order.Id); await engine.Get("bob", order.Order.Id);
        Assert.Equal(writes, store.Writes);
    }


    [Fact] public async Task ConflictPreservesCompetingOrder()
    {
        var store = new HookStore(); var competingId = "";
        store.BeforeWrite = async (_, _, ct) => {
            if (store.Writes == 1) competingId = (await new OrdersEngine(store.Inner).Submit("bob", "competing-key", new("SKU-2", 2), ct)).Order.Id;
            return null;
        };
        var accepted = await new OrdersEngine(store).Submit("alice", "original-key", new("SKU-1", 1));
        var ledger = (await store.Inner.Read(default)).Value;
        Assert.Equal(2, store.Writes); Assert.Equal(2, ledger.Orders.Count);
        Assert.Contains(competingId, ledger.Orders.Keys); Assert.Contains(accepted.Order.Id, ledger.Orders.Keys);
    }


    [Fact] public async Task ConflictExhaustionIsBounded()
    {
        var store = new HookStore { BeforeWrite = (_, _, _) => Task.FromResult<bool?>(false) };
        await Assert.ThrowsAsync<LedgerBusy>(() => new OrdersEngine(store).Submit("alice", "exhausted-key", new("SKU-1", 1)));
        Assert.Equal(32, store.Writes); Assert.Empty((await store.Inner.Read(default)).Value.Orders);
    }


    [Fact] public async Task CancellationDuringContention()
    {
        using var cancellation = new CancellationTokenSource(); var store = new HookStore();
        store.BeforeWrite = (_, _, _) => { cancellation.Cancel(); return Task.FromResult<bool?>(false); };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new OrdersEngine(store).Submit("alice", "cancel-retry", new("SKU-1", 1), cancellation.Token));
        Assert.Equal(1, store.Writes); Assert.Empty((await store.Inner.Read(default)).Value.Orders);
    }


    [Fact] public async Task CommittedCancellationReplays()
    {
        var store = new HookStore { AfterWrite = _ => throw new OperationCanceledException("acknowledgment lost") };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new OrdersEngine(store).Submit("alice", "committed-key", new("SKU-1", 1)));
        var replay = await new OrdersEngine(store.Inner).Submit("alice", "committed-key", new("SKU-1", 1));
        Assert.False(replay.Created); Assert.Single((await store.Inner.Read(default)).Value.Orders);
        Assert.Single((await store.Inner.Read(default)).Value.Outbox);
    }


    [Fact] public async Task ConcurrentDuplicateDelivery()
    {
        var store = new MemoryStore(); var engine = new OrdersEngine(store);
        var accepted = await engine.Submit("alice", "duplicate-key", new("SKU-1", 1));
        var message = new OrderEvent(accepted.Order.EventId, accepted.Order.Id);
        var outcomes = await Task.WhenAll(Enumerable.Range(0, 24).Select(_ => Task.Run(() => new OrdersEngine(store).Process(message))));
        Assert.All(outcomes, status => Assert.Equal("SUCCESS", status));
        var ledger = (await store.Read(default)).Value;
        Assert.Single(ledger.Receipts); Assert.Equal("fulfilled", ledger.Orders[accepted.Order.Id].Status);
    }


    [Fact] public async Task CrossOrderEventCannotFulfill()
    {
        var store = new MemoryStore(); var engine = new OrdersEngine(store);
        var first = await engine.Submit("alice", "first-event", new("SKU-1", 1));
        var second = await engine.Submit("alice", "second-event", new("SKU-1", 1));
        Assert.Equal("DROP", await engine.Process(new(first.Order.EventId, second.Order.Id)));
        var ledger = (await store.Read(default)).Value;
        Assert.Empty(ledger.Receipts); Assert.All(ledger.Orders.Values, order => Assert.Equal("accepted", order.Status));
    }


    [Fact] public async Task RemovalRacePreservesOtherState()
    {
        var store = new HookStore(); var engine = new OrdersEngine(store);
        var first = await engine.Submit("alice", "first-race-key", new("SKU-1", 1));
        var secondId = ""; var injected = false;
        store.BeforeWrite = async (_, _, ct) => {
            if (!injected) {
                injected = true; var rival = new OrdersEngine(store.Inner);
                secondId = (await rival.Submit("bob", "second-race-key", new("SKU-2", 2), ct)).Order.Id;
                await rival.Process(new(first.Order.EventId, first.Order.Id), ct);
            }
            return null;
        };
        Assert.Equal(1, await engine.Flush(new RecordingPublisher()));
        var ledger = (await store.Inner.Read(default)).Value;
        Assert.Equal(2, ledger.Orders.Count); Assert.Single(ledger.Outbox); Assert.Single(ledger.Receipts);
        Assert.Equal(secondId, ledger.Outbox.Values.Single().OrderId);
        Assert.Equal("fulfilled", ledger.Orders[first.Order.Id].Status);
    }


    [Fact] public async Task PartialBatchFailureRetainsWork()
    {
        var store = new MemoryStore(); var engine = new OrdersEngine(store);
        for (var i = 0; i < 3; i++) await engine.Submit("alice", "batch-key-" + i, new("SKU-1", 1));
        var calls = 0; var successful = "";
        await Assert.ThrowsAsync<HttpRequestException>(() => engine.Flush(new DelegatePublisher((message, _) => {
            if (++calls == 2) throw new HttpRequestException("unavailable");
            successful = message.EventId; return Task.CompletedTask;
        })));
        var pending = (await store.Read(default)).Value.Outbox;
        Assert.Equal(2, pending.Count); Assert.DoesNotContain(successful, pending.Keys);
    }


    [Fact] public async Task CommittedRemovalIsNotRecreated()
    {
        var store = new HookStore(); var engine = new OrdersEngine(store);
        await engine.Submit("alice", "removal-key", new("SKU-1", 1));
        store.AfterWrite = _ => throw new HttpRequestException("state response lost");
        var publisher = new RecordingPublisher();
        await Assert.ThrowsAsync<HttpRequestException>(() => engine.Flush(publisher));
        Assert.Equal(0, await new OrdersEngine(store.Inner).Flush(publisher));
        Assert.Single(publisher.Messages); Assert.Empty((await store.Inner.Read(default)).Value.Outbox);
    }


    [Fact] public async Task BatchLimitPreservesRemainder()
    {
        var store = new MemoryStore(); var engine = new OrdersEngine(store);
        for (var i = 0; i < 30; i++) await engine.Submit("alice", "limited-key-" + i, new("SKU-1", 1));
        var publisher = new RecordingPublisher(); Assert.Equal(25, await engine.Flush(publisher));
        Assert.Equal(25, publisher.Messages.Count); Assert.Equal(5, (await store.Read(default)).Value.Outbox.Count);
        Assert.Equal(5, await engine.Flush(publisher)); Assert.Empty((await store.Read(default)).Value.Outbox);
    }


    [Fact] public async Task CompetingDispatchersPreserveReceipt()
    {
        var store = new MemoryStore(); var engine = new OrdersEngine(store);
        var accepted = await engine.Submit("alice", "dispatch-race", new("SKU-1", 1));
        var arrivals = 0; var barrier = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var publisher = new DelegatePublisher(async (message, ct) => {
            if (Interlocked.Increment(ref arrivals) == 2) barrier.SetResult();
            await barrier.Task.WaitAsync(TimeSpan.FromSeconds(5), ct);
            await new OrdersEngine(store).Process(message, ct);
        });
        await Task.WhenAll(engine.Flush(publisher), new OrdersEngine(store).Flush(publisher));
        var ledger = (await store.Read(default)).Value;
        Assert.Equal(2, arrivals); Assert.Single(ledger.Receipts); Assert.Empty(ledger.Outbox);
        Assert.Equal("fulfilled", ledger.Orders[accepted.Order.Id].Status);
    }

// TESTS
}
