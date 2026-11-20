import { spawn } from 'node:child_process';
import { existsSync } from 'node:fs';
import { dirname, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const __dirname = dirname(fileURLToPath(import.meta.url));
const overlayExe = resolve(__dirname, '..', 'outputs', 'Codex-InputUsage-Overlay.exe');

function launchOverlay() {
  if (!existsSync(overlayExe)) return;
  try {
    const child = spawn(overlayExe, ['--exit-with-codex'], {
      detached: true,
      stdio: 'ignore',
      windowsHide: true,
    });
    child.unref();
  } catch {
    // Never fail MCP handshake if overlay cannot be spawned.
  }
}

launchOverlay();

// Minimal zero-tool MCP stdio server so Codex starts cleanly with 0 warnings and 0 prompt overhead.
let rawBuf = Buffer.alloc(0);
let useContentLength = false;

function sendJson(msg) {
  const payload = JSON.stringify(msg);
  if (useContentLength) {
    const bytes = Buffer.byteLength(payload, 'utf8');
    process.stdout.write(`Content-Length: ${bytes}\r\n\r\n${payload}`);
  } else {
    process.stdout.write(`${payload}\n`);
  }
}

function handleMessage(raw) {
  let req;
  try {
    req = JSON.parse(raw);
  } catch {
    return;
  }
  if (!req || typeof req !== 'object') return;
  const { id, method, params } = req;
  if (id === undefined || id === null) return; // notification

  if (method === 'initialize') {
    const protocolVersion =
      params && typeof params.protocolVersion === 'string' && params.protocolVersion
        ? params.protocolVersion
        : '2024-11-05';
    sendJson({
      jsonrpc: '2.0',
      id,
      result: {
        protocolVersion,
        capabilities: {},
        serverInfo: { name: 'codex-input-usage', version: '1.0.0' },
      },
    });
    return;
  }
  if (method === 'tools/list') {
    sendJson({ jsonrpc: '2.0', id, result: { tools: [] } });
    return;
  }
  if (method === 'resources/list') {
    sendJson({ jsonrpc: '2.0', id, result: { resources: [] } });
    return;
  }
  if (method === 'prompts/list') {
    sendJson({ jsonrpc: '2.0', id, result: { prompts: [] } });
    return;
  }
  if (method === 'ping') {
    sendJson({ jsonrpc: '2.0', id, result: {} });
    return;
  }
  sendJson({
    jsonrpc: '2.0',
    id,
    error: { code: -32601, message: `Method not found: ${method}` },
  });
}

function drainBuffer() {
  while (rawBuf.length > 0) {
    // Detect Content-Length framing vs newline-delimited JSON
    const head = rawBuf.subarray(0, Math.min(rawBuf.length, 32)).toString('ascii');
    if (/^Content-Length:/i.test(head)) {
      useContentLength = true;
      const sepIdx = rawBuf.indexOf('\r\n\r\n');
      if (sepIdx < 0) return;
      const headerText = rawBuf.subarray(0, sepIdx).toString('ascii');
      const m = headerText.match(/Content-Length:\s*(\d+)/i);
      if (!m) {
        rawBuf = rawBuf.subarray(sepIdx + 4);
        continue;
      }
      const len = Number(m[1]);
      const bodyStart = sepIdx + 4;
      if (rawBuf.length < bodyStart + len) return;
      const body = rawBuf.subarray(bodyStart, bodyStart + len).toString('utf8');
      rawBuf = rawBuf.subarray(bodyStart + len);
      handleMessage(body);
    } else {
      const nlIdx = rawBuf.indexOf(0x0a);
      if (nlIdx < 0) return;
      const line = rawBuf.subarray(0, nlIdx).toString('utf8').trim();
      rawBuf = rawBuf.subarray(nlIdx + 1);
      if (line) handleMessage(line);
    }
  }
}

process.stdin.on('data', (chunk) => {
  rawBuf = Buffer.concat([rawBuf, Buffer.isBuffer(chunk) ? chunk : Buffer.from(chunk, 'utf8')]);
  drainBuffer();
});

process.stdin.on('end', () => process.exit(0));
process.stdin.on('close', () => process.exit(0));
process.stdin.on('error', () => process.exit(0));
process.stdin.resume();
