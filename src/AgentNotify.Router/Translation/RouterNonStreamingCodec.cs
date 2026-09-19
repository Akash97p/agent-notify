using System.Text;
using System.Text.Json;

namespace AgentNotify.Router.Translation;

internal static class RouterNonStreamingCodec
{
    public static RouterResponse ParseResponses(ReadOnlySpan<byte> body)
    {
        JsonDocument doc;
        try { doc = JsonDocument.Parse(body.ToArray()); } catch (JsonException ex) { throw new TranslationException("invalid_request", "Invalid JSON", ex); }
        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) throw new TranslationException("invalid_request", "Invalid response.");
            JsonElement resp = root;
            string text = "";
            string reasoning = "";
            var toolCalls = new List<RouterToolCallPart>();
            string? finish = null;
            RouterUsage? usage = null;

            if (resp.TryGetProperty("output", out var outputEl) && outputEl.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in outputEl.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.Object) continue;
                    if (!item.TryGetProperty("type", out var ty) || ty.ValueKind != JsonValueKind.String) continue;
                    var t = ty.GetString();
                    if (t == "message")
                    {
                        if (item.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array)
                        {
                            foreach (var c in content.EnumerateArray())
                            {
                                if (c.TryGetProperty("type", out var ct) && ct.ValueKind == JsonValueKind.String && ct.GetString() == "output_text" && c.TryGetProperty("text", out var te) && te.ValueKind == JsonValueKind.String)
                                    text += te.GetString();
                            }
                        }
                    }
                    else if (t == "function_call")
                    {
                        string callId = "", name = "", args = "";
                        if (item.TryGetProperty("call_id", out var cid) && cid.ValueKind == JsonValueKind.String) callId = cid.GetString()!;
                        if (item.TryGetProperty("name", out var nm) && nm.ValueKind == JsonValueKind.String) name = nm.GetString()!;
                        if (item.TryGetProperty("arguments", out var a) && a.ValueKind == JsonValueKind.String) args = a.GetString()!;
                        else if (item.TryGetProperty("arguments", out var a2)) args = a2.GetRawText();
                        toolCalls.Add(new RouterToolCallPart(callId, name, args));
                    }
                    else if (t == "custom_tool_call")
                    {
                        string callId = "", name = "", input = "";
                        if (item.TryGetProperty("call_id", out var cid) && cid.ValueKind == JsonValueKind.String) callId = cid.GetString()!;
                        if (item.TryGetProperty("name", out var nm) && nm.ValueKind == JsonValueKind.String) name = nm.GetString()!;
                        if (item.TryGetProperty("input", out var inp) && inp.ValueKind == JsonValueKind.String) input = inp.GetString()!;
                        var argsJson = "{\"input\":" + JsonSerializer.Serialize(input) + "}";
                        toolCalls.Add(new RouterToolCallPart(callId, name, argsJson));
                    }
                    else if (t == "reasoning")
                    {
                        if (item.TryGetProperty("summary", out var summ) && summ.ValueKind == JsonValueKind.Array)
                        {
                            foreach (var s in summ.EnumerateArray())
                            {
                                if (s.TryGetProperty("type", out var st) && st.ValueKind == JsonValueKind.String && st.GetString() == "summary_text" && s.TryGetProperty("text", out var te) && te.ValueKind == JsonValueKind.String)
                                    reasoning += te.GetString();
                            }
                        }
                    }
                }
            }

            if (resp.TryGetProperty("status", out var statusEl) && statusEl.ValueKind == JsonValueKind.String)
            {
                var status = statusEl.GetString();
                if (status == "completed") finish = toolCalls.Count > 0 ? FinishEvent.ToolCalls : FinishEvent.Stop;
                else if (status == "incomplete")
                {
                    if (resp.TryGetProperty("incomplete_details", out var inc) && inc.TryGetProperty("reason", out var rs) && rs.ValueKind == JsonValueKind.String && rs.GetString() == "max_output_tokens")
                        finish = FinishEvent.Length;
                    else
                        finish = toolCalls.Count > 0 ? FinishEvent.ToolCalls : FinishEvent.Length;
                }
                else if (status == "failed") finish = FinishEvent.Error;
            }

            if (resp.TryGetProperty("usage", out var usageEl) && usageEl.ValueKind == JsonValueKind.Object)
                usage = ParseResponsesUsage(usageEl);

            return new RouterResponse(text, reasoning, toolCalls, finish, usage);
        }
    }

    public static byte[] WriteResponses(RouterResponse response, string clientModel, RouterRequest? request)
    {
        using var ms = new MemoryStream();
        using var writer = new Utf8JsonWriter(ms);
        writer.WriteStartObject();
        writer.WriteString("id", "resp_" + RandomHex(12));
        writer.WriteString("object", "response");
        writer.WriteNumber("created_at", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        writer.WriteString("model", clientModel);
        string status = response.FinishReason == FinishEvent.Length ? "incomplete" : response.FinishReason == FinishEvent.Error ? "failed" : "completed";
        writer.WriteString("status", status);
        if (status == "incomplete")
        {
            writer.WritePropertyName("incomplete_details");
            writer.WriteStartObject();
            writer.WriteString("reason", "max_output_tokens");
            writer.WriteEndObject();
        }
        writer.WritePropertyName("output");
        writer.WriteStartArray();
        if (!string.IsNullOrEmpty(response.Text))
        {
            writer.WriteStartObject();
            writer.WriteString("type", "message");
            writer.WriteString("id", "msg_" + RandomHex(8));
            writer.WriteString("role", "assistant");
            writer.WriteString("status", "completed");
            writer.WritePropertyName("content");
            writer.WriteStartArray();
            writer.WriteStartObject();
            writer.WriteString("type", "output_text");
            writer.WriteString("text", response.Text);
            writer.WritePropertyName("annotations");
            writer.WriteStartArray();
            writer.WriteEndArray();
            writer.WriteEndObject();
            writer.WriteEndArray();
            writer.WriteEndObject();
        }
        foreach (var call in response.ToolCalls)
        {
            bool isCustom = request != null && request.CustomToolNames.Contains(call.Name);
            writer.WriteStartObject();
            writer.WriteString("type", isCustom ? "custom_tool_call" : "function_call");
            writer.WriteString("id", isCustom ? "ctc_" + RandomHex(8) : "fc_" + RandomHex(8));
            writer.WriteString("call_id", call.Id);
            writer.WriteString("name", call.Name);
            if (!isCustom) writer.WriteString("arguments", call.ArgumentsJson);
            else
            {
                string input = ExtractCustomInput(call.ArgumentsJson);
                writer.WriteString("input", input);
            }
            writer.WriteString("status", "completed");
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
        if (response.Usage != null)
        {
            writer.WritePropertyName("usage");
            writer.WriteStartObject();
            if (response.Usage.InputTokens.HasValue) writer.WriteNumber("input_tokens", response.Usage.InputTokens.Value);
            if (response.Usage.CachedInputTokens.HasValue)
            {
                writer.WritePropertyName("input_tokens_details");
                writer.WriteStartObject();
                writer.WriteNumber("cached_tokens", response.Usage.CachedInputTokens.Value);
                writer.WriteEndObject();
            }
            if (response.Usage.OutputTokens.HasValue) writer.WriteNumber("output_tokens", response.Usage.OutputTokens.Value);
            if (response.Usage.ReasoningTokens.HasValue)
            {
                writer.WritePropertyName("output_tokens_details");
                writer.WriteStartObject();
                writer.WriteNumber("reasoning_tokens", response.Usage.ReasoningTokens.Value);
                writer.WriteEndObject();
            }
            if (response.Usage.InputTokens.HasValue && response.Usage.OutputTokens.HasValue)
                writer.WriteNumber("total_tokens", response.Usage.InputTokens.Value + response.Usage.OutputTokens.Value);
            writer.WriteEndObject();
        }
        writer.WriteEndObject();
        writer.Flush();
        return ms.ToArray();
    }

    public static RouterResponse ParseChat(ReadOnlySpan<byte> body)
    {
        JsonDocument doc;
        try { doc = JsonDocument.Parse(body.ToArray()); } catch (JsonException ex) { throw new TranslationException("invalid_request", "Invalid JSON", ex); }
        using (doc)
        {
            var root = doc.RootElement;
            string text = "";
            string reasoning = "";
            var toolCalls = new List<RouterToolCallPart>();
            string? finish = null;
            RouterUsage? usage = null;
            if (root.TryGetProperty("choices", out var choices) && choices.ValueKind == JsonValueKind.Array)
            {
                foreach (var choice in choices.EnumerateArray())
                {
                    if (choice.TryGetProperty("message", out var msg) && msg.ValueKind == JsonValueKind.Object)
                    {
                        if (msg.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.String)
                            text += content.GetString();
                        else if (msg.TryGetProperty("content", out var content2) && content2.ValueKind == JsonValueKind.Array)
                        {
                            foreach (var c in content2.EnumerateArray())
                                if (c.TryGetProperty("text", out var te)) text += te.GetString();
                        }
                        if (msg.TryGetProperty("reasoning_content", out var rc) && rc.ValueKind == JsonValueKind.String)
                            reasoning += rc.GetString();
                        else if (msg.TryGetProperty("reasoning", out var r2) && r2.ValueKind == JsonValueKind.String)
                            reasoning += r2.GetString();
                        if (msg.TryGetProperty("tool_calls", out var tcs) && tcs.ValueKind == JsonValueKind.Array)
                        {
                            foreach (var tc in tcs.EnumerateArray())
                            {
                                string id = "", name = "", args = "";
                                if (tc.TryGetProperty("id", out var idEl) && idEl.ValueKind == JsonValueKind.String) id = idEl.GetString()!;
                                if (tc.TryGetProperty("function", out var fn) && fn.ValueKind == JsonValueKind.Object)
                                {
                                    if (fn.TryGetProperty("name", out var nm) && nm.ValueKind == JsonValueKind.String) name = nm.GetString()!;
                                    if (fn.TryGetProperty("arguments", out var a) && a.ValueKind == JsonValueKind.String) args = a.GetString()!;
                                    else if (fn.TryGetProperty("arguments", out var a2)) args = a2.GetRawText();
                                }
                                toolCalls.Add(new RouterToolCallPart(id, name, args));
                            }
                        }
                    }
                    if (choice.TryGetProperty("finish_reason", out var fr) && fr.ValueKind == JsonValueKind.String)
                        finish = MapChatFinish(fr.GetString()!);
                }
            }
            if (root.TryGetProperty("usage", out var usageEl) && usageEl.ValueKind == JsonValueKind.Object)
                usage = ParseChatUsage(usageEl);
            return new RouterResponse(text, reasoning, toolCalls, finish, usage);
        }
    }

    public static byte[] WriteChat(RouterResponse response, string clientModel, RouterRequest? request)
    {
        using var ms = new MemoryStream();
        using var writer = new Utf8JsonWriter(ms);
        writer.WriteStartObject();
        writer.WriteString("id", "chatcmpl-" + RandomHex(12));
        writer.WriteString("object", "chat.completion");
        writer.WriteNumber("created", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        writer.WriteString("model", clientModel);
        writer.WritePropertyName("choices");
        writer.WriteStartArray();
        writer.WriteStartObject();
        writer.WriteNumber("index", 0);
        writer.WritePropertyName("message");
        writer.WriteStartObject();
        writer.WriteString("role", "assistant");
        if (!string.IsNullOrEmpty(response.Text))
            writer.WriteString("content", response.Text);
        else
            writer.WriteNull("content");
        if (!string.IsNullOrEmpty(response.ReasoningText))
            writer.WriteString("reasoning_content", response.ReasoningText);
        if (response.ToolCalls.Count > 0)
        {
            writer.WritePropertyName("tool_calls");
            writer.WriteStartArray();
            foreach (var call in response.ToolCalls)
            {
                writer.WriteStartObject();
                writer.WriteString("id", call.Id);
                writer.WriteString("type", "function");
                writer.WritePropertyName("function");
                writer.WriteStartObject();
                writer.WriteString("name", call.Name);
                writer.WriteString("arguments", call.ArgumentsJson);
                writer.WriteEndObject();
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
        }
        writer.WriteEndObject();
        string finish = MapFinishToChat(response.FinishReason ?? FinishEvent.Stop);
        writer.WriteString("finish_reason", finish);
        writer.WriteEndObject();
        writer.WriteEndArray();
        if (response.Usage != null)
        {
            writer.WritePropertyName("usage");
            writer.WriteStartObject();
            if (response.Usage.InputTokens.HasValue) writer.WriteNumber("prompt_tokens", response.Usage.InputTokens.Value);
            if (response.Usage.OutputTokens.HasValue) writer.WriteNumber("completion_tokens", response.Usage.OutputTokens.Value);
            if (response.Usage.InputTokens.HasValue && response.Usage.OutputTokens.HasValue)
                writer.WriteNumber("total_tokens", response.Usage.InputTokens.Value + response.Usage.OutputTokens.Value);
            if (response.Usage.CachedInputTokens.HasValue)
            {
                writer.WritePropertyName("prompt_tokens_details");
                writer.WriteStartObject();
                writer.WriteNumber("cached_tokens", response.Usage.CachedInputTokens.Value);
                writer.WriteEndObject();
            }
            if (response.Usage.ReasoningTokens.HasValue)
            {
                writer.WritePropertyName("completion_tokens_details");
                writer.WriteStartObject();
                writer.WriteNumber("reasoning_tokens", response.Usage.ReasoningTokens.Value);
                writer.WriteEndObject();
            }
            writer.WriteEndObject();
        }
        writer.WriteEndObject();
        writer.Flush();
        return ms.ToArray();
    }

    public static RouterResponse ParseAnthropic(ReadOnlySpan<byte> body)
    {
        JsonDocument doc;
        try { doc = JsonDocument.Parse(body.ToArray()); } catch (JsonException ex) { throw new TranslationException("invalid_request", "Invalid JSON", ex); }
        using (doc)
        {
            var root = doc.RootElement;
            string text = "";
            string reasoning = "";
            var toolCalls = new List<RouterToolCallPart>();
            string? finish = null;
            RouterUsage? usage = null;
            if (root.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array)
            {
                foreach (var block in content.EnumerateArray())
                {
                    if (block.TryGetProperty("type", out var ty) && ty.ValueKind == JsonValueKind.String)
                    {
                        var t = ty.GetString();
                        if (t == "text" && block.TryGetProperty("text", out var te) && te.ValueKind == JsonValueKind.String)
                            text += te.GetString();
                        else if (t == "tool_use")
                        {
                            string id = "", name = "", inputJson = "{}";
                            if (block.TryGetProperty("id", out var idEl) && idEl.ValueKind == JsonValueKind.String) id = idEl.GetString()!;
                            if (block.TryGetProperty("name", out var nm) && nm.ValueKind == JsonValueKind.String) name = nm.GetString()!;
                            if (block.TryGetProperty("input", out var inp)) inputJson = inp.GetRawText();
                            toolCalls.Add(new RouterToolCallPart(id, name, inputJson));
                        }
                        else if (t == "thinking" && block.TryGetProperty("thinking", out var th) && th.ValueKind == JsonValueKind.String)
                            reasoning += th.GetString();
                    }
                }
            }
            if (root.TryGetProperty("stop_reason", out var sr) && sr.ValueKind == JsonValueKind.String)
                finish = MapAnthropicStop(sr.GetString()!);
            if (root.TryGetProperty("usage", out var usageEl) && usageEl.ValueKind == JsonValueKind.Object)
                usage = ParseAnthropicUsage(usageEl);

            return new RouterResponse(text, reasoning, toolCalls, finish, usage);
        }
    }

    public static byte[] WriteAnthropic(RouterResponse response, string clientModel, RouterRequest? request)
    {
        using var ms = new MemoryStream();
        using var writer = new Utf8JsonWriter(ms);
        writer.WriteStartObject();
        writer.WriteString("id", "msg_" + RandomHex(12));
        writer.WriteString("type", "message");
        writer.WriteString("role", "assistant");
        writer.WriteString("model", clientModel);
        writer.WritePropertyName("content");
        writer.WriteStartArray();
        if (!string.IsNullOrEmpty(response.Text))
        {
            writer.WriteStartObject();
            writer.WriteString("type", "text");
            writer.WriteString("text", response.Text);
            writer.WriteEndObject();
        }
        foreach (var call in response.ToolCalls)
        {
            writer.WriteStartObject();
            writer.WriteString("type", "tool_use");
            writer.WriteString("id", call.Id.StartsWith("toolu_") ? call.Id : "toolu_" + RandomHex(8));
            writer.WriteString("name", call.Name);
            writer.WritePropertyName("input");
            try
            {
                using var doc = JsonDocument.Parse(call.ArgumentsJson);
                doc.RootElement.WriteTo(writer);
            }
            catch
            {
                writer.WriteStartObject();
                writer.WriteString("input", call.ArgumentsJson);
                writer.WriteEndObject();
            }
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
        writer.WriteString("stop_reason", MapFinishToAnthropic(response.FinishReason ?? FinishEvent.Stop));
        writer.WriteNull("stop_sequence");
        if (response.Usage != null)
        {
            writer.WritePropertyName("usage");
            writer.WriteStartObject();
            long inputExclusive = response.Usage.InputTokens ?? 0;
            if (response.Usage.CachedInputTokens.HasValue)
                inputExclusive -= response.Usage.CachedInputTokens.Value;
            if (inputExclusive < 0) inputExclusive = 0;
            writer.WriteNumber("input_tokens", (int)inputExclusive);
            if (response.Usage.CachedInputTokens.HasValue)
                writer.WriteNumber("cache_read_input_tokens", (int)response.Usage.CachedInputTokens.Value);
            if (response.Usage.OutputTokens.HasValue)
                writer.WriteNumber("output_tokens", (int)response.Usage.OutputTokens.Value);
            writer.WriteEndObject();
        }
        else
        {
            writer.WritePropertyName("usage");
            writer.WriteStartObject();
            writer.WriteNumber("input_tokens", 0);
            writer.WriteNumber("output_tokens", 0);
            writer.WriteEndObject();
        }
        writer.WriteEndObject();
        writer.Flush();
        return ms.ToArray();
    }

    public static byte[] WriteErrorResponses(int status, string code, string message)
    {
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
        return ms.ToArray();
    }

    public static byte[] WriteErrorChat(int status, string code, string message)
    {
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
        return ms.ToArray();
    }

    public static byte[] WriteErrorAnthropic(int status, string code, string message)
    {
        using var ms = new MemoryStream();
        using var writer = new Utf8JsonWriter(ms);
        writer.WriteStartObject();
        writer.WriteString("type", "error");
        writer.WritePropertyName("error");
        writer.WriteStartObject();
        writer.WriteString("type", code);
        writer.WriteString("message", message);
        writer.WriteEndObject();
        writer.WriteEndObject();
        writer.Flush();
        return ms.ToArray();
    }

    private static RouterUsage ParseResponsesUsage(JsonElement usage)
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

    private static RouterUsage ParseChatUsage(JsonElement usage)
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

    private static RouterUsage ParseAnthropicUsage(JsonElement usage)
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

    private static string MapChatFinish(string raw) => raw switch
    {
        "stop" => FinishEvent.Stop,
        "length" => FinishEvent.Length,
        "tool_calls" => FinishEvent.ToolCalls,
        "content_filter" => FinishEvent.ContentFilter,
        _ => FinishEvent.Stop
    };
    private static string MapAnthropicStop(string raw) => raw switch
    {
        "end_turn" => FinishEvent.Stop,
        "stop_sequence" => FinishEvent.Stop,
        "max_tokens" => FinishEvent.Length,
        "tool_use" => FinishEvent.ToolCalls,
        _ => FinishEvent.Stop
    };
    private static string MapFinishToChat(string reason) => reason switch
    {
        FinishEvent.Stop => "stop",
        FinishEvent.Length => "length",
        FinishEvent.ToolCalls => "tool_calls",
        FinishEvent.ContentFilter => "content_filter",
        FinishEvent.Error => "stop",
        _ => "stop"
    };
    private static string MapFinishToAnthropic(string reason) => reason switch
    {
        FinishEvent.Stop => "end_turn",
        FinishEvent.Length => "max_tokens",
        FinishEvent.ToolCalls => "tool_use",
        FinishEvent.ContentFilter => "end_turn",
        _ => "end_turn"
    };
    private static string ExtractCustomInput(string argsJson)
    {
        try { using var doc = JsonDocument.Parse(argsJson); if (doc.RootElement.TryGetProperty("input", out var inp) && inp.ValueKind == JsonValueKind.String) return inp.GetString() ?? argsJson; return argsJson; } catch { return argsJson; }
    }
    private static string RandomHex(int bytes) => Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(bytes)).ToLowerInvariant();
}
