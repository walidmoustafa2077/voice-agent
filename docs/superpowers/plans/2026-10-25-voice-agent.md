# Voice Agent Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** A local, interruptible voice chat: browser mic → Silero VAD → Whisper → gemma via Ollama → Kokoro, with barge-in.

**Architecture:** One ASP.NET Core app serves a static page and a SignalR hub. The browser only captures and plays audio. The server owns the cascade, a per-turn `CancellationToken`, and the turn state machine.

**Tech Stack:** .NET 10, ASP.NET Core + SignalR (MessagePack), OllamaSharp 5.5.0, Whisper.net 1.9.1, Microsoft.ML.OnnxRuntime, KokoroSharp.CPU 0.8.4, vanilla JS + AudioWorklet.

**Spec:** `docs/superpowers/specs/2026-10-25-voice-agent-design.md`

## Global Constraints

- Target `net10.0`. SDK 10.0.302 is what's installed.
- **CPU only.** No CUDA, DirectML, or GPU runtime packages.
- Browser support: **Chrome or Edge only**.
- Audio in: 16 kHz mono PCM16 LE after the server resampler. One message = **20 ms at the
  capture rate**: 320 samples @16k, 882 @44.1k, 960 @48k. Never assume 640 bytes.
- All speech models are ours and local. Only the LLM is remote (`gemma4:31b-cloud` via Ollama).
- No npm, no bundler, no frontend framework.
- Localhost only, no auth, no TLS, no persistence.
- Pinned packages: `OllamaSharp` 5.5.0, `Whisper.net` 1.9.1, `Whisper.net.Runtime` 1.9.1,
  `KokoroSharp.CPU` 0.8.4, `NAudio` 2.x (harness only), `Microsoft.Extensions.TimeProvider.Testing`
  9.x (tests). Unverified until Task 1's restore gate.
- Every duration is measured on a single clock. Never subtract a server timestamp from a client one.
- Sampling rates other than 16 kHz are resampled **on the server**, in tested C#, with state
  carried across frames.
- Naming note: the spec's `VoiceSession` is realised as `VoicePipeline` (orchestration) plus
  `TurnCoordinator` (state and cancellation).

## Review Focus

Five failure modes the spec implies but no happy-path test covers.

1. **Mic runs at 44.1 kHz, or the browser ignores the requested 16 kHz** — must produce clean
   streaming 16 kHz without aliasing or per-frame clicks. Task 1 (`StreamingResamplerTests`),
   including a 20 ms-frame equivalence test and a 10 kHz attenuation test.
2. **`SentencePlayed` never arrives** (throttled tab, dropped frame) — the session must not stay in
   `Speaking` with the raised threshold. Task 3 (`TurnCoordinatorTests`).
3. **User speaks while `Thinking`** — stale turn dies, one merged user message, one transcript.
   Task 7.
4. **Speaker echo trips barge-in** — the agent must not interrupt itself. Task 7
   (`EchoFloorGateTests`) plus the M4 manual run.
5. **Silence or noise reaching Whisper** — Whisper emits `[BLANK_AUDIO]`, not an empty string.
   Task 4 (`SttServiceTests`, `TranscriptCleanerTests`).

---

### Task 1: Scaffold, audio path, M0 gate

**Files:**
- Create: `.gitignore`, `VoiceAgent.sln`
- Create: `src/VoiceAgent.Server/VoiceAgent.Server.csproj`, `Program.cs`
- Create: `src/VoiceAgent.Server/Hubs/VoiceHub.cs`
- Create: `src/VoiceAgent.Server/Audio/StreamingResampler.cs`, `Audio/FrameRateCounter.cs`
- Create: `src/VoiceAgent.Server/wwwroot/index.html`, `app.js`, `pcm-worklet.js`, `style.css`
- Create: `tests/VoiceAgent.Tests/VoiceAgent.Tests.csproj`, `Audio/StreamingResamplerTests.cs`,
  `Audio/FrameRateCounterTests.cs`

