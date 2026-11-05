using System.Text.Json;

namespace IDevelop.Clients;

/// <summary>Reads client JSON leniently: a missing field or a field of another kind reads as absent.</summary>
internal static class JsonFields
{
    /// <summary>The value that <paramref name="text"/> holds, which needs no disposal, or null when the text is not JSON.</summary>
    public static JsonElement? Parse(string text)
    {
        try
        {
            return JsonElement.Parse(text);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public static JsonElement? Property(this JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind != JsonValueKind.Null
            ? value
            : null;

    public static string? String(this JsonElement element, string name) =>
        element.Property(name) is { ValueKind: JsonValueKind.String } value ? value.GetString() : null;

    public static bool? Bool(this JsonElement element, string name) => element.Property(name)?.ValueKind switch
    {
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        _ => null,
    };

    public static IEnumerable<JsonElement> Items(this JsonElement element, string name) =>
        element.Property(name) is { ValueKind: JsonValueKind.Array } array ? array.EnumerateArray() : [];

    public static string? NonBlank(string? text) => string.IsNullOrWhiteSpace(text) ? null : text;
}
