using System.Text;
using System.Text.Json;

namespace AgentNotify.Core.Router.Translation;

/// <summary>Parses Chat Completions SSE into stream events.</summary>
public sealed class RouterChatStreamParser
{
    private readonly SseReader _reader = new();
    private readonly List<RouterStreamEvent> _pending = new();
    private readonly Dictionary<int, (string id, string name)> _toolCalls = new();
    private readonly HashSet<int> _toolCallStarted = new();
    private bool _doneSeen;

    public IReadOnlyList<RouterStreamEvent> Feed(ReadOnlySpan<byte> chunk)
    {
        _pending.Clear();
        if (_doneSeen) return _pending;
        _reader.Feed(chunk);
        SseEvent ev;
        while (_reader.TryRead(out ev))
        {
            ProcessSseEvent(ev);
            if (_doneSeen) break;
        }
        var result = _pending.ToList();
        _pending.Clear();
        return result;
    }

    public IReadOnlyList<RouterStreamEvent> Complete()
    {
        _reader.Complete();
        SseEvent ev;
        while (_reader.TryRead(out ev))
            ProcessSseEvent(ev);
        foreach (var idx in _toolCallStarted.ToList())
        {
            _pending.Add(new ToolCallEndEvent(idx));
        }
        _toolCallStarted.Clear();
        var res = _pending.ToList();
        _pending.Clear();
        return res;
    }

    private void ProcessSseEvent(SseEvent ev)
    {
        var data = ev.Data;
        if (string.IsNullOrEmpty(data)) return;
        if (data == "[DONE]")
        {
            _doneSeen = true;
            foreach (var idx in _toolCallStarted.ToList())
            {
                _pending.Add(new ToolCallEndEvent(idx));
            }
            _toolCallStarted.Clear();
            return;
        }

        JsonDocument doc;
        try { doc = JsonDocument.Parse(data); }
        catch { return; }

        using (doc)
        {
            var root = doc.RootElement;

            if (root.TryGetProperty("error", out var err) && err.ValueKind != JsonValueKind.Null)
            {
                _pending.Add(new FinishEvent(FinishEvent.Error));
                return;
            }

            if (root.TryGetProperty("choices", out var choices) && choices.ValueKind == JsonValueKind.Array)
            {
                foreach (var choice in choices.EnumerateArray())
                {
                    if (choice.TryGetProperty("delta", out var delta) && delta.ValueKind == JsonValueKind.Object)
                    {
                        if (delta.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.String)
                        {
                            var txt = content.GetString() ?? "";
                            if (!string.IsNullOrEmpty(txt))
                                _pending.Add(new TextDeltaEvent(txt));
                        }
                        if (delta.TryGetProperty("reasoning_content", out var rc) && rc.ValueKind == JsonValueKind.String)
                        {
                            var r = rc.GetString() ?? "";
                            if (!string.IsNullOrEmpty(r)) _pending.Add(new ReasoningDeltaEvent(r));
                        }
                        else if (delta.TryGetProperty("reasoning", out var r2) && r2.ValueKind == JsonValueKind.String)
                        {
                            var r = r2.GetString() ?? "";
                            if (!string.IsNullOrEmpty(r)) _pending.Add(new ReasoningDeltaEvent(r));
                        }

                        if (delta.TryGetProperty("tool_calls", out var tcs) && tcs.ValueKind == JsonValueKind.Array)
                        {
                            foreach (var tc in tcs.EnumerateArray())
                            {
                                if (tc.ValueKind != JsonValueKind.Object) continue;
                                if (!tc.TryGetProperty("index", out var idxEl) || idxEl.ValueKind != JsonValueKind.Number || !idxEl.TryGetInt32(out var idx))
                                    continue;
                                string? id = null;
                                string? name = null;
                                string? argsDelta = null;
                                if (tc.TryGetProperty("id", out var idEl) && idEl.ValueKind == JsonValueKind.String)
                                    id = idEl.GetString();
                                if (tc.TryGetProperty("function", out var fn) && fn.ValueKind == JsonValueKind.Object)
                                {
                                    if (fn.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String) name = n.GetString();
                                    if (fn.TryGetProperty("arguments", out var a) && a.ValueKind == JsonValueKind.String) argsDelta = a.GetString();
                                }
                                else
                                {
                                    if (tc.TryGetProperty("name", out var n2) && n2.ValueKind == JsonValueKind.String) name = n2.GetString();
                                    if (tc.TryGetProperty("arguments", out var a2) && a2.ValueKind == JsonValueKind.String) argsDelta = a2.GetString();
                                }

                                if (!_toolCallStarted.Contains(idx))
                                {
                                    string emitId;
                                    string emitName;
                                    if (_toolCalls.TryGetValue(idx, out var prev))
                                    {
                                        emitId = id ?? prev.Item1;
                                        emitName = name ?? prev.Item2;
                                    }
                                    else
                                    {
                                        emitId = id ?? $"call_{idx}";
                                        emitName = name ?? "";
                                    }
                                    if (id != null || name != null)
                                    {
                                        var existing = _toolCalls.TryGetValue(idx, out var cur) ? cur : (id ?? "", name ?? "");
                                        var newId = id ?? existing.Item1;
                                        var newName = name ?? existing.Item2;
                                        _toolCalls[idx] = (newId, newName);
                                        emitId = newId;
                                        emitName = newName;
                                    }
                                    _pending.Add(new ToolCallStartEvent(idx, emitId, emitName));
                                    _toolCallStarted.Add(idx);
                                }
                                else
                                {
                                    if (id != null || name != null)
                                    {
                                        var cur = _toolCalls[idx];
                                        var newId = id ?? cur.Item1;
                                        var newName = name ?? cur.Item2;
                                        _toolCalls[idx] = (newId, newName);
                                    }
                                }

                                if (!string.IsNullOrEmpty(argsDelta))
                                    _pending.Add(new ToolCallArgumentsDeltaEvent(idx, argsDelta));
                            }
                        }
                    }

                    if (choice.TryGetProperty("finish_reason", out var fr) && fr.ValueKind != JsonValueKind.Null && fr.ValueKind == JsonValueKind.String)
                    {
                        var reason = MapFinishReason(fr.GetString()!);
                        foreach (var idx in _toolCallStarted.ToList())
                        {
                            _pending.Add(new ToolCallEndEvent(idx));
                        }
                        _toolCallStarted.Clear();
                        _pending.Add(new FinishEvent(reason));
                    }
                }
            }

            if (root.TryGetProperty("usage", out var usage) && usage.ValueKind == JsonValueKind.Object)
            {
                var ru = ParseChatUsage(usage);
                if (ru != null) _pending.Add(new UsageEvent(ru));
            }
        }
    }

