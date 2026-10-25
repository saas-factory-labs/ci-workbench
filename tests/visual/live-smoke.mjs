// Read-only smoke of LIVE mode against a dev instance you started yourself on a loopback port. Prints counts and pass/fail only:
// no repository names, no tokens, no screenshots (live data is private and must not be written to disk).
//   node live-smoke.mjs --base http://127.0.0.1:5193
import { chromium } from 'playwright';

const arg = (k, d) => { const i = process.argv.indexOf('--' + k); return i > 0 ? process.argv[i + 1] : d; };
const base = arg('base', 'http://127.0.0.1:5193');
const browser = await chromium.launch();
const out = [];
const ok = (name, pass, extra = '') => { out.push(pass); console.log(`${pass ? 'PASS' : 'FAIL'} ${name}${extra ? ' ' + extra : ''}`); };

for (const theme of ['dark', 'light']) {
  const ctx = await browser.newContext({ viewport: { width: 1366, height: 768 }, colorScheme: theme === 'dark' ? 'light' : 'dark', reducedMotion: 'reduce' });
  await ctx.addInitScript((t) => { try { localStorage.setItem('ciw:theme', t); } catch { } }, theme);
  const page = await ctx.newPage();
  const errors = [];
  page.on('pageerror', (e) => errors.push(e.message));
  page.on('console', (m) => { if (m.type() === 'error' && !/WebSocket|Normalizing/.test(m.text())) errors.push(m.text().slice(0, 120)); });
  try {
    await page.goto(base + '/settings'); await page.waitForSelector('main h1');
    ok(`[${theme}] live banner (not demo)`, (await page.locator('.live-banner').count()) === 1 && (await page.locator('.demo-banner').count()) === 0);
    await page.waitForTimeout(2500);
    const repos = await page.locator('[data-testid=repo-select] option').count();
    ok(`[${theme}] repositories discovered`, repos > 1, `(${repos - 1})`);
    // First repository that has workflows
    let rows = 0;
    const values = await page.locator('[data-testid=repo-select] option').evaluateAll((os) => os.map((o) => o.value).filter(Boolean));
    for (const v of values.slice(0, 8)) {
      await page.goto(`${base}/repo/${v}`); await page.waitForTimeout(3500);
      rows = await page.locator('.ciw-wf').count();
      if (rows > 0) break;
    }
    ok(`[${theme}] a repository workspace renders workflow rows`, rows > 0, `(${rows} rows)`);
    if (rows > 0) {
      const kpis = await page.locator('.ciw-kpi').count();
      ok(`[${theme}] exactly three KPI cards`, kpis === 3);
      await page.locator('.ciw-wf .ciw-wf-toggle').first().click(); await page.waitForTimeout(1500);
      ok(`[${theme}] quick preview opens`, (await page.locator('[data-testid=quick-preview]').count()) === 1);
      const dots = await page.locator('[data-testid=quick-preview] a.ciw-sdot').count();
      ok(`[${theme}] preview run indicators are links`, dots > 0, `(${dots})`);
      const href = await page.locator('a.ciw-wf-name').first().getAttribute('href');
      await page.goto(new URL(href, base + '/').toString()); await page.waitForSelector('[data-testid=workflow-details] .ciw-dtitle', { timeout: 20000 }); await page.waitForTimeout(2500);
      ok(`[${theme}] workflow details render outcomes`, (await page.locator('[data-testid=workflow-outcomes]').count()) === 1 || (await page.locator('[data-testid=workflow-details] .ciw-empty, .ciw-notice').count()) > 0);
      const runRow = page.locator('tr.ciw-row').first();
      if (await runRow.count()) { await runRow.locator('td').first().click(); await page.waitForSelector('[data-testid=run-diagnostics]', { timeout: 20000 }); ok(`[${theme}] run details open`, true); }
    }
    const t = await page.evaluate(() => document.documentElement.dataset.theme);
    ok(`[${theme}] theme applied`, t === theme);
    ok(`[${theme}] no browser errors`, errors.length === 0, errors.length ? JSON.stringify(errors.slice(0, 2)) : '');
  } catch (e) { ok(`[${theme}] flow`, false, String(e.message).split('\n')[0]); }
  await ctx.close();
}
await browser.close();
process.exitCode = out.every(Boolean) ? 0 : 1;
