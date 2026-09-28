import test from 'node:test';
import assert from 'node:assert/strict';
import { readFile, stat } from 'node:fs/promises';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');

test('published page contains the complete static landing page', async () => {
  const html = await readFile(path.join(root, 'dist/index.html'), 'utf8');
  for (const content of [
    '<h1 id="hero-title">JitHub</h1>',
    'Twenty color families are included.',
    'Repository Pull Requests',
    'https://apps.microsoft.com/store/detail/jithub/9MXRBJBB552V',
    'https://jithub.app/media/showcase/home-workspace-light.png'
  ]) assert.ok(html.includes(content), `Missing ${content}`);
  assert.ok(!html.includes('<!--JITHUB_APP-->'));
  assert.ok(!html.includes('GithubCodeToHandoff'));
  assert.ok(!html.includes('RedeemGithubHandoff'));
  assert.ok(!html.includes('<noscript><img'));
  assert.ok(!html.includes('data:image/gif'));
  assert.match(html, /<img[^>]*class="product-frame__image"[^>]*src="\/media\/showcase\/pull-request-conversation-light\.png"/);
});

test('site assets and inert retired callback are publishable', async () => {
  for (const asset of [
    'JitHubLogo.png', 'favicon.png', 'app.css',
    'media/showcase/home-workspace-light.png', 'media/showcase/home-workspace-dark.png',
    'js/theme.js', 'js/media.js', 'auth-retired/index.html'
  ]) assert.ok((await stat(path.join(root, 'dist', asset))).size > 0, `Missing ${asset}`);
  const retired = await readFile(path.join(root, 'dist/auth-retired/index.html'), 'utf8');
  assert.ok(retired.includes('name="referrer" content="no-referrer"'));
  assert.ok(!retired.includes('<script'));
});
