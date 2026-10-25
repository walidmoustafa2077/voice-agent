using VoiceAgent.Server.Pipeline;

namespace VoiceAgent.Tests.Pipeline;

public sealed class SentenceChunkerTests
{
    [Fact]
    public void Flushes_on_full_stop_regardless_of_length()
    {
        var chunker = new SentenceChunker();

        var chunks = chunker.Push("Hello there.");

        Assert.Equal(["Hello there."], chunks);
    }

    [Fact]
    public void Does_not_flush_a_short_comma_clause()
    {
        var chunker = new SentenceChunker();

        Assert.Empty(chunker.Push("Well,"));
    }

    [Fact]
    public void Flushes_the_first_chunk_early_at_a_word_boundary()
    {
        var chunker = new SentenceChunker();
        var text = string.Join(' ', Enumerable.Repeat("word", 24)); // 119 chars, no terminator

        var chunks = chunker.Push(text);

        Assert.NotEmpty(chunks);
        Assert.True(chunks[0].Length <= 36, $"first chunk was {chunks[0].Length} chars");
        Assert.DoesNotContain("  ", chunks[0]);
        Assert.NotEmpty(chunker.Flush());
    }

    [Fact]
    public void Flushes_at_the_cap_after_the_first_chunk()
    {
        var chunker = new SentenceChunker();
        _ = chunker.Push(string.Join(' ', Enumerable.Repeat("word", 12))); // consume the first chunk
        var text = string.Join(' ', Enumerable.Repeat("word", 24));       // 119 chars, no terminator

        var chunks = chunker.Push(text);

        Assert.NotEmpty(chunks);
        Assert.All(chunks, c => Assert.True(c.Length <= 90, $"chunk was {c.Length} chars"));
    }

    [Fact]
    public void Flush_emits_the_remainder()
    {
        var chunker = new SentenceChunker();

        Assert.Empty(chunker.Push("and that is all"));
        Assert.Equal("and that is all", chunker.Flush());
        Assert.Equal("", chunker.Flush());
    }

    [Fact]
    public void Cuts_a_long_unbroken_token_without_throwing()
    {
        var chunker = new SentenceChunker();

        var chunks = chunker.Push(new string('a', 120));

        Assert.NotEmpty(chunks);
        Assert.Equal(36, chunks[0].Length);
        Assert.All(chunks, c => Assert.True(c.Length <= 90));
        Assert.Equal(84, chunker.Flush().Length);
    }

    [Fact]
    public void Joins_across_deltas()
    {
        var chunker = new SentenceChunker();

        Assert.Empty(chunker.Push("Hello "));
        var chunks = chunker.Push("there. How");

        Assert.Equal(["Hello there."], chunks);
        Assert.Equal("How", chunker.Flush());
    }
}
