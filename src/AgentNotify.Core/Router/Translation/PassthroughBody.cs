using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AgentNotify.Core.Router.Translation;

/// <summary>Replaces the model field for passthrough hops.</summary>
public static class PassthroughBody
{
    public static byte[] ReplaceModel(ReadOnlySpan<byte> body, string nativeModel)
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
            var json = obj.ToJsonString();
            return Encoding.UTF8.GetBytes(json);
        }
        else
        {
            throw new TranslationException("invalid_request", "Invalid request shape.");
        }
    }
}
