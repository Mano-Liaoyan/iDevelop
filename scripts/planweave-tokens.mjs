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
  'edge-0': ['#2563eb', '#2563eb'],
  'edge-9': ['#ea580c', '#ea580c'],
};

// Apple's system colors from https://developer.apple.com/design/human-interface-guidelines/color, fetched 2026-10-05,
// as [light, dark]. A plain value is for tints, dots, the minimap, and dark strokes. A -strong value is the
// increased-contrast one, for glyphs and light strokes, never text. A -text value is for colored text, a -fill value is
// a fill under white text, and a -tile value carries a white glyph.
const apple = {
  'apple-red': ['#FF383C', '#FF4245'],
  'apple-red-strong': ['#E9152D', '#FF6165'],
  'apple-red-text': ['#D70015', '#FF6165'],
  'apple-red-fill': ['#E9152D', '#E9152D'],
  'apple-orange': ['#FF8D28', '#FF9230'],
  'apple-orange-strong': ['#C55300', '#FFA056'],
  'apple-yellow': ['#FFCC00', '#FFD600'],
  'apple-yellow-strong': ['#A16A00', '#FEDF43'],
  'apple-green': ['#34C759', '#30D158'],
  'apple-green-strong': ['#008932', '#4AD968'],
  'apple-mint': ['#00C8B3', '#00DAC3'],
  'apple-mint-strong': ['#008575', '#54DFCB'],
  'apple-mint-tile': ['#008575', '#008575'],
  'apple-cyan': ['#00C0E8', '#3CD3FE'],
  'apple-cyan-strong': ['#007EAE', '#6DD9FF'],
  'apple-cyan-tile': ['#007EAE', '#007EAE'],
  'apple-blue': ['#0088FF', '#0091FF'],
  'apple-blue-strong': ['#1E6EF4', '#5CB8FF'],
  'apple-blue-text': ['#0040DD', '#5CB8FF'],
  'apple-blue-fill': ['#1E6EF4', '#1E6EF4'],
  'apple-indigo': ['#6155F5', '#6D7CFF'],
  'apple-indigo-strong': ['#564ADE', '#A7AAFF'],
  'apple-indigo-tile': ['#564ADE', '#6D7CFF'],
  'apple-purple': ['#CB30E0', '#DB34F2'],
  'apple-purple-strong': ['#B02FC2', '#EA8DFF'],
  'apple-purple-tile': ['#B02FC2', '#DB34F2'],
  'apple-brown': ['#AC7F5E', '#B78A66'],
  'apple-brown-strong': ['#956D51', '#DBA679'],
  'apple-brown-tile': ['#956D51', '#B78A66'],
  'apple-gray': ['#8E8E93', '#8E8E93'],
  'apple-gray-strong': ['#6C6C70', '#AEAEB2'],
  'apple-gray-tile': ['#6C6C70', '#8E8E93'],
  white: ['#FFFFFF', '#FFFFFF'],
};

const tokens = { ...planweave, ...apple };

// Each node kind and each state has its own hue. Blue is the accent.
const kinds = [
  ['Implement', 'indigo'],
  ['Plan', 'cyan'],
  ['Architect', 'purple'],
  ['Review', 'mint'],
  ['Approval', 'brown'],
  ['ReadOnlyAgent', 'gray'],
];
const states = [
  ['Waiting', 'orange'],
  ['Warning', 'yellow'],
  ['Success', 'green'],
  ['Failed', 'red'],
  ['Neutral', 'gray'],
];

