# SchemaScope explainer video: source

`scene.html` is a single self-contained 1920x1080 motion-graphics scene. Every frame
is a pure function of time: `window.renderAt(seconds)` draws the frame, with no CSS
transitions or timers. It uses the system fonts Segoe UI and Cascadia Mono, so it
renders the same offline on Windows. `render.mjs` steps through the timeline at 30 fps
with Playwright and pipes the frames into ffmpeg.

The outputs are published in `site/media/`:

| File | What it is |
|---|---|
| `schemascope-explainer.mp4` / `.webm` | The full 55 s explainer, 1920x1080 |
| `schemascope-hook.mp4` / `.webm` | An 8 s muted hero loop (hook, then reveal), 1280x720 |
| `schemascope-explainer-poster.jpg`, `schemascope-hook-poster.jpg` | Poster frames |
| `schemascope-explainer.vtt` | Captions, generated from `window.CAPTIONS` in the scene |

## Re-render

You need Node 18+, a Chromium build (the Playwright download under
`%LOCALAPPDATA%\ms-playwright\chromium-*` is found on its own, or set `CHROME`), and
ffmpeg with libx264 and libvpx-vp9 (set `FFMPEG` if it is not on PATH).

```bash
cd assets/video
npm init -y && npm i playwright-core
export FFMPEG=/path/to/ffmpeg

node render.mjs stills qa 3 9 14 26 33 39 43 53   # PNG stills, for checking by eye
node render.mjs full  out/master.mp4               # near-lossless master, 55 s
node render.mjs loop  out/hook-master.mp4          # 0-4 s plus 11-15 s
node render.mjs vtt   out/captions.vtt

# Delivery encodes (the settings used for site/media; two-pass so sizes stay under budget)
M=../../site/media; Q="-y -hide_banner -loglevel error"
ffmpeg $Q -i out/master.mp4 -c:v libx264 -preset slower -b:v 1100k -pass 1 -passlogfile x264 -pix_fmt yuv420p -an -f null NUL
ffmpeg $Q -i out/master.mp4 -c:v libx264 -preset slower -b:v 1100k -pass 2 -passlogfile x264 -pix_fmt yuv420p -profile:v high -movflags +faststart -an $M/schemascope-explainer.mp4
ffmpeg $Q -i out/master.mp4 -c:v libvpx-vp9 -b:v 780k -pass 1 -passlogfile vp9 -row-mt 1 -cpu-used 4 -an -f null NUL
ffmpeg $Q -i out/master.mp4 -c:v libvpx-vp9 -b:v 780k -pass 2 -passlogfile vp9 -row-mt 1 -cpu-used 2 -an $M/schemascope-explainer.webm
ffmpeg $Q -i out/hook-master.mp4 -vf scale=1280:720:flags=lanczos -c:v libx264 -preset slower -b:v 1300k -pass 1 -passlogfile hk -pix_fmt yuv420p -an -f null NUL
ffmpeg $Q -i out/hook-master.mp4 -vf scale=1280:720:flags=lanczos -c:v libx264 -preset slower -b:v 1300k -pass 2 -passlogfile hk -pix_fmt yuv420p -movflags +faststart -an $M/schemascope-hook.mp4
ffmpeg $Q -i out/hook-master.mp4 -vf scale=1280:720:flags=lanczos -c:v libvpx-vp9 -b:v 1000k -pass 1 -passlogfile hkv -row-mt 1 -an -f null NUL
ffmpeg $Q -i out/hook-master.mp4 -vf scale=1280:720:flags=lanczos -c:v libvpx-vp9 -b:v 1000k -pass 2 -passlogfile hkv -row-mt 1 -cpu-used 1 -an $M/schemascope-hook.webm
node render.mjs stills posters 13.9 3.2
ffmpeg $Q -i posters/t013.90.png -q:v 3 $M/schemascope-explainer-poster.jpg
ffmpeg $Q -i posters/t003.20.png -vf scale=1280:720:flags=lanczos -q:v 3 $M/schemascope-hook-poster.jpg
cp out/captions.vtt $M/schemascope-explainer.vtt
```

To change copy or timing, edit `CAPTIONS`, `CAM` (camera keyframes) and `CUR` (cursor
keyframes) in `scene.html`, or the per-scene blocks in `renderAt`. Then run `stills`
to check before you do a full render. The UI numbers (1,189 objects, 806 ms, 89.1%
and so on) come from the README benchmark and the site screenshots. Keep them honest.
