using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AgentNotify.Router.Translation;

/// <summary>Writes Chat Completions SSE to the client.</summary>
public sealed class RouterChatStreamWriter
{
    private readonly string _clientModel;
    private readonly string _id;
    private readonly long _created;
    private bool _hasSentRole = false;
    private RouterUsage? _pendingUsage;
    private string? _pendingFinishReason;
    private bool _completedEmitted = false;
    private bool _errorEmitted = false;
    private readonly Dictionary<int, (string id, string name)> _toolCalls = new();

    public RouterChatStreamWriter(string clientModel, RouterRequest? request)
    {
        _clientModel = clientModel;
        _id = "chatcmpl-" + RandomHex(12);
        _created = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
    }

    private static string RandomHex(int bytes)
    {
        var b = RandomNumberGenerator.GetBytes(bytes);
        return Convert.ToHexString(b).ToLowerInvariant();
    }

    public byte[] Write(RouterStreamEvent ev)
    {
        if (_errorEmitted || _completedEmitted) return Array.Empty<byte>();
        switch (ev)
        {
            case TextDeltaEvent t:
                return WriteChunk(deltaContent: t.Text);
            case ReasoningDeltaEvent r:
                return WriteChunk(reasoningContent: r.Text);
            case ToolCallStartEvent s:
                _toolCalls[s.Index] = (s.Id, s.Name);
                return WriteChunk(toolCallIndex: s.Index, toolCallId: s.Id, toolCallName: s.Name, toolCallArgs: null);
            case ToolCallArgumentsDeltaEvent d:
                if (!_toolCalls.TryGetValue(d.Index, out var entry))
                {
                    entry = ($"call_{d.Index}", "");
                    _toolCalls[d.Index] = entry;
                }
                return WriteChunk(toolCallIndex: d.Index, toolCallArgs: d.JsonDelta);
            case ToolCallEndEvent e:
                return Array.Empty<byte>();
            case UsageEvent u:
                _pendingUsage = u.Usage;
                return Array.Empty<byte>();
            case FinishEvent f:
                _pendingFinishReason = MapFinishToChat(f.Reason);
                return Array.Empty<byte>();
            default:
                return Array.Empty<byte>();
        }
    }

    public byte[] Complete()
    {
        if (_errorEmitted) return Array.Empty<byte>();
        if (_completedEmitted) return Array.Empty<byte>();
        _completedEmitted = true;
        string finishReason = _pendingFinishReason ?? "stop";
        var finishBytes = WriteChunk(finishReason: finishReason);
        byte[] usageBytes = Array.Empty<byte>();
        if (_pendingUsage != null)
        {
            usageBytes = WriteUsageChunk(_pendingUsage);
        }
        byte[] done = Encoding.UTF8.GetBytes("data: [DONE]\n\n");
        var total = new byte[finishBytes.Length + usageBytes.Length + done.Length];
        Buffer.BlockCopy(finishBytes, 0, total, 0, finishBytes.Length);
        Buffer.BlockCopy(usageBytes, 0, total, finishBytes.Length, usageBytes.Length);
        Buffer.BlockCopy(done, 0, total, finishBytes.Length + usageBytes.Length, done.Length);
        return total;
    }

    public byte[] WriteError(string code, string message)
    {
        _errorEmitted = true;
        using var ms = new MemoryStream();
        using var writer = new Utf8JsonWriter(ms);
        writer.WriteStartObject();
        writer.WritePropertyName("error");
        writer.WriteStartObject();
        writer.WriteString("message", message);
        writer.WriteString("type", code);
        writer.WriteString("code", code);
        writer.WriteEndObject();
        writer.WriteEndObject();
        writer.Flush();
        var json = Encoding.UTF8.GetString(ms.ToArray());
        var bytes = Encoding.UTF8.GetBytes($"data: {json}\n\n");
        var done = Encoding.UTF8.GetBytes("data: [DONE]\n\n");
        var result = new byte[bytes.Length + done.Length];
        Buffer.BlockCopy(bytes, 0, result, 0, bytes.Length);
        Buffer.BlockCopy(done, 0, result, bytes.Length, done.Length);
        return result;
    }

    public byte[] Flush() => Array.Empty<byte>();

