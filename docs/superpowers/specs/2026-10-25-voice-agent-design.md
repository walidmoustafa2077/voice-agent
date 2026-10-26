# Voice Agent — Prototype Design

**Date:** 2026-10-25
**Status:** approved 2026-10-25. Implementation plan: `docs/superpowers/plans/2026-10-25-voice-agent.md`
**Size:** project (not a small change)

---

## 1. Intent

A local, real-time voice chat you can have with your own Ollama model: you speak, it
answers out loud, and it **stops the moment you start talking**. The feel to aim for is a
phone call, not a walkie-talkie.

Prototype. Single user, one machine. The point is to feel the interaction and learn where
the latency actually is — not to ship.

## 2. Why

The user has a local Ollama model (`gemma4:31b-cloud`) and wants a voice front end for it
that they control. They do not want a browser-based or cloud speech stack doing the work:
the AI models must be ours. The machine is CPU-only. The interaction requirement that
drives the whole design is barge-in with echo cancellation.

## 3. Goals

| # | Goal | How we know |
|---|------|-------------|
| G1 | Phone-like turn-taking | Playback silenced within ~300 ms of the user starting to speak; cancel fires at ≤ 250 ms of sustained speech |
| G2 | The speech stack is local and offline | Silero VAD and Whisper.net run in .NET on our own models; TTS is an OS-provided offline voice (Windows SAPI) by default, with Kokoro as a local opt-in. Only the LLM is remote (Ollama cloud route). No Web Speech, no `speechSynthesis`, no cloud speech service |
| G3 | One command to run | `dotnet run` then open one page. No npm, no build step |
| G4 | Text alongside voice | Live transcript pane: user text, assistant text, state |
| G5 | Honest latency | First audio typically under 3 s on CPU (see §10); every stage timed and shown |

## 4. Non-goals

- Telephony, Twilio, SIP, real phone numbers
- Multi-user, auth, accounts, persistence
- Deployment, containers, HTTPS
- Voice cloning, emotion, paralinguistics
- Full-duplex speech-to-speech (Moshi-style). This is a cascade, by necessity
- Mobile
- Tool calling, web search, RAG in v1 (a seam is left; nothing is built)
- Languages other than English

## 5. Anti-goals

These are failure modes even if the thing "works":

- **Barge-in that self-triggers.** If the agent hears its own voice and interrupts itself,
  the prototype has failed, no matter how good the transcription is.
- **Faking latency.** No filler audio, no canned "let me think" sounds to mask a slow stage.
  Measure it and show it.
- **Becoming a framework.** No plugin architecture, no abstractions beyond what the
  pipeline needs.
- **Polishing the UI.** It is a diagnostic surface, not a product.

## 6. Constraints

- Windows, **CPU only** — no CUDA/DirectML. All acoustic models run on CPU.
- **Chrome or Edge only.** AEC on Web Audio output is dependable in Chromium; other browsers
  are not supported and this is deliberate, not an oversight.
- .NET SDK 10.0.302 installed (9.0.315 also present). Target `net10.0`.
- Ollama at `http://localhost:11434`, model `gemma4:31b-cloud`.
  This is a **cloud-routed, text-only** model: LLM latency is network-bound and outside our
  control, and it cannot accept or emit audio. It shapes the architecture: we must cascade.
- Single user, localhost, no TLS.
- First run downloads ~400 MB of model files.

## 7. Approaches considered

### A. Use `ElBruno.Realtime` end-to-end — rejected

MIT, .NET 8/10, MEAI-shaped, does VAD → STT → LLM → TTS with pluggable providers. Genuinely
close to what we want. But reading its source (not just the README):

- Its SignalR sample (`src/samples/scenario-02-api/ConversationHub.cs`) returns **text only**,
  with `EnableAudioResponse = false` and the comment *"Streaming TTS over SignalR is complex;
  text for now."*
- The pipeline is **turn-based**: one VAD segment in, one response out, sequential. There is
  no mid-response cancellation, no client playhead, no stop signal.