**Interfaces:**
- Produces: `StreamingResampler(int inputRate)` with `short[] Process(ReadOnlySpan<short> input)`
  — stateful; biquad state and interpolation phase persist across calls.
- Produces: `FrameRateCounter.Add(int frames)` / `double FramesPerSecond`
- Produces: hub `StartSession(string? systemPrompt, int micSampleRate)`, `SendAudio(byte[] pcm)`

- [ ] **Step 1: Init repo and projects**

```bash
git init
dotnet new sln -n VoiceAgent
dotnet new web -n VoiceAgent.Server -o src/VoiceAgent.Server -f net10.0
dotnet new xunit -n VoiceAgent.Tests -o tests/VoiceAgent.Tests -f net10.0
dotnet sln add src/VoiceAgent.Server tests/VoiceAgent.Tests
dotnet add tests/VoiceAgent.Tests reference src/VoiceAgent.Server
dotnet add src/VoiceAgent.Server package Microsoft.AspNetCore.SignalR.Protocols.MessagePack
dotnet add tests/VoiceAgent.Tests package Microsoft.Extensions.TimeProvider.Testing
```
`.gitignore`: `bin/`, `obj/`, `models/`, `*.user`.

- [ ] **Step 2: Write the failing resampler tests**

```csharp
[Fact]
public void One_shot_44100_to_16000_preserves_a_1khz_tone()
{
    var input = TestSignals.Sine(1000, 44100, seconds: 1.0);
    var output = new StreamingResampler(44100).Process(input);
    Assert.InRange(output.Length, 16000 - 32, 16000 + 32);
    Assert.InRange(Rms(output), Rms(input) * 0.95, Rms(input) * 1.05);
}

[Fact]
public void Streaming_in_twenty_ms_frames_matches_one_shot()
{
    var input = TestSignals.Sine(1000, 44100, seconds: 1.0);
    var oneShot = new StreamingResampler(44100).Process(input);
    var r = new StreamingResampler(44100);
    var streamed = input.Chunk(882).SelectMany(r.Process).ToArray();
    Assert.Equal(oneShot.Length, streamed.Length);
    Assert.InRange(MaxAbsDiff(oneShot, streamed), 0, 2);   // no per-frame discontinuity
}

[Fact]
public void Attenuates_a_10khz_tone_above_the_new_nyquist()
{
    var input = TestSignals.Sine(10000, 48000, seconds: 1.0);
    var output = new StreamingResampler(48000).Process(input);
    Assert.True(Rms(output) < Rms(input) * 0.2);
}
```

- [ ] **Step 3: Run to confirm failure** — `dotnet test --filter StreamingResamplerTests` → compile error.

- [ ] **Step 4: Implement `StreamingResampler`**

Two cascaded biquad low-passes at 7 kHz, coefficients computed once from `inputRate`, `DirectForm`
state fields carried between calls. Interpolation phase (`double`) carried too. Identity when
`inputRate == 16000`. `Process` may emit 0 samples for a short input; the hub does not require
1:1 output per call.

- [ ] **Step 5: Frame-rate counter test** — 50 frames → `FramesPerSecond` in [48, 52].

- [ ] **Step 6: Hub, worklet, page**

`Program.cs`: `AddSignalR().AddMessagePackProtocol()`, `MaximumReceiveMessageSize = 1_048_576`,
`UseStaticFiles`, `MapHub<VoiceHub>("/hub/voice")`, `MapFallbackToFile("index.html")`.

Per-connection state lives in `Context.Items["session"]` — **hubs are transient**, a hub field
would reset on every message. Validate an incoming frame by duration: expected samples =
`micSampleRate / 50`; reject anything else with a clear error.

`pcm-worklet.js`: no resampling. Reads `ctx.sampleRate`, emits 20 ms Float32 blocks.
`app.js`: `getUserMedia({ audio: { echoCancellation: true, noiseSuppression: true,
autoGainControl: true, channelCount: 1 } })`; convert Float32 → Int16; `invoke("SendAudio", bytes)`.
Two contexts: `micCtx` (capture) and `playCtx` (playback, default rate — see Task 6).

