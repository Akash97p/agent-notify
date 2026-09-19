using System.Text;

namespace AgentNotify.Router.Translation;

/// <summary>Translates requests, responses and streams between wires.</summary>
public sealed class RouterTranslator
{
    public static bool IsPassthrough(string inboundWire, string upstreamWire) =>
        string.Equals(inboundWire, upstreamWire, StringComparison.Ordinal);

    public DecodeResult DecodeRequest(string wire, ReadOnlySpan<byte> body)
    {
        return wire switch
        {
            RouterWire.OpenAiResponses => RouterResponsesCodec.Decode(body),
            RouterWire.OpenAiChat => RouterChatCodec.Decode(body),
            RouterWire.AnthropicMessages => RouterAnthropicCodec.Decode(body),
            _ => throw new TranslationException("invalid_request", "Unknown wire.")
        };
    }

    public byte[] EncodeRequest(string wire, RouterRequest request, string nativeModel)
    {
        return wire switch
        {
            RouterWire.OpenAiResponses => RouterResponsesCodec.Encode(request, nativeModel),
            RouterWire.OpenAiChat => RouterChatCodec.Encode(request, nativeModel),
            RouterWire.AnthropicMessages => RouterAnthropicCodec.Encode(request, nativeModel),
            _ => throw new TranslationException("invalid_request", "Unknown wire.")
        };
    }

    public IRouterStreamParser CreateStreamParser(string wire)
    {
        return wire switch
        {
            RouterWire.OpenAiResponses => new ResponsesStreamParserAdapter(),
            RouterWire.OpenAiChat => new ChatStreamParserAdapter(),
            RouterWire.AnthropicMessages => new AnthropicStreamParserAdapter(),
            _ => throw new TranslationException("invalid_request", "Unknown wire.")
        };
    }

    public IRouterStreamWriter CreateStreamWriter(string inboundWire, string clientModel, RouterRequest? request)
    {
        return inboundWire switch
        {
            RouterWire.OpenAiResponses => new ResponsesStreamWriterAdapter(clientModel, request),
            RouterWire.OpenAiChat => new ChatStreamWriterAdapter(clientModel, request),
            RouterWire.AnthropicMessages => new AnthropicStreamWriterAdapter(clientModel, request),
            _ => throw new TranslationException("invalid_request", "Unknown wire.")
        };
    }

    public RouterResponse ParseResponse(string wire, ReadOnlySpan<byte> body)
    {
        return wire switch
        {
            RouterWire.OpenAiResponses => RouterNonStreamingCodec.ParseResponses(body),
            RouterWire.OpenAiChat => RouterNonStreamingCodec.ParseChat(body),
            RouterWire.AnthropicMessages => RouterNonStreamingCodec.ParseAnthropic(body),
            _ => throw new TranslationException("invalid_request", "Unknown wire.")
        };
    }

    public byte[] WriteResponse(string inboundWire, RouterResponse response, string clientModel, RouterRequest? request)
    {
        return inboundWire switch
        {
            RouterWire.OpenAiResponses => RouterNonStreamingCodec.WriteResponses(response, clientModel, request),
            RouterWire.OpenAiChat => RouterNonStreamingCodec.WriteChat(response, clientModel, request),
            RouterWire.AnthropicMessages => RouterNonStreamingCodec.WriteAnthropic(response, clientModel, request),
            _ => throw new TranslationException("invalid_request", "Unknown wire.")
        };
    }

    public byte[] WriteErrorBody(string wire, int status, string code, string message)
    {
        return wire switch
        {
            RouterWire.OpenAiResponses => RouterNonStreamingCodec.WriteErrorResponses(status, code, message),
            RouterWire.OpenAiChat => RouterNonStreamingCodec.WriteErrorChat(status, code, message),
            RouterWire.AnthropicMessages => RouterNonStreamingCodec.WriteErrorAnthropic(status, code, message),
            _ => throw new TranslationException("invalid_request", "Unknown wire.")
        };
    }
}

/// <summary>Parses an upstream SSE stream.</summary>
public interface IRouterStreamParser
{
    IReadOnlyList<RouterStreamEvent> Feed(ReadOnlySpan<byte> chunk);
    IReadOnlyList<RouterStreamEvent> Complete();
}

/// <summary>Writes a client SSE stream.</summary>
public interface IRouterStreamWriter
{
    byte[] Write(RouterStreamEvent ev);
    byte[] WriteError(string code, string message);
    byte[] Complete();
}

internal sealed class ResponsesStreamParserAdapter : IRouterStreamParser
{
    private readonly RouterResponsesStreamParser _inner = new();
    public IReadOnlyList<RouterStreamEvent> Feed(ReadOnlySpan<byte> chunk) => _inner.Feed(chunk);
    public IReadOnlyList<RouterStreamEvent> Complete() => _inner.Complete();
}

internal sealed class ChatStreamParserAdapter : IRouterStreamParser
{
    private readonly RouterChatStreamParser _inner = new();
    public IReadOnlyList<RouterStreamEvent> Feed(ReadOnlySpan<byte> chunk) => _inner.Feed(chunk);
    public IReadOnlyList<RouterStreamEvent> Complete() => _inner.Complete();
}

internal sealed class AnthropicStreamParserAdapter : IRouterStreamParser
{
    private readonly RouterAnthropicStreamParser _inner = new();
    public IReadOnlyList<RouterStreamEvent> Feed(ReadOnlySpan<byte> chunk) => _inner.Feed(chunk);
    public IReadOnlyList<RouterStreamEvent> Complete() => _inner.Complete();
}

internal sealed class ResponsesStreamWriterAdapter : IRouterStreamWriter
{
    private readonly RouterResponsesStreamWriter _inner;
    public ResponsesStreamWriterAdapter(string model, RouterRequest? req) => _inner = new RouterResponsesStreamWriter(model, req);
    public byte[] Write(RouterStreamEvent ev) => _inner.Write(ev);
    public byte[] WriteError(string code, string message) => _inner.WriteError(code, message);
    public byte[] Complete() => _inner.Complete();
}

internal sealed class ChatStreamWriterAdapter : IRouterStreamWriter
{
    private readonly RouterChatStreamWriter _inner;
    public ChatStreamWriterAdapter(string model, RouterRequest? req) => _inner = new RouterChatStreamWriter(model, req);
    public byte[] Write(RouterStreamEvent ev) => _inner.Write(ev);
    public byte[] WriteError(string code, string message) => _inner.WriteError(code, message);
    public byte[] Complete() => _inner.Complete();
}

internal sealed class AnthropicStreamWriterAdapter : IRouterStreamWriter
{
    private readonly RouterAnthropicStreamWriter _inner;
    public AnthropicStreamWriterAdapter(string model, RouterRequest? req) => _inner = new RouterAnthropicStreamWriter(model, req);
    public byte[] Write(RouterStreamEvent ev) => _inner.Write(ev);
    public byte[] WriteError(string code, string message) => _inner.WriteError(code, message);
    public byte[] Complete() => _inner.Complete();
}
