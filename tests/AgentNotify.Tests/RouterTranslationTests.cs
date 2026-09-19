using System.Text;
using System.Text.Json;
using AgentNotify.Core.Router;
using AgentNotify.Core.Router.Translation;

namespace AgentNotify.Tests;

public sealed class RouterTranslationTests
{
    // Helpers
    private static byte[] Utf8(string s) => Encoding.UTF8.GetBytes(s);
    private static string Json(object o) => JsonSerializer.Serialize(o);

    // Sample fixtures

    // Codex Responses request with instructions, developer, function_call + output history, custom apply_patch, reasoning dropped, web_search dropped
    private static string CodexResponsesRequestJson()
    {
        return """
        {
          "model": "gpt-5",
          "instructions": "You are a helpful assistant.",
          "input": [
            {"type":"message","role":"system","content":[{"type":"input_text","text":"system from input"}]},
            {"type":"message","role":"developer","content":[{"type":"input_text","text":"developer prompt"}]},
            {"type":"message","role":"user","content":[{"type":"input_text","text":"hello world"}]},
            {"type":"function_call","call_id":"fc_1","name":"my_tool","arguments":"{\"a\":1}"},
            {"type":"function_call_output","call_id":"fc_1","output":"tool result text"},
            {"type":"custom_tool_call","call_id":"ctc_1","name":"apply_patch","input":"*** Begin Patch\n+ hello\n*** End Patch"},
            {"type":"custom_tool_call_output","call_id":"ctc_1","output":"patch applied"},
            {"type":"reasoning","summary":[{"type":"summary_text","text":"thinking"}]},
            {"type":"message","role":"user","content":"plain string content"}
          ],
          "tools": [
            {"type":"function","name":"my_tool","description":"desc","parameters":{"type":"object","properties":{"a":{"type":"number"}},"required":["a"]}},
            {"type":"custom","name":"apply_patch","description":"apply"},
            {"type":"web_search"},
            {"type":"function","name":"other_func","description":"other","parameters":{"type":"object"}}
          ],
          "tool_choice":"auto",
          "stream": true,
          "reasoning":{"effort":"high"},
          "store": false,
          "previous_response_id": "resp_123"
        }
        """;
    }

    private static string ChatRequestJson()
    {
        return """
        {
          "model":"gpt-4o",
          "messages":[
            {"role":"system","content":"system prompt"},
            {"role":"developer","content":"developer prompt"},
            {"role":"user","content":[{"type":"text","text":"hello"},{"type":"image_url","image_url":{"url":"https://example.com/img.png"}}]},
            {"role":"user","content":"plain user text"},
            {"role":"assistant","content":"assistant text","tool_calls":[{"id":"call_1","type":"function","function":{"name":"my_tool","arguments":"{\"a\":1}"}}]},
            {"role":"tool","tool_call_id":"call_1","content":"tool result"}
          ],
          "tools":[{"type":"function","function":{"name":"my_tool","description":"desc","parameters":{"type":"object","properties":{"a":{"type":"number"}}}}}],
          "tool_choice":"auto",
          "max_tokens": 100,
          "temperature": 0.7,
          "top_p": 0.9,
          "stop": ["STOP"],
          "stream": false,
          "reasoning_effort": "high"
        }
        """;
    }

    private static string AnthropicRequestJson()
    {
        return """
        {
          "model":"claude-sonnet-4",
          "system":[{"type":"text","text":"system line1"},{"type":"text","text":"system line2"}],
          "messages":[
            {"role":"user","content":[{"type":"text","text":"before tool"},{"type":"tool_result","tool_use_id":"toolu_1","content":[{"type":"text","text":"result1"}]},{"type":"tool_result","tool_use_id":"toolu_2","content":"result2","is_error":true},{"type":"text","text":"after tool"}]},
            {"role":"assistant","content":[{"type":"text","text":"assistant text"},{"type":"tool_use","id":"toolu_1","name":"my_tool","input":{"a":1}}]},
            {"role":"user","content":"plain text"}
          ],
          "tools":[{"name":"my_tool","description":"desc","input_schema":{"type":"object","properties":{"a":{"type":"number"}}}}],
          "tool_choice":{"type":"tool","name":"my_tool"},
          "max_tokens": 512,
          "temperature": 0.5,
          "top_p": 0.8,
          "stop_sequences": ["STOP"],
          "stream": true
        }
        """;
    }

    // Decoder tests

    [Fact]
    public void Responses_Decoder_CodexRequest()
    {
        var json = CodexResponsesRequestJson();
        var translator = new RouterTranslator();
        var result = translator.DecodeRequest(RouterWire.OpenAiResponses, Utf8(json));
        var req = result.Request;
        var notes = result.DroppedNotes;
        Assert.Equal("gpt-5", req.Model);
        // system merges instructions + system + developer
        Assert.Contains("You are a helpful assistant.", req.SystemPrompt);
        Assert.Contains("system from input", req.SystemPrompt);
        Assert.Contains("developer prompt", req.SystemPrompt);
        // reasoning dropped note?
        Assert.Contains("reasoning_dropped", notes);
        Assert.Contains("tools_dropped", notes);
        Assert.Contains("previous_response_id", notes);
        // custom tool names
        Assert.Contains("apply_patch", req.CustomToolNames);
        // Tools: should have 3 (my_tool, apply_patch, other_func) – web_search dropped
        Assert.Equal(3, req.Tools.Count);
        Assert.True(req.Stream);
        Assert.Equal("high", req.ReasoningEffort);
        // Messages: should contain user hello world, plus function_call, function_call_output, custom, custom_output, and plain string user
        // Find tool calls
        var assistantCalls = req.Messages.Where(m => m.Role == RouterMessage.Assistant).SelectMany(m => m.Parts.OfType<RouterToolCallPart>()).ToList();
        Assert.Contains(assistantCalls, c => c.Name == "my_tool" && c.Id == "fc_1");
        Assert.Contains(assistantCalls, c => c.Name == "apply_patch");
        var toolResults = req.Messages.Where(m => m.Role == RouterMessage.Tool).SelectMany(m => m.Parts.OfType<RouterToolResultPart>()).ToList();
        Assert.Contains(toolResults, r => r.CallId == "fc_1" && r.Text == "tool result text");
        Assert.Contains(toolResults, r => r.CallId == "ctc_1" && r.Text == "patch applied");
        // plain string user message -> should be user with text
        Assert.Contains(req.Messages, m => m.Parts.OfType<RouterTextPart>().Any(p => p.Text == "plain string content"));
        // custom tool args should be {"input": "..."}
        var custom = assistantCalls.First(c => c.Name == "apply_patch");
        using var doc = JsonDocument.Parse(custom.ArgumentsJson);
        Assert.True(doc.RootElement.TryGetProperty("input", out var inp));
        Assert.Contains("Begin Patch", inp.GetString());
    }

    [Fact]
    public void Responses_Decoder_CustomToolOutputStringAndArray()
    {
        var json = """
        {
          "model":"m",
          "input":[
            {"type":"function_call_output","call_id":"c1","output":[{"type":"output_text","text":"hello "},{"type":"input_text","text":"world"}]},
            {"type":"function_call_output","call_id":"c2","output":"plain"}
          ]
        }
        """;
        var translator = new RouterTranslator();
        var res = translator.DecodeRequest(RouterWire.OpenAiResponses, Utf8(json));
        var toolResults = res.Request.Messages.Where(m => m.Role == RouterMessage.Tool).SelectMany(m => m.Parts.OfType<RouterToolResultPart>()).ToList();
        Assert.Equal("hello world", toolResults.First(r => r.CallId == "c1").Text);
        Assert.Equal("plain", toolResults.First(r => r.CallId == "c2").Text);
    }

    [Fact]
    public void Responses_Decoder_InputContentPlainString()
    {
        var json = """{"model":"m","input":[{"type":"message","role":"user","content":"just a string"}]}""";
        var translator = new RouterTranslator();
        var res = translator.DecodeRequest(RouterWire.OpenAiResponses, Utf8(json));
        Assert.Single(res.Request.Messages);
        Assert.Equal("just a string", res.Request.Messages[0].Parts.OfType<RouterTextPart>().Single().Text);
    }

    [Fact]
    public void Responses_Decoder_InvalidShape_Throws()
    {
        var translator = new RouterTranslator();
        Assert.Throws<TranslationException>(() => translator.DecodeRequest(RouterWire.OpenAiResponses, Utf8("""{"model":123}""")));
        Assert.Throws<TranslationException>(() => translator.DecodeRequest(RouterWire.OpenAiResponses, Utf8("""not json""")));
        // tools missing type
        Assert.Throws<TranslationException>(() => translator.DecodeRequest(RouterWire.OpenAiResponses, Utf8("""{"model":"m","tools":[{"name":"x"}]}""")));
    }

    [Fact]
    public void Chat_Decoder()
    {
        var json = ChatRequestJson();
        var translator = new RouterTranslator();
        var res = translator.DecodeRequest(RouterWire.OpenAiChat, Utf8(json));
        var req = res.Request;
        Assert.Equal("gpt-4o", req.Model);
        Assert.Contains("system prompt", req.SystemPrompt);
        Assert.Contains("developer prompt", req.SystemPrompt);
        Assert.Equal("high", req.ReasoningEffort);
        Assert.Equal(100, req.MaxOutputTokens);
        // Check that user image preserved
        var userMsgs = req.Messages.Where(m => m.Role == RouterMessage.User).ToList();
        Assert.Contains(userMsgs, m => m.Parts.OfType<RouterImagePart>().Any(img => img.Url == "https://example.com/img.png"));
        // Assistant tool call
        var assistant = req.Messages.First(m => m.Role == RouterMessage.Assistant);
        var call = assistant.Parts.OfType<RouterToolCallPart>().Single();
        Assert.Equal("call_1", call.Id);
        Assert.Equal("my_tool", call.Name);
        // Tool result
        var toolMsg = req.Messages.First(m => m.Role == RouterMessage.Tool);
        Assert.Equal("tool result", toolMsg.Parts.OfType<RouterToolResultPart>().Single().Text);
        Assert.False(req.Stream);
    }