- [ ] **Step 7: Restore gate**

Run: `dotnet restore`, `dotnet list package`
Expected: restore succeeds on `net10.0`; all pins resolve. If a pin fails, record the resolving
version in the spec — do not float silently.

- [ ] **Step 8: M0 acceptance**

`dotnet run --project src/VoiceAgent.Server`, open in Chrome, speak.
Expected: level moves, fps reads 50 ± 2, `dotnet test` green.

- [ ] **Step 9: Commit**

```bash
git add -A && git commit -m "feat: M0 audio path, hub, streaming resampler"
```

---

### Task 2: Silero VAD and utterance segmenter

**Files:**
- Create: `src/VoiceAgent.Server/Pipeline/IVadModel.cs`, `SileroVadModel.cs`
- Create: `src/VoiceAgent.Server/Pipeline/SpeechSegmenter.cs`, `VadOptions.cs`
- Create: `tests/VoiceAgent.Tests/Pipeline/SpeechSegmenterTests.cs`, `SileroVadModelTests.cs`
- Create: `tests/VoiceAgent.Tests/Fixtures/` (speech + silence WAVs)

**Interfaces:**
- Produces: `interface IVadModel { float Predict(ReadOnlySpan<short> window512); void Reset(); }`
- Produces: `record SegmentEvent(bool IsStart, short[]? Pcm, long SampleIndex)`
- Produces: `record SegmenterFrame(IReadOnlyList<SegmentEvent> Events, float Probability, bool IsSpeech)`
- Produces: `SpeechSegmenter(IVadModel, VadOptions)` with `SegmenterFrame Push(ReadOnlySpan<short>)`,
  `float Threshold { get; set; }`, `short[] CurrentUtterance()`
- Produces: `VadOptions { float Threshold = 0.5f; int MinSpeechMs = 250; int MinSilenceMs = 600; int PreRollMs = 150; }`

`SegmenterFrame` is what makes barge-in possible: `BargeInGate` needs a per-window decision, not
just start/end events.

- [ ] **Step 1: Segmenter tests**

```csharp
[Fact] public void Emits_start_then_end_after_600ms_of_silence()
[Fact] public void Discards_a_150ms_burst()
[Fact] public void Emitted_pcm_includes_the_150ms_preroll()
[Fact] public void Exposes_per_window_speech_decisions()   // Assert.True(frame.IsSpeech) mid-utterance
[Fact] public void Caps_an_utterance_at_15_seconds()
```

- [ ] **Step 2: Run to confirm failure** — compile error.

- [ ] **Step 3: Implement**

512-sample windows with the remainder carried. Emit `IsStart` on the first window above `Threshold`
once speech persists `MinSpeechMs`. Emit end after `MinSilenceMs` below threshold. Retained PCM
starts `PreRollMs` before speech onset (this buffer replaces the ring buffer — there is exactly one
utterance buffer, owned here). `Threshold` is mutable for the 0.60 mode.

- [ ] **Step 4: Silero integration test**

```csharp
[Fact]
public async Task Speech_scores_higher_than_silence()
{
    // skips when models/silero_vad.onnx is absent
    using var m = new SileroVadModel(ModelPaths.SileroVad);
    Assert.True(m.MaxProbability(SpeechWav) > 0.8f);
    Assert.True(m.MaxProbability(SilenceWav) < 0.2f);
}
```

- [ ] **Step 5: Implement `SileroVadModel`**

Inputs: `input` `[1,576]` (previous **64 samples** + the 512 window), `state` `[2,1,128]`, `sr` `[1]`.
Outputs `prob`, `stateN`, both carried. Unit test asserts the context buffer is 64 samples and
zero-padded on first call — the detail that silently produces plausible wrong probabilities.

- [ ] **Step 6: Run, commit**

```bash
dotnet test --filter "SpeechSegmenterTests|SileroVadModelTests"
git commit -am "feat: Silero VAD, segmentation, single utterance buffer"
```

---

### Task 3: Turn coordinator, events, watchdog

