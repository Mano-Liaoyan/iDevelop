#!/usr/bin/env node
import { readdirSync, readFileSync, writeFileSync } from 'node:fs';
import { dirname, join, relative, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const root = resolve(dirname(fileURLToPath(import.meta.url)), '..');
const desktop = join(root, 'src', 'IDevelop.Desktop');
const output = join(desktop, 'Theme', 'Tokens.axaml');

// PlanWeave packages/desktop/src/renderer/index.css at 8647d015, as [:root, .dark].
// The edge colors come from graph/dependencyEdgeVisual.ts and are the same in both themes.
const planweave = {
  black: ['oklch(0 0 0)', 'oklch(0 0 0)'],
  foreground: ['oklch(0.145 0 0)', 'oklch(0.985 0 0)'],
  border: ['oklch(0.922 0 0)', 'oklch(1 0 0 / 10%)'],
  'app-shell': ['oklch(0.973 0.006 96)', 'oklch(0.18 0.006 80)'],
  'app-sidebar': ['oklch(0.955 0.008 255)', 'oklch(0.205 0.008 250)'],
  'app-topbar': ['oklch(0.982 0.004 106)', 'oklch(0.215 0.006 80)'],
  'app-canvas': ['oklch(0.987 0.003 106)', 'oklch(0.16 0.008 120)'],
  'app-panel': ['oklch(0.99 0.002 106)', 'oklch(0.225 0.006 80)'],
  'surface-base': ['oklch(0.99 0.002 106)', 'oklch(0.205 0.006 80)'],
  'surface-muted': ['oklch(0.952 0.005 248)', 'oklch(0.265 0.008 248)'],
  'surface-raised': ['oklch(1 0 0)', 'oklch(0.245 0.007 80)'],
  'surface-overlay': ['oklch(0.985 0.004 250)', 'oklch(0.28 0.008 248)'],
  'text-strong': ['oklch(0.16 0.006 248)', 'oklch(0.96 0.004 100)'],
  text: ['oklch(0.28 0.01 248)', 'oklch(0.86 0.006 100)'],
  'text-muted': ['oklch(0.49 0.014 248)', 'oklch(0.68 0.008 100)'],
  'text-faint': ['oklch(0.67 0.012 248)', 'oklch(0.5 0.01 100)'],
  'state-selected': ['oklch(0.58 0.14 250)', 'oklch(0.72 0.13 250)'],
  'state-selected-surface': ['oklch(0.94 0.035 250)', 'oklch(0.32 0.055 250)'],
  'state-running': ['oklch(0.63 0.13 195)', 'oklch(0.72 0.12 195)'],
  'state-running-surface': ['oklch(0.94 0.04 195)', 'oklch(0.31 0.05 195)'],
  'state-success': ['oklch(0.61 0.14 145)', 'oklch(0.72 0.13 145)'],
  'state-success-surface': ['oklch(0.94 0.045 145)', 'oklch(0.32 0.055 145)'],
  'state-failed': ['oklch(0.58 0.2 28)', 'oklch(0.72 0.17 28)'],
  'state-failed-surface': ['oklch(0.94 0.05 28)', 'oklch(0.32 0.065 28)'],
  'edge-0': ['#2563eb', '#2563eb'],
  'edge-9': ['#ea580c', '#ea580c'],
};

// Brush key prefix, PlanWeave token, and opacity in percent.
const brushes = [
  ['AppShell', 'app-shell'],
  ['AppSidebar', 'app-sidebar'],
  ['AppTopbar', 'app-topbar'],
  ['AppPanel', 'app-panel'],
  ['SurfaceBase', 'surface-base'],
  ['SurfaceMuted', 'surface-muted'],
  ['SurfaceMuted60', 'surface-muted', 60],
  ['SurfaceRaised', 'surface-raised'],
  ['SurfaceOverlay', 'surface-overlay'],
  ['SurfaceOverlay95', 'surface-overlay', 95],
  ['TextStrong', 'text-strong'],
  ['Text', 'text'],
  ['TextMuted', 'text-muted'],
  ['TextFaint', 'text-faint'],
  ['Border100', 'border'],
  ['Border80', 'border', 80],
  ['Border60', 'border', 60],
  ['StateSelected', 'state-selected'],
  ['StateSelected25', 'state-selected', 25],
  ['StateSelected40', 'state-selected', 40],
  ['StateSelected55', 'state-selected', 55],
  ['StateSelectedSurface', 'state-selected-surface'],
  ['StateRunning', 'state-running'],
  ['StateRunning45', 'state-running', 45],
  ['StateRunning55', 'state-running', 55],
  ['StateRunningSurface', 'state-running-surface'],
  ['StateSuccess', 'state-success'],
  ['StateSuccess45', 'state-success', 45],
  ['StateSuccess55', 'state-success', 55],
  ['StateSuccessSurface', 'state-success-surface'],
  ['StateFailed', 'state-failed'],
  ['StateFailed50', 'state-failed', 50],
  ['StateFailed60', 'state-failed', 60],
  ['StateFailedSurface', 'state-failed-surface'],
  ['ConnectionDependency', 'edge-0'],
  ['ConnectionContext', 'text-muted'],
];

// Color keys for brushes that must stay the same objects while their color follows the theme. Nodify's control
// themes read its brushes with StaticResource, and a DrawingBrush keeps drawing the brushes of its first frame.
const colorKeys = [
  ['CanvasGrid.BackgroundColor', 'app-canvas'],
  ['CanvasGrid.DotColor', 'border'],
  ['NodifyEditor.SelectionRectangleColor', 'state-selected'],
  ['PendingConnection.StrokeColor', 'state-selected'],
  ['PendingConnection.BackgroundColor', 'surface-overlay'],
  ['PendingConnection.ForegroundColor', 'text'],
  ['PendingConnection.BorderColor', 'border'],
  ['Minimap.BackgroundColor', 'surface-muted'],
  ['MinimapItem.BackgroundColor', 'text-faint'],
];

// Each layer is x, y, blur, spread, token, and opacity in percent. Tailwind's shadow-sm is two black layers at 10%.
// A status card's ring-1 at 15% is a one-pixel spread layer in the status color.
const shadowSm = [
  [0, 1, 3, 0, 'black', 10],
  [0, 1, 2, -1, 'black', 10],
];
const ringed = (token, percent) => [[0, 0, 0, 1, token, percent], ...shadowSm];
const themedShadows = [
  ['CardShadow', ringed('foreground', 10), ringed('foreground', 10)],
  ['CardRunningShadow', ringed('state-running', 15), ringed('state-running', 15)],
  ['CardSuccessShadow', ringed('state-success', 15), ringed('state-success', 15)],
  ['CardFailedShadow', ringed('state-failed', 15), ringed('state-failed', 15)],
  ['CardWaitingShadow', ringed('state-selected', 15), ringed('state-selected', 15)],
  ['FloatingShadow', [[0, 12, 28, 0, 'black', 12]], [[0, 14, 32, 0, 'black', 32]]],
];

// --radius is 0.625rem, and Tailwind derives each step from it.
const radius = 10;
const radii = [
  ['RadiusSm', 0.6],
  ['RadiusMd', 0.8],
  ['RadiusLg', 1],
  ['RadiusXl', 1.4],
];

function parse(value) {
  const hex = /^#([0-9a-f]{6})$/i.exec(value);
  if (hex) {
    return { rgb: [0, 2, 4].map((i) => parseInt(hex[1].slice(i, i + 2), 16)), alpha: 1 };
  }
  const oklch = /^oklch\(([\d.]+) ([\d.]+) ([\d.]+)(?: \/ ([\d.]+)%)?\)$/.exec(value);
  if (!oklch) {
    throw new Error(`Unsupported color ${value}`);
  }
  const [l, c, h] = oklch.slice(1, 4).map(Number);
  return { rgb: toSrgb(l, c, h), alpha: oklch[4] === undefined ? 1 : Number(oklch[4]) / 100 };
}

// OKLCH to OKLab to linear sRGB, clamped to the gamut, then gamma encoded.
function toSrgb(l, c, h) {
  const a = c * Math.cos((h * Math.PI) / 180);
  const b = c * Math.sin((h * Math.PI) / 180);
  const l_ = (l + 0.3963377774 * a + 0.2158037573 * b) ** 3;
  const m_ = (l - 0.1055613458 * a - 0.0638541728 * b) ** 3;
  const s_ = (l - 0.0894841775 * a - 1.291485548 * b) ** 3;
  return [
    4.0767416621 * l_ - 3.3077115913 * m_ + 0.2309699292 * s_,
    -1.2684380046 * l_ + 2.6097574011 * m_ - 0.3413193965 * s_,
    -0.0041960863 * l_ - 0.7034186147 * m_ + 1.707614701 * s_,
  ].map((linear) => {
    const x = Math.min(1, Math.max(0, linear));
    return Math.round((x <= 0.0031308 ? 12.92 * x : 1.055 * x ** (1 / 2.4) - 0.055) * 255);
  });
}

const byte = (n) => n.toString(16).padStart(2, '0').toUpperCase();

// Avalonia puts alpha first.
function color(token, theme, percent = 100) {
  const { rgb, alpha } = parse(planweave[token][theme]);
  const a = Math.round(alpha * (percent / 100) * 255);
  return `#${a === 255 ? '' : byte(a)}${rgb.map(byte).join('')}`;
}

const shadow = (layers, theme) =>
  layers.map(([x, y, blur, spread, token, percent]) => `${x} ${y} ${blur} ${spread} ${color(token, theme, percent)}`).join(', ');

function themeDictionary(name, theme) {
  return [
    `    <ResourceDictionary x:Key="${name}">`,
    ...brushes.map(([key, token, percent]) => `      <SolidColorBrush x:Key="${key}Brush" Color="${color(token, theme, percent)}" />`),
    ...colorKeys.map(([key, token]) => `      <Color x:Key="${key}">${color(token, theme)}</Color>`),
    ...themedShadows.map(([key, ...layers]) => `      <BoxShadows x:Key="${key}">${shadow(layers[theme], theme)}</BoxShadows>`),
    '    </ResourceDictionary>',
  ];
}

function generate() {
  return [
    '<!-- Generated by scripts/planweave-tokens.mjs. Edit the script and rerun it. -->',
    '<ResourceDictionary xmlns="https://github.com/avaloniaui"',
    '                    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">',
    '  <ResourceDictionary.ThemeDictionaries>',
    ...themeDictionary('Light', 0),
    ...themeDictionary('Dark', 1),
    '  </ResourceDictionary.ThemeDictionaries>',
    `  <BoxShadows x:Key="SmallShadow">${shadow(shadowSm, 0)}</BoxShadows>`,
    ...radii.map(([key, scale]) => `  <CornerRadius x:Key="${key}">${radius * scale}</CornerRadius>`),
    '</ResourceDictionary>',
    '',
  ].join('\n');
}

// Avalonia's named colors, which it parses without regard to case. Transparent paints nothing, so it may appear anywhere.
const namedColors = new Set(
  `AliceBlue AntiqueWhite Aqua Aquamarine Azure Beige Bisque Black BlanchedAlmond Blue BlueViolet Brown BurlyWood CadetBlue
  Chartreuse Chocolate Coral CornflowerBlue Cornsilk Crimson Cyan DarkBlue DarkCyan DarkGoldenrod DarkGray DarkGreen DarkKhaki
  DarkMagenta DarkOliveGreen DarkOrange DarkOrchid DarkRed DarkSalmon DarkSeaGreen DarkSlateBlue DarkSlateGray DarkTurquoise
  DarkViolet DeepPink DeepSkyBlue DimGray DodgerBlue Firebrick FloralWhite ForestGreen Fuchsia Gainsboro GhostWhite Gold Goldenrod
  Gray Green GreenYellow Honeydew HotPink IndianRed Indigo Ivory Khaki Lavender LavenderBlush LawnGreen LemonChiffon LightBlue
  LightCoral LightCyan LightGoldenrodYellow LightGray LightGreen LightPink LightSalmon LightSeaGreen LightSkyBlue LightSlateGray
  LightSteelBlue LightYellow Lime LimeGreen Linen Magenta Maroon MediumAquamarine MediumBlue MediumOrchid MediumPurple
  MediumSeaGreen MediumSlateBlue MediumSpringGreen MediumTurquoise MediumVioletRed MidnightBlue MintCream MistyRose Moccasin
  NavajoWhite Navy OldLace Olive OliveDrab Orange OrangeRed Orchid PaleGoldenrod PaleGreen PaleTurquoise PaleVioletRed PapayaWhip
  PeachPuff Peru Pink Plum PowderBlue Purple Red RosyBrown RoyalBlue SaddleBrown Salmon SandyBrown SeaGreen SeaShell Sienna
  Silver SkyBlue SlateBlue SlateGray Snow SpringGreen SteelBlue Tan Teal Thistle Tomato Turquoise Violet Wheat White WhiteSmoke
  Yellow YellowGreen`
    .split(/\s+/)
    .map((name) => name.toLowerCase()),
);

// Each pattern finds colors in one kind of file. A character reference such as &#160; is not a hex color. A XAML
// value or element that is only a color name is a named color, except a font weight, because Black is also a weight.
const colorPatterns = [
  [/\.(axaml|xaml|cs)$/, /(?<!&)#(?:[0-9a-f]{8}|[0-9a-f]{6}|[0-9a-f]{3,4})\b/gi, () => true],
  [
    /\.(axaml|xaml)$/,
    /(?<=(?:=\s*["']|>)\s*)(?<!(?:FontWeight|Property\s*=\s*["']FontWeight["']\s+Value)\s*=\s*["']\s*)[a-z]+(?=\s*["'<])/gi,
    (name) => namedColors.has(name.toLowerCase()),
  ],
  [/\.cs$/, /\b(?:Brushes|Colors)\.\w+|\bColor\.(?:Parse|From\w*)/g, () => true],
];

function* files(folder) {
  for (const entry of readdirSync(folder, { withFileTypes: true })) {
    const path = join(folder, entry.name);
    if (entry.isDirectory()) {
      if (entry.name !== 'bin' && entry.name !== 'obj') {
        yield* files(path);
      }
    } else if (path !== output && /\.(axaml|xaml|cs)$/.test(path)) {
      yield path;
    }
  }
}

function strayColors() {
  const found = [];
  for (const path of files(desktop)) {
    const patterns = colorPatterns.filter(([kind]) => kind.test(path));
    readFileSync(path, 'utf8')
      .split('\n')
      .forEach((line, index) => {
        for (const [, pattern, isColor] of patterns) {
          for (const [match] of line.matchAll(pattern)) {
            if (isColor(match)) {
              found.push(`${relative(root, path).replaceAll('\\', '/')}:${index + 1}: ${match}`);
            }
          }
        }
      });
  }
  return found;
}

const args = process.argv.slice(2);
if (args.length === 0) {
  writeFileSync(output, generate());
  console.log(`Wrote ${relative(root, output).replaceAll('\\', '/')}.`);
} else if (args.length === 1 && args[0] === '--check') {
  let failed = false;
  let current = '';
  try {
    current = readFileSync(output, 'utf8').replaceAll('\r\n', '\n');
  } catch {}
  if (current !== generate()) {
    console.error('src/IDevelop.Desktop/Theme/Tokens.axaml is stale. Run node scripts/planweave-tokens.mjs.');
    failed = true;
  }
  const stray = strayColors();
  if (stray.length > 0) {
    console.error('Colors belong in the generated Tokens.axaml. Found colors in:');
    stray.forEach((line) => console.error(`  ${line}`));
    failed = true;
  }
  if (failed) {
    process.exit(1);
  }
  console.log('Tokens.axaml is current, and no other desktop XAML or C# file sets a color.');
} else {
  console.error('Usage: node scripts/planweave-tokens.mjs [--check]');
  process.exit(2);
}
