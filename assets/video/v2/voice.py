#!/usr/bin/env python3
"""SchemaScope explainer voiceover, generated with Kokoro (Apache-2.0) via kokoro-onnx.

Each line is rendered on its own, trimmed, and placed at a fixed time on the 71.5 s
timeline so it lands on its scene. If a line would overrun its window it is
re-rendered slightly faster (never above MAX_SPEED).

  python voice.py <voice> <out.wav> <plan.json>
  python voice.py captions <plan.json> <out.vtt>  # captions of what is spoken
  python voice.py samples <dir>          # the hook line in several male voices

Output is 48 kHz mono float, the format sound.py mixes.

Spelling is phonetic on purpose: "Q A" (Kokoro reads "QA" as "kah"),
"Re indented" (no hyphen), "One point two", the URL spelled out word by word.
The 8 s hero loop is silent, so there is no hook take in v2.
"""
import sys, json, subprocess, tempfile, os
import numpy as np, soundfile as sf
from kokoro_onnx import Kokoro

HERE = os.path.dirname(os.path.abspath(__file__))
MODEL = os.path.join(HERE, '..', 'kokoro-v1.0.onnx')
VOICES = os.path.join(HERE, '..', 'voices-v1.0.bin')
BASE_SPEED, MAX_SPEED = 0.96, 1.12
MAX_TEMPO = 1.08    # last resort, pitch-preserving (ffmpeg atempo)
FFMPEG = os.environ.get('FFMPEG', 'ffmpeg')
DUR = 71.5

# (start, must end by, text, caption) - one line per scene of script.json, starting
# 0.2 s into its scene and done 0.2 s before the next one.
LINES = [
    (0.2, 3.6, "Eight client databases. Plus Q A. One compare.",
     "Eight client databases. Plus QA. One compare."),
    (4.0, 9.6, "The old way: one pair at a time. Then eight more.", None),
    (11.0, 17.8, "SchemaScope takes all nine targets in one go, and reads every one at the same time.", None),
    (18.2, 23.3, "Over a thousand objects in each of ten databases, in under a second.", None),
    # (1,070 to 1,219 objects per database in the measured run; 1,195 in the source)
    (23.7, 27.8, "Your eight client databases. One compare. About one second.", None),
    (28.2, 35.0, "Every client gets a score, worst first. Re indented code is formatting, not a false alarm.",
     "Every client gets a score, worst first. Re-indented code is formatting, not a false alarm."),
    (35.4, 41.6, "See the exact lines, then create scripts that make it match, checked before they run.", None),
    (42.0, 47.6, "One point two adds write mode. Off at every launch, until you confirm.",
     "1.2 adds Write mode. Off at every launch, until you confirm."),
    (48.0, 53.1, "Type the database name to apply. Protected servers are refused, scripts only.", None),
    (53.5, 59.1, "Each file runs in its own transaction. One error rolls that file back.", None),
    (59.5, 64.6, "Then it compares again, and the client hits one hundred percent.",
     "Then it compares again, and the client hits 100%."),
    (65.0, 71.3, "Stop comparing in pairs. Get SchemaScope free, at schema scope dot shadow mark dot in.",
     "Stop comparing in pairs. Get SchemaScope free, at schemascope.shadowmark.in"),
]

# No split lines in v2 (kept so say()/captions() stay as in v1).
SPLIT = {}


def trim(x, sr, thr_db=-45, pad=0.03):
    env = np.abs(x)
    thr = np.max(env) * 10 ** (thr_db / 20)
    idx = np.where(env > thr)[0]
    if not len(idx):
        return x
    a = max(0, idx[0] - int(pad * sr)); b = min(len(x), idx[-1] + int(pad * 2 * sr))
    return x[a:b]


def tighten(x, sr, max_pause=0.24, thr_db=-38):
    """Shorten long pauses between phrases (Kokoro leaves generous ones) without touching words."""
    hop = int(0.01 * sr)
    n = len(x) // hop
    rms = np.sqrt(np.mean(x[:n * hop].reshape(n, hop) ** 2, axis=1) + 1e-12)
    quiet = 20 * np.log10(rms / rms.max()) < thr_db
    out, i, keep = [], 0, int(max_pause / 0.01)
    fade = np.linspace(0, 1, hop)
    while i < n:
        j = i
        while j < n and quiet[j] == quiet[i]:
            j += 1
        seg = x[i * hop:j * hop]
        if quiet[i] and (j - i) > keep and i > 0 and j < n:
            h = keep // 2
            a, b = seg[:h * hop].copy(), seg[-(keep - h) * hop:].copy()
            a[-hop:] *= fade[::-1]; b[:hop] *= fade
            seg = np.concatenate([a, b])
        out.append(seg); i = j
    return np.concatenate(out + [x[n * hop:]])


