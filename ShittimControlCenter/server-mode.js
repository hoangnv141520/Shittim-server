'use strict';

// All OS actions are injected so transitions and failure handling can be tested without
// touching the game, hosts file, running servers or player databases.
function createServerModeController(deps) {
  let switching = false;
  return {
    get switching() { return switching; },
    async switchTo(mode) {
      if (!['official', 'local'].includes(mode)) return { ok: false, error: 'Unknown server mode.' };
      if (switching) return { ok: false, error: 'A server switch is already in progress.' };
      switching = true;
      let changed = false;
      try {
        const check = await deps.preflight();
        if (!check.ok) return check;
        await deps.saveMode('unknown');
        changed = true;
        for (const stop of [deps.stopProxy, deps.stopServer, deps.clearHosts]) {
          const result = await stop();
          if (!result.ok) throw new Error(result.error || 'Could not stop local routing.');
        }
        if (mode === 'official') {
          const restored = await deps.restoreClient();
          if (!restored.ok) throw new Error(restored.error || 'Client restore failed.');
        } else {
          for (const start of [deps.startServer, deps.waitForServer, deps.startProxy, deps.waitForProxy]) {
            const result = await start();
            if (!result.ok) throw new Error(result.error || 'Local server startup failed.');
          }
        }
        await deps.saveMode(mode);
        return { ok: true, mode };
      } catch (e) {
        // A failed local start must not leave a redirector pointing at a dead backend.
        if (changed && mode === 'local') {
          await deps.stopProxy().catch(() => {});
          await deps.stopServer().catch(() => {});
        }
        return { ok: false, mode: 'unknown', error: String(e.message || e) };
      } finally { switching = false; }
    },
  };
}

module.exports = { createServerModeController };
