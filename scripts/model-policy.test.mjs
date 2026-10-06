import assert from 'node:assert/strict';
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { spawnSync } from 'node:child_process';
import { test } from 'node:test';
import { validateModelPolicy, resolveRole } from './model-policy.mjs';

function requestedPolicy() {
  return {
    schemaVersion: 3,
    policy: 'quality-first',
    budget: 'large',
    reasoningPolicy: { ceiling: 'xhigh', allowImplicit: false },
    reviewPolicy: { crossProvider: { backend: true, frontend: false }, onUnavailable: 'block' },
    models: {
      sol: {
        provider: 'openai', client: 'codex', requestedName: 'GPT-6.1 Sol',
        requestedModel: 'gpt-6.1-sol', model: 'gpt-6.1-sol',
        verifiedEfforts: ['low', 'medium', 'high', 'xhigh'], verification: 'native-catalog',
      },
      astra: {
        provider: 'openai', client: 'codex', requestedName: 'GPT-6 Astra',
        requestedModel: 'gpt-6-astra', model: 'gpt-6-astra',
        verifiedEfforts: ['low', 'medium', 'high', 'xhigh'], verification: 'native-catalog',
      },
      opus: {
        provider: 'anthropic', client: 'claude', requestedName: 'Claude Opus 5.5',
        requestedModel: 'claude-opus-5-5', model: 'claude-opus-5-5',
        verifiedEfforts: ['low', 'high', 'xhigh'], verification: 'client-catalog',
      },
      gemini: {
        provider: 'google', client: 'agy', requestedName: 'Gemini 3.8 Flash',
        requestedModel: 'gemini-3.8-flash', model: 'gemini-3.8-flash',
        verifiedEfforts: ['low', 'high'], verification: 'client-catalog',
      },
    },
    roles: {
      'backend-implementation': [{ model: 'sol', reasoningEffort: 'high' }],
      'frontend-implementation': [{ model: 'opus', reasoningEffort: 'high' }],
      'backend-review': [{ model: 'opus', reasoningEffort: 'xhigh' }],
      'frontend-review': [{ model: 'opus', reasoningEffort: 'xhigh' }],
      judgment: [{ model: 'astra', reasoningEffort: 'xhigh' }],
      'difficult-task-review': [{ model: 'astra', reasoningEffort: 'xhigh' }],
      exploration: [{ model: 'sol', reasoningEffort: 'low' }],
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

const expectedRoutes = {
  'backend-implementation': [{ key: 'sol', provider: 'openai', client: 'codex', model: 'gpt-6.1-sol', reasoningEffort: 'high' }],
  'frontend-implementation': [{ key: 'opus', provider: 'anthropic', client: 'claude', model: 'claude-opus-5-5', reasoningEffort: 'high' }],
  'backend-review': [{ key: 'opus', provider: 'anthropic', client: 'claude', model: 'claude-opus-5-5', reasoningEffort: 'xhigh' }],
  'frontend-review': [{ key: 'opus', provider: 'anthropic', client: 'claude', model: 'claude-opus-5-5', reasoningEffort: 'xhigh' }],
  judgment: [{ key: 'astra', provider: 'openai', client: 'codex', model: 'gpt-6-astra', reasoningEffort: 'xhigh' }],
  'difficult-task-review': [{ key: 'astra', provider: 'openai', client: 'codex', model: 'gpt-6-astra', reasoningEffort: 'xhigh' }],
  exploration: [{ key: 'sol', provider: 'openai', client: 'codex', model: 'gpt-6.1-sol', reasoningEffort: 'low' }],
};

for (const [name, load] of [
  ['fixture', requestedPolicy],
  ['checked-in policy', () => JSON.parse(fs.readFileSync(new URL('../.pstack/models.json', import.meta.url), 'utf8'))],
]) {
  test(`${name} resolves every requested model and per-role effort without mutation`, () => {
    const config = load();
    const before = structuredClone(config);
    for (const [role, participants] of Object.entries(expectedRoutes)) {
      assert.deepEqual(resolveRole(config, role), { status: 'ready', role, participants });
    }
    assert.deepEqual(config, before);
  });
}

test('pending Opus blocks its roles without blocking Codex work or substituting a model', () => {
  const config = requestedPolicy();
  config.models.opus.model = null;
  config.models.opus.verification = 'pending-client';
  for (const [role, reasoningEffort] of [
    ['frontend-implementation', 'high'], ['frontend-review', 'xhigh'], ['backend-review', 'xhigh'],
  ]) {
    assert.deepEqual(resolveRole(config, role), {
      status: 'blocked', role,
      missing: [{ key: 'opus', provider: 'anthropic', client: 'claude', requestedName: 'Claude Opus 5.5',
        requestedModel: 'claude-opus-5-5', reasoningEffort, verification: 'pending-client' }],
    });
  }
  assert.deepEqual(resolveRole(config, 'exploration'), {
    status: 'ready', role: 'exploration',
    participants: [{ key: 'sol', provider: 'openai', client: 'codex', model: 'gpt-6.1-sol', reasoningEffort: 'low' }],
  });
});

test('one pending Sol identity blocks both of its role-specific efforts', () => {
  const config = requestedPolicy();
  config.models.sol.model = null;
  config.models.sol.verification = 'pending-client';
  for (const [role, reasoningEffort] of [['backend-implementation', 'high'], ['exploration', 'low']]) {
    assert.deepEqual(resolveRole(config, role), {
      status: 'blocked', role,
      missing: [{ key: 'sol', provider: 'openai', client: 'codex', requestedName: 'GPT-6.1 Sol',
        requestedModel: 'gpt-6.1-sol', reasoningEffort, verification: 'pending-client' }],
    });
  }
});

test('an unavailable unassigned model does not block a selected role', () => {
  const config = requestedPolicy();
  config.models.gemini.model = null;
  config.models.gemini.verification = 'pending-client';
  assert.deepEqual(resolveRole(config, 'judgment'), {
    status: 'ready', role: 'judgment',
    participants: [{ key: 'astra', provider: 'openai', client: 'codex', model: 'gpt-6-astra', reasoningEffort: 'xhigh' }],
  });
});

function reviewPanel() {
  const config = requestedPolicy();
  config.models.alpha = {
    provider: 'anthropic', client: 'claude', requestedName: 'Alpha reviewer',
    requestedModel: 'alpha-model', model: null, verifiedEfforts: [], verification: 'pending-client',
  };
  config.models.beta = {
    provider: 'anthropic', client: 'claude', requestedName: 'Beta reviewer',
    requestedModel: 'beta-model', model: null, verifiedEfforts: [], verification: 'pending-client',
  };
  config.roles['backend-review'] = [
    { model: 'opus', reasoningEffort: 'xhigh' },
    { model: 'beta', reasoningEffort: 'high' },
    { model: 'alpha', reasoningEffort: 'low' },
  ];
  return config;
}

test('a partly available review panel returns every missing member in order and no runnable member', () => {
  const config = reviewPanel();
  assert.deepEqual(resolveRole(config, 'backend-review'), {
    status: 'blocked', role: 'backend-review',
    missing: [
      { key: 'beta', provider: 'anthropic', client: 'claude', requestedName: 'Beta reviewer',
        requestedModel: 'beta-model', reasoningEffort: 'high', verification: 'pending-client' },
      { key: 'alpha', provider: 'anthropic', client: 'claude', requestedName: 'Alpha reviewer',
        requestedModel: 'alpha-model', reasoningEffort: 'low', verification: 'pending-client' },
    ],
  });
});

test('a fully verified panel resolves all participants in assignment order with their efforts', () => {
  const config = reviewPanel();
  for (const key of ['alpha', 'beta']) {
    config.models[key].model = config.models[key].requestedModel;
    config.models[key].verification = 'client-catalog';
    config.models[key].verifiedEfforts = ['low', 'high'];
  }
  assert.deepEqual(resolveRole(config, 'backend-review'), {
    status: 'ready', role: 'backend-review',
    participants: [
      { key: 'opus', provider: 'anthropic', client: 'claude', model: 'claude-opus-5-5', reasoningEffort: 'xhigh' },
      { key: 'beta', provider: 'anthropic', client: 'claude', model: 'beta-model', reasoningEffort: 'high' },
      { key: 'alpha', provider: 'anthropic', client: 'claude', model: 'alpha-model', reasoningEffort: 'low' },
    ],
  });
});

test('registry keys may be renamed without changing a model or either assigned effort', () => {
  const config = requestedPolicy();
  config.models.worker = config.models.sol;
  delete config.models.sol;
  config.roles['backend-implementation'][0].model = 'worker';
  config.roles.exploration[0].model = 'worker';
  assert.deepEqual(resolveRole(config, 'exploration'), {
    status: 'ready', role: 'exploration',
    participants: [{ key: 'worker', provider: 'openai', client: 'codex', model: 'gpt-6.1-sol', reasoningEffort: 'low' }],
  });
});

test('an explicit same-provider model change remains a configuration edit', () => {
  const config = requestedPolicy();
  config.models.sol.requestedName = 'Replacement OpenAI model';
  config.models.sol.requestedModel = 'replacement-openai-model';
  config.models.sol.model = 'replacement-openai-model';
  assert.deepEqual(resolveRole(config, 'backend-implementation'), {
    status: 'ready', role: 'backend-implementation',
    participants: [{ key: 'sol', provider: 'openai', client: 'codex', model: 'replacement-openai-model', reasoningEffort: 'high' }],
  });
});

for (const value of ['max', 'ultra', 'auto', 'inherit-parent', 'unknown', null, undefined]) {
  test(`rejects role reasoning effort ${String(value)}`, () => {
    const config = requestedPolicy();
    config.roles.exploration[0].reasoningEffort = value;
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
  delete config.roles.exploration[0].reasoningEffort;
  assert.throws(() => validateModelPolicy(config), /must contain exactly/);
  const lowered = requestedPolicy();
  lowered.reasoningPolicy.ceiling = 'high';
  assert.throws(() => validateModelPolicy(lowered), /exceeds the policy ceiling/);
});

test('accepts explicit lower efforts within the lowered ceiling without raising exploration', () => {
  const config = requestedPolicy();
  config.reasoningPolicy.ceiling = 'medium';
  config.models.opus.verifiedEfforts.push('medium');
  for (const assignments of Object.values(config.roles)) {
    for (const assignment of assignments) assignment.reasoningEffort = 'medium';
  }
  config.roles.exploration[0].reasoningEffort = 'low';
  assert.deepEqual(resolveRole(config, 'exploration'), {
    status: 'ready', role: 'exploration',
    participants: [{ key: 'sol', provider: 'openai', client: 'codex', model: 'gpt-6.1-sol', reasoningEffort: 'low' }],
  });
});

test('rejects Gemini xhigh before provider ownership validation', () => {
  const config = requestedPolicy();
  config.roles.exploration = [{ model: 'gemini', reasoningEffort: 'xhigh' }];
  assert.throws(() => validateModelPolicy(config), /Gemini's high limit/);
});

for (const [name, mutate, message] of [
  ['implicit selection', config => { config.reasoningPolicy.allowImplicit = true; }, /allowImplicit must be false/],
  ['global cross-provider flag', config => { config.reviewPolicy.crossProvider = true; }, /crossProvider must be an object/],
  ['missing scope review policy', config => { delete config.reviewPolicy.crossProvider.frontend; }, /crossProvider must contain exactly/],
  ['non-boolean scope review policy', config => { config.reviewPolicy.crossProvider.frontend = 'false'; }, /must be a boolean/],
  ['same-provider backend review', config => { config.roles['backend-review'] = [{ model: 'astra', reasoningEffort: 'xhigh' }]; }, /backend review must use providers different from its author/],
  ['cross-provider requirement on shared frontend model', config => { config.reviewPolicy.crossProvider.frontend = true; }, /frontend review must use providers different from its author/],
  ['unavailable fallback', config => { config.reviewPolicy.onUnavailable = 'continue'; }, /onUnavailable must be block/],
  ['pending model activation', config => { config.models.opus.verification = 'pending-client'; }, /model must be null/],
  ['verified identity mismatch', config => { config.models.astra.model = 'gpt-6.1-sol'; }, /model must match/],
  ['unknown verification', config => { config.models.opus.verification = 'ready'; }, /verification must be/],
  ['missing verified efforts', config => { delete config.models.sol.verifiedEfforts; }, /models.sol must contain exactly/],
  ['non-array verified efforts', config => { config.models.sol.verifiedEfforts = 'high'; }, /verifiedEfforts must be an array/],
  ['empty verified efforts', config => { config.models.sol.verifiedEfforts = []; }, /verifiedEfforts must not be empty/],
  ['duplicate verified efforts', config => { config.models.sol.verifiedEfforts.push('high'); }, /verifiedEfforts must not contain duplicates/],
  ['unsupported verified effort', config => { config.models.sol.verifiedEfforts.push('max'); }, /verifiedEfforts must contain only/],
  ['unverified assigned effort', config => { config.models.sol.verifiedEfforts = ['high']; }, /reasoningEffort has not been verified/],
  ['Gemini xhigh capability', config => { config.models.gemini.verifiedEfforts.push('xhigh'); }, /Gemini's high limit/],
  ['wrong verification source', config => { config.models.opus.verification = 'native-catalog'; }, /verification must be/],
  ['client relabeling', config => { config.models.opus.client = 'codex'; }, /provider must match client/],
  ['implicit requested identity', config => { config.models.astra.requestedModel = 'auto'; }, /requestedModel must name an explicit model/],
  ['duplicate model identity', config => { config.models.alias = { ...config.models.astra }; }, /duplicates a model identity/],
  ['duplicate panel model with another effort', config => { config.roles['frontend-review'].push({ model: 'opus', reasoningEffort: 'low' }); }, /duplicate participants/],
  ['unknown registry reference', config => { config.roles.exploration[0].model = 'missing'; }, /references unknown model/],
  ['inherited registry reference', config => { config.roles.exploration[0].model = 'toString'; }, /references unknown model/],
  ['missing role', config => { delete config.roles['backend-review']; }, /roles must contain exactly/],
  ['empty reviewer list', config => { config.roles['backend-review'] = []; }, /must be a nonempty array/],
  ['non-Claude frontend reviewer', config => { config.roles['frontend-review'] = [{ model: 'astra', reasoningEffort: 'xhigh' }]; }, /frontend-review must select exactly/],
  ['non-OpenAI backend implementer', config => { config.roles['backend-implementation'] = [{ model: 'gemini', reasoningEffort: 'high' }]; }, /backend-implementation must select exactly/],
  ['multiple backend implementation owners', config => { config.roles['backend-implementation'].push({ model: 'astra', reasoningEffort: 'xhigh' }); }, /backend implementation must have a single owner/],
  ['multiple frontend implementation owners', config => { config.roles['frontend-implementation'].push({ model: 'astra', reasoningEffort: 'xhigh' }); }, /frontend implementation must have a single owner/],
  ['non-OpenAI judgment', config => { config.roles.judgment = [{ model: 'opus', reasoningEffort: 'xhigh' }]; }, /judgment must select exactly/],
  ['wrong difficult-task provider', config => { config.roles['difficult-task-review'] = [{ model: 'gemini', reasoningEffort: 'high' }]; }, /difficult-task-review must select exactly/],
  ['retired Gemini CLI client', config => { config.models.gemini.client = 'gemini'; }, /client must be codex, claude, or agy/],
  ['obsolete client block', config => { config.codex = { implementation: { model: 'gpt-6-astra' } }; }, /config must contain exactly/],
  ['obsolete registry effort', config => { config.models.astra.reasoningEffort = 'xhigh'; }, /models.astra must contain exactly/],
  ['ignored assignment field', config => { config.roles.exploration[0].effort = 'max'; }, /must contain exactly/],
  ['old string assignment', config => { config.roles.exploration = ['sol']; }, /must be an object/],
  ['unknown role definition', config => { config.roles.fallback = [{ model: 'astra', reasoningEffort: 'xhigh' }]; }, /roles must contain exactly/],
  ['old schema', config => { config.schemaVersion = 2; }, /schemaVersion must be 3/],
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

test('CLI emits every requested model and effort', () => {
  for (const [role, participants] of Object.entries(expectedRoutes)) {
    const result = cli(['resolve', role]);
    assert.equal(result.status, 0, result.stderr);
    assert.deepEqual(JSON.parse(result.stdout), { status: 'ready', role, participants });
  }
});

test('CLI exits 2 with the complete blocked frontend result', () => {
  const config = requestedPolicy();
  config.models.opus.model = null;
  config.models.opus.verification = 'pending-client';
  const result = cli(['resolve', 'frontend-review'], config);
  assert.equal(result.status, 2, result.stderr);
  assert.deepEqual(JSON.parse(result.stdout), {
    status: 'blocked', role: 'frontend-review',
    missing: [{ key: 'opus', provider: 'anthropic', client: 'claude', requestedName: 'Claude Opus 5.5',
      requestedModel: 'claude-opus-5-5', reasoningEffort: 'xhigh', verification: 'pending-client' }],
  });
});

test('CLI rejects an invalid config, unknown role, and invalid arguments', () => {
  const config = requestedPolicy();
  config.roles.exploration[0].reasoningEffort = 'max';
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