def split_benefits(x, sr):
    """"Fast. Free. Read-only." -> three clips. Kokoro leaves a real pause only before
    "Read-only", so cut there, then cut "Fast" from "Free" at the quietest 10 ms between."""
    hop = int(0.01 * sr); n = len(x) // hop
    rms = 20 * np.log10(np.sqrt(np.mean(x[:n * hop].reshape(n, hop) ** 2, axis=1) + 1e-12))
    quiet = rms - rms.max() < -38
    runs, i = [], 0
    while i < n:
        j = i
        while j < n and quiet[j] == quiet[i]:
            j += 1
        if quiet[i] and i > 0 and j < n:
            runs.append((j - i, i, j))
        i = j
    _, a, b = max(runs)
    c2 = (a + b) // 2
    lo, hi = int(0.3 * a), int(0.75 * a)
    c1 = lo + int(np.argmin(rms[lo:hi]))
    f = int(0.012 * sr); out = []
    for p, q in ((0, c1 * hop), (c1 * hop, c2 * hop), (c2 * hop, len(x))):
        y = x[p:q].copy(); y[:f] *= np.linspace(0, 1, f); y[-f:] *= np.linspace(1, 0, f)
        out.append(trim(y, sr))
    return out


def say(k, text, voice, speed):
    if text in SPLIT:
        phrase, i = SPLIT[text]
        x, sr = k.create(phrase, voice=voice, speed=speed, lang='en-us')
        return split_benefits(trim(np.asarray(x, np.float32), sr), sr)[i], sr
    x, sr = k.create(text, voice=voice, speed=speed, lang='en-us')
    return tighten(trim(np.asarray(x, np.float32), sr), sr), sr


def atempo(x, sr, r):
    with tempfile.TemporaryDirectory() as d:
        a, b = os.path.join(d, 'a.wav'), os.path.join(d, 'b.wav')
        sf.write(a, x, sr, subtype='FLOAT')
        subprocess.run([FFMPEG, '-v', 'error', '-y', '-i', a, '-af', f'atempo={r:.4f}', '-c:a', 'pcm_f32le', b], check=True)
        y, _ = sf.read(b, dtype='float32')
    return y


def write48(path, x, sr):
    """Kokoro speaks at 24 kHz; the soundtrack is 48 kHz."""
    with tempfile.TemporaryDirectory() as d:
        a = os.path.join(d, 'a.wav')
        sf.write(a, x, sr, subtype='FLOAT')
        subprocess.run([FFMPEG, '-v', 'error', '-y', '-i', a, '-af', 'aresample=48000:resampler=soxr', '-c:a', 'pcm_f32le', path], check=True)


def captions(plan_path, vtt_path, linger=0.4):
    """WebVTT of what is actually said, timed from plan.json. Sentences of one line share
    its time by length; the one-word benefit lines are joined back into one cue."""
    cues = []
    for p in json.load(open(plan_path)):
        if p['text'] in SPLIT and cues and cues[-1][3]:
            cues[-1][1] = p['end']; cues[-1][2] += ' ' + p['caption']
            continue
        parts = [s + '.' if not s.endswith(('.', '?', '!', ',')) else s
                 for s in p['caption'].replace('? ', '?\n').replace('. ', '.\n').split('\n')]
        t, total = p['start'], sum(len(s) for s in parts)
        for s in parts:
            d = (p['end'] - p['start']) * len(s) / total
            cues.append([t, t + d, s, p['text'] in SPLIT]); t += d
    def ts(x):
        return f"{int(x // 3600):02d}:{int(x % 3600 // 60):02d}:{x % 60:06.3f}"
    out = ['WEBVTT', '']
    for i, (a, b, s, _) in enumerate(cues):
        nxt = cues[i + 1][0] if i + 1 < len(cues) else DUR
        out += [str(i + 1), f"{ts(a)} --> {ts(min(b + linger, nxt, DUR))}", s, '']
    open(vtt_path, 'w', encoding='utf-8', newline='\n').write('\n'.join(out))


def main():
    if sys.argv[1] == 'captions':
        captions(*sys.argv[2:4])
        return
    k = Kokoro(MODEL, VOICES)
    if sys.argv[1] == 'samples':
        out = sys.argv[2]
        for v in ('am_michael', 'am_fenrir', 'am_puck', 'am_onyx', 'am_adam', 'bm_george', 'bm_lewis'):
            x, sr = say(k, "Eight client databases. Plus Q A. One compare. Get SchemaScope free.", v, BASE_SPEED)
            sf.write(f'{out}/voice-{v}.wav', x, sr)
        return
    voice, out_wav, plan_path = sys.argv[1:4]
    sr = 24000
    track = np.zeros(int((DUR + 1) * sr), np.float32)
    plan = []
    for start, end, text, cap in LINES:
        speed = BASE_SPEED
        while True:
            x, sr = say(k, text, voice, speed)
            d = len(x) / sr
            if start + d <= end or speed >= MAX_SPEED:
                break
            speed = min(MAX_SPEED, speed * min(1.06, (start + d - start) / (end - start) * 1.01))
        tempo = 1.0
        if start + d > end:
            tempo = min(MAX_TEMPO, d / (end - start))
            x = atempo(x, sr, tempo); d = len(x) / sr
        i = int(start * sr)
        track[i:i + len(x)] += x
        plan.append({'start': start, 'end': round(start + d, 3), 'limit': end, 'speed': round(speed, 3), 'tempo': round(tempo, 3),
                     'overrun': round(max(0, start + d - end), 3), 'text': text, 'caption': cap or text})
        print(f"{start:6.2f}-{start + d:6.2f} (limit {end:5.2f}) speed {speed:.2f} tempo {tempo:.3f}  {text}")
    write48(out_wav, track[:int(DUR * sr)], sr)
    json.dump(plan, open(plan_path, 'w'), indent=1)


if __name__ == '__main__':
    main()
