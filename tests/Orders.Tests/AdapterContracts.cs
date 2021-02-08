using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Orders;
using Xunit;
namespace Orders.Tests;
public class AdapterContracts
{
    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web);
    private static DaprLedgerStore Responding(string json, HttpStatusCode status = HttpStatusCode.OK, string etag = "1")
    {
        var response = new HttpResponseMessage(status) { Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json") };
        response.Headers.TryAddWithoutValidation("ETag", etag);
        return new(new StubClients(new StubHandler(_ => response)));
    }

    [Fact] public async Task UnsupportedLedgerSchemaIsRejected()
    {
        var document = JsonSerializer.SerializeToNode(new Ledger(), WebJson)!.AsObject();
        document["schemaVersion"] = 99;
        await Assert.ThrowsAsync<HttpRequestException>(() => Responding(document.ToJsonString()).Read(default));
        document.Remove("schemaVersion");
        Assert.Empty((await Responding(document.ToJsonString()).Read(default)).Value.Orders);
    }


    [Fact] public async Task InvalidLedgerShapeIsRejected()
    {
        foreach (var invalid in new[] {
            "{}", "null", "[]",
            "{\"orders\":null,\"requests\":{},\"outbox\":{},\"receipts\":[]}",
            "{\"orders\":{},\"requests\":[],\"outbox\":{},\"receipts\":[]}" })
            await Assert.ThrowsAsync<HttpRequestException>(() => Responding(invalid).Read(default));
    }

// TESTS
}
