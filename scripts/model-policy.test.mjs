import assert from 'node:assert/strict';
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { spawnSync } from 'node:child_process';
import { test } from 'node:test';
import { validateModelPolicy, resolveRole } from './model-policy.mjs';

function requestedPolicy() {
  return {
    schemaVersion: 2,
    policy: 'quality-first',
    budget: 'large',
    reasoningPolicy: { ceiling: 'xhigh', allowImplicit: false },
    reviewPolicy: { crossProvider: true, onUnavailable: 'block' },
    models: {
      astra: {
        provider: 'openai', client: 'codex', requestedName: 'GPT-6 Astra',
        requestedModel: 'gpt-6-astra', model: 'gpt-6-astra',
        reasoningEffort: 'xhigh', verification: 'native-catalog',
      },
      opus: {
        provider: 'anthropic', client: 'claude', requestedName: 'Claude Opus 5.5',
        requestedModel: 'claude-opus-5-5', model: null,
        reasoningEffort: 'xhigh', verification: 'pending-client',
      },
      gemini: {
        provider: 'google', client: 'gemini', requestedName: 'Gemini 3.8 Flash',
        requestedModel: 'gemini-3.8-flash', model: null,
        reasoningEffort: 'high', verification: 'pending-client',
      },
    },
    roles: {
      'backend-implementation': ['astra'],
      'frontend-implementation': ['opus'],
      'backend-review': ['opus'],
      'frontend-review': ['gemini', 'astra'],
      judgment: ['astra', 'opus'],
      'difficult-task-review': ['astra', 'opus'],
      exploration: ['astra'],
    },
  };
}

function cli(args, config = requestedPolicy()) {
  const directory = fs.mkdtempSync(path.join(os.tmpdir(), 'idevelop-model-policy-'));
  try {
    fs.mkdirSync(path.join(directory, 'scripts'));
    fs.mkdirSync(path.join(directory, '.pstack'));
    const script = path.join(directory, 'scripts/model-policy.mjs');
    fs.copyFileSync(new URL('./model-policy.mjs', import.meta.url), script);
    fs.writeFileSync(path.join(directory, '.pstack/models.json'), JSON.stringify(config));
    return spawnSync(process.execPath, [script, ...args], { cwd: os.tmpdir(), encoding: 'utf8', windowsHide: true });
  } finally {
    fs.rmSync(directory, { recursive: true, force: true });
  }
}

test('the requested policy validates while external clients remain pending', () => {
  assert.equal(validateModelPolicy(requestedPolicy()), true);
});

test('backend implementation resolves to the exact explicit native participant', () => {
  const config = requestedPolicy();
  const before = structuredClone(config);
  assert.deepEqual(resolveRole(config, 'backend-implementation'), {
    status: 'ready', role: 'backend-implementation',
    participants: [{ key: 'astra', provider: 'openai', client: 'codex', model: 'gpt-6-astra', reasoningEffort: 'xhigh' }],
  });
  assert.deepEqual(config, before);
});

test('frontend review reports missing Gemini without returning the runnable Astra participant', () => {
  assert.deepEqual(resolveRole(requestedPolicy(), 'frontend-review'), {
    status: 'blocked', role: 'frontend-review',
    missing: [{
      key: 'gemini', provider: 'google', client: 'gemini', requestedName: 'Gemini 3.8 Flash',
      requestedModel: 'gemini-3.8-flash', reasoningEffort: 'high', verification: 'pending-client',
    }],
  });
});

test('a blocked panel reports every missing participant', () => {
  const config = requestedPolicy();
  config.models.astra.model = null;
  config.models.astra.verification = 'pending-client';
  assert.deepEqual(resolveRole(config, 'judgment'), {
    status: 'blocked', role: 'judgment',
    missing: [
      { key: 'astra', provider: 'openai', client: 'codex', requestedName: 'GPT-6 Astra',
        requestedModel: 'gpt-6-astra', reasoningEffort: 'xhigh', verification: 'pending-client' },
      { key: 'opus', provider: 'anthropic', client: 'claude', requestedName: 'Claude Opus 5.5',
        requestedModel: 'claude-opus-5-5', reasoningEffort: 'xhigh', verification: 'pending-client' },
    ],
  });
});

test('a fixture declaring external client verification resolves the complete frontend panel', () => {
  const config = requestedPolicy();
  // This fixture exercises declared verification; it is not evidence of provider access.
  config.models.gemini.model = 'gemini-3.8-flash';
  config.models.gemini.verification = 'client-catalog';
  assert.deepEqual(resolveRole(config, 'frontend-review'), {
    status: 'ready', role: 'frontend-review',
    participants: [
      { key: 'gemini', provider: 'google', client: 'gemini', model: 'gemini-3.8-flash', reasoningEffort: 'high' },
      { key: 'astra', provider: 'openai', client: 'codex', model: 'gpt-6-astra', reasoningEffort: 'xhigh' },
    ],
  });
});

