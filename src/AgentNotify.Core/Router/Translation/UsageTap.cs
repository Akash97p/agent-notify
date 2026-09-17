using System.Text.Json;

namespace AgentNotify.Core.Router.Translation;

/// <summary>Taps usage from streamed or buffered responses.</summary>
public sealed class UsageTap
{
    private readonly string _wire;
    private readonly SseReader? _sseReader;
    private RouterUsage? _usage;
    private readonly List<byte> _buffer = new();

    public UsageTap(string wire)
    {
        _wire = wire;
        if (wire != RouterWire.OpenAiChat && wire != RouterWire.AnthropicMessages && wire != RouterWire.OpenAiResponses)
            throw new ArgumentException("Invalid wire");
        if (IsStreamingWire(wire))
            _sseReader = new SseReader();
    }

    private static bool IsStreamingWire(string wire) => true; // all support streaming; but tap may be used for both streaming and non-streaming

    public void Feed(ReadOnlySpan<byte> chunk)
    {
        if (_sseReader != null)
        {
            _buffer.AddRange(chunk.ToArray());
            _sseReader.Feed(chunk);
            TryExtractFromSse();
        }
        else
        {
            _buffer.AddRange(chunk.ToArray());
        }
    }

    private void TryExtractFromSse()
    {
        if (_sseReader == null) return;
        SseEvent ev;
        while (_sseReader.TryRead(out ev))
            ProcessSseEvent(ev);
    }

    private void ProcessSseEvent(SseEvent ev)
    {
        var data = ev.Data;
        if (string.IsNullOrEmpty(data) || data == "[DONE]") return;
        JsonDocument doc;
        try { doc = JsonDocument.Parse(data); } catch { return; }
        using (doc)
        {
            var root = doc.RootElement;
            if (_wire == RouterWire.OpenAiChat)
            {
                if (root.TryGetProperty("usage", out var u) && u.ValueKind == JsonValueKind.Object)
                    _usage = ParseChatUsage(u);
            }
            else if (_wire == RouterWire.AnthropicMessages)
            {
                if (root.TryGetProperty("type", out var t) && t.ValueKind == JsonValueKind.String)
                {
                    var type = t.GetString();
                    if (type == "message_start" && root.TryGetProperty("message", out var msg) && msg.TryGetProperty("usage", out var u2))
                        _usage = ParseAnthropicMessageStartUsage(u2);
                    else if (type == "message_delta" && root.TryGetProperty("usage", out var u3))
                    {
                        var outToks = u3.TryGetProperty("output_tokens", out var ot) && ot.TryGetInt64(out var v) ? v : (long?)null;
                        if (_usage != null)
                            _usage = _usage with { OutputTokens = outToks ?? _usage.OutputTokens };
                        else
                            _usage = new RouterUsage(null, null, outToks, null);
                    }
                }
            }
            else if (_wire == RouterWire.OpenAiResponses)
            {
                JsonElement usageEl = default;
                bool found = false;
                if (root.TryGetProperty("response", out var resp) && resp.TryGetProperty("usage", out usageEl)) found = true;
                else if (root.TryGetProperty("usage", out var u)) { usageEl = u; found = true; }
                if (found && usageEl.ValueKind == JsonValueKind.Object)
                    _usage = ParseResponsesUsage(usageEl);
            }
        }
    }

    public void Complete()
    {
        _sseReader?.Complete();
        SseEvent ev;
        while (_sseReader != null && _sseReader.TryRead(out ev))
            ProcessSseEvent(ev);

        if (_usage == null && _buffer.Count > 0)
        {
            try
            {
                using var doc = JsonDocument.Parse(_buffer.ToArray());
                var root = doc.RootElement;
                if (_wire == RouterWire.OpenAiChat)
                {
                    if (root.TryGetProperty("usage", out var u)) _usage = ParseChatUsage(u);
                }
                else if (_wire == RouterWire.AnthropicMessages)
                {
                    if (root.TryGetProperty("usage", out var u)) _usage = ParseAnthropicResponseUsage(u);
                }
                else if (_wire == RouterWire.OpenAiResponses)
                {
                    if (root.TryGetProperty("usage", out var u)) _usage = ParseResponsesUsage(u);
                    else if (root.TryGetProperty("response", out var r) && r.TryGetProperty("usage", out var u2)) _usage = ParseResponsesUsage(u2);
                }
            }
            catch { }
        }
    }