// Brush key prefix, token, and opacity in percent. A pair of tokens is the light theme's and the dark theme's: a stroke
// takes the increased-contrast value on a light surface and the plain one on a dark surface.
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
  ['StateSelected', 'apple-blue'],
  ['StateSelected25', 'apple-blue', 25],
  ['StateSelected40', 'apple-blue', 40],
  ['StateSelected55', 'apple-blue', 55],
  ['StateSelectedSurface', 'apple-blue', 10],
  ['StateRunning', 'apple-blue'],
  ['StateRunning45', 'apple-blue', 45],
  ['StateRunning55', 'apple-blue', 55],
  ['StateRunningSurface', 'apple-blue', 10],
  ['StateSuccess', 'apple-green'],
  ['StateSuccess45', 'apple-green', 45],
  ['StateSuccess55', 'apple-green', 55],
  ['StateSuccessSurface', 'apple-green', 10],
  ['StateFailed', 'apple-red'],
  ['StateFailed50', 'apple-red', 50],
  ['StateFailed60', 'apple-red', 60],
  ['StateFailedSurface', 'apple-red', 10],
  ['StateWaiting', 'apple-orange'],
  ['StateWaiting40', 'apple-orange', 40],
  ['StateWaiting55', 'apple-orange', 55],
  ['ConnectionDependency', 'edge-0'],
  ['ConnectionContext', 'text-muted'],
  ...kinds.flatMap(([kind, hue]) => [
    [`Kind${kind}`, `apple-${hue}`],
    [`Kind${kind}10`, `apple-${hue}`, 10],
    [`Kind${kind}Strong`, `apple-${hue}-strong`],
    [`Kind${kind}Tile`, `apple-${hue}-tile`],
    [`Kind${kind}Stroke`, [`apple-${hue}-strong`, `apple-${hue}`]],
  ]),
  ...states.flatMap(([state, hue]) => [
    [`State${state}10`, `apple-${hue}`, 10],
    [`State${state}Strong`, `apple-${hue}-strong`],
    [`State${state}Stroke`, [`apple-${hue}-strong`, `apple-${hue}`]],
  ]),
  ['Accent', 'apple-blue'],
  ['Accent10', 'apple-blue', 10],
  ['AccentStrong', 'apple-blue-strong'],
  ['AccentStroke', ['apple-blue-strong', 'apple-blue']],
  ['AccentText', 'apple-blue-text'],
  ['AccentFill', 'apple-blue-fill'],
  ['DestructiveText', 'apple-red-text'],
  ['DestructiveFill', 'apple-red-fill'],
  ['OnAccent', 'white'],
  ['OnTile', 'white'],
];

