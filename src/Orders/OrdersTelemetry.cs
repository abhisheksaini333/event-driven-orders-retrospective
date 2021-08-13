using System.Diagnostics.Metrics;
namespace Orders;
public static class OrdersTelemetry
{
    public static readonly Meter Meter = new("Orders.Core", "1.0.0");
    private static readonly Counter<long> Submissions = Meter.CreateCounter<long>("orders.submissions", unit: "requests");
    public static void Submission(string outcome) => Submissions.Add(1, new KeyValuePair<string, object?>("outcome", outcome));
}
