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
    public void Flushes_at_the_cap_when_no_punctuation_arrives()
    {
        var chunker = new SentenceChunker();
        var text = string.Join(' ', Enumerable.Repeat("word", 24)); // 119 chars, no terminator

        var chunks = chunker.Push(text);

        Assert.Single(chunks);
        Assert.True(chunks[0].Length <= 90, $"chunk was {chunks[0].Length} chars");
        Assert.NotEmpty(chunker.Flush());
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

        Assert.Single(chunks);
        Assert.Equal(90, chunks[0].Length);
        Assert.Equal(30, chunker.Flush().Length);
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