**Files:**
- Create: `src/VoiceAgent.Server/Sessions/TurnCoordinator.cs`, `VoiceState.cs`
- Create: `src/VoiceAgent.Server/Contracts/VoiceEvents.cs`, `IVoiceEventSink.cs`
- Create: `tests/VoiceAgent.Tests/Sessions/TurnCoordinatorTests.cs`

**Interfaces:**
- Produces: `enum VoiceState { Idle, Listening, Thinking, Speaking }`
- Produces: `interface IVoiceEventSink { ValueTask SendAsync(VoiceEvent evt, CancellationToken ct); }`
- Produces: `abstract record VoiceEvent` and `StateChanged`, `TurnStarted`, `Transcript`,
  `AssistantDelta`, `AudioChunk(int Index, byte[] Wav, double DurationMs)`, `TurnCancelled`,
  `PipelineError`
- Produces: `TurnCoordinator(TimeProvider, TimeSpan fallbackMargin)` with `State`,
  `CancellationToken TurnToken`, `int NextSentenceIndex`, `BeginListening()`, `BeginThinking()`,
  `BeginSpeaking(int index, double durationMs)`, `SentenceStarted(int index)`,
  `SentencePlayed(int index)`, `MarkAllSentencesDispatched()`, `Cancel(string reason)`,
  `event Action<VoiceState> StateChanged`

- [ ] **Step 1: Failing tests, `FakeTimeProvider`**

```csharp
[Fact]
public void Speaking_ends_when_the_last_sentence_played_and_all_are_dispatched()
[Fact]
public void Speaking_does_not_end_while_kokoro_is_behind_playback()
// sentence 0 plays; sentence 1 not yet dispatched. Advance past the watchdog window. Still Speaking.
[Fact]
public void Watchdog_forces_listening_when_SentencePlayed_never_arrives()
// MarkAllSentencesDispatched, BeginSpeaking(duration 300), advance 800ms with no playback event
[Fact]
public void Watchdog_is_not_armed_before_dispatch_completes()
[Fact]
public void SentenceStarted_resets_the_watchdog()
```

No `Thread.Sleep` anywhere; every time advance goes through `FakeTimeProvider`.

- [ ] **Step 2: Implement**

End condition: `AllSentencesDispatched && lastPlayed == NextSentenceIndex - 1`. The watchdog arms
only after `MarkAllSentencesDispatched()`, for the remaining audio duration + margin. `Cancel`
fires the turn token, forces `Listening`, raises `StateChanged`.

- [ ] **Step 3: Run, commit** — `dotnet test --filter TurnCoordinatorTests`.

```bash
git commit -am "feat: turn state machine with dispatch-aware watchdog"
```

---

### Task 4: Whisper STT and the first observable milestone

**Files:**
- Create: `src/VoiceAgent.Server/Pipeline/SttService.cs`, `TranscriptCleaner.cs`
- Create: `src/VoiceAgent.Server/Pipeline/VoicePipeline.cs`
- Create: `src/VoiceAgent.Server/ModelDownloader.cs`, `Hubs/SignalRVoiceEventSink.cs`
- Create: `tests/VoiceAgent.Tests/Pipeline/SttServiceTests.cs`, `TranscriptCleanerTests.cs`
- Modify: `Hubs/VoiceHub.cs`, `wwwroot/app.js`

**Interfaces:**
- Produces: `record SttResult(string Text, double LatencyMs)`
- Produces: `Task<SttResult> TranscribeAsync(short[] pcm16k, CancellationToken ct)` — serialized by
  an internal `SemaphoreSlim(1,1)`.
- Produces: `static string TranscriptCleaner.Clean(string raw)` and `static bool IsSilent(short[] pcm)`
- Produces: `VoicePipeline(IVadModel, SttService, IVoiceEventSink)` with
  `Task PushAudioAsync(ReadOnlyMemory<byte> pcm16k, CancellationToken ct)`

- [ ] **Step 1: Cleaner tests (Review Focus 5)**

