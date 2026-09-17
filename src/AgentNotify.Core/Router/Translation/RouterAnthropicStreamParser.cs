using System.Text.Json;

namespace AgentNotify.Core.Router.Translation;

/// <summary>Parses Anthropic SSE into stream events.</summary>
public sealed class RouterAnthropicStreamParser
{
    private readonly SseReader _reader = new();
    private readonly List<RouterStreamEvent> _pending = new();

    private readonly Dictionary<int, (string id, string name)> _toolCalls = new();
    private readonly HashSet<int> _toolStarted = new();
    private int _nextToolIndex = 0;
    private readonly Dictionary<int, int> _blockIndexToToolIndex = new(); // map content_block index to tool index
    private long? _inputTokens;
    private long? _cachedInput;
    private long? _outputTokens;
    private string? _stopReason;

    public IReadOnlyList<RouterStreamEvent> Feed(ReadOnlySpan<byte> chunk)
    {
        _pending.Clear();
        _reader.Feed(chunk);
        SseEvent ev;
        while (_reader.TryRead(out ev))
            Process(ev);
        var res = _pending.ToList();
        _pending.Clear();
        return res;
    }

    public IReadOnlyList<RouterStreamEvent> Complete()
    {
        _reader.Complete();
        SseEvent ev;
        while (_reader.TryRead(out ev)) Process(ev);
        foreach (var idx in _toolStarted.ToList())
            _pending.Add(new ToolCallEndEvent(idx));
        _toolStarted.Clear();
        var res = _pending.ToList();
        _pending.Clear();
        return res;
    }

    private void Process(SseEvent ev)
    {
        var eventType = ev.Event;
        var data = ev.Data;
        if (string.IsNullOrEmpty(data)) return;

        if (eventType == "error")
        {
            _pending.Add(new FinishEvent(FinishEvent.Error));
            return;
        }

        JsonDocument doc;
        try { doc = JsonDocument.Parse(data); }
        catch { return; }
        using (doc)
        {
            var root = doc.RootElement;
            var type = "";
            if (root.TryGetProperty("type", out var t) && t.ValueKind == JsonValueKind.String) type = t.GetString()!;

            switch (type)
            {
                case "message_start":
                {
                    if (root.TryGetProperty("message", out var msg) && msg.ValueKind == JsonValueKind.Object)
                    {
                        if (msg.TryGetProperty("usage", out var usage) && usage.ValueKind == JsonValueKind.Object)
                        {
                            ParseAnthropicUsageMessageStart(usage);
                        }
                    }
                    break;
                }
                case "content_block_start":
                {
                    if (!root.TryGetProperty("index", out var idxEl) || idxEl.ValueKind != JsonValueKind.Number || !idxEl.TryGetInt32(out var idx))
                        break;
                    if (!root.TryGetProperty("content_block", out var block) || block.ValueKind != JsonValueKind.Object)
                        break;
                    if (!block.TryGetProperty("type", out var btype) || btype.ValueKind != JsonValueKind.String)
                        break;
                    var bt = btype.GetString();
                    if (bt == "text")
                    {
                    }
                    else if (bt == "tool_use")
                    {
                        string id = "", name = "";
                        if (block.TryGetProperty("id", out var idEl) && idEl.ValueKind == JsonValueKind.String) id = idEl.GetString()!;
                        if (block.TryGetProperty("name", out var nm) && nm.ValueKind == JsonValueKind.String) name = nm.GetString()!;
                        int toolIdx = _nextToolIndex++;
                        _blockIndexToToolIndex[idx] = toolIdx;
                        _toolCalls[toolIdx] = (id, name);
                        _pending.Add(new ToolCallStartEvent(toolIdx, id, name));
                        _toolStarted.Add(toolIdx);
                    }
                    else if (bt == "thinking")
                    {
                    }
                    break;
                }
                case "content_block_delta":
                {
                    if (!root.TryGetProperty("index", out var idxEl) || !idxEl.TryGetInt32(out var idx))
                        break;
                    if (!root.TryGetProperty("delta", out var delta) || delta.ValueKind != JsonValueKind.Object)
                        break;
                    if (!delta.TryGetProperty("type", out var dtype) || dtype.ValueKind != JsonValueKind.String)
                        break;
                    var dt = dtype.GetString();
                    if (dt == "text_delta")
                    {
                        if (delta.TryGetProperty("text", out var te) && te.ValueKind == JsonValueKind.String)
                        {
                            var txt = te.GetString() ?? "";
                            if (!string.IsNullOrEmpty(txt)) _pending.Add(new TextDeltaEvent(txt));
                        }
                    }
                    else if (dt == "input_json_delta")
                    {
                        if (delta.TryGetProperty("partial_json", out var pj) && pj.ValueKind == JsonValueKind.String)
                        {
                            var json = pj.GetString() ?? "";
                            if (_blockIndexToToolIndex.TryGetValue(idx, out var toolIdx))
                                _pending.Add(new ToolCallArgumentsDeltaEvent(toolIdx, json));
                        }
                    }
                    else if (dt == "thinking_delta")
                    {
                    }
                    break;
                }
                case "content_block_stop":
                {
                    if (!root.TryGetProperty("index", out var idxEl) || !idxEl.TryGetInt32(out var idx))
                        break;
                    if (_blockIndexToToolIndex.TryGetValue(idx, out var toolIdx))
                    {
                        if (_toolStarted.Contains(toolIdx))
                        {
                            _pending.Add(new ToolCallEndEvent(toolIdx));
                            _toolStarted.Remove(toolIdx);
                        }
                    }
                    break;
                }
                case "message_delta":
                {
                    if (root.TryGetProperty("delta", out var delta) && delta.ValueKind == JsonValueKind.Object)
                    {
                        if (delta.TryGetProperty("stop_reason", out var sr) && sr.ValueKind == JsonValueKind.String)
                            _stopReason = sr.GetString();
                    }
                    if (root.TryGetProperty("usage", out var usage) && usage.ValueKind == JsonValueKind.Object)
                    {
                        if (usage.TryGetProperty("output_tokens", out var ot) && ot.ValueKind == JsonValueKind.Number && ot.TryGetInt64(out var v))
                            _outputTokens = v;
                    }
                    break;
                }
                case "message_stop":
                {
                    // Need to compute RouterUsage inclusive? For Anthropic, input exclusive + cache_read.
                    // We have _inputTokens and _cachedInput stored; need to combine to inclusive.
                    var usage = BuildUsage();
                    if (usage != null) _pending.Add(new UsageEvent(usage));
                    var finish = MapAnthropicStop(_stopReason);
                    _pending.Add(new FinishEvent(finish));
                    break;
                }
            }
        }
    }