    private byte[] WriteChunk(string? deltaContent = null, string? reasoningContent = null, int? toolCallIndex = null, string? toolCallId = null, string? toolCallName = null, string? toolCallArgs = null, string? finishReason = null)
    {
        using var ms = new MemoryStream();
        using var writer = new Utf8JsonWriter(ms);
        writer.WriteStartObject();
        writer.WriteString("id", _id);
        writer.WriteString("object", "chat.completion.chunk");
        writer.WriteNumber("created", _created);
        writer.WriteString("model", _clientModel);
        writer.WritePropertyName("choices");
        writer.WriteStartArray();
        writer.WriteStartObject();
        writer.WritePropertyName("delta");
        writer.WriteStartObject();
        if (!_hasSentRole)
        {
            writer.WriteString("role", "assistant");
            _hasSentRole = true;
        }
        if (deltaContent != null)
            writer.WriteString("content", deltaContent);
        if (reasoningContent != null)
            writer.WriteString("reasoning_content", reasoningContent);
        if (toolCallIndex.HasValue)
        {
            writer.WritePropertyName("tool_calls");
            writer.WriteStartArray();
            writer.WriteStartObject();
            writer.WriteNumber("index", toolCallIndex.Value);
            if (toolCallId != null)
                writer.WriteString("id", toolCallId);
            if (toolCallName != null)
            {
                writer.WritePropertyName("function");
                writer.WriteStartObject();
                writer.WriteString("name", toolCallName);
                if (toolCallArgs != null) writer.WriteString("arguments", toolCallArgs);
                writer.WriteEndObject();
            }
            else if (toolCallArgs != null)
            {
                writer.WritePropertyName("function");
                writer.WriteStartObject();
                writer.WriteString("arguments", toolCallArgs);
                writer.WriteEndObject();
            }
            writer.WriteEndObject();
            writer.WriteEndArray();
        }
        writer.WriteEndObject();
        if (finishReason != null)
            writer.WriteString("finish_reason", finishReason);
        else
            writer.WriteNull("finish_reason");
        writer.WriteNumber("index", 0);
        writer.WriteEndObject();
        writer.WriteEndArray();
        writer.WriteEndObject();
        writer.Flush();
        var json = Encoding.UTF8.GetString(ms.ToArray());
        return Encoding.UTF8.GetBytes($"data: {json}\n\n");
    }

    private byte[] WriteUsageChunk(RouterUsage usage)
    {
        using var ms = new MemoryStream();
        using var writer = new Utf8JsonWriter(ms);
        writer.WriteStartObject();
        writer.WriteString("id", _id);
        writer.WriteString("object", "chat.completion.chunk");
        writer.WriteNumber("created", _created);
        writer.WriteString("model", _clientModel);
        writer.WritePropertyName("choices");
        writer.WriteStartArray();
        writer.WriteStartObject();
        writer.WritePropertyName("delta");
        writer.WriteStartObject();
        writer.WriteEndObject();
        writer.WriteNull("finish_reason");
        writer.WriteNumber("index", 0);
        writer.WriteEndObject();
        writer.WriteEndArray();
        writer.WritePropertyName("usage");
        writer.WriteStartObject();
        if (usage.InputTokens.HasValue) writer.WriteNumber("prompt_tokens", usage.InputTokens.Value);
        if (usage.OutputTokens.HasValue) writer.WriteNumber("completion_tokens", usage.OutputTokens.Value);
        if (usage.InputTokens.HasValue && usage.OutputTokens.HasValue)
            writer.WriteNumber("total_tokens", usage.InputTokens.Value + usage.OutputTokens.Value);
        if (usage.CachedInputTokens.HasValue || usage.ReasoningTokens.HasValue)
        {
            if (usage.CachedInputTokens.HasValue)
            {
                writer.WritePropertyName("prompt_tokens_details");
                writer.WriteStartObject();
                writer.WriteNumber("cached_tokens", usage.CachedInputTokens.Value);
                writer.WriteEndObject();
            }
            if (usage.ReasoningTokens.HasValue)
            {
                writer.WritePropertyName("completion_tokens_details");
                writer.WriteStartObject();
                writer.WriteNumber("reasoning_tokens", usage.ReasoningTokens.Value);
                writer.WriteEndObject();
            }
        }
        writer.WriteEndObject();
        writer.WriteEndObject();
        writer.Flush();
        var json = Encoding.UTF8.GetString(ms.ToArray());
        return Encoding.UTF8.GetBytes($"data: {json}\n\n");
    }

    private static string? MapFinishToChat(string reason) => reason switch
    {
        FinishEvent.Stop => "stop",
        FinishEvent.Length => "length",
        FinishEvent.ToolCalls => "tool_calls",
        FinishEvent.ContentFilter => "content_filter",
        FinishEvent.Error => null, // error handled separately
        _ => "stop"
    };
}