Barge-in and audio return are exactly the two things it declines to do. We would fork it on
day one and still write the hard part.

### B. Own thin pipeline, same building blocks — **chosen**

~500–700 lines of C#. SignalR hub, Silero VAD (ONNX), Whisper.net, `OllamaApiClient`,
KokoroSharp. One `CancellationToken` per turn. We use the same libraries ElBruno wraps, but
we own the loop, which is where all the interesting behaviour lives.

### C. Hybrid: their adapters, our orchestration — rejected

Saves ~100 lines, puts two 0.x packages on the critical path of a project whose whole point
is the orchestration. Not worth it.

### Transport fork: browser pipe vs. console with own AEC — browser chosen

The browser is used **only as microphone, speaker, and echo canceller**. No model, no
speech API. Console would remove SignalR (~200 lines) but force us to build AEC ourselves:
WASAPI loopback as far-end reference plus WebRTC AEC3 or NLMS. On a CPU-only machine that
is the worst possible place to spend cycles and the flakiest part of the build.

Mitigation for the "own everything" instinct: a **console harness** (`--harness file.wav`)
pushes a WAV through the identical pipeline, so STT/LLM/TTS can be debugged without a
browser or a microphone.

## 8. Architecture

```
┌─ Browser (dumb pipe, no AI) ────────────────────────────────┐
│  getUserMedia(echoCancellation:true, noiseSuppression:true)  │
│    → AudioWorklet: Float32→PCM16, 20 ms frames, native rate  │
│    → SignalR (MessagePack, binary)                           │
│                                                              │
│  ← AudioChunk(wav)  → decodeAudioData → AudioBufferSource    │
│      scheduled on a playhead; StopAudio flushes all sources  │
│  ← Transcript / AssistantDelta / State / TurnCancelled       │
└──────────────────────────────────────────────────────────────┘
                 │ localhost
┌─ ASP.NET Core (net10.0) ────────────────────────────────────┐
│  VoiceHub ─ VoicePipeline + TurnCoordinator per connection  │
│    ├─ StreamingResampler (native rate → 16 kHz)             │
│    ├─ SpeechSegmenter (Silero v5 ONNX) ── speech start/end  │
│    ├─ SttService (Whisper.net, tiny.en)                     │
│    ├─ LlmService (OllamaApiClient, streaming)               │
│    ├─ SentenceChunker                                       │
│    └─ TtsService (SAPI by default, Kokoro opt-in)           │
│  TurnContext { CancellationTokenSource, state, history }    │
└──────────────────────────────────────────────────────────────┘
                 │ HTTP
            Ollama :11434  (gemma4:31b-cloud)
```

### Components

| Component | Responsibility | Detail |
|---|---|---|
| `AudioWorklet` | capture, frame | asks for `AudioContext({sampleRate: 16000})`; if the browser reports another rate, `StartSession(actualRate)` tells the server and **C# resamples** (low-pass 7 kHz + linear). Worklet only converts Float32 → PCM16 and emits 320-sample frames |
| `VoiceHub` | transport | `AddMessagePackProtocol`, `MaximumReceiveMessageSize = 1 MB` |
| `VoiceSession` | per-connection state | ring buffer, VAD, turn context, history (max 20 turns) |
| `VadSegmenter` | speech boundaries | Silero v5 ONNX, 512-sample (32 ms) windows, state tensor carried between calls |
| `SttService` | speech → text | Whisper.net, `ggml-tiny.en.bin` default, 4 threads, greedy, no timestamps |
| `LlmService` | text → text stream | OllamaSharp `OllamaApiClient` (implements MEAI `IChatClient`), streaming |
| `SentenceChunker` | text stream → utterances | flush on `.` `?` `!`, or at 90 chars on `,`/`;`, min 20 chars |
| `TtsService` | utterance → WAV | **Default: Windows SAPI** (`SAPI.SpVoice` + `SAPI.SpFileStream` via COM, offline OS voice, ~200 ms fixed cost per chunk, RTF ≈ 0.01). **Opt-in: KokoroSharp** `KokoroWavSynthesizer`, voice `af_heart`, selected with `VOICEAGENT_TTS=kokoro` (RTF ≈ 1.1 at 3 threads). One WAV per sentence in both cases |

