namespace Orders;

public sealed record CreateOrder(string Sku, int Quantity);
public sealed record Order(string Id, string Owner, string Sku, int Quantity, string Status, string EventId);
public sealed record OrderEvent(string EventId, string OrderId, int Version = 1);
public sealed record RequestRecord(string Fingerprint, string OrderId);
public sealed class Ledger
{
    public Dictionary<string, Order> Orders { get; set; } = new();
    public Dictionary<string, RequestRecord> Requests { get; set; } = new();
    public Dictionary<string, OrderEvent> Outbox { get; set; } = new();
    public HashSet<string> Receipts { get; set; } = new();
}
public sealed record Snapshot(Ledger Value, string ETag);
public interface ILedgerStore
{
    Task<Snapshot> Read(CancellationToken cancellationToken);
    Task<bool> CompareExchange(Ledger ledger, string etag, CancellationToken cancellationToken);
}
public interface IEventPublisher { Task Publish(OrderEvent message, CancellationToken cancellationToken); }
public sealed class IdempotencyConflict : Exception;
public sealed class LedgerBusy : Exception;
public sealed class InvalidOrder : Exception;
public sealed class OrdersEngine(ILedgerStore store)
{
    public async Task<(Order Order, bool Created)> Submit(string owner, string key, CreateOrder input, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(owner) || key is null || !System.Text.RegularExpressions.Regex.IsMatch(key, "^[A-Za-z0-9_-]{8,64}$") ||
            input.Sku is null || !System.Text.RegularExpressions.Regex.IsMatch(input.Sku, "^[A-Z0-9-]{1,32}$") || input.Quantity is < 1 or > 1000)
            throw new InvalidOrder();
        var requestKey = Hash(owner + "\n" + key);
        var fingerprint = Hash(input.Sku + "\n" + input.Quantity.ToString(System.Globalization.CultureInfo.InvariantCulture));
        return await Mutate(ledger =>
        {
            if (ledger.Requests.TryGetValue(requestKey, out var previous))
            {
                if (previous.Fingerprint != fingerprint) throw new IdempotencyConflict();
                return ((ledger.Orders[previous.OrderId], false), false);
            }
            var order = new Order(Guid.NewGuid().ToString("N"), owner, input.Sku, input.Quantity, "accepted", Guid.NewGuid().ToString("N"));
            ledger.Orders.Add(order.Id, order);
            ledger.Requests.Add(requestKey, new(fingerprint, order.Id));
            ledger.Outbox.Add(order.EventId, new(order.EventId, order.Id));
            return ((order, true), true);
        }, ct);
    }

    public async Task<Order?> Get(string owner, string id, CancellationToken ct = default)
    {
        var state = await store.Read(ct);
        return state.Value.Orders.TryGetValue(id, out var order) && order.Owner == owner ? order : null;
    }

    public Task<string> Process(OrderEvent message, CancellationToken ct = default) => Mutate(ledger =>
    {
        if (message.Version != 1 || !Guid.TryParseExact(message.EventId, "N", out _) || !Guid.TryParseExact(message.OrderId, "N", out _) ||
            !ledger.Orders.TryGetValue(message.OrderId, out var order) || order.EventId != message.EventId)
            return ("DROP", false);
        if (ledger.Receipts.Contains(message.EventId)) return ("SUCCESS", false);
        ledger.Orders[order.Id] = order with { Status = "fulfilled" };
        ledger.Receipts.Add(message.EventId);
        return ("SUCCESS", true);
    }, ct);

    public async Task<int> Flush(IEventPublisher publisher, CancellationToken ct = default)
    {
        var pending = (await store.Read(ct)).Value.Outbox.Values.Take(25).ToArray();
        foreach (var message in pending)
        {
            // Publish before removal: a crash in between deliberately permits duplicate delivery.
            await publisher.Publish(message, ct);
            await Mutate(ledger => (true, ledger.Outbox.Remove(message.EventId)), ct);
        }
        return pending.Length;
    }

    private async Task<T> Mutate<T>(Func<Ledger, (T Result, bool Changed)> action, CancellationToken ct)
    {
        for (var attempt = 0; attempt < 32; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            var snapshot = await store.Read(ct);
            var outcome = action(snapshot.Value);
            if (!outcome.Changed || await store.CompareExchange(snapshot.Value, snapshot.ETag, ct)) return outcome.Result;
            await Task.Delay(Random.Shared.Next(2, Math.Min(100, 5 + attempt * 5)), ct);
        }
        throw new LedgerBusy();
    }

    private static string Hash(string value) => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(value)));
}
