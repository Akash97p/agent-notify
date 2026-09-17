using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AgentNotify.Core.Router.Translation;

/// <summary>Writes Anthropic SSE to the client.</summary>
public sealed class RouterAnthropicStreamWriter
{
    private readonly string _clientModel;
    private readonly string _messageId;
    private bool _started = false;
    private bool _hasTextBlock = false;
    private int _currentBlockIndex = -1;
    private int _nextBlockIndex = 0;
    private readonly Dictionary<int, int> _toolIndexToBlock = new();
    private RouterUsage? _pendingUsage;
    private string? _pendingFinishReason;
    private bool _completedEmitted = false;
    private bool _errorEmitted = false;

    public RouterAnthropicStreamWriter(string clientModel, RouterRequest? request)
    {
        _clientModel = clientModel;
        _messageId = "msg_" + RandomHex(12);
    }

    private static string RandomHex(int bytes) => Convert.ToHexString(RandomNumberGenerator.GetBytes(bytes)).ToLowerInvariant();

    public byte[] Write(RouterStreamEvent ev)
    {
        if (_errorEmitted || _completedEmitted) return Array.Empty<byte>();
        var outBytes = new List<byte>();
        void Append(byte[] b) { if (b.Length > 0) outBytes.AddRange(b); }

        switch (ev)
        {
            case TextDeltaEvent t:
                if (!_started) { Append(WriteMessageStart()); _started = true; }
                if (!_hasTextBlock)
                {
                    _currentBlockIndex = _nextBlockIndex++;
                    Append(WriteContentBlockStart(_currentBlockIndex, "text"));
                    _hasTextBlock = true;
                }
                Append(WriteContentBlockDelta(_currentBlockIndex, "text_delta", t.Text));
                break;
            case ReasoningDeltaEvent r:
                break;
            case ToolCallStartEvent s:
                if (!_started) { Append(WriteMessageStart()); _started = true; }
                if (_hasTextBlock)
                {
                    Append(WriteContentBlockStop(_currentBlockIndex));
                    _hasTextBlock = false;
                    _currentBlockIndex = -1;
                }
                int blockIdx = _nextBlockIndex++;
                _toolIndexToBlock[s.Index] = blockIdx;
                Append(WriteContentBlockStartForTool(blockIdx, s.Id, s.Name));
                break;
            case ToolCallArgumentsDeltaEvent d:
                if (_toolIndexToBlock.TryGetValue(d.Index, out var bIdx))
                {
                    Append(WriteToolInputDelta(bIdx, d.JsonDelta));
                }
                else
                {
                    int newBlock = _nextBlockIndex++;
                    _toolIndexToBlock[d.Index] = newBlock;
                    Append(WriteContentBlockStartForTool(newBlock, $"toolu_{RandomHex(6)}", "unknown"));
                    Append(WriteToolInputDelta(newBlock, d.JsonDelta));
                }
                break;
            case ToolCallEndEvent e:
                if (_toolIndexToBlock.TryGetValue(e.Index, out var bi))
                {
                    Append(WriteContentBlockStop(bi));
                }
                break;
            case UsageEvent u:
                _pendingUsage = u.Usage;
                break;
            case FinishEvent f:
                _pendingFinishReason = f.Reason;
                break;
        }

        return outBytes.ToArray();
    }

    public byte[] Complete()
    {
        if (_errorEmitted) return Array.Empty<byte>();
        if (_completedEmitted) return Array.Empty<byte>();
        _completedEmitted = true;
        var outBytes = new List<byte>();
        void Append(byte[] b) { if (b.Length > 0) outBytes.AddRange(b); }
        string finishReason = _pendingFinishReason ?? FinishEvent.Stop;
        if (!_started) { Append(WriteMessageStart()); _started = true; }
        if (_hasTextBlock)
        {
            Append(WriteContentBlockStop(_currentBlockIndex));
            _hasTextBlock = false;
        }
        var anthReason = MapFinishToAnthropic(finishReason);
        Append(WriteMessageDelta(anthReason, _pendingUsage));
        Append(WriteMessageStop());
        return outBytes.ToArray();
    }

    public byte[] WriteError(string code, string message)
    {
        _errorEmitted = true;
        using var ms = new MemoryStream();
        var errJson = JsonSerializer.Serialize(new { type = "error", error = new { type = code, message } });
        var sse = $"event: error\ndata: {errJson}\n\n";
        return Encoding.UTF8.GetBytes(sse);
    }