### Wire protocol

Client → server:

| Method | Payload | Notes |
|---|---|---|
| `StartSession(string? systemPrompt, int micSampleRate)` | 16000 / 44100 / 48000 | returns `sessionId`; server resamples if the rate isn't 16 kHz |
| `SendAudio(byte[] pcm16le)` | 640 B = 20 ms | 16 kHz mono |
| `SentenceStarted(int index, double atMs)` | client clock | first one flips the server to `Speaking` |
| `SentencePlayed(int index, double atMs)` | client clock | drives history, `Speaking → Listening`, and the HUD |
| `StopSpeaking()` | — | explicit interrupt button |

Server → client:

| Event | Payload | Notes |
|---|---|---|
| `TurnStarted` | — | sent at VAD speech end; the client's clock reference for latency |
| `StateChanged` | `idle\|listening\|thinking\|speaking` | drives the UI ring |
| `Transcript` | final user text | one per utterance |
| `AssistantDelta` | token text | append to bubble |
| `AudioChunk` | WAV bytes | one per sentence |
| `TurnCancelled` | reason | client flushes playback immediately |
| `Error` | message | non-fatal |

**One clock per duration.** The client times from `TurnStarted` *arrival* to the first
`SentenceStarted` it actually hears; it never subtracts a server timestamp from a client one.
Localhost transit is a few milliseconds and is accepted as part of the number. The server
times its own stages (STT, first token, synthesis) internally.

## 9. Turn lifecycle and barge-in

States: `Idle → Listening → Thinking → Speaking → (BargeIn) → Listening`.

1. Audio frames always flow to `VadSegmenter`, regardless of state.
2. Barge-in is armed in **both `Thinking` and `Speaking`**. During `Thinking` there is no
   audio yet, but the stale turn must still die — otherwise the old answer plays over the
   user who has already moved on.
3. VAD **speech start** begins evaluation. Barge-in fires only after **250 ms of sustained
   speech**; shorter blips (cough, door, speaker leak) are discarded.
4. **Client ducking, before the round trip.** While assistant audio is scheduled, the client
   watches mic RMS. On ~120 ms above a playback-relative threshold it ramps playback gain to
   zero over 30 ms. If `TurnCancelled` arrives, it stays silent. If not within 350 ms, it
   restores gain over 80 ms — that was a cough. This is an energy gate, not a model, and it
   is what makes interruption feel instant rather than merely fast.
5. On fire: `turn.Cts.Cancel()`. That one token stops the LLM stream, the chunker, and the
   TTS worker. Then immediately send `TurnCancelled` + `StateChanged(listening)` — we do not
   wait for the pipeline to unwind.
6. Client stops every scheduled `AudioBufferSourceNode` and resets the playhead on
   `TurnCancelled`.
7. **Speech during `Thinking` merges turns.** Cancel the in-flight STT/LLM immediately, keep
   the aborted turn's audio segment, append the new speech to it, and run **one** STT pass
   over the merged audio. One user message, no duplicated text, no turn queue. The aborted
   assistant turn is dropped, never having existed.
8. **History truncates at what was heard, not what was dispatched.** `SentencePlayed(n)` is
   the boundary: the assistant message keeps sentences `0..n` and is marked `[interrupted]`.
   The visible bubble is trimmed the same way, so the transcript never shows words that were
   never spoken. Synthesised-but-unplayed sentences are not remembered.
9. VAD **speech end** → finalize: STT → LLM → chunker → TTS per sentence.
10. **Playback drives the state, not dispatch.** `Speaking` begins on the first
    `SentenceStarted` and ends on the last `SentencePlayed`. A server-side timer based on
    synthesis would report `Listening` while audio is still audible, disarming barge-in over
    the tail of a reply — exactly where people interrupt.
