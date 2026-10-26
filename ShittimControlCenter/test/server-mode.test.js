'use strict';
const test = require('node:test');
const assert = require('node:assert/strict');
const { createServerModeController } = require('../server-mode');

function fixture(overrides = {}) {
  const calls = [];
  const deps = Object.fromEntries(['preflight', 'stopProxy', 'stopServer', 'clearHosts', 'restoreClient',
    'startServer', 'waitForServer', 'startProxy', 'waitForProxy'].map((name) => [name, async () => {
    calls.push(name); return { ok: true };
  }]));
  deps.saveMode = async (mode) => { calls.push(`mode:${mode}`); };
  Object.assign(deps, overrides);
  return { controller: createServerModeController(deps), calls };
}

test('official switch removes all local routing before restoring the client and committing the mode', async () => {
  const { controller, calls } = fixture();
  assert.deepEqual(await controller.switchTo('official'), { ok: true, mode: 'official' });
  assert.deepEqual(calls, ['preflight', 'mode:unknown', 'stopProxy', 'stopServer', 'clearHosts', 'restoreClient', 'mode:official']);
});

test('local switch waits for backend readiness before starting the proxy', async () => {
  const { controller, calls } = fixture();
  assert.equal((await controller.switchTo('local')).ok, true);
  assert.deepEqual(calls, ['preflight', 'mode:unknown', 'stopProxy', 'stopServer', 'clearHosts', 'startServer', 'waitForServer', 'startProxy', 'waitForProxy', 'mode:local']);
});

test('an open game or external process blocks switching without changing anything', async () => {
  const { controller, calls } = fixture({ preflight: async () => ({ ok: false, error: 'Close the game.' }) });
  assert.equal((await controller.switchTo('official')).ok, false);
  assert.deepEqual(calls, []);
});

test('refused hosts elevation prevents official mode from being reported as ready', async () => {
  const { controller, calls } = fixture({ clearHosts: async () => ({ ok: false, error: 'Elevation refused.' }) });
  assert.equal((await controller.switchTo('official')).error, 'Elevation refused.');
  assert.ok(!calls.includes('restoreClient'));
  assert.ok(!calls.includes('mode:official'));
});

test('client restore failure leaves mode unknown and is returned to the UI', async () => {
  const { controller, calls } = fixture({ restoreClient: async () => ({ ok: false, error: 'Original DLL is missing.' }) });
  assert.equal((await controller.switchTo('official')).error, 'Original DLL is missing.');
  assert.ok(!calls.includes('mode:official'));
});

test('failed proxy startup cleans up both processes instead of leaving partial local routing', async () => {
  const { controller, calls } = fixture({ waitForProxy: async () => ({ ok: false, error: 'Proxy failed.' }) });
  assert.equal((await controller.switchTo('local')).ok, false);
  assert.deepEqual(calls.slice(-2), ['stopProxy', 'stopServer']);
  assert.ok(!calls.includes('mode:local'));
});

test('concurrent switches are rejected while the first one is waiting', async () => {
  let release;
  const { controller } = fixture({ preflight: () => new Promise((resolve) => { release = resolve; }) });
  const first = controller.switchTo('official');
  assert.equal((await controller.switchTo('local')).ok, false);
  release({ ok: true });
  assert.equal((await first).ok, true);
  assert.equal(controller.switching, false);
});

test('invalid mode cannot stop services or alter settings', async () => {
  const { controller, calls } = fixture();
  assert.equal((await controller.switchTo('bad-mode')).ok, false);
  assert.deepEqual(calls, []);
});