    private byte[] WriteMessageStart()
    {
        using var ms = new MemoryStream();
        var obj = new
        {
            type = "message_start",
            message = new
            {
                id = _messageId,
                type = "message",
                role = "assistant",
                content = Array.Empty<object>(),
                model = _clientModel,
                stop_reason = (string?)null,
                stop_sequence = (string?)null,
                usage = new
                {
                    input_tokens = _pendingUsage?.InputTokens != null ? (int?) (int)(_pendingUsage.InputTokens.Value - (_pendingUsage.CachedInputTokens ?? 0)) : 0,
                    cache_read_input_tokens = _pendingUsage?.CachedInputTokens != null ? (int?) (int)_pendingUsage.CachedInputTokens.Value : null,
                    output_tokens = 0
                }
            }
        };
        using var wMs = new MemoryStream();
        using var writer = new Utf8JsonWriter(wMs);
        writer.WriteStartObject();
        writer.WriteString("type", "message_start");
        writer.WritePropertyName("message");
        writer.WriteStartObject();
        writer.WriteString("id", _messageId);
        writer.WriteString("type", "message");
        writer.WriteString("role", "assistant");
        writer.WritePropertyName("content");
        writer.WriteStartArray();
        writer.WriteEndArray();
        writer.WriteString("model", _clientModel);
        writer.WriteNull("stop_reason");
        writer.WriteNull("stop_sequence");
        writer.WritePropertyName("usage");
        writer.WriteStartObject();
        long inputExclusive = 0;
        long? cached = _pendingUsage?.CachedInputTokens;
        if (_pendingUsage?.InputTokens != null)
        {
            inputExclusive = _pendingUsage.InputTokens.Value - (cached ?? 0);
            if (inputExclusive < 0) inputExclusive = 0;
        }
        writer.WriteNumber("input_tokens", (int)inputExclusive);
        if (cached.HasValue) writer.WriteNumber("cache_read_input_tokens", (int)cached.Value);
        writer.WriteNumber("output_tokens", 0);
        writer.WriteEndObject();
        writer.WriteEndObject();
        writer.WriteEndObject();
        writer.Flush();
        var json = Encoding.UTF8.GetString(wMs.ToArray());
        var sse = $"event: message_start\ndata: {json}\n\n";
        return Encoding.UTF8.GetBytes(sse);
    }

    private byte[] WriteContentBlockStart(int index, string type)
    {
        using var ms = new MemoryStream();
        using var writer = new Utf8JsonWriter(ms);
        writer.WriteStartObject();
        writer.WriteString("type", "content_block_start");
        writer.WriteNumber("index", index);
        writer.WritePropertyName("content_block");
        writer.WriteStartObject();
        writer.WriteString("type", type);
        if (type == "text") writer.WriteString("text", "");
        writer.WriteEndObject();
        writer.WriteEndObject();
        writer.Flush();
        var json = Encoding.UTF8.GetString(ms.ToArray());
        return Encoding.UTF8.GetBytes($"event: content_block_start\ndata: {json}\n\n");
    }

    private byte[] WriteContentBlockStartForTool(int index, string id, string name)
    {
        using var ms = new MemoryStream();
        using var writer = new Utf8JsonWriter(ms);
        writer.WriteStartObject();
        writer.WriteString("type", "content_block_start");
        writer.WriteNumber("index", index);
        writer.WritePropertyName("content_block");
        writer.WriteStartObject();
        writer.WriteString("type", "tool_use");
        writer.WriteString("id", id.StartsWith("toolu_") ? id : "toolu_" + RandomHex(8));
        writer.WriteString("name", name);
        writer.WritePropertyName("input");
        writer.WriteStartObject();
        writer.WriteEndObject();
        writer.WriteEndObject();
        writer.WriteEndObject();
        writer.Flush();
        var json = Encoding.UTF8.GetString(ms.ToArray());
        using var ms2 = new MemoryStream();
        using var w2 = new Utf8JsonWriter(ms2);
        w2.WriteStartObject();
        w2.WriteString("type", "content_block_start");
        w2.WriteNumber("index", index);
        w2.WritePropertyName("content_block");
        w2.WriteStartObject();
        w2.WriteString("type", "tool_use");
        string finalId = string.IsNullOrEmpty(id) ? "toolu_" + RandomHex(8) : id;
        if (!finalId.StartsWith("toolu_")) finalId = "toolu_" + finalId;
        w2.WriteString("id", finalId);
        w2.WriteString("name", string.IsNullOrEmpty(name) ? "unknown" : name);
        w2.WritePropertyName("input");
        w2.WriteStartObject();
        w2.WriteEndObject();
        w2.WriteEndObject();
        w2.WriteEndObject();
        w2.Flush();
        json = Encoding.UTF8.GetString(ms2.ToArray());
        return Encoding.UTF8.GetBytes($"event: content_block_start\ndata: {json}\n\n");
    }