    public RouterUsage? GetUsage() => _usage;

    private static RouterUsage? ParseChatUsage(JsonElement usage)
    {
        long? prompt = null, completion = null, reasoning = null, cached = null;
        if (usage.TryGetProperty("prompt_tokens", out var pt) && pt.TryGetInt64(out var v)) prompt = v;
        if (usage.TryGetProperty("completion_tokens", out var ct) && ct.TryGetInt64(out var v2)) completion = v2;
        if (usage.TryGetProperty("completion_tokens_details", out var ctd) && ctd.ValueKind == JsonValueKind.Object)
            if (ctd.TryGetProperty("reasoning_tokens", out var rt) && rt.TryGetInt64(out var v3)) reasoning = v3;
        if (usage.TryGetProperty("prompt_tokens_details", out var ptd) && ptd.ValueKind == JsonValueKind.Object)
            if (ptd.TryGetProperty("cached_tokens", out var ca) && ca.TryGetInt64(out var v5)) cached = v5;
        return new RouterUsage(prompt, cached, completion, reasoning);
    }

    private static RouterUsage? ParseResponsesUsage(JsonElement usage)
    {
        long? input = null, cached = null, output = null, reasoning = null;
        if (usage.TryGetProperty("input_tokens", out var it) && it.TryGetInt64(out var v)) input = v;
        if (usage.TryGetProperty("output_tokens", out var ot) && ot.TryGetInt64(out var v2)) output = v2;
        if (usage.TryGetProperty("input_tokens_details", out var itd) && itd.ValueKind == JsonValueKind.Object)
            if (itd.TryGetProperty("cached_tokens", out var ct) && ct.TryGetInt64(out var v3)) cached = v3;
        if (usage.TryGetProperty("output_tokens_details", out var otd) && otd.ValueKind == JsonValueKind.Object)
            if (otd.TryGetProperty("reasoning_tokens", out var rt) && rt.TryGetInt64(out var v4)) reasoning = v4;
        return new RouterUsage(input, cached, output, reasoning);
    }

    private static RouterUsage? ParseAnthropicMessageStartUsage(JsonElement usage)
    {
        long? input = null, cacheRead = null, cacheCreation = null;
        if (usage.TryGetProperty("input_tokens", out var it) && it.TryGetInt64(out var v)) input = v;
        if (usage.TryGetProperty("cache_read_input_tokens", out var cr) && cr.TryGetInt64(out var v2)) cacheRead = v2;
        if (usage.TryGetProperty("cache_creation_input_tokens", out var cc) && cc.TryGetInt64(out var v3)) cacheCreation = v3;
        long? inclusive = null;
        if (input.HasValue) inclusive = input.Value + (cacheRead ?? 0) + (cacheCreation ?? 0);
        return new RouterUsage(inclusive, cacheRead, null, null);
    }

    private static RouterUsage? ParseAnthropicResponseUsage(JsonElement usage)
    {
        long? input = null, cacheRead = null, cacheCreation = null, output = null;
        if (usage.TryGetProperty("input_tokens", out var it) && it.TryGetInt64(out var v)) input = v;
        if (usage.TryGetProperty("cache_read_input_tokens", out var cr) && cr.TryGetInt64(out var v2)) cacheRead = v2;
        if (usage.TryGetProperty("cache_creation_input_tokens", out var cc) && cc.TryGetInt64(out var v3)) cacheCreation = v3;
        if (usage.TryGetProperty("output_tokens", out var ot) && ot.TryGetInt64(out var v4)) output = v4;
        long? inclusive = null;
        if (input.HasValue) inclusive = input.Value + (cacheRead ?? 0) + (cacheCreation ?? 0);
        return new RouterUsage(inclusive, cacheRead, output, null);
    }
}
