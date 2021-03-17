namespace Orders;
public sealed record DaprSettings(string StateStore, string StateKey, string PubSub, string Topic)
{
    public static DaprSettings Load(IConfiguration? configuration)
    {
        string Read(string key, string fallback)
        {
            var value = configuration?["Dapr:" + key] ?? fallback;
            if (!System.Text.RegularExpressions.Regex.IsMatch(value, @"\A[A-Za-z0-9][A-Za-z0-9._-]{0,63}\z"))
                throw new ArgumentException("Dapr " + key + " must be a bounded component identifier.");
            return value;
        }
        return new(Read("StateStore", "orders-state"), Read("StateKey", "orders-ledger-v1"), Read("PubSub", "orders-pubsub"), Read("Topic", "orders.accepted"));
    }
}
