import { readFile, writeFile } from 'node:fs/promises';
import { fileURLToPath } from 'node:url';
import path from 'node:path';
import { createElement } from 'react';
import { renderToStaticMarkup } from 'react-dom/server';
import { build, createServer } from 'vite';

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const catalog = await readFile(path.resolve(root, '../JitHub.WinUI/Models/ThemePaletteCatalog.cs'), 'utf8');
const entries = [...catalog.matchAll(/(?:Resource|Generated)\(ThemePaletteIds\.(\w+),\s*"[^"]+",\s*"([^"]+)",\s*"([^"]+)",\s*(?:"[^"]+",\s*)?(?:new|Light)\(([^)]*)\),\s*(?:new|Dark)\(([^)]*)\)\)/g)];
if (entries.length !== 20) throw new Error(`Expected 20 theme palettes; found ${entries.length}`);
const values = (source) => [...source.matchAll(/"([^"]*)"/g)].map(match => match[1]);
const preview = (source) => {
  const colors = values(source);
  return colors.length === 5
    ? { canvas: colors[0], rail: colors[1], surface: colors[2], accent: colors[3], ink: colors[4] }
    : { canvas: colors[0], rail: colors[1], surface: colors[2], accent: colors[9], ink: colors[7] };
};
const palettes = entries.map(([, id, name, description, light, dark]) => ({
  id, name, description, light: preview(light), dark: preview(dark)
}));

await build({ root, base: '/' });
const server = await createServer({ root, server: { middlewareMode: true }, appType: 'custom' });
try {
  const { default: App } = await server.ssrLoadModule('/src/App.jsx');
  const rendered = renderToStaticMarkup(createElement(App, { palettes }));
  const output = path.join(root, 'dist/index.html');
  const html = await readFile(output, 'utf8');
  if (!html.includes('<!--JITHUB_APP-->')) throw new Error('Missing static render marker');
  await writeFile(output, html.replace('<!--JITHUB_APP-->', rendered));
} finally {
  await server.close();
}
