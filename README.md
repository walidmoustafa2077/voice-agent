# Voice Agent

A local, phone-call-style voice agent. You talk, it answers out loud, and you can talk over it
to interrupt. The browser is only a microphone and a speaker: all voice activity detection,
speech-to-text and text-to-speech run on this machine, inside the server.

The language model is the only remote piece. It is served through your local Ollama install
(`gemma4:31b-cloud`), so the audio path never leaves the box.

```
mic → VAD (Silero) → STT (Whisper tiny.en) → LLM (Ollama) → TTS (SAPI or Kokoro) → speaker
```

The whole thing is a cascade, not a speech-to-speech model. That is what makes it cheap to run on
a CPU: the only models that run per turn are tiny, and the large model is remote.

## Requirements

- **.NET 10 SDK** (`dotnet --version` should report 10.x)
- **Ollama** running on `http://localhost:11434` with the model already pulled:
  `ollama pull gemma4:31b-cloud`
- **Chrome or Edge** on desktop. The page uses an `AudioWorklet` and browser echo cancellation;
  Firefox and Safari are not supported.
- **Headphones.** The agent's own voice reaches the microphone through the speakers, and echo
  cancellation only goes so far. Without headphones you will see phantom barge-ins.
- ~100 MB free disk for the default speech models (Silero VAD and Whisper `tiny.en`). Opting into
  Kokoro adds about 325 MB. A CPU is enough; no GPU is used.

## First run

```bash
dotnet run --project src/VoiceAgent.Server
```

On startup the server downloads Silero VAD and Whisper `tiny.en` into `models/` if they are missing
(about 80 MB). Kokoro is only downloaded when you select it (see below). It logs each file as it
finishes. Downloads go to `*.part` files and are only moved into place after they pass a
minimum-size sanity check, so a killed download cannot poison later runs.

Then open <http://localhost:5192>, click **Start microphone**, and allow microphone access.
`localhost` counts as a secure context, so no certificate is needed.

## What you should see

The header shows the turn state (`idle` → `listening` → `thinking` → `speaking`). The log shows
your transcript and the agent's reply as it streams. The HUD under the log reports the last turn's
latency, one number per stage:

| chip    | meaning                                              |
| ------- | ---------------------------------------------------- |
| `vad`   | active VAD threshold (rises while speaking)          |
| `stt`   | speech end → transcript                              |
| `token` | speech end → first LLM token                         |
| `synth` | speech end → first synthesized audio chunk           |
| `audio` | turn started → first audible sound in the browser    |

**Stop speaking** cancels the current reply without using the microphone, which is handy for
testing the cancel path.

## Talking over it

While the agent is speaking, sustained speech for 250 ms triggers a barge-in: the server cancels
the LLM stream, drops queued audio, and starts listening on a new turn. The browser also ducks
playback immediately on loud local audio, so the round trip is not audible. The echo floor is
calibrated from the first 200 ms of each reply to avoid the agent interrupting itself.

A segment that starts while the agent is speaking but never reaches 250 ms (the agent's own echo,
a cough) is discarded instead of becoming a turn. Tune `speakingVadThreshold` up if your speakers
leak enough to trigger it.

If you start talking while it is still *thinking*, the new speech is merged with the pending
utterance into a single transcript and a single LLM call.

## Speech engine

Two TTS engines are available; the server picks one at startup.

| engine | default on | model | measured speed | voice |
| ------ | ---------- | ----- | -------------- | ----- |
| Windows SAPI | Windows | none (built into the OS) | ~0.1x realtime | dated but intelligible |
| Kokoro `af_heart` | other OSes | `kokoro.onnx` (~325 MB) | ~1.1x realtime (3 threads) | natural |

SAPI renders a sentence in well under a second on this CPU, while Kokoro needs several seconds,
so SAPI is the default here. Choose Kokoro for quality:

```bash
set VOICEAGENT_TTS=kokoro      # PowerShell: $env:VOICEAGENT_TTS="kokoro"
dotnet run --project src/VoiceAgent.Server
```

The first Kokoro run downloads its model. To pick a specific installed SAPI voice, set
`VOICEAGENT_SAPI_VOICE` to part of its description (for example `Zira`); otherwise the OS default
is used.

