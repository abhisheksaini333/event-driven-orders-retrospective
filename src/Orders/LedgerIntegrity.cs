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
                string.IsNullOrWhiteSpace(order.Owner) || order.Owner.Length > 256 || order.Owner.Any(char.IsControl) || order.Sku is null || !System.Text.RegularExpressions.Regex.IsMatch(order.Sku, @"\A[A-Z0-9-]{1,32}\z") || order.Quantity is < 1 or > 1000 || order.Status is not ("accepted" or "fulfilled") || !events.Add(order.EventId))
                throw new HttpRequestException("Ledger contains an invalid order");
            if ((order.Status == "accepted" && order.FulfilledAt is not null) || (order.AcceptedAt is {} accepted && order.FulfilledAt is {} fulfilled && fulfilled < accepted))
                throw new HttpRequestException("Ledger lifecycle timestamps are inconsistent");
            if ((order.Status == "fulfilled") != ledger.Receipts.Contains(order.EventId)) throw new HttpRequestException("Ledger fulfillment receipt is inconsistent");
        }
        if (ledger.Requests.Any(pair => pair.Value is null || pair.Value.Version != 1 || !System.Text.RegularExpressions.Regex.IsMatch(pair.Key, @"\A[A-Fa-f0-9]{64}\z") || pair.Value.Fingerprint is null || !System.Text.RegularExpressions.Regex.IsMatch(pair.Value.Fingerprint, @"\A[A-Fa-f0-9]{64}\z") || string.IsNullOrEmpty(pair.Value.OrderId) || !ledger.Orders.ContainsKey(pair.Value.OrderId)))
            throw new HttpRequestException("Ledger request references a missing order");
        foreach (var pair in ledger.Outbox)
        {
            var message = pair.Value;
            if (message is null || pair.Key != message.EventId || message.Version != 1 || string.IsNullOrEmpty(message.OrderId) || !ledger.Orders.TryGetValue(message.OrderId, out var order) || order.EventId != message.EventId)
                throw new HttpRequestException("Ledger outbox references an invalid event");
        }
        if (ledger.Receipts.Any(receipt => !events.Contains(receipt))) throw new HttpRequestException("Ledger contains an orphan receipt");
    }
}