    [Fact]
    public void Chat_Decoder_InvalidShape_Throws()
    {
        var t = new RouterTranslator();
        Assert.Throws<TranslationException>(() => t.DecodeRequest(RouterWire.OpenAiChat, Utf8("""{"messages":"not array"}""")));
        Assert.Throws<TranslationException>(() => t.DecodeRequest(RouterWire.OpenAiChat, Utf8("""{"messages":[{"role":"invalid","content":"hi"}]}""")));
        Assert.Throws<TranslationException>(() => t.DecodeRequest(RouterWire.OpenAiChat, Utf8("""{"messages":[{"role":"user","content":123}]}""")));
    }

    [Fact]
    public void Anthropic_Decoder()
    {
        var json = AnthropicRequestJson();
        var t = new RouterTranslator();
        var res = t.DecodeRequest(RouterWire.AnthropicMessages, Utf8(json));
        var req = res.Request;
        Assert.Equal("claude-sonnet-4", req.Model);
        Assert.Contains("system line1", req.SystemPrompt);
        Assert.Contains("system line2", req.SystemPrompt);
        // Check consecutive tool_result blocks become separate tool messages
        var toolMessages = req.Messages.Where(m => m.Role == RouterMessage.Tool).ToList();
        Assert.Equal(2, toolMessages.Count);
        Assert.Equal("toolu_1", toolMessages[0].Parts.OfType<RouterToolResultPart>().Single().CallId);
        Assert.Equal("toolu_2", toolMessages[1].Parts.OfType<RouterToolResultPart>().Single().CallId);
        Assert.True(toolMessages[1].Parts.OfType<RouterToolResultPart>().Single().IsError);
        // Text after tool results stays user message after them
        var userTexts = req.Messages.Where(m => m.Role == RouterMessage.User).ToList();
        // Should have: user with "before tool", then two tool messages, then user with "after tool", then assistant, then user plain
        Assert.Contains(userTexts, m => m.Parts.OfType<RouterTextPart>().Any(p => p.Text == "before tool"));
        Assert.Contains(userTexts, m => m.Parts.OfType<RouterTextPart>().Any(p => p.Text == "after tool"));
        // Assistant tool_use
        var assistant = req.Messages.First(m => m.Role == RouterMessage.Assistant);
        Assert.Contains(assistant.Parts.OfType<RouterToolCallPart>().Single().Name, "my_tool");
        Assert.True(req.Stream);
    }

    [Fact]
    public void Anthropic_Decoder_ThinkingDropped()
    {
        var json = """
        {
          "model":"m",
          "messages":[{"role":"user","content":[{"type":"text","text":"hi"},{"type":"thinking","thinking":"secret"},{"type":"redacted_thinking","data":"x"}]}]
        }
        """;
        var t = new RouterTranslator();
        var res = t.DecodeRequest(RouterWire.AnthropicMessages, Utf8(json));
        Assert.Contains("thinking_dropped", res.DroppedNotes);
        Assert.Single(res.Request.Messages);
        Assert.Equal("hi", res.Request.Messages[0].Parts.OfType<RouterTextPart>().Single().Text);
    }

    [Fact]
    public void Anthropic_Decoder_MidConversationSystemJoinsTheSystemPrompt()
    {
        // Claude Code's environment block, sent under the mid-conversation-system beta.
        var json = """
        {
          "model":"m",
          "system":[{"type":"text","text":"base"}],
          "messages":[
            {"role":"user","content":"hi"},
            {"role":"system","content":[{"type":"text","text":"# Environment"}]}
          ]
        }
        """;
        var res = new RouterTranslator().DecodeRequest(RouterWire.AnthropicMessages, Utf8(json));
        Assert.Equal("base\n# Environment", res.Request.SystemPrompt);
        Assert.Single(res.Request.Messages);
        Assert.Equal(RouterMessage.User, res.Request.Messages[0].Role);
    }

    [Fact]
    public void Anthropic_Decoder_DropsBlocksOtherWiresCannotCarry()
    {
        var json = """
        {
          "model":"m",
          "messages":[{"role":"user","content":[{"type":"text","text":"hi"},{"type":"document","source":{"type":"text","data":"x"}}]}]
        }
        """;
        var res = new RouterTranslator().DecodeRequest(RouterWire.AnthropicMessages, Utf8(json));
        Assert.Contains("blocks_dropped", res.DroppedNotes);
        Assert.Equal("hi", res.Request.Messages.Single().Parts.OfType<RouterTextPart>().Single().Text);
    }

    // Encoder tests

    [Fact]
    public void Responses_Encoder_StoreAndStreamFlags()
    {
        var req = new RouterRequest("model-x", "sys", [], [], null, null, null, null, null, true, null, null, new HashSet<string>());
        var bytes = new RouterTranslator().EncodeRequest(RouterWire.OpenAiResponses, req, "native-model");
        using var doc = JsonDocument.Parse(bytes);
        Assert.Equal("native-model", doc.RootElement.GetProperty("model").GetString());
        Assert.False(doc.RootElement.GetProperty("store").GetBoolean());
        Assert.True(doc.RootElement.GetProperty("stream").GetBoolean());
        Assert.Equal("sys", doc.RootElement.GetProperty("instructions").GetString());
    }

    [Fact]
    public void Responses_Encoder_CustomToolMapsCorrectly()
    {
        var req = new RouterRequest("m", null,
            [new RouterMessage(RouterMessage.Assistant, [new RouterToolCallPart("ctc_1","apply_patch","{\"input\":\"patch text\"}")])],
            [new RouterTool("apply_patch","desc", """{"type":"object","properties":{"input":{"type":"string"}},"required":["input"],"additionalProperties":false}""")],
            null, null, null, null, null, false, null, null, new HashSet<string>(["apply_patch"]));
        var bytes = new RouterTranslator().EncodeRequest(RouterWire.OpenAiResponses, req, "native");
        using var doc = JsonDocument.Parse(bytes);
        var tools = doc.RootElement.GetProperty("tools");
        var custom = tools.EnumerateArray().First();
        Assert.Equal("custom", custom.GetProperty("type").GetString());
        var input = doc.RootElement.GetProperty("input");
        var first = input.EnumerateArray().First();
        Assert.Equal("custom_tool_call", first.GetProperty("type").GetString());
        Assert.Equal("patch text", first.GetProperty("input").GetString());
    }

    [Fact]
    public void Responses_Encoder_ToolCallAndResult()
    {
        var req = new RouterRequest("m", null,
            [
                new RouterMessage(RouterMessage.Assistant, [new RouterToolCallPart("call_1","my_tool","{\"a\":1}")]),
                new RouterMessage(RouterMessage.Tool, [new RouterToolResultPart("call_1","result", false)])
            ], [new RouterTool("my_tool", null, "{}")], null, null, null, null, null, false, null, null, new HashSet<string>());
        var bytes = new RouterTranslator().EncodeRequest(RouterWire.OpenAiResponses, req, "native");
        using var doc = JsonDocument.Parse(bytes);
        var input = doc.RootElement.GetProperty("input");
        var arr = input.EnumerateArray().ToList();
        Assert.Equal("function_call", arr[0].GetProperty("type").GetString());
        Assert.Equal("function_call_output", arr[1].GetProperty("type").GetString());
        Assert.Equal("result", arr[1].GetProperty("output").GetString());
    }

    [Fact]
    public void Chat_Encoder_StreamOptions()
    {
        var req = new RouterRequest("m", null, [], [], null, null, null, null, null, true, null, null, new HashSet<string>());
        var bytes = new RouterTranslator().EncodeRequest(RouterWire.OpenAiChat, req, "native");
        using var doc = JsonDocument.Parse(bytes);
        Assert.True(doc.RootElement.GetProperty("stream_options").GetProperty("include_usage").GetBoolean());
        var req2 = new RouterRequest("m", null, [], [], null, null, null, null, null, false, null, null, new HashSet<string>());
        var bytes2 = new RouterTranslator().EncodeRequest(RouterWire.OpenAiChat, req2, "native");
        using var doc2 = JsonDocument.Parse(bytes2);
        Assert.False(doc2.RootElement.TryGetProperty("stream_options", out _));
    }

    [Fact]
    public void Chat_Encoder_MergesAssistantTextAndToolCalls()
    {
        var req = new RouterRequest("m", null,
            [new RouterMessage(RouterMessage.Assistant, [new RouterTextPart("hello"), new RouterToolCallPart("call_1","my_tool","{}")])],
            [], null, null, null, null, null, false, null, null, new HashSet<string>());
        var bytes = new RouterTranslator().EncodeRequest(RouterWire.OpenAiChat, req, "native");
        using var doc = JsonDocument.Parse(bytes);
        var msgs = doc.RootElement.GetProperty("messages").EnumerateArray().ToList();
        var assistant = msgs.First(m => m.GetProperty("role").GetString() == "assistant");
        Assert.Equal("hello", assistant.GetProperty("content").GetString());
        Assert.True(assistant.TryGetProperty("tool_calls", out var tcs));
        Assert.Single(tcs.EnumerateArray());
    }

