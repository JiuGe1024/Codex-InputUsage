import fs from 'node:fs/promises';
import path from 'node:path';
import os from 'node:os';
import { fileURLToPath } from 'node:url';
import { connect } from '../src/cdp.mjs';
import { findRollout, RolloutReader } from '../src/rollout-reader.mjs';
import { locateComposer, installInputUsage } from '../src/input-usage-view.mjs';

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const statusPath = path.join(root, 'work', 'tokenbar-state.json');
const stopPath = path.join(root, 'work', 'tokenbar.stop');
const port = Number(process.argv[2] ?? 9337);
if (!Number.isInteger(port) || port < 1024 || port > 65535) throw new Error('Invalid CDP port');
let session, reader, threadId, stopped = false, attached = false;
const status = async (phase, extra = {}) => fs.writeFile(statusPath, JSON.stringify({ phase, pid: process.pid, updatedAt: new Date().toISOString(), ...extra }, null, 2));
process.on('SIGINT', () => { stopped = true; });
process.on('SIGTERM', () => { stopped = true; });
async function shouldStop() {
  if (stopped) return true;
  try { await fs.access(stopPath); return true; }
  catch (error) { if (error.code !== 'ENOENT') throw error; return false; }
}
async function install() {
  const result = await session.evaluate(`(${installInputUsage})(${locateComposer})`);
  if (!result?.ok) throw Object.assign(new Error(result?.reason ?? 'Usage view installation failed'), { code: 'PAGE_INCOMPATIBLE' });
}
async function run() {
  await fs.mkdir(path.join(root, 'work'), { recursive: true });
  if (await shouldStop()) return;
  // The PowerShell owner verifies the official process and listener before
  // every attempt. Return transport failures to that owner for bounded retry.
  session = await connect(port);
  await session.evaluate('window.__codexInputUsage?.stop(); true');
  await install();
  attached = true;
  const emptyRows = [];
  let missingRetryAt = 0, dataError = null, projectedState, projectedOffset, cachedRows, sentThread, sentRows;
  while (!(await shouldStop())) {
    const context = await session.evaluate('({page:window.__codexInputUsage?.context()})');
    if (!context.page) {
      await install();
      sentRows = undefined;
      await status('waiting-codex-page', { reason: 'usage-view-reinstalled' });
      await new Promise((resolve) => setTimeout(resolve, 1000));
      continue;
    }
    const page = context.page;
    if (page.threadId !== threadId) {
      reader = projectedState = null;
      cachedRows = emptyRows;
      threadId = page.threadId;
      missingRetryAt = 0;
      dataError = null;
    }
    let snapshot = { threadId, rows: emptyRows };
    try {
      if (threadId && !reader && Date.now() >= missingRetryAt) {
        const file = await findRollout(path.join(os.homedir(), '.codex', 'sessions'), threadId);
        if (file) { reader = new RolloutReader(file, threadId); dataError = null; }
        else missingRetryAt = Date.now() + 10000;
      }
      if (reader) {
        await reader.poll();
        if (projectedState !== reader.state || projectedOffset !== reader.offset) {
          projectedState = reader.state;
          projectedOffset = reader.offset;
          cachedRows = reader.state.snapshot([...reader.state.turns.keys()]).rows;
        }
        snapshot.rows = cachedRows;
      }
    } catch (error) {
      reader = null;
      missingRetryAt = Date.now() + 10000;
      dataError = error.code ?? 'session-read-failed';
    }
    const sameData = sentThread === snapshot.threadId && sentRows === snapshot.rows;
    const payload = sameData ? { threadId } : snapshot;
    const result = await session.evaluate(`window.__codexInputUsage.update(${JSON.stringify(payload)})`);
    sentThread = snapshot.threadId;
    sentRows = result.mountedRows ? snapshot.rows : undefined;
    if (!result?.ok) throw Object.assign(new Error('Usage view update failed'), { code: 'PAGE_INCOMPATIBLE' });
    const reason = !threadId ? page.reason : !reader ? 'session-data-unavailable'
      : !snapshot.rows.length ? page.reason ?? 'no-completed-turn-data' : !result.mountedRows ? result.reason ?? 'no-visible-compatible-anchor' : null;
    await status(!threadId ? 'waiting-codex-page' : result.mountedRows ? 'running' : 'waiting-statistics', { reason, mountedRows: result.mountedRows,
      completedRounds: reader ? [...reader.state.turns.values()].filter((t) => t.complete).length : 0,
      warnings: reader?.state.warningCounts ?? {}, dataError, layout: result.diagnostics ?? {} });
    await new Promise((resolve) => setTimeout(resolve, 1000));
  }
}
try {
  await run();
  await status('stopped');
} catch (error) {
  const recoverable = ['CDP_TRANSPORT', 'CDP_TARGET_UNAVAILABLE'].includes(error.code);
  await status(recoverable ? 'disconnected-or-failed' : 'incompatible-or-failed', { reason: error.message, code: error.code ?? 'RUNTIME_ERROR' }).catch(() => {});
  process.exitCode = recoverable ? attached ? 2 : 1 : 4;
} finally {
  if (session) { await session.evaluate('window.__codexInputUsage?.stop()').catch(() => {}); session.close(); }
}