## Latency expectations

On a CPU-only machine the local stages dominate. Measured on the development machine (4 cores,
2016 APU) with a warm Whisper model:

- Whisper `tiny.en` runs at about **1.2–1.5x realtime**, so a 20 s question costs 25–30 s of
  transcription. That is the largest single cost.
- SAPI synthesis is effectively free (~0.1x realtime); the first audio arrives a fraction of a
  second after the first sentence is known.
- Kokoro, if selected, runs at ~1.1x realtime and adds several seconds before the first sound.

With SAPI, budget roughly **1–2 s of local work after you stop talking**, plus the time it takes
the remote model to produce its first token and the first sentence. A 20 s question still takes
20 s+ to transcribe because recognition is whole-utterance today. Nothing is spoken before the
first sentence is complete.

If it feels sluggish, check the HUD to see which stage is responsible before changing anything.

## Runtime configuration

The page reads `GET /api/config`. A partial in-memory update is accepted by `POST /api/config`
with camelCase keys; unknown keys are rejected. Changes are **not persisted** and reset on restart.

```bash
curl http://localhost:5192/api/config

curl -X POST http://localhost:5192/api/config \
  -H "Content-Type: application/json" \
  -d '{"vadThreshold":0.55,"minSilenceMs":500}'
```

Tunable keys: `vadThreshold`, `speakingVadThreshold`, `minSpeechMs`, `minSilenceMs`, `preRollMs`,
`maxChunkChars`, `minChunkChars`, `maxHistoryMessages`.

A session captures the configuration when it is created, so a POST applies to the next session:
reload the page after changing values. This is deliberate — the segmenter and history are sized
at construction — and is why nothing is persisted.

`vadThreshold` applies while listening; `speakingVadThreshold` applies only while the agent is
speaking and is deliberately higher so the agent's own voice is harder to trigger on.

## Console harness

For repeatable tests without a microphone, feed a WAV file through the same pipeline. It prints
each stage event and the transcript, and exits when the turn finishes.

```bash
dotnet run --project src/VoiceAgent.Harness -- tests/VoiceAgent.Tests/Fixtures/hello.wav --no-audio
```

Any sample rate and channel count is accepted, but rates below about 14 kHz lose treble content:
the resampler's anti-alias filter cuts off at 7 kHz. The harness resamples to 16 kHz mono with the
same streaming resampler the server uses. The transcript for a given file should match what the
browser produces for the same audio. `--no-audio` is accepted today for forward compatibility; the
harness prints events only and does not play the reply.

## Tests

```bash
dotnet test
node --test tests/js/playback.test.mjs tests/js/ducking.test.mjs
```

The .NET suite covers the resampler, VAD/segmenter, turn state machine, chunking, history,
barge-in, cancellation boundaries and the end-to-end pipeline. The Node tests cover the browser
playback queue (ordering, cancel, generation guards) and the ducking controller (120 ms hold,
350 ms restore, cancel stays silent).

Tests that need the Kokoro model return quietly when it is not downloaded yet, so a fresh clone
reports them as passed rather than skipped.

## Layout

```
src/VoiceAgent.Server/     ASP.NET Core host, SignalR hub, pipeline, static UI
src/VoiceAgent.Harness/    console WAV harness
tests/VoiceAgent.Tests/    xUnit tests and fixtures
tests/js/                  Node tests for wwwroot/playback.js
models/                    downloaded speech models (gitignored)
docs/superpowers/plans/    implementation plan and acceptance criteria
```

## Troubleshooting

- **"model ... is not ready"** — delete the file and any `.part` sibling in `models/`, then restart.
- **No transcript ever appears** — check the mic permission and that the `vad` chip moves when you
  speak. A quiet microphone can sit under `minSpeechMs`.
- **It interrupts itself** — use headphones, or raise `speakingVadThreshold` via `POST /api/config`.
- **Ollama connection errors** — confirm `ollama list` shows `gemma4:31b-cloud` and that the daemon
  is listening on `11434`.
- **No sound, but the log fills in** — the browser may have blocked audio until a user gesture;
  click anywhere on the page and start the microphone again.
