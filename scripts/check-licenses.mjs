#!/usr/bin/env node
import { createHash } from 'node:crypto';
import { existsSync, readFileSync } from 'node:fs';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const root = resolve(dirname(fileURLToPath(import.meta.url)), '..');
const allowed = new Set(['MIT', 'Apache-2.0', 'BSD-2-Clause', 'BSD-3-Clause']);
const noticePattern = /(^|\/)(third[-_]?party[-_]?(notice|license)|licenses?[-_]?third[-_]?party|notice|copying)[^/]*$/i;

const licenseTextReviews = {
  'Avalonia.Angle.Windows.Natives/2.1.25547.20250602': {
    license: 'BSD-3-Clause',
    reason: 'The packaged LICENSE file is the ANGLE Project Authors BSD 3-clause text.',
  },
  'xunit.abstractions/2.0.3': {
    license: 'Apache-2.0',
    reason:
      'The nuspec licenseUrl https://raw.githubusercontent.com/xunit/xunit/master/license.txt and the package repository license https://raw.githubusercontent.com/xunit/abstractions.xunit/main/license.txt are both the Apache License 2.0 text, copyright .NET Foundation and Contributors, read on 2026-10-03.',
  },
};

const skiaNotice = {
  sha256: ['21504c46c4c58aa64c1055bd2dcbc5f9a136b4b8c412ed3cc6740e22c5b127f5'],
  summary:
    'One Microsoft notice for SkiaSharp and HarfBuzzSharp native code. Outside the allowed list it names: MPL-1.1, GPL-2.0, or LGPL-2.1 (Skia GIF decoder, zlib mozzconf.h), LGPL-2.1 or eCos (libmicrohttpd), FreeType License or GPL-2.0 (FreeType), HarfBuzz Old MIT, libpng, IJG and Zlib (libjpeg-turbo, SDL, zlib), ICU, Unicode data, NAIST IPADIC with ICOT terms (ICU dictionaries), Khronos (SPIR-V Headers), Adobe DNG SDK License Agreement, and public domain (jsoncpp, tz database).',
};
const notices = {
  'HarfBuzzSharp.NativeAssets.Linux/8.3.1.1': skiaNotice,
  'HarfBuzzSharp.NativeAssets.macOS/8.3.1.1': skiaNotice,
  'HarfBuzzSharp.NativeAssets.WebAssembly/8.3.1.1': skiaNotice,
  'HarfBuzzSharp.NativeAssets.Win32/8.3.1.1': skiaNotice,
  'Microsoft.CodeCoverage/18.10.1': {
    sha256: ['4fc4244935bbe3516164402c573f053a5d246571ed4d357e6cccd8ebec296bbc'],
    summary: 'Mono.Cecil 0.11.3 under MIT. Nothing outside the allowed list.',
  },
  'Microsoft.TestPlatform.TestHost/18.10.1': {
    sha256: ['e2ce7628e0f67d0d724eea9d9286a6938bf1eedd253fc84b23d72ae54778a5f7'],
    summary:
      'Newtonsoft.Json 13.0.3 and Mono.Cecil 0.11.3 under MIT, NuGet.Client 6.8.0.117 under Apache-2.0. Nothing outside the allowed list.',
  },
  'SkiaSharp/2.88.9': skiaNotice,
  'SkiaSharp.NativeAssets.Linux/2.88.9': skiaNotice,
  'SkiaSharp.NativeAssets.macOS/2.88.9': skiaNotice,
  'SkiaSharp.NativeAssets.WebAssembly/2.88.9': skiaNotice,
  'SkiaSharp.NativeAssets.Win32/2.88.9': skiaNotice,
};

const solution = readFileSync(join(root, 'iDevelop.slnx'), 'utf8');
const projects = [...solution.matchAll(/<Project Path="([^"]+)"/g)].map((match) => dirname(join(root, match[1])));