```csharp
[Theory]
[InlineData("[BLANK_AUDIO]", "")]
[InlineData("(silence)", "")]
[InlineData("[Music] hello there", "hello there")]
[InlineData("  hello   world  ", "hello world")]
public void Strips_whisper_artifacts(string raw, string expected) =>
    Assert.Equal(expected, TranscriptCleaner.Clean(raw));
```

- [ ] **Step 2: STT tests**

```csharp
[Fact] public async Task Transcribes_the_speech_fixture()      // contains "hello"
[Fact] public async Task Skips_digital_silence_without_calling_whisper()
[Fact] public async Task Serializes_concurrent_calls()           // two in flight, no exception
```

- [ ] **Step 3: Implement**

`WhisperFactory.FromPath` once; per call `WithLanguage("en")`, `WithThreads(4)`,
`WithNoSpeechThreshold(0.6f)`. `IsSilent` = RMS < 0.005 → return empty without invoking Whisper.
`Clean` strips `[...]`/`(...)` then collapses whitespace. `ModelDownloader.EnsureAsync` fetches
`ggml-tiny.en.bin` (~75 MB) into `models/`.

- [ ] **Step 4: Observable milestone — segments appear in the page**

`VoicePipeline` now owns the segmenter + STT and raises `TurnStarted` at speech end and `Transcript`
after STT. `SignalRVoiceEventSink` maps events to client method names. `app.js` prints transcripts.
No coordinator wiring needed yet beyond state labels.

- [ ] **Step 5: Manual M1 acceptance**

Speak; the sentence appears in the page. Server logs STT ms. Record the number.

- [ ] **Step 6: Commit**

```bash
git commit -am "feat: Whisper STT, transcript cleaning, observable M1"
```

---

### Task 5: LLM streaming, chunker, history

**Files:**
- Create: `src/VoiceAgent.Server/Pipeline/LlmService.cs`, `SentenceChunker.cs`,
  `ConversationHistory.cs`
- Create: `tests/VoiceAgent.Tests/Pipeline/SentenceChunkerTests.cs`,
  `ConversationHistoryTests.cs`
- Modify: `Pipeline/VoicePipeline.cs`, `wwwroot/app.js`

**Interfaces:**
- Produces: `IAsyncEnumerable<string> LlmService.StreamAsync(IReadOnlyList<ChatMessage>, CancellationToken)`
- Produces: `SentenceChunker(int maxChars = 90, int minChars = 20)` with
  `IReadOnlyList<string> Push(string delta)` and `string Flush()`
- Produces: `ConversationHistory` with `AddUser`, `AddAssistant`, `RemoveLastUser`,
  `TruncateLastAssistant(IReadOnlyList<string> sentences, int lastPlayedIndex)` (`-1` removes the
  assistant message entirely), `IReadOnlyList<ChatMessage> Messages`

- [ ] **Step 1: Chunker tests**

```csharp
[Fact] public void Flushes_on_full_stop_regardless_of_length()   // "Hello there." -> 1 chunk
[Fact] public void Does_not_flush_a_short_comma_clause()         // "Well," -> 0
[Fact] public void Flushes_at_90_chars_when_no_punctuation_arrives()
[Fact] public void Flush_emits_the_remainder()
```
`minChars` applies only to comma and length flushes; `.?!` always flushes. That is what makes
"Hello there." legitimate at 12 characters.

- [ ] **Step 2: History tests**

```csharp
[Fact] public void Keeps_only_the_sentences_the_user_heard()
// ... -> "First sentence. [interrupted]"
[Fact] public void Interruption_before_any_sentence_drops_the_message()  // lastPlayedIndex: -1
[Fact] public void RemoveLastUser_supports_turn_merging()
```

- [ ] **Step 3: Implement both, run tests.**

- [ ] **Step 4: LlmService**

`OllamaApiClient(new Uri("http://localhost:11434"), "gemma4:31b-cloud")` as `IChatClient`.
System prompt: *"You are a voice assistant. Answer in one or two short sentences. Never use
markdown, lists, or code blocks."* Yield deltas. Tool-call seam left empty.

