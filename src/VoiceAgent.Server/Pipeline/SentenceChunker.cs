using System.Text;

namespace VoiceAgent.Server.Pipeline;

public sealed class SentenceChunker(int maxChars = 90, int minChars = 20)
{
    private readonly StringBuilder _buffer = new();

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
            else if (_buffer.Length >= maxChars)
            {
                EmitAtWordBoundary(chunks);
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

        if (text.Length > 0)
            chunks.Add(text);
    }

    private void EmitAtWordBoundary(List<string> chunks)
    {
        var text = _buffer.ToString();
        var split = text.LastIndexOf(' ');

        if (split <= minChars)
            split = text.Length;

        _buffer.Clear();
        _buffer.Append(text[(split + 1)..]);

        var chunk = text[..split].Trim();
        if (chunk.Length > 0)
            chunks.Add(chunk);
    }
}
