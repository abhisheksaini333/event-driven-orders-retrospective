using Orders;
namespace Orders.Tests;
public sealed class HookStore : ILedgerStore
{
    public MemoryStore Inner { get; } = new();
    public Func<Ledger, string, CancellationToken, Task<bool?>>? BeforeWrite { get; set; }
    public Func<CancellationToken, Task>? AfterWrite { get; set; }
    public int Reads { get; private set; }
    public int Writes { get; private set; }
    public Task<Snapshot> Read(CancellationToken ct) { Reads++; return Inner.Read(ct); }
    public async Task<bool> CompareExchange(Ledger ledger, string etag, CancellationToken ct)
    {
        Writes++;
        if (BeforeWrite != null && await BeforeWrite(ledger, etag, ct) is { } decision) return decision;
        var written = await Inner.CompareExchange(ledger, etag, ct);
        if (written && AfterWrite != null) await AfterWrite(ct);
        return written;
    }
}
