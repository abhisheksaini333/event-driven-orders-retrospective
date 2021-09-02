using System.Diagnostics.Metrics;
using System.Diagnostics;
namespace Orders;
public static class OrdersTelemetry
{
    public static readonly ActivitySource Activities = new("Orders.Core", "1.0.0");
    public static readonly Meter Meter = new("Orders.Core", "1.0.0");
    private static int pending;
    private static double oldestAge;
    private static readonly ObservableGauge<int> Pending = Meter.CreateObservableGauge("orders.outbox.pending", () => Volatile.Read(ref pending));
    private static readonly ObservableGauge<double> Oldest = Meter.CreateObservableGauge("orders.outbox.oldest_age", () => Volatile.Read(ref oldestAge), unit: "s");
    public static void Backlog(Ledger ledger)
    {
        Volatile.Write(ref pending, ledger.Outbox.Count);
        var timestamps = ledger.Outbox.Values.Select(message => ledger.Orders.GetValueOrDefault(message.OrderId)?.AcceptedAt).ToArray();
        var age = timestamps.Length == 0 ? 0 : timestamps.Any(value => value is null) ? double.NaN : Math.Max(0, (DateTimeOffset.UtcNow - timestamps.Min()!.Value).TotalSeconds);
        Volatile.Write(ref oldestAge, age);
    }
    private static readonly Counter<long> Submissions = Meter.CreateCounter<long>("orders.submissions", unit: "requests");
    private static readonly Counter<long> Conflicts = Meter.CreateCounter<long>("orders.cas.conflicts");
    private static readonly Counter<long> Exhaustions = Meter.CreateCounter<long>("orders.cas.exhausted");
    private static readonly Histogram<int> Attempts = Meter.CreateHistogram<int>("orders.cas.attempts");
    public static void CasConflict() => Conflicts.Add(1);
    public static void CasExhausted() => Exhaustions.Add(1);
    public static void CasCompleted(int attempts) => Attempts.Record(attempts);
    public static void Submission(string outcome) => Submissions.Add(1, new KeyValuePair<string, object?>("outcome", outcome));
}
