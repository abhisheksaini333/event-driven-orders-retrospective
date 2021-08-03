using System.Text.Json;
using System.Text.Json.Serialization;
namespace Orders;
public sealed class CreateOrderConverter : JsonConverter<CreateOrder>
{
    public override CreateOrder Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var document = JsonDocument.ParseValue(ref reader);
        if (document.RootElement.ValueKind != JsonValueKind.Object) throw new JsonException("Order must be an object.");
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase); string? sku = null; var quantity = 0;
        foreach (var property in document.RootElement.EnumerateObject())
        {
            if (!names.Add(property.Name)) throw new JsonException("Duplicate order property.");
            switch (property.Name.ToLowerInvariant())
            {
                case "sku":
                    if (property.Value.ValueKind is not (JsonValueKind.String or JsonValueKind.Null)) throw new JsonException("SKU must be text.");
                    sku = property.Value.GetString(); break;
                case "quantity":
                    if (!property.Value.TryGetInt32(out quantity)) throw new JsonException("Quantity must be an integer.");
                    break;
                default: throw new JsonException("Unknown order property.");
            }
        }
        return new(sku!, quantity);
    }
    public override void Write(Utf8JsonWriter writer, CreateOrder value, JsonSerializerOptions options)
    {
        writer.WriteStartObject(); writer.WriteString("sku", value.Sku); writer.WriteNumber("quantity", value.Quantity); writer.WriteEndObject();
    }
}
