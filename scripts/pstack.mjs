import assert from 'node:assert/strict';
import fs from 'node:fs';
import path from 'node:path';
import os from 'node:os';
import { execFileSync } from 'node:child_process';
import { fileURLToPath } from 'node:url';
import { listSkills } from './codex-skills.mjs';
import { validateModelPolicy } from './model-policy.mjs';

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const upstream = path.join(root, '.pstack/upstream');
const source = path.join(upstream, 'pstack');
const active = path.join(root, '.agents/skills');
const command = process.argv[2] ?? 'check';
const adapterNotice = 'Project adapter: read `.pstack/compatibility.md` and `.pstack/models.json` from the repository root before executing this skill.';

function git(args, cwd = root) {
  return execFileSync('git', args, { cwd, encoding: 'utf8', windowsHide: true }).trim();
}

if (command === 'setup') {
  if (!fs.existsSync(path.join(upstream, '.git'))) {
    git(['submodule', 'update', '--init', '--checkout', '--', '.pstack/upstream']);
  }
  git(['sparse-checkout', 'set', 'pstack'], upstream);
}
assert.ok(fs.existsSync(path.join(source, 'skills')), 'PStack submodule is missing. Run node scripts/pstack.mjs setup.');
const local = path.join(root, '.pstack/local');

function files(directory) {
  if (!fs.existsSync(directory)) return [];
  return fs.readdirSync(directory, { withFileTypes: true }).flatMap(entry => {
    const target = path.join(directory, entry.name);
    return entry.isDirectory() ? files(target) : entry.isFile() ? [target] : [];
  });
}

function skillFile(base, input) {
  return { input, relative: path.relative(base, input).replaceAll('\\', '/') };
}

const upstreamSkills = path.join(source, 'skills');
const upstreamFiles = git(['ls-files', '-z', '--', 'skills'], source).split('\0').filter(Boolean)
  .map(file => skillFile(upstreamSkills, path.join(source, file)));
const upstreamNames = upstreamFiles.map(file => file.relative)
  .filter(relative => /^[^/]+\/SKILL\.md$/.test(relative)).map(relative => relative.split('/')[0]).sort();
const projectSkills = path.join(root, 'skills');
const projectNames = fs.existsSync(projectSkills)
  ? fs.readdirSync(projectSkills, { withFileTypes: true }).filter(entry => entry.isDirectory()).map(entry => entry.name).sort()
  : [];
// Windows and macOS paths ignore case, so a name that differs only in case would overwrite the PStack skill.
const upstreamKeys = new Set(upstreamNames.map(name => name.toLowerCase()));
for (const name of projectNames) {
  assert.ok(!upstreamKeys.has(name.toLowerCase()), `Project skill skills/${name} has the same name as a PStack skill. Rename the project skill.`);
  const entrypoint = path.join(projectSkills, name, 'SKILL.md');
  assert.ok(fs.existsSync(entrypoint), `Project skill skills/${name} has no SKILL.md.`);
  const frontmatter = /^---\r?\n([\s\S]*?)\r?\n---(?:\r?\n|$)/.exec(fs.readFileSync(entrypoint, 'utf8'))?.[1] ?? '';
  assert.ok(/^name:/m.test(frontmatter) && /^description:/m.test(frontmatter),
    `Project skill skills/${name}/SKILL.md must begin with YAML frontmatter that holds name: and description:.`);
}
const skillFiles = [...upstreamFiles, ...projectNames.flatMap(name => files(path.join(projectSkills, name)))
  .map(input => skillFile(projectSkills, input))];
const names = [...upstreamNames, ...projectNames].sort();

function write(target, content) {
  fs.mkdirSync(path.dirname(target), { recursive: true });
  fs.writeFileSync(target, content);
}

function adapt(content, relative) {
  let result = content.replaceAll('~/.cursor/rules/pstack-models.mdc', '.cursor/rules/pstack-models.mdc');
  if (/^[^/]+\/SKILL\.md$/.test(relative)) {
    result = result.replace(/^name:.*$/m, `name: ${relative.split('/')[0]}`);
    const end = result.indexOf('\n---', 3) + 4;
    result = result.slice(0, end) + '\n\n' + adapterNotice + '\n' + result.slice(end);
  }
  return result;
}

