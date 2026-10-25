// Regenerates the README screenshots (docs/screenshots/dark/*.webp) from the built-in demo provider. No credentials, no GitHub access.
//   cd tests/visual && npm install && npx playwright install chromium   (first time only)
//   node readme-shots.mjs                       builds the web app into a temp folder, starts it on port 5197 in demo mode, captures, stops it
//   node readme-shots.mjs --base http://127.0.0.1:5191   reuse a demo-mode server you already started (it must run with CIWORKBENCH_DEMO_NOW, see below)
//   node readme-shots.mjs --port 5198 --out ../../docs/screenshots/dark --only run-details,job-history
// Determinism: the server is started with CIWORKBENCH_DEMO_NOW=<fixed instant>, so every synthetic timestamp ("4 hours ago") is reproducible.
// Only the process started by this script is stopped. Existing Aspire/Blazor processes and their ports are never touched.
import { chromium } from 'playwright';
import sharp from 'sharp';
import { spawn, spawnSync } from 'node:child_process';
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const here = path.dirname(fileURLToPath(import.meta.url));
const root = path.resolve(here, '..', '..');
const arg = (k, d) => { const i = process.argv.indexOf('--' + k); return i > 0 ? process.argv[i + 1] : d; };
const out = path.resolve(here, arg('out', path.join(root, 'docs', 'screenshots', 'dark')));
const only = arg('only', '') ? new Set(arg('only').split(',')) : null;
const port = Number(arg('port', '5197'));
const NOW = '2026-10-23T14:00:00Z';
const WIDTH = 1600, SCALE = Number(arg('scale', '1.5'));
const MONO = 'example-org/platform-monorepo', ACTOR = 'example-org/review-scraper-actor';

let server = null, base = arg('base', null);
async function startServer() {
  const build = path.join(os.tmpdir(), 'ciw-readme-build');
  console.log('building into', build);
  const b = spawnSync('dotnet', ['build', path.join(root, 'src', 'CIWorkbench.Web'), '-c', 'Release', '-o', build, '--nologo', '-v', 'q'], { stdio: 'inherit' });
  if (b.status !== 0) throw new Error('dotnet build failed');
  base = `http://127.0.0.1:${port}`;
  server = spawn('dotnet', [path.join(build, 'CIWorkbench.Web.dll'), '--urls', base], {
    cwd: build, stdio: 'ignore',
    env: { ...process.env, ASPNETCORE_ENVIRONMENT: 'Development', CIWORKBENCH_MODE: 'demo', CIWORKBENCH_DEMO_NOW: NOW },
  });
  for (let i = 0; i < 60; i++) {
    try { if ((await fetch(base + '/')).ok) return; } catch { /* not up yet */ }
    await new Promise((r) => setTimeout(r, 500));
  }
  throw new Error('server did not start on ' + base);
}

const prep = async (page) => {
  await page.evaluate(async () => { await document.fonts.ready; await Promise.all([...document.images].map((i) => i.complete ? 0 : new Promise((r) => { i.onload = i.onerror = r; }))); });
  await page.waitForTimeout(500);
};

