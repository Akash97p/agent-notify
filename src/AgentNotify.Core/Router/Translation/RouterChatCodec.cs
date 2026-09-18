using System.Text;
using System.Text.Json;

namespace AgentNotify.Core.Router.Translation;

internal static class RouterChatCodec
{
    public static DecodeResult Decode(ReadOnlySpan<byte> body)
    {
        JsonDocument doc;
        try { doc = JsonDocument.Parse(body.ToArray()); }
        catch (JsonException ex) { throw new TranslationException("invalid_request", "Invalid JSON.", ex); }
        using (doc)
        {
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
                throw new TranslationException("invalid_request", "Request must be object.");
            var root = doc.RootElement;
            var notes = new List<string>();

            string? model = null;
            if (root.TryGetProperty("model", out var m))
            {
                if (m.ValueKind == JsonValueKind.String) model = m.GetString();
                else if (m.ValueKind != JsonValueKind.Null) throw new TranslationException("invalid_request", "Invalid model.");
            }
            bool stream = false;
            if (root.TryGetProperty("stream", out var s))
            {
                if (s.ValueKind == JsonValueKind.True) stream = true;
                else if (s.ValueKind == JsonValueKind.False) stream = false;
                else if (s.ValueKind != JsonValueKind.Null) throw new TranslationException("invalid_request", "Invalid stream.");
            }

            int? maxOutput = null;
            if (root.TryGetProperty("max_completion_tokens", out var mct) && mct.ValueKind != JsonValueKind.Null)
            {
                if (mct.ValueKind == JsonValueKind.Number && mct.TryGetInt32(out var v)) maxOutput = v;
                else throw new TranslationException("invalid_request", "Invalid max_completion_tokens.");
            }
            else if (root.TryGetProperty("max_tokens", out var mt) && mt.ValueKind != JsonValueKind.Null)
            {
                if (mt.ValueKind == JsonValueKind.Number && mt.TryGetInt32(out var v)) maxOutput = v;
                else throw new TranslationException("invalid_request", "Invalid max_tokens.");
            }

            double? temperature = null;
            if (root.TryGetProperty("temperature", out var tmp) && tmp.ValueKind != JsonValueKind.Null)
            {
                if (tmp.ValueKind == JsonValueKind.Number && tmp.TryGetDouble(out var d)) temperature = d;
                else throw new TranslationException("invalid_request", "Invalid temperature.");
            }
            double? topP = null;
            if (root.TryGetProperty("top_p", out var tp) && tp.ValueKind != JsonValueKind.Null)
            {
                if (tp.ValueKind == JsonValueKind.Number && tp.TryGetDouble(out var d)) topP = d;
                else throw new TranslationException("invalid_request", "Invalid top_p.");
            }

            string? reasoningEffort = null;
            if (root.TryGetProperty("reasoning_effort", out var re) && re.ValueKind == JsonValueKind.String)
                reasoningEffort = re.GetString();

            bool? parallelToolCalls = null;
            if (root.TryGetProperty("parallel_tool_calls", out var ptc) && ptc.ValueKind != JsonValueKind.Null)
            {
                if (ptc.ValueKind == JsonValueKind.True) parallelToolCalls = true;
                else if (ptc.ValueKind == JsonValueKind.False) parallelToolCalls = false;
                else throw new TranslationException("invalid_request", "Invalid parallel_tool_calls.");
            }

            IReadOnlyList<string>? stop = null;
            if (root.TryGetProperty("stop", out var stopEl) && stopEl.ValueKind != JsonValueKind.Null)
            {
                if (stopEl.ValueKind == JsonValueKind.String)
                    stop = [stopEl.GetString()!];
                else if (stopEl.ValueKind == JsonValueKind.Array)
                {
                    var list = new List<string>();
                    foreach (var e in stopEl.EnumerateArray())
                    {
                        if (e.ValueKind == JsonValueKind.String) list.Add(e.GetString()!);
                        else throw new TranslationException("invalid_request", "Invalid stop.");
                    }
                    stop = list;
                }
                else throw new TranslationException("invalid_request", "Invalid stop.");
            }

            RouterToolChoice? toolChoice = null;
            if (root.TryGetProperty("tool_choice", out var tc) && tc.ValueKind != JsonValueKind.Null)
            {
                if (tc.ValueKind == JsonValueKind.String)
                {
                    var s2 = tc.GetString();
                    toolChoice = s2 switch
                    {
                        "auto" => RouterToolChoice.Auto,
                        "none" => RouterToolChoice.None,
                        "required" => RouterToolChoice.Required,
                        _ => throw new TranslationException("invalid_request", "Invalid tool_choice.")
                    };
                }
                else if (tc.ValueKind == JsonValueKind.Object)
                {
                    if (!tc.TryGetProperty("type", out var ttype) || ttype.ValueKind != JsonValueKind.String)
                        throw new TranslationException("invalid_request", "Invalid tool_choice object.");
                    var tt = ttype.GetString();
                    if (tt == "function")
                    {
                        if (!tc.TryGetProperty("function", out var fn) || fn.ValueKind != JsonValueKind.Object || !fn.TryGetProperty("name", out var nm) || nm.ValueKind != JsonValueKind.String)
                            throw new TranslationException("invalid_request", "Invalid tool_choice function.");
                        toolChoice = RouterToolChoice.ForTool(nm.GetString()!);
                    }
                    else if (tt == "none" || tt == "auto" || tt == "required")
                    {
                        toolChoice = tt switch
                        {
                            "auto" => RouterToolChoice.Auto,
                            "none" => RouterToolChoice.None,
                            "required" => RouterToolChoice.Required,
                            _ => throw new TranslationException("invalid_request", "Invalid tool_choice type.")
                        };
                    }
                    else throw new TranslationException("invalid_request", "Invalid tool_choice type.");
                }
                else throw new TranslationException("invalid_request", "Invalid tool_choice.");
            }

            var tools = new List<RouterTool>();
            var customToolNames = new HashSet<string>(StringComparer.Ordinal);
            if (root.TryGetProperty("tools", out var toolsEl) && toolsEl.ValueKind != JsonValueKind.Null)
            {
                if (toolsEl.ValueKind != JsonValueKind.Array) throw new TranslationException("invalid_request", "Invalid tools.");
                foreach (var t in toolsEl.EnumerateArray())
                {
                    if (t.ValueKind != JsonValueKind.Object) throw new TranslationException("invalid_request", "Invalid tool.");
                    if (!t.TryGetProperty("type", out var ty) || ty.ValueKind != JsonValueKind.String || ty.GetString() != "function")
                        throw new TranslationException("invalid_request", "Tool type must be function.");
                    if (!t.TryGetProperty("function", out var fn) || fn.ValueKind != JsonValueKind.Object)
                        throw new TranslationException("invalid_request", "Tool missing function.");
                    if (!fn.TryGetProperty("name", out var nm) || nm.ValueKind != JsonValueKind.String)
                        throw new TranslationException("invalid_request", "Tool missing name.");
                    var name = nm.GetString()!;
                    string? desc = null;
                    if (fn.TryGetProperty("description", out var d) && d.ValueKind == JsonValueKind.String) desc = d.GetString();
                    string? paramsJson = null;
                    if (fn.TryGetProperty("parameters", out var p) && p.ValueKind != JsonValueKind.Null)
                        paramsJson = p.GetRawText();
                    tools.Add(new RouterTool(name, desc, paramsJson));
                }
            }

            string? systemPrompt = null;
            var messages = new List<RouterMessage>();
            if (root.TryGetProperty("messages", out var msgsEl) && msgsEl.ValueKind != JsonValueKind.Null)
            {
                if (msgsEl.ValueKind != JsonValueKind.Array) throw new TranslationException("invalid_request", "Invalid messages.");
                foreach (var mEl in msgsEl.EnumerateArray())
                {
                    if (mEl.ValueKind != JsonValueKind.Object) throw new TranslationException("invalid_request", "Invalid message.");
                    if (!mEl.TryGetProperty("role", out var roleEl) || roleEl.ValueKind != JsonValueKind.String)
                        throw new TranslationException("invalid_request", "Message missing role.");
                    var role = roleEl.GetString()!;
                    if (role == "system" || role == "developer")
                    {
                        string txt = ExtractContentAsText(mEl);
                        if (!string.IsNullOrEmpty(txt))
                        {
                            if (systemPrompt == null) systemPrompt = txt;
                            else systemPrompt += "\n" + txt;
                        }
                    }
                    else if (role == "user")
                    {
                        var parts = ExtractUserParts(mEl);
                        messages.Add(new RouterMessage(RouterMessage.User, parts));
                    }
                    else if (role == "assistant")
                    {
                        var parts = new List<RouterMessagePart>();
                        if (mEl.TryGetProperty("content", out var contentEl) && contentEl.ValueKind != JsonValueKind.Null)
                        {
                            if (contentEl.ValueKind == JsonValueKind.String)
                            {
                                var txt = contentEl.GetString();
                                if (!string.IsNullOrEmpty(txt)) parts.Add(new RouterTextPart(txt));
                            }
                            else if (contentEl.ValueKind == JsonValueKind.Array)
                            {
                                foreach (var c in contentEl.EnumerateArray())
                                {
                                    if (c.ValueKind == JsonValueKind.Object && c.TryGetProperty("type", out var t) && t.ValueKind == JsonValueKind.String)
                                    {
                                        var typ = t.GetString();
                                        if ((typ == "text" || typ == "output_text") && c.TryGetProperty("text", out var te) && te.ValueKind == JsonValueKind.String)
                                            parts.Add(new RouterTextPart(te.GetString()!));
                                    }
                                }
                            }
                            else if (contentEl.ValueKind != JsonValueKind.Null)
                                throw new TranslationException("invalid_request", "Invalid assistant content.");
                        }
                        if (mEl.TryGetProperty("tool_calls", out var tcEl) && tcEl.ValueKind != JsonValueKind.Null)
                        {
                            if (tcEl.ValueKind != JsonValueKind.Array) throw new TranslationException("invalid_request", "Invalid tool_calls.");
                            foreach (var call in tcEl.EnumerateArray())
                            {
                                if (call.ValueKind != JsonValueKind.Object) throw new TranslationException("invalid_request", "Invalid tool_call.");
                                if (!call.TryGetProperty("id", out var idEl) || idEl.ValueKind != JsonValueKind.String)
                                    throw new TranslationException("invalid_request", "tool_call missing id.");
                                if (!call.TryGetProperty("type", out var typeEl) || typeEl.ValueKind != JsonValueKind.String || typeEl.GetString() != "function")
                                    throw new TranslationException("invalid_request", "tool_call type must be function.");
                                if (!call.TryGetProperty("function", out var fn) || fn.ValueKind != JsonValueKind.Object)
                                    throw new TranslationException("invalid_request", "tool_call missing function.");
                                if (!fn.TryGetProperty("name", out var nm) || nm.ValueKind != JsonValueKind.String)
                                    throw new TranslationException("invalid_request", "tool_call missing name.");
                                string args = "";
                                if (fn.TryGetProperty("arguments", out var a))
                                {
                                    if (a.ValueKind == JsonValueKind.String) args = a.GetString() ?? "";
                                    else args = a.GetRawText();
                                }
                                parts.Add(new RouterToolCallPart(idEl.GetString()!, nm.GetString()!, args));
                            }
                        }
                        messages.Add(new RouterMessage(RouterMessage.Assistant, parts));
                    }
                    else if (role == "tool")
                    {
                        if (!mEl.TryGetProperty("tool_call_id", out var cid) || cid.ValueKind != JsonValueKind.String)
                            throw new TranslationException("invalid_request", "tool message missing tool_call_id.");
                        string text = "";
                        if (mEl.TryGetProperty("content", out var cEl))
                        {
                            if (cEl.ValueKind == JsonValueKind.String) text = cEl.GetString() ?? "";
                            else if (cEl.ValueKind == JsonValueKind.Array)
                            {
                                var sb = new StringBuilder();
                                foreach (var p in cEl.EnumerateArray())
                                {
                                    if (p.ValueKind == JsonValueKind.Object && p.TryGetProperty("type", out var ty) && ty.ValueKind == JsonValueKind.String && ty.GetString() == "text" && p.TryGetProperty("text", out var tt) && tt.ValueKind == JsonValueKind.String)
                                        sb.Append(tt.GetString());
                                    else if (p.ValueKind == JsonValueKind.String) sb.Append(p.GetString());
                                }
                                text = sb.ToString();
                            }
                            else text = cEl.GetRawText();
                        }
                        bool isError = false; // chat tool result has no is_error, default false
                        messages.Add(new RouterMessage(RouterMessage.Tool, [new RouterToolResultPart(cid.GetString()!, text, isError)]));
                    }
                    else
                        throw new TranslationException("invalid_request", $"Invalid role {role}.");
                }
            }
            else
            {
                throw new TranslationException("invalid_request", "Missing messages.");
            }

            var req = new RouterRequest(
                Model: model,
                SystemPrompt: systemPrompt,
                Messages: messages,
                Tools: tools,
                ToolChoice: toolChoice,
                MaxOutputTokens: maxOutput,
                Temperature: temperature,
                TopP: topP,
                StopSequences: stop,
                Stream: stream,
                ReasoningEffort: reasoningEffort,
                ParallelToolCalls: parallelToolCalls,
                CustomToolNames: customToolNames
            );
            return new DecodeResult(req, notes);
        }
    }

