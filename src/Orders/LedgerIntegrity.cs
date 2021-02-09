namespace Orders;
public static class LedgerIntegrity
{
    public static void Validate(Ledger ledger)
    {
        var events = new HashSet<string>();
        foreach (var pair in ledger.Orders)
        {
            var order = pair.Value;
            if (order is null || pair.Key != order.Id || !Guid.TryParseExact(order.Id, "N", out _) || !Guid.TryParseExact(order.EventId, "N", out _) ||
                string.IsNullOrWhiteSpace(order.Owner) || order.Quantity is < 1 or > 1000 || order.Status is not ("accepted" or "fulfilled") || !events.Add(order.EventId))
                throw new HttpRequestException("Ledger contains an invalid order");
            if ((order.Status == "fulfilled") != ledger.Receipts.Contains(order.EventId)) throw new HttpRequestException("Ledger fulfillment receipt is inconsistent");
        }
        if (ledger.Requests.Any(pair => pair.Value is null || !ledger.Orders.ContainsKey(pair.Value.OrderId)))
            throw new HttpRequestException("Ledger request references a missing order");
        foreach (var pair in ledger.Outbox)
        {
            var message = pair.Value;
            if (message is null || pair.Key != message.EventId || message.Version != 1 || !ledger.Orders.TryGetValue(message.OrderId, out var order) || order.EventId != message.EventId)
                throw new HttpRequestException("Ledger outbox references an invalid event");
        }
        if (ledger.Receipts.Any(receipt => !events.Contains(receipt))) throw new HttpRequestException("Ledger contains an orphan receipt");
    }
}
