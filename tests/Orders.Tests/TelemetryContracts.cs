using System.Diagnostics;
using Microsoft.Extensions.Configuration;
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


    [Fact] public async Task CasContentionMetricsReflectFailedWrites()
    {
        var totals = new Dictionary<string, long>(); using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, owner) => { if (instrument.Meter.Name == "Orders.Core" && instrument.Name.StartsWith("orders.cas.")) owner.EnableMeasurementEvents(instrument); };
        listener.SetMeasurementEventCallback<long>((instrument, value, _, _) => totals[instrument.Name] = totals.GetValueOrDefault(instrument.Name) + value); listener.Start();
        var configuration = new Microsoft.Extensions.Configuration.ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Limits:CasAttempts"] = "1" }).Build();
        var store = new HookStore { BeforeWrite = (_, _, _) => Task.FromResult<bool?>(false) };
        await Assert.ThrowsAsync<LedgerBusy>(() => new OrdersEngine(store, configuration).Submit("alice", "metric-conflict", new("SKU-1", 1)));
        Assert.Equal(1, totals.GetValueOrDefault("orders.cas.conflicts")); Assert.Equal(1, totals.GetValueOrDefault("orders.cas.exhausted"));
    }


    [Fact] public async Task OutboxGaugesReflectPendingWork()
    {
        var observed = new Dictionary<string, double>(); using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, owner) => { if (instrument.Meter.Name == "Orders.Core" && instrument.Name.StartsWith("orders.outbox.")) owner.EnableMeasurementEvents(instrument); };
        listener.SetMeasurementEventCallback<int>((instrument, value, _, _) => observed[instrument.Name] = value);
        listener.SetMeasurementEventCallback<double>((instrument, value, _, _) => observed[instrument.Name] = value); listener.Start();
        var engine = new OrdersEngine(new MemoryStore()); await engine.Submit("alice", "outbox-gauge", new("SKU-1", 1));
        await Assert.ThrowsAsync<HttpRequestException>(() => engine.Flush(new RecordingPublisher { Fail = true }));
        listener.RecordObservableInstruments();
        Assert.Equal(1, observed.GetValueOrDefault("orders.outbox.pending", -1)); Assert.True(observed.GetValueOrDefault("orders.outbox.oldest_age", -1) >= 0);
        await engine.Flush(new RecordingPublisher()); await engine.Flush(new RecordingPublisher()); listener.RecordObservableInstruments();
        Assert.Equal(0, observed["orders.outbox.pending"]);
    }


    [Fact] public async Task OrderActivitiesFollowExecutionBoundaries()
    {
        var observed = new List<Activity>(); using var listener = new ActivityListener {
            ShouldListenTo = source => source.Name == "Orders.Core", Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStopped = activity => observed.Add(activity)
        }; ActivitySource.AddActivityListener(listener);
        var engine = new OrdersEngine(new MemoryStore()); var order = (await engine.Submit("private-owner", "private-trace-key", new("SKU-1", 1))).Order;
        await engine.Process(new(order.EventId, order.Id));
        await new DaprPublisher(new StubClients(new StubHandler(_ => new(System.Net.HttpStatusCode.NoContent)))).Publish(new(order.EventId, order.Id), default);
        Assert.Equal(new[] { "orders.submit", "orders.process", "orders.publish" }, observed.Select(item => item.OperationName));
        Assert.All(observed, item => { Assert.Null(item.GetTagItem("owner")); Assert.Null(item.GetTagItem("idempotency_key")); });
    }


    [Fact] public async Task TransitionLogsExcludeIdentityAndReplaySecrets()
    {
        var logger = new CapturingLogger<OrdersEngine>();
        var constructor = typeof(OrdersEngine).GetConstructors().SingleOrDefault(item => item.GetParameters().Length == 3); Assert.NotNull(constructor);
        var engine = (OrdersEngine)constructor.Invoke(new object?[] { new MemoryStore(), null, logger });
        var order = (await engine.Submit("private-owner", "private-log-key", new("SKU-1", 1))).Order;
        await engine.Submit("private-owner", "private-log-key", new("SKU-1", 1));
        await engine.Process(new(order.EventId, order.Id)); await engine.Process(new(order.EventId, order.Id));
        Assert.Equal(2, logger.Messages.Count);
        Assert.Contains("Accepted", logger.Messages[0]); Assert.Contains("Fulfilled", logger.Messages[1]);
        Assert.All(logger.Messages, message => { Assert.DoesNotContain("private-owner", message); Assert.DoesNotContain("private-log-key", message); });
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
