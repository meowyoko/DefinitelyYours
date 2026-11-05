import { chromium } from 'playwright';
const [,, html, outdir, n, w, h] = process.argv;
const b = await chromium.launch();
const p = await b.newPage({ viewport: { width: +w, height: +h } });
await p.goto('file://' + html, { waitUntil: 'networkidle' });
await p.evaluate(() => document.fonts.ready);
for (let i = 0; i < +n; i++) {
  await p.evaluate(f => window.setFrame(f), i);
  await p.screenshot({ path: `${outdir}/f${String(i).padStart(3,'0')}.png` });
}
await b.close();
