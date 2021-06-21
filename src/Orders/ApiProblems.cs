using System.Diagnostics;
namespace Orders;
public static class ApiProblems
{
    public static Task Write(HttpContext context, int status, string title, string code, IReadOnlyDictionary<string, string[]>? errors = null)
    {
        var extensions = new Dictionary<string, object?> { ["code"] = code, ["traceId"] = Activity.Current?.Id ?? context.TraceIdentifier };
        if (errors is not null) extensions["errors"] = errors;
        return Results.Problem(statusCode: status, title: title, type: "urn:orders:problem:" + code, extensions: extensions).ExecuteAsync(context);
    }
}
