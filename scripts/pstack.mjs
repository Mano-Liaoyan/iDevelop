import assert from 'node:assert/strict';
import fs from 'node:fs';
import path from 'node:path';
import os from 'node:os';
import { createHash } from 'node:crypto';
import { fileURLToPath } from 'node:url';
import { listSkills } from './codex-skills.mjs';

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const source = path.join(root, 'vendor/pstack');
const active = path.join(root, '.agents/skills');
const lock = JSON.parse(fs.readFileSync(path.join(root, '.pstack/upstream.json'), 'utf8'));
const names = fs.readdirSync(path.join(source, 'skills')).filter(name =>
  fs.existsSync(path.join(source, 'skills', name, 'SKILL.md'))).sort();
const local = path.join(root, '.pstack/local');

function files(directory) {
  if (!fs.existsSync(directory)) return [];
  return fs.readdirSync(directory, { withFileTypes: true }).flatMap(entry => {
    const target = path.join(directory, entry.name);
    return entry.isDirectory() ? files(target) : entry.isFile() ? [target] : [];
  });
}

function write(target, content) {
  fs.mkdirSync(path.dirname(target), { recursive: true });
  fs.writeFileSync(target, content);
}

function adapt(content, relative) {
  let result = content.replaceAll('~/.cursor/rules/pstack-models.mdc', '.cursor/rules/pstack-models.mdc');
  if (/^[^/]+\/SKILL\.md$/.test(relative)) {
    result = result.replace(/^name:.*$/m, `name: ${relative.split('/')[0]}`);
    const end = result.indexOf('\n---', 3) + 4;
    result = result.slice(0, end) + '\n\nProject adapter: read `.pstack/compatibility.md` and `.pstack/models.json` from the repository root before executing this skill.\n' + result.slice(end);
  }
  return result;
}

function checkSource() {
  const actual = files(source).map(file => path.relative(source, file).replaceAll('\\', '/')).sort();
  assert.deepEqual(actual, Object.keys(lock.files).sort(), 'Upstream file inventory changed');
  for (const relative of actual) {
    const digest = createHash('sha256').update(fs.readFileSync(path.join(source, relative))).digest('hex');
    assert.equal(digest, lock.files[relative], `Upstream changed: ${relative}`);
  }
}

function link(directory) {
  const target = path.join(root, directory, 'skills');
  fs.mkdirSync(path.dirname(target), { recursive: true });
  if (fs.existsSync(target)) {
    assert.equal(fs.realpathSync(target), fs.realpathSync(active), `Refusing to replace ${target}`);
  } else {
    fs.symlinkSync(process.platform === 'win32' ? active : '../.agents/skills', target,
      process.platform === 'win32' ? 'junction' : 'dir');
  }
}

async function isolateCodex() {
  const entries = await listSkills([root, path.dirname(root)]);
  for (const entry of entries) assert.deepEqual(entry.errors, [], 'Codex discovery errors');
  const excluded = new Set(entries.flatMap(entry => entry.skills)
    .filter(skill => !path.resolve(skill.path).startsWith(root + path.sep)).map(skill => skill.path));
  for (const directory of ['.agents/skills', '.codex/skills']) {
    for (const file of files(path.join(os.homedir(), directory))) {
      if (path.basename(file) === 'SKILL.md') excluded.add(file);
    }
  }
  const overrides = [...excluded].sort().map(file =>
    `{path=${JSON.stringify(file.replaceAll('\\', '/'))},enabled=false}`).join(',');
  write(path.join(local, 'codex-override.txt'), `skills.config=[${overrides}]`);
  write(path.join(local, 'codex-before.json'), JSON.stringify(entries, null, 2) + '\n');
  await auditCodex();
}

async function auditCodex() {
  const override = fs.readFileSync(path.join(local, 'codex-override.txt'), 'utf8');
  const entries = await listSkills([root], [override]);
  const project = entries.find(entry => path.resolve(entry.cwd) === root);
  assert.ok(project, 'Codex omitted this project');
  assert.deepEqual(project.errors, [], 'Codex discovery errors');
  const enabled = project.skills.filter(skill => skill.enabled);
  assert.deepEqual(enabled.map(skill => skill.name).sort(), names,
    'Enabled Codex skills must be exactly PStack; re-run isolate-codex');
  for (const skill of enabled) {
    assert.ok(fs.realpathSync(skill.path).startsWith(fs.realpathSync(active) + path.sep),
      `Non-project skill enabled: ${skill.name}`);
  }
  const [sibling] = await listSkills([path.dirname(root)]);
  assert.ok(sibling.skills.some(skill => skill.enabled && !names.includes(skill.name)),
    'Control directory should retain its other skills');
  write(path.join(local, 'codex-audit.json'), JSON.stringify([...entries, sibling], null, 2) + '\n');
  console.log(`PASS: Codex launch override enables exactly ${names.length} project PStack skills; other skills remain enabled outside this project.`);
}

function check() {
  checkSource();
  for (const input of files(path.join(source, 'skills'))) {
    const relative = path.relative(path.join(source, 'skills'), input).replaceAll('\\', '/');
    const expected = input.endsWith('.md') ? Buffer.from(adapt(fs.readFileSync(input, 'utf8'), relative)) : fs.readFileSync(input);
    assert.deepEqual(fs.readFileSync(path.join(active, relative)), expected, `Generated file drift: ${relative}`);
  }
  assert.deepEqual(fs.readdirSync(active).sort(), names, 'Unexpected active skills');
  for (const name of names) {
    const content = fs.readFileSync(path.join(active, name, 'SKILL.md'), 'utf8');
    assert.match(content, new RegExp(`^---\\r?\\nname: ${name}\\r?\\n`, 'u'));
    assert.match(content, /\ndescription: .+/);
  }
  for (const directory of ['.claude', '.cursor']) {
    assert.equal(fs.realpathSync(path.join(root, directory, 'skills')), fs.realpathSync(active));
  }
  const models = JSON.parse(fs.readFileSync(path.join(root, '.pstack/models.json'), 'utf8'));
  assert.equal(models.policy, 'quality-first');
  assert.ok(models.codex.implementation.model && models.codex.judgment.model);
  assert.ok(models.codex.reviewers.length >= 2);
  console.log(`PASS: ${names.length} skills, upstream hashes, generated adapters, shared links, and model configuration.`);
}

const command = process.argv[2] ?? 'check';
if (command === 'setup') {
  checkSource();
  for (const input of files(path.join(source, 'skills'))) {
    const relative = path.relative(path.join(source, 'skills'), input).replaceAll('\\', '/');
    write(path.join(active, relative), input.endsWith('.md')
      ? adapt(fs.readFileSync(input, 'utf8'), relative) : fs.readFileSync(input));
  }
  link('.claude');
  link('.cursor');
  check();
  console.log('Next: node scripts/pstack.mjs isolate-codex (when Codex is installed).');
} else if (command === 'check') {
  check();
} else if (command === 'isolate-codex') {
  await isolateCodex();
} else if (command === 'audit-codex') {
  await auditCodex();
} else {
  throw new Error('Use setup, check, isolate-codex, or audit-codex');
}
