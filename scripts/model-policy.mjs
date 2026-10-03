import assert from 'node:assert/strict';
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const efforts = ['low', 'medium', 'high', 'xhigh'];
const clientProviders = { codex: 'openai', claude: 'anthropic', gemini: 'google' };
const requiredRoles = {
  'backend-implementation': ['openai'],
  'frontend-implementation': ['anthropic'],
  'backend-review': ['anthropic'],
  'frontend-review': ['google', 'openai'],
  judgment: ['openai', 'anthropic'],
  'difficult-task-review': ['openai', 'anthropic'],
  exploration: ['openai'],
};

function object(value, label) {
  assert.ok(value !== null && typeof value === 'object' && !Array.isArray(value),
    `${label} must be an object`);
}

function fields(value, expected, label) {
  object(value, label);
  assert.deepEqual(Object.keys(value).sort(), [...expected].sort(),
    `${label} must contain exactly ${expected.join(', ')}`);
}

function nonemptyString(value, label) {
  assert.ok(typeof value === 'string' && value.trim().length > 0 && value === value.trim(),
    `${label} must be a nonempty string without surrounding whitespace`);
}

export function validateModelPolicy(config) {
  fields(config, ['schemaVersion', 'policy', 'budget', 'reasoningPolicy', 'reviewPolicy', 'models', 'roles'], 'config');
  assert.equal(config.schemaVersion, 2, 'schemaVersion must be 2');
  assert.equal(config.policy, 'quality-first', 'policy must be quality-first');
  assert.equal(config.budget, 'large', 'budget must be large');
  fields(config.reasoningPolicy, ['ceiling', 'allowImplicit'], 'reasoningPolicy');
  const ceiling = efforts.indexOf(config.reasoningPolicy.ceiling);
  assert.ok(ceiling >= 0, 'reasoningPolicy.ceiling must be low, medium, high, or xhigh');
  assert.equal(config.reasoningPolicy.allowImplicit, false, 'reasoningPolicy.allowImplicit must be false');
  fields(config.reviewPolicy, ['crossProvider', 'onUnavailable'], 'reviewPolicy');
  assert.equal(config.reviewPolicy.crossProvider, true, 'reviewPolicy.crossProvider must be true');
  assert.equal(config.reviewPolicy.onUnavailable, 'block', 'reviewPolicy.onUnavailable must be block');
  object(config.models, 'models');
  const seenModels = new Set();
  for (const [key, entry] of Object.entries(config.models)) {
    assert.match(key, /^[a-z][a-z0-9-]*$/, 'model keys must be lowercase names');
    const label = `models.${key}`;
    fields(entry, ['provider', 'client', 'requestedName', 'requestedModel', 'model', 'reasoningEffort', 'verification'], label);
    nonemptyString(entry.requestedName, `${label}.requestedName`);
    nonemptyString(entry.requestedModel, `${label}.requestedModel`);
    assert.ok(!['auto', 'default', 'inherit', 'inherit-parent'].includes(entry.requestedModel),
      `${label}.requestedModel must name an explicit model`);
    assert.ok(typeof entry.client === 'string' && Object.hasOwn(clientProviders, entry.client),
      `${label}.client must be codex, claude, or gemini`);
    assert.equal(entry.provider, clientProviders[entry.client], `${label}.provider must match client`);
    const identity = JSON.stringify([entry.provider, entry.requestedModel]);
    assert.ok(!seenModels.has(identity), `${label} duplicates a model identity`);
    seenModels.add(identity);
    const effort = efforts.indexOf(entry.reasoningEffort);
    assert.ok(effort >= 0, `${label}.reasoningEffort must be explicitly low, medium, high, or xhigh`);
    assert.ok(effort <= ceiling, `${label}.reasoningEffort exceeds the policy ceiling`);
    assert.ok(entry.provider !== 'google' || effort <= efforts.indexOf('high'),
      `${label}.reasoningEffort exceeds Gemini's high limit`);
    if (entry.verification === 'pending-client') {
      assert.equal(entry.model, null, `${label}.model must be null while verification is pending-client`);
    } else {
      const verification = entry.client === 'codex' ? 'native-catalog' : 'client-catalog';
      assert.equal(entry.verification, verification, `${label}.verification must be pending-client or ${verification}`);
      assert.equal(entry.model, entry.requestedModel, `${label}.model must match the verified requestedModel`);
    }
  }
  fields(config.roles, Object.keys(requiredRoles), 'roles');
  for (const [role, keys] of Object.entries(config.roles)) {
    assert.ok(Array.isArray(keys) && keys.length > 0, `roles.${role} must be a nonempty array`);
    assert.equal(new Set(keys).size, keys.length, `roles.${role} must not contain duplicate participants`);
    for (const key of keys) {
      assert.ok(typeof key === 'string' && Object.hasOwn(config.models, key), `roles.${role} references unknown model ${key}`);
    }
  }
  for (const scope of ['backend', 'frontend']) {
    assert.equal(config.roles[`${scope}-implementation`].length, 1, `${scope} implementation must have a single owner`);
    const [author] = config.roles[`${scope}-implementation`];
    for (const reviewer of config.roles[`${scope}-review`]) {
      assert.notEqual(reviewer, author, `${scope} review must exclude its author`);
      assert.notEqual(config.models[reviewer].provider, config.models[author].provider,
        `${scope} review must use providers different from its author`);
    }
  }
  for (const [role, providers] of Object.entries(requiredRoles)) {
    const entries = config.roles[role].map(key => config.models[key]);
    assert.deepEqual(entries.map(entry => entry.provider).sort(), [...providers].sort(),
      `roles.${role} must select exactly these providers: ${providers.join(', ')}`);
  }
  return true;
}

export function resolveRole(config, role) {
  validateModelPolicy(config);
  assert.ok(typeof role === 'string' && Object.hasOwn(config.roles, role), `Unknown role ${role}`);
  const selected = config.roles[role].map(key => ({ key, ...config.models[key] }));
  const missing = selected.filter(entry => entry.verification === 'pending-client');
  if (missing.length > 0) {
    return {
      status: 'blocked',
      role,
      missing: missing.map(({ key, provider, client, requestedName, requestedModel, reasoningEffort, verification }) =>
        ({ key, provider, client, requestedName, requestedModel, reasoningEffort, verification })),
    };
  }
  return {
    status: 'ready',
    role,
    participants: selected.map(({ key, provider, client, model, reasoningEffort }) =>
      ({ key, provider, client, model, reasoningEffort })),
  };
}

if (process.argv[1] && path.resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  try {
    const [command, role, ...extra] = process.argv.slice(2);
    assert.ok(extra.length === 0 && ((command === 'validate' && role === undefined) ||
      (command === 'resolve' && role !== undefined)),
    'Use node scripts/model-policy.mjs validate or node scripts/model-policy.mjs resolve <role>');
    const configPath = new URL('../.pstack/models.json', import.meta.url);
    const config = JSON.parse(fs.readFileSync(configPath, 'utf8'));
    let result;
    if (command === 'validate') {
      validateModelPolicy(config);
      result = { status: 'valid' };
    } else {
      result = resolveRole(config, role);
    }
    console.log(JSON.stringify(result, null, 2));
    process.exitCode = result.status === 'blocked' ? 2 : 0;
  } catch (error) {
    console.error(JSON.stringify({ status: 'error', message: error.message }));
    process.exitCode = 1;
  }
}
