// Screenshot matrix for CIWorkbench (demo fixtures only: deterministic, no GitHub access).
//   node shoot.mjs --base http://127.0.0.1:5191 --out ../../artifacts/visual/after [--themes dark,light] [--browser chromium|webkit] [--only 375x667,1366x768]
// The app is expected to be running in demo mode (CIWORKBENCH_MODE=demo). Nothing here updates an approved baseline:
// output goes to the directory you name, and comparing two directories is a separate, explicit step (compare.mjs).
import { chromium, webkit } from 'playwright';
import fs from 'node:fs';
import path from 'node:path';

const arg = (k, d) => { const i = process.argv.indexOf('--' + k); return i > 0 ? process.argv[i + 1] : d; };
const base = arg('base', 'http://127.0.0.1:5191');
const out = path.resolve(arg('out', 'out'));
const themes = arg('themes', 'dark,light').split(',');
const engine = arg('browser', 'chromium') === 'webkit' ? webkit : chromium;
const density = arg('density', 'comfortable');
const REPO = arg('repo', 'example-org/platform-monorepo');
const ALL = ['320x568', '375x667', '430x932', '440x956', '768x1024', '1366x768', '1536x864', '1920x1080', '3440x1440'];
const sizes = (arg('only', '') ? arg('only').split(',') : ALL).map((s) => s.split('x').map(Number));

// Relative times ("12 min ago") and clock stamps drift between runs. They are rewritten to a fixed token just before each capture,
// so the rest of the text (which is deterministic demo data) stays visible in the picture.
const stabilize = () => {
  const re = /(just now|(?:\d+|an?)\s?(?:s|sec|secs|seconds?|m|min|mins|minutes?|h|hrs?|hours?|d|days?|w|weeks?|mo|months?)(?:\s+ago)?)/gi;
  const clock = /\d{4}-\d{2}-\d{2}[ T]\d{2}:\d{2}(?::\d{2})?(?:\s?(?:UTC|Z))?|\d{1,2}:\d{2}:\d{2}/g;
  const w = document.createTreeWalker(document.body, NodeFilter.SHOW_TEXT);
  for (let n = w.nextNode(); n; n = w.nextNode()) {
    if (n.parentElement?.closest('script,style')) continue;
    const t = n.nodeValue;
    const u = t.replace(/(\d+|an?)\s?(?:min|mins|minutes?|hours?|hrs?|days?|weeks?|months?)\s+ago|just now/gi, 'some time ago').replace(clock, 'DATE');
    if (u !== t) n.nodeValue = u;
  }
  document.querySelectorAll('[data-tip]').forEach((e) => e.setAttribute('data-tip', e.getAttribute('data-tip').replace(/\d+\s?(min|hours?|days?|weeks?|months?)\s+ago|just now/gi, 'some time ago')));
};

fs.mkdirSync(out, { recursive: true });
const results = [];
const browser = await engine.launch();