    private byte[] WriteContentBlockDelta(int index, string deltaType, string text)
    {
        using var ms = new MemoryStream();
        using var writer = new Utf8JsonWriter(ms);
        writer.WriteStartObject();
        writer.WriteString("type", "content_block_delta");
        writer.WriteNumber("index", index);
        writer.WritePropertyName("delta");
        writer.WriteStartObject();
        writer.WriteString("type", deltaType);
        writer.WriteString("text", text);
        writer.WriteEndObject();
        writer.WriteEndObject();
        writer.Flush();
        var json = Encoding.UTF8.GetString(ms.ToArray());
        return Encoding.UTF8.GetBytes($"event: content_block_delta\ndata: {json}\n\n");
    }

    private byte[] WriteToolInputDelta(int index, string partial)
    {
        using var ms = new MemoryStream();
        using var writer = new Utf8JsonWriter(ms);
        writer.WriteStartObject();
        writer.WriteString("type", "content_block_delta");
        writer.WriteNumber("index", index);
        writer.WritePropertyName("delta");
        writer.WriteStartObject();
        writer.WriteString("type", "input_json_delta");
        writer.WriteString("partial_json", partial);
        writer.WriteEndObject();
        writer.WriteEndObject();
        writer.Flush();
        var json = Encoding.UTF8.GetString(ms.ToArray());
        return Encoding.UTF8.GetBytes($"event: content_block_delta\ndata: {json}\n\n");
    }

    private byte[] WriteContentBlockStop(int index)
    {
        using var ms = new MemoryStream();
        using var writer = new Utf8JsonWriter(ms);
        writer.WriteStartObject();
        writer.WriteString("type", "content_block_stop");
        writer.WriteNumber("index", index);
        writer.WriteEndObject();
        writer.Flush();
        var json = Encoding.UTF8.GetString(ms.ToArray());
        return Encoding.UTF8.GetBytes($"event: content_block_stop\ndata: {json}\n\n");
    }

    private byte[] WriteMessageDelta(string stopReason, RouterUsage? usage)
    {
        using var ms = new MemoryStream();
        using var writer = new Utf8JsonWriter(ms);
        writer.WriteStartObject();
        writer.WriteString("type", "message_delta");
        writer.WritePropertyName("delta");
        writer.WriteStartObject();
        writer.WriteString("stop_reason", stopReason);
        writer.WriteNull("stop_sequence");
        writer.WriteEndObject();
        writer.WritePropertyName("usage");
        writer.WriteStartObject();
        int output = 0;
        if (usage?.OutputTokens.HasValue == true) output = (int)usage.OutputTokens.Value;
        writer.WriteNumber("output_tokens", output);
        writer.WriteEndObject();
        writer.WriteEndObject();
        writer.Flush();
        var json = Encoding.UTF8.GetString(ms.ToArray());
        return Encoding.UTF8.GetBytes($"event: message_delta\ndata: {json}\n\n");
    }

    private byte[] WriteMessageStop()
    {
        var json = """{"type":"message_stop"}""";
        return Encoding.UTF8.GetBytes($"event: message_stop\ndata: {json}\n\n");
    }

    private static string MapFinishToAnthropic(string reason) => reason switch
    {
        FinishEvent.Stop => "end_turn",
        FinishEvent.Length => "max_tokens",
        FinishEvent.ToolCalls => "tool_use",
        FinishEvent.ContentFilter => "end_turn",
        FinishEvent.Error => "end_turn",
        _ => "end_turn"
    };
}
