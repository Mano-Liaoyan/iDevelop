using System.Text.Json;

namespace IDevelop.Execution;

/// <summary>Reads client JSON leniently: a missing field or a field of another kind reads as absent.</summary>
internal static class JsonFields
{
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
