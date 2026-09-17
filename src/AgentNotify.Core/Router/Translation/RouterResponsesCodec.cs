using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AgentNotify.Core.Router.Translation;

internal static class RouterResponsesCodec
{
    public static DecodeResult Decode(ReadOnlySpan<byte> body)
    {
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(body.ToArray());
        }
        catch (JsonException ex)
        {
            throw new TranslationException("invalid_request", "Invalid JSON.", ex);
        }
        using (doc)
        {
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
                throw new TranslationException("invalid_request", "Request must be a JSON object.");

            var root = doc.RootElement;
            var notes = new List<string>();

            string? model = null;
            if (root.TryGetProperty("model", out var m))
            {
                if (m.ValueKind == JsonValueKind.String)
                    model = m.GetString();
                else if (m.ValueKind != JsonValueKind.Null)
                    throw new TranslationException("invalid_request", "Invalid model.");
            }

            bool stream = false;
            if (root.TryGetProperty("stream", out var s))
            {
                if (s.ValueKind == JsonValueKind.True) stream = true;
                else if (s.ValueKind == JsonValueKind.False) stream = false;
                else if (s.ValueKind != JsonValueKind.Null)
                    throw new TranslationException("invalid_request", "Invalid stream flag.");
            }

            int? maxOutput = null;
            if (root.TryGetProperty("max_output_tokens", out var mo) && mo.ValueKind != JsonValueKind.Null)
            {
                if (mo.ValueKind == JsonValueKind.Number && mo.TryGetInt32(out var v)) maxOutput = v;
                else throw new TranslationException("invalid_request", "Invalid max_output_tokens.");
            }
            else if (root.TryGetProperty("max_tokens", out var mt) && mt.ValueKind != JsonValueKind.Null)
            {
                if (mt.ValueKind == JsonValueKind.Number && mt.TryGetInt32(out var v)) maxOutput = v;
                else throw new TranslationException("invalid_request", "Invalid max_tokens.");
            }

            double? temperature = null;
            if (root.TryGetProperty("temperature", out var temp) && temp.ValueKind != JsonValueKind.Null)
            {
                if (temp.ValueKind == JsonValueKind.Number && temp.TryGetDouble(out var d)) temperature = d;
                else throw new TranslationException("invalid_request", "Invalid temperature.");
            }
            double? topP = null;
            if (root.TryGetProperty("top_p", out var tp) && tp.ValueKind != JsonValueKind.Null)
            {
                if (tp.ValueKind == JsonValueKind.Number && tp.TryGetDouble(out var d)) topP = d;
                else throw new TranslationException("invalid_request", "Invalid top_p.");
            }

            string? reasoningEffort = null;
            if (root.TryGetProperty("reasoning", out var reasoning) && reasoning.ValueKind == JsonValueKind.Object)
            {
                if (reasoning.TryGetProperty("effort", out var eff) && eff.ValueKind == JsonValueKind.String)
                    reasoningEffort = eff.GetString();
            }

            bool? parallelToolCalls = null;
            if (root.TryGetProperty("parallel_tool_calls", out var ptc) && ptc.ValueKind != JsonValueKind.Null)
            {
                if (ptc.ValueKind == JsonValueKind.True) parallelToolCalls = true;
                else if (ptc.ValueKind == JsonValueKind.False) parallelToolCalls = false;
                else throw new TranslationException("invalid_request", "Invalid parallel_tool_calls.");
            }

            RouterToolChoice? toolChoice = null;
            if (root.TryGetProperty("tool_choice", out var tc) && tc.ValueKind != JsonValueKind.Null)
            {
                toolChoice = ParseToolChoice(tc);
            }

            var tools = new List<RouterTool>();
            var customToolNames = new HashSet<string>(StringComparer.Ordinal);
            if (root.TryGetProperty("tools", out var toolsEl) && toolsEl.ValueKind != JsonValueKind.Null)
            {
                if (toolsEl.ValueKind != JsonValueKind.Array)
                    throw new TranslationException("invalid_request", "Invalid tools.");
                foreach (var t in toolsEl.EnumerateArray())
                {
                    if (t.ValueKind != JsonValueKind.Object)
                        throw new TranslationException("invalid_request", "Invalid tool shape.");
                    if (!t.TryGetProperty("type", out var ty) || ty.ValueKind != JsonValueKind.String)
                        throw new TranslationException("invalid_request", "Tool missing type.");
                    var type = ty.GetString();
                    if (type == "function")
                    {
                        if (!t.TryGetProperty("name", out var nm) || nm.ValueKind != JsonValueKind.String)
                            throw new TranslationException("invalid_request", "Function tool missing name.");
                        var name = nm.GetString()!;
                        string? desc = null;
                        if (t.TryGetProperty("description", out var d) && d.ValueKind == JsonValueKind.String) desc = d.GetString();
                        string? paramsJson = null;
                        if (t.TryGetProperty("parameters", out var p) && p.ValueKind != JsonValueKind.Null)
                            paramsJson = p.GetRawText();
                        tools.Add(new RouterTool(name, desc, paramsJson));
                    }
                    else if (type == "custom")
                    {
                        if (!t.TryGetProperty("name", out var nm) || nm.ValueKind != JsonValueKind.String)
                            throw new TranslationException("invalid_request", "Custom tool missing name.");
                        var name = nm.GetString()!;
                        string? desc = null;
                        if (t.TryGetProperty("description", out var d) && d.ValueKind == JsonValueKind.String) desc = d.GetString();
                        var schema = """{"type":"object","properties":{"input":{"type":"string"}},"required":["input"],"additionalProperties":false}""";
                        tools.Add(new RouterTool(name, desc, schema));
                        customToolNames.Add(name);
                    }
                    else
                    {
                        if (!notes.Contains("tools_dropped")) notes.Add("tools_dropped");
                    }
                }
            }

            string? systemPrompt = null;
            if (root.TryGetProperty("instructions", out var instr) && instr.ValueKind != JsonValueKind.Null)
            {
                if (instr.ValueKind == JsonValueKind.String)
                    systemPrompt = instr.GetString();
                else throw new TranslationException("invalid_request", "Invalid instructions.");
            }

            if (root.TryGetProperty("previous_response_id", out var prid) && prid.ValueKind != JsonValueKind.Null)
            {
                if (prid.ValueKind == JsonValueKind.String)
                {
                    if (!notes.Contains("previous_response_id")) notes.Add("previous_response_id");
                }
                else throw new TranslationException("invalid_request", "Invalid previous_response_id.");
            }


            var messages = new List<RouterMessage>();
            string? systemFromInput = null;
            if (root.TryGetProperty("input", out var inputEl) && inputEl.ValueKind != JsonValueKind.Null)
            {
                if (inputEl.ValueKind == JsonValueKind.String)
                {
                    var txt = inputEl.GetString() ?? "";
                    messages.Add(new RouterMessage(RouterMessage.User, [new RouterTextPart(txt)]));
                }
                else if (inputEl.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in inputEl.EnumerateArray())
                    {
                        if (item.ValueKind != JsonValueKind.Object)
                            throw new TranslationException("invalid_request", "Invalid input item.");
                        if (!item.TryGetProperty("type", out var ity) || ity.ValueKind != JsonValueKind.String)
                            throw new TranslationException("invalid_request", "Input item missing type.");
                        var itype = ity.GetString();
                        switch (itype)
                        {
                            case "message":
                            {
                                if (!item.TryGetProperty("role", out var roleEl) || roleEl.ValueKind != JsonValueKind.String)
                                    throw new TranslationException("invalid_request", "Message missing role.");
                                var role = roleEl.GetString()!;
                                if (!item.TryGetProperty("content", out var contentEl))
                                    throw new TranslationException("invalid_request", "Message missing content.");

                                List<RouterMessagePart> parts = new();
                                if (contentEl.ValueKind == JsonValueKind.String)
                                {
                                    var txt = contentEl.GetString() ?? "";
                                    parts.Add(new RouterTextPart(txt));
                                }
                                else if (contentEl.ValueKind == JsonValueKind.Array)
                                {
                                    foreach (var c in contentEl.EnumerateArray())
                                    {
                                        if (c.ValueKind != JsonValueKind.Object) throw new TranslationException("invalid_request", "Invalid content part.");
                                        if (!c.TryGetProperty("type", out var ctype) || ctype.ValueKind != JsonValueKind.String)
                                            throw new TranslationException("invalid_request", "Content part missing type.");
                                        var ct = ctype.GetString();
                                        if (ct == "input_text" || ct == "output_text")
                                        {
                                            if (!c.TryGetProperty("text", out var te) || te.ValueKind != JsonValueKind.String)
                                                throw new TranslationException("invalid_request", "Text part missing text.");
                                            parts.Add(new RouterTextPart(te.GetString()!));
                                        }
                                        else if (ct == "input_image")
                                        {
                                            string? url = null;
                                            string? b64 = null;
                                            string? mt = null;
                                            if (c.TryGetProperty("image_url", out var iu) && iu.ValueKind == JsonValueKind.String)
                                            {
                                                url = iu.GetString();
                                                if (url != null && url.StartsWith("data:", StringComparison.Ordinal))
                                                {
                                                    var parsed = ParseDataUrl(url);
                                                    if (parsed is not null)
                                                    {
                                                        b64 = parsed.Value.Base64;
                                                        mt = parsed.Value.MediaType;
                                                        url = null;
                                                    }
                                                }
                                            }
                                            else
                                            {
                                            }
                                            parts.Add(new RouterImagePart(url, b64, mt));
                                        }
                                        else
                                        {
                                        }
                                    }
                                }
                                else throw new TranslationException("invalid_request", "Invalid message content.");

                                if (role == "system" || role == "developer")
                                {
                                    var txt = string.Join("\n", parts.OfType<RouterTextPart>().Select(p => p.Text));
                                    var imageTexts = parts.OfType<RouterImagePart>().Count();
                                    if (!string.IsNullOrEmpty(txt))
                                    {
                                        if (systemFromInput == null) systemFromInput = txt;
                                        else systemFromInput += "\n" + txt;
                                    }
                                }
                                else if (role == "user")
                                {
                                    messages.Add(new RouterMessage(RouterMessage.User, parts));
                                }
                                else if (role == "assistant")
                                {
                                    messages.Add(new RouterMessage(RouterMessage.Assistant, parts));
                                }
                                else
                                {
                                    throw new TranslationException("invalid_request", $"Invalid message role {role}.");
                                }
                                break;
                            }
                            case "function_call":
                            {
                                if (!item.TryGetProperty("call_id", out var cid) || cid.ValueKind != JsonValueKind.String)
                                    throw new TranslationException("invalid_request", "function_call missing call_id.");
                                if (!item.TryGetProperty("name", out var nm) || nm.ValueKind != JsonValueKind.String)
                                    throw new TranslationException("invalid_request", "function_call missing name.");
                                string args = "";
                                if (item.TryGetProperty("arguments", out var a))
                                {
                                    if (a.ValueKind == JsonValueKind.String) args = a.GetString() ?? "";
                                    else args = a.GetRawText();
                                }
                                var call = new RouterToolCallPart(cid.GetString()!, nm.GetString()!, args);
                                messages.Add(new RouterMessage(RouterMessage.Assistant, [call]));
                                break;
                            }
                            case "function_call_output":
                            {
                                if (!item.TryGetProperty("call_id", out var cid) || cid.ValueKind != JsonValueKind.String)
                                    throw new TranslationException("invalid_request", "function_call_output missing call_id.");
                                string text = "";
                                bool isError = false;
                                if (item.TryGetProperty("output", out var outEl))
                                {
                                    if (outEl.ValueKind == JsonValueKind.String)
                                        text = outEl.GetString() ?? "";
                                    else if (outEl.ValueKind == JsonValueKind.Array)
                                    {
                                        var sb = new StringBuilder();
                                        foreach (var p in outEl.EnumerateArray())
                                        {
                                            if (p.ValueKind == JsonValueKind.Object && p.TryGetProperty("type", out var pt) && pt.ValueKind == JsonValueKind.String)
                                            {
                                                var ptv = pt.GetString();
                                                if (ptv == "input_text" || ptv == "output_text")
                                                {
                                                    if (p.TryGetProperty("text", out var tt) && tt.ValueKind == JsonValueKind.String)
                                                        sb.Append(tt.GetString());
                                                }
                                            }
                                            else if (p.ValueKind == JsonValueKind.String)
                                                sb.Append(p.GetString());
                                        }
                                        text = sb.ToString();
                                    }
                                    else
                                        text = outEl.GetRawText();
                                }
                                messages.Add(new RouterMessage(RouterMessage.Tool, [new RouterToolResultPart(cid.GetString()!, text, isError)]));
                                break;
                            }
                            case "custom_tool_call":
                            {
                                if (!item.TryGetProperty("call_id", out var cid) || cid.ValueKind != JsonValueKind.String)
                                    throw new TranslationException("invalid_request", "custom_tool_call missing call_id.");
                                if (!item.TryGetProperty("name", out var nm) || nm.ValueKind != JsonValueKind.String)
                                    throw new TranslationException("invalid_request", "custom_tool_call missing name.");
                                string input = "";
                                if (item.TryGetProperty("input", out var inp) && inp.ValueKind == JsonValueKind.String)
                                    input = inp.GetString() ?? "";
                                else if (item.TryGetProperty("input", out var inp2))
                                    input = inp2.GetRawText();
                                var args = JsonSerializer.Serialize(new { input });
                                var argsJson = "{\"input\":" + JsonSerializer.Serialize(input) + "}";
                                var call = new RouterToolCallPart(cid.GetString()!, nm.GetString()!, argsJson);
                                messages.Add(new RouterMessage(RouterMessage.Assistant, [call]));
                                customToolNames.Add(nm.GetString()!);
                                break;
                            }
                            case "custom_tool_call_output":
                            {
                                if (!item.TryGetProperty("call_id", out var cid) || cid.ValueKind != JsonValueKind.String)
                                    throw new TranslationException("invalid_request", "custom_tool_call_output missing call_id.");
                                string output = "";
                                if (item.TryGetProperty("output", out var outE))
                                {
                                    if (outE.ValueKind == JsonValueKind.String) output = outE.GetString() ?? "";
                                    else output = outE.GetRawText();
                                }
                                messages.Add(new RouterMessage(RouterMessage.Tool, [new RouterToolResultPart(cid.GetString()!, output, false)]));
                                break;
                            }
                            case "reasoning":
                            {
                                if (!notes.Contains("reasoning_dropped")) notes.Add("reasoning_dropped");
                                break;
                            }
                            default:
                            {
                                throw new TranslationException("invalid_request", $"Unknown input item type {itype}.");
                            }
                        }
                    }
                }
                else throw new TranslationException("invalid_request", "Invalid input shape.");
            }