const packages = new Map();
for (const project of projects) {
  const assetsPath = join(project, 'obj', 'project.assets.json');
  if (!existsSync(assetsPath)) {
    console.error(`${assetsPath} is missing. Run dotnet restore first.`);
    process.exit(1);
  }
  const assets = JSON.parse(readFileSync(assetsPath, 'utf8'));
  const folders = Object.keys(assets.packageFolders);
  for (const target of Object.values(assets.targets)) {
    for (const [key, entry] of Object.entries(target)) {
      if (entry.type !== 'package' || packages.has(key)) continue;
      const library = assets.libraries[key];
      const nuspec = library.files.find((file) => file.endsWith('.nuspec'));
      const packageDir = folders.map((folder) => join(folder, library.path)).find((dir) => existsSync(join(dir, nuspec)));
      const noticeFiles = packageDir ? library.files.filter((file) => noticePattern.test(file)) : [];
      packages.set(key, {
        nuspecPath: packageDir && join(packageDir, nuspec),
        noticesBySha256: Map.groupBy(noticeFiles, (file) => sha256(join(packageDir, file))),
      });
    }
  }
}

const rows = [];
const problems = [];
for (const [key, { nuspecPath, noticesBySha256 }] of packages) {
  const [id, version] = key.split('/');
  for (const [hash, files] of noticesBySha256) {
    if (!notices[key]?.sha256.includes(hash)) {
      problems.push(`${id} ${version}: ships ${files.join(', ')} with SHA-256 ${hash}, which no reviewed notice entry covers`);
    }
  }
  if (!nuspecPath) {
    problems.push(`${id} ${version}: .nuspec not found in the NuGet package folders`);
    continue;
  }
  const expression = readFileSync(nuspecPath, 'utf8').match(/<license\b[^>]*\btype="expression"[^>]*>([^<]*)<\/license>/)?.[1].trim();
  const exception = licenseTextReviews[key];
  if (expression && exception) problems.push(`${id} ${version}: has the expression ${expression}, so remove its exception`);
  const license = expression ?? exception?.license;
  if (!license) problems.push(`${id} ${version}: no license expression and no exception`);
  else if (!permits(license)) problems.push(`${id} ${version}: ${license} is not ${[...allowed].join(', ')}`);
  rows.push([id, version, license ? (expression ? license : `${license} (exception)`) : '(none)']);
}
for (const key of Object.keys(licenseTextReviews)) {
  if (!packages.has(key)) problems.push(`${key}: exception for a package that is not restored`);
}
for (const [key, { sha256: hashes }] of Object.entries(notices)) {
  const shipped = packages.get(key)?.noticesBySha256;
  if (!shipped?.size) {
    problems.push(`${key}: notice entry for a package that is not restored or ships no notice`);
    continue;
  }
  for (const hash of hashes) {
    if (!shipped.has(hash)) problems.push(`${key}: notice entry for SHA-256 ${hash}, which the package does not ship`);
  }
}

rows.sort(([a, av], [b, bv]) => a.localeCompare(b, 'en', { sensitivity: 'base' }) || av.localeCompare(bv));
const table = [['package', 'version', 'license'], ...rows];
const widths = [0, 1].map((column) => Math.max(...table.map((row) => row[column].length)));
for (const row of table) console.log(`${row[0].padEnd(widths[0])}  ${row[1].padEnd(widths[1])}  ${row[2]}`);

console.log('\nReviewed third-party notices:');
for (const [{ summary }, keys] of Map.groupBy(Object.keys(notices), (key) => notices[key])) {
  console.log(`  ${keys.map((key) => key.replace('/', ' ')).join(', ')}\n    ${summary}`);
}

if (problems.length > 0) {
  console.error(`\n${problems.length} license problem(s):`);
  for (const problem of problems) console.error(`  ${problem}`);
  process.exitCode = 1;
}

function sha256(path) {
  return createHash('sha256').update(readFileSync(path)).digest('hex');
}

function permits(expression) {
  return expression.split(/\s+OR\s+/).some((alternative) => alternative.split(/\s+AND\s+/).every((id) => allowed.has(id)));
}