- [ ] **Step 5: Observable milestone — tokens appear**

Pipeline: `Transcript` → LLM stream → `AssistantDelta` per delta, and `MarkAllSentencesDispatched`
after `Flush()` when the stream completes. Page appends tokens to a bubble. Integration test
`Streams_tokens_from_gemma` skips when Ollama is unreachable.

- [ ] **Step 6: Manual M2 acceptance** — ask a question, watch text stream in.

- [ ] **Step 7: Commit**

```bash
git commit -am "feat: gemma streaming, sentence chunker, history truncation"
```

---

### Task 6: Kokoro TTS and playback (review checkpoint before barge-in)

**Files:**
- Create: `src/VoiceAgent.Server/Pipeline/TtsService.cs`
- Modify: `Hubs/VoiceHub.cs`, `wwwroot/app.js`, `Pipeline/VoicePipeline.cs`
- Create: `tests/VoiceAgent.Tests/Pipeline/TtsServiceTests.cs`

**Interfaces:**
- Produces: `record TtsChunk(byte[] Wav, double DurationMs)`,
  `Task<TtsChunk> SynthesizeAsync(string text, CancellationToken ct)`
- Consumes: `AudioChunk` events; `SentenceStarted(int index)`, `SentencePlayed(int index)`

- [ ] **Step 1: TTS test** — WAV starts with `RIFF`, length > 1000, duration in [200, 5000] ms.

- [ ] **Step 2: Implement** — `KokoroWavSynthesizer.LoadModel()`, voice `af_heart`, duration from
  the `data` chunk / byte rate. Honour `ct` before and after synthesis.

- [ ] **Step 3: Client playback, with the two traps closed**

- **Separate playback context.** `playCtx = new AudioContext()` at the device rate. Kokoro emits
  24 kHz; a 16 kHz context would cap it at 8 kHz and everything would sound muffled. Mic capture
  keeps its own context.
- **No `onstarted` exists.** Schedule the node, then `setTimeout(..., (startAt - playCtx.currentTime
  + playCtx.outputLatency) * 1000)` to invoke `SentenceStarted(index, performance.now())`.
- **`onended` fires on `stop()` too.** Maintain `turnGeneration`; before flushing, null every
  `onended` and clear the array, then `stop()`. `SentencePlayed` is only sent by a live handler
  whose generation still matches.

- [ ] **Step 4: Wire TTS into the pipeline**, one WAV per chunk with its index.

- [ ] **Step 5: Manual M3 acceptance**

Hear a reply. Log `TurnStarted → first audible` client-side, plus server STT/first-token/synthesis.
Record real numbers in the spec §10 table.

- [ ] **Step 6: Checkpoint**

Run: `dotnet test` + one manual call. **Dispatch the fresh reviewer here**, before Task 7 adds
barge-in. Fix findings before continuing.

- [ ] **Step 7: Commit**

```bash
git commit -am "feat: Kokoro TTS, generation-safe playback, observable M3"
```

---

### Task 7: Barge-in, echo floor, turn merging

**Files:**
- Create: `src/VoiceAgent.Server/Pipeline/BargeInGate.cs`, `EchoFloorGate.cs`
- Modify: `Pipeline/VoicePipeline.cs`, `Sessions/TurnCoordinator.cs`, `wwwroot/app.js`
- Create: `tests/VoiceAgent.Tests/Pipeline/BargeInGateTests.cs`, `EchoFloorGateTests.cs`

**Interfaces:**
- Produces: `BargeInGate(int confirmMs = 250)` with `bool Push(bool isUserSpeech, int frameMs)` and
  `Reset()`.
- Produces: `EchoFloorGate` with `void BeginPlayback()`, `void EndPlayback()`,
  `bool IsUserSpeech(ReadOnlySpan<short> pcm, float vadProbability)`, `float Floor { get; }`.

- [ ] **Step 1: Gate tests**

