using System.Text;
using System.Text.Json;

namespace AgentNotify.Core.Router.Translation;

internal static class RouterAnthropicCodec
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
            if (root.TryGetProperty("max_tokens", out var mt) && mt.ValueKind != JsonValueKind.Null)
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
            if (root.TryGetProperty("output_config", out var outputConfig) && outputConfig.ValueKind == JsonValueKind.Object &&
                outputConfig.TryGetProperty("effort", out var effort) && effort.ValueKind == JsonValueKind.String)
                reasoningEffort = effort.GetString();

            IReadOnlyList<string>? stop = null;
            if (root.TryGetProperty("stop_sequences", out var stopEl) && stopEl.ValueKind != JsonValueKind.Null)
            {
                if (stopEl.ValueKind != JsonValueKind.Array) throw new TranslationException("invalid_request", "Invalid stop_sequences.");
                var list = new List<string>();
                foreach (var e in stopEl.EnumerateArray())
                {
                    if (e.ValueKind == JsonValueKind.String) list.Add(e.GetString()!);
                    else throw new TranslationException("invalid_request", "Invalid stop sequence.");
                }
                stop = list;
            }

            string? systemPrompt = null;
            if (root.TryGetProperty("system", out var sysEl) && sysEl.ValueKind != JsonValueKind.Null)
            {
                if (sysEl.ValueKind == JsonValueKind.String)
                    systemPrompt = sysEl.GetString();
                else if (sysEl.ValueKind == JsonValueKind.Array)
                {
                    var sb = new StringBuilder();
                    foreach (var bl in sysEl.EnumerateArray())
                    {
                        if (bl.ValueKind == JsonValueKind.Object && bl.TryGetProperty("type", out var ty) && ty.ValueKind == JsonValueKind.String && ty.GetString() == "text" && bl.TryGetProperty("text", out var te) && te.ValueKind == JsonValueKind.String)
                        {
                            if (sb.Length > 0) sb.Append("\n");
                            sb.Append(te.GetString());
                        }
                        else if (bl.ValueKind == JsonValueKind.String)
                        {
                            if (sb.Length > 0) sb.Append("\n");
                            sb.Append(bl.GetString());
                        }
                        else throw new TranslationException("invalid_request", "Invalid system block.");
                    }
                    systemPrompt = sb.ToString();
                }
                else throw new TranslationException("invalid_request", "Invalid system.");
            }

            RouterToolChoice? toolChoice = null;
            if (root.TryGetProperty("tool_choice", out var tc) && tc.ValueKind != JsonValueKind.Null)
            {
                if (tc.ValueKind != JsonValueKind.Object) throw new TranslationException("invalid_request", "Invalid tool_choice.");
                if (!tc.TryGetProperty("type", out var ty) || ty.ValueKind != JsonValueKind.String)
                    throw new TranslationException("invalid_request", "tool_choice missing type.");
                var t = ty.GetString();
                switch (t)
                {
                    case "auto": toolChoice = RouterToolChoice.Auto; break;
                    case "any": toolChoice = RouterToolChoice.Required; break;
                    case "tool":
                        if (!tc.TryGetProperty("name", out var nm) || nm.ValueKind != JsonValueKind.String)
                            throw new TranslationException("invalid_request", "tool_choice tool missing name.");
                        toolChoice = RouterToolChoice.ForTool(nm.GetString()!);
                        break;
                    case "none": toolChoice = RouterToolChoice.None; break;
                    default: throw new TranslationException("invalid_request", $"Unknown tool_choice type {t}.");
                }
            }

            var tools = new List<RouterTool>();
            var customToolNames = new HashSet<string>(StringComparer.Ordinal);
            if (root.TryGetProperty("tools", out var toolsEl) && toolsEl.ValueKind != JsonValueKind.Null)
            {
                if (toolsEl.ValueKind != JsonValueKind.Array) throw new TranslationException("invalid_request", "Invalid tools.");
                foreach (var t in toolsEl.EnumerateArray())
                {
                    if (t.ValueKind != JsonValueKind.Object) throw new TranslationException("invalid_request", "Invalid tool.");
                    if (!t.TryGetProperty("name", out var nm) || nm.ValueKind != JsonValueKind.String)
                        throw new TranslationException("invalid_request", "Tool missing name.");
                    var name = nm.GetString()!;
                    string? desc = null;
                    if (t.TryGetProperty("description", out var d) && d.ValueKind == JsonValueKind.String) desc = d.GetString();
                    string? paramsJson = null;
                    if (t.TryGetProperty("input_schema", out var isc) && isc.ValueKind != JsonValueKind.Null)
                        paramsJson = isc.GetRawText();
                    tools.Add(new RouterTool(name, desc, paramsJson));
                }
            }

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
                    if (role == "system")
                    {
                        // Claude Code sends system text mid-conversation too (its environment block,
                        // under the mid-conversation-system beta). The other wires have nowhere to put a
                        // system turn, so it joins the system prompt, as their own decoders do.
                        if (SystemText(mEl) is { Length: > 0 } midSystem)
                            systemPrompt = string.IsNullOrEmpty(systemPrompt) ? midSystem : systemPrompt + "\n" + midSystem;
                        continue;
                    }
                    if (role != "user" && role != "assistant")
                        throw new TranslationException("invalid_request", $"Invalid role {role}.");
                    if (!mEl.TryGetProperty("content", out var contentEl))
                        throw new TranslationException("invalid_request", "Message missing content.");
                    if (contentEl.ValueKind == JsonValueKind.String)
                    {
                        var txt = contentEl.GetString() ?? "";
                        messages.Add(new RouterMessage(role, [new RouterTextPart(txt)]));
                    }
                    else if (contentEl.ValueKind == JsonValueKind.Array)
                    {
                        var pendingParts = new List<RouterMessagePart>();
                        void FlushPending(string flushRole)
                        {
                            if (pendingParts.Count > 0)
                            {
                                messages.Add(new RouterMessage(flushRole, pendingParts.ToList()));
                                pendingParts.Clear();
                            }
                        }

                        foreach (var blk in contentEl.EnumerateArray())
                        {
                            if (blk.ValueKind != JsonValueKind.Object) throw new TranslationException("invalid_request", "Invalid content block.");
                            if (!blk.TryGetProperty("type", out var ty) || ty.ValueKind != JsonValueKind.String)
                                throw new TranslationException("invalid_request", "Content block missing type.");
                            var typ = ty.GetString();
                            switch (typ)
                            {
                                case "text":
                                    if (!blk.TryGetProperty("text", out var te) || te.ValueKind != JsonValueKind.String)
                                        throw new TranslationException("invalid_request", "Text block missing text.");
                                    pendingParts.Add(new RouterTextPart(te.GetString()!));
                                    break;
                                case "image":
                                    if (!blk.TryGetProperty("source", out var src) || src.ValueKind != JsonValueKind.Object)
                                        throw new TranslationException("invalid_request", "Image missing source.");
                                    if (!src.TryGetProperty("type", out var st) || st.ValueKind != JsonValueKind.String)
                                        throw new TranslationException("invalid_request", "Image source missing type.");
                                    var stype = st.GetString();
                                    if (stype == "base64")
                                    {
                                        if (!src.TryGetProperty("data", out var d) || d.ValueKind != JsonValueKind.String)
                                            throw new TranslationException("invalid_request", "Image base64 missing data.");
                                        string? mediaType = null;
                                        if (src.TryGetProperty("media_type", out var mtEl) && mtEl.ValueKind == JsonValueKind.String) mediaType = mtEl.GetString();
                                        pendingParts.Add(new RouterImagePart(null, d.GetString()!, mediaType ?? "image/png"));
                                    }
                                    else if (stype == "url")
                                    {
                                        if (!src.TryGetProperty("url", out var u) || u.ValueKind != JsonValueKind.String)
                                            throw new TranslationException("invalid_request", "Image url missing url.");
                                        pendingParts.Add(new RouterImagePart(u.GetString()!, null, null));
                                    }
                                    else throw new TranslationException("invalid_request", $"Unknown image source type {stype}.");
                                    break;
                                case "tool_use":
                                    if (!blk.TryGetProperty("id", out var id) || id.ValueKind != JsonValueKind.String)
                                        throw new TranslationException("invalid_request", "tool_use missing id.");
                                    if (!blk.TryGetProperty("name", out var nm2) || nm2.ValueKind != JsonValueKind.String)
                                        throw new TranslationException("invalid_request", "tool_use missing name.");
                                    string argsJson = "{}";
                                    if (blk.TryGetProperty("input", out var inp) && inp.ValueKind != JsonValueKind.Null)
                                        argsJson = inp.GetRawText();
                                    pendingParts.Add(new RouterToolCallPart(id.GetString()!, nm2.GetString()!, argsJson));
                                    break;
                                case "tool_result":
                                    if (role != "user")
                                        throw new TranslationException("invalid_request", "tool_result must be in user role.");
                                    FlushPending(role); // flush any pending user text before tool results
                                    if (!blk.TryGetProperty("tool_use_id", out var tuid) || tuid.ValueKind != JsonValueKind.String)
                                        throw new TranslationException("invalid_request", "tool_result missing tool_use_id.");
                                    string txt2 = "";
                                    bool isError = false;
                                    if (blk.TryGetProperty("is_error", out var isErr) && isErr.ValueKind == JsonValueKind.True) isError = true;
                                    if (blk.TryGetProperty("content", out var cont))
                                    {
                                        if (cont.ValueKind == JsonValueKind.String) txt2 = cont.GetString() ?? "";
                                        else if (cont.ValueKind == JsonValueKind.Array)
                                        {
                                            var sb2 = new StringBuilder();
                                            foreach (var cb in cont.EnumerateArray())
                                            {
                                                if (cb.ValueKind == JsonValueKind.Object && cb.TryGetProperty("type", out var cty) && cty.ValueKind == JsonValueKind.String && cty.GetString() == "text" && cb.TryGetProperty("text", out var cte) && cte.ValueKind == JsonValueKind.String)
                                                    sb2.Append(cte.GetString());
                                                else if (cb.ValueKind == JsonValueKind.String) sb2.Append(cb.GetString());
                                            }
                                            txt2 = sb2.ToString();
                                        }
                                        else txt2 = cont.GetRawText();
                                    }
                                    messages.Add(new RouterMessage(RouterMessage.Tool, [new RouterToolResultPart(tuid.GetString()!, txt2, isError)]));
                                    break;
                                case "thinking":
                                case "redacted_thinking":
                                    if (!notes.Contains("thinking_dropped")) notes.Add("thinking_dropped");
                                    break;
                                default:
                                    // Server tools, documents, and whatever Anthropic adds next have no
                                    // equivalent on another wire; the rest of the conversation still does.
                                    if (!notes.Contains("blocks_dropped")) notes.Add("blocks_dropped");
                                    break;
                            }
                        }
                        if (pendingParts.Count > 0)
                        {
                            messages.Add(new RouterMessage(role, pendingParts));
                        }
                    }
                    else throw new TranslationException("invalid_request", "Invalid message content.");
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
                ParallelToolCalls: null,
                CustomToolNames: customToolNames
            );
            return new DecodeResult(req, notes);
        }
    }

    public static byte[] Encode(RouterRequest request, string nativeModel) => EncodeAnthropicProper(request, nativeModel);

    /// <summary>The text of a mid-conversation system message: a string, or its text blocks.</summary>
    private static string? SystemText(JsonElement message)
    {
        if (!message.TryGetProperty("content", out var content)) return null;
        if (content.ValueKind == JsonValueKind.String) return content.GetString();
        if (content.ValueKind != JsonValueKind.Array) throw new TranslationException("invalid_request", "Invalid message content.");
        var parts = content.EnumerateArray()
            .Where(block => block.ValueKind == JsonValueKind.Object &&
                            block.TryGetProperty("type", out var type) && type.ValueKind == JsonValueKind.String && type.GetString() == "text" &&
                            block.TryGetProperty("text", out var text) && text.ValueKind == JsonValueKind.String)
            .Select(block => block.GetProperty("text").GetString());
        return string.Join("\n", parts);
    }

    private static byte[] EncodeAnthropicProper(RouterRequest request, string nativeModel)
    {
        using var ms = new MemoryStream();
        using var writer = new Utf8JsonWriter(ms);
        writer.WriteStartObject();
        writer.WriteString("model", nativeModel);
        if (!string.IsNullOrEmpty(request.SystemPrompt))
            writer.WriteString("system", request.SystemPrompt);

        var grouped = new List<(string role, List<RouterMessagePart> parts)>();
        foreach (var msg in request.Messages)
        {
            string effRole = msg.Role == RouterMessage.Tool ? "user" : msg.Role;
            if (grouped.Count > 0 && grouped[^1].role == effRole)
            {
                grouped[^1].parts.AddRange(msg.Parts);
            }
            else
            {
                grouped.Add((effRole, msg.Parts.ToList()));
            }
        }


        writer.WritePropertyName("messages");
        writer.WriteStartArray();
        foreach (var g in grouped)
        {
            writer.WriteStartObject();
            writer.WriteString("role", g.role);
            writer.WritePropertyName("content");
            writer.WriteStartArray();
            foreach (var part in g.parts)
            {
                WriteAnthBlock(writer, part);
            }
            writer.WriteEndArray();
            writer.WriteEndObject();
        }
        writer.WriteEndArray();

        if (request.Tools.Count > 0)
        {
            writer.WritePropertyName("tools");
            writer.WriteStartArray();
            foreach (var tool in request.Tools)
            {
                writer.WriteStartObject();
                writer.WriteString("name", tool.Name);
                if (!string.IsNullOrEmpty(tool.Description))
                    writer.WriteString("description", tool.Description);
                writer.WritePropertyName("input_schema");
                if (!string.IsNullOrEmpty(tool.ParametersJson))
                    writer.WriteRawValue(tool.ParametersJson);
                else
                {
                    writer.WriteStartObject();
                    writer.WritePropertyName("type");
                    writer.WriteStringValue("object");
                    writer.WriteEndObject();
                }
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
        }

        if (request.ToolChoice != null)
        {
            writer.WritePropertyName("tool_choice");
            writer.WriteStartObject();
            var tc = request.ToolChoice;
            if (tc.IsAuto) writer.WriteString("type", "auto");
            else if (tc.IsNone) writer.WriteString("type", "none");
            else if (tc.IsRequired) writer.WriteString("type", "any");
            else if (tc.IsTool) { writer.WriteString("type", "tool"); writer.WriteString("name", tc.Name!); }
            writer.WriteEndObject();
        }

        writer.WriteNumber("max_tokens", request.MaxOutputTokens ?? 8192);
        if (request.Temperature.HasValue)
            writer.WriteNumber("temperature", request.Temperature.Value);
        if (request.TopP.HasValue)
            writer.WriteNumber("top_p", request.TopP.Value);
        if (request.StopSequences != null && request.StopSequences.Count > 0)
        {
            writer.WritePropertyName("stop_sequences");
            writer.WriteStartArray();
            foreach (var s in request.StopSequences) writer.WriteStringValue(s);
            writer.WriteEndArray();
        }
        writer.WriteBoolean("stream", request.Stream);

        writer.WriteEndObject();
        writer.Flush();
        return ms.ToArray();
    }

    private static void WriteAnthBlock(Utf8JsonWriter writer, RouterMessagePart part)
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
                writer.WriteString("type", "image");
                writer.WritePropertyName("source");
                writer.WriteStartObject();
                if (img.IsUrl && img.Url != null)
                {
                    writer.WriteString("type", "url");
                    writer.WriteString("url", img.Url);
                }
                else
                {
                    writer.WriteString("type", "base64");
                    writer.WriteString("media_type", img.MediaType ?? "image/png");
                    writer.WriteString("data", img.Base64Data ?? "");
                }
                writer.WriteEndObject();
                writer.WriteEndObject();
                break;
            case RouterToolCallPart call:
                writer.WriteStartObject();
                writer.WriteString("type", "tool_use");
                writer.WriteString("id", call.Id);
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
                break;
            case RouterToolResultPart tr:
                writer.WriteStartObject();
                writer.WriteString("type", "tool_result");
                writer.WriteString("tool_use_id", tr.CallId);
                if (tr.IsError) writer.WriteBoolean("is_error", true);
                writer.WritePropertyName("content");
                writer.WriteStartArray();
                writer.WriteStartObject();
                writer.WriteString("type", "text");
                writer.WriteString("text", tr.Text);
                writer.WriteEndObject();
                writer.WriteEndArray();
                writer.WriteEndObject();
                break;
        }
    }

    private static object PartToAnthBlock(RouterMessagePart part) => part; // placeholder

    private static List<RouterMessage> MergeSameRoleMessages(IReadOnlyList<RouterMessage> messages)
    {
        var list = new List<RouterMessage>();
        foreach (var m in messages)
        {
            if (list.Count > 0 && list[^1].Role == m.Role)
            {
                var last = list[^1];
                var mergedParts = last.Parts.Concat(m.Parts).ToList();
                list[^1] = new RouterMessage(last.Role, mergedParts);
            }
            else
                list.Add(m);
        }
        return list;
    }
}
