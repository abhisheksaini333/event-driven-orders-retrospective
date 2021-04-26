using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Orders;
public sealed class DaprLedgerStore(IHttpClientFactory clients, IConfiguration? configuration = null) : ILedgerStore
{
    private const int MaximumStateBytes = 16 * 1024 * 1024;
    private readonly DaprSettings settings = DaprSettings.Load(configuration);
    private string StatePath => "/v1.0/state/" + settings.StateStore;
    private string Key => settings.StateKey;
    public async Task<Snapshot> Read(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        using var response = await ReadResponse(ct);
        DaprOperationException.EnsureSuccess(response, "read");
        if (response.StatusCode == HttpStatusCode.NoContent) return new(new Ledger(), "0");
        if (response.StatusCode != HttpStatusCode.OK) throw new HttpRequestException("Unexpected state read response status", null, response.StatusCode);
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
        if (!response.Headers.TryGetValues("ETag", out var versions)) throw new HttpRequestException("State ETag is missing");
        var values = versions.ToArray();
        if (values.Length != 1) throw new HttpRequestException("State ETag is ambiguous");
        var raw = values[0]; var etag = raw.Trim('"');
        if ((raw != etag && raw != "\"" + etag + "\"") || !long.TryParse(etag, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var version) || version <= 0)
            throw new HttpRequestException("State ETag is invalid");
        return new(ledger, etag);
    }
    private async Task<HttpResponseMessage> ReadResponse(CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            var response = await clients.CreateClient("dapr").GetAsync(StatePath + "/" + Key + "?consistency=strong", HttpCompletionOption.ResponseHeadersRead, ct);
            if (attempt >= 2 || response.StatusCode is not (HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests or HttpStatusCode.BadGateway or HttpStatusCode.ServiceUnavailable or HttpStatusCode.GatewayTimeout)) return response;
            var advised = response.Headers.RetryAfter?.Delta ?? (response.Headers.RetryAfter?.Date - DateTimeOffset.UtcNow);
            var delay = advised is { } value ? TimeSpan.FromMilliseconds(Math.Clamp(value.TotalMilliseconds, 0, 2000)) : TimeSpan.FromMilliseconds(20 * (1 << attempt));
            response.Dispose();
            await Task.Delay(delay, ct);
        }
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
        ct.ThrowIfCancellationRequested();
        var payload = JsonSerializer.SerializeToUtf8Bytes(new[] {
            new { key = Key, value = ledger, etag, options = new { concurrency = "first-write", consistency = "strong" } }
        }, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        if (payload.Length > MaximumStateBytes) throw new HttpRequestException("State write exceeds the configured size bound");
        using var content = new ByteArrayContent(payload);
        content.Headers.ContentType = new("application/json");
        using var response = await clients.CreateClient("dapr").PostAsync(StatePath, content, ct);
        if (response.StatusCode == HttpStatusCode.Conflict) return false;
        DaprOperationException.EnsureSuccess(response, "write"); return true;
    }
}
public sealed class DaprPublisher(IHttpClientFactory clients, IConfiguration? configuration = null) : IEventPublisher
{
    private readonly DaprSettings settings = DaprSettings.Load(configuration);
    public async Task Publish(OrderEvent message, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        using var response = await clients.CreateClient("dapr").PostAsJsonAsync("/v1.0/publish/" + settings.PubSub + "/" + settings.Topic, message, ct);
        DaprOperationException.EnsureSuccess(response, "publish");
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
