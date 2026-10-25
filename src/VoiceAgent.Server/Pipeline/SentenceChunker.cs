using System.Text;

namespace VoiceAgent.Server.Pipeline;

public sealed class SentenceChunker(int maxChars = 90, int minChars = 20, int firstChunkMax = 36)
{
    private readonly StringBuilder _buffer = new();
    private bool _firstEmitted;

    public IReadOnlyList<string> Push(string delta)
    {
        var chunks = new List<string>();

        foreach (var ch in delta)
        {
            _buffer.Append(ch);

            if (ch is '.' or '!' or '?')
            {
                Emit(chunks);
            }
            else if (ch is ',' or ';' or ':' && _buffer.Length >= minChars)
            {
                Emit(chunks);
            }
            else if (!_firstEmitted && firstChunkMax > 0 && _buffer.Length >= Math.Min(firstChunkMax, maxChars))
            {
                // The first chunk sets the perceived latency. Emit it early, at a word boundary,
                // instead of waiting for a punctuation mark or the full maxChars.
                EmitAtWordBoundary(chunks, Math.Min(firstChunkMax, maxChars));
            }
            else if (_buffer.Length >= maxChars)
            {
                EmitAtWordBoundary(chunks, maxChars);
            }
        }

        return chunks;
    }

    public string Flush()
    {
        var text = _buffer.ToString().Trim();
        _buffer.Clear();
        return text;
    }

    private void Emit(List<string> chunks)
    {
        var text = _buffer.ToString().Trim();
        _buffer.Clear();
        _firstEmitted = true;

        if (text.Length > 0)
            chunks.Add(text);
    }

    private void EmitAtWordBoundary(List<string> chunks, int limit)
    {
        var text = _buffer.ToString();
        var split = text.LastIndexOf(' ');
        var hardCut = split < minChars;

        if (hardCut)
            split = Math.Min(text.Length, limit);

        _buffer.Clear();
        if (split < text.Length)
            _buffer.Append(text[hardCut ? split.. : (split + 1)..]);

        var chunk = text[..split].Trim();
        if (chunk.Length > 0)
        {
            _firstEmitted = true;
            chunks.Add(chunk);
        }
    }
}
