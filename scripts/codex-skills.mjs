import { spawn, execFileSync } from 'node:child_process';
import fs from 'node:fs';
import path from 'node:path';

function codexCommand() {
  if (process.platform !== 'win32') return ['codex'];
  const candidates = execFileSync('where.exe', ['codex'], { encoding: 'utf8', windowsHide: true })
    .trim().split(/\r?\n/);
  const native = candidates.find(file => file.toLowerCase().endsWith('.exe'));
  if (native) return [native];
  for (const shim of candidates) {
    const entry = path.join(path.dirname(shim), 'node_modules/@openai/codex/bin/codex.js');
    if (fs.existsSync(entry)) return [process.execPath, entry];
  }
  throw new Error('Cannot resolve Codex. Use the native install or the standard @openai/codex npm install.');
}

export function listSkills(cwds, configOverrides = []) {
  return new Promise((resolve, reject) => {
    const [executable, ...prefix] = codexCommand();
    const child = spawn(executable, [...prefix, 'app-server', '--stdio',
      ...configOverrides.flatMap(value => ['-c', value])], {
      stdio: ['pipe', 'pipe', 'pipe'], windowsHide: true,
    });
    let buffer = '';
    let finished = false;
    const timer = setTimeout(() => finish(new Error('Codex skills/list timed out')), 30000);
    function finish(error, value) {
      if (finished) return;
      finished = true;
      clearTimeout(timer);
      child.stdin.end();
      child.kill();
      if (error) reject(error);
      else resolve(value);
    }
    const send = message => child.stdin.write(JSON.stringify(message) + '\n');
    child.on('error', error => finish(error));
    child.on('exit', code => {
      if (!finished) finish(new Error(`Codex exited before skills/list completed (${code})`));
    });
    child.stdin.on('error', error => finish(error));
    child.stderr.resume();
    child.stdout.on('data', data => {
      buffer += data;
      let end;
      while ((end = buffer.indexOf('\n')) >= 0) {
        const line = buffer.slice(0, end);
        buffer = buffer.slice(end + 1);
        let message;
        try { message = JSON.parse(line); } catch { continue; }
        if (message.error) return finish(new Error(JSON.stringify(message.error)));
        if (message.id === 1) {
          send({ method: 'initialized', params: {} });
          send({ id: 2, method: 'skills/list', params: { cwds, forceReload: true } });
        }
        if (message.id === 2) finish(null, message.result.data);
      }
    });
    send({ id: 1, method: 'initialize', params: {
      clientInfo: { name: 'idevelop-pstack-check', version: '1.0.0' },
      capabilities: { experimentalApi: true },
    } });
  });
}