    private static string ExtractContentAsText(JsonElement msg)
    {
        if (!msg.TryGetProperty("content", out var c)) return "";
        if (c.ValueKind == JsonValueKind.String) return c.GetString() ?? "";
        if (c.ValueKind == JsonValueKind.Array)
        {
            var sb = new StringBuilder();
            foreach (var part in c.EnumerateArray())
            {
                if (part.ValueKind == JsonValueKind.Object && part.TryGetProperty("type", out var t) && t.ValueKind == JsonValueKind.String)
                {
                    var typ = t.GetString();
                    if (typ == "text" && part.TryGetProperty("text", out var te) && te.ValueKind == JsonValueKind.String)
                    {
                        if (sb.Length > 0) sb.Append("\n");
                        sb.Append(te.GetString());
                    }
                    else if (typ == "image_url" && part.TryGetProperty("image_url", out var iu) && iu.ValueKind == JsonValueKind.Object)
                    {
                    }
                }
                else if (part.ValueKind == JsonValueKind.String)
                {
                    if (sb.Length > 0) sb.Append("\n");
                    sb.Append(part.GetString());
                }
            }
            return sb.ToString();
        }
        if (c.ValueKind == JsonValueKind.Null) return "";
        throw new TranslationException("invalid_request", "Invalid content.");
    }