    [Fact]
    public void Chat_Encoder_ReasoningEffortOnlyWhenSet()
    {
        var req = new RouterRequest("m", null, [], [], null, null, null, null, null, false, "high", null, new HashSet<string>());
        var bytes = new RouterTranslator().EncodeRequest(RouterWire.OpenAiChat, req, "native");
        using var doc = JsonDocument.Parse(bytes);
        Assert.Equal("high", doc.RootElement.GetProperty("reasoning_effort").GetString());
        var req2 = new RouterRequest("m", null, [], [], null, null, null, null, null, false, null, null, new HashSet<string>());
        var bytes2 = new RouterTranslator().EncodeRequest(RouterWire.OpenAiChat, req2, "native");
        using var doc2 = JsonDocument.Parse(bytes2);
        Assert.False(doc2.RootElement.TryGetProperty("reasoning_effort", out _));
    }

    [Fact]
    public void Anthropic_DecoderReadsClaudeCodeOutputEffort()
    {
        var body = Encoding.UTF8.GetBytes("{\"model\":\"claude-opus-5\",\"max_tokens\":10,\"output_config\":{\"effort\":\"xhigh\"},\"messages\":[{\"role\":\"user\",\"content\":\"hi\"}]}");
        var decoded = new RouterTranslator().DecodeRequest(RouterWire.AnthropicMessages, body);
        Assert.Equal("xhigh", decoded.Request.ReasoningEffort);
    }

