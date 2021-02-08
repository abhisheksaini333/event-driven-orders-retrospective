using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Orders;
public sealed class DaprLedgerStore(IHttpClientFactory clients) : ILedgerStore
{
    private const string StatePath = "/v1.0/state/orders-state";
    private const string Key = "orders-ledger-v1";
    public async Task<Snapshot> Read(CancellationToken ct)
    {
        using var response = await clients.CreateClient("dapr").GetAsync(StatePath + "/" + Key + "?consistency=strong", ct);
        response.EnsureSuccessStatusCode();
        if (response.StatusCode == HttpStatusCode.NoContent) return new(new Ledger(), "0");
        Ledger ledger;
        try
        {
            var document = await response.Content.ReadFromJsonAsync<JsonElement>(ct);
            if (document.ValueKind != JsonValueKind.Object || new[] { "orders", "requests", "outbox", "receipts" }.Any(name => !document.TryGetProperty(name, out _)))
                throw new HttpRequestException("State response is missing required ledger collections");
            ledger = document.Deserialize<Ledger>(new JsonSerializerOptions(JsonSerializerDefaults.Web)) ?? throw new HttpRequestException("Invalid state response");
        }
        catch (JsonException exception) { throw new HttpRequestException("Invalid state JSON", exception); }
        if (ledger.Orders is null || ledger.Requests is null || ledger.Outbox is null || ledger.Receipts is null)
            throw new HttpRequestException("State response has null ledger collections");
        if (ledger.SchemaVersion != 1) throw new HttpRequestException("Unsupported ledger schema");
        // Dapr's Redis ETag is an unquoted number; HttpHeaders.ETag rejects it as RFC entity-tag syntax.
        var etag = response.Headers.TryGetValues("ETag", out var versions) ? versions.Single().Trim('"') : throw new HttpRequestException("State ETag is missing");
        return new(ledger, etag);
    }
    public async Task<bool> CompareExchange(Ledger ledger, string etag, CancellationToken ct)
    {
        using var response = await clients.CreateClient("dapr").PostAsJsonAsync(StatePath, new[] {
            new { key = Key, value = ledger, etag, options = new { concurrency = "first-write", consistency = "strong" } }
        }, ct);
        if (response.StatusCode == HttpStatusCode.Conflict) return false;
        response.EnsureSuccessStatusCode(); return true;
    }
}
public sealed class DaprPublisher(IHttpClientFactory clients) : IEventPublisher
{
    public async Task Publish(OrderEvent message, CancellationToken ct)
    {
        using var response = await clients.CreateClient("dapr").PostAsJsonAsync("/v1.0/publish/orders-pubsub/orders.accepted", message, ct);
        response.EnsureSuccessStatusCode();
    }
}
public sealed class OutboxDispatcher(OrdersEngine engine, IEventPublisher publisher, ILogger<OutboxDispatcher> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await engine.Flush(publisher, stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception exception) when (exception is HttpRequestException or LedgerBusy or OperationCanceledException)
            { logger.LogWarning("Outbox deferred; dependency failure type {FailureType}", exception.GetType().Name); }
            try { await Task.Delay(TimeSpan.FromMilliseconds(500), stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
        }
    }
}
