import { existsSync, readdirSync, readFileSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';

const root = process.argv[2] ?? join(dirname(fileURLToPath(import.meta.url)), '..');
const docs = ['docs/context.md', 'docs/product-direction.md'];
const folder = 'docs/handoffs';

// Git keeps no empty folder, so a clone without records has no handoffs folder at all.
const records = existsSync(join(root, folder)) ? readdirSync(join(root, folder)).filter((name) => name.endsWith('.md')) : [];
const links = new Set(
  docs.flatMap((doc) =>
    [...readFileSync(join(root, doc), 'utf8').matchAll(/\]\((?:\.\/)?handoffs\/([^)#\s]+\.md)(?:#[^)\s]*)?(?:\s+"[^"]*")?\)/g)].map(
      (match) => match[1],
    ),
  ),
);

const problems = [
  ...records
    .filter((name) => !links.has(name))
    .map((name) => `${folder}/${name} is not linked from ${docs.join(' or ')}. Link it where its decision is recorded, or delete it.`),
  ...[...links]
    .filter((name) => !records.includes(name))
    .map((name) => `A link in ${docs.join(' or ')} names ${folder}/${name}, which does not exist.`),
];

if (problems.length > 0) {
  console.error(problems.join('\n'));
  process.exit(1);
}
console.log(`Handoff records: ${records.length}. Each is linked from ${docs.join(' or ')}.`);
