using System.Text.Json;
using System.Text.Json.Nodes;

namespace AgentNotify.Core.Router;

/// <summary>
/// The ChatGPT-plan Codex backend accepts a narrower Responses request than the public API: it keeps
/// no state, so <c>store</c> must be false, and it rejects the output-length and sampling
/// parameters, and it only answers as a stream. A request is adjusted to that shape just before it is
/// sent there.
/// </summary>
internal static class ChatGptBackendBody
{
    private static readonly string[] Unsupported =
        ["max_output_tokens", "max_tokens", "temperature", "top_p", "previous_response_id"];

    public static byte[] Adapt(byte[] body)
    {
        JsonObject? root;
        try { root = JsonNode.Parse(body) as JsonObject; }
        catch (JsonException) { return body; }
        if (root is null) return body;

        root["store"] = false;
        // The backend only streams. A client that wanted one response gets the stream collected into
        // one (RouterProxy.CollectStream).
        root["stream"] = true;
        foreach (var name in Unsupported) root.Remove(name);
        // The backend requires instructions; an empty string satisfies it when the client sent none.
        if (root["instructions"] is null) root["instructions"] = "";
        return JsonSerializer.SerializeToUtf8Bytes(root);
    }
}
