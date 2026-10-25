// Builds a side-by-side review page from two screenshot directories produced by shoot.mjs. It only reads; nothing is overwritten.
//   node compare.mjs --before ../../artifacts/visual/before --after ../../artifacts/visual/after --out ../../artifacts/visual/compare.html
import fs from 'node:fs';
import path from 'node:path';

const arg = (k, d) => { const i = process.argv.indexOf('--' + k); return i > 0 ? process.argv[i + 1] : d; };
const before = path.resolve(arg('before', '../../artifacts/visual/before'));
const after = path.resolve(arg('after', '../../artifacts/visual/after'));
const out = path.resolve(arg('out', '../../artifacts/visual/compare.html'));
const rel = (f) => path.relative(path.dirname(out), f).split(path.sep).join('/');
const names = ['workspace', 'preview', 'workflow', 'run', 'inspector', 'navigation', 'settings'];
const sizes = ['320x568', '375x667', '430x932', '440x956', '768x1024', '1366x768', '1536x864', '1920x1080', '3440x1440'];
const has = (d, n, t, s) => fs.existsSync(path.join(d, `${n}.${t}-${s}.png`));

let html = `<!doctype html><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1"><title>CIWorkbench before / after</title>
<style>body{font:14px system-ui;margin:0;background:#111;color:#eee}header{position:sticky;top:0;background:#000;padding:10px 16px;z-index:2}h2{margin:28px 16px 6px}h3{margin:14px 16px 4px;font-weight:500;color:#9ab}
.row{display:flex;gap:12px;padding:0 16px;overflow-x:auto;align-items:flex-start}figure{margin:0;flex:none}figcaption{font-size:12px;color:#9ab;margin:2px 0}img{display:block;max-height:560px;max-width:none;border:1px solid #333}</style>
<header>Before = redesign-1 (Dark only, it has no Light theme) &middot; After = feat/design-system-v2 (Dark and Light). Demo fixtures, relative times normalised.</header>`;
for (const n of names) {
  html += `<h2>${n}</h2>`;
  for (const s of sizes) {
    const cells = [['before dark', before, 'dark'], ['after dark', after, 'dark'], ['after light', after, 'light']].filter(([, d, t]) => has(d, n, t, s));
    if (!cells.length) continue;
    html += `<h3>${s}</h3><div class="row">` + cells.map(([label, d, t]) => `<figure><figcaption>${label}</figcaption><img loading="lazy" src="${rel(path.join(d, `${n}.${t}-${s}.png`))}" alt="${n} ${label} ${s}"></figure>`).join('') + '</div>';
  }
}
fs.writeFileSync(out, html);
console.log('wrote', out);
