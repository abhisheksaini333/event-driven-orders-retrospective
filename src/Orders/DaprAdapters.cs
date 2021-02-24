using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Orders;
public sealed class DaprLedgerStore(IHttpClientFactory clients) : ILedgerStore
{
    private const int MaximumStateBytes = 16 * 1024 * 1024;
    private const string StatePath = "/v1.0/state/orders-state";
    private const string Key = "orders-ledger-v1";
    public async Task<Snapshot> Read(CancellationToken ct)
    {
        using var response = await clients.CreateClient("dapr").GetAsync(StatePath + "/" + Key + "?consistency=strong", HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();
        if (response.StatusCode == HttpStatusCode.NoContent) return new(new Ledger(), "0");
        Ledger ledger;
        try
        {
            var document = JsonSerializer.Deserialize<JsonElement>(await ReadBounded(response.Content, ct));
            if (document.ValueKind != JsonValueKind.Object || new[] { "orders", "requests", "outbox", "receipts" }.Any(name => !document.TryGetProperty(name, out _)))
                throw new HttpRequestException("State response is missing required ledger collections");
            ledger = document.Deserialize<Ledger>(new JsonSerializerOptions(JsonSerializerDefaults.Web)) ?? throw new HttpRequestException("Invalid state response");
        }
        catch (JsonException exception) { throw new HttpRequestException("Invalid state JSON", exception); }
        if (ledger.Orders is null || ledger.Requests is null || ledger.Outbox is null || ledger.Receipts is null)
            throw new HttpRequestException("State response has null ledger collections");
        if (ledger.SchemaVersion != 1) throw new HttpRequestException("Unsupported ledger schema");
        LedgerIntegrity.Validate(ledger);
        // Dapr's Redis ETag is an unquoted number; HttpHeaders.ETag rejects it as RFC entity-tag syntax.
        var etag = response.Headers.TryGetValues("ETag", out var versions) ? versions.Single().Trim('"') : throw new HttpRequestException("State ETag is missing");
        return new(ledger, etag);
    }
    private static async Task<byte[]> ReadBounded(HttpContent content, CancellationToken ct)
    {
        if (content.Headers.ContentLength > MaximumStateBytes) throw new HttpRequestException("State response exceeds the configured size bound");
        await using var input = await content.ReadAsStreamAsync(ct);
        using var output = new MemoryStream();
        var buffer = new byte[8192];
        int count;
        while ((count = await input.ReadAsync(buffer, ct)) != 0)
        {
            if (output.Length + count > MaximumStateBytes) throw new HttpRequestException("State response exceeds the configured size bound");
            output.Write(buffer, 0, count);
        }
        return output.ToArray();
    }
    public async Task<bool> CompareExchange(Ledger ledger, string etag, CancellationToken ct)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(new[] {
            new { key = Key, value = ledger, etag, options = new { concurrency = "first-write", consistency = "strong" } }
        }, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        if (payload.Length > MaximumStateBytes) throw new HttpRequestException("State write exceeds the configured size bound");
        using var content = new ByteArrayContent(payload);
        content.Headers.ContentType = new("application/json");
        using var response = await clients.CreateClient("dapr").PostAsync(StatePath, content, ct);
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
