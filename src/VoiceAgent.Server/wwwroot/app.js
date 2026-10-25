import { createPlayback } from './playback.js';

const stateEl = document.getElementById('state');
const levelEl = document.getElementById('level');
const fpsEl = document.getElementById('fps');
const logEl = document.getElementById('log');
const startBtn = document.getElementById('start');

let connection;
let micContext;
let worklet;
let mediaStream;

let playCtx;
let playbackGain;
let playback;
let currentTurnId = 0;
let turnStartedAt = 0;
let firstAudioLogged = false;

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
      connection.invoke('SentenceStarted', currentTurnId, index, performance.now()).catch(() => {});
      if (!firstAudioLogged) {
        firstAudioLogged = true;
        note(`first audio ${Math.round(performance.now() - turnStartedAt)} ms`, 'meta');
      }
    },
    onPlayed(index) {
      connection.invoke('SentencePlayed', currentTurnId, index).catch(() => {});
    },
  });
}
let agentLine = null;

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
  connection = new signalR.HubConnectionBuilder()
    .withUrl('/hub/voice')
    .withHubProtocol(new signalR.protocols.msgpack.MessagePackHubProtocol())
    .build();

  connection.on('level', (rms, fps) => {
    levelEl.style.width = `${Math.min(100, rms * 400)}%`;
    fpsEl.textContent = `${fps.toFixed(0)} fps`;
  });
  connection.on('error', (message) => note(message));
  connection.on('stateChanged', (state) => setState(String(state).toLowerCase()));
  connection.on('transcript', (text) => {
    note(text, 'user');
    agentLine = null;
  });
  connection.on('assistantDelta', (text) => {
    if (!agentLine) agentLine = note('', 'agent');
    agentLine.textContent += text;
    logEl.scrollTop = logEl.scrollHeight;
  });
  connection.on('turnStarted', (turnId) => {
    currentTurnId = turnId;
    turnStartedAt = performance.now();
    firstAudioLogged = false;
  });
  connection.on('audioChunk', (index, wav, durationMs) => {
    ensurePlayback();
    if (playCtx.state === 'suspended') playCtx.resume();
    playback.enqueue(index, wav, durationMs);
  });
  connection.on('turnCancelled', (reason) => {
    playback?.flush();
    note(`interrupted (${reason})`, 'meta');
  });

  await connection.start();
}

async function startCapture() {
  startBtn.disabled = true;

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

  const source = micContext.createMediaStreamSource(mediaStream);
  worklet = new AudioWorkletNode(micContext, 'pcm-capture');
  worklet.port.onmessage = (event) => {
    if (!connection || connection.state !== signalR.HubConnectionState.Connected) return;
    const bytes = toPcm16(event.data);
    connection.invoke('SendAudio', bytes).catch((err) => console.error(err));
  };
  source.connect(worklet);

  const rate = micContext.sampleRate;
  ensurePlayback();
  await connection.invoke('StartSession', null, rate);
  setState('listening');
  note(`microphone at ${rate} Hz`);
}

startBtn.addEventListener('click', async () => {
  try {
    await connect();
    await startCapture();
  } catch (err) {
    startBtn.disabled = false;
    note(`could not start: ${err.message || err}`);
  }
});