for (const theme of themes) {
  for (const [w, h] of sizes) {
    const ctx = await browser.newContext({ viewport: { width: w, height: h }, deviceScaleFactor: 1, hasTouch: w < 800, isMobile: w < 800 && engine === chromium, colorScheme: theme === 'dark' ? 'light' : 'dark', reducedMotion: 'reduce' });
    // The explicit choice must override the (deliberately opposite) OS preference.
    await ctx.addInitScript(([t, d]) => { try { localStorage.setItem('ciw:theme', t); localStorage.setItem('ciw:density', d); } catch { } }, [theme, density]);
    const page = await ctx.newPage();
    const errors = [];
    page.on('pageerror', (e) => errors.push(e.message));
    page.on('console', (m) => { if (m.type() === 'error') errors.push(m.text()); });
    const tag = `${theme}-${w}x${h}`;
    const shot = async (name, opts = {}) => {
      await page.waitForTimeout(350);
      await page.evaluate(stabilize);
      const file = path.join(out, `${name}.${tag}.png`);
      await page.screenshot({ path: file, animations: 'disabled', caret: 'hide', ...opts });
      const overflow = await page.evaluate(() => document.documentElement.scrollWidth - window.innerWidth);
      results.push({ name, tag, overflow, file: path.basename(file) });
    };
    const step = async (name, fn) => { try { await fn(); } catch (e) { results.push({ name, tag, error: String(e.message).split('\n')[0] }); console.log('FAIL', name, tag, String(e.message).split('\n')[0]); } };
    const phone = w <= 760;

    // 6. global navigation (drawer on a phone, theme menu on larger screens)
    await step('navigation', async () => {
      await page.goto(`${base}/`); await page.waitForSelector('.topbar');
      await page.waitForTimeout(600);
      if (phone) { await page.click('[data-testid=sidebar-toggle]'); await page.waitForTimeout(300); }
      else if (await page.locator('[data-testid=theme-menu-button]').count()) { await page.click('[data-testid=theme-menu-button]'); await page.waitForSelector('[data-testid=theme-menu]'); }
      await shot('navigation');
      if (phone) await page.click('[data-testid=scrim]').catch(() => { });
    });

    // 1. repository workspace
    await step('workspace', async () => {
      await page.goto(`${base}/repo/${REPO}`); await page.waitForSelector('.ciw-wf', { timeout: 20000 });
      await page.waitForTimeout(900);
      await shot('workspace');
    });

    // 2. quick preview, expanded on the first failing workflow
    let failingRow = page.locator('.ciw-wf', { has: page.locator('.ciw-badge.ciw-fail') }).first();
    await step('preview', async () => {
      await failingRow.locator('.ciw-wf-toggle').click();
      await page.waitForSelector('[data-testid=quick-preview]'); await page.waitForTimeout(1200);
      await failingRow.scrollIntoViewIfNeeded();
      await shot('preview');
    });

    // 5. run inspector (docked, drawer or sheet depending on width)
    await step('inspector', async () => {
      await page.locator('[data-action=quick-inspect]').first().click();
      await page.waitForSelector('[data-testid=quick-inspector]'); await page.waitForTimeout(1200);
      await shot('inspector');
      const close = page.locator('[data-testid=inspector-close]'); if (await close.count()) await close.first().click();
      await page.waitForTimeout(250);
    });

    // 3. workflow details
    let wfUrl = null;
    await step('workflow', async () => {
      const href = await failingRow.locator('a.ciw-wf-name').first().getAttribute('href');
      wfUrl = new URL(href, `${base}/`).toString();
      await page.goto(wfUrl); await page.waitForSelector('[data-testid=workflow-details] .ciw-dtitle'); await page.waitForTimeout(1500);
      await shot('workflow');
    });

    // 4. run details
    await step('run', async () => {
      await page.goto(wfUrl); await page.waitForSelector('.ciw-row'); await page.waitForTimeout(800);
      const row = page.locator('tr.ciw-row', { has: page.locator('.ciw-badge.ciw-fail') }).first();
      await row.locator('a, td').first().click();
      await page.waitForSelector('[data-testid=run-diagnostics]'); await page.waitForTimeout(1800);
      await shot('run');
    });

    // 7. settings with the theme control
    await step('settings', async () => {
      await page.goto(`${base}/settings`); await page.waitForSelector('main h1'); await page.waitForTimeout(800);
      await shot('settings');
    });

    results.push({ tag, consoleErrors: errors.filter((e) => !/WebSocket|Normalizing|Failed to load resource.*(favicon)/.test(e)).slice(0, 5) });
    await ctx.close();
  }
}
await browser.close();
fs.writeFileSync(path.join(out, 'results.json'), JSON.stringify(results, null, 1));
const bad = results.filter((r) => r.error || (r.overflow ?? 0) > 0 || r.consoleErrors?.length);
console.log(`${results.length} entries, ${bad.length} with problems`);
for (const b of bad) console.log(JSON.stringify(b));
