using Orders;
namespace Orders.Tests;
public sealed class HookStore : ILedgerStore
{
    public MemoryStore Inner { get; } = new();
    public int Reads { get; private set; }
    public int Writes { get; private set; }
    public Task<Snapshot> Read(CancellationToken ct) { Reads++; return Inner.Read(ct); }
    public async Task<bool> CompareExchange(Ledger ledger, string etag, CancellationToken ct)
    {
        Writes++;
        return await Inner.CompareExchange(ledger, etag, ct);
    }
}