```csharp
[Fact] public void Fires_after_250ms_of_sustained_speech()
[Fact] public void Does_not_fire_on_a_150ms_burst()
[Fact] public void Does_not_fire_twice_without_reset()
```

- [ ] **Step 2: Echo-floor tests (Review Focus 4)**

```csharp
[Fact] public void Calibrates_the_floor_during_the_first_200ms_of_playback()
[Fact] public void Rejects_echo_at_or_below_the_floor_even_when_vad_says_speech()
[Fact] public void Accepts_a_voice_well_above_the_floor()
```
Definition: during the first 200 ms after playback begins, accumulate mic RMS into `Floor`
(AEC residual). Afterwards, user speech requires `rms > max(0.015, Floor * 2.5)` **and**
`vadProbability >= 0.6`. Without a real far-end reference this is a heuristic, and the tests pin
the heuristic, not acoustic truth; M4 remains the real proof.

- [ ] **Step 3: Implement both, run tests.**

- [ ] **Step 4: Wire barge-in**

While `Thinking` or `Speaking`: `EchoFloorGate.IsUserSpeech(...)` → `BargeInGate.Push(...)`. On
fire: `turn.Cancel("user")`, send `TurnCancelled` + `StateChanged(Listening)`, restore threshold to
0.5, and keep `segmenter.CurrentUtterance()` as the new input.

- [ ] **Step 5: Turn merging (Review Focus 3)**

When the state was `Thinking`: `TurnCancelled("merge")` (client drops the pending bubble),
`history.RemoveLastUser()`, concatenate the aborted PCM with the new utterance, run **one** STT pass,
send **one** `Transcript`, make **one** LLM call. Test asserts exactly one transcript and one LLM
invocation.

- [ ] **Step 6: Client ducking**

While audio is scheduled: mic RMS above the echo floor for ~120 ms → ramp `playbackGain` to 0 over
30 ms. `TurnCancelled` → stay silent. No cancel within 350 ms → restore over 80 ms.

- [ ] **Step 7: Threshold scoping**

`Threshold = 0.6f` on entering `Speaking`, `0.5f` on leaving — watchdog path included. Send the live
value to the HUD.

- [ ] **Step 8: Manual M4 acceptance**

Log voice-onset → silence and cancel → silence. Five minutes with speakers on, zero
self-interruptions. If 0.60 still leaks, document headphones as the condition.

- [ ] **Step 9: Commit**

```bash
git commit -am "feat: barge-in with echo floor, ducking, and turn merging"
```

---

### Task 8: HUD, config, console harness, README

**Files:**
- Create: `src/VoiceAgent.Harness/VoiceAgent.Harness.csproj`, `Program.cs`
- Create: `src/VoiceAgent.Server/Options/VoiceOptions.cs`, `ConfigEndpoints.cs`
- Create: `src/VoiceAgent.Server/Hubs/ConsoleVoiceEventSink.cs` (moved to harness if cleaner)
- Modify: `wwwroot/index.html`, `app.js`, `style.css`
- Create: `README.md`

- [ ] **Step 1: HUD** — state ring, live VAD threshold, per-stage timings (`TurnStarted` → live,
  STT, first token, first synthesis, first audible), one line each. A **Stop speaking** button
  wired to `StopSpeaking()` so the cancel path is testable without a mic.

- [ ] **Step 2: Config** — `GET /api/config` returns effective options; `POST` accepts a partial
  in-memory update. No persistence.

- [ ] **Step 3: Harness** — `dotnet run --project src/VoiceAgent.Harness -- file.wav --no-audio`.
  Uses `NAudio` to read the WAV, implements `IVoiceEventSink` to print, runs the same
  `VoicePipeline`. Acceptance: same transcript as the browser for the same file.

- [ ] **Step 4: README** — prerequisites, first-run downloads (~400 MB), run command, headphone
  note, CPU-only latency expectation.

- [ ] **Step 5: Full verification** — `dotnet build`, `dotnet test`, one interruption, one cough.

- [ ] **Step 6: Commit**

```bash
git commit -am "feat: latency HUD, runtime config, console harness, README"
```
