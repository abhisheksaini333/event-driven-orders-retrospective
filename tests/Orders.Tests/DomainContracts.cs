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

// TESTS
}
