using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AgentNotify.Core.Router.Translation;

/// <summary>Writes Responses SSE to the client.</summary>
public sealed class RouterResponsesStreamWriter
{
    private readonly string _clientModel;
    private readonly RouterRequest? _request;
    private readonly string _respId;
    private readonly string _msgId;
    private int _seq = 0;
    private bool _started = false;
    private bool _hasMessageItem = false;
    private string? _msgItemId;
    private bool _hasTextPart = false;
    private int _outputIndex = 0;
    private int _contentIndex = 0;
    private readonly StringBuilder _textBuffer = new();
    private readonly Dictionary<int, (string callId, string name, StringBuilder args, string itemId)> _toolCalls = new();
    private RouterUsage? _pendingUsage;
    private string? _pendingFinishReason;
    private bool _completedEmitted = false;
    private bool _errorEmitted = false;
    private readonly List<(string callId, string name, string args)> _completedTools = new();
    private string _finalText = "";

    public RouterResponsesStreamWriter(string clientModel, RouterRequest? request)
    {
        _clientModel = clientModel;
        _request = request;
        _respId = "resp_" + RandomHex(12);
        _msgId = "msg_" + RandomHex(12);
    }

    private static string RandomHex(int bytes) => Convert.ToHexString(RandomNumberGenerator.GetBytes(bytes)).ToLowerInvariant();

    private int NextSeq() => _seq++;