11. **The barge-in audio is kept.** The segmenter owns exactly one utterance buffer: pre-roll
    plus speech. When barge-in fires, that buffer (not a second ring buffer) becomes the opening
    of the new turn, so the user never repeats their first words. AEC is what keeps the agent's
    own tail out of the pre-roll. The pre-roll is configurable (default **150 ms**) and is a
    guess until tested against real interruptions.
12. **State cannot strand.** `SentencePlayed` is best-effort: a throttled tab or a dropped
    frame would otherwise leave the session in `Speaking` with the raised VAD threshold
    applied forever. A watchdog fires at the remaining audio duration + 500 ms and forces
    `Listening`. It is **armed only after every sentence has been dispatched** — otherwise a
    fast player and a slow Kokoro would end `Speaking` mid-reply. Any
    `SentenceStarted`/`SentencePlayed` resets it.
13. **The raised threshold is scoped.** 0.60 applies only while `Speaking`; it returns to
    0.50 the instant the state changes, and the live value is shown in the HUD. If quiet
    speakers cannot interrupt, this is the first knob.

Additional guards:

- AEC on in `getUserMedia` (free, browser-side).
- While `Speaking`, the VAD threshold is raised (0.60 vs 0.50) and barge-in additionally
  requires mic RMS above the **echo floor**, calibrated from the first 200 ms of playback
  (the AEC residual), with the rule `rms > max(0.015, floor × 2.5)` **and** `VAD ≥ 0.6`.
  Heuristic, pinned by unit tests; the M4 speakers-on run is the acoustic proof.
- Ignore the first **200 ms** after playback starts (AEC warm-up / device latency).
- `minSilence` 600 ms default, tunable; `minSpeech` 250 ms.
- **Headphones are the reliable path.** If the speakers-on test still self-triggers at the
  raised threshold, headphones become the documented acceptance condition rather than a
  silent failure.

## 10. Latency budget (CPU-only, cloud LLM)

Measured on the development box (AMD PRO A10-8770 R7, 4 cores / 4 logical, 2016 APU, no GPU).
These are observations, not targets.

| Stage | Estimate | Measured | Control |
|---|---|---|---|
| Silence detection | 600 ms | 600 ms | `minSilence`; lower to 450 ms if it feels sluggish |
| Whisper tiny.en | 300–800 ms for 5 s | **1.4–1.5× realtime** (2.7 s audio → 3.9–4.0 s; a 22 s question → ~22.5 s) | model size, threads; tiny.en is the floor |
| LLM first token | 300–1500 ms | ~0.3–3 s | **not ours** — cloud routing; mitigate with short replies |
| First sentence to TTS (Kokoro) | 200–600 ms | **8.2 s wall for 5.36 s of audio** (RTF 1.53 at 4 threads); even a 6-char sentence took 3.5 s | Kokoro on CPU is the wrong default on this box |
| First sentence to TTS (SAPI) | — | **~200 ms per chunk**; 3 chunks in 597 ms (RTF 0.10 including COM/temp-file overhead) | default engine |
| Network / hub | < 10 ms | < 10 ms | localhost |
| **First audio** | **1.4–3 s realistic** | **~10 s with Kokoro** on a several-second turn; **~5–6 s with SAPI**, dominated by Whisper | client: `TurnStarted` arrival → first audible sentence |
| **Barge-in to silence** | **~300 ms from voice onset**; < 50 ms from the cancel | not machine-verifiable here | 250 ms sustain + 30 ms duck; independent of the above |

The honest read: on this hardware the *response* will not feel instant. The *interruption*
will, and that is what makes it read as a conversation rather than a query box. With SAPI the
speech path no longer dominates; the remaining lever is streaming ASR (§13 Task 3), which
targets decode RTF < 0.5 and would cut a long utterance's STT from tens of seconds to sub-second
while the user is still talking. Until that is measured, the ~3.5–4 s post-ASR floor is modelled,
not observed.

