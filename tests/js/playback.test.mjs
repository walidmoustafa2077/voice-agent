import test from 'node:test';
import assert from 'node:assert/strict';
import { createPlayback } from '../../src/VoiceAgent.Server/wwwroot/playback.js';

const settle = () => new Promise((resolve) => setImmediate(resolve));

function makeContext() {
  const started = [];
  const decodes = [];
  const pending = new Map();

  const ctx = {
    currentTime: 0,
    outputLatency: 0.01,
    decodeAudioData(buffer) {
      const id = new Uint8Array(buffer)[0];
      decodes.push(id);
      return new Promise((resolve, reject) => pending.set(id, { resolve, reject }));
    },
    createBufferSource() {
      const source = {
        onended: null,
        buffer: null,
        connect() {},
        stop() {},
        start(at) {
          started.push({ id: source.buffer?.id, at });
        },
      };
      return source;
    },
  };

  return {
    ctx,
    started,
    decodes,
    resolveDecode(id) {
      pending.get(id).resolve({ id, duration: 1 });
    },
  };
}

function bytes(id) {
  return new Uint8Array([id, 0, 0, 0]);
}

test('plays chunks in arrival order even when decoding completes out of order', async () => {
  const { ctx, started, decodes, resolveDecode } = makeContext();
  const playback = createPlayback({ ctx, gain: { connect() {} }, onStarted() {}, onPlayed() {} });

  const first = playback.enqueue(0, bytes(0), 1000);
  const second = playback.enqueue(1, bytes(1), 1000);

  await settle();
  assert.deepEqual(decodes, [0], 'decode must be serialized, not fired concurrently');

  resolveDecode(0);
  await settle();
  assert.deepEqual(decodes, [0, 1]);

  resolveDecode(1);
  await Promise.all([first, second]);

  assert.deepEqual(started.map((s) => s.id), [0, 1]);
  assert.ok(started[1].at >= started[0].at + 1, 'second chunk must be scheduled after the first');
});

test('a chunk that was decoding when playback was flushed never starts', async () => {
  const { ctx, started, resolveDecode } = makeContext();
  const playback = createPlayback({ ctx, gain: { connect() {} }, onStarted() {}, onPlayed() {} });

  const pending = playback.enqueue(0, bytes(0), 1000);
  await settle();
  playback.flush();
  resolveDecode(0);
  await pending;

  assert.equal(started.length, 0);
});

test('a chunk decoded after a flush belongs to the new generation and plays', async () => {
  const { ctx, started, resolveDecode } = makeContext();
  const playback = createPlayback({ ctx, gain: { connect() {} }, onStarted() {}, onPlayed() {} });

  playback.flush();
  const queued = playback.enqueue(0, bytes(0), 1000);
  await settle();
  resolveDecode(0);
  await queued;

  assert.deepEqual(started.map((s) => s.id), [0]);
});

test('reports start with the index of the chunk once it is audible', async () => {
  const { ctx, resolveDecode } = makeContext();
  const started = [];
  const played = [];
  const playback = createPlayback({
    ctx,
    gain: { connect() {} },
    onStarted: (index) => started.push(index),
    onPlayed: (index) => played.push(index),
  });

  const queued = playback.enqueue(7, bytes(0), 1000);
  await settle();
  resolveDecode(0);
  await queued;

  await new Promise((resolve) => setTimeout(resolve, 60));
  assert.deepEqual(started, [7]);
  assert.deepEqual(played, []);
});