    private static List<RouterMessagePart> ExtractUserParts(JsonElement msg)
    {
        var parts = new List<RouterMessagePart>();
        if (!msg.TryGetProperty("content", out var c) || c.ValueKind == JsonValueKind.Null)
            return parts;
        if (c.ValueKind == JsonValueKind.String)
        {
            parts.Add(new RouterTextPart(c.GetString() ?? ""));
            return parts;
        }
        if (c.ValueKind == JsonValueKind.Array)
        {
            foreach (var p in c.EnumerateArray())
            {
                if (p.ValueKind != JsonValueKind.Object) throw new TranslationException("invalid_request", "Invalid user content part.");
                if (!p.TryGetProperty("type", out var ty) || ty.ValueKind != JsonValueKind.String)
                    throw new TranslationException("invalid_request", "Content part missing type.");
                var type = ty.GetString();
                if (type == "text")
                {
                    if (!p.TryGetProperty("text", out var te) || te.ValueKind != JsonValueKind.String)
                        throw new TranslationException("invalid_request", "Text part missing text.");
                    parts.Add(new RouterTextPart(te.GetString()!));
                }
                else if (type == "image_url")
                {
                    if (!p.TryGetProperty("image_url", out var iu) || iu.ValueKind != JsonValueKind.Object)
                        throw new TranslationException("invalid_request", "image_url missing.");
                    if (!iu.TryGetProperty("url", out var urlEl) || urlEl.ValueKind != JsonValueKind.String)
                        throw new TranslationException("invalid_request", "image_url missing url.");
                    var url = urlEl.GetString()!;
                    if (url.StartsWith("data:", StringComparison.Ordinal))
                    {
                        var parsed = ParseDataUrl(url);
                        if (parsed is not null)
                            parts.Add(new RouterImagePart(null, parsed.Value.Base64, parsed.Value.MediaType));
                        else
                            parts.Add(new RouterImagePart(url, null, null));
                    }
                    else
                    {
                        parts.Add(new RouterImagePart(url, null, null));
                    }
                }
                else throw new TranslationException("invalid_request", $"Unknown content type {type}.");
            }
            return parts;
        }
        throw new TranslationException("invalid_request", "Invalid user content.");
    }

