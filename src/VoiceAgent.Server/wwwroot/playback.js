// Serialized playback queue: chunks decode and start in arrival order, and a flush (barge-in)
// invalidates every chunk that was in flight, including ones whose decode finishes afterwards.

export function createPlayback({ ctx, gain, onStarted, onPlayed }) {
  let generation = 0;
  let nextStart = 0;
  let chain = Promise.resolve();
  const sources = [];

  function flush() {
    generation += 1;
    for (const source of sources) {
      source.onended = null;
      try {
        source.stop();
      } catch {
        // already stopped
      }
    }
    sources.length = 0;
    nextStart = 0;
    chain = Promise.resolve();
  }

  function enqueue(index, bytes, durationMs) {
    const mine = generation;

    chain = chain
      .then(async () => {
        if (mine !== generation) return;

        const buffer = await ctx.decodeAudioData(bytes.slice().buffer);
        if (mine !== generation) return;

        const source = ctx.createBufferSource();
        source.buffer = buffer;
        source.connect(gain);

        const startAt = Math.max(ctx.currentTime + 0.03, nextStart);
        nextStart = startAt + (buffer.duration || durationMs / 1000);

        const audibleInMs = Math.max(0, (startAt - ctx.currentTime + (ctx.outputLatency || 0)) * 1000);
        setTimeout(() => {
          if (mine === generation) onStarted(index);
        }, audibleInMs);

        source.onended = () => {
          const at = sources.indexOf(source);
          if (at >= 0) sources.splice(at, 1);
          if (mine === generation) onPlayed(index);
        };

        sources.push(source);
        source.start(startAt);
      })
      .catch(() => {
        // a decode failure drops that sentence; the watchdog ends the turn
      });

    return chain;
  }

  return {
    enqueue,
    flush,
    get generation() {
      return generation;
    },
  };
}