Kokoro is kept because it is local and the voice is better; it is a one-variable env switch.
Its thread sweep (same 67-char sentence, 4.05 s audio): 1 → 1.76×, 2 → 1.27×, 3 → 1.11×,
4 → 1.16×, so 3 intra-op threads is the optimum on four cores.

System prompt enforces brevity: answer in one or two short sentences unless asked to
elaborate. This is a latency decision as much as a style one.

## 11. Project layout

```
C:\Dev\voice-agent\
  VoiceAgent.sln
  docs/superpowers/specs/2026-10-25-voice-agent-design.md
  src/
    VoiceAgent.Server/
      Program.cs
      Hubs/VoiceHub.cs
      Hubs/SignalRVoiceEventSink.cs
      Sessions/TurnCoordinator.cs
      Audio/StreamingResampler.cs
      Audio/FrameRateCounter.cs
      Pipeline/VoicePipeline.cs
      Pipeline/SpeechSegmenter.cs
      Pipeline/SileroVadModel.cs
      Pipeline/SttService.cs
      Pipeline/TranscriptCleaner.cs
      Pipeline/LlmService.cs
      Pipeline/SentenceChunker.cs
      Pipeline/ConversationHistory.cs
      Pipeline/TtsService.cs
      Pipeline/BargeInGate.cs
      Pipeline/EchoFloorGate.cs
      Contracts/VoiceEvents.cs
      wwwroot/index.html
      wwwroot/app.js
      wwwroot/pcm-worklet.js
      wwwroot/style.css
    VoiceAgent.Harness/          console: push a WAV through the same pipeline
  tests/VoiceAgent.Tests/
```

Models live in `models/` (gitignored), downloaded on first run.

## 12. Packages

| Package | Version | Purpose |
|---|---|---|
| `Microsoft.AspNetCore.SignalR` (framework) | net10.0 | transport |
| `Microsoft.AspNetCore.SignalR.Protocols.MessagePack` | 10.x | binary payloads |
| `OllamaSharp` | 5.5.0 | `OllamaApiClient` (MEAI `IChatClient`) |
| `Whisper.net` | 1.9.1 | STT |
| `Whisper.net.Runtime` | 1.9.1 | native CPU runtime |
| `Microsoft.ML.OnnxRuntime` | 1.x | Silero VAD |
| `KokoroSharp.CPU` | 0.8.4 | optional TTS (`VOICEAGENT_TTS=kokoro`) + voices + model |
| Windows SAPI (`SAPI.SpVoice` / `SAPI.SpFileStream`) | OS-provided COM | default TTS on Windows; no NuGet package, offline |
| `NAudio` | 2.x | console harness only |

`Microsoft.Extensions.AI.Ollama` is deliberately **not** used: it ships preview-only and
OllamaSharp's client already implements the MEAI interface.

## 13. Milestones

Each milestone is runnable and observable.

| # | Delivers | Acceptance |
|---|---|---|
| M0 | Hub + worklet + level meter | Speak; see a live level bar; server counts frames at 50/s; all pinned packages restore on `net10.0` |
| M1 | VAD + Whisper | Segmenter test written first against a fixed WAV, then speech segments appear as text; **logged STT latency** |
| M2 | LLM streaming | Text streams into the pane; **logged first-token latency** |
| M3 | TTS (SAPI default, Kokoro opt-in) | Hear a reply; **logged first-audio latency** |
| M4 | Barge-in | Two numbers logged: voice onset → silence (~300 ms) and cancel → silence (< 50 ms). Interrupts during `Thinking` too. Duck-and-restore works on a cough. No self-interruption across 5 minutes with speakers at the raised threshold, or headphones documented as the condition |
| M5 | Polish | Latency HUD per stage, config endpoint, error states, harness works |

## 14. Risks