// Color keys for brushes that must stay the same objects while their color follows the theme. Nodify's control
// themes read its brushes with StaticResource, and a DrawingBrush keeps drawing the brushes of its first frame.
const colorKeys = [
  ['CanvasGrid.BackgroundColor', 'app-canvas'],
  ['CanvasGrid.DotColor', 'border'],
  ['NodifyEditor.SelectionRectangleColor', 'apple-blue'],
  ['PendingConnection.StrokeColor', 'apple-blue'],
  ['PendingConnection.BackgroundColor', 'surface-overlay'],
  ['PendingConnection.ForegroundColor', 'text'],
  ['PendingConnection.BorderColor', 'border'],
  ['Minimap.BackgroundColor', 'surface-muted'],
  ['MinimapItem.BackgroundColor', 'text-faint'],
  ...kinds.map(([kind, hue]) => [`Kind${kind}Color`, `apple-${hue}`]),
  ['AccentColor', 'apple-blue'],
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
  ['CardRunningShadow', ringed('apple-blue', 15), ringed('apple-blue', 15)],
  ['CardSuccessShadow', ringed('apple-green', 15), ringed('apple-green', 15)],
  ['CardFailedShadow', ringed('apple-red', 15), ringed('apple-red', 15)],
  ['CardWaitingShadow', ringed('apple-orange', 15), ringed('apple-orange', 15)],
  ['FloatingShadow', [[0, 12, 28, 0, 'black', 12]], [[0, 14, 32, 0, 'black', 32]]],
  ['NodeShadow', [[0, 1, 2, 0, 'black', 8], [0, 2, 6, 0, 'black', 6]], [[0, 1, 2, 0, 'black', 40], [0, 2, 8, 0, 'black', 32]]],
  ['NodeHoverShadow', [[0, 2, 6, 0, 'black', 10], [0, 8, 20, 0, 'black', 10]], [[0, 2, 6, 0, 'black', 45], [0, 10, 24, 0, 'black', 40]]],
  ['PopoverShadow', [[0, 10, 30, 0, 'black', 16], [0, 2, 6, 0, 'black', 8]], [[0, 12, 32, 0, 'black', 45], [0, 2, 6, 0, 'black', 30]]],
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

// A color as it is written: sRGB bytes and an alpha byte.
function rgba(token, theme, percent = 100) {
  const { rgb, alpha } = parse(tokens[Array.isArray(token) ? token[theme] : token][theme]);
  return { rgb, a: Math.round(alpha * (percent / 100) * 255) };
}

// Avalonia puts alpha first.
function color(token, theme, percent = 100) {
  const { rgb, a } = rgba(token, theme, percent);
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

// Foreground, background, and the least contrast ratio each theme must reach. A background is one key or a stack of keys,
// top first, that composites over its last, opaque key. Text needs 4.5:1 and a glyph, stroke, or tile needs 3:1.
const canvas = 'CanvasGrid.BackgroundColor';
const strokes = [...kinds.map(([kind]) => `Kind${kind}`), ...states.map(([state]) => `State${state}`), 'Accent'];
const tints = [...states.map(([state]) => `State${state}10Brush`), ...kinds.map(([kind]) => `Kind${kind}10Brush`), 'Accent10Brush'];
const pairs = (foregrounds, backgrounds, minimum) =>
  foregrounds.flatMap((foreground) => backgrounds.map((background) => [foreground, background, minimum]));
const contrast = [
  ...pairs(['TextBrush', 'TextMutedBrush'], ['SurfaceRaisedBrush', 'SurfaceOverlayBrush', 'AppPanelBrush'], 4.5),
  ...pairs(['TextBrush'], tints.map((tint) => [tint, 'SurfaceRaisedBrush']), 4.5),
  ...pairs(['AccentTextBrush', 'DestructiveTextBrush'], ['SurfaceOverlayBrush', 'AppPanelBrush', 'SurfaceRaisedBrush'], 4.5),
  ...pairs(['OnAccentBrush'], ['AccentFillBrush', 'DestructiveFillBrush'], 4.5),
  ...pairs(['OnTileBrush'], kinds.map(([kind]) => `Kind${kind}TileBrush`), 3),
  ...pairs(strokes.map((stroke) => `${stroke}StrokeBrush`), [canvas, 'SurfaceRaisedBrush'], 3),
  ...strokes.flatMap((hue) => pairs([`${hue}StrongBrush`], ['SurfaceRaisedBrush', [`${hue}10Brush`, 'SurfaceRaisedBrush']], 3)),
  ['AccentBrush', canvas, 3],
];

function lookUp(key, theme) {
  const brush = brushes.find(([name]) => `${name}Brush` === key);
  const [, token, percent] = brush ?? colorKeys.find(([name]) => name === key) ?? [];
  if (token === undefined) {
    throw new Error(`The contrast list names ${key}, which is no brush or color key.`);
  }
  return rgba(token, theme, percent);
}

// Source-over compositing of a straight-alpha color onto an opaque one.
const over = ({ rgb, a }, below) => rgb.map((channel, i) => (channel * a + below[i] * (255 - a)) / 255);

function composite(keys, theme) {
  const layers = keys.map((key) => lookUp(key, theme));
  if (layers.at(-1).a !== 255) {
    throw new Error(`${keys.at(-1)} is not opaque, so it cannot be the bottom of a contrast pair.`);
  }
  return layers.slice(0, -1).reduceRight((below, layer) => over(layer, below), layers.at(-1).rgb);
}

// WCAG 2 relative luminance and contrast ratio.
function luminance(rgb) {
  const [r, g, b] = rgb.map((channel) => {
    const c = channel / 255;
    return c <= 0.04045 ? c / 12.92 : ((c + 0.055) / 1.055) ** 2.4;
  });
  return 0.2126 * r + 0.7152 * g + 0.0722 * b;
}

function ratio(foreground, background) {
  const [high, low] = [luminance(foreground), luminance(background)].sort((x, y) => y - x);
  return (high + 0.05) / (low + 0.05);
}

function lowContrast() {
  const found = [];
  ['Light', 'Dark'].forEach((name, theme) => {
    for (const [foreground, background, minimum] of contrast) {
      const stack = [background].flat();
      const below = composite(stack, theme);
      const value = ratio(over(lookUp(foreground, theme), below), below);
      if (value < minimum) {
        found.push(`${name}: ${foreground} on ${stack.join(' over ')} is ${value.toFixed(2)}:1, below ${minimum}:1.`);
      }
    }
  });
  return found;
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

function reportContrast() {
  const low = lowContrast();
  if (low.length > 0) {
    console.error('These color pairs are too close to read:');
    low.forEach((line) => console.error(`  ${line}`));
  }
  return low.length > 0;
}

const args = process.argv.slice(2);
if (args.length === 0) {
  writeFileSync(output, generate());
  console.log(`Wrote ${relative(root, output).replaceAll('\\', '/')}.`);
  if (reportContrast()) {
    process.exit(1);
  }
} else if (args.length === 1 && args[0] === '--check') {
  let failed = reportContrast();
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
  console.log(`Tokens.axaml is current, ${contrast.length * 2} color pairs keep their contrast, and no other desktop XAML or C# file sets a color.`);
} else {
  console.error('Usage: node scripts/planweave-tokens.mjs [--check]');
  process.exit(2);
}
