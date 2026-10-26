using System.Text.RegularExpressions;

namespace VoiceAgent.Server.Pipeline;

/// <summary>
/// Whisper does not return an empty string for silence — it returns markers like
/// <c>[BLANK_AUDIO]</c> or <c>(silence)</c>. Everything downstream treats cleaned text as truth.
/// </summary>
public static partial class TranscriptCleaner
{
    private const double SilenceRmsThreshold = 0.005;

    public static string Clean(string raw)
    {
        var withoutMarkers = MarkerPattern().Replace(raw, " ");
        var collapsed = WhitespacePattern().Replace(withoutMarkers, " ").Trim();
        return collapsed.Any(char.IsLetterOrDigit) ? collapsed : "";
    }

    public static bool IsSilent(short[] pcm) => Rms(pcm) < SilenceRmsThreshold;

    /// <summary>
    /// Streaming transducers return all caps with no punctuation. Turn that into something the
    /// language model and the transcript pane can read: lowercase it when it is shouting, capitalise
    /// the first letter, and end with a full stop.
    /// </summary>
    public static string SentenceCase(string text)
    {
        var trimmed = text.Trim();
        if (trimmed.Length == 0) return "";

        if (trimmed.Any(char.IsLower) == false && trimmed.Any(char.IsUpper))
            trimmed = trimmed.ToLowerInvariant();

        var chars = trimmed.ToCharArray();
        if (char.IsLetter(chars[0]))
            chars[0] = char.ToUpperInvariant(chars[0]);

        var result = new string(chars);
        return result[^1] is '.' or '!' or '?' ? result : result + ".";
    }

    private static double Rms(short[] pcm)
    {
        if (pcm.Length == 0) return 0;

        double sum = 0;
        foreach (var sample in pcm) sum += (double)sample * sample;
        return Math.Sqrt(sum / pcm.Length) / 32768.0;
    }

    [GeneratedRegex(@"\[[^\]]*\]|\([^)]*\)")]
    private static partial Regex MarkerPattern();

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespacePattern();
}
