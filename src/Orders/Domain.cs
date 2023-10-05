namespace Orders;

public sealed record CreateOrder(string Sku, int Quantity);
public sealed record Order(string Id, string Owner, string Sku, int Quantity, string Status, string EventId, DateTimeOffset? AcceptedAt = null, DateTimeOffset? FulfilledAt = null);
public sealed record OrderEvent(string EventId, string OrderId, int Version = 1);
public sealed record RequestRecord(string Fingerprint, string OrderId, int Version = 1);
public sealed class Ledger
{
    public int SchemaVersion { get; set; } = 1;
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
public sealed class LedgerCapacity : Exception;
public sealed class InvalidOrder(IReadOnlyDictionary<string, string[]>? errors = null) : Exception("Order validation failed")
{
    public IReadOnlyDictionary<string, string[]> Errors { get; } = errors ?? new Dictionary<string, string[]>();
}
public sealed class OrdersEngine(ILedgerStore store, IConfiguration? configuration = null, ILogger<OrdersEngine>? logger = null)
{
    private readonly RuntimeLimits limits = RuntimeLimits.Load(configuration);
    public async Task<(Order Order, bool Created)> Submit(string owner, string key, CreateOrder input, CancellationToken ct = default)
    {
        using var activity = OrdersTelemetry.Activities.StartActivity("orders.submit");
        var errors = new Dictionary<string, string[]>();
        if (input is null) throw new InvalidOrder(new Dictionary<string, string[]> { ["request"] = ["A request body is required."] });
        if (string.IsNullOrWhiteSpace(owner) || owner.Length > 256 || owner.Any(char.IsControl)) errors["owner"] = ["Owner must contain 1 to 256 characters without controls."];
        if (key is null || !System.Text.RegularExpressions.Regex.IsMatch(key, @"\A[A-Za-z0-9_-]{8,64}\z")) errors["idempotencyKey"] = ["Use 8 to 64 ASCII letters, digits, underscores or hyphens."];
        if (input.Sku is null || !System.Text.RegularExpressions.Regex.IsMatch(input.Sku, @"\A[A-Z0-9-]{1,32}\z")) errors["sku"] = ["Use 1 to 32 uppercase ASCII letters, digits or hyphens."];
        if (input.Quantity is < 1 or > 1000) errors["quantity"] = ["Quantity must be between 1 and 1000."];
        if (errors.Count != 0) throw new InvalidOrder(errors);
        var requestKey = Hash(owner + "\n" + key);
        var fingerprint = Hash(input.Sku + "\n" + input.Quantity.ToString(System.Globalization.CultureInfo.InvariantCulture));
        var result = await Mutate(ledger =>
        {
            if (ledger.Requests.TryGetValue(requestKey, out var previous))
            {
                if (previous.Version != 1 || previous.Fingerprint != fingerprint) { OrdersTelemetry.Submission("conflict"); throw new IdempotencyConflict(); }
                return ((ledger.Orders[previous.OrderId], false), false);
            }
            if (ledger.Orders.Count >= limits.MaximumOrders) throw new LedgerCapacity();
            var order = new Order(Guid.NewGuid().ToString("N"), owner, input.Sku!, input.Quantity, "accepted", Guid.NewGuid().ToString("N"), TimeProvider.System.GetUtcNow());
            ledger.Orders.Add(order.Id, order);
            ledger.Requests.Add(requestKey, new(fingerprint, order.Id));
            ledger.Outbox.Add(order.EventId, new(order.EventId, order.Id));
            return ((order, true), true);
        }, ct);
        OrdersTelemetry.Submission(result.Item2 ? "accepted" : "replayed");
        if (result.Item2) logger?.LogInformation("Accepted synthetic order {OrderId}", result.Item1.Id);
        return result;
    }

    public async Task<Order?> Get(string owner, string id, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(owner) || owner.Length > 256 || owner.Any(char.IsControl) || !Guid.TryParseExact(id, "N", out _)) return null;
        var state = await store.Read(ct);
        return state.Value.Orders.TryGetValue(id, out var order) && order.Owner == owner ? order : null;
    }

    public async Task<string> Process(OrderEvent message, CancellationToken ct = default)
    {
        using var activity = OrdersTelemetry.Activities.StartActivity("orders.process");
        ct.ThrowIfCancellationRequested();
        if (message is null || message.Version != 1 || !Guid.TryParseExact(message.EventId, "N", out _) || !Guid.TryParseExact(message.OrderId, "N", out _)) return "DROP";
        var result = await Mutate(ledger =>
        {
            if (message.Version != 1 || !Guid.TryParseExact(message.EventId, "N", out _) || !Guid.TryParseExact(message.OrderId, "N", out _) ||
                !ledger.Orders.TryGetValue(message.OrderId, out var order) || order.EventId != message.EventId)
                return (("DROP", false), false);
            if (ledger.Receipts.Contains(message.EventId)) return (("SUCCESS", false), false);
            ledger.Orders[order.Id] = order with { Status = "fulfilled", FulfilledAt = TimeProvider.System.GetUtcNow() };
            ledger.Receipts.Add(message.EventId);
            return (("SUCCESS", true), true);
        }, ct);
        if (result.Item2) logger?.LogInformation("Fulfilled synthetic order {OrderId}", message.OrderId);
        return result.Item1;
    }

    public async Task<int> Flush(IEventPublisher publisher, CancellationToken ct = default)
    {
        var snapshot = (await store.Read(ct)).Value;
        OrdersTelemetry.Backlog(snapshot);
        var pending = snapshot.Outbox.Values.Take(limits.BatchSize).ToArray();
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
        for (var attempt = 0; attempt < limits.CasAttempts; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            var snapshot = await store.Read(ct);
            var outcome = action(snapshot.Value);
            if (!outcome.Changed) { OrdersTelemetry.CasCompleted(0); return outcome.Result; }
            if (await store.CompareExchange(snapshot.Value, snapshot.ETag, ct)) { OrdersTelemetry.CasCompleted(attempt + 1); return outcome.Result; }
            OrdersTelemetry.CasConflict();
            await Task.Delay(Random.Shared.Next(2, Math.Min(100, 5 + attempt * 5)), ct);
        }
        OrdersTelemetry.CasExhausted();
        throw new LedgerBusy();
    }

    private static string Hash(string value) => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(value)));
}
