'use strict';

// Startup includes resource downloads and extraction, before /health exists.
// Bound silence rather than total runtime, and sample partial-file sizes because
// the server only prints download progress in ten-percent increments.
function createStartupProgress({ now = Date.now, readSizes = () => '' } = {}) {
  let lastProgressAt = now();
  let sizes = readSizes();
  let lastLine = '';
  return {
    observe(line) {
      if (line === lastLine) return;
      lastLine = line;
      lastProgressAt = now();
    },
    sample() {
      const next = readSizes();
      if (next !== sizes) {
        sizes = next;
        lastProgressAt = now();
      }
      return lastProgressAt;
    },
  };
}

async function waitForServerReady({ alive, probe, progressAt, now = Date.now,
  sleep = (ms) => new Promise((resolve) => setTimeout(resolve, ms)), timeoutMs = 90_000 }) {
  while (true) {
    if (!alive()) return { ok: false, error: 'Local server exited during startup. See the server log.' };
    if (await probe()) return { ok: true };
    if (now() - progressAt() >= timeoutMs) {
      return { ok: false, error: `Local server did not become ready and made no startup progress for ${timeoutMs / 1000} seconds. See the server log.` };
    }
    await sleep(500);
  }
}

module.exports = { createStartupProgress, waitForServerReady };