function checkSource() {
  const recorded = git(['ls-files', '--stage', '--', '.pstack/upstream']).match(/^160000 ([0-9a-f]+) 0\t/);
  assert.ok(recorded, 'PStack must be a registered submodule.');
  assert.equal(git(['rev-parse', 'HEAD'], upstream), recorded[1],
    'PStack differs from the recorded submodule commit. Run git submodule update --init --checkout, or stage an intentional upgrade before setup.');
  assert.equal(git(['status', '--porcelain', '--untracked-files=normal'], upstream), '',
    'PStack submodule has local changes. Preserve or commit them before regenerating skills.');
}

function render(input, relative) {
  const bytes = fs.readFileSync(input);
  const text = bytes.toString('utf8');
  if (bytes.includes(0) || !Buffer.from(text).equals(bytes)) return bytes;
  const normalized = text.replaceAll('\r\n', '\n');
  return Buffer.from(input.endsWith('.md') ? adapt(normalized, relative) : normalized);
}

function pruneEmptyDirectories(directory) {
  for (const entry of fs.readdirSync(directory, { withFileTypes: true })) {
    if (!entry.isDirectory()) continue;
    const child = path.join(directory, entry.name);
    pruneEmptyDirectories(child);
    if (fs.readdirSync(child).length === 0) fs.rmdirSync(child);
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
    'Enabled Codex skills must be exactly PStack and the project skills; re-run isolate-codex');
  for (const skill of enabled) {
    assert.ok(fs.realpathSync(skill.path).startsWith(fs.realpathSync(active) + path.sep),
      `Non-project skill enabled: ${skill.name}`);
  }
  const [sibling] = await listSkills([path.dirname(root)]);
  assert.ok(sibling.skills.some(skill => skill.enabled && !names.includes(skill.name)),
    'Control directory should retain its other skills');
  write(path.join(local, 'codex-audit.json'), JSON.stringify([...entries, sibling], null, 2) + '\n');
  console.log(`PASS: Codex launch override enables exactly ${names.length} skills (${upstreamNames.length} PStack, ${projectNames.length} project-owned); other skills remain enabled outside this project.`);
}

function check() {
  checkSource();
  const expectedFiles = skillFiles.map(file => file.relative).sort();
  assert.deepEqual(files(active).map(file => path.relative(active, file).replaceAll('\\', '/')).sort(), expectedFiles,
    'Generated skill file inventory drifted. Run setup to synchronize it.');
  for (const { input, relative } of skillFiles) {
    const expected = render(input, relative);
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
  validateModelPolicy(models);
  console.log(`PASS: ${names.length} skills (${upstreamNames.length} PStack, ${projectNames.length} project-owned), clean upstream ${git(['rev-parse', '--short', 'HEAD'], upstream)}, generated adapters, shared links, and model configuration.`);
}

if (command === 'setup') {
  checkSource();
  if (fs.existsSync(active)) {
    assert.equal(fs.realpathSync(active), active, 'Generated skills must be a real directory inside this checkout.');
    pruneEmptyDirectories(active);
    for (const entry of fs.readdirSync(active, { withFileTypes: true })) {
      assert.ok(entry.isDirectory(), `Unexpected generated skill entry: ${entry.name}`);
      const entrypoint = path.join(active, entry.name, 'SKILL.md');
      assert.ok(fs.existsSync(entrypoint) ? fs.readFileSync(entrypoint, 'utf8').includes(adapterNotice) : names.includes(entry.name),
        `Refusing to overwrite a non-generated skill: ${entry.name}`);
    }
  }
  const expectedFiles = new Set();
  for (const { input, relative } of skillFiles) {
    expectedFiles.add(path.join(active, relative));
    write(path.join(active, relative), render(input, relative));
  }
  const obsoleteFiles = files(active).filter(file => !expectedFiles.has(file));
  // Keep ownership notices until the other stale files are gone, so interrupted cleanup can resume.
  obsoleteFiles.sort((a, b) => Number(path.basename(a) === 'SKILL.md') - Number(path.basename(b) === 'SKILL.md'));
  for (const file of obsoleteFiles) fs.unlinkSync(file);
  pruneEmptyDirectories(active);
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
