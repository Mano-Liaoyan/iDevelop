#!/usr/bin/env node
// Runs the installed coding clients against scratch Git repositories to observe what the node model relies on.
// Each run spends subscription quota, so CI never runs it. Usage:
//   node scripts/probe-clients.mjs [--clients claude,codex,pi,agy] [--cases resume,stop,block,readonly,resume-readonly,slash] [--samples 2]
// The models default to small ones and can be changed with PROBE_CLAUDE_MODEL, PROBE_CODEX_MODEL, PROBE_PI_MODEL, and
// PROBE_AGY_MODEL. The scratch repositories stay under the system temporary folder for inspection.
import { execFileSync, spawn } from 'node:child_process';
import { existsSync, mkdirSync, mkdtempSync, readFileSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { dirname, join } from 'node:path';
import { parseArgs } from 'node:util';

const env = process.env;

function jsonLines(text) {
  return text.split('\n').flatMap((line) => {
    try {
      return [JSON.parse(line)];
    } catch {
      return [];
    }
  });
}

const clients = {
  claude: {
    command: 'claude',
    model: env.PROBE_CLAUDE_MODEL ?? 'claude-haiku-4-5',
    args: (model, { resume, readOnly }) => [
      '-p', '--output-format', 'stream-json', '--verbose', '--model', model,
      '--permission-mode', readOnly ? 'plan' : 'acceptEdits',
      ...(resume ? ['--resume', resume] : []),
    ],
    stdin: (prompt) => prompt,
    session: (events) => events.find((e) => e.type === 'system' && e.subtype === 'init')?.session_id,
    finalText: (events) => events.findLast((e) => e.type === 'result')?.result,
    commandFile: () => ['.claude/commands/probe-mark.md', '/probe-mark'],
  },
  codex: {
    command: 'codex',
    model: env.PROBE_CODEX_MODEL ?? 'gpt-5.6-luna',
    args: (model, { resume, readOnly }) => {
      const sandbox = readOnly ? 'read-only' : 'workspace-write';
      // A user's approval_policy can approve a sandbox escalation, so the sandbox holds only with approvals off.
      const common = ['--json', '-m', model, '-c', 'model_reasoning_effort=low', '-c', 'approval_policy=never', '--skip-git-repo-check'];
      return resume
        ? ['exec', 'resume', ...common, '-c', `sandbox_mode=${sandbox}`, resume, '-']
        : ['exec', ...common, '--sandbox', sandbox, '-'];
    },
    stdin: (prompt) => prompt,
    session: (events) => events.find((e) => e.type === 'thread.started')?.thread_id,
    finalText: (events) => events.findLast((e) => e.type === 'item.completed' && e.item?.type === 'agent_message')?.item.text,
    commandFile: () => ['.agents/skills/probe-mark/SKILL.md', '$probe-mark'],
  },
  pi: {
    command: 'pi',
    model: env.PROBE_PI_MODEL ?? 'openai-codex/gpt-5.6-luna',
    readOnlyUnsupported: true,
    args: (model, { resume }) => [
      '-p', '--mode', 'json', '--model', model, '--thinking', 'low',
      ...(resume ? ['--session-id', resume] : []),
    ],
    stdin: (prompt) => prompt,
    session: (events) => events.find((e) => e.type === 'session')?.id,
    finalText: (events) => {
      const message = events.findLast((e) => e.type === 'message_end' && e.message?.role === 'assistant')?.message;
      return message?.content?.filter((part) => part.type === 'text').map((part) => part.text).join('');
    },
    commandFile: () => ['.pi/prompts/probe-mark.md', '/probe-mark'],
  },
  agy: {
    command: 'agy',
    model: env.PROBE_AGY_MODEL ?? 'gemini-3.8-flash',
    args: (model, { resume, readOnly }) => [
      '--input-format', 'stream-json', '--output-format', 'stream-json', '--model', model, '--effort', 'low',
      ...(readOnly ? [] : ['--mode', 'accept-edits']),
      '--print=',
      ...(resume ? ['--conversation', resume] : []),
    ],
    stdin: (prompt) => `${JSON.stringify({ event: 'user', message: { role: 'user', content: prompt } })}\n`,
    session: (events) => events.find((e) => e.event === 'init')?.conversation_id,
    resultSession: (events) => events.findLast((e) => e.event === 'result')?.result?.conversation_id,
    finalText: (events) => events.findLast((e) => e.event === 'result')?.result?.response,
    commandFile: () => ['.agents/skills/probe-mark/SKILL.md', '/probe-mark'],
    // On 2026-10-05 a conversation started with accept-edits kept writing when resumed without it and with --mode plan.
    resumeKeepsStartMode: true,
  },
};

function scratchRepo(name) {
  const dir = mkdtempSync(join(tmpdir(), `idp-probe-${name}-`));
  execFileSync('git', ['init', '-q', dir]);
  return dir;
}

function stopTree(child) {
  if (process.platform === 'win32') {
    execFileSync('taskkill', ['/T', '/F', '/PID', String(child.pid)], { stdio: 'ignore' });
  } else {
    process.kill(-child.pid, 'SIGKILL');
  }
}

// One client process. stopWhen, if given, is polled and stops the whole process tree once it returns true.
function turn(client, repo, prompt, options = {}) {
  return new Promise((resolve) => {
    const started = Date.now();
    const child = spawn(client.command, client.args(client.model, options), {
      cwd: repo,
      detached: process.platform !== 'win32',
      shell: process.platform === 'win32',
      stdio: ['pipe', 'pipe', 'pipe'],
    });
    let stdout = '';
    let stderr = '';
    let stopped = false;
    child.stdout.on('data', (chunk) => (stdout += chunk));
    child.stderr.on('data', (chunk) => (stderr += chunk));
    child.stdin.end(client.stdin(prompt));
    const poll = options.stopWhen
      ? setInterval(() => {
          if (!stopped && options.stopWhen()) {
            stopped = true;
            setTimeout(() => stopTree(child), 1500);
          }
        }, 200)
      : null;
    const limit = setTimeout(() => {
      stopped = true;
      stopTree(child);
    }, 300_000);
    child.on('close', (code) => {
      clearInterval(poll);
      clearTimeout(limit);
      const events = jsonLines(stdout);
      writeFileSync(join(repo, `.probe-${Date.now()}.out`), `${stdout}\n---stderr---\n${stderr}`);
      resolve({
        code,
        stopped,
        seconds: Math.round((Date.now() - started) / 100) / 10,
        session: client.session(events),
        resultSession: client.resultSession?.(events),
        finalText: client.finalText(events) ?? '',
      });
    });
  });
}

const read = (repo, file) => (existsSync(join(repo, file)) ? readFileSync(join(repo, file), 'utf8').trim() : null);

const blockPattern = /```idevelop[ \t]*\n([\s\S]*?)\n```\s*$/;

const cases = {
  async resume(client, name) {
    const repo = scratchRepo(name);
    const first = await turn(client, repo, 'Do not create or edit any file in this turn. Ask me exactly one short question: which fruit should go into answer.txt? Then end your turn and wait for my answer.');
    const second = await turn(client, repo, 'banana. Now write that one word, lowercase, into answer.txt in the current folder and stop.', { resume: first.session });
    return {
      pass: read(repo, 'answer.txt') === 'banana' && Boolean(first.session),
      detail: { session: first.session, sameSession: second.session === undefined || second.session === first.session, resultSession: first.resultSession, turns: [first.seconds, second.seconds], repo },
    };
  },

  async stop(client, name) {
    const repo = scratchRepo(name);
    const first = await turn(
      client,
      repo,
      'Use your file writing tool, not shell commands. First create started.txt containing 1. Then create f01.txt through f20.txt one at a time, each containing its own number.',
      { stopWhen: () => existsSync(join(repo, 'started.txt')) },
    );
    const second = await turn(
      client,
      repo,
      'Your previous turn was stopped on purpose. Do not create any more f files. Create resumed.txt containing ok, then stop.',
      { resume: first.session },
    );
    return {
      pass: first.stopped && Boolean(first.session) && read(repo, 'resumed.txt') === 'ok',
      detail: { stopped: first.stopped, session: first.session, secondExit: second.code, turns: [first.seconds, second.seconds], repo },
    };
  },

  async block(client, name, samples) {
    const results = [];
    for (let i = 0; i < samples; i++) {
      const repo = scratchRepo(name);
      const reply = await turn(
        client,
        repo,
        [
          'Do not create or edit any file. You need to know which fruit belongs in answer.txt, so ask me instead of deciding.',
          'End your final message with this block and nothing after it, where the question is your own:',
          '```idevelop',
          '{"status": "asking", "question": "..."}',
          '```',
        ].join('\n'),
      );
      const match = reply.finalText.trim().match(blockPattern);
      let parsed = null;
      try {
        parsed = match ? JSON.parse(match[1]) : null;
      } catch {}
      results.push(parsed?.status === 'asking' && typeof parsed.question === 'string' && parsed.question.length > 0);
    }
    return { pass: results.every(Boolean), detail: { readable: `${results.filter(Boolean).length}/${samples}` } };
  },

  async readonly(client, name) {
    if (client.readOnlyUnsupported) return { pass: null, detail: 'no read-only mode' };
    const repo = scratchRepo(name);
    const reply = await turn(client, repo, 'Create the file readonly.txt containing x.', { readOnly: true });
    return { pass: read(repo, 'readonly.txt') === null, detail: { exit: reply.code, repo } };
  },

  // The session starts in the mode that may write, so a resume that kept the session's own mode would write the file.
  // For a client known to keep that mode, a write is the known limitation seen again, and no write means
  // docs/agent-clients.md is out of date.
  async 'resume-readonly'(client, name) {
    if (client.readOnlyUnsupported) return { pass: null, detail: 'no read-only mode' };
    const repo = scratchRepo(name);
    const first = await turn(client, repo, 'Do not create or edit any file in this turn. Reply with the single word ready.');
    if (!first.session) return { pass: false, detail: { session: null, repo } };
    const second = await turn(client, repo, 'Create the file readonly.txt containing x.', { resume: first.session, readOnly: true });
    const sameSession = second.session === undefined || second.session === first.session;
    const written = read(repo, 'readonly.txt');
    const detail = { session: first.session, sameSession, exit: second.code, written, finalText: second.finalText, repo };
    if (client.resumeKeepsStartMode && written !== null) {
      return { pass: null, detail: { ...detail, reason: 'the resumed read-only turn wrote readonly.txt, the known limitation that docs/agent-clients.md records' } };
    }

    return {
      pass: sameSession && written === null,
      detail: client.resumeKeepsStartMode ? { ...detail, note: 'the resumed read-only turn wrote nothing, so docs/agent-clients.md needs updating' } : detail,
    };
  },

  async slash(client, name) {
    const repo = scratchRepo(name);
    const [file, prompt] = client.commandFile();
    mkdirSync(join(repo, dirname(file)), { recursive: true });
    const body = 'Create the file slash.txt containing ran. Do nothing else.\n';
    writeFileSync(
      join(repo, file),
      file.endsWith('SKILL.md') ? `---\nname: probe-mark\ndescription: Marks the repository when invoked.\n---\n\n${body}` : body,
    );
    const reply = await turn(client, repo, prompt);
    return { pass: read(repo, 'slash.txt') === 'ran', detail: { prompt, file, exit: reply.code, repo } };
  },
};

const { values } = parseArgs({
  options: {
    clients: { type: 'string', default: Object.keys(clients).join(',') },
    cases: { type: 'string', default: Object.keys(cases).join(',') },
    samples: { type: 'string', default: '2' },
  },
});

const chosen = values.clients.split(',');
const runs = await Promise.all(
  chosen.map(async (name) => {
    const rows = [];
    for (const kase of values.cases.split(',')) {
      const result = await cases[kase](clients[name], name, Number(values.samples));
      rows.push({ client: name, case: kase, ...result });
      console.log(JSON.stringify(rows.at(-1)));
    }
    return rows;
  }),
);

const failed = runs.flat().filter((row) => row.pass === false);
console.log(`${runs.flat().length} checks, ${failed.length} failed: ${failed.map((row) => `${row.client} ${row.case}`).join(', ') || 'none'}`);
process.exitCode = failed.length > 0 ? 1 : 0;
