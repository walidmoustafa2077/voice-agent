import { createPlayback } from './playback.js';
import { createDucking } from './ducking.js';

const stateEl = document.getElementById('state');
const levelEl = document.getElementById('level');
const fpsEl = document.getElementById('fps');
const logEl = document.getElementById('log');
const startBtn = document.getElementById('start');
const stopBtn = document.getElementById('stop');

const hudVad = document.getElementById('hud-vad');
const hudStt = document.getElementById('hud-stt');
const hudToken = document.getElementById('hud-token');
const hudSynth = document.getElementById('hud-synth');
const hudAudio = document.getElementById('hud-audio');

let vadNormal = 0.5;
let vadSpeaking = 0.6;

let connection;
let micContext;
let worklet;
let sourceNode;
let mediaStream;

let playCtx;
let playbackGain;
let playbackStartedThisTurn = false;

function rmsOf(samples) {
  let sum = 0;
  for (let i = 0; i < samples.length; i++) sum += samples[i] * samples[i];
  return Math.sqrt(sum / samples.length);
}

const ducking = createDucking({
  onDuck() {
    if (!playbackGain || !playCtx) return;
    playbackGain.gain.setTargetAtTime(0, playCtx.currentTime, 0.01); // ~30 ms to silence
  },
  onRestore() {
    if (!playbackGain || !playCtx) return;
    playbackGain.gain.setTargetAtTime(1, playCtx.currentTime, 0.027); // ~80 ms
  },
  onSilence() {
    if (!playbackGain || !playCtx) return;
    playbackGain.gain.cancelScheduledValues(playCtx.currentTime);
    playbackGain.gain.setValueAtTime(0, playCtx.currentTime);
  },
});

let playback;
let currentTurnId = 0;
let turnStartedAt = 0;
let firstAudioLogged = false;
let agentLine = null;
let lastUserLine = null;

function ensurePlayback() {
  if (playCtx) return;
  // A separate context at the device rate: Kokoro is 24 kHz, and a 16 kHz
  // context would cap it at 8 kHz and muffle every reply.
  playCtx = new AudioContext();
  playbackGain = playCtx.createGain();
  playbackGain.connect(playCtx.destination);

  playback = createPlayback({
    ctx: playCtx,
    gain: playbackGain,
    onStarted(index) {
      if (currentTurnId === null) return;
      connection.invoke('SentenceStarted', currentTurnId, index, performance.now()).catch(() => {});
      if (!firstAudioLogged) {
        firstAudioLogged = true;
        const elapsed = Math.round(performance.now() - turnStartedAt);
        hudAudio.textContent = `${elapsed} ms`;
        note(`first audio ${elapsed} ms`, 'meta');
      }
    },
    onPlayed(index) {
      if (currentTurnId === null) return;
      connection.invoke('SentencePlayed', currentTurnId, index).catch(() => {});
    },
  });
}

stopBtn.addEventListener('click', () => {
  connection?.invoke('StopSpeaking').catch(() => {});
});

async function loadConfig() {
  try {
    const config = await (await fetch('/api/config')).json();
    vadNormal = config.vadThreshold ?? vadNormal;
    vadSpeaking = config.speakingVadThreshold ?? vadSpeaking;
  } catch {
    // The HUD is optional; the session works without it.
  }
}

loadConfig();

function setState(name) {
  stateEl.textContent = name;
  stateEl.className = `state ${name}`;
}

function note(text, cls = 'note') {
  const div = document.createElement('div');
  div.className = `line ${cls}`;
  div.textContent = text;
  logEl.appendChild(div);
  logEl.scrollTop = logEl.scrollHeight;
  return div;
}

function toPcm16(float32) {
  const out = new Int16Array(float32.length);
  for (let i = 0; i < float32.length; i++) {
    const s = Math.max(-1, Math.min(1, float32[i]));
    out[i] = s < 0 ? s * 0x8000 : s * 0x7fff;
  }
  return new Uint8Array(out.buffer);
}

