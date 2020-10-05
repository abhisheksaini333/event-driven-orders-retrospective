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

// TESTS
}
