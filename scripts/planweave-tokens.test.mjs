import assert from 'node:assert/strict';
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { spawnSync } from 'node:child_process';
import { test } from 'node:test';

const script = path.join(import.meta.dirname, 'planweave-tokens.mjs');

// A copy of the generator in an empty project, with one token changed, and the Tokens.axaml it writes.
function project(from = '', to = '') {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), 'planweave-tokens-'));
  fs.mkdirSync(path.join(root, 'scripts'));
  fs.mkdirSync(path.join(root, 'src', 'IDevelop.Desktop', 'Theme'), { recursive: true });
  const source = fs.readFileSync(script, 'utf8');
  assert.ok(source.includes(from), `the generator holds ${from}`);
  fs.writeFileSync(path.join(root, 'scripts', 'planweave-tokens.mjs'), source.replace(from, to));
  spawnSync(process.execPath, [path.join(root, 'scripts', 'planweave-tokens.mjs')], { encoding: 'utf8' });
  return root;
}

function check(root) {
  const result = spawnSync(process.execPath, [path.join(root, 'scripts', 'planweave-tokens.mjs'), '--check'], { encoding: 'utf8' });
  fs.rmSync(root, { recursive: true, force: true });
  return { status: result.status, out: result.stdout.trim(), err: result.stderr.trim() };
}

test('passes when every color pair keeps its contrast', () => {
  const result = check(project());
  assert.equal(result.status, 0, result.err);
  assert.match(result.out, /^Tokens\.axaml is current, \d+ color pairs keep their contrast/);
});

test('fails when a text color drops below 4.5:1 on a surface it sits on', () => {
  const result = check(project("'apple-red-text': ['#D70015'", "'apple-red-text': ['#FF7F7F'"));
  assert.equal(result.status, 1);
  assert.equal(result.err, [
    'These color pairs are too close to read:',
    '  Light: DestructiveTextBrush on SurfaceOverlayBrush is 2.34:1, below 4.5:1.',
    '  Light: DestructiveTextBrush on AppPanelBrush is 2.38:1, below 4.5:1.',
    '  Light: DestructiveTextBrush on SurfaceRaisedBrush is 2.44:1, below 4.5:1.',
  ].join('\n'));
});

test('fails when a glyph color drops below 3:1, measured over its own tint too', () => {
  const result = check(project("'apple-indigo-strong': ['#564ADE'", "'apple-indigo-strong': ['#B7B2F2'"));
  assert.equal(result.status, 1);
  assert.equal(result.err, [
    'These color pairs are too close to read:',
    '  Light: KindImplementStrokeBrush on CanvasGrid.BackgroundColor is 1.90:1, below 3:1.',
    '  Light: KindImplementStrokeBrush on SurfaceRaisedBrush is 1.97:1, below 3:1.',
    '  Light: KindImplementStrongBrush on SurfaceRaisedBrush is 1.97:1, below 3:1.',
    '  Light: KindImplementStrongBrush on KindImplement10Brush over SurfaceRaisedBrush is 1.71:1, below 3:1.',
  ].join('\n'));
});