async function connect() {
  if (connection) {
    await connection.stop().catch(() => {});
    connection = null;
  }

  connection = new signalR.HubConnectionBuilder()
    .withUrl('/hub/voice')
    .withHubProtocol(new signalR.protocols.msgpack.MessagePackHubProtocol())
    .build();

  connection.on('level', (rms, fps) => {
    levelEl.style.width = `${Math.min(100, rms * 400)}%`;
    fpsEl.textContent = `${fps.toFixed(0)} fps`;
  });
  connection.on('error', (message) => note(message));
  connection.on('stateChanged', (state, threshold) => {
    const name = String(state).toLowerCase();
    setState(name);
    const live = Number(threshold);
    hudVad.textContent = (Number.isFinite(live)
      ? live
      : (name === 'speaking' ? vadSpeaking : vadNormal)).toFixed(2);
  });
  connection.on('stageTiming', (stage, ms) => {
    const target = stage === 'stt' ? hudStt
      : stage === 'firstToken' ? hudToken
      : stage === 'firstSynth' ? hudSynth
      : null;
    if (target) target.textContent = `${Math.round(ms)} ms`;
  });
  connection.on('transcript', (turnId, text) => {
    if (turnId !== currentTurnId) return;
    lastUserLine = note(text, 'user');
    agentLine = null;
  });
  connection.on('assistantDelta', (turnId, text) => {
    if (turnId !== currentTurnId) return;
    if (!agentLine) agentLine = note('', 'agent');
    agentLine.textContent += text;
    logEl.scrollTop = logEl.scrollHeight;
  });
  connection.on('turnStarted', (turnId) => {
    currentTurnId = turnId;
    turnStartedAt = performance.now();
    firstAudioLogged = false;
    playbackStartedThisTurn = false;
    agentLine = null;
    ducking.end();
  });
  connection.on('audioChunk', (turnId, index, wav, durationMs) => {
    if (turnId !== currentTurnId) return;

    ensurePlayback();
    if (playCtx.state === 'suspended') playCtx.resume();

    if (!playbackStartedThisTurn) {
      playbackStartedThisTurn = true;
      playbackGain.gain.cancelScheduledValues(playCtx.currentTime);
      playbackGain.gain.setValueAtTime(1, playCtx.currentTime);
      ducking.beginPlayback(performance.now());
    }

    playback.enqueue(index, wav, durationMs);
  });
  connection.on('turnCancelled', (turnId, reason, lastPlayedIndex, heardText, removeLastUser) => {
    if (turnId !== currentTurnId) return;

    ducking.cancel();
    playback?.flush();
    playbackStartedThisTurn = false;

    if (agentLine) {
      if (heardText) {
        agentLine.textContent = `${heardText} [interrupted]`;
      } else {
        agentLine.remove();
      }
      agentLine = null;
    }

    if (removeLastUser && lastUserLine) {
      lastUserLine.remove();
      lastUserLine = null;
    }

    note(`interrupted (${reason})`, 'meta');

    // Anything still in flight for this turn (a transcript or delta sent just before/after the
    // cancel) must not land in the next turn's UI.
    currentTurnId = null;
  });

  await connection.start();
}

async function stopCapture() {
  worklet?.disconnect();
  worklet = null;
  sourceNode?.disconnect();
  sourceNode = null;
  mediaStream?.getTracks().forEach((track) => track.stop());
  mediaStream = null;

  if (micContext) {
    await micContext.close().catch(() => {});
    micContext = null;
  }
}

async function startCapture() {
  startBtn.disabled = true;

  try {
    mediaStream = await navigator.mediaDevices.getUserMedia({
      audio: {
        echoCancellation: true,
        noiseSuppression: true,
        autoGainControl: true,
        channelCount: 1,
      },
    });

    micContext = new AudioContext({ sampleRate: 16000 });
    await micContext.audioWorklet.addModule('pcm-worklet.js');

    sourceNode = micContext.createMediaStreamSource(mediaStream);
    worklet = new AudioWorkletNode(micContext, 'pcm-capture');
    worklet.port.onmessage = (event) => {
      if (!connection || connection.state !== signalR.HubConnectionState.Connected) return;
      const bytes = toPcm16(event.data);
      ducking.observe(rmsOf(event.data), performance.now());
      connection.invoke('SendAudio', bytes).catch((err) => console.error(err));
    };

    // An AudioWorkletNode whose output is not connected can be culled by Chromium, so its
    // process() (and therefore all capture) stops. Pull it with a silent sink.
    const sink = micContext.createGain();
    sink.gain.value = 0;
    sink.connect(micContext.destination);
    sourceNode.connect(worklet);
    worklet.connect(sink);

    const rate = micContext.sampleRate;
    ensurePlayback();
    await connection.invoke('StartSession', null, rate);
    setState('listening');
    note(`microphone at ${rate} Hz`);
  } catch (err) {
    await stopCapture();
    throw err;
  }
}

startBtn.addEventListener('click', async () => {
  try {
    await connect();
    await startCapture();
  } catch (err) {
    startBtn.disabled = false;
    await stopCapture();
    await connection?.stop().catch(() => {});
    connection = null;
    note(`could not start: ${err.message || err}`);
  }
});
