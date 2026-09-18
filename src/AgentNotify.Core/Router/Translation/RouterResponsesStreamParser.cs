using System.Text.Json;

namespace AgentNotify.Core.Router.Translation;

/// <summary>Parses Responses SSE into stream events.</summary>
public sealed class RouterResponsesStreamParser
{
    private readonly SseReader _reader = new();
    private readonly List<RouterStreamEvent> _pending = new();

    private readonly Dictionary<string, int> _callIdToIndex = new();
    private readonly Dictionary<int, (string callId, string name)> _indexToCall = new();
    private readonly HashSet<int> _started = new();
    private int _nextIndex = 0;
    private RouterUsage? _pendingUsage;
    private string? _finishReason;


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
        foreach (var idx in _started.ToList())
            _pending.Add(new ToolCallEndEvent(idx));
        _started.Clear();
        var res = _pending.ToList();
        _pending.Clear();
        return res;
    }

    private void Process(SseEvent ev)
    {
        var eventName = ev.Event;
        var data = ev.Data;
        if (string.IsNullOrEmpty(data)) return;
        JsonDocument doc;
        try { doc = JsonDocument.Parse(data); }
        catch { return; }
        using (doc)
        {
            var root = doc.RootElement;

            var type = eventName ?? "";
            if (string.IsNullOrEmpty(type) && root.TryGetProperty("type", out var t) && t.ValueKind == JsonValueKind.String)
                type = t.GetString()!;

            switch (type)
            {
                case "response.output_text.delta":
                {
                    string delta = "";
                    if (root.TryGetProperty("delta", out var d) && d.ValueKind == JsonValueKind.String) delta = d.GetString() ?? "";
                    if (!string.IsNullOrEmpty(delta))
                        _pending.Add(new TextDeltaEvent(delta));
                    break;
                }
                case "response.reasoning_summary_text.delta":
                case "response.reasoning_text.delta":
                {
                    string delta = "";
                    if (root.TryGetProperty("delta", out var d) && d.ValueKind == JsonValueKind.String) delta = d.GetString() ?? "";
                    if (!string.IsNullOrEmpty(delta))
                        _pending.Add(new ReasoningDeltaEvent(delta));
                    break;
                }
                case "response.output_item.added":
                {
                    if (!root.TryGetProperty("item", out var item) || item.ValueKind != JsonValueKind.Object) break;
                    if (!item.TryGetProperty("type", out var itype) || itype.ValueKind != JsonValueKind.String) break;
                    var ity = itype.GetString();
                    if (ity == "function_call")
                    {
                        string callId = "", name = "", itemId = "";
                        if (item.TryGetProperty("call_id", out var cid) && cid.ValueKind == JsonValueKind.String) callId = cid.GetString()!;
                        if (item.TryGetProperty("name", out var nm) && nm.ValueKind == JsonValueKind.String) name = nm.GetString()!;
                        if (item.TryGetProperty("id", out var iid) && iid.ValueKind == JsonValueKind.String) itemId = iid.GetString()!;
                        int idx = _nextIndex++;
                        _callIdToIndex[callId] = idx;
                        if (!string.IsNullOrEmpty(itemId)) _callIdToIndex[itemId] = idx;
                        _indexToCall[idx] = (callId, name);
                        _pending.Add(new ToolCallStartEvent(idx, callId, name));
                        _started.Add(idx);
                    }
                    else if (ity == "custom_tool_call")
                    {
                        string callId = "", name = "", itemId = "";
                        if (item.TryGetProperty("call_id", out var cid) && cid.ValueKind == JsonValueKind.String) callId = cid.GetString()!;
                        if (item.TryGetProperty("name", out var nm) && nm.ValueKind == JsonValueKind.String) name = nm.GetString()!;
                        if (item.TryGetProperty("id", out var iid) && iid.ValueKind == JsonValueKind.String) itemId = iid.GetString()!;
                        int idx = _nextIndex++;
                        _callIdToIndex[callId] = idx;
                        if (!string.IsNullOrEmpty(itemId)) _callIdToIndex[itemId] = idx;
                        _indexToCall[idx] = (callId, name);
                        _pending.Add(new ToolCallStartEvent(idx, callId, name));
                        _started.Add(idx);
                    }
                    break;
                }
                case "response.function_call_arguments.delta":
                {
                    string callId = "";
                    string delta = "";
                    if (root.TryGetProperty("call_id", out var cid) && cid.ValueKind == JsonValueKind.String) callId = cid.GetString()!;
                    else if (root.TryGetProperty("item_id", out var iid) && iid.ValueKind == JsonValueKind.String) callId = iid.GetString()!; // fallback
                    if (root.TryGetProperty("delta", out var d) && d.ValueKind == JsonValueKind.String) delta = d.GetString()!;
                    if (string.IsNullOrEmpty(callId))
                    {
                        if (_started.Count > 0) callId = _indexToCall[_started.Max()].callId;
                    }
                    if (_callIdToIndex.TryGetValue(callId, out var idx))
                    {
                        if (!string.IsNullOrEmpty(delta))
                            _pending.Add(new ToolCallArgumentsDeltaEvent(idx, delta));
                    }
                    break;
                }
                case "response.custom_tool_call_input.delta":
                {
                    string callId = "";
                    string delta = "";
                    if (root.TryGetProperty("call_id", out var cid) && cid.ValueKind == JsonValueKind.String) callId = cid.GetString()!;
                    else if (root.TryGetProperty("item_id", out var iid) && iid.ValueKind == JsonValueKind.String) callId = iid.GetString()!;
                    if (root.TryGetProperty("delta", out var d) && d.ValueKind == JsonValueKind.String) delta = d.GetString()!;
                    if (_callIdToIndex.TryGetValue(callId, out var idx))
                    {
                        if (!string.IsNullOrEmpty(delta))
                        {
                            _pending.Add(new ToolCallArgumentsDeltaEvent(idx, delta));
                        }
                    }
                    break;
                }
                case "response.function_call_arguments.done":
                {
                    string callId = "";
                    string args = "";
                    if (root.TryGetProperty("call_id", out var cid) && cid.ValueKind == JsonValueKind.String) callId = cid.GetString()!;
                    if (root.TryGetProperty("arguments", out var a) && a.ValueKind == JsonValueKind.String) args = a.GetString()!;
                    if (_callIdToIndex.TryGetValue(callId, out var idx))
                    {
                    }
                    break;
                }
                case "response.custom_tool_call_input.done":
                {
                    break;
                }
                case "response.output_item.done":
                {
                    if (root.TryGetProperty("item", out var item2) && item2.ValueKind == JsonValueKind.Object)
                    {
                        if (item2.TryGetProperty("type", out var tp) && tp.ValueKind == JsonValueKind.String)
                        {
                            var typ = tp.GetString();
                            if (typ == "function_call" || typ == "custom_tool_call")
                            {
                                string callId = "";
                                if (item2.TryGetProperty("call_id", out var cid) && cid.ValueKind == JsonValueKind.String) callId = cid.GetString()!;
                                if (_callIdToIndex.TryGetValue(callId, out var idx) && _started.Contains(idx))
                                {
                                    _pending.Add(new ToolCallEndEvent(idx));
                                    _started.Remove(idx);
                                }
                            }
                        }
                    }
                    break;
                }
                case "response.completed":
                case "response.incomplete":
                {
                    if (root.TryGetProperty("response", out var resp) && resp.ValueKind == JsonValueKind.Object)
                    {
                        if (resp.TryGetProperty("usage", out var usage) && usage.ValueKind == JsonValueKind.Object)
                            _pendingUsage = ParseResponsesUsage(usage);
                        else if (root.TryGetProperty("usage", out var usage2) && usage2.ValueKind == JsonValueKind.Object)
                            _pendingUsage = ParseResponsesUsage(usage2);

                        string finish = FinishEvent.Stop;
                        if (type == "response.incomplete")
                        {
                            if (resp.TryGetProperty("incomplete_details", out var inc) && inc.ValueKind == JsonValueKind.Object)
                            {
                                if (inc.TryGetProperty("reason", out var rs) && rs.ValueKind == JsonValueKind.String && rs.GetString() == "max_output_tokens")
                                    finish = FinishEvent.Length;
                                else
                                    finish = FinishEvent.Length;
                            }
                            else finish = FinishEvent.Length;

                            bool hasFunctionCall = false;
                            if (resp.TryGetProperty("output", out var outArr) && outArr.ValueKind == JsonValueKind.Array)
                            {
                                foreach (var outItem in outArr.EnumerateArray())
                                {
                                    if (outItem.ValueKind == JsonValueKind.Object && outItem.TryGetProperty("type", out var ot) && ot.ValueKind == JsonValueKind.String && ot.GetString() == "function_call")
                                    {
                                        hasFunctionCall = true;
                                        break;
                                    }
                                    if (outItem.ValueKind == JsonValueKind.Object && outItem.TryGetProperty("type", out var ot2) && ot2.ValueKind == JsonValueKind.String && ot2.GetString() == "custom_tool_call")
                                        hasFunctionCall = true;
                                }
                            }
                            if (hasFunctionCall) finish = FinishEvent.ToolCalls;
                        }
                        else // completed
                        {
                            bool hasFunctionCall = false;
                            if (resp.TryGetProperty("output", out var outArr) && outArr.ValueKind == JsonValueKind.Array)
                            {
                                foreach (var outItem in outArr.EnumerateArray())
                                {
                                    if (outItem.ValueKind == JsonValueKind.Object && outItem.TryGetProperty("type", out var ot) && ot.ValueKind == JsonValueKind.String && (ot.GetString() == "function_call" || ot.GetString() == "custom_tool_call"))
                                    {
                                        hasFunctionCall = true; break;
                                    }
                                }
                            }
                            finish = hasFunctionCall ? FinishEvent.ToolCalls : FinishEvent.Stop;
                        }
                        _finishReason = finish;
                        foreach (var idx in _started.ToList())
                        {
                            _pending.Add(new ToolCallEndEvent(idx));
                        }
                        _started.Clear();
                        if (_pendingUsage != null) _pending.Add(new UsageEvent(_pendingUsage));
                        _pending.Add(new FinishEvent(finish));
                    }
                    break;
                }
                case "response.failed":
                {
                    if (root.TryGetProperty("response", out var resp) && resp.ValueKind == JsonValueKind.Object)
                    {
                        if (resp.TryGetProperty("usage", out var usage) && usage.ValueKind == JsonValueKind.Object)
                            _pendingUsage = ParseResponsesUsage(usage);
                    }
                    foreach (var idx in _started.ToList())
                    {
                        _pending.Add(new ToolCallEndEvent(idx));
                    }
                    _started.Clear();
                    if (_pendingUsage != null) _pending.Add(new UsageEvent(_pendingUsage));
                    _pending.Add(new FinishEvent(FinishEvent.Error));
                    break;
                }
                case "response.created":
                case "response.in_progress":
                    break;
                default:
                    if (root.TryGetProperty("type", out var typEl) && typEl.ValueKind == JsonValueKind.String)
                    {
                        var tp = typEl.GetString();
                    }
                    break;
            }
        }
    }

    private static RouterUsage ParseResponsesUsage(JsonElement usage)
    {
        long? input = null, cached = null, output = null, reasoning = null;
        if (usage.TryGetProperty("input_tokens", out var it) && it.ValueKind == JsonValueKind.Number && it.TryGetInt64(out var v)) input = v;
        if (usage.TryGetProperty("output_tokens", out var ot) && ot.ValueKind == JsonValueKind.Number && ot.TryGetInt64(out var v2)) output = v2;
        if (usage.TryGetProperty("input_tokens_details", out var itd) && itd.ValueKind == JsonValueKind.Object)
        {
            if (itd.TryGetProperty("cached_tokens", out var ct) && ct.ValueKind == JsonValueKind.Number && ct.TryGetInt64(out var v3)) cached = v3;
        }
        if (usage.TryGetProperty("output_tokens_details", out var otd) && otd.ValueKind == JsonValueKind.Object)
        {
            if (otd.TryGetProperty("reasoning_tokens", out var rt) && rt.ValueKind == JsonValueKind.Number && rt.TryGetInt64(out var v4)) reasoning = v4;
        }
        return new RouterUsage(input, cached, output, reasoning);
    }
}