            string? finalSystem = null;
            if (systemPrompt != null && systemFromInput != null)
                finalSystem = systemPrompt + "\n" + systemFromInput;
            else finalSystem = systemPrompt ?? systemFromInput;


            var req = new RouterRequest(
                Model: model,
                SystemPrompt: string.IsNullOrEmpty(finalSystem) ? null : finalSystem,
                Messages: messages,
                Tools: tools,
                ToolChoice: toolChoice,
                MaxOutputTokens: maxOutput,
                Temperature: temperature,
                TopP: topP,
                StopSequences: null,
                Stream: stream,
                ReasoningEffort: reasoningEffort,
                ParallelToolCalls: parallelToolCalls,
                CustomToolNames: customToolNames
            );
            return new DecodeResult(req, notes);
        }
    }

    private static RouterToolChoice? ParseToolChoice(JsonElement el)
    {
        if (el.ValueKind == JsonValueKind.String)
        {
            var s = el.GetString();
            return s switch
            {
                "auto" => RouterToolChoice.Auto,
                "none" => RouterToolChoice.None,
                "required" => RouterToolChoice.Required,
                _ => throw new TranslationException("invalid_request", "Invalid tool_choice.")
            };
        }
        if (el.ValueKind == JsonValueKind.Object)
        {
            if (el.TryGetProperty("type", out var ty) && ty.ValueKind == JsonValueKind.String)
            {
                var type = ty.GetString();
                if (type == "function" || type == "custom")
                {
                    if (el.TryGetProperty("name", out var nm) && nm.ValueKind == JsonValueKind.String)
                        return RouterToolChoice.ForTool(nm.GetString()!);
                    if (el.TryGetProperty("function", out var fn) && fn.ValueKind == JsonValueKind.Object && fn.TryGetProperty("name", out var fnm))
                        return RouterToolChoice.ForTool(fnm.GetString()!);
                }
            }
            throw new TranslationException("invalid_request", "Invalid tool_choice object.");
        }
        throw new TranslationException("invalid_request", "Invalid tool_choice.");
    }

    private static (string MediaType, string Base64)? ParseDataUrl(string url)
    {
        if (!url.StartsWith("data:", StringComparison.Ordinal)) return null;
        var comma = url.IndexOf(',');
        if (comma < 0) return null;
        var meta = url.Substring(5, comma - 5); // after data:
        var data = url.Substring(comma + 1);
        var isBase64 = meta.Contains("base64", StringComparison.OrdinalIgnoreCase);
        if (!isBase64) return null;
        var semi = meta.IndexOf(';');
        var media = semi >= 0 ? meta.Substring(0, semi) : meta;
        if (string.IsNullOrEmpty(media)) media = "image/png";
        return (media, data);
    }

    public static byte[] Encode(RouterRequest request, string nativeModel)
    {
        using var ms = new MemoryStream();
        using var writer = new Utf8JsonWriter(ms);
        writer.WriteStartObject();
        writer.WriteString("model", nativeModel);
        if (!string.IsNullOrEmpty(request.SystemPrompt))
            writer.WriteString("instructions", request.SystemPrompt);
        writer.WritePropertyName("input");
        writer.WriteStartArray();
        foreach (var msg in request.Messages)
        {
            if (msg.Role == RouterMessage.User)
            {
                writer.WriteStartObject();
                writer.WriteString("type", "message");
                writer.WriteString("role", "user");
                writer.WritePropertyName("content");
                writer.WriteStartArray();
                foreach (var part in msg.Parts)
                {
                    switch (part)
                    {
                        case RouterTextPart t:
                            writer.WriteStartObject();
                            writer.WriteString("type", "input_text");
                            writer.WriteString("text", t.Text);
                            writer.WriteEndObject();
                            break;
                        case RouterImagePart img:
                            writer.WriteStartObject();
                            writer.WriteString("type", "input_image");
                            if (img.IsUrl && img.Url != null)
                                writer.WriteString("image_url", img.Url);
                            else if (img.IsBase64 && img.Base64Data != null)
                            {
                                var mt = img.MediaType ?? "image/png";
                                writer.WriteString("image_url", $"data:{mt};base64,{img.Base64Data}");
                            }
                            else
                                writer.WriteString("image_url", "");
                            writer.WriteEndObject();
                            break;
                        case RouterToolResultPart tr:
                            break;
                    }
                }
                writer.WriteEndArray();
                writer.WriteEndObject();
            }
            else if (msg.Role == RouterMessage.Assistant)
            {
                var textParts = msg.Parts.OfType<RouterTextPart>().ToList();
                var callParts = msg.Parts.OfType<RouterToolCallPart>().ToList();
                if (textParts.Count > 0)
                {
                    writer.WriteStartObject();
                    writer.WriteString("type", "message");
                    writer.WriteString("role", "assistant");
                    writer.WritePropertyName("content");
                    writer.WriteStartArray();
                    foreach (var t in textParts)
                    {
                        writer.WriteStartObject();
                        writer.WriteString("type", "output_text");
                        writer.WriteString("text", t.Text);
                        writer.WriteEndObject();
                    }
                    writer.WriteEndArray();
                    writer.WriteEndObject();
                }
                foreach (var call in callParts)
                {
                    bool isCustom = request.CustomToolNames.Contains(call.Name);
                    if (isCustom)
                    {
                        writer.WriteStartObject();
                        writer.WriteString("type", "custom_tool_call");
                        writer.WriteString("call_id", call.Id);
                        writer.WriteString("name", call.Name);
                        string input = ExtractCustomInput(call.ArgumentsJson);
                        writer.WriteString("input", input);
                        writer.WriteEndObject();
                    }
                    else
                    {
                        writer.WriteStartObject();
                        writer.WriteString("type", "function_call");
                        writer.WriteString("call_id", call.Id);
                        writer.WriteString("name", call.Name);
                        writer.WriteString("arguments", call.ArgumentsJson);
                        writer.WriteEndObject();
                    }
                }
            }
            else if (msg.Role == RouterMessage.Tool)
            {
                foreach (var part in msg.Parts)
                {
                    if (part is RouterToolResultPart tr)
                    {
                        bool isCustom = false;
                        string? customName = null;
                        foreach (var m in request.Messages)
                        {
                            foreach (var p in m.Parts)
                            {
                                if (p is RouterToolCallPart c && c.Id == tr.CallId && request.CustomToolNames.Contains(c.Name))
                                {
                                    isCustom = true;
                                    customName = c.Name;
                                    break;
                                }
                            }
                        }
                        if (isCustom)
                        {
                            writer.WriteStartObject();
                            writer.WriteString("type", "custom_tool_call_output");
                            writer.WriteString("call_id", tr.CallId);
                            writer.WriteString("output", tr.Text);
                            writer.WriteEndObject();
                        }
                        else
                        {
                            writer.WriteStartObject();
                            writer.WriteString("type", "function_call_output");
                            writer.WriteString("call_id", tr.CallId);
                            writer.WriteString("output", tr.Text);
                            writer.WriteEndObject();
                        }
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
                bool isCustom = request.CustomToolNames.Contains(tool.Name);
                if (isCustom)
                {
                    writer.WriteStartObject();
                    writer.WriteString("type", "custom");
                    writer.WriteString("name", tool.Name);
                    if (!string.IsNullOrEmpty(tool.Description))
                        writer.WriteString("description", tool.Description);
                    writer.WriteEndObject();
                }
                else
                {
                    writer.WriteStartObject();
                    writer.WriteString("type", "function");
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
                }
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
                writer.WriteString("name", tc.Name!);
                writer.WriteEndObject();
            }
        }

        if (request.MaxOutputTokens.HasValue)
            writer.WriteNumber("max_output_tokens", request.MaxOutputTokens.Value);
        if (request.Temperature.HasValue)
            writer.WriteNumber("temperature", request.Temperature.Value);
        if (request.TopP.HasValue)
            writer.WriteNumber("top_p", request.TopP.Value);
        if (request.ParallelToolCalls.HasValue)
            writer.WriteBoolean("parallel_tool_calls", request.ParallelToolCalls.Value);
        if (!string.IsNullOrEmpty(request.ReasoningEffort))
        {
            writer.WritePropertyName("reasoning");
            writer.WriteStartObject();
            writer.WriteString("effort", request.ReasoningEffort);
            writer.WriteEndObject();
        }
        writer.WriteBoolean("store", false);
        writer.WriteBoolean("stream", request.Stream);

        writer.WriteEndObject();
        writer.Flush();
        return ms.ToArray();
    }

    private static string ExtractCustomInput(string argsJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(argsJson);
            if (doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("input", out var inp) && inp.ValueKind == JsonValueKind.String)
                return inp.GetString() ?? argsJson;
            return argsJson;
        }
        catch
        {
            return argsJson;
        }
    }
}