async function main() {
  if (!base) await startServer();
  fs.mkdirSync(out, { recursive: true });
  const browser = await chromium.launch();
  const ctx = await browser.newContext({ viewport: { width: WIDTH, height: 2600 }, deviceScaleFactor: SCALE, reducedMotion: 'reduce', colorScheme: 'light' });
  // The explicit Dark choice must win over the (deliberately opposite) OS preference.
  await ctx.addInitScript(() => { try { localStorage.setItem('ciw:theme', 'dark'); localStorage.setItem('ciw:density', 'comfortable'); } catch { /* storage unavailable */ } });
  const page = await ctx.newPage();
  const errors = [];
  page.on('pageerror', (e) => errors.push(e.message));
  const open = async (url, ready) => { await page.goto(base + url); await page.waitForSelector(ready, { timeout: 30000 }); await page.waitForTimeout(1200); };
  const group = (name) => page.locator('.ciw-group', { has: page.locator('.ciw-group-name', { hasText: name }) }).first();
  // Group open/closed state is remembered between pages, so every shot sets it explicitly.
  const setGroup = async (name, wantOpen) => { const g = group(name); if ((await g.getAttribute('class')).includes('open') !== wantOpen) await g.locator('.ciw-group-toggle').first().click(); };
  const ensureOpen = (name) => setGroup(name, true);
  const row = (name) => page.locator('.ciw-wf', { hasText: name }).first();
  const expandRow = async (name) => { await row(name).locator('.ciw-wf-toggle').click(); await page.waitForSelector('[data-testid=quick-preview]'); await page.waitForTimeout(900); };

  // Capture the page from the top down to the bottom of `lastLocator` (content-bounded crop, never cuts content).
  const shot = async (name, lastLocator, { pad = 28, minHeight = 0 } = {}) => {
    await prep(page);
    // `main` stretches to the viewport, so for page-level shots measure the lowest rendered leaf element instead.
    const bottom = lastLocator ? (await lastLocator.boundingBox()).y + (await lastLocator.boundingBox()).height
      : await page.evaluate(() => Math.max(...[...document.querySelectorAll('main *')].filter((e) => !e.children.length && e.getBoundingClientRect().height > 0).map((e) => e.getBoundingClientRect().bottom + scrollY)));
    const height = Math.max(minHeight, Math.ceil(bottom + pad));
    const tmp = path.join(os.tmpdir(), `ciw-${name}.png`);
    await page.screenshot({ path: tmp, animations: 'disabled', caret: 'hide', fullPage: true, clip: { x: 0, y: 0, width: WIDTH, height } });
    const file = path.join(out, `${name}.webp`);
    await sharp(tmp).webp({ quality: 88, effort: 6, smartSubsample: false }).toFile(file);
    fs.unlinkSync(tmp);
    console.log(`${name}.webp ${(fs.statSync(file).size / 1024).toFixed(0)} KB`);
  };
  const wanted = (n) => !only || only.has(n);

  if (wanted('overview')) {
    await open('/overview', '[data-testid=needs-attention] .ov-toggle');
    await page.waitForFunction(() => !document.querySelector('[data-testid=ov-scan] progress'), null, { timeout: 30000 });
    await page.locator('[data-testid=needs-attention] .ov-toggle').first().click();
    await page.waitForSelector('[data-testid=ov-detail]');
    await page.waitForTimeout(900);
    await shot('overview', page.locator('[data-testid=repo-health]'), { pad: 12 });
  }
  if (wanted('repository-workspace')) {
    await open(`/repo/${MONO}`, '.ciw-wf');
    await ensureOpen('Build & Test'); await ensureOpen('Security');
    await page.waitForTimeout(400);
    await shot('repository-workspace', group('Security'), { pad: 12 });
  }
  if (wanted('workflow-quick-preview')) {
    await open(`/repo/${MONO}`, '.ciw-wf');
    await setGroup('Build & Test', false); await ensureOpen('Security');
    await expandRow('Container image scan');
    await expandRow('CodeQL analysis');
    await shot('workflow-quick-preview', group('Security'), { pad: 12 });
  }
  const WF = `/repo/${ACTOR}/workflows/2001`;
  if (wanted('workflow-details')) {
    await open(WF, '[data-testid=workflow-details] .ciw-dtitle');
    await shot('workflow-details', null);
  }
  if (wanted('run-details')) {
    await open(`/repo/${ACTOR}/runs/2000000`, '[data-testid=run-details]');
    await shot('run-details', null);
  }
  if (wanted('failure-series')) {
    await open(`${WF}?tab=failures`, '[data-testid=workflow-details] .ciw-dtitle');
    await shot('failure-series', null);
  }
  if (wanted('job-history')) {
    await open(`${WF}?tab=jobs`, '[data-testid=workflow-details] .ciw-dtitle');
    await shot('job-history', null);
  }
  await browser.close();
  if (errors.length) console.log('page errors:', errors.slice(0, 5));
}

try { await main(); } finally { if (server) server.kill(); }