    private static (string MediaType, string Base64)? ParseDataUrl(string url)
    {
        if (!url.StartsWith("data:", StringComparison.Ordinal)) return null;
        var comma = url.IndexOf(',');
        if (comma < 0) return null;
        var meta = url.Substring(5, comma - 5);
        var data = url.Substring(comma + 1);
        var isBase64 = meta.Contains("base64", StringComparison.OrdinalIgnoreCase);
        if (!isBase64) return null;
        var semi = meta.IndexOf(';');
        var media = semi >= 0 ? meta.Substring(0, semi) : meta;
        if (string.IsNullOrEmpty(media)) media = "image/png";
        return (media, data);
    }

    /// <summary>
    /// Joins neighbouring assistant messages into one. A Responses client sends a turn's text and its
    /// tool calls as separate items, which decodes to two assistant messages in a row; some
    /// OpenAI-compatible providers reject that, and one message carrying both is what every provider
    /// documents.
    /// </summary>
    internal static List<RouterMessage> MergeAdjacentAssistants(IReadOnlyList<RouterMessage> messages)
    {
        var merged = new List<RouterMessage>(messages.Count);
        foreach (var message in messages)
        {
            if (merged.Count > 0 &&
                message.Role == RouterMessage.Assistant &&
                merged[^1].Role == RouterMessage.Assistant)
            {
                var combined = new List<RouterMessagePart>(merged[^1].Parts);
                combined.AddRange(message.Parts);
                merged[^1] = merged[^1] with { Parts = combined };
                continue;
            }
            merged.Add(message);
        }
        return merged;
    }