    [Fact]
    public void EffortCatalogMapsAndDefaultsByModelFamily()
    {
        var upstream = new StoredRouterUpstream("up", "aggregator", "Aggregator", RouterWire.OpenAiChat,
            "https://example.com/v1", null, ["deepseek-v4", "gpt-5"], true, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
        var deepseek = RouterEffortCatalog.Resolve(upstream, "deepseek-v4", []);
        Assert.Equal(["low", "low", "medium", "high", "xhigh"], deepseek.LevelMap);
        Assert.Equal("xhigh", RouterEffortCatalog.Map(deepseek, "max"));

        var saved = new RouterEffortMapping("up", "gpt-5", ["low", "high"],
            ["low", "low", "high", "high", "high"], "high");
        var openai = RouterEffortCatalog.Resolve(upstream, "gpt-5", [saved]);
        Assert.Equal("high", RouterEffortCatalog.Map(openai, null));
        Assert.Equal("high", RouterEffortCatalog.Map(openai, "max"));
    }

    [Fact]
    public void Anthropic_Encoder_DefaultsMaxTokens()
    {
        var req = new RouterRequest("m", null, [new RouterMessage(RouterMessage.User, [new RouterTextPart("hi")])], [], null, null, null, null, null, false, null, null, new HashSet<string>());
        var bytes = new RouterTranslator().EncodeRequest(RouterWire.AnthropicMessages, req, "native");
        using var doc = JsonDocument.Parse(bytes);
        Assert.Equal(8192, doc.RootElement.GetProperty("max_tokens").GetInt32());
        var req2 = new RouterRequest("m", null, [new RouterMessage(RouterMessage.User, [new RouterTextPart("hi")])], [], null, 512, null, null, null, false, null, null, new HashSet<string>());
        var bytes2 = new RouterTranslator().EncodeRequest(RouterWire.AnthropicMessages, req2, "native");
        using var doc2 = JsonDocument.Parse(bytes2);
        Assert.Equal(512, doc2.RootElement.GetProperty("max_tokens").GetInt32());
    }

    [Fact]
    public void Anthropic_Encoder_MergesConsecutiveSameRole()
    {
        var req = new RouterRequest("m", null,
            [
                new RouterMessage(RouterMessage.User, [new RouterTextPart("a")]),
                new RouterMessage(RouterMessage.User, [new RouterTextPart("b")]),
                new RouterMessage(RouterMessage.Assistant, [new RouterTextPart("c")])
            ], [], null, null, null, null, null, false, null, null, new HashSet<string>());
        var bytes = new RouterTranslator().EncodeRequest(RouterWire.AnthropicMessages, req, "native");
        using var doc = JsonDocument.Parse(bytes);
        var msgs = doc.RootElement.GetProperty("messages").EnumerateArray().ToList();
        Assert.Equal(2, msgs.Count);
        // First message should have merged text (two blocks)
        var firstContent = msgs[0].GetProperty("content").EnumerateArray().ToList();
        Assert.Equal(2, firstContent.Count);
    }

    [Fact]
    public void Anthropic_Encoder_ToolResultAndToolUse()
    {
        var req = new RouterRequest("m", null,
            [
                new RouterMessage(RouterMessage.Assistant, [new RouterToolCallPart("toolu_1","my_tool","{\"a\":1}")]),
                new RouterMessage(RouterMessage.Tool, [new RouterToolResultPart("toolu_1","ok", false)])
            ], [], null, null, null, null, null, false, null, null, new HashSet<string>());
        var bytes = new RouterTranslator().EncodeRequest(RouterWire.AnthropicMessages, req, "native");
        using var doc = JsonDocument.Parse(bytes);
        var msgs = doc.RootElement.GetProperty("messages").EnumerateArray().ToList();
        // Should be 2 messages: assistant with tool_use, user with tool_result
        var assistant = msgs[0];
        var toolUse = assistant.GetProperty("content").EnumerateArray().First(c => c.GetProperty("type").GetString() == "tool_use");
        Assert.Equal("toolu_1", toolUse.GetProperty("id").GetString());
        var user = msgs[1];
        var result = user.GetProperty("content").EnumerateArray().First(c => c.GetProperty("type").GetString() == "tool_result");
        Assert.Equal("toolu_1", result.GetProperty("tool_use_id").GetString());
    }

    [Fact]
    public void Anthropic_Encoder_ImageHandling()
    {
        var reqBase64 = new RouterRequest("m", null,
            [new RouterMessage(RouterMessage.User, [new RouterImagePart(null, "abcd", "image/png")])],
            [], null, null, null, null, null, false, null, null, new HashSet<string>());
        var b1 = new RouterTranslator().EncodeRequest(RouterWire.AnthropicMessages, reqBase64, "native");
        using var d1 = JsonDocument.Parse(b1);
        var src1 = d1.RootElement.GetProperty("messages").EnumerateArray().First().GetProperty("content").EnumerateArray().First().GetProperty("source");
        Assert.Equal("base64", src1.GetProperty("type").GetString());

        var reqUrl = new RouterRequest("m", null,
            [new RouterMessage(RouterMessage.User, [new RouterImagePart("https://example.com/img.png", null, null)])],
            [], null, null, null, null, null, false, null, null, new HashSet<string>());
        var b2 = new RouterTranslator().EncodeRequest(RouterWire.AnthropicMessages, reqUrl, "native");
        using var d2 = JsonDocument.Parse(b2);
        var src2 = d2.RootElement.GetProperty("messages").EnumerateArray().First().GetProperty("content").EnumerateArray().First().GetProperty("source");
        Assert.Equal("url", src2.GetProperty("type").GetString());
    }

    [Fact]
    public void Chat_Encoder_ImageHandling()
    {
        var req = new RouterRequest("m", null,
            [new RouterMessage(RouterMessage.User, [new RouterImagePart(null, "abcd", "image/png")])],
            [], null, null, null, null, null, false, null, null, new HashSet<string>());
        var b = new RouterTranslator().EncodeRequest(RouterWire.OpenAiChat, req, "native");
        using var d = JsonDocument.Parse(b);
        var content = d.RootElement.GetProperty("messages").EnumerateArray().First().GetProperty("content").EnumerateArray().First();
        var url = content.GetProperty("image_url").GetProperty("url").GetString()!;
        Assert.StartsWith("data:image/png;base64,", url);
    }

    // Stream parser tests - byte-by-byte vs one chunk

    private static string ChatSseStream()
    {
        // Realistic chat SSE: two text deltas, one tool call in two deltas, finish, usage, DONE
        var chunk1Obj = new { id = "chatcmpl-1", @object = "chat.completion.chunk", created = 123, model = "gpt-4o", choices = new[] { new { delta = new { content = "Hello " }, index = 0, finish_reason = (string?)null } } };
        var chunk2Obj = new { id = "chatcmpl-1", @object = "chat.completion.chunk", created = 123, model = "gpt-4o", choices = new[] { new { delta = new { content = "world" }, index = 0, finish_reason = (string?)null } } };
        var toolStart = new { id = "chatcmpl-1", @object = "chat.completion.chunk", created = 123, model = "gpt-4o", choices = new[] { new { delta = new { tool_calls = new[] { new { index = 0, id = "call_1", function = new { name = "my_tool", arguments = "" } } } }, index = 0, finish_reason = (string?)null } } };
        var toolDelta1 = new { id = "chatcmpl-1", @object = "chat.completion.chunk", created = 123, model = "gpt-4o", choices = new[] { new { delta = new { tool_calls = new[] { new { index = 0, function = new { arguments = "{\"a\":" } } } }, index = 0, finish_reason = (string?)null } } };
        var toolDelta2 = new { id = "chatcmpl-1", @object = "chat.completion.chunk", created = 123, model = "gpt-4o", choices = new[] { new { delta = new { tool_calls = new[] { new { index = 0, function = new { arguments = "1}" } } } }, index = 0, finish_reason = (string?)null } } };
        var finish = new { id = "chatcmpl-1", @object = "chat.completion.chunk", created = 123, model = "gpt-4o", choices = new[] { new { delta = new { }, index = 0, finish_reason = "tool_calls" } } };
        var usage = new { id = "chatcmpl-1", @object = "chat.completion.chunk", created = 123, model = "gpt-4o", choices = Array.Empty<object>(), usage = new { prompt_tokens = 10, completion_tokens = 20, total_tokens = 30, prompt_tokens_details = new { cached_tokens = 2 }, completion_tokens_details = new { reasoning_tokens = 5 } } };
        string ToSse(object o) => $"data: {JsonSerializer.Serialize(o)}\n\n";
        return ToSse(chunk1Obj) + ToSse(chunk2Obj) + ToSse(toolStart) + ToSse(toolDelta1) + ToSse(toolDelta2) + ToSse(finish) + ToSse(usage) + "data: [DONE]\n\n";
    }

    [Fact]
    public void Chat_StreamParser_ByteByByteVsOneChunk()
    {
        var sse = ChatSseStream();
        var bytes = Utf8(sse);
        var t = new RouterTranslator();
        var parser1 = t.CreateStreamParser(RouterWire.OpenAiChat);
        var parser2 = t.CreateStreamParser(RouterWire.OpenAiChat);

        var events1 = new List<RouterStreamEvent>();
        events1.AddRange(parser1.Feed(bytes));
        events1.AddRange(parser1.Complete());

        // Feed byte by byte
        var events2 = new List<RouterStreamEvent>();
        foreach (var b in bytes)
            events2.AddRange(parser2.Feed(new[] { b }));
        events2.AddRange(parser2.Complete());

        Assert.Equal(events1.Count, events2.Count);
        for (int i = 0; i < events1.Count; i++)
            Assert.Equal(events1[i].ToString(), events2[i].ToString());
    }

    [Fact]
    public void Chat_StreamParser_HandlesReasoningAndUsage()
    {
        var sse = ChatSseStream();
        var t = new RouterTranslator();
        var parser = t.CreateStreamParser(RouterWire.OpenAiChat);
        var evs = parser.Feed(Utf8(sse)).ToList();
        // Already covered; also test reasoning delta handling
        var reasoningSse = $"data: {JsonSerializer.Serialize(new { choices = new[] { new { delta = new { reasoning_content = "think" }, index=0, finish_reason=(string?)null } } })}\n\n" + "data: [DONE]\n\n";
        var parser2 = t.CreateStreamParser(RouterWire.OpenAiChat);
        var evs2 = parser2.Feed(Utf8(reasoningSse)).Concat(parser2.Complete()).ToList();
        Assert.Contains(evs2, e => e is ReasoningDeltaEvent r && r.Text == "think");
    }

    [Fact]
    public void Chat_StreamParser_MidUtf8AndMidLine()
    {
        // Test SseReader handles split mid-UTF8 and mid-line
        var sse = "data: {\"choices\":[{\"delta\":{\"content\":\"héllo\"}}]}\n\n";
        var bytes = Utf8(sse);
        // Split inside the UTF8 sequence for é (2 bytes in UTF8)
        // Find position of é
        var parser = new RouterTranslator().CreateStreamParser(RouterWire.OpenAiChat);
        var evs = new List<RouterStreamEvent>();
        // Feed in weird chunks
        evs.AddRange(parser.Feed(bytes.AsSpan(0, 15)));
        evs.AddRange(parser.Feed(bytes.AsSpan(15, 5)));
        evs.AddRange(parser.Feed(bytes.AsSpan(20)));
        evs.AddRange(parser.Complete());
        Assert.Contains(evs, e => e is TextDeltaEvent t && t.Text.Contains("héllo"));
    }

    private static string AnthropicSseStream()
    {
        string ToEvent(string ev, object data) => $"event: {ev}\ndata: {JsonSerializer.Serialize(data)}\n\n";
        var start = new { type = "message_start", message = new { id = "msg_1", type = "message", role = "assistant", content = Array.Empty<object>(), model = "claude", stop_reason = (string?)null, stop_sequence = (string?)null, usage = new { input_tokens = 10, cache_read_input_tokens = 2, output_tokens = 0 } } };
        var blockTextStart = new { type = "content_block_start", index = 0, content_block = new { type = "text", text = "" } };
        var deltaText1 = new { type = "content_block_delta", index = 0, delta = new { type = "text_delta", text = "Hello " } };
        var deltaText2 = new { type = "content_block_delta", index = 0, delta = new { type = "text_delta", text = "world" } };
        var blockStop = new { type = "content_block_stop", index = 0 };
        var toolBlockStart = new { type = "content_block_start", index = 1, content_block = new { type = "tool_use", id = "toolu_1", name = "my_tool", input = new { } } };
        var toolDelta1 = new { type = "content_block_delta", index = 1, delta = new { type = "input_json_delta", partial_json = "{\"a\":" } };
        var toolDelta2 = new { type = "content_block_delta", index = 1, delta = new { type = "input_json_delta", partial_json = "1}" } };
        var toolStop = new { type = "content_block_stop", index = 1 };
        var msgDelta = new { type = "message_delta", delta = new { stop_reason = "tool_use", stop_sequence = (string?)null }, usage = new { output_tokens = 15 } };
        var msgStop = new { type = "message_stop" };
        return ToEvent("message_start", start) + ToEvent("content_block_start", blockTextStart) + ToEvent("content_block_delta", deltaText1) + ToEvent("content_block_delta", deltaText2) + ToEvent("content_block_stop", blockStop) + ToEvent("content_block_start", toolBlockStart) + ToEvent("content_block_delta", toolDelta1) + ToEvent("content_block_delta", toolDelta2) + ToEvent("content_block_stop", toolStop) + ToEvent("message_delta", msgDelta) + ToEvent("message_stop", msgStop);
    }

    [Fact]
    public void Anthropic_StreamParser_ByteByByte()
    {
        var sse = AnthropicSseStream();
        var bytes = Utf8(sse);
        var t = new RouterTranslator();
        var p1 = t.CreateStreamParser(RouterWire.AnthropicMessages);
        var p2 = t.CreateStreamParser(RouterWire.AnthropicMessages);
        var e1 = p1.Feed(bytes).Concat(p1.Complete()).ToList();
        var e2 = new List<RouterStreamEvent>();
        foreach (var b in bytes) e2.AddRange(p2.Feed(new[] { b }));
        e2.AddRange(p2.Complete());
        Assert.Equal(e1.Count, e2.Count);
        for (int i = 0; i < e1.Count; i++) Assert.Equal(e1[i].ToString(), e2[i].ToString());
        Assert.Contains(e1, e => e is TextDeltaEvent td && td.Text == "Hello ");
        Assert.Contains(e1, e => e is ToolCallStartEvent);
        Assert.Contains(e1, e => e is FinishEvent f && f.Reason == FinishEvent.ToolCalls);
        Assert.Contains(e1, e => e is UsageEvent u && u.Usage.InputTokens == 12); // 10+2
    }

    private static string ResponsesSseStream()
    {
        string Ev(string ev, object data) => $"event: {ev}\ndata: {JsonSerializer.Serialize(data)}\n\n";
        var created = new { type = "response.created", sequence_number = 0, response = new { id = "resp_1", @object = "response", created_at = 123, status = "in_progress", model = "gpt-5", output = Array.Empty<object>() } };
        var inProg = new { type = "response.in_progress", sequence_number = 1, response = new { id = "resp_1", @object = "response", created_at = 123, status = "in_progress", model = "gpt-5", output = Array.Empty<object>() } };
        var itemAddedMsg = new { type = "response.output_item.added", sequence_number = 2, output_index = 0, item = new { type = "message", id = "msg_1", role = "assistant", status = "in_progress", content = Array.Empty<object>() } };
        var partAdded = new { type = "response.content_part.added", sequence_number = 3, output_index = 0, content_index = 0, item_id = "msg_1", part = new { type = "output_text", text = "", annotations = Array.Empty<object>() } };
        var delta1 = new { type = "response.output_text.delta", sequence_number = 4, output_index = 0, content_index = 0, item_id = "msg_1", delta = "Hello " };
        var delta2 = new { type = "response.output_text.delta", sequence_number = 5, output_index = 0, content_index = 0, item_id = "msg_1", delta = "world" };
        var doneText = new { type = "response.output_text.done", sequence_number = 6, output_index = 0, content_index = 0, item_id = "msg_1", text = "Hello world" };
        var partDone = new { type = "response.content_part.done", sequence_number = 7, output_index = 0, content_index = 0, item_id = "msg_1", part = new { type = "output_text", text = "Hello world", annotations = Array.Empty<object>() } };
        var itemDoneMsg = new { type = "response.output_item.done", sequence_number = 8, output_index = 0, item = new { type = "message", id = "msg_1", role = "assistant", status = "completed", content = new[] { new { type = "output_text", text = "Hello world", annotations = Array.Empty<object>() } } } };
        var toolAdded = new { type = "response.output_item.added", sequence_number = 9, output_index = 1, item = new { type = "function_call", id = "fc_1", call_id = "call_1", name = "my_tool", arguments = "", status = "in_progress" } };
        var argDelta1 = new { type = "response.function_call_arguments.delta", sequence_number = 10, output_index = 1, item_id = "fc_1", delta = "{\"a\":" };
        var argDelta2 = new { type = "response.function_call_arguments.delta", sequence_number = 11, output_index = 1, item_id = "fc_1", delta = "1}" };
        var argDone = new { type = "response.function_call_arguments.done", sequence_number = 12, output_index = 1, item_id = "fc_1", arguments = "{\"a\":1}" };
        var itemDoneTool = new { type = "response.output_item.done", sequence_number = 13, output_index = 1, item = new { type = "function_call", id = "fc_1", call_id = "call_1", name = "my_tool", arguments = "{\"a\":1}", status = "completed" } };
        var completed = new { type = "response.completed", sequence_number = 14, response = new { id = "resp_1", @object = "response", created_at = 123, status = "completed", model = "gpt-5", output = new object[] { new { type = "message", id = "msg_1", role = "assistant", status = "completed", content = new[] { new { type = "output_text", text = "Hello world", annotations = Array.Empty<object>() } } }, new { type = "function_call", id = "fc_1", call_id = "call_1", name = "my_tool", arguments = "{\"a\":1}", status = "completed" } }, usage = new { input_tokens = 10, input_tokens_details = new { cached_tokens = 2 }, output_tokens = 20, output_tokens_details = new { reasoning_tokens = 5 }, total_tokens = 30 } } };
        return Ev("response.created", created) + Ev("response.in_progress", inProg) + Ev("response.output_item.added", itemAddedMsg) + Ev("response.content_part.added", partAdded) + Ev("response.output_text.delta", delta1) + Ev("response.output_text.delta", delta2) + Ev("response.output_text.done", doneText) + Ev("response.content_part.done", partDone) + Ev("response.output_item.done", itemDoneMsg) + Ev("response.output_item.added", toolAdded) + Ev("response.function_call_arguments.delta", argDelta1) + Ev("response.function_call_arguments.delta", argDelta2) + Ev("response.function_call_arguments.done", argDone) + Ev("response.output_item.done", itemDoneTool) + Ev("response.completed", completed);
    }

    [Fact]
    public void Responses_StreamParser_ByteByByte()
    {
        var sse = ResponsesSseStream();
        var bytes = Utf8(sse);
        var t = new RouterTranslator();
        var p1 = t.CreateStreamParser(RouterWire.OpenAiResponses);
        var p2 = t.CreateStreamParser(RouterWire.OpenAiResponses);
        var e1 = p1.Feed(bytes).Concat(p1.Complete()).ToList();
        var e2 = new List<RouterStreamEvent>();
        foreach (var b in bytes) e2.AddRange(p2.Feed(new[] { b }));
        e2.AddRange(p2.Complete());
        Assert.Equal(e1.Count, e2.Count);
        for (int i = 0; i < e1.Count; i++) Assert.Equal(e1[i].ToString(), e2[i].ToString());
        Assert.Contains(e1, e => e is TextDeltaEvent td && td.Text == "Hello ");
        Assert.Contains(e1, e => e is ToolCallStartEvent s && s.Name == "my_tool");
        Assert.Contains(e1, e => e is ToolCallArgumentsDeltaEvent d && d.JsonDelta.Contains("{\"a\":"));
        Assert.Contains(e1, e => e is UsageEvent u && u.Usage.InputTokens == 10 && u.Usage.CachedInputTokens == 2);
        Assert.Contains(e1, e => e is FinishEvent f && f.Reason == FinishEvent.ToolCalls);
    }

    [Fact]
    public void Responses_StreamParser_CRLF()
    {
        var sse = "event: response.output_text.delta\ndata: {\"type\":\"response.output_text.delta\",\"delta\":\"hi\",\"sequence_number\":0,\"output_index\":0,\"content_index\":0,\"item_id\":\"x\"}\r\n\r\n";
        var bytes = Utf8(sse);
        var t = new RouterTranslator();
        var p = t.CreateStreamParser(RouterWire.OpenAiResponses);
        var evs = p.Feed(bytes).Concat(p.Complete()).ToList();
        Assert.Contains(evs, e => e is TextDeltaEvent);
    }

    // Stream writer round-trip

    private static List<RouterStreamEvent> SampleEventsTextOnly()
    {
        return [new TextDeltaEvent("Hello world"), new UsageEvent(new RouterUsage(10, 2, 20, 5)), new FinishEvent(FinishEvent.Stop)];
    }

    private static List<RouterStreamEvent> SampleEventsToolOnly()
    {
        return [
            new ToolCallStartEvent(0, "call_1", "my_tool"),
            new ToolCallArgumentsDeltaEvent(0, "{\"a\":1}"),
            new ToolCallEndEvent(0),
            new UsageEvent(new RouterUsage(10, null, 5, null)),
            new FinishEvent(FinishEvent.ToolCalls)
        ];
    }

    private static List<RouterStreamEvent> SampleEventsTextAndTwoTools()
    {
        return [
            new TextDeltaEvent("Result: "),
            new ToolCallStartEvent(0, "call_1", "tool_a"),
            new ToolCallArgumentsDeltaEvent(0, "{\"x\":"),
            new ToolCallArgumentsDeltaEvent(0, "1}"),
            new ToolCallEndEvent(0),
            new ToolCallStartEvent(1, "call_2", "tool_b"),
            new ToolCallArgumentsDeltaEvent(1, "{\"y\":2}"),
            new ToolCallEndEvent(1),
            new TextDeltaEvent(" done"),
            new UsageEvent(new RouterUsage(100, 10, 50, null)),
            new FinishEvent(FinishEvent.Stop)
        ];
    }

    [Fact]
    public void Chat_WriterRoundTrip_TextOnly()
    {
        var events = SampleEventsTextOnly();
        var writer = new RouterTranslator().CreateStreamWriter(RouterWire.OpenAiChat, "gpt-4o", null);
        var parser = new RouterTranslator().CreateStreamParser(RouterWire.OpenAiChat);
        byte[] all = [];
        foreach (var e in events) all = Combine(all, writer.Write(e));
        all = Combine(all, writer.Complete());
        var parsed = parser.Feed(all).Concat(parser.Complete()).ToList();
        Assert.Contains(parsed, e => e is TextDeltaEvent td && td.Text == "Hello world");
        Assert.Contains(parsed, e => e is UsageEvent u && u.Usage.InputTokens == 10);
        Assert.Contains(parsed, e => e is FinishEvent f && f.Reason == FinishEvent.Stop);
    }

    [Fact]
    public void Anthropic_WriterRoundTrip_TextOnly()
    {
        var events = SampleEventsTextOnly();
        var writer = new RouterTranslator().CreateStreamWriter(RouterWire.AnthropicMessages, "claude", null);
        var parser = new RouterTranslator().CreateStreamParser(RouterWire.AnthropicMessages);
        byte[] all = [];
        foreach (var e in events) all = Combine(all, writer.Write(e));
        all = Combine(all, writer.Complete());
        var parsed = parser.Feed(all).Concat(parser.Complete()).ToList();
        Assert.Contains(parsed, e => e is TextDeltaEvent td && td.Text == "Hello world");
        Assert.Contains(parsed, e => e is FinishEvent);
    }

    [Fact]
    public void Responses_WriterRoundTrip_TextOnly()
    {
        var events = SampleEventsTextOnly();
        var writer = new RouterTranslator().CreateStreamWriter(RouterWire.OpenAiResponses, "gpt-5", new RouterRequest(null, null, [], [], null, null, null, null, null, false, null, null, new HashSet<string>()));
        var parser = new RouterTranslator().CreateStreamParser(RouterWire.OpenAiResponses);
        byte[] all = [];
        foreach (var e in events) all = Combine(all, writer.Write(e));
        all = Combine(all, writer.Complete());
        var parsed = parser.Feed(all).Concat(parser.Complete()).ToList();
        Assert.Contains(parsed, e => e is TextDeltaEvent td && td.Text == "Hello world");
    }

    [Fact]
    public void Chat_WriterRoundTrip_ToolOnly()
    {
        var events = SampleEventsToolOnly();
        var writer = new RouterTranslator().CreateStreamWriter(RouterWire.OpenAiChat, "gpt-4o", null);
        var parser = new RouterTranslator().CreateStreamParser(RouterWire.OpenAiChat);
        byte[] all = [];
        foreach (var e in events) all = Combine(all, writer.Write(e));
        all = Combine(all, writer.Complete());
        var parsed = parser.Feed(all).Concat(parser.Complete()).ToList();
        var starts = parsed.OfType<ToolCallStartEvent>().ToList();
        Assert.Single(starts);
        Assert.Equal("my_tool", starts[0].Name);
        Assert.Contains(parsed, e => e is ToolCallArgumentsDeltaEvent d && d.JsonDelta.Contains("{\"a\""));
        Assert.Contains(parsed, e => e is FinishEvent f && f.Reason == FinishEvent.ToolCalls);
    }

    [Fact]
    public void Anthropic_WriterRoundTrip_ToolOnly()
    {
        var events = SampleEventsToolOnly();
        var writer = new RouterTranslator().CreateStreamWriter(RouterWire.AnthropicMessages, "claude", null);
        var parser = new RouterTranslator().CreateStreamParser(RouterWire.AnthropicMessages);
        byte[] all = [];
        foreach (var e in events) all = Combine(all, writer.Write(e));
        all = Combine(all, writer.Complete());
        var parsed = parser.Feed(all).Concat(parser.Complete()).ToList();
        Assert.Contains(parsed, e => e is ToolCallStartEvent);
        Assert.Contains(parsed, e => e is FinishEvent f && f.Reason == FinishEvent.ToolCalls);
    }

    [Fact]
    public void Responses_WriterRoundTrip_ToolOnly()
    {
        var events = SampleEventsToolOnly();
        var writer = new RouterTranslator().CreateStreamWriter(RouterWire.OpenAiResponses, "gpt-5", new RouterRequest(null, null, [], [], null, null, null, null, null, false, null, null, new HashSet<string>()));
        var parser = new RouterTranslator().CreateStreamParser(RouterWire.OpenAiResponses);
        byte[] all = [];
        foreach (var e in events) all = Combine(all, writer.Write(e));
        all = Combine(all, writer.Complete());
        var parsed = parser.Feed(all).Concat(parser.Complete()).ToList();
        Assert.Contains(parsed, e => e is ToolCallStartEvent);
        Assert.Contains(parsed, e => e is ToolCallArgumentsDeltaEvent);
    }

    [Fact]
    public void AllWires_WriterRoundTrip_TextAndTwoTools()
    {
        var events = SampleEventsTextAndTwoTools();
        foreach (var wire in new[] { RouterWire.OpenAiChat, RouterWire.AnthropicMessages, RouterWire.OpenAiResponses })
        {
            var req = wire == RouterWire.OpenAiResponses ? new RouterRequest(null, null, [], [], null, null, null, null, null, false, null, null, new HashSet<string>()) : null;
            var writer = new RouterTranslator().CreateStreamWriter(wire, "model", req);
            var parser = new RouterTranslator().CreateStreamParser(wire);
            byte[] all = [];
            foreach (var e in events) all = Combine(all, writer.Write(e));
            all = Combine(all, writer.Complete());
            var parsed = parser.Feed(all).Concat(parser.Complete()).ToList();
            var texts = parsed.OfType<TextDeltaEvent>().Select(t => t.Text).ToList();
            Assert.Contains("Result: ", texts);
            // For Responses writer, second text after tool calls opens new block - our writer does that
            var toolStarts = parsed.OfType<ToolCallStartEvent>().ToList();
            Assert.Equal(2, toolStarts.Count);
            Assert.Contains(parsed, e => e is FinishEvent);
        }
    }

    [Fact]
    public void Writer_ToleratesEventsWithoutPriorStart()
    {
        // Write TextDelta without Start
        var writer = new RouterTranslator().CreateStreamWriter(RouterWire.OpenAiChat, "m", null);
        var bytes = writer.Write(new TextDeltaEvent("hi"));
        Assert.NotEmpty(bytes);
        var parser = new RouterTranslator().CreateStreamParser(RouterWire.OpenAiChat);
        var evs = parser.Feed(bytes).Concat(parser.Complete()).ToList();
        Assert.Contains(evs, e => e is TextDeltaEvent td && td.Text == "hi");
    }

    [Fact]
    public void ReasoningDeltaHandling()
    {
        // Chat writer should forward reasoning, Anthropic drops, Responses drops
        var ev = new ReasoningDeltaEvent("think step");
        var chatWriter = new RouterTranslator().CreateStreamWriter(RouterWire.OpenAiChat, "m", null);
        var chatBytes = chatWriter.Write(ev);
        var chatParser = new RouterTranslator().CreateStreamParser(RouterWire.OpenAiChat);
        var chatParsed = chatParser.Feed(chatBytes).Concat(chatParser.Complete()).ToList();
        Assert.Contains(chatParsed, e => e is ReasoningDeltaEvent r && r.Text == "think step");

        var anthWriter = new RouterTranslator().CreateStreamWriter(RouterWire.AnthropicMessages, "m", null);
        var anthBytes = anthWriter.Write(ev);
        Assert.Empty(anthBytes); // dropped

        var respWriter = new RouterTranslator().CreateStreamWriter(RouterWire.OpenAiResponses, "m", new RouterRequest(null, null, [], [], null, null, null, null, null, false, null, null, new HashSet<string>()));
        var respBytes = respWriter.Write(ev);
        Assert.Empty(respBytes);
    }

    // Usage conversion
    [Fact]
    public void UsageInclusiveExclusiveAnthropic()
    {
        // Anthropic reports input exclusive of cache_read and cache_creation; our IR inclusive
        // Test round-trip: Anthropic JSON with input_tokens 10 + cache_read 2 + cache_creation 3 -> IR Input 15
        var anthJson = """{"id":"msg_1","type":"message","role":"assistant","model":"claude","content":[],"stop_reason":"end_turn","usage":{"input_tokens":10,"cache_read_input_tokens":2,"cache_creation_input_tokens":3,"output_tokens":5}}""";
        var t = new RouterTranslator();
        var resp = t.ParseResponse(RouterWire.AnthropicMessages, Utf8(anthJson));
        Assert.Equal(15, resp.Usage!.InputTokens);
        Assert.Equal(2, resp.Usage.CachedInputTokens);
        // Write back to Anthropic should produce exclusive input_tokens = Input - Cached = 13 (per spec Input inclusive, Cached = cache_read)
        var outBytes = t.WriteResponse(RouterWire.AnthropicMessages, resp, "claude", null);
        using var doc = JsonDocument.Parse(outBytes);
        Assert.Equal(13, doc.RootElement.GetProperty("usage").GetProperty("input_tokens").GetInt32());
        Assert.Equal(2, doc.RootElement.GetProperty("usage").GetProperty("cache_read_input_tokens").GetInt32());
    }

    [Fact]
    public void UsageInclusiveChatAndResponses()
    {
        var chatRespJson = """{"id":"chatcmpl-1","object":"chat.completion","created":123,"model":"gpt-4o","choices":[{"index":0,"message":{"role":"assistant","content":"hi"},"finish_reason":"stop"}],"usage":{"prompt_tokens":10,"completion_tokens":20,"total_tokens":30,"prompt_tokens_details":{"cached_tokens":2},"completion_tokens_details":{"reasoning_tokens":5}}}""";
        var t = new RouterTranslator();
        var parsed = t.ParseResponse(RouterWire.OpenAiChat, Utf8(chatRespJson));
        Assert.Equal(10, parsed.Usage!.InputTokens);
        Assert.Equal(2, parsed.Usage.CachedInputTokens);
        Assert.Equal(5, parsed.Usage.ReasoningTokens);
        // Write to Responses should keep inclusive
        var outBytes = t.WriteResponse(RouterWire.OpenAiResponses, parsed, "gpt-5", null);
        using var doc = JsonDocument.Parse(outBytes);
        Assert.Equal(10, doc.RootElement.GetProperty("usage").GetProperty("input_tokens").GetInt32());
        Assert.Equal(2, doc.RootElement.GetProperty("usage").GetProperty("input_tokens_details").GetProperty("cached_tokens").GetInt32());
        Assert.Equal(5, doc.RootElement.GetProperty("usage").GetProperty("output_tokens_details").GetProperty("reasoning_tokens").GetInt32());

        // Chat cached tokens: prompt_tokens_details.cached_tokens inside prompt_tokens inclusive
        // Already tested
    }

    [Fact]
    public void CustomToolSurfacesAsCustomOnResponses()
    {
        var req = new RouterRequest("m", null,
            [new RouterMessage(RouterMessage.Assistant, [new RouterToolCallPart("call_1","apply_patch","{\"input\":\"patch\"}")])],
            [new RouterTool("apply_patch", null, """{"type":"object","properties":{"input":{"type":"string"}},"required":["input"],"additionalProperties":false}""")],
            null, null, null, null, null, false, null, null, new HashSet<string>(["apply_patch"]));
        var response = new RouterResponse("", "", [new RouterToolCallPart("call_1","apply_patch","{\"input\":\"patch\"}")], FinishEvent.ToolCalls, null);
        var t = new RouterTranslator();
        var bytes = t.WriteResponse(RouterWire.OpenAiResponses, response, "gpt-5", req);
        using var doc = JsonDocument.Parse(bytes);
        var output = doc.RootElement.GetProperty("output").EnumerateArray().ToList();
        var custom = output.First(o => o.GetProperty("type").GetString() == "custom_tool_call");
        Assert.Equal("patch", custom.GetProperty("input").GetString());
        Assert.Equal("call_1", custom.GetProperty("call_id").GetString());

        // Stream writer also
        var writer = t.CreateStreamWriter(RouterWire.OpenAiResponses, "gpt-5", req);
        byte[] all = [];
        all = Combine(all, writer.Write(new ToolCallStartEvent(0, "call_1", "apply_patch")));
        all = Combine(all, writer.Write(new ToolCallArgumentsDeltaEvent(0, "{\"input\":\"patch\"}")));
        all = Combine(all, writer.Write(new ToolCallEndEvent(0)));
        all = Combine(all, writer.Write(new FinishEvent(FinishEvent.ToolCalls)));
        all = Combine(all, writer.Complete());
        var parser = t.CreateStreamParser(RouterWire.OpenAiResponses);
        var evs = parser.Feed(all).Concat(parser.Complete()).ToList();
        // Ensure parser still yields tool calls
        Assert.Contains(evs, e => e is ToolCallStartEvent s && s.Name == "apply_patch");
    }

    // Non-streaming round-trips

    [Fact]
    public void NonStreaming_RoundTrip_AllWires()
    {
        var t = new RouterTranslator();
        var original = new RouterResponse("Hello", "think", [new RouterToolCallPart("call_1","my_tool","{\"a\":1}")], FinishEvent.Stop, new RouterUsage(10, 2, 20, 5));
        foreach (var wire in new[] { RouterWire.OpenAiResponses, RouterWire.OpenAiChat, RouterWire.AnthropicMessages })
        {
            var bytes = t.WriteResponse(wire, original, "model", null);
            var parsed = t.ParseResponse(wire, bytes);
            Assert.Equal(original.Text, parsed.Text);
            if (wire == RouterWire.OpenAiChat)
                Assert.Equal(original.ReasoningText, parsed.ReasoningText);
            else
                Assert.Equal("", parsed.ReasoningText); // dropped for Responses and Anthropic per spec (Responses reasoning not forwarded / Anthropic needs signatures)
            Assert.Single(parsed.ToolCalls);
            Assert.Equal("my_tool", parsed.ToolCalls[0].Name);
            Assert.Equal(original.Usage!.InputTokens, parsed.Usage!.InputTokens);
        }
    }

    [Fact]
    public void NonStreaming_Responses_CustomAndReasoning()
    {
        var req = new RouterRequest(null, null, [], [], null, null, null, null, null, false, null, null, new HashSet<string>(["apply_patch"]));
        var resp = new RouterResponse("text", "", [new RouterToolCallPart("c1","apply_patch","{\"input\":\"x\"}")], FinishEvent.ToolCalls, new RouterUsage(5, null, 10, null));
        var t = new RouterTranslator();
        var bytes = t.WriteResponse(RouterWire.OpenAiResponses, resp, "m", req);
        var parsed = t.ParseResponse(RouterWire.OpenAiResponses, bytes);
        Assert.Single(parsed.ToolCalls);
        Assert.Equal("apply_patch", parsed.ToolCalls[0].Name);
        using var doc = JsonDocument.Parse(parsed.ToolCalls[0].ArgumentsJson);
        Assert.Equal("x", doc.RootElement.GetProperty("input").GetString());
    }

    [Fact]
    public void Passthrough_ReplaceModel_PreservesOtherFields()
    {
        var body = Utf8("""{"model":"old","temperature":0.5,"messages":[{"role":"user","content":"hi"}],"extra":123}""");
        var newBytes = PassthroughBody.ReplaceModel(body, "new-model");
        using var doc = JsonDocument.Parse(newBytes);
        Assert.Equal("new-model", doc.RootElement.GetProperty("model").GetString());
        Assert.Equal(0.5, doc.RootElement.GetProperty("temperature").GetDouble());
        Assert.Equal(123, doc.RootElement.GetProperty("extra").GetInt32());
    }

    [Fact]
    public void Passthrough_ReplaceModel_ThrowsOnInvalid()
    {
        Assert.Throws<TranslationException>(() => PassthroughBody.ReplaceModel(Utf8("not json"), "m"));
    }

    [Fact]
    public void IsPassthrough()
    {
        Assert.True(RouterTranslator.IsPassthrough(RouterWire.OpenAiChat, RouterWire.OpenAiChat));
        Assert.False(RouterTranslator.IsPassthrough(RouterWire.OpenAiChat, RouterWire.AnthropicMessages));
    }

    // Error writers

    [Fact]
    public void ErrorBody_Writers()
    {
        var t = new RouterTranslator();
        var respErr = t.WriteErrorBody(RouterWire.OpenAiResponses, 400, "invalid_request", "bad");
        using var d1 = JsonDocument.Parse(respErr);
        Assert.Equal("bad", d1.RootElement.GetProperty("error").GetProperty("message").GetString());
        Assert.Equal("invalid_request", d1.RootElement.GetProperty("error").GetProperty("code").GetString());

        var chatErr = t.WriteErrorBody(RouterWire.OpenAiChat, 400, "invalid_request", "bad");
        using var d2 = JsonDocument.Parse(chatErr);
        Assert.Equal("bad", d2.RootElement.GetProperty("error").GetProperty("message").GetString());

        var anthErr = t.WriteErrorBody(RouterWire.AnthropicMessages, 400, "invalid_request", "bad");
        using var d3 = JsonDocument.Parse(anthErr);
        Assert.Equal("error", d3.RootElement.GetProperty("type").GetString());
        Assert.Equal("bad", d3.RootElement.GetProperty("error").GetProperty("message").GetString());
    }

    [Fact]
    public void StreamWriter_WriteError()
    {
        var t = new RouterTranslator();
        var w1 = t.CreateStreamWriter(RouterWire.OpenAiChat, "m", null);
        var b1 = w1.WriteError("invalid_request", "bad");
        var s1 = Encoding.UTF8.GetString(b1);
        Assert.Contains("error", s1);

        var w2 = t.CreateStreamWriter(RouterWire.AnthropicMessages, "m", null);
        var b2 = w2.WriteError("invalid_request", "bad");
        var s2 = Encoding.UTF8.GetString(b2);
        Assert.Contains("event: error", s2);

        var w3 = t.CreateStreamWriter(RouterWire.OpenAiResponses, "m", new RouterRequest(null, null, [], [], null, null, null, null, null, false, null, null, new HashSet<string>()));
        var b3 = w3.WriteError("invalid_request", "bad");
        var s3 = Encoding.UTF8.GetString(b3);
        Assert.Contains("response.failed", s3);
    }

    // UsageTap tests

    [Fact]
    public void UsageTap_ChatStream()
    {
        var sse = ChatSseStream();
        var tap = new UsageTap(RouterWire.OpenAiChat);
        tap.Feed(Utf8(sse));
        tap.Complete();
        var usage = tap.GetUsage();
        Assert.NotNull(usage);
        Assert.Equal(10, usage!.InputTokens);
        Assert.Equal(2, usage.CachedInputTokens);
    }

    [Fact]
    public void UsageTap_AnthropicStream()
    {
        var sse = AnthropicSseStream();
        var tap = new UsageTap(RouterWire.AnthropicMessages);
        tap.Feed(Utf8(sse));
        tap.Complete();
        var usage = tap.GetUsage();
        Assert.NotNull(usage);
        Assert.Equal(12, usage!.InputTokens);
        Assert.Equal(15, usage.OutputTokens);
    }

    [Fact]
    public void UsageTap_ResponsesStream()
    {
        var sse = ResponsesSseStream();
        var tap = new UsageTap(RouterWire.OpenAiResponses);
        tap.Feed(Utf8(sse));
        tap.Complete();
        var usage = tap.GetUsage();
        Assert.NotNull(usage);
        Assert.Equal(10, usage!.InputTokens);
    }

    [Fact]
    public void UsageTap_NonStreamingJson()
    {
        var json = """{"usage":{"prompt_tokens":5,"completion_tokens":10,"prompt_tokens_details":{"cached_tokens":1}}}""";
        var tap = new UsageTap(RouterWire.OpenAiChat);
        tap.Feed(Utf8(json));
        tap.Complete();
        var usage = tap.GetUsage();
        Assert.Equal(5, usage!.InputTokens);
        Assert.Equal(1, usage.CachedInputTokens);
    }

    // Finish reason mapping

    [Fact]
    public void FinishMapping_RoundTrip()
    {
        var t = new RouterTranslator();
        // Chat stop -> Anthropic end_turn -> IR stop -> back to chat stop
        var chatJson = """{"id":"x","object":"chat.completion","created":1,"model":"m","choices":[{"index":0,"message":{"role":"assistant","content":"hi"},"finish_reason":"stop"}],"usage":{"prompt_tokens":1,"completion_tokens":1}}""";
        var parsedChat = t.ParseResponse(RouterWire.OpenAiChat, Utf8(chatJson));
        Assert.Equal(FinishEvent.Stop, parsedChat.FinishReason);
        var anthBytes = t.WriteResponse(RouterWire.AnthropicMessages, parsedChat, "m", null);
        var parsedAnth = t.ParseResponse(RouterWire.AnthropicMessages, anthBytes);
        Assert.Equal(FinishEvent.Stop, parsedAnth.FinishReason);

        // length -> max_tokens
        var chatLength = """{"id":"x","object":"chat.completion","created":1,"model":"m","choices":[{"index":0,"message":{"role":"assistant","content":"hi"},"finish_reason":"length"}],"usage":{"prompt_tokens":1,"completion_tokens":1}}""";
        var parsedLength = t.ParseResponse(RouterWire.OpenAiChat, Utf8(chatLength));
        Assert.Equal(FinishEvent.Length, parsedLength.FinishReason);
        var anthLenBytes = t.WriteResponse(RouterWire.AnthropicMessages, parsedLength, "m", null);
        using var doc = JsonDocument.Parse(anthLenBytes);
        Assert.Equal("max_tokens", doc.RootElement.GetProperty("stop_reason").GetString());
    }

    // Bug pin: late usage after finish_reason must be present in terminal frame
    [Fact]
    public void LateUsage_ChatFinishThenUsage_ToResponses_HasUsage()
    {
        // Simulate Chat SSE: finish_reason in one chunk, usage-only chunk afterwards (stream_options.include_usage)
        var finishChunk = JsonSerializer.Serialize(new { id = "chatcmpl-1", @object = "chat.completion.chunk", created = 123, model = "gpt-4o", choices = new[] { new { delta = new { }, index = 0, finish_reason = "stop" } } });
        var usageChunk = JsonSerializer.Serialize(new { id = "chatcmpl-1", @object = "chat.completion.chunk", created = 123, model = "gpt-4o", choices = Array.Empty<object>(), usage = new { prompt_tokens = 42, completion_tokens = 7, total_tokens = 49, prompt_tokens_details = new { cached_tokens = 8 } } });
        var sse = $"data: {finishChunk}\n\n" + $"data: {usageChunk}\n\n" + "data: [DONE]\n\n";
        var parser = new RouterTranslator().CreateStreamParser(RouterWire.OpenAiChat);
        var events = parser.Feed(Utf8(sse)).Concat(parser.Complete()).ToList();
        // Verify parser order: finish before usage
        var finishIdx = events.FindIndex(e => e is FinishEvent);
        var usageIdx = events.FindIndex(e => e is UsageEvent);
        Assert.True(finishIdx >= 0 && usageIdx >= 0 && finishIdx < usageIdx);

        var writer = new RouterTranslator().CreateStreamWriter(RouterWire.OpenAiResponses, "gpt-5", new RouterRequest(null, null, [], [], null, null, null, null, null, false, null, null, new HashSet<string>()));
        byte[] outBytes = [];
        foreach (var ev in events) outBytes = Combine(outBytes, writer.Write(ev));
        // FinishEvent alone should not have emitted terminal yet
        var beforeCompleteText = Encoding.UTF8.GetString(outBytes);
        Assert.DoesNotContain("response.completed", beforeCompleteText);
        outBytes = Combine(outBytes, writer.Complete());
        var text = Encoding.UTF8.GetString(outBytes);
        Assert.Contains("response.completed", text);
        // Extract response.completed JSON
        var completedJson = ExtractResponsesCompletedJson(text);
        using var doc = JsonDocument.Parse(completedJson);
        var resp = doc.RootElement.GetProperty("response");
        var usage = resp.GetProperty("usage");
        Assert.Equal(42, usage.GetProperty("input_tokens").GetInt32());
        Assert.Equal(8, usage.GetProperty("input_tokens_details").GetProperty("cached_tokens").GetInt32());
        Assert.Equal(7, usage.GetProperty("output_tokens").GetInt32());
        Assert.Equal(49, usage.GetProperty("total_tokens").GetInt32());

        // Idempotent second Complete returns empty
        var second = writer.Complete();
        Assert.Empty(second);
        // WriteError suppresses later terminal
        var writer2 = new RouterTranslator().CreateStreamWriter(RouterWire.OpenAiResponses, "gpt-5", null);
        writer2.Write(new TextDeltaEvent("hi"));
        writer2.WriteError("upstream_error", "fail");
        var afterErrorComplete = writer2.Complete();
        Assert.Empty(afterErrorComplete);
    }

    [Fact]
    public void LateUsage_ChatFinishThenUsage_ToAnthropic_HasUsage()
    {
        var finishChunk = JsonSerializer.Serialize(new { id = "chatcmpl-1", @object = "chat.completion.chunk", created = 123, model = "gpt-4o", choices = new[] { new { delta = new { }, index = 0, finish_reason = "stop" } } });
        var usageChunk = JsonSerializer.Serialize(new { id = "chatcmpl-1", @object = "chat.completion.chunk", created = 123, model = "gpt-4o", choices = Array.Empty<object>(), usage = new { prompt_tokens = 42, completion_tokens = 7, total_tokens = 49, prompt_tokens_details = new { cached_tokens = 8 } } });
        var sse = $"data: {finishChunk}\n\n" + $"data: {usageChunk}\n\n" + "data: [DONE]\n\n";
        var parser = new RouterTranslator().CreateStreamParser(RouterWire.OpenAiChat);
        var events = parser.Feed(Utf8(sse)).Concat(parser.Complete()).ToList();

        var writer = new RouterTranslator().CreateStreamWriter(RouterWire.AnthropicMessages, "claude", null);
        byte[] outBytes = [];
        foreach (var ev in events) outBytes = Combine(outBytes, writer.Write(ev));
        outBytes = Combine(outBytes, writer.Complete());
        var text = Encoding.UTF8.GetString(outBytes);
        Assert.Contains("message_delta", text);
        Assert.Contains("message_stop", text);
        // message_delta must carry output_tokens 7
        var deltaJson = ExtractAnthropicDeltaJson(text);
        using var doc = JsonDocument.Parse(deltaJson);
        Assert.Equal(7, doc.RootElement.GetProperty("usage").GetProperty("output_tokens").GetInt32());
        Assert.Equal("end_turn", doc.RootElement.GetProperty("delta").GetProperty("stop_reason").GetString());
        Assert.Empty(writer.Complete());
    }

    [Fact]
    public void LateUsage_ChatFinishThenUsage_ToChat_HasUsageBeforeDone()
    {
        var finishChunk = JsonSerializer.Serialize(new { id = "chatcmpl-1", @object = "chat.completion.chunk", created = 123, model = "gpt-4o", choices = new[] { new { delta = new { }, index = 0, finish_reason = "stop" } } });
        var usageChunk = JsonSerializer.Serialize(new { id = "chatcmpl-1", @object = "chat.completion.chunk", created = 123, model = "gpt-4o", choices = Array.Empty<object>(), usage = new { prompt_tokens = 42, completion_tokens = 7, total_tokens = 49, prompt_tokens_details = new { cached_tokens = 8 } } });
        var sse = $"data: {finishChunk}\n\n" + $"data: {usageChunk}\n\n" + "data: [DONE]\n\n";
        var parser = new RouterTranslator().CreateStreamParser(RouterWire.OpenAiChat);
        var events = parser.Feed(Utf8(sse)).Concat(parser.Complete()).ToList();

        var writer = new RouterTranslator().CreateStreamWriter(RouterWire.OpenAiChat, "gpt-4o", null);
        byte[] outBytes = [];
        foreach (var ev in events) outBytes = Combine(outBytes, writer.Write(ev));
        // Before Complete, no terminal yet
        Assert.DoesNotContain("data: [DONE]", Encoding.UTF8.GetString(outBytes));
        outBytes = Combine(outBytes, writer.Complete());
        var text = Encoding.UTF8.GetString(outBytes);
        // Should have finish chunk, then usage chunk, then [DONE]
        Assert.Contains("\"finish_reason\":\"stop\"", text);
        Assert.Contains("\"prompt_tokens\":42", text);
        Assert.Contains("\"cached_tokens\":8", text);
        Assert.Contains("data: [DONE]", text);
        // Ensure usage chunk appears before DONE
        var usageIdx = text.IndexOf("\"prompt_tokens\":42", StringComparison.Ordinal);
        var doneIdx = text.IndexOf("data: [DONE]", StringComparison.Ordinal);
        Assert.True(usageIdx >= 0 && doneIdx > usageIdx);
        Assert.Empty(writer.Complete());
    }

    [Fact]
    public void NoFinishReason_StillProducesTerminalFrame()
    {
        // Stream ends with only text deltas, no finish_reason
        foreach (var wire in new[] { RouterWire.OpenAiChat, RouterWire.AnthropicMessages, RouterWire.OpenAiResponses })
        {
            var req = wire == RouterWire.OpenAiResponses ? new RouterRequest(null, null, [], [], null, null, null, null, null, false, null, null, new HashSet<string>()) : null;
            var writer = new RouterTranslator().CreateStreamWriter(wire, "model", req);
            var b1 = writer.Write(new TextDeltaEvent("hello"));
            Assert.NotEmpty(b1);
            var terminal = writer.Complete();
            var text = Encoding.UTF8.GetString(terminal);
            if (wire == RouterWire.OpenAiChat)
            {
                Assert.Contains("\"finish_reason\":\"stop\"", text);
                Assert.Contains("data: [DONE]", text);
            }
            else if (wire == RouterWire.AnthropicMessages)
            {
                Assert.Contains("message_delta", text);
                Assert.Contains("message_stop", text);
                Assert.Contains("end_turn", text);
            }
            else
            {
                Assert.Contains("response.completed", text);
            }
            Assert.Empty(writer.Complete());
        }
        // Even with no deltas at all, Complete should still emit terminal
        var emptyWriter = new RouterTranslator().CreateStreamWriter(RouterWire.OpenAiChat, "m", null);
        var emptyTerminal = emptyWriter.Complete();
        Assert.Contains("data: [DONE]", Encoding.UTF8.GetString(emptyTerminal));
    }

    private static string ExtractResponsesCompletedJson(string sse)
    {
        foreach (var block in sse.Split("\n\n", StringSplitOptions.RemoveEmptyEntries))
        {
            if (!block.Contains("response.completed")) continue;
            var lines = block.Split('\n');
            foreach (var line in lines)
            {
                if (line.StartsWith("data: "))
                {
                    var json = line.Substring(6).Trim();
                    if (json.StartsWith("{")) return json;
                }
            }
        }
        throw new InvalidOperationException("response.completed not found");
    }

    private static string ExtractAnthropicDeltaJson(string sse)
    {
        foreach (var block in sse.Split("\n\n", StringSplitOptions.RemoveEmptyEntries))
        {
            if (!block.Contains("message_delta")) continue;
            var lines = block.Split('\n');
            foreach (var line in lines)
            {
                if (line.StartsWith("data: "))
                {
                    var json = line.Substring(6).Trim();
                    if (json.StartsWith("{") && json.Contains("message_delta")) return json;
                }
            }
        }
        throw new InvalidOperationException("message_delta not found");
    }

    // Helper combine
    private static byte[] Combine(byte[] a, byte[] b)
    {
        if (a.Length == 0) return b;
        if (b.Length == 0) return a;
        var c = new byte[a.Length + b.Length];
        Buffer.BlockCopy(a, 0, c, 0, a.Length);
        Buffer.BlockCopy(b, 0, c, a.Length, b.Length);
        return c;
    }

    [Fact]
    public void ChatEncoder_MergesTheTwoAssistantMessagesAResponsesTurnDecodesTo()
    {
        // Codex sends a turn's text and its tool call as separate input items, which decodes to two
        // assistant messages; a provider that requires alternating roles rejects that.
        var body = """
            {
              "model": "x",
              "input": [
                { "type": "message", "role": "user", "content": [{ "type": "input_text", "text": "go" }] },
                { "type": "message", "role": "assistant", "content": [{ "type": "output_text", "text": "Running a command." }] },
                { "type": "function_call", "name": "exec_command", "call_id": "call_a", "arguments": "{\"cmd\":\"ls\"}" },
                { "type": "function_call_output", "call_id": "call_a", "output": "a.txt" }
              ]
            }
            """;
        var decoded = RouterResponsesCodec.Decode(System.Text.Encoding.UTF8.GetBytes(body));
        var encoded = System.Text.Json.JsonDocument.Parse(RouterChatCodec.Encode(decoded.Request, "native-model"));
        var roles = encoded.RootElement.GetProperty("messages").EnumerateArray()
            .Select(message => message.GetProperty("role").GetString()).ToList();

        Assert.Equal(["user", "assistant", "tool"], roles);
        var assistant = encoded.RootElement.GetProperty("messages")[1];
        Assert.Equal("Running a command.", assistant.GetProperty("content").GetString());
        Assert.Equal("exec_command", assistant.GetProperty("tool_calls")[0].GetProperty("function").GetProperty("name").GetString());
    }
}