| Risk | Impact | Mitigation |
|---|---|---|
| Cloud LLM latency spikes | breaks the phone feel | measure; keep replies short; local fallback model swappable in one line |
| Whisper on CPU too slow for long utterances | 2 s+ lag | tiny.en; cap utterance at 15 s; log per-utterance timing |
| AEC insufficient on laptop speakers | self-interruption | 250 ms guard; headphones; raise threshold; loopback test |
| Silero VAD state handling bugs (v5 state tensors) | wrong segmentation | unit test with a fixed WAV; compare against expected segment count |
| Kokoro first-run download (~320 MB) | slow start | **not downloaded in the default SAPI configuration**; pre-download step in README only for `VOICEAGENT_TTS=kokoro` |
| SAPI voice quality is below Kokoro and varies by Windows install | sounds robotic on some machines | documented; `VOICEAGENT_TTS=kokoro` restores the neural voice at ~1.5× slower than realtime |
| SAPI synthesis is a blocking COM call and cannot be cancelled mid-sentence | a cancelled turn can spend up to one sentence synthesizing | cancel checked before and after the call; audio is dropped if the turn was cancelled |
| .NET 10 + SignalR binary size limits | messages dropped | `MaximumReceiveMessageSize = 1 MB`, 640-byte frames |
| Mic runs at 44.1 kHz, or the browser ignores the requested rate | aliasing degrades both Whisper and Silero | server-side streaming resampler, state carried across frames; 10 kHz attenuation test |
| Silero v5 needs the previous 64 samples prepended to each 512-sample window | plausible-looking but wrong probabilities | fixed-WAV segmenter test written before the implementation |

## 15. Calls I made (check these)

1. **Browser as audio pipe**, not console, because AEC is free there and CPU is scarce.
2. **Whisper `tiny.en`** as the default, `base.en` behind a config flag.
3. **TTS is Windows SAPI by default, Kokoro opt-in.** The benchmark forced this: Kokoro is
   ~1.5× slower than realtime on four cores while SAPI is ~0.01×. G2 was narrowed from "we own
   every model" to "local and offline"; the voice quality trade-off is real and reversible with
   `VOICEAGENT_TTS=kokoro`. Kokoro keeps `af_heart` and 3 threads when selected.
4. **No tools/RAG in v1.** One seam in `LlmService` where a tool loop would go.
5. **English only.**
6. **No persistence.** History lives in memory per session.
7. **Sentence-level TTS**, not word-level streaming. Simpler cancel semantics, and on CPU
   the sentence is the natural unit of synthesis anyway.
8. **Client-side ducking** on mic RMS, not a second VAD model in the browser. Cheap, model-free,
   and reversible when the server disagrees.
9. **Barge-in armed during `Thinking` as well as `Speaking`**, with the interrupted utterance
   merged into the next user turn.
10. **Chromium only** (Chrome/Edge), because AEC quality is the whole interaction.
11. **`SentencePlayed` on the wire**, so history and the latency HUD use what was heard.
12. **Playback events drive the state machine**, not synthesis completion. `Speaking` is
    audible audio, not dispatched audio.
13. **Barge-in audio is retained** (5 s ring buffer, 150 ms pre-roll) so interruption never
    costs the user their opening words.
14. **VAD threshold raised while speaking** (0.60) as the structural defence against AEC
    residual; headphones documented if it still leaks.
15. **Every duration lives on one clock.** Client measures from `TurnStarted` arrival; the
    server never compares its timestamps to the browser's.
16. **Playback watchdog** based on last-sentence audio duration + 500 ms, so a dropped
    `SentencePlayed` can't strand the session in `Speaking`.
17. **Pre-roll default 150 ms, configurable**, because the number is a guess until real
    interruptions test it.

## 16. Left to the builder

- Exact VAD tuning constants beyond the defaults above.
- Pre-roll length and barge-in threshold, tuned against real interruptions.
- WAV chunk framing over the wire (raw WAV per sentence is acceptable; document the format).
- Whether resampling stays server-side after M0 measurements (it is the tested path).
- Resampling stays server-side (it is the tested path); the client only frames at the device rate.
- Console harness ergonomics.