    public static byte[] Encode(RouterRequest request, string nativeModel)
    {
        using var ms = new MemoryStream();
        using var writer = new Utf8JsonWriter(ms);
        writer.WriteStartObject();
        writer.WriteString("model", nativeModel);

        writer.WritePropertyName("messages");
        writer.WriteStartArray();
        if (!string.IsNullOrEmpty(request.SystemPrompt))
        {
            writer.WriteStartObject();
            writer.WriteString("role", "system");
            writer.WriteString("content", request.SystemPrompt);
            writer.WriteEndObject();
        }
        foreach (var msg in MergeAdjacentAssistants(request.Messages))
        {
            if (msg.Role == RouterMessage.User)
            {
                writer.WriteStartObject();
                writer.WriteString("role", "user");
                var textParts = msg.Parts.OfType<RouterTextPart>().ToList();
                var imageParts = msg.Parts.OfType<RouterImagePart>().ToList();
                var toolParts = msg.Parts.OfType<RouterToolResultPart>().ToList(); // should not appear in user, but tool results are separate role
                if (imageParts.Count == 0 && textParts.Count == 1 && msg.Parts.Count == 1)
                {
                    writer.WriteString("content", textParts[0].Text);
                }
                else
                {
                    writer.WritePropertyName("content");
                    writer.WriteStartArray();
                    foreach (var part in msg.Parts)
                    {
                        switch (part)
                        {
                            case RouterTextPart t:
                                writer.WriteStartObject();
                                writer.WriteString("type", "text");
                                writer.WriteString("text", t.Text);
                                writer.WriteEndObject();
                                break;
                            case RouterImagePart img:
                                writer.WriteStartObject();
                                writer.WriteString("type", "image_url");
                                writer.WritePropertyName("image_url");
                                writer.WriteStartObject();
                                if (img.IsUrl && img.Url != null)
                                    writer.WriteString("url", img.Url);
                                else if (img.IsBase64 && img.Base64Data != null)
                                {
                                    var mt = img.MediaType ?? "image/png";
                                    writer.WriteString("url", $"data:{mt};base64,{img.Base64Data}");
                                }
                                else writer.WriteString("url", "");
                                writer.WriteEndObject();
                                writer.WriteEndObject();
                                break;
                        }
                    }
                    writer.WriteEndArray();
                }
                writer.WriteEndObject();
            }
            else if (msg.Role == RouterMessage.Assistant)
            {
                writer.WriteStartObject();
                writer.WriteString("role", "assistant");
                var textParts = msg.Parts.OfType<RouterTextPart>().ToList();
                var toolCalls = msg.Parts.OfType<RouterToolCallPart>().ToList();
                if (textParts.Count > 0)
                {
                    if (textParts.Count == 1 && toolCalls.Count == 0)
                        writer.WriteString("content", textParts[0].Text);
                    else if (textParts.Count > 0)
                    {
                        var combined = string.Join("\n", textParts.Select(p => p.Text));
                        writer.WriteString("content", combined);
                    }
                }
                else
                {
                    if (toolCalls.Count > 0)
                        writer.WriteNull("content");
                    else
                        writer.WriteNull("content");
                }

                if (toolCalls.Count > 0)
                {
                    writer.WritePropertyName("tool_calls");
                    writer.WriteStartArray();
                    foreach (var call in toolCalls)
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
            }
            else if (msg.Role == RouterMessage.Tool)
            {
                foreach (var part in msg.Parts)
                {
                    if (part is RouterToolResultPart tr)
                    {
                        writer.WriteStartObject();
                        writer.WriteString("role", "tool");
                        writer.WriteString("tool_call_id", tr.CallId);
                        writer.WriteString("content", tr.Text);
                        writer.WriteEndObject();
                    }
                }
            }
        }
        writer.WriteEndArray();

        if (request.Tools.Count > 0)
        {
            writer.WritePropertyName("tools");
            writer.WriteStartArray();
            foreach (var tool in request.Tools)
            {
                writer.WriteStartObject();
                writer.WriteString("type", "function");
                writer.WritePropertyName("function");
                writer.WriteStartObject();
                writer.WriteString("name", tool.Name);
                if (!string.IsNullOrEmpty(tool.Description))
                    writer.WriteString("description", tool.Description);
                if (!string.IsNullOrEmpty(tool.ParametersJson))
                {
                    writer.WritePropertyName("parameters");
                    writer.WriteRawValue(tool.ParametersJson);
                }
                else
                {
                    writer.WritePropertyName("parameters");
                    writer.WriteStartObject();
                    writer.WriteEndObject();
                }
                writer.WriteEndObject();
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
        }

        if (request.ToolChoice != null)
        {
            writer.WritePropertyName("tool_choice");
            var tc = request.ToolChoice;
            if (tc.IsAuto) writer.WriteStringValue("auto");
            else if (tc.IsNone) writer.WriteStringValue("none");
            else if (tc.IsRequired) writer.WriteStringValue("required");
            else if (tc.IsTool)
            {
                writer.WriteStartObject();
                writer.WriteString("type", "function");
                writer.WritePropertyName("function");
                writer.WriteStartObject();
                writer.WriteString("name", tc.Name!);
                writer.WriteEndObject();
                writer.WriteEndObject();
            }
        }

        if (request.MaxOutputTokens.HasValue)
            writer.WriteNumber("max_tokens", request.MaxOutputTokens.Value); // chat uses max_tokens
        if (request.Temperature.HasValue)
            writer.WriteNumber("temperature", request.Temperature.Value);
        if (request.TopP.HasValue)
            writer.WriteNumber("top_p", request.TopP.Value);
        if (request.StopSequences != null && request.StopSequences.Count > 0)
        {
            writer.WritePropertyName("stop");
            if (request.StopSequences.Count == 1)
                writer.WriteStringValue(request.StopSequences[0]);
            else
            {
                writer.WriteStartArray();
                foreach (var s in request.StopSequences) writer.WriteStringValue(s);
                writer.WriteEndArray();
            }
        }
        if (request.ParallelToolCalls.HasValue)
            writer.WriteBoolean("parallel_tool_calls", request.ParallelToolCalls.Value);
        if (!string.IsNullOrEmpty(request.ReasoningEffort))
            writer.WriteString("reasoning_effort", request.ReasoningEffort);

        writer.WriteBoolean("stream", request.Stream);
        if (request.Stream)
        {
            writer.WritePropertyName("stream_options");
            writer.WriteStartObject();
            writer.WriteBoolean("include_usage", true);
            writer.WriteEndObject();
        }

        writer.WriteEndObject();
        writer.Flush();
        return ms.ToArray();
    }
}
