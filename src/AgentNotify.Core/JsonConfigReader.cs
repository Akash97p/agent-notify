using System.Text.Json;

namespace AgentNotify.Core;

/// <summary>
/// Reads optional values out of a stored provider configuration document.
/// </summary>
/// <remarks>
/// <see cref="JsonElement.TryGetInt32(out int)"/> does not do what its name suggests: it returns
/// false only when the element is a number that will not fit, and <b>throws</b>
/// <see cref="InvalidOperationException"/> for every other kind, including JSON <c>null</c>.
/// Optional settings are serialized as <c>null</c> when the user leaves them blank, so reading one
/// back with <c>TryGetProperty(...) &amp;&amp; element.TryGetInt32(...)</c> throws on exactly the
/// configuration a user is most likely to have. These helpers treat absent, null, and
/// wrong-typed values alike as "not set".
/// </remarks>
public static class JsonConfigReader
{
    /// <summary>Reads an optional integer. Absent, null, or non-numeric values yield false.</summary>
    public static bool TryGetInt32(JsonElement root, string propertyName, out int value)
    {
        value = 0;
        if (root.ValueKind != JsonValueKind.Object) return false;
        if (!root.TryGetProperty(propertyName, out var element)) return false;
        if (element.ValueKind != JsonValueKind.Number) return false;
        return element.TryGetInt32(out value);
    }

    /// <summary>Reads an optional integer as text, or an empty string when it is not set.</summary>
    public static string GetInt32Text(JsonElement root, string propertyName) =>
        TryGetInt32(root, propertyName, out var value) ? value.ToString() : "";

    /// <summary>Reads an optional string. Absent, null, or non-string values yield an empty string.</summary>
    public static string GetString(JsonElement root, string propertyName)
    {
        if (root.ValueKind != JsonValueKind.Object) return "";
        if (!root.TryGetProperty(propertyName, out var element)) return "";
        return element.ValueKind == JsonValueKind.String ? element.GetString() ?? "" : "";
    }

    /// <summary>Reads an optional boolean, falling back to <paramref name="fallback"/>.</summary>
    public static bool GetBoolean(JsonElement root, string propertyName, bool fallback)
    {
        if (root.ValueKind != JsonValueKind.Object) return fallback;
        if (!root.TryGetProperty(propertyName, out var element)) return fallback;
        return element.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => fallback
        };
    }

    /// <summary>
    /// Reads the elements of an optional string array, skipping entries that are not strings.
    /// </summary>
    public static IReadOnlyList<string> GetStringArray(JsonElement root, string propertyName)
    {
        if (root.ValueKind != JsonValueKind.Object) return [];
        if (!root.TryGetProperty(propertyName, out var element)) return [];
        if (element.ValueKind != JsonValueKind.Array) return [];

        var values = new List<string>();
        foreach (var item in element.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String) continue;
            var text = item.GetString();
            if (!string.IsNullOrEmpty(text)) values.Add(text);
        }

        return values;
    }
}