test('registry keys may be renamed without changing selected model identities', () => {
  const config = requestedPolicy();
  config.models.backend = config.models.astra;
  delete config.models.astra;
  for (const [role, participants] of Object.entries(config.roles)) {
    config.roles[role] = participants.map(key => key === 'astra' ? 'backend' : key);
  }
  assert.deepEqual(resolveRole(config, 'backend-implementation'), {
    status: 'ready', role: 'backend-implementation',
    participants: [{ key: 'backend', provider: 'openai', client: 'codex', model: 'gpt-6-astra', reasoningEffort: 'xhigh' }],
  });
});

test('an explicit model change remains a configuration edit', () => {
  const config = requestedPolicy();
  config.models.astra.requestedName = 'Replacement OpenAI model';
  config.models.astra.requestedModel = 'replacement-openai-model';
  config.models.astra.model = 'replacement-openai-model';
  assert.deepEqual(resolveRole(config, 'backend-implementation'), {
    status: 'ready', role: 'backend-implementation',
    participants: [{ key: 'astra', provider: 'openai', client: 'codex', model: 'replacement-openai-model', reasoningEffort: 'xhigh' }],
  });
});

test('a distinct model from the author provider cannot satisfy backend review', () => {
  const config = requestedPolicy();
  config.models.second = { ...config.models.astra, requestedModel: 'second-model', model: 'second-model' };
  config.roles['backend-review'] = ['second'];
  assert.throws(() => validateModelPolicy(config), /review must use providers different from its author/);
});

for (const value of ['max', 'ultra', 'auto', 'inherit-parent', 'unknown', null, undefined]) {
  test(`rejects model reasoning effort ${String(value)}`, () => {
    const config = requestedPolicy();
    config.models.opus.reasoningEffort = value;
    assert.throws(() => validateModelPolicy(config), /reasoningEffort must be explicitly/);
  });
  test(`rejects policy ceiling ${String(value)}`, () => {
    const config = requestedPolicy();
    config.reasoningPolicy.ceiling = value;
    assert.throws(() => validateModelPolicy(config), /reasoningPolicy.ceiling must be/);
  });
}

test('rejects missing effort and efforts above a lowered ceiling', () => {
  const config = requestedPolicy();
  delete config.models.astra.reasoningEffort;
  assert.throws(() => validateModelPolicy(config), /models.astra must contain exactly/);
  const lowered = requestedPolicy();
  lowered.reasoningPolicy.ceiling = 'high';
  assert.throws(() => validateModelPolicy(lowered), /exceeds the policy ceiling/);
});

test('accepts explicit lower efforts within the lowered ceiling', () => {
  const config = requestedPolicy();
  config.reasoningPolicy.ceiling = 'medium';
  config.models.astra.reasoningEffort = 'medium';
  config.models.opus.reasoningEffort = 'low';
  config.models.gemini.reasoningEffort = 'medium';
  assert.deepEqual(resolveRole(config, 'backend-implementation'), {
    status: 'ready', role: 'backend-implementation',
    participants: [{ key: 'astra', provider: 'openai', client: 'codex', model: 'gpt-6-astra', reasoningEffort: 'medium' }],
  });
});

