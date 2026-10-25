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
let nextStart = 0;
let sources = [];
let turnGeneration = 0;
let turnStartedAt = 0;
let firstAudioLogged = false;

function ensurePlayback() {
  if (playCtx) return;
  // A separate context at the device rate: Kokoro is 24 kHz, and a 16 kHz
  // context would cap it at 8 kHz and muffle every reply.
  playCtx = new AudioContext();
  playbackGain = playCtx.createGain();
  playbackGain.connect(playCtx.destination);
}

function stopPlayback() {
  turnGeneration++;
  for (const src of sources) {
    src.onended = null;   // stop() fires onended too; never report an unplayed sentence
    try { src.stop(); } catch { /* already stopped */ }
  }
  sources = [];
  nextStart = playCtx ? playCtx.currentTime : 0;
}

async function playChunk(index, wavBytes, durationMs) {
  ensurePlayback();
  if (playCtx.state === 'suspended') await playCtx.resume();

  const bytes = wavBytes instanceof Uint8Array ? wavBytes : new Uint8Array(wavBytes);
  const buffer = await playCtx.decodeAudioData(bytes.slice().buffer);

  const generation = turnGeneration;
  const src = playCtx.createBufferSource();
  src.buffer = buffer;
  src.connect(playbackGain);

  const startAt = Math.max(playCtx.currentTime + 0.03, nextStart);
  nextStart = startAt + buffer.duration;
  sources.push(src);

  const leadMs = (startAt - playCtx.currentTime + (playCtx.outputLatency || 0)) * 1000;
  setTimeout(() => {
    if (generation !== turnGeneration) return;
    connection.invoke('SentenceStarted', index, durationMs).catch(() => {});
    if (!firstAudioLogged) {
      firstAudioLogged = true;
      note(`first audio ${Math.round(performance.now() - turnStartedAt)} ms`, 'meta');
    }
  }, Math.max(0, leadMs));

  src.onended = () => {
    src.onended = null;
    const i = sources.indexOf(src);
    if (i >= 0) sources.splice(i, 1);
    if (generation !== turnGeneration) return;
    connection.invoke('SentencePlayed', index).catch(() => {});
  };

  src.start(startAt);
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
  connection.on('turnStarted', () => {
    turnStartedAt = performance.now();
    firstAudioLogged = false;
  });
  connection.on('audioChunk', (index, wav, durationMs) => {
    playChunk(index, wav, durationMs).catch((err) => note(`playback: ${err.message || err}`));
  });
  connection.on('turnCancelled', (reason) => {
    stopPlayback();
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
