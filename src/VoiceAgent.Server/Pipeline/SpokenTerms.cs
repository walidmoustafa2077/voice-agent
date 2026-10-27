using System.Text.RegularExpressions;

namespace VoiceAgent.Server.Pipeline;

/// <summary>
/// Repairs spoken forms that a recognizer writes literally: "C sharp" and the common mishearing
/// "T sharp" become "C#", "dot net" becomes ".NET". An acoustic model hears phones and has no way
/// to know that a term is code, so this is the one place where a small, explicit list beats a
/// bigger model. Rules are deliberately unambiguous, so ordinary sentences are left alone.
/// </summary>
public static partial class SpokenTerms
{
    private static readonly (Regex Pattern, string Replacement)[] Rules =
    [
        // C# first: "T sharp" is the mishearing that prompted this, and it must not survive.
        (Rule(@"\b(?:t|c|see|sea)[\s-]+sharp\b"), "C#"),
        (Rule(@"\bdot[\s-]+net\b"), ".NET"),
        (Rule(@"\bjava[\s-]*script\b"), "JavaScript"),
        (Rule(@"\btype[\s-]*script\b"), "TypeScript"),
        (Rule(@"\bgit[\s-]+hub\b"), "GitHub"),
        (Rule(@"\bmy[\s-]+sequel\b"), "MySQL"),
        (Rule(@"\bpost[\s-]+gres(?:ql)?\b"), "PostgreSQL"),
        (Rule(@"\bj[\s-]+son\b"), "JSON"),
        (Rule(@"\bv[\s-]+s[\s-]+code\b"), "VS Code"),
        (Rule(@"\bk[\s-]*8[\s-]*s\b"), "Kubernetes"),
    ];

    /// <summary>Rewrites known spoken forms; everything else is returned unchanged.</summary>
    public static string Normalize(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return text;

        var result = text;
        foreach (var (pattern, replacement) in Rules)
            result = pattern.Replace(result, replacement);

        return result;
    }

    private static Regex Rule(string pattern) =>
        new(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
}
