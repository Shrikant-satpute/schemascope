// Screenshot an HTML/SVG file with headless Chromium.
//   node shoot.mjs <in.html|svg> <out.png> <width> <height> [scale] [hash]
import { createRequire } from 'node:module';
import { existsSync } from 'node:fs';
import { resolve } from 'node:path';
import { pathToFileURL } from 'node:url';
const require = createRequire(resolve(import.meta.dirname, '../package.json'));
const { chromium } = require('playwright-core');

const [inp, out, w = '1280', h = '720', scale = '1', hash = ''] = process.argv.slice(2);
const local = process.env.LOCALAPPDATA || 'C:/Users/Admin/AppData/Local';
const CHROME = ['chromium-1234', 'chromium-1217'].map(v => `${local}/ms-playwright/${v}/chrome-win64/chrome.exe`).find(existsSync);
const browser = await chromium.launch({ executablePath: CHROME, args: ['--force-color-profile=srgb', '--hide-scrollbars'] });
const page = await browser.newPage({ viewport: { width: +w, height: +h }, deviceScaleFactor: +scale });
await page.goto(pathToFileURL(resolve(inp)).href + (hash ? '#' + hash : ''));
await page.evaluate(() => document.fonts.ready);
await page.waitForTimeout(150);
await page.screenshot({ path: out });
await browser.close();
console.log('shot', out);