    private static string MapFinishReason(string raw)
    {
        return raw switch
        {
            "stop" => FinishEvent.Stop,
            "length" => FinishEvent.Length,
            "tool_calls" => FinishEvent.ToolCalls,
            "content_filter" => FinishEvent.ContentFilter,
            _ => FinishEvent.Stop
        };
    }

    private static RouterUsage? ParseChatUsage(JsonElement usage)
    {
        long? prompt = null, completion = null, reasoning = null, cached = null;
        if (usage.TryGetProperty("prompt_tokens", out var pt) && pt.ValueKind == JsonValueKind.Number && pt.TryGetInt64(out var v)) prompt = v;
        if (usage.TryGetProperty("completion_tokens", out var ct) && ct.ValueKind == JsonValueKind.Number && ct.TryGetInt64(out var v2)) completion = v2;
        if (usage.TryGetProperty("completion_tokens_details", out var ctd) && ctd.ValueKind == JsonValueKind.Object)
        {
            if (ctd.TryGetProperty("reasoning_tokens", out var rt) && rt.ValueKind == JsonValueKind.Number && rt.TryGetInt64(out var v3)) reasoning = v3;
        }
        if (usage.TryGetProperty("output_tokens_details", out var otd) && otd.ValueKind == JsonValueKind.Object)
        {
            if (otd.TryGetProperty("reasoning_tokens", out var rt2) && rt2.ValueKind == JsonValueKind.Number && rt2.TryGetInt64(out var v4)) reasoning = v4;
        }
        if (usage.TryGetProperty("prompt_tokens_details", out var ptd) && ptd.ValueKind == JsonValueKind.Object)
        {
            if (ptd.TryGetProperty("cached_tokens", out var ca) && ca.ValueKind == JsonValueKind.Number && ca.TryGetInt64(out var v5)) cached = v5;
        }
        // input_tokens etc for responses wire? but chat parser only for chat upstream? still handle inclusive?
        // For chat, InputTokens inclusive of cached, so we use prompt as inclusive.
        return new RouterUsage(prompt, cached, completion, reasoning);
    }
}
