import assert from 'node:assert/strict';
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { spawnSync } from 'node:child_process';
import { test } from 'node:test';

const script = path.join(import.meta.dirname, 'check-handoffs.mjs');

function project({ context = '', direction = '', records = [], folder = true }) {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), 'check-handoffs-'));
  fs.mkdirSync(path.join(root, 'docs', folder ? 'handoffs' : ''), { recursive: true });
  fs.writeFileSync(path.join(root, 'docs', 'context.md'), context);
  fs.writeFileSync(path.join(root, 'docs', 'product-direction.md'), direction);
  for (const name of records) {
    fs.writeFileSync(path.join(root, 'docs', 'handoffs', name), '# Record\n');
  }
  return root;
}

function check(root) {
  const result = spawnSync(process.execPath, [script, root], { encoding: 'utf8' });
  fs.rmSync(root, { recursive: true, force: true });
  return { status: result.status, out: result.stdout.trim(), err: result.stderr.trim() };
}

test('passes when each record is linked from one of the two docs', () => {
  const result = check(project({
    context: 'The [canvas record](handoffs/a.md) holds the design.',
    direction: 'See the [restyle record](handoffs/b.md#decisions).',
    records: ['a.md', 'b.md'],
  }));
  assert.equal(result.status, 0);
  assert.equal(result.out, 'Handoff records: 2. Each is linked from docs/context.md or docs/product-direction.md.');
});

test('fails on a record that nothing links', () => {
  const result = check(project({ context: 'The [record](handoffs/a.md).', records: ['a.md', 'stale.md'] }));
  assert.equal(result.status, 1);
  assert.equal(result.err, 'docs/handoffs/stale.md is not linked from docs/context.md or docs/product-direction.md. Link it where its decision is recorded, or delete it.');
});

test('fails on a link to a missing record', () => {
  const result = check(project({ direction: 'The [old record](handoffs/gone.md).', records: [] }));
  assert.equal(result.status, 1);
  assert.equal(result.err, 'A link in docs/context.md or docs/product-direction.md names docs/handoffs/gone.md, which does not exist.');
});

test('links may start with ./ and carry a title', () => {
  const result = check(project({
    context: 'The [canvas record](./handoffs/a.md) and the [restyle record](handoffs/b.md "Restyle").',
    records: ['a.md', 'b.md'],
  }));
  assert.equal(result.status, 0);
  assert.equal(result.out, 'Handoff records: 2. Each is linked from docs/context.md or docs/product-direction.md.');
});

test('a project without a handoffs folder has no records', () => {
  const result = check(project({ folder: false }));
  assert.equal(result.status, 0);
  assert.equal(result.out, 'Handoff records: 0. Each is linked from docs/context.md or docs/product-direction.md.');
});

test('only markdown files count as records', () => {
  const root = project({ context: 'The [record](handoffs/a.md).', records: ['a.md'] });
  fs.writeFileSync(path.join(root, 'docs', 'handoffs', 'diagram.png'), '');
  const result = check(root);
  assert.equal(result.status, 0);
  assert.equal(result.out, 'Handoff records: 1. Each is linked from docs/context.md or docs/product-direction.md.');
});

test('a file name in plain text is not a link', () => {
  const result = check(project({ context: 'Mentions `handoffs/a.md` without a link.', records: ['a.md'] }));
  assert.equal(result.status, 1);
  assert.match(result.err, /^docs\/handoffs\/a\.md is not linked/);
});