test('rejects Gemini xhigh even though the global ceiling permits it', () => {
  const config = requestedPolicy();
  config.models.gemini.reasoningEffort = 'xhigh';
  assert.throws(() => validateModelPolicy(config), /Gemini's high limit/);
});

for (const [name, mutate, message] of [
  ['implicit selection', config => { config.reasoningPolicy.allowImplicit = true; }, /allowImplicit must be false/],
  ['same-provider fallback', config => { config.reviewPolicy.crossProvider = false; }, /crossProvider must be true/],
  ['unavailable fallback', config => { config.reviewPolicy.onUnavailable = 'continue'; }, /onUnavailable must be block/],
  ['pending model activation', config => { config.models.opus.model = 'claude-opus-5-5'; }, /model must be null/],
  ['verified identity mismatch', config => { config.models.astra.model = 'gpt-6.1-sol'; }, /model must match/],
  ['unknown verification', config => { config.models.opus.verification = 'ready'; }, /verification must be/],
  ['wrong verification source', config => { config.models.opus.verification = 'native-catalog'; }, /verification must be/],
  ['provider relabeling', config => { config.models.astra.provider = 'anthropic'; }, /provider must match/],
  ['client relabeling', config => { config.models.opus.client = 'codex'; }, /provider must match client/],
  ['unknown provider', config => { config.models.opus.provider = 'other'; }, /provider must match/],
  ['unknown client', config => { config.models.opus.client = 'other'; }, /client must be/],
  ['implicit requested identity', config => { config.models.astra.requestedModel = 'auto'; }, /requestedModel must name an explicit model/],
  ['duplicate model identity', config => { config.models.alias = { ...config.models.astra }; }, /duplicates a model identity/],
  ['duplicate panel participant', config => { config.roles.judgment.push('astra'); }, /duplicate participants/],
  ['unknown registry reference', config => { config.roles.exploration = ['missing']; }, /references unknown model/],
  ['empty registry', config => { config.models = {}; }, /references unknown model/],
  ['missing role', config => { delete config.roles['backend-review']; }, /roles must contain exactly/],
  ['empty reviewer list', config => { config.roles['backend-review'] = []; }, /must be a nonempty array/],
  ['missing frontend reviewer', config => { config.roles['frontend-review'] = ['astra']; }, /frontend-review must select exactly/],
  ['backend author reviewing own work', config => { config.roles['backend-review'] = ['astra']; }, /review must exclude its author/],
  ['frontend author reviewing own work', config => { config.roles['frontend-review'] = ['gemini', 'opus']; }, /review must exclude its author/],
  ['multiple implementation owners', config => { config.roles['backend-implementation'].push('opus'); }, /implementation must have a single owner/],
  ['missing judgment provider', config => { config.roles.judgment = ['astra']; }, /judgment must select exactly/],
  ['wrong difficult-task provider', config => { config.roles['difficult-task-review'] = ['astra', 'gemini']; }, /difficult-task-review must select exactly/],
  ['obsolete client block', config => { config.codex = { implementation: { model: 'gpt-6-astra' } }; }, /config must contain exactly/],
  ['ignored model field', config => { config.models.astra.effort = 'max'; }, /models.astra must contain exactly/],
  ['unknown role definition', config => { config.roles.fallback = ['astra']; }, /roles must contain exactly/],
  ['old schema', config => { config.schemaVersion = 1; }, /schemaVersion must be 2/],
]) {
  test(`rejects ${name}`, () => {
    const config = requestedPolicy();
    mutate(config);
    assert.throws(() => validateModelPolicy(config), message);
  });
}

test('unknown requested roles are errors', () => {
  assert.throws(() => resolveRole(requestedPolicy(), 'missing-role'), /Unknown role missing-role/);
  assert.throws(() => resolveRole(requestedPolicy(), 'toString'), /Unknown role toString/);
});

test('CLI validates the config beside its script from an unrelated working directory', () => {
  const result = cli(['validate']);
  assert.equal(result.status, 0, result.stderr);
  assert.deepEqual(JSON.parse(result.stdout), { status: 'valid' });
});

test('CLI emits the literal ready backend route', () => {
  const result = cli(['resolve', 'backend-implementation']);
  assert.equal(result.status, 0, result.stderr);
  assert.deepEqual(JSON.parse(result.stdout), {
    status: 'ready', role: 'backend-implementation',
    participants: [{ key: 'astra', provider: 'openai', client: 'codex', model: 'gpt-6-astra', reasoningEffort: 'xhigh' }],
  });
});

test('CLI exits 2 with the complete blocked frontend result', () => {
  const result = cli(['resolve', 'frontend-review']);
  assert.equal(result.status, 2, result.stderr);
  assert.deepEqual(JSON.parse(result.stdout), {
    status: 'blocked', role: 'frontend-review',
    missing: [{ key: 'gemini', provider: 'google', client: 'gemini', requestedName: 'Gemini 3.8 Flash',
      requestedModel: 'gemini-3.8-flash', reasoningEffort: 'high', verification: 'pending-client' }],
  });
});

test('CLI rejects an invalid config, unknown role, and invalid arguments', () => {
  const config = requestedPolicy();
  config.models.astra.reasoningEffort = 'max';
  for (const result of [cli(['validate'], config), cli(['resolve', 'missing-role']), cli(['validate', 'extra']), cli([])]) {
    assert.equal(result.status, 1);
    assert.equal(result.stdout, '');
    assert.equal(JSON.parse(result.stderr).status, 'error');
  }
});

test('ordinary library import does not invoke the CLI', () => {
  const script = `await import(${JSON.stringify(new URL('./model-policy.mjs', import.meta.url).href)}); console.log('imported');`;
  const result = spawnSync(process.execPath, ['--input-type=module', '-e', script], { encoding: 'utf8', windowsHide: true });
  assert.equal(result.status, 0, result.stderr);
  assert.equal(result.stdout, 'imported\n');
});