    private void ParseAnthropicUsageMessageStart(JsonElement usage)
    {
        long? input = null, cacheRead = null, cacheCreation = null;
        if (usage.TryGetProperty("input_tokens", out var it) && it.ValueKind == JsonValueKind.Number && it.TryGetInt64(out var v)) input = v;
        if (usage.TryGetProperty("cache_read_input_tokens", out var cr) && cr.ValueKind == JsonValueKind.Number && cr.TryGetInt64(out var v2)) cacheRead = v2;
        if (usage.TryGetProperty("cache_creation_input_tokens", out var cc) && cc.ValueKind == JsonValueKind.Number && cc.TryGetInt64(out var v3)) cacheCreation = v3;
        // Convert to inclusive RouterUsage: Input = input + cache_read + cache_creation, Cached = cache_read
        long? inclusive = null;
        if (input.HasValue)
        {
            inclusive = input.Value + (cacheRead ?? 0) + (cacheCreation ?? 0);
        }
        _inputTokens = inclusive;
        _cachedInput = cacheRead;
    }

    private RouterUsage? BuildUsage()
    {
        return new RouterUsage(_inputTokens, _cachedInput, _outputTokens, null);
    }

    private static string MapAnthropicStop(string? reason)
    {
        return reason switch
        {
            "end_turn" => FinishEvent.Stop,
            "stop_sequence" => FinishEvent.Stop,
            "max_tokens" => FinishEvent.Length,
            "tool_use" => FinishEvent.ToolCalls,
            null => FinishEvent.Stop,
            _ => FinishEvent.Stop
        };
    }
}
