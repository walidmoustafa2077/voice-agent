using VoiceAgent.Server.Pipeline;

namespace VoiceAgent.Tests.Pipeline;

public sealed class SpokenTermsTests
{
    [Theory]
    [InlineData("Except in what is T-sharp and dotnet.", "Except in what is C# and dotnet.")]
    [InlineData("I write C sharp every day.", "I write C# every day.")]
    [InlineData("do you know c-sharp", "do you know C#")]
    [InlineData("see sharp is a language", "C# is a language")]
    [InlineData("dot net and dot-net", ".NET and .NET")]
    [InlineData("java script and type script", "JavaScript and TypeScript")]
    [InlineData("git hub and my sequel", "GitHub and MySQL")]
    [InlineData("post gres and j son", "PostgreSQL and JSON")]
    [InlineData("open v s code and k8s", "open VS Code and Kubernetes")]
    public void Spoken_forms_are_repaired(string input, string expected)
    {
        Assert.Equal(expected, SpokenTerms.Normalize(input));
    }

    [Theory]
    [InlineData("Tell me a story about the sea.")]
    [InlineData("What is the capital of France?")]
    [InlineData("Yes.")]
    [InlineData("")]
    public void Ordinary_sentences_are_untouched(string text)
    {
        Assert.Equal(text, SpokenTerms.Normalize(text));
    }
}