    public byte[] Write(RouterStreamEvent ev)
    {
        if (_errorEmitted || _completedEmitted) return Array.Empty<byte>();
        var outBytes = new List<byte>();
        void Append(byte[] b) { if (b.Length > 0) outBytes.AddRange(b); }

        switch (ev)
        {
            case TextDeltaEvent t:
                if (!_started) { Append(WriteCreated()); Append(WriteInProgress()); _started = true; }
                if (!_hasMessageItem)
                {
                    _msgItemId = "msg_" + RandomHex(8);
                    Append(WriteOutputItemAddedMessage(_msgItemId));
                    Append(WriteContentPartAdded(_msgItemId));
                    _hasMessageItem = true;
                    _hasTextPart = true;
                }
                _textBuffer.Append(t.Text);
                _finalText += t.Text;
                Append(WriteOutputTextDelta(_msgItemId!, t.Text));
                break;
            case ReasoningDeltaEvent r:
                break;
            case ToolCallStartEvent s:
                if (!_started) { Append(WriteCreated()); Append(WriteInProgress()); _started = true; }
                if (_hasMessageItem && _hasTextPart)
                {
                    Append(WriteOutputTextDone(_msgItemId!, _textBuffer.ToString()));
                    Append(WriteContentPartDone(_msgItemId!));
                    Append(WriteOutputItemDoneMessage(_msgItemId!, _textBuffer.ToString()));
                    _hasMessageItem = false;
                    _hasTextPart = false;
                    _textBuffer.Clear();
                    _outputIndex++;
                }
                var itemId = "fc_" + RandomHex(8);
                bool isCustom = _request != null && _request.CustomToolNames.Contains(s.Name);
                string finalItemId = isCustom ? "ctc_" + RandomHex(8) : itemId;
                _toolCalls[s.Index] = (s.Id, s.Name, new StringBuilder(), finalItemId);
                Append(WriteOutputItemAddedTool(finalItemId, isCustom, s.Id, s.Name));
                break;
            case ToolCallArgumentsDeltaEvent d:
                if (_toolCalls.TryGetValue(d.Index, out var entry))
                {
                    entry.args.Append(d.JsonDelta);
                    bool isCustom2 = _request != null && _request.CustomToolNames.Contains(entry.name);
                    if (isCustom2)
                        Append(WriteCustomToolInputDelta(entry.itemId, d.JsonDelta));
                    else
                        Append(WriteFunctionCallDelta(entry.itemId, d.JsonDelta));
                }
                else
                {
                    var itemId2 = "fc_" + RandomHex(8);
                    _toolCalls[d.Index] = ("call_" + d.Index, "unknown", new StringBuilder(d.JsonDelta), itemId2);
                    Append(WriteFunctionCallDelta(itemId2, d.JsonDelta));
                }
                break;
            case ToolCallEndEvent e:
                if (_toolCalls.TryGetValue(e.Index, out var ent))
                {
                    bool isCustom3 = _request != null && _request.CustomToolNames.Contains(ent.name);
                    if (isCustom3)
                    {
                        string raw = ent.args.ToString();
                        string input = ExtractCustomInput(raw);
                        Append(WriteCustomToolDone(ent.itemId, ent.callId, ent.name, input));
                    }
                    else
                    {
                        string args = ent.args.ToString();
                        Append(WriteFunctionCallDone(ent.itemId, args));
                    }
                    Append(WriteOutputItemDoneTool(ent.itemId, isCustom3, ent.callId, ent.name, ent.args.ToString()));
                    _completedTools.Add((ent.callId, ent.name, ent.args.ToString()));
                    _toolCalls.Remove(e.Index);
                    _outputIndex++;
                }
                break;
            case UsageEvent u:
                if (_errorEmitted || _completedEmitted) break;
                _pendingUsage = u.Usage;
                break;
            case FinishEvent f:
                if (_errorEmitted || _completedEmitted) break;
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
        if (!_started) { Append(WriteCreated()); Append(WriteInProgress()); _started = true; }
        if (_hasMessageItem && _hasTextPart)
        {
            Append(WriteOutputTextDone(_msgItemId!, _textBuffer.ToString()));
            Append(WriteContentPartDone(_msgItemId!));
            Append(WriteOutputItemDoneMessage(_msgItemId!, _textBuffer.ToString()));
            _hasMessageItem = false;
            _hasTextPart = false;
            _textBuffer.Clear();
        }
        foreach (var kv in _toolCalls.ToList())
        {
            bool isCustom4 = _request != null && _request.CustomToolNames.Contains(kv.Value.name);
            if (isCustom4)
            {
                string input = ExtractCustomInput(kv.Value.args.ToString());
                Append(WriteCustomToolDone(kv.Value.itemId, kv.Value.callId, kv.Value.name, input));
                Append(WriteOutputItemDoneTool(kv.Value.itemId, true, kv.Value.callId, kv.Value.name, kv.Value.args.ToString()));
            }
            else
            {
                Append(WriteFunctionCallDone(kv.Value.itemId, kv.Value.args.ToString()));
                Append(WriteOutputItemDoneTool(kv.Value.itemId, false, kv.Value.callId, kv.Value.name, kv.Value.args.ToString()));
            }
            _completedTools.Add((kv.Value.callId, kv.Value.name, kv.Value.args.ToString()));
        }
        _toolCalls.Clear();
        Append(WriteCompleted(finishReason, _pendingUsage));
        return outBytes.ToArray();
    }

    public byte[] WriteError(string code, string message)
    {
        _errorEmitted = true;
        using var ms = new MemoryStream();
        using var writer = new Utf8JsonWriter(ms);
        writer.WriteStartObject();
        writer.WriteString("type", "response.failed");
        writer.WriteNumber("sequence_number", NextSeq());
        writer.WritePropertyName("response");
        writer.WriteStartObject();
        writer.WriteString("id", _respId);
        writer.WriteString("object", "response");
        writer.WriteNumber("created_at", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        writer.WriteString("status", "failed");
        writer.WriteString("model", _clientModel);
        writer.WritePropertyName("error");
        writer.WriteStartObject();
        writer.WriteString("code", code);
        writer.WriteString("message", message);
        writer.WriteEndObject();
        writer.WriteEndObject();
        writer.WriteEndObject();
        writer.Flush();
        var json = Encoding.UTF8.GetString(ms.ToArray());
        return Encoding.UTF8.GetBytes($"event: response.failed\ndata: {json}\n\n");
    }

    private static string ExtractCustomInput(string raw)
    {
        try
        {
            using var doc = JsonDocument.Parse(raw);
            if (doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("input", out var inp) && inp.ValueKind == JsonValueKind.String)
                return inp.GetString() ?? raw;
            return raw;
        }
        catch
        {
            return raw;
        }
    }

    private byte[] WriteCreated()
    {
        using var ms = new MemoryStream();
        using var writer = new Utf8JsonWriter(ms);
        writer.WriteStartObject();
        writer.WriteString("type", "response.created");
        writer.WriteNumber("sequence_number", NextSeq());
        writer.WritePropertyName("response");
        writer.WriteStartObject();
        writer.WriteString("id", _respId);
        writer.WriteString("object", "response");
        writer.WriteNumber("created_at", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        writer.WriteString("status", "in_progress");
        writer.WriteString("model", _clientModel);
        writer.WritePropertyName("output");
        writer.WriteStartArray();
        writer.WriteEndArray();
        writer.WriteEndObject();
        writer.WriteEndObject();
        writer.Flush();
        return Encoding.UTF8.GetBytes($"event: response.created\ndata: {Encoding.UTF8.GetString(ms.ToArray())}\n\n");
    }

    private byte[] WriteInProgress()
    {
        using var ms = new MemoryStream();
        using var writer = new Utf8JsonWriter(ms);
        writer.WriteStartObject();
        writer.WriteString("type", "response.in_progress");
        writer.WriteNumber("sequence_number", NextSeq());
        writer.WritePropertyName("response");
        writer.WriteStartObject();
        writer.WriteString("id", _respId);
        writer.WriteString("object", "response");
        writer.WriteNumber("created_at", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        writer.WriteString("status", "in_progress");
        writer.WriteString("model", _clientModel);
        writer.WritePropertyName("output");
        writer.WriteStartArray();
        writer.WriteEndArray();
        writer.WriteEndObject();
        writer.WriteEndObject();
        writer.Flush();
        return Encoding.UTF8.GetBytes($"event: response.in_progress\ndata: {Encoding.UTF8.GetString(ms.ToArray())}\n\n");
    }

    private byte[] WriteOutputItemAddedMessage(string itemId)
    {
        using var ms = new MemoryStream();
        using var writer = new Utf8JsonWriter(ms);
        writer.WriteStartObject();
        writer.WriteString("type", "response.output_item.added");
        writer.WriteNumber("sequence_number", NextSeq());
        writer.WriteNumber("output_index", _outputIndex);
        writer.WritePropertyName("item");
        writer.WriteStartObject();
        writer.WriteString("type", "message");
        writer.WriteString("id", itemId);
        writer.WriteString("role", "assistant");
        writer.WriteString("status", "in_progress");
        writer.WritePropertyName("content");
        writer.WriteStartArray();
        writer.WriteEndArray();
        writer.WriteEndObject();
        writer.WriteEndObject();
        writer.Flush();
        return Encoding.UTF8.GetBytes($"event: response.output_item.added\ndata: {Encoding.UTF8.GetString(ms.ToArray())}\n\n");
    }

    private byte[] WriteContentPartAdded(string itemId)
    {
        using var ms = new MemoryStream();
        using var writer = new Utf8JsonWriter(ms);
        writer.WriteStartObject();
        writer.WriteString("type", "response.content_part.added");
        writer.WriteNumber("sequence_number", NextSeq());
        writer.WriteNumber("output_index", _outputIndex);
        writer.WriteNumber("content_index", _contentIndex);
        writer.WriteString("item_id", itemId);
        writer.WritePropertyName("part");
        writer.WriteStartObject();
        writer.WriteString("type", "output_text");
        writer.WriteString("text", "");
        writer.WritePropertyName("annotations");
        writer.WriteStartArray();
        writer.WriteEndArray();
        writer.WriteEndObject();
        writer.WriteEndObject();
        writer.Flush();
        return Encoding.UTF8.GetBytes($"event: response.content_part.added\ndata: {Encoding.UTF8.GetString(ms.ToArray())}\n\n");
    }

    private byte[] WriteOutputTextDelta(string itemId, string delta)
    {
        using var ms = new MemoryStream();
        using var writer = new Utf8JsonWriter(ms);
        writer.WriteStartObject();
        writer.WriteString("type", "response.output_text.delta");
        writer.WriteNumber("sequence_number", NextSeq());
        writer.WriteNumber("output_index", _outputIndex);
        writer.WriteNumber("content_index", _contentIndex);
        writer.WriteString("item_id", itemId);
        writer.WriteString("delta", delta);
        writer.WriteEndObject();
        writer.Flush();
        return Encoding.UTF8.GetBytes($"event: response.output_text.delta\ndata: {Encoding.UTF8.GetString(ms.ToArray())}\n\n");
    }

    private byte[] WriteOutputTextDone(string itemId, string text)
    {
        using var ms = new MemoryStream();
        using var writer = new Utf8JsonWriter(ms);
        writer.WriteStartObject();
        writer.WriteString("type", "response.output_text.done");
        writer.WriteNumber("sequence_number", NextSeq());
        writer.WriteNumber("output_index", _outputIndex);
        writer.WriteNumber("content_index", _contentIndex);
        writer.WriteString("item_id", itemId);
        writer.WriteString("text", text);
        writer.WriteEndObject();
        writer.Flush();
        return Encoding.UTF8.GetBytes($"event: response.output_text.done\ndata: {Encoding.UTF8.GetString(ms.ToArray())}\n\n");
    }

    private byte[] WriteContentPartDone(string itemId)
    {
        using var ms = new MemoryStream();
        using var writer = new Utf8JsonWriter(ms);
        writer.WriteStartObject();
        writer.WriteString("type", "response.content_part.done");
        writer.WriteNumber("sequence_number", NextSeq());
        writer.WriteNumber("output_index", _outputIndex);
        writer.WriteNumber("content_index", _contentIndex);
        writer.WriteString("item_id", itemId);
        writer.WritePropertyName("part");
        writer.WriteStartObject();
        writer.WriteString("type", "output_text");
        writer.WriteString("text", _textBuffer.ToString());
        writer.WritePropertyName("annotations");
        writer.WriteStartArray();
        writer.WriteEndArray();
        writer.WriteEndObject();
        writer.WriteEndObject();
        writer.Flush();
        return Encoding.UTF8.GetBytes($"event: response.content_part.done\ndata: {Encoding.UTF8.GetString(ms.ToArray())}\n\n");
    }

    private byte[] WriteOutputItemDoneMessage(string itemId, string text)
    {
        using var ms = new MemoryStream();
        using var writer = new Utf8JsonWriter(ms);
        writer.WriteStartObject();
        writer.WriteString("type", "response.output_item.done");
        writer.WriteNumber("sequence_number", NextSeq());
        writer.WriteNumber("output_index", _outputIndex);
        writer.WritePropertyName("item");
        writer.WriteStartObject();
        writer.WriteString("type", "message");
        writer.WriteString("id", itemId);
        writer.WriteString("role", "assistant");
        writer.WriteString("status", "completed");
        writer.WritePropertyName("content");
        writer.WriteStartArray();
        writer.WriteStartObject();
        writer.WriteString("type", "output_text");
        writer.WriteString("text", text);
        writer.WritePropertyName("annotations");
        writer.WriteStartArray();
        writer.WriteEndArray();
        writer.WriteEndObject();
        writer.WriteEndArray();
        writer.WriteEndObject();
        writer.WriteEndObject();
        writer.Flush();
        return Encoding.UTF8.GetBytes($"event: response.output_item.done\ndata: {Encoding.UTF8.GetString(ms.ToArray())}\n\n");
    }

    private byte[] WriteOutputItemAddedTool(string itemId, bool isCustom, string callId, string name)
    {
        using var ms = new MemoryStream();
        using var writer = new Utf8JsonWriter(ms);
        writer.WriteStartObject();
        writer.WriteString("type", "response.output_item.added");
        writer.WriteNumber("sequence_number", NextSeq());
        writer.WriteNumber("output_index", _outputIndex);
        writer.WritePropertyName("item");
        writer.WriteStartObject();
        writer.WriteString("type", isCustom ? "custom_tool_call" : "function_call");
        writer.WriteString("id", itemId);
        writer.WriteString("call_id", callId);
        writer.WriteString("name", name);
        if (!isCustom) writer.WriteString("arguments", "");
        else writer.WriteString("input", "");
        writer.WriteString("status", "in_progress");
        writer.WriteEndObject();
        writer.WriteEndObject();
        writer.Flush();
        return Encoding.UTF8.GetBytes($"event: response.output_item.added\ndata: {Encoding.UTF8.GetString(ms.ToArray())}\n\n");
    }

    private byte[] WriteFunctionCallDelta(string itemId, string delta)
    {
        using var ms = new MemoryStream();
        using var writer = new Utf8JsonWriter(ms);
        writer.WriteStartObject();
        writer.WriteString("type", "response.function_call_arguments.delta");
        writer.WriteNumber("sequence_number", NextSeq());
        writer.WriteNumber("output_index", _outputIndex);
        writer.WriteString("item_id", itemId);
        writer.WriteString("delta", delta);
        writer.WriteEndObject();
        writer.Flush();
        return Encoding.UTF8.GetBytes($"event: response.function_call_arguments.delta\ndata: {Encoding.UTF8.GetString(ms.ToArray())}\n\n");
    }

    private byte[] WriteFunctionCallDone(string itemId, string args)
    {
        using var ms = new MemoryStream();
        using var writer = new Utf8JsonWriter(ms);
        writer.WriteStartObject();
        writer.WriteString("type", "response.function_call_arguments.done");
        writer.WriteNumber("sequence_number", NextSeq());
        writer.WriteNumber("output_index", _outputIndex);
        writer.WriteString("item_id", itemId);
        writer.WriteString("arguments", args);
        writer.WriteEndObject();
        writer.Flush();
        return Encoding.UTF8.GetBytes($"event: response.function_call_arguments.done\ndata: {Encoding.UTF8.GetString(ms.ToArray())}\n\n");
    }

    private byte[] WriteCustomToolInputDelta(string itemId, string delta)
    {
        using var ms = new MemoryStream();
        using var writer = new Utf8JsonWriter(ms);
        writer.WriteStartObject();
        writer.WriteString("type", "response.custom_tool_call_input.delta");
        writer.WriteNumber("sequence_number", NextSeq());
        writer.WriteNumber("output_index", _outputIndex);
        writer.WriteString("item_id", itemId);
        writer.WriteString("delta", delta);
        writer.WriteEndObject();
        writer.Flush();
        return Encoding.UTF8.GetBytes($"event: response.custom_tool_call_input.delta\ndata: {Encoding.UTF8.GetString(ms.ToArray())}\n\n");
    }

    private byte[] WriteCustomToolDone(string itemId, string callId, string name, string input)
    {
        using var ms = new MemoryStream();
        using var writer = new Utf8JsonWriter(ms);
        writer.WriteStartObject();
        writer.WriteString("type", "response.custom_tool_call_input.done");
        writer.WriteNumber("sequence_number", NextSeq());
        writer.WriteNumber("output_index", _outputIndex);
        writer.WriteString("item_id", itemId);
        writer.WriteString("input", input);
        writer.WriteEndObject();
        writer.Flush();
        return Encoding.UTF8.GetBytes($"event: response.custom_tool_call_input.done\ndata: {Encoding.UTF8.GetString(ms.ToArray())}\n\n");
    }

    private byte[] WriteOutputItemDoneTool(string itemId, bool isCustom, string callId, string name, string args)
    {
        using var ms = new MemoryStream();
        using var writer = new Utf8JsonWriter(ms);
        writer.WriteStartObject();
        writer.WriteString("type", "response.output_item.done");
        writer.WriteNumber("sequence_number", NextSeq());
        writer.WriteNumber("output_index", _outputIndex);
        writer.WritePropertyName("item");
        writer.WriteStartObject();
        writer.WriteString("type", isCustom ? "custom_tool_call" : "function_call");
        writer.WriteString("id", itemId);
        writer.WriteString("call_id", callId);
        writer.WriteString("name", name);
        if (!isCustom) writer.WriteString("arguments", args);
        else
        {
            string input = ExtractCustomInput(args);
            writer.WriteString("input", input);
        }
        writer.WriteString("status", "completed");
        writer.WriteEndObject();
        writer.WriteEndObject();
        writer.Flush();
        return Encoding.UTF8.GetBytes($"event: response.output_item.done\ndata: {Encoding.UTF8.GetString(ms.ToArray())}\n\n");
    }

    private byte[] WriteCompleted(string finishReason, RouterUsage? usage)
    {
        bool isLength = finishReason == FinishEvent.Length;
        string eventType = isLength ? "response.incomplete" : "response.completed";
        string status = isLength ? "incomplete" : "completed";
        using var ms = new MemoryStream();
        using var writer = new Utf8JsonWriter(ms);
        writer.WriteStartObject();
        writer.WriteString("type", eventType);
        writer.WriteNumber("sequence_number", NextSeq());
        writer.WritePropertyName("response");
        writer.WriteStartObject();
        writer.WriteString("id", _respId);
        writer.WriteString("object", "response");
        writer.WriteNumber("created_at", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        writer.WriteString("status", status);
        writer.WriteString("model", _clientModel);
        writer.WritePropertyName("output");
        writer.WriteStartArray();
        if (!string.IsNullOrEmpty(_finalText))
        {
            writer.WriteStartObject();
            writer.WriteString("type", "message");
            writer.WriteString("id", _msgId);
            writer.WriteString("role", "assistant");
            writer.WriteString("status", "completed");
            writer.WritePropertyName("content");
            writer.WriteStartArray();
            writer.WriteStartObject();
            writer.WriteString("type", "output_text");
            writer.WriteString("text", _finalText);
            writer.WritePropertyName("annotations");
            writer.WriteStartArray();
            writer.WriteEndArray();
            writer.WriteEndObject();
            writer.WriteEndArray();
            writer.WriteEndObject();
        }
        foreach (var tool in _completedTools)
        {
            bool isCustom = _request != null && _request.CustomToolNames.Contains(tool.name);
            writer.WriteStartObject();
            writer.WriteString("type", isCustom ? "custom_tool_call" : "function_call");
            writer.WriteString("id", isCustom ? "ctc_" + RandomHex(6) : "fc_" + RandomHex(6));
            writer.WriteString("call_id", tool.callId);
            writer.WriteString("name", tool.name);
            if (!isCustom) writer.WriteString("arguments", tool.args);
            else
            {
                string input = ExtractCustomInput(tool.args);
                writer.WriteString("input", input);
            }
            writer.WriteString("status", "completed");
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
        if (isLength)
        {
            writer.WritePropertyName("incomplete_details");
            writer.WriteStartObject();
            writer.WriteString("reason", "max_output_tokens");
            writer.WriteEndObject();
        }
        if (usage != null)
        {
            writer.WritePropertyName("usage");
            writer.WriteStartObject();
            if (usage.InputTokens.HasValue) writer.WriteNumber("input_tokens", usage.InputTokens.Value);
            if (usage.CachedInputTokens.HasValue)
            {
                writer.WritePropertyName("input_tokens_details");
                writer.WriteStartObject();
                writer.WriteNumber("cached_tokens", usage.CachedInputTokens.Value);
                writer.WriteEndObject();
            }
            if (usage.OutputTokens.HasValue) writer.WriteNumber("output_tokens", usage.OutputTokens.Value);
            if (usage.ReasoningTokens.HasValue)
            {
                writer.WritePropertyName("output_tokens_details");
                writer.WriteStartObject();
                writer.WriteNumber("reasoning_tokens", usage.ReasoningTokens.Value);
                writer.WriteEndObject();
            }
            if (usage.InputTokens.HasValue && usage.OutputTokens.HasValue)
                writer.WriteNumber("total_tokens", usage.InputTokens.Value + usage.OutputTokens.Value);
            writer.WriteEndObject();
        }
        writer.WriteEndObject();
        writer.WriteEndObject();
        writer.Flush();
        return Encoding.UTF8.GetBytes($"event: {eventType}\ndata: {Encoding.UTF8.GetString(ms.ToArray())}\n\n");
    }
}
