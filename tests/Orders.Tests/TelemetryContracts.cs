using System.Diagnostics;
using System.Diagnostics.Metrics;
using Microsoft.Extensions.Logging;
using Orders;
using Xunit;
namespace Orders.Tests;
[CollectionDefinition("Telemetry", DisableParallelization = true)] public class TelemetryCollection { }
[Collection("Telemetry")]
public class TelemetryContracts
{

    [Fact] public async Task SubmissionMetricsHaveBoundedTags()
    {
        var observed = new List<string>(); using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, owner) => { if (instrument.Meter.Name == "Orders.Core" && instrument.Name == "orders.submissions") owner.EnableMeasurementEvents(instrument); };
        listener.SetMeasurementEventCallback<long>((_, value, tags, _) => {
            Assert.Equal(1, value); var entries = tags.ToArray(); Assert.Single(entries); Assert.Equal("outcome", entries[0].Key); observed.Add((string)entries[0].Value!);
        }); listener.Start();
        var engine = new OrdersEngine(new MemoryStore());
        await engine.Submit("private-owner", "private-metric-key", new("SKU-1", 1));
        await engine.Submit("private-owner", "private-metric-key", new("SKU-1", 1));
        await Assert.ThrowsAsync<IdempotencyConflict>(() => engine.Submit("private-owner", "private-metric-key", new("SKU-1", 2)));
        Assert.Equal(new[] { "accepted", "replayed", "conflict" }, observed);
    }

// TESTS
}
public sealed class CapturingLogger<T> : ILogger<T>
{
    public List<string> Messages { get; } = new();
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel logLevel) => true;
    public void Log<TState>(LogLevel level, EventId id, TState state, Exception? error, Func<TState, Exception?, string> formatter) => Messages.Add(formatter(state, error));
}
