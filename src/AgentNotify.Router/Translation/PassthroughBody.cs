using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AgentNotify.Router.Translation;

/// <summary>Replaces the model field for passthrough hops.</summary>
public static class PassthroughBody
{
    public static byte[] ReplaceModel(ReadOnlySpan<byte> body, string nativeModel, string? effort = null, string? effortWire = null)
    {
        if (string.IsNullOrEmpty(nativeModel))
            throw new ArgumentException("nativeModel required", nameof(nativeModel));

        if (body.IsEmpty)
            return Array.Empty<byte>();

        JsonNode? node;
        try
        {
            node = JsonNode.Parse(body);
        }
        catch (JsonException)
        {
            throw new TranslationException("invalid_request", "Invalid JSON body.");
        }

        if (node is JsonObject obj)
        {
            obj["model"] = nativeModel;
            if (effortWire == RouterWire.AnthropicMessages)
                SetNested(obj, "output_config", "effort", effort);
            else if (effortWire == RouterWire.OpenAiResponses)
                SetNested(obj, "reasoning", "effort", effort);
            else if (effortWire == RouterWire.OpenAiChat)
            {
                if (effort is null) obj.Remove("reasoning_effort");
                else obj["reasoning_effort"] = effort;
            }
            var json = obj.ToJsonString();
            return Encoding.UTF8.GetBytes(json);
        }
        else
        {
            throw new TranslationException("invalid_request", "Invalid request shape.");
        }
    }

    /// <summary>
    /// Sets or clears one field of a nested object, editing the node in place. A node that is already
    /// in the document is never reassigned to its own parent, which
    /// <see cref="System.Text.Json.Nodes"/> refuses.
    /// </summary>
    private static void SetNested(JsonObject root, string objectName, string fieldName, string? value)
    {
        if (root[objectName] is JsonObject existing)
        {
            if (value is not null) existing[fieldName] = value;
            else
            {
                existing.Remove(fieldName);
                if (existing.Count == 0) root.Remove(objectName);
            }
            return;
        }
        if (value is not null) root[objectName] = new JsonObject { [fieldName] = value };
    }
}
