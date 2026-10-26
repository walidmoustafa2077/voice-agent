using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using VoiceAgent.Server.Audio;

namespace VoiceAgent.Server.Pipeline;

/// <summary>
/// The built-in Windows SAPI voice, rendered to a temp WAV. On a CPU it is roughly two orders of
/// magnitude faster than Kokoro (about 0.01x realtime), at the cost of a dated-sounding voice.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class SapiTtsService : ITtsService, IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly string? _voiceName;
    private bool _disposed;

    public SapiTtsService(string? voiceName = null)
    {
        _voiceName = voiceName;
    }

    public async Task<TtsChunk> SynthesizeAsync(string text, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ct.ThrowIfCancellationRequested();

            // SAPI's Speak() is a blocking COM call; keep it off the SignalR and pipeline threads.
            return await Task.Run(() => Render(text, ct), ct).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private TtsChunk Render(string text, CancellationToken ct)
    {
        var path = Path.Combine(Path.GetTempPath(), $"voice-agent-{Guid.NewGuid():N}.wav");

        try
        {
            var voiceType = Type.GetTypeFromProgID("SAPI.SpVoice")
                ?? throw new InvalidOperationException("SAPI.SpVoice is not registered");
            var streamType = Type.GetTypeFromProgID("SAPI.SpFileStream")
                ?? throw new InvalidOperationException("SAPI.SpFileStream is not registered");

            dynamic speaker = Activator.CreateInstance(voiceType)!;
            dynamic stream = Activator.CreateInstance(streamType)!;

            try
            {
                // SSFMCreateForWrite = 3, do not autobind to a playback device.
                stream.Open(path, 3, false);
                speaker.AudioOutputStream = stream;

                if (_voiceName is { Length: > 0 })
                    SelectVoice(speaker, _voiceName);

                speaker.Speak(text);
                ct.ThrowIfCancellationRequested();
            }
            finally
            {
                // The RCW holds the WAV open even after Close(), so release it before reading.
                try { speaker.AudioOutputStream = null; } catch { /* already detached */ }
                try { stream.Close(); } catch { /* already closed */ }

                if (Marshal.IsComObject(stream)) Marshal.FinalReleaseComObject(stream);
                if (Marshal.IsComObject(speaker)) Marshal.FinalReleaseComObject(speaker);
            }

            var wav = ReadWithRetry(path);
            return new TtsChunk(wav, Pcm16Wav.DurationMs(wav));
        }
        finally
        {
            try { File.Delete(path); } catch { /* best effort */ }
        }
    }

    private static void SelectVoice(dynamic speaker, string name)
    {
        foreach (var token in speaker.GetVoices())
        {
            var description = (string)token.GetDescription();
            if (!description.Contains(name, StringComparison.OrdinalIgnoreCase))
                continue;

            speaker.Voice = token;
            return;
        }
    }

    private static byte[] ReadWithRetry(string path)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                return File.ReadAllBytes(path);
            }
            catch (IOException) when (attempt < 20)
            {
                Thread.Sleep(50);
            }
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _gate.Dispose();
    }
}
