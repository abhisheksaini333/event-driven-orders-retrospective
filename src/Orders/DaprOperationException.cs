using System.Net;
namespace Orders;
public sealed class DaprOperationException(string operation, HttpStatusCode status) : HttpRequestException("Dapr " + operation + " failed with HTTP " + (int)status, null, status)
{
    public string Operation { get; } = operation;
    public bool Retryable { get; } = status is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests || (int)status >= 500;
    public static void EnsureSuccess(HttpResponseMessage response, string operation)
    {
        if (!response.IsSuccessStatusCode) throw new DaprOperationException(operation, response.StatusCode);
    }
}
