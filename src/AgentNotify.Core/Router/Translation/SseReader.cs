using System.Text;

namespace AgentNotify.Core.Router.Translation;

/// <summary>A single server-sent event.</summary>
public sealed record SseEvent(string? Event, string Data, string? Id);

/// <summary>
/// Incremental SSE parser handling arbitrary byte chunk boundaries including mid-UTF8 and mid-line,
/// CRLF or LF, event:/data: multi-line, comments, blank-line dispatch.
/// </summary>
public sealed class SseReader
{
    private readonly Decoder _decoder = Encoding.UTF8.GetDecoder();
    private readonly StringBuilder _charBuffer = new(); // decoded chars not yet processed into lines
    private readonly StringBuilder _lineBuffer = new(); // chars for current line being accumulated
    private string? _currentEvent;
    private readonly List<string> _currentDataLines = new();
    private string? _currentId;
    private readonly Queue<SseEvent> _events = new();

    /// <summary>
    /// A chunk larger than this is decoded into a rented array rather than the stack. An upstream
    /// can hand us a megabyte in one read, and a stack allocation that size crashes the process.
    /// </summary>
    private const int MaxStackChars = 1024;

    public void Feed(ReadOnlySpan<byte> chunk)
    {
        if (chunk.IsEmpty) return;

        var charCount = _decoder.GetCharCount(chunk, flush: false);
        if (charCount == 0) return;

        if (charCount <= MaxStackChars)
        {
            Span<char> chars = stackalloc char[charCount];
            _decoder.GetChars(chunk, chars, flush: false);
            _charBuffer.Append(chars);
        }
        else
        {
            var rented = System.Buffers.ArrayPool<char>.Shared.Rent(charCount);
            try
            {
                var written = _decoder.GetChars(chunk, rented.AsSpan(0, charCount), flush: false);
                _charBuffer.Append(rented.AsSpan(0, written));
            }
            finally
            {
                System.Buffers.ArrayPool<char>.Shared.Return(rented);
            }
        }

        ProcessCharBuffer();
    }

    public void Complete()
    {
        Span<char> flushChars = stackalloc char[32];
        int count = _decoder.GetChars(ReadOnlySpan<byte>.Empty, flushChars, flush: true);
        if (count > 0)
            _charBuffer.Append(flushChars.Slice(0, count));

        ProcessCharBuffer();
        if (_lineBuffer.Length > 0)
        {
            ProcessLine(_lineBuffer.ToString());
            _lineBuffer.Clear();
        }
        if (_charBuffer.Length > 0)
        {
            var remaining = _charBuffer.ToString();
            _charBuffer.Clear();
            if (remaining.Length > 0)
            {
                ProcessLine(remaining);
            }
        }
        if (_currentDataLines.Count > 0 || _currentEvent is not null)
        {
            Dispatch();
        }
    }

    public bool TryRead(out SseEvent ev)
    {
        if (_events.Count > 0)
        {
            ev = _events.Dequeue();
            return true;
        }
        ev = default!;
        return false;
    }

    public IReadOnlyList<SseEvent> Drain()
    {
        var list = _events.ToList();
        _events.Clear();
        return list;
    }

    private void ProcessCharBuffer()
    {
        var s = _charBuffer.ToString();
        _charBuffer.Clear();

        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            if (c == '\r')
            {
                if (i + 1 < s.Length && s[i + 1] == '\n')
                {
                    ProcessLine(_lineBuffer.ToString());
                    _lineBuffer.Clear();
                    i++; // skip \n
                }
                else
                {
                    ProcessLine(_lineBuffer.ToString());
                    _lineBuffer.Clear();
                }
            }
            else if (c == '\n')
            {
                ProcessLine(_lineBuffer.ToString());
                _lineBuffer.Clear();
            }
            else
            {
                _lineBuffer.Append(c);
            }
        }
    }

    private void ProcessLine(string line)
    {
        if (line.Length == 0)
        {
            if (_currentDataLines.Count > 0 || _currentEvent is not null)
            {
                Dispatch();
            }
            return;
        }

        if (line.StartsWith(":"))
        {
            return;
        }

        string field;
        string value;
        int colon = line.IndexOf(':');
        if (colon < 0)
        {
            field = line;
            value = "";
        }
        else
        {
            field = line.Substring(0, colon);
            value = line.Substring(colon + 1);
            // If value starts with space, strip one leading space
            if (value.Length > 0 && value[0] == ' ')
                value = value.Substring(1);
        }

        switch (field)
        {
            case "event":
                _currentEvent = value;
                break;
            case "data":
                _currentDataLines.Add(value);
                break;
            case "id":
                _currentId = value;
                break;
            case "retry":
                break;
            default:
                break;
        }
    }

    private void Dispatch()
    {
        var data = string.Join("\n", _currentDataLines);
        if (_currentDataLines.Count == 0 && _currentEvent is null)
        {
            return;
        }
        var ev = new SseEvent(_currentEvent, data, _currentId);
        _events.Enqueue(ev);
        _currentEvent = null;
        _currentDataLines.Clear();
    }
}
