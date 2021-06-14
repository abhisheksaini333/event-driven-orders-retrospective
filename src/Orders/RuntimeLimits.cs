namespace Orders;
public sealed record RuntimeLimits(int MaximumOrders, int CasAttempts, int BatchSize, int DispatchIntervalMs, int MaximumStateBytes, int ReadAttempts, int MaximumReadDelayMs)
{
    public static RuntimeLimits Load(IConfiguration? configuration)
    {
        int Read(string key, int fallback, int minimum, int maximum)
        {
            var text = configuration?["Limits:" + key];
            if (text is null) return fallback;
            if (!int.TryParse(text, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var value) || value < minimum || value > maximum)
                throw new ArgumentException("Limits " + key + " is outside its supported range.");
            return value;
        }
        return new(Read("MaximumOrders", 10000, 1, 100000), Read("CasAttempts", 32, 1, 100), Read("BatchSize", 25, 1, 1000),
            Read("DispatchIntervalMs", 500, 10, 60000), Read("MaximumStateBytes", 16777216, 4096, 67108864), Read("ReadAttempts", 3, 1, 10), Read("MaximumReadDelayMs", 2000, 0, 10000));
    }
}
