// Client-side ducking: energy only, no model and no network round trip. The server's cancel is
// what actually stops speech; this covers the ~300 ms before TurnCancelled arrives.
//
// Driven by mic frames (one call to observe per frame), so the timing rules are testable without
// real timers:
//   - calibrate the echo floor for the first 200 ms of assistant playback
//   - duck after ~120 ms above max(0.02, floor * 2.5)
//   - stay silent once the turn is cancelled
//   - otherwise restore 350 ms after the duck, over the caller's 80 ms ramp

export const HOLD_MS = 120;
export const RESTORE_AFTER_MS = 350;
export const CALIBRATION_MS = 200;
export const MIN_THRESHOLD = 0.02;
export const FLOOR_MULTIPLIER = 2.5;
const CONTAMINATION_LEVEL = 0.08;

export function createDucking({
  onDuck = () => {},
  onRestore = () => {},
  onSilence = () => {},
  holdMs = HOLD_MS,
  restoreAfterMs = RESTORE_AFTER_MS,
  calibrationMs = CALIBRATION_MS,
  minThreshold = MIN_THRESHOLD,
  floorMultiplier = FLOOR_MULTIPLIER,
} = {}) {
  let phase = 'idle'; // idle | calibrating | monitoring | ducked | cooldown | silent
  let calibrationStart = 0;
  let frames = 0;
  let sum = 0;
  let floor = 0;
  let overSince = null;
  let duckedAt = 0;
  let threshold = minThreshold;

  function beginPlayback(now) {
    phase = 'calibrating';
    calibrationStart = now;
    frames = 0;
    sum = 0;
    floor = 0;
    overSince = null;
    threshold = minThreshold;
  }

  function observe(level, now) {
    if (phase === 'idle' || phase === 'silent') return;

    if (phase === 'calibrating') {
      if (level > CONTAMINATION_LEVEL) {
        // The user started talking during the window; do not let their voice become the floor.
        floor = 0;
        finishCalibration();
        return;
      }

      sum += level;
      frames++;
      if (now - calibrationStart >= calibrationMs) {
        floor = frames > 0 ? sum / frames : 0;
        finishCalibration();
      }
      return;
    }

    const above = level > threshold;

    if (phase === 'cooldown') {
      if (!above) {
        phase = 'monitoring';
        overSince = null;
      }
      return;
    }

    if (phase === 'ducked') {
      if (now - duckedAt >= restoreAfterMs) {
        phase = 'cooldown';
        onRestore();
      }
      return;
    }

    if (!above) {
      overSince = null;
      return;
    }

    if (overSince === null) overSince = now;

    if (now - overSince >= holdMs) {
      phase = 'ducked';
      duckedAt = now;
      onDuck();
    }
  }

  function cancel() {
    if (phase === 'idle' || phase === 'silent') return;
    phase = 'silent';
    onSilence();
  }

  // The turn is over and was not cancelled: leave gain where the caller expects it. A cancelled
  // turn stays silent until the next playback begins.
  function end() {
    if (phase === 'idle') return;

    if (phase === 'ducked') onRestore();
    phase = 'idle';
    overSince = null;
  }

  function finishCalibration() {
    threshold = Math.max(minThreshold, floor * floorMultiplier);
    phase = 'monitoring';
    overSince = null;
  }

  return {
    beginPlayback,
    observe,
    cancel,
    end,
    get phase() {
      return phase;
    },
    get threshold() {
      return threshold;
    },
  };
}
