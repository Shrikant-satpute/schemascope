// Render scene.html frame-by-frame with Playwright and pipe JPEG frames into ffmpeg.
//
//   node render.mjs full  out/master.mp4          -> whole explainer (window.DURATION s)
//   node render.mjs loop  out/hook-master.mp4     -> hook + reveal loop (0-4 s, then 11-15 s)
//   node render.mjs stills out/frames 1.5 7 12    -> PNG stills at the given seconds (QA)
//   node render.mjs vtt   out/captions.vtt        -> WebVTT from window.CAPTIONS
//
// Env: CHROME (chrome.exe path), FFMPEG (ffmpeg path), FPS (default 30).
import { chromium } from 'playwright-core';
import { spawn } from 'node:child_process';
import { existsSync, mkdirSync, writeFileSync } from 'node:fs';
import { dirname, resolve } from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';

const here = dirname(fileURLToPath(import.meta.url));
const [mode = 'full', out = 'out/master.mp4', ...rest] = process.argv.slice(2);
const FPS = Number(process.env.FPS || 30);
const local = process.env.LOCALAPPDATA || 'C:/Users/Admin/AppData/Local';
const CHROME = process.env.CHROME || ['chromium-1234', 'chromium-1217']
  .map(v => `${local}/ms-playwright/${v}/chrome-win64/chrome.exe`).find(existsSync);
const FFMPEG = process.env.FFMPEG || 'ffmpeg';

const browser = await chromium.launch({ executablePath: CHROME, args: ['--force-color-profile=srgb', '--hide-scrollbars'] });
const page = await browser.newPage({ viewport: { width: 1920, height: 1080 }, deviceScaleFactor: 1 });
await page.goto(pathToFileURL(resolve(here, 'scene.html')).href);
await page.evaluate(() => document.fonts.ready);
const duration = await page.evaluate(() => window.DURATION);
mkdirSync(dirname(resolve(out)), { recursive: true });

const fmt = s => { const h = Math.floor(s / 3600), m = Math.floor(s / 60) % 60, x = (s % 60).toFixed(3).padStart(6, '0');
  return `${String(h).padStart(2, '0')}:${String(m).padStart(2, '0')}:${x}`; };

if (mode === 'vtt') {
  const caps = await page.evaluate(() => window.CAPTIONS);
  const body = caps.map((c, i) => `${i + 1}\n${fmt(c.a)} --> ${fmt(c.b)}\n${c.n ? `Step ${c.n}: ` : ''}${c.text}\n`).join('\n');
  writeFileSync(out, 'WEBVTT\n\n' + body);
} else if (mode === 'stills') {
  mkdirSync(out, { recursive: true });
  for (const s of rest.map(Number)) {
    await page.evaluate(t => window.renderAt(t), s);
    await page.screenshot({ path: `${out}/t${s.toFixed(2).padStart(6, '0')}.png` });
  }
} else {
  // loop = hook (0..4 s) then reveal (11..15 s); both scenes fade from/to the page background, so it loops cleanly.
  const times = [];
  if (mode === 'loop') {
    for (let i = 0; i < 4 * FPS; i++) times.push(i / FPS);
    for (let i = 0; i < 4 * FPS; i++) times.push(11 + i / FPS);
  } else {
    for (let i = 0; i < Math.round(duration * FPS); i++) times.push(i / FPS);
  }
  const ff = spawn(FFMPEG, ['-y', '-hide_banner', '-loglevel', 'error', '-f', 'image2pipe', '-framerate', String(FPS), '-c:v', 'mjpeg', '-i', '-',
    // JPEG frames are full-range BT.601: convert to limited-range BT.709 so every player shows the right colours.
    '-vf', 'scale=in_range=pc:out_range=tv:in_color_matrix=bt601:out_color_matrix=bt709,format=yuv420p',
    '-colorspace', 'bt709', '-color_primaries', 'bt709', '-color_trc', 'bt709', '-color_range', 'tv',
    '-c:v', 'libx264', '-preset', 'slow', '-crf', '10', '-r', String(FPS), out], { stdio: ['pipe', 'inherit', 'inherit'] });
  const done = new Promise((ok, bad) => ff.on('close', c => c === 0 ? ok() : bad(new Error('ffmpeg exit ' + c))));
  for (let i = 0; i < times.length; i++) {
    await page.evaluate(t => window.renderAt(t), times[i]);
    const buf = await page.screenshot({ type: 'jpeg', quality: 95 });
    if (!ff.stdin.write(buf)) await new Promise(r => ff.stdin.once('drain', r));
    if (i % 150 === 0) process.stderr.write(`frame ${i}/${times.length}\n`);
  }
  ff.stdin.end();
  await done;
}
await browser.close();
