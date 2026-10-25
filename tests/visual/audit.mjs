// Accessibility + layout audit of every major page state, in both themes (demo fixtures only).
//   node audit.mjs --base http://127.0.0.1:5191 [--browser chromium|webkit] [--only 375x667,1366x768] [--shots dir]
// axe-core (WCAG 2.0/2.1/2.2 A + AA rules, including colour-contrast) runs on each state; also asserts no page-level horizontal overflow.
import { chromium, webkit } from 'playwright';
import AxeBuilder from '@axe-core/playwright';
import fs from 'node:fs';
import path from 'node:path';

const arg = (k, d) => { const i = process.argv.indexOf('--' + k); return i > 0 ? process.argv[i + 1] : d; };
const base = arg('base', 'http://127.0.0.1:5191');
const engine = arg('browser', 'chromium') === 'webkit' ? webkit : chromium;
const REPO = 'example-org/platform-monorepo';
const sizes = arg('only', '375x667,1366x768').split(',').map((s) => s.split('x').map(Number));
const shots = arg('shots', '');
if (shots) fs.mkdirSync(shots, { recursive: true });
const TAGS = ['wcag2a', 'wcag2aa', 'wcag21a', 'wcag21aa', 'wcag22aa'];

const browser = await engine.launch();
const findings = []; let states = 0;

for (const theme of ['dark', 'light']) {
  for (const [w, h] of sizes) {
    const ctx = await browser.newContext({ viewport: { width: w, height: h }, hasTouch: w < 800, colorScheme: theme === 'dark' ? 'light' : 'dark', reducedMotion: 'reduce' });
    await ctx.addInitScript((t) => { try { localStorage.setItem('ciw:theme', t); } catch { } }, theme);
    const page = await ctx.newPage();
    const tag = `${theme}-${w}x${h}`;
    const check = async (name) => {
      states++;
      await page.waitForTimeout(500);
      const r = await new AxeBuilder({ page }).withTags(TAGS).analyze();
      const overflow = await page.evaluate(() => document.documentElement.scrollWidth - window.innerWidth);
      if (overflow > 0) findings.push({ tag, name, rule: 'page-horizontal-overflow', detail: `${overflow}px` });
      for (const v of r.violations) for (const n of v.nodes.slice(0, 4)) findings.push({ tag, name, rule: v.id, impact: v.impact, target: n.target.join(' '), detail: (n.any[0]?.message || n.failureSummary || '').split('\n')[0].slice(0, 160) });
      if (shots) await page.screenshot({ path: path.join(shots, `${name}.${tag}.png`) });
    };
    const go = async (url, ready) => { await page.goto(base + url); await page.waitForSelector(ready, { timeout: 20000 }); await page.waitForTimeout(700); };
    const step = async (name, fn) => { try { await fn(); await check(name); } catch (e) { findings.push({ tag, name, rule: 'STEP-FAILED', detail: String(e.message).split('\n')[0] }); } };

    await step('overview', () => go('/', '.ciw-root h1'));
    await step('repositories', () => go('/repositories', '.repo-table, .repo-cards, table'));
    await step('settings', () => go('/settings', 'main [data-testid=theme-toggle]'));
    await step('not-found', () => go('/does-not-exist', 'main'));
    await step('theme-menu', async () => { await go('/', '.topbar'); if (w > 760) { await page.click('[data-testid=theme-menu-button]'); await page.waitForSelector('[data-testid=theme-menu]'); } else { await page.click('[data-testid=more-toggle]'); } });
    await step('navigation', async () => { await go('/', '.topbar'); if (w <= 760) await page.click('[data-testid=sidebar-toggle]'); });
    await step('workspace', () => go(`/repo/${REPO}`, '.ciw-wf'));
    const row = page.locator('.ciw-wf', { has: page.locator('.ciw-badge.ciw-fail') }).first();
    await step('preview', async () => { await row.locator('.ciw-wf-toggle').click(); await page.waitForSelector('[data-testid=quick-preview]'); await page.waitForTimeout(1200); });
    await step('inspector', async () => { await page.locator('[data-action=quick-inspect]').first().click(); await page.waitForSelector('[data-testid=quick-inspector]'); await page.waitForTimeout(1200); });
    let wf = null;
    try { wf = new URL(await row.locator('a.ciw-wf-name').first().getAttribute('href'), base + '/').pathname; } catch { }
    if (wf) {
      await step('workflow-runs', () => go(wf, '[data-testid=workflow-details] .ciw-dtitle'));
      await step('workflow-failures', async () => { await go(wf + '?tab=failures', '[data-testid=workflow-details] .ciw-dtitle'); await page.waitForTimeout(1200); });
      await step('workflow-jobs', async () => { await go(wf + '?tab=jobs', '[data-testid=workflow-details] .ciw-dtitle'); await page.waitForTimeout(1500); });
      await step('run-summary', async () => { await go(wf, '.ciw-row'); await page.locator('tr.ciw-row', { has: page.locator('.ciw-badge.ciw-fail') }).first().locator('td').first().click(); await page.waitForSelector('[data-testid=run-diagnostics]'); await page.waitForTimeout(1500); });
      await step('run-logs', async () => { await page.locator('[data-tab=Logs], [data-tab=logs]').first().click(); await page.waitForTimeout(1500); });
    }
    await ctx.close();
  }
}
await browser.close();

const byRule = {};
for (const f of findings) (byRule[f.rule] ??= []).push(f);
console.log(`${states} states checked, ${findings.length} findings`);
for (const [rule, list] of Object.entries(byRule)) {
  console.log(`\n## ${rule} (${list.length})`);
  const seen = new Set();
  for (const f of list) { const k = `${f.tag}|${f.name}|${f.target}`; if (seen.has(k)) continue; seen.add(k); if (seen.size > 14) break; console.log(`  ${f.tag} ${f.name} ${f.target ?? ''} ${f.detail ?? ''}`); }
}
fs.writeFileSync(arg('json', 'audit-results.json'), JSON.stringify(findings, null, 1));
process.exitCode = findings.length ? 1 : 0;
