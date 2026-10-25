// Node tests for the pure ducking controller. Timing is injected, so these exercise the spec's
// 120 ms confirmation / 350 ms restore rules without a browser or a real clock.

import test from 'node:test';
import assert from 'node:assert/strict';
import { createDucking } from '../../src/VoiceAgent.Server/wwwroot/ducking.js';

function controller() {
  const calls = { duck: 0, restore: 0, silence: 0 };
  const ducking = createDucking({
    onDuck: () => calls.duck++,
    onRestore: () => calls.restore++,
    onSilence: () => calls.silence++,
  });
  return { ducking, calls };
}

function calibrate(ducking, level = 0.001) {
  ducking.beginPlayback(0);
  for (let t = 20; t <= 200; t += 20) ducking.observe(level, t);
}

test('calibrates for 200 ms and sets a playback-relative threshold', () => {
  const { ducking } = controller();
  calibrate(ducking, 0.03);
  assert.equal(ducking.phase, 'monitoring');
  assert.ok(ducking.threshold > 0.03, `floor should raise the threshold, got ${ducking.threshold}`);
});

test('a 100 ms cough does not duck', () => {
  const { ducking, calls } = controller();
  calibrate(ducking);
  ducking.observe(0.5, 220);
  ducking.observe(0.5, 270);
  ducking.observe(0.5, 320);
  assert.equal(ducking.phase, 'monitoring');
  assert.equal(calls.duck, 0);
});

test('speech sustained for 120 ms ducks, and restores 350 ms later without a cancel', () => {
  const { ducking, calls } = controller();
  calibrate(ducking);
  ducking.observe(0.5, 220);
  ducking.observe(0.5, 340);
  assert.equal(ducking.phase, 'ducked');
  assert.equal(calls.duck, 1);

  ducking.observe(0.5, 680); // 340 ms after the duck
  assert.equal(calls.restore, 0);
  ducking.observe(0.5, 700);
  assert.equal(calls.restore, 1);
  assert.equal(ducking.phase, 'cooldown');
});

test('a cancelled turn stays silent and never restores', () => {
  const { ducking, calls } = controller();
  calibrate(ducking);
  ducking.observe(0.5, 220);
  ducking.observe(0.5, 340);
  ducking.cancel();
  assert.equal(calls.silence, 1);

  ducking.observe(0.5, 2000);
  ducking.observe(0.5, 5000);
  assert.equal(calls.restore, 0);
  assert.equal(ducking.phase, 'silent');
});

test('a loud echo during calibration does not become the floor', () => {
  const { ducking } = controller();
  ducking.beginPlayback(0);
  ducking.observe(0.2, 20); // user speech during the window
  assert.equal(ducking.phase, 'monitoring');
  assert.equal(ducking.threshold, 0.02);
});

test('a new turn after a cancel calibrates again and can duck', () => {
  const { ducking, calls } = controller();
  calibrate(ducking);
  ducking.observe(0.5, 220);
  ducking.observe(0.5, 340);
  ducking.cancel();

  calibrate(ducking);
  ducking.observe(0.5, 420);
  ducking.observe(0.5, 540);
  assert.equal(calls.duck, 2);
});

test('cooldown needs a quiet frame before it can duck again', () => {
  const { ducking, calls } = controller();
  calibrate(ducking);
  ducking.observe(0.5, 220);
  ducking.observe(0.5, 340);
  ducking.observe(0.5, 700); // restore -> cooldown

  ducking.observe(0.5, 720); // still loud, no re-duck
  assert.equal(calls.duck, 1);

  ducking.observe(0.0, 740); // quiet resets
  ducking.observe(0.5, 760);
  ducking.observe(0.5, 900);
  assert.equal(calls.duck, 2);
});
