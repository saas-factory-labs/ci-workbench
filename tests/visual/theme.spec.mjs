// Browser tests for Light / Dark / System (demo fixtures; run against a demo-mode instance on a port you own).
//   CIW_BASE=http://127.0.0.1:5191 node --test theme.spec.mjs           (CIW_BROWSER=webkit for WebKit)
import test, { before, after } from 'node:test';
import assert from 'node:assert/strict';
import { chromium, webkit } from 'playwright';
import { PNG } from 'pngjs';

const BASE = process.env.CIW_BASE ?? 'http://127.0.0.1:5191';
const engine = process.env.CIW_BROWSER === 'webkit' ? webkit : chromium;
const REPO = 'example-org/platform-monorepo';
const BG = { dark: [0x0e, 0x12, 0x18], light: [0xf7, 0xf9, 0xfc] };

let browser;
before(async () => { browser = await engine.launch(); });
after(async () => { await browser.close(); });

const open = async (colorScheme, viewport = { width: 1366, height: 768 }) => {
  const ctx = await browser.newContext({ viewport, colorScheme, reducedMotion: 'reduce' });
  const page = await ctx.newPage();
  const errors = [];
  page.on('pageerror', (e) => errors.push(e.message));
  page.on('console', (m) => { if (m.type() === 'error') errors.push(m.text()); });
  return { ctx, page, errors };
};
const theme = (page) => page.evaluate(() => ({ theme: document.documentElement.dataset.theme, pref: document.documentElement.dataset.themePref, stored: (() => { try { return localStorage.getItem('ciw:theme'); } catch { return 'blocked'; } })() }));
const ready = async (page, url = '/') => { await page.goto(BASE + url); await page.waitForSelector('.topbar'); await page.locator('[data-testid=theme-menu-button], [data-testid=more-toggle]').locator('visible=true').first().waitFor(); await page.waitForTimeout(500); };
const pick = async (page, key) => { await page.click('[data-testid=theme-menu-button]'); await page.click(`[data-testid=theme-menu] [data-theme-option=${key}]`); await page.waitForTimeout(250); };

test('first visit defaults to System and follows a light OS', async () => {
  const { ctx, page } = await open('light'); await ready(page);
  assert.deepEqual(await theme(page), { theme: 'light', pref: 'system', stored: null });
  await ctx.close();
});

test('System resolves to dark when the OS is dark', async () => {
  const { ctx, page } = await open('dark'); await ready(page);
  assert.deepEqual(await theme(page), { theme: 'dark', pref: 'system', stored: null });
  await ctx.close();
});

test('changing the OS preference updates the UI live while System is selected', async () => {
  const { ctx, page } = await open('light'); await ready(page);
  await page.emulateMedia({ colorScheme: 'dark' }); await page.waitForTimeout(300);
  assert.equal((await theme(page)).theme, 'dark');
  assert.match(await page.locator('[data-testid=theme-menu-button]').getAttribute('aria-label'), /System/);
  await page.emulateMedia({ colorScheme: 'light' }); await page.waitForTimeout(300);
  assert.equal((await theme(page)).theme, 'light');
  await ctx.close();
});

test('explicit Light and Dark override the OS preference and do not follow it', async () => {
  const { ctx, page } = await open('dark'); await ready(page);
  await pick(page, 'light');
  assert.deepEqual(await theme(page), { theme: 'light', pref: 'light', stored: 'light' });
  await page.emulateMedia({ colorScheme: 'light' }); await page.emulateMedia({ colorScheme: 'dark' }); await page.waitForTimeout(250);
  assert.equal((await theme(page)).theme, 'light', 'OS changes must not move an explicit choice');
  await pick(page, 'dark');
  await page.emulateMedia({ colorScheme: 'light' }); await page.waitForTimeout(250);
  assert.deepEqual(await theme(page), { theme: 'dark', pref: 'dark', stored: 'dark' });
  await ctx.close();
});

test('the choice persists across navigation and reload, and the selector reflects it', async () => {
  const { ctx, page } = await open('light'); await ready(page);
  await pick(page, 'dark');
  await page.click('a[href="settings"].iconlink, a.iconlink[href$="settings"]'); await page.waitForSelector('main [data-testid=theme-toggle]');
  assert.equal((await theme(page)).theme, 'dark');
  assert.equal(await page.locator('main [data-testid=theme-toggle] button.on').getAttribute('data-theme-option'), 'dark');
  await page.reload(); await page.waitForSelector('main [data-testid=theme-toggle]'); await page.waitForTimeout(400);
  assert.deepEqual(await theme(page), { theme: 'dark', pref: 'dark', stored: 'dark' });
  assert.equal(await page.locator('main [data-testid=theme-toggle] button.on').getAttribute('data-theme-option'), 'dark');
  // Settings is the second control for the same state
  await page.click('main [data-testid=theme-toggle] [data-theme-option=system]'); await page.waitForTimeout(250);
  assert.deepEqual(await theme(page), { theme: 'light', pref: 'system', stored: 'system' });
  await ctx.close();
});

