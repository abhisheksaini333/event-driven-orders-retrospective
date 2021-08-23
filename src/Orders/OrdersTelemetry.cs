using System.Diagnostics.Metrics;
namespace Orders;
public static class OrdersTelemetry
{
    public static readonly Meter Meter = new("Orders.Core", "1.0.0");
    private static readonly Counter<long> Submissions = Meter.CreateCounter<long>("orders.submissions", unit: "requests");
    private static readonly Counter<long> Conflicts = Meter.CreateCounter<long>("orders.cas.conflicts");
    private static readonly Counter<long> Exhaustions = Meter.CreateCounter<long>("orders.cas.exhausted");
    private static readonly Histogram<int> Attempts = Meter.CreateHistogram<int>("orders.cas.attempts");
    public static void CasConflict() => Conflicts.Add(1);
    public static void CasExhausted() => Exhaustions.Add(1);
    public static void CasCompleted(int attempts) => Attempts.Record(attempts);
    public static void Submission(string outcome) => Submissions.Add(1, new KeyValuePair<string, object?>("outcome", outcome));
}
