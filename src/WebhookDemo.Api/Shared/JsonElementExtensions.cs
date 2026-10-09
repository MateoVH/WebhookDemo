using System.Text.Json;

namespace WebhookDemo.Shared;

public static class JsonElementExtensions
{
    /// <summary>The string property, or a <see cref="KeyNotFoundException"/> if it's missing, null or empty.</summary>
    public static string RequiredString(this JsonElement element, string name) =>
        element.GetProperty(name).GetString() is { Length: > 0 } value
            ? value
            : throw new KeyNotFoundException($"'{name}' is required");

    public static string? OptionalString(this JsonElement element, string name) =>
        Property(element, name, JsonValueKind.String)?.GetString();

    public static long? OptionalInt64(this JsonElement element, string name) =>
        Property(element, name, JsonValueKind.Number)?.GetInt64();

    public static JsonElement? OptionalObject(this JsonElement element, string name) =>
        Property(element, name, JsonValueKind.Object);

    public static JsonElement? OptionalArray(this JsonElement element, string name) =>
        Property(element, name, JsonValueKind.Array);

    private static JsonElement? Property(JsonElement element, string name, JsonValueKind kind) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == kind
            ? value
            : null;
}