test('changing theme keeps filters, expanded rows, search text and an open docked inspector', async () => {
  const { ctx, page, errors } = await open('light', { width: 1920, height: 1080 });
  await page.goto(`${BASE}/repo/${REPO}`); await page.waitForSelector('.ciw-wf'); await page.waitForTimeout(800);
  await page.fill('#ciw-global-search', 'scan');
  await page.locator('.ciw-fchip', { hasText: 'Failed' }).first().click();
  const row = page.locator('.ciw-wf', { has: page.locator('.ciw-badge.ciw-fail') }).first();
  await row.locator('.ciw-wf-toggle').click(); await page.waitForSelector('[data-testid=quick-preview]');
  await page.locator('[data-action=quick-inspect]').first().click(); await page.waitForSelector('[data-testid=quick-inspector]');
  const before = { url: page.url(), search: await page.inputValue('#ciw-global-search'), chip: await page.locator('.ciw-fchip.on').first().innerText(), previews: await page.locator('[data-testid=quick-preview]').count() };
  await pick(page, 'dark'); await pick(page, 'light'); await pick(page, 'dark');
  const after = { url: page.url(), search: await page.inputValue('#ciw-global-search'), chip: await page.locator('.ciw-fchip.on').first().innerText(), previews: await page.locator('[data-testid=quick-preview]').count() };
  assert.deepEqual(after, before);
  assert.equal(await page.locator('[data-testid=quick-inspector]').count(), 1, 'the inspector must stay open');
  assert.deepEqual(errors, []);
  await ctx.close();
});

for (const mode of ['dark', 'light']) {
  test(`no wrong-theme flash: the first paint of a ${mode} page already has the ${mode} background`, async () => {
    const { ctx, page } = await open(mode === 'dark' ? 'light' : 'dark');   // OS deliberately opposite, choice is explicit
    await ctx.addInitScript((t) => { try { localStorage.setItem('ciw:theme', t); } catch { } }, mode);
    // Hold the interactive runtime back so only the static shell (the part that could flash) is on screen.
    await page.route(/_framework\/blazor\.web.*\.js/, async (route) => { await new Promise((r) => setTimeout(r, 2500)); await route.continue(); });
    await page.goto(BASE + '/', { waitUntil: 'commit' });
    await page.waitForTimeout(900);
    assert.equal((await theme(page)).theme, mode);
    const png = PNG.sync.read(await page.screenshot());
    const px = (x, y) => { const i = (png.width * y + x) * 4; return [png.data[i], png.data[i + 1], png.data[i + 2]]; };
    for (const [x, y] of [[2, 2], [png.width - 3, png.height - 3], [png.width >> 1, png.height >> 1]]) assert.deepEqual(px(x, y), BG[mode], `pixel ${x},${y}`);
    await ctx.close();
  });
}

test('the theme attribute is set before the body exists (so no CSS ever resolves against the wrong theme)', async () => {
  const { ctx, page } = await open('dark');
  await ctx.addInitScript(() => {
    window.__themeAtBody = null;
    new MutationObserver(() => { if (document.body && window.__themeAtBody === null) window.__themeAtBody = document.documentElement.dataset.theme; }).observe(document, { childList: true, subtree: true });
  });
  await ready(page);
  assert.equal(await page.evaluate(() => window.__themeAtBody), 'dark');
  await ctx.close();
});

test('no browser errors while cycling every theme on several pages', async () => {
  const { ctx, page, errors } = await open('dark');
  for (const url of ['/', '/repositories', `/repo/${REPO}`, '/settings']) {
    await ready(page, url);
    for (const k of ['light', 'dark', 'system']) await pick(page, k);
  }
  assert.deepEqual(errors, []);
  await ctx.close();
});

test('phone: theme is reachable from the more menu without extra topbar buttons', async () => {
  const { ctx, page } = await open('light', { width: 375, height: 667 });
  await page.goto(BASE + '/'); await page.waitForSelector('[data-testid=more-toggle]'); await page.waitForTimeout(500);
  assert.equal(await page.locator('.topbar [data-testid=theme-menu-button]:visible').count(), 0);
  await page.click('[data-testid=more-toggle]');
  const seg = page.locator('.top-actions [data-testid=theme-toggle]');
  await seg.waitFor();
  const box = await seg.locator('button').first().boundingBox();
  assert.ok(box.height >= 44 - 0.5, `touch target ${box.height}`);
  await seg.locator('[data-theme-option=dark]').click(); await page.waitForTimeout(250);
  assert.equal((await theme(page)).theme, 'dark');
  await ctx.close();
});
