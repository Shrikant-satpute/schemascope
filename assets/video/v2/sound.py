#!/usr/bin/env python3
"""SchemaScope explainer v2 soundtrack ("Stop comparing in pairs", 71.5 s, white theme).

Every sound is synthesised here from sine waves and noise - no samples, no stock
music - so the audio can be published under the repo's licence. Event times live in
EV below: they come from script.json's scene windows and the narration's measured
phrase onsets, and v2/scene.html should place its visual beats on them (or EV moves
with the scene).

  python sound.py full snd/full.wav snd/vo.wav   # 71.5 s with the voiceover; the bed ducks ~8 dB under it
  python sound.py full snd/bed.wav               # music + SFX only
  python sound.py loop snd/loop.wav              # 8 s hero-loop bed, tail wrapped so it loops (no VO)
  python sound.py events snd/events.json         # EV and LOOP_EV, for syncing scene.html

Needs numpy only. A voiceover must be a 48 kHz mono float WAV already placed on the
same timeline (voice.py writes one). Output is 48 kHz stereo float WAV, not loudness-normalised; the
delivery step does that (see README.md).
"""
import sys, struct
import numpy as np

SR = 48000
BEAT = 0.5          # 120 bpm; each section's grid starts at its own downbeat (see EV)


# ------------------------------------------------------------------ basics
def T(d):
    return np.arange(int(round(d * SR))) / SR

def hz(m):
    return 440.0 * 2 ** ((m - 69) / 12)

def rng(seed):
    return np.random.default_rng(seed)

def _nf(n):
    return 1 << int(np.ceil(np.log2(max(2, n) * 2)))

def fgain(x, g_of_f):
    """Zero-phase filter by spectral gain. Pads 2x so ringing does not wrap."""
    n = len(x); nf = _nf(n)
    X = np.fft.rfft(x, n=nf, axis=0)
    g = g_of_f(np.fft.rfftfreq(nf, 1 / SR))
    if x.ndim == 2:
        g = g[:, None]
    return np.fft.irfft(X * g, n=nf, axis=0)[:n]

def lp(fc, o=2):
    return lambda f: 1 / np.sqrt(1 + (f / fc) ** (2 * o))

def hp(fc, o=2):
    return lambda f: 1 / np.sqrt(1 + (fc / np.maximum(f, 1e-3)) ** (2 * o))

def bp(lo, hi, o=2):
    return lambda f: lp(hi, o)(f) * hp(lo, o)(f)

def noise(d, seed):
    return rng(seed).standard_normal(int(round(d * SR)))

def ex(t, tau):
    return np.exp(-t / tau)

def attack(t, a):
    return np.clip(t / max(a, 1e-4), 0, 1)

def norm(x, peak=1.0):
    m = np.max(np.abs(x))
    return x * (peak / m) if m > 0 else x

def pan(x, p=0.0):
    """Mono -> stereo, constant power, p in [-1, 1]."""
    if x.ndim == 2:
        return x
    a = (p + 1) * np.pi / 4
    return np.stack([x * np.cos(a), x * np.sin(a)], axis=1)

def saw(freq, d, maxf=5000, kmax=40, phase0=0.0, rolloff=1.0):
    t = T(d); out = np.zeros_like(t)
    K = int(max(1, min(kmax, maxf // max(freq, 1))))
    for k in range(1, K + 1):
        out += np.sin(2 * np.pi * k * freq * t + phase0 * k) / k ** rolloff
    return out * 0.6

def sweep_phase(f_of_t):
    return 2 * np.pi * np.cumsum(f_of_t) / SR


def swept_noise(d, f0, f1, width_oct=0.7, seed=0):
    """Noise through a band whose centre glides f0 -> f1 (exponential). STFT shaped."""
    n = int(round(d * SR)); win = 1024; hop = 256
    x = noise(d + win / SR * 2, seed)
    w = np.hanning(win)
    starts = np.arange(0, n, hop)
    idx = starts[:, None] + np.arange(win)[None, :]
    F = np.fft.rfft(x[idx] * w, axis=1)
    freqs = np.fft.rfftfreq(win, 1 / SR)
    tc = np.clip(starts / max(n, 1), 0, 1)
    fc = f0 * (f1 / f0) ** tc
    lf = np.log2(np.maximum(freqs, 1.0))[None, :]
    g = np.exp(-0.5 * ((lf - np.log2(fc)[:, None]) / (width_oct / 2)) ** 2)
    y = np.fft.irfft(F * g, n=win, axis=1) * w
    out = np.zeros(n + win); wsum = np.zeros(n + win)
    for i, s in enumerate(starts):
        out[s:s + win] += y[i]; wsum[s:s + win] += w ** 2
    out = out[:n] / np.maximum(wsum[:n], 1e-3)
    return out / (np.std(out) + 1e-9)


# ------------------------------------------------------------------ drums
def kick(f0=130, f1=44, decay=0.32, click=0.25, seed=1):
    t = T(decay * 2.2)
    f = f1 + (f0 - f1) * ex(t, 0.035)
    body = np.sin(sweep_phase(f)) * ex(t, decay) * attack(t, 0.002)
    c = fgain(noise(len(t) / SR, seed), hp(2000)) * ex(t, 0.003) * click
    return np.tanh(1.6 * (body + c)) / np.tanh(1.6)

def snare(level=1.0, seed=2, decay=0.11):
    t = T(0.3)
    tone = np.sin(2 * np.pi * 186 * t) * ex(t, 0.05) * 0.5
    nz = fgain(noise(0.3, seed), bp(1200, 8000)) * ex(t, decay)
    return (tone + nz * 0.9) * level

def clap(seed=3):
    t = T(0.35); out = np.zeros_like(t)
    nz = fgain(noise(0.35, seed), bp(900, 5200))
    for k, dt in enumerate((0.0, 0.009, 0.018)):
        out += nz * ex(np.maximum(t - dt, 0), 0.005) * (t >= dt) * (0.8 + 0.1 * k)
    out += nz * ex(np.maximum(t - 0.026, 0), 0.085) * (t >= 0.026) * 0.9
    return out

def hat(open_=False, seed=4):
    d = 0.35 if open_ else 0.09
    t = T(d)
    return fgain(noise(d, seed), hp(7500, 4)) * ex(t, 0.16 if open_ else 0.028)


# ------------------------------------------------------------------ tonal
def pluck(m, d=0.55, bright=1.0, seed=0):
    f = hz(m); t = T(d); out = np.zeros_like(t)
    for k in range(1, 11):
        if k * f > 12000:
            break
        amp = (1 / k ** 1.45) * (1.0 if k % 2 else 0.65) * (bright if k > 2 else 1)
        out += amp * np.sin(2 * np.pi * k * f * t) * ex(t, 0.32 / (1 + 0.55 * (k - 1)))
    out *= attack(t, 0.0025)
    out += fgain(noise(d, seed), bp(2000, 9000)) * ex(t, 0.002) * 0.08
    return out

def bell(m, d=2.6, scale=1.0):
    f = hz(m); t = T(d); out = np.zeros_like(t)
    for r, a, tau in ((0.5, .22, 3.0), (1, 1, 2.2), (2.0, .45, 1.3), (2.76, .32, .9), (4.07, .18, .55), (5.4, .1, .35)):
        if r * f < 16000:
            out += a * np.sin(2 * np.pi * r * f * t) * ex(t, tau * scale)
    return out * attack(t, 0.0015)

def pad_note(m, d, rel=0.9, att=0.5, maxf=4200, detune=7.0):
    out = np.zeros((int(round((d + rel) * SR)), 2))
    t = T(d + rel)
    env = attack(t, att) * np.where(t < d, 1.0, ex(np.maximum(t - d, 0), rel / 3))
    for ch, cents in ((0, -detune), (1, detune)):
        for c2, g in ((cents, 0.6), (0.0, 0.4)):
            f = hz(m) * 2 ** (c2 / 1200)
            out[:, ch] += saw(f, d + rel, maxf=maxf, kmax=30, phase0=ch * 1.7 + c2) * g
    return out * env[:, None]

def bass_note(m, d=0.22, cutoff=700, drive=1.3):
    t = T(d + 0.06)
    x = saw(hz(m), d + 0.06, maxf=cutoff * 2.5, kmax=20) + 0.6 * np.sin(2 * np.pi * hz(m) * t)
    x = fgain(x, lp(cutoff, 3))
    env = attack(t, 0.004) * np.where(t < d, ex(t, 0.35), ex(t, 0.35) * ex(np.maximum(t - d, 0), 0.015))
    return np.tanh(drive * x * env)


# ------------------------------------------------------------------ fx
def whoosh(d=0.5, f0=350, f1=3200, seed=11, width=0.9):
    t = T(d); x = swept_noise(d, f0, f1, width, seed)
    env = np.sin(np.pi * np.clip(t / d, 0, 1)) ** 1.6
    peak = 0.62
    env *= np.where(t / d < peak, (t / d / peak) ** 1.2, 1)
    s = pan(x * env, 0)
    p = np.linspace(-0.7, 0.7, len(t))     # travels left to right
    a = (p + 1) * np.pi / 4
    s[:, 0] *= np.cos(a) * 1.41; s[:, 1] *= np.sin(a) * 1.41
    return s

def riser(d, f0=200, f1=8500, seed=12, tone=True):
    t = T(d); u = t / d
    x = swept_noise(d, f0, f1, 1.2, seed) * u ** 2.2
    if tone:
        f = 110 * (8 ** u)
        x += 0.35 * np.sin(sweep_phase(f)) * u ** 2.5
    x[-int(0.004 * SR):] *= np.linspace(1, 0, int(0.004 * SR))
    return x

def impact(big=1.0, seed=13):
    d = 3.2; t = T(d)
    f = 31 + 32 * ex(t, 0.28)
    boom = np.sin(sweep_phase(f)) * ex(t, 1.0) * attack(t, 0.003)
    crash = swept_noise(d, 9000, 1400, 1.6, seed) * ex(t, 0.75) * attack(t, 0.002) * 0.35
    k = np.zeros_like(t); kk = kick(150, 42, 0.45, 0.4); k[:len(kk)] = kk
    return np.tanh((boom * 0.9 + crash + k * 0.8) * big * 1.3)

def glitch(seed=21):
    r = rng(seed); d = 0.32; t = T(d); out = np.zeros_like(t)
    nz = fgain(noise(d, seed), bp(800, 7000))
    for i in range(6):
        s = int((i * 0.045 + r.uniform(0, 0.01)) * SR); L = int(r.uniform(0.012, 0.03) * SR)
        out[s:s + L] += nz[s:s + L] * (0.9 - i * 0.12)
        ff = r.choice([880, 1320, 1760, 660])
        out[s:s + L] += 0.25 * np.sign(np.sin(2 * np.pi * ff * t[s:s + L]))
    return fgain(out, lp(9000))

def mouse_click(seed=0):
    r = rng(seed); p = 1 + r.uniform(-0.04, 0.04)
    d = 0.16; t = T(d); out = np.zeros_like(t)
    for t0, g, fm in ((0.0, 1.0, 1.0), (0.072 + r.uniform(-0.006, 0.006), 0.5, 1.13)):
        tt = np.maximum(t - t0, 0); on = t >= t0
        nz = fgain(noise(d, seed * 7 + int(g * 10)), bp(2500, 10000))
        out += on * g * (nz * ex(tt, 0.0011) * 0.8
                         + np.sin(2 * np.pi * 3100 * p * fm * tt) * ex(tt, 0.0035) * 0.55
                         + np.sin(2 * np.pi * 1150 * p * fm * tt) * ex(tt, 0.006) * 0.45)
    return out

def key(seed=0, strength=1.0, space=False):
    r = rng(seed + 1000); p = 1 + r.uniform(-0.08, 0.08)
    d = 0.18; t = T(d); out = np.zeros_like(t)
    nz = noise(d, seed + 2000)
    hi = fgain(nz, bp(2400, 7500)); mid = fgain(nz, bp(500, 2600))
    f_thock = (150 if space else 255) * p
    out += hi * ex(t, 0.0014) * 0.55
    out += np.sin(2 * np.pi * f_thock * t) * ex(t, 0.028 if space else 0.016) * 0.85
    out += np.sin(2 * np.pi * f_thock * 2.1 * t) * ex(t, 0.007) * 0.25
    out += mid * ex(t, 0.022 if space else 0.010) * 0.6
    tr = (0.05 if space else 0.042) + r.uniform(0, 0.025)
    tt = np.maximum(t - tr, 0); on = t >= tr
    out += on * (hi * ex(tt, 0.0018) * 0.22 + np.sin(2 * np.pi * 360 * p * tt) * ex(tt, 0.005) * 0.18)
    return out * strength * attack(t, 0.0004)

def pop(m, seed=0, d=0.16):
    f = hz(m); t = T(d)
    fr = f * (1 + 0.7 * ex(t, 0.012))
    x = np.sin(sweep_phase(fr)) * ex(t, 0.045) * attack(t, 0.001)
    x += 0.2 * np.sin(2 * sweep_phase(fr)) * ex(t, 0.02)
    return x

def tick(f=3200, d=0.04, tau=0.005):
    t = T(d)
    return np.sin(2 * np.pi * f * t) * ex(t, tau) * attack(t, 0.0005)

def woodblock(f=1900, seed=0):
    t = T(0.08)
    x = np.sin(2 * np.pi * f * t) * ex(t, 0.011) + 0.4 * np.sin(2 * np.pi * f * 1.58 * t) * ex(t, 0.006)
    x += fgain(noise(0.08, seed), bp(1500, 6000)) * ex(t, 0.0015) * 0.4
    return x

def alert(m, seed=0, two=True):
    d = 0.2; t = T(d); out = np.zeros_like(t)
    for i, (mm, t0) in enumerate(((m, 0.0), (m - 3, 0.075)) if two else ((m, 0.0),)):
        tt = np.maximum(t - t0, 0); on = (t >= t0) & (tt < 0.07)
        sq = sum(np.sin(2 * np.pi * k * hz(mm) * tt) / k for k in (1, 3, 5, 7))
        out += on * sq * ex(tt, 0.03)
    return fgain(out, lp(3500))

def chime(ms=(84, 91, 96), gap=0.085):
    d = 2.4 + gap * len(ms); out = np.zeros(int(d * SR))
    for i, m in enumerate(ms):
        b = bell(m, 2.2, 0.55) * (1 - 0.25 * i)
        s = int(i * gap * SR); out[s:s + len(b)] += b[:len(out) - s]
    return out

def data_chatter(d, rate=28, seed=31, lo=2200, hi=5200):
    r = rng(seed); out = np.zeros(int(d * SR) + SR // 10)
    tt = 0.0
    while tt < d:
        tt += r.exponential(1 / rate)
        if tt >= d:
            break
        tk = tick(r.uniform(lo, hi), 0.03, 0.003) * r.uniform(0.3, 1.0)
        s = int(tt * SR); out[s:s + len(tk)] += tk
    return out


# ------------------------------------------------------------------ reverb
def make_ir(rt, seed, pre=0.012, dark=900):
    n = int(rt * 1.6 * SR); t = np.arange(n) / SR
    ir = np.zeros((n, 2))
    for ch in range(2):
        nz = rng(seed + ch).standard_normal(n)
        low = fgain(nz, lp(dark)) * np.exp(-6.9 * t / rt)
        high = fgain(nz, hp(dark)) * np.exp(-6.9 * t / (rt * 0.45))
        ir[:, ch] = low + high
    r = rng(seed + 9)
    for _ in range(9):                                    # a few early reflections
        s = int(r.uniform(0.006, 0.05) * SR); ir[s, :] += r.uniform(0.3, 0.8) * r.choice([-1, 1], 2)
    ir = np.vstack([np.zeros((int(pre * SR), 2)), ir])
    return ir / np.sqrt(np.sum(ir ** 2) / 2)

def convolve(x, ir):
    n = len(x) + len(ir); nf = 1 << int(np.ceil(np.log2(n)))
    out = np.zeros((len(x), 2))
    for ch in range(2):
        X = np.fft.rfft(x[:, ch], nf); H = np.fft.rfft(ir[:, ch], nf)
        out[:, ch] = np.fft.irfft(X * H, nf)[:len(x)]
    return out



# ------------------------------------------------------------------ v2 one-shots
def tape_stop(d=0.32, m=48, seed=70):
    """Pitch falls away to nothing: colour drains out of the hook into the grey old way."""
    t = T(d); u = t / d
    f = hz(m) * (1 - 0.92 * u ** 0.8)
    x = np.sin(sweep_phase(f)) + 0.4 * np.sin(2 * sweep_phase(f)) + 0.2 * np.sin(3 * sweep_phase(f))
    x += fgain(noise(d, seed), lp(1800)) * 0.15
    return x * (1 - u) ** 1.2 * attack(t, 0.003)

def clunk(seed=71):
    """Short mechanical stop, the frame freeze at 9.8 s."""
    t = T(0.09)
    x = np.sin(2 * np.pi * 95 * t) * ex(t, 0.02) + fgain(noise(0.09, seed), bp(300, 2500)) * ex(t, 0.006) * 0.8
    return x * attack(t, 0.0005)

def flip(seed=0, f=2400):
    """Card flip: a short swish into a light tap."""
    d = 0.14; t = T(d)
    sw = swept_noise(d, 1500, 6000, 0.8, seed) * np.sin(np.pi * np.clip(t / 0.09, 0, 1)) ** 2 * (t < 0.09)
    tap = np.zeros_like(t); i = int(0.085 * SR); tt = t[i:] - t[i]
    tap[i:] = np.sin(2 * np.pi * f * tt) * ex(tt, 0.006) + fgain(noise(d, seed + 5), hp(3000))[i:] * ex(tt, 0.002) * 0.5
    return sw * 0.35 + tap * 0.6

def paper(seed=0, d=0.28):
    """Paper swish for a script folder fanning out."""
    t = T(d)
    x = swept_noise(d, 900, 5200, 1.1, seed) * np.sin(np.pi * np.clip(t / d, 0, 1)) ** 1.5
    return x * (0.6 + 0.4 * np.sin(2 * np.pi * 23 * t))

def stamp(seed=72):
    """Dull rubber-stamp thud for 'Refused'."""
    t = T(0.45)
    body = np.sin(sweep_phase(70 + 60 * ex(t, 0.02))) * ex(t, 0.09)
    slap = fgain(noise(0.45, seed), bp(200, 1600)) * ex(t, 0.018)
    return np.tanh(1.8 * (body + slap * 0.7)) * attack(t, 0.0008)

def latch(seed=73):
    """Solid two-part latch: metal tick, then the body seating."""
    t = T(0.25); out = np.zeros_like(t)
    out += np.sin(2 * np.pi * 3400 * t) * ex(t, 0.003) * 0.7 + fgain(noise(0.25, seed), hp(4000)) * ex(t, 0.0015) * 0.6
    i = int(0.028 * SR); tt = t[i:] - t[i]
    out[i:] += np.sin(2 * np.pi * 180 * tt) * ex(tt, 0.03) + np.sin(2 * np.pi * 620 * tt) * ex(tt, 0.012) * 0.5
    return out * attack(t, 0.0004)

def shimmer(d=1.4, seed=74, lo=5000, hi=11000):
    """Logo / strip shimmer: an airy high band that swells and fades, plus a few glints."""
    t = T(d)
    x = swept_noise(d, lo, hi, 0.6, seed) * np.sin(np.pi * np.clip(t / d, 0, 1)) ** 2 * 0.25
    r = rng(seed)
    for _ in range(9):
        s = int(r.uniform(0.05, d * 0.8) * SR); g = tick(r.uniform(5500, 9500), 0.12, 0.03) * r.uniform(0.2, 0.6)
        x[s:s + len(g)] += g[:len(x) - s]
    return x

def sparkle(d=1.2, seed=75, n=26):
    """Confetti: random bright pings over a second, thinning out."""
    r = rng(seed); out = np.zeros(int((d + 0.3) * SR))
    for k in range(n):
        tt = d * (k / n) ** 1.6 + r.uniform(0, 0.03)
        g = bell(r.choice([96, 98, 100, 103, 105, 108]), 0.3, 0.12) * r.uniform(0.15, 0.5) * (1 - 0.6 * k / n)
        s = int(tt * SR); out[s:s + len(g)] += g[:len(out) - s]
    return out

def stopwatch_tick(seed=0):
    t = T(0.03)
    return (np.sin(2 * np.pi * 4300 * t) * ex(t, 0.002) + fgain(noise(0.03, seed), hp(5000)) * ex(t, 0.001) * 0.5)

def thud_card(seed=0):
    """A database card slamming into the grid: small low thud plus a tap."""
    t = T(0.14)
    x = np.sin(sweep_phase(160 + 120 * ex(t, 0.01))) * ex(t, 0.035) + fgain(noise(0.14, seed), bp(1500, 6000)) * ex(t, 0.002) * 0.6
    return x * attack(t, 0.0006)

def drone(d, ms, cut=420, seed=0):
    t = T(d)
    x = sum(saw(hz(m), d, 1400, 16, phase0=0.7 * i) for i, m in enumerate(ms))
    x = fgain(x, lp(cut, 3)) + 0.5 * np.sin(2 * np.pi * hz(ms[0]) * t)
    return x * (1 - 0.15 * (0.5 + 0.5 * np.sin(2 * np.pi * 0.35 * t)))


# ------------------------------------------------------------------ mixer
BUSES = ('music', 'drums', 'ui', 'keys', 'fx')

class Mix:
    def __init__(self, mode):
        self.mode = mode
        self.dur = DUR if mode == 'full' else LOOP_DUR
        self.n = int((self.dur + 4.0) * SR)
        self.b = {k: np.zeros((self.n, 2)) for k in BUSES}
        self.big = np.zeros((self.n, 2)); self.room = np.zeros((self.n, 2))
        self.kicks = []
        self.gates = []          # (start, end): whole bed silenced (the 'old way' freeze)

    def add(self, bus, t, sig, g=1.0, p=0.0, big=0.0, room=0.0):
        s = pan(np.asarray(sig, float), p) * g
        i = int(round(t * SR)); j = min(self.n, i + len(s))
        if i >= self.n or i < 0:
            return
        self.b[bus][i:j] += s[:j - i]
        if big:
            self.big[i:j] += s[:j - i] * big
        if room:
            self.room[i:j] += s[:j - i] * room

    def kick(self, t, g=1.0, pump=True):
        if pump:
            self.kicks.append(t)
        self.add('drums', t, KICK, g)


# cached one-shots (variation comes from per-hit gain and seed lists)
KICK = kick(140, 46, 0.28, 0.3)
SNARE = [snare(seed=s) for s in range(4)]
CLAP = clap()
HATS = [hat(seed=s) for s in range(6)]
OHAT = hat(True, 9)
CLICKS = [mouse_click(s) for s in range(6)]
KEYS = [key(s) for s in range(16)]
SPACE = key(77, 1.0, space=True)

# Brighter than v1: C major, added ninths, IV-V-vi-I rather than the minor loop.
CHORDS = {
    'C': (48, [60, 64, 67, 74]), 'F': (41, [57, 60, 65, 67]), 'G': (43, [59, 62, 67, 69]),
    'Am': (45, [57, 60, 64, 71]), 'Em': (40, [55, 59, 64, 66]), 'Dm': (38, [57, 62, 65, 69]),
    'Fm': (41, [56, 60, 65, 67]), 'Esus': (40, [57, 59, 64, 69]),
}
PENT = [72, 74, 76, 79, 81, 84, 86, 88, 91, 93]

DUR, LOOP_DUR = 71.5, 8.0

# Event times, from script.json scene windows and the narration's measured phrase
# onsets (snd/plan.json). scene.html for v2 should put its visual beats on these;
# `python sound.py events snd/events.json` writes them out.
EV = {
    'cold_open': 0.0, 'cards': 0.06, 'card_step': 0.067,           # 10 cards, two frames apart
    'hit1': 0.0, 'hit2': 1.75, 'hit3': 2.68, 'yellow_hook': 2.68,  # VO: "Eight client..." / "Plus Q A." / "One compare."
    'char_pop': 3.1, 'to_grey': 3.72,
    'old_start': 3.8, 'freeze': 9.8, 'silence_end': 10.3, 'turn': 10.8,
    'source_chip': 11.5, 'targets': 11.9, 'target_step': 0.12,     # nine chips fan in
    'compare_click': 14.6, 'sweep': (14.75, 15.5), 'scores_land': 15.5,   # lands on "...the same time"
    'push_in': 16.1, 'char_happy': 16.6,
    'stopwatch_start': 18.25, 'stopwatch_stop': 19.2,              # 0.95 s in real time
    'chips': (19.6, 20.5, 21.2), 'strip': 22.0,
    'pitch_lines': (23.73, 25.69, 26.68),                          # VO phrase onsets
    'sort': 28.3, 'sort_step': 0.09, 'push_c07': 30.9, 'chip_flip': 32.15, 'pan_hero': 33.6,
    'diff_in': 35.3, 'token': 36.3, 'create_click': 37.15, 'folders': 37.5, 'folder_step': 0.22,
    'review': 38.75, 'badge': 39.4,
    'zoom_switch': 41.85, 'char_nervous': 42.6, 'switch_click': 43.9, 'dialog': 44.0, 'dialog_hl': 44.8,
    'enable_click': 46.0, 'amber': 46.05,
    'apply_panel': 47.85, 'ticks': 48.1, 'type': 48.5, 'type_step': 0.085, 'apply_wakes': 49.95,
    'inset': 50.6, 'banner': 50.9, 'refused': 51.35,
    'files': (53.6, 57.9), 'n_files': 34, 'rollback': (56.5, 56.95), 'applied': 58.2,   # rollback = the labelled illustration panel
    'recompare': 59.5, 'count': (60.6, 61.95), 'hundred': 61.95, 'tile_slide': 63.6,
    'logo': 64.9, 'title_type': 65.1, 'tagline': 66.8, 'pills': 67.6, 'pill_step': 0.25,
    'button': 69.2, 'end_chord': 69.9,
}


def pad_chord(M, name, t0, d, g=0.075, cut=3200, att=0.25, rel=0.6, big=0.3, top=False):
    root, notes = CHORDS[name]
    for m in notes + ([notes[0] + 12] if top else []):
        M.add('music', t0, fgain(pad_note(m, d, rel, att, 4200), lp(cut)), g, big=big)

def bass_line(M, name, t0, d, g=0.15, cut=700, step=0.25):
    root, _ = CHORDS[name]
    r = root - 12 if root > 40 else root
    for k in range(int(d / step + 0.01)):
        M.add('music', t0 + k * step, bass_note(r + (12 if k % 4 == 2 else 0), step * 0.8, cut), g)

def arp(M, name, t0, d, g=0.035, step=0.125, oct_=12, pat=(0, 1, 2, 3, 2, 1, 2, 3), seed=0):
    _, notes = CHORDS[name]
    tones = [n + oct_ for n in notes]
    for k in range(int(d / step + 0.01)):
        M.add('music', t0 + k * step, pluck(tones[pat[k % len(pat)] % len(tones)], 0.42, 0.9, seed + k),
              g * (1.2 if k % 4 == 0 else 1.0), p=0.35 * np.sin(k * 0.7), big=0.25)

def groove(M, a, b, kick_g=0.62, clap_g=0.15, hat_sub=0.25, hat_g=0.06, R=None, four=True):
    for bt in np.arange(a, b - 1e-6, BEAT):
        k = int(round((bt - a) / BEAT))
        if four or k % 2 == 0:
            M.kick(bt, kick_g)
        if clap_g and k % 4 in (1, 3):
            M.add('drums', bt, CLAP, clap_g, big=0.12)
        for s in np.arange(0, BEAT - 1e-6, hat_sub):
            if bt + s < b:
                M.add('drums', bt + s + R.uniform(0, 0.004), HATS[R.integers(6)], hat_g * (0.7 if s == 0 else 1.0), p=0.3)

def section(M, prog, a, b, g_pad=0.07, cut=3200, arp_g=0.035, bass_g=0.15, bar=2.0, seed=0):
    t0, i = a, 0
    while t0 < b - 1e-6:
        name = prog[i % len(prog)]; d = min(bar, b - t0)
        pad_chord(M, name, t0, d, g_pad, cut)
        bass_line(M, name, t0, d, bass_g)
        if arp_g:
            arp(M, name, t0, d, arp_g, seed=seed + 20 * i)
        t0 += bar; i += 1


# ------------------------------------------------------------------ score
def score(M):
    R = rng(99); E = EV

    # ===== 1 HOOK 0-3.8: cold-open hit, cards slam in, three bass hits, yellow block
    M.add('fx', E['cold_open'], impact(0.7, 40), 0.75, big=0.35)
    for i, m in enumerate((72, 76, 79, 86)):
        M.add('music', 0.0 + i * 0.02, bell(m, 2.6, 0.8), 0.06, p=-0.4 + 0.27 * i, big=0.5)
    for i in range(10):
        M.add('ui', E['cards'] + i * E['card_step'], thud_card(i), 0.30 if i else 0.42,
              p=-0.6 + 0.13 * i, room=0.25)
    for j, (ht, name) in enumerate(zip((E['hit1'], E['hit2'], E['hit3']), ('C', 'F', 'G'))):
        root, notes = CHORDS[name]
        M.kick(ht, 0.85 if j else 0.6, pump=False)
        M.add('music', ht, bass_note(root - 12 if root > 40 else root, 0.5, 900, 1.6), 0.24)
        for m in notes:
            M.add('music', ht, fgain(pad_note(m, 0.45, 0.35, 0.005, 5200), lp(4200)), 0.05, big=0.35)
    M.add('fx', E['yellow_hook'] - 0.28, whoosh(0.34, 500, 4200, 14), 0.24)
    M.add('drums', E['yellow_hook'], CLAP, 0.14, big=0.15)
    M.add('ui', E['char_pop'], pop(79, 3), 0.20, room=0.2)
    M.add('ui', E['char_pop'] + 0.09, pop(84, 4), 0.12, room=0.2)
    air = fgain(noise(3.7, 50), bp(4000, 12000)) * attack(T(3.7), 1.0) * 0.02
    M.add('fx', 0.0, air, 1.0)
    M.add('music', E['to_grey'], tape_stop(0.34, 55), 0.16)

    # ===== 2 OLD WAY 3.8-9.8: grey, slow mechanical bed, ticking clock; hard stop, 0.5 s silence
    a, b = E['old_start'], E['freeze']
    d = b - a + 0.2
    M.add('music', a, drone(d, (45, 52, 57), 380) * attack(T(d), 0.3), 0.13)
    for k, tt in enumerate(np.arange(a + 0.2, b, 0.5)):          # wall clock: tick / tock
        M.add('ui', tt, woodblock(2000 if k % 2 == 0 else 1450, k), 0.11, p=0.35, room=0.35)
    for k, tt in enumerate(np.arange(a + 0.2, b, 1.0)):          # a dull, slow two-note machine
        M.add('music', tt, fgain(pluck(45 if k % 2 == 0 else 52, 0.6, 0.4, k), lp(900)), 0.18)
    for k, tt in enumerate(np.arange(a + 0.45, b, 0.25)):         # ratchet of the one crawling bar
        M.add('ui', tt, tick(900 + 20 * (k % 4), 0.03, 0.004), 0.035, p=-0.3)
    crawl = swept_noise(b - a - 0.3, 300, 900, 0.5, 51) * attack(T(b - a - 0.3), 0.8)
    M.add('fx', a + 0.3, fgain(crawl, lp(1200)), 0.03)
    M.add('ui', b, clunk(), 0.4)
    M.gates.append((b + 0.004, E['silence_end']))
    M.add('fx', E['silence_end'], riser(E['turn'] - E['silence_end'], 400, 9000, 15), 0.26)

    # ===== 3 TURN 10.8-18: bright cut, chips fan in, click, sweep, all nine land in one bloom
    M.add('fx', E['turn'], impact(0.45, 41), 0.45, big=0.4)
    M.add('ui', E['turn'], chime((84, 88, 91)), 0.15, big=0.4)
    section(M, ('F', 'G', 'Am', 'C'), E['turn'], E['stopwatch_start'] - 0.05, 0.065, 2800, 0.03, 0.13, seed=100)
    groove(M, E['turn'] + 2.0, E['stopwatch_start'] - 0.05, 0.55, 0.0, 0.25, 0.055, R)
    M.add('ui', E['source_chip'], pop(72, 1), 0.2, room=0.2)
    M.add('ui', E['source_chip'] + 0.01, pluck(84, 0.4, 0.9, 5), 0.06, big=0.15)
    for i in range(9):
        M.add('ui', E['targets'] + i * E['target_step'], pop(PENT[i], 10 + i), 0.15, p=-0.5 + 0.125 * i, room=0.2)
    M.add('ui', E['compare_click'], CLICKS[0], 0.62, room=0.18)
    s0, s1 = E['sweep']
    M.add('fx', E['compare_click'] + 0.02, riser(E['scores_land'] - E['compare_click'] - 0.02, 300, 9000, 16), 0.2)
    M.add('fx', s0, whoosh(s1 - s0 + 0.15, 600, 5200, 17, 0.8), 0.22)
    M.add('drums', E['scores_land'], hat(True, 3), 0.12, big=0.3)
    M.add('ui', E['scores_land'], snare(1.0, 9, 0.06), 0.16, big=0.3)       # the clean snap
    for i, m in enumerate((72, 76, 79, 84, 88, 91)):                       # bloom: every target lights
        M.add('music', E['scores_land'] + i * 0.012, bell(m, 3.0, 0.9), 0.05, p=-0.5 + 0.2 * i, big=0.6)
    for m in (60, 64, 67, 72, 74):
        M.add('music', E['scores_land'], fgain(pad_note(m, 2.2, 1.0, 0.04, 5200), lp(5000)), 0.045, big=0.5)
    for i in range(9):                                                     # nine soft ticks
        M.add('ui', E['scores_land'] + 0.04 + i * 0.03, tick(hz(PENT[i] + 12), 0.05, 0.004), 0.05, p=-0.6 + 0.15 * i, room=0.3)
    M.add('fx', E['push_in'], whoosh(0.6, 250, 1800, 18, 1.0), 0.08)
    M.add('ui', E['char_happy'], pop(84, 6), 0.16, room=0.2)
    M.add('fx', E['stopwatch_start'] - 0.45, whoosh(0.45, 400, 3000, 19), 0.12)

    # ===== 4 PROOF 18-23.5: stopwatch, stat chips type on
    pad_chord(M, 'Am', 18.0, 2.0, 0.05, 1800); pad_chord(M, 'F', 20.0, 2.0, 0.05, 1800)
    pad_chord(M, 'G', 22.0, 1.5, 0.05, 2200)
    for bt in np.arange(18.0, 23.0, 1.0):
        M.kick(bt, 0.4)
    M.add('ui', E['stopwatch_start'], CLICKS[1], 0.55)
    for k, tt in enumerate(np.arange(E['stopwatch_start'] + 0.05, E['stopwatch_stop'] - 0.01, 0.05)):
        M.add('ui', tt, stopwatch_tick(k), 0.08 + 0.04 * (k % 2), p=0.2)
    M.add('ui', E['stopwatch_stop'], latch(80), 0.5, room=0.25)
    M.add('ui', E['stopwatch_stop'] + 0.02, pluck(88, 0.5, 1.0, 81), 0.06, big=0.3)
    for j, (ct, n) in enumerate(zip(E['chips'], (19, 12, 9))):
        M.add('ui', ct - 0.03, pop(76 + 3 * j, 30 + j), 0.12, room=0.2)
        for i in range(n):
            M.add('keys', ct + i * 0.035 + R.uniform(-0.005, 0.005), KEYS[(i + j) % 16], 0.18, p=R.uniform(-0.2, 0.2), room=0.2)
    M.add('fx', E['strip'], whoosh(0.4, 600, 3000, 20), 0.07)
    M.add('fx', 23.0, riser(0.7, 300, 9000, 21), 0.22)

    # ===== 5 PITCH CARD 23.5-28: one drum hit per line, music back underneath
    pad_chord(M, 'F', 23.5, 2.2, 0.06, 3000); pad_chord(M, 'G', 25.69, 1.0, 0.06, 3400)
    pad_chord(M, 'C', 26.68, 1.32, 0.07, 4200, top=True)
    for j, ht in enumerate(E['pitch_lines']):
        M.kick(ht, 0.9, pump=False)
        M.add('drums', ht, CLAP, 0.16 + 0.04 * j, big=0.2)
        M.add('drums', ht, snare(1.0, 40 + j, 0.14), 0.09 + 0.03 * j, big=0.2)
        root = CHORDS[('F', 'G', 'C')[j]][0]
        M.add('music', ht, bass_note(root - 12 if root > 40 else root, 0.6, 900, 1.5), 0.22)
    M.add('fx', E['pitch_lines'][2] - 0.3, whoosh(0.34, 500, 4200, 22), 0.22)
    for i, m in enumerate((84, 88, 91)):
        M.add('music', E['pitch_lines'][2] + i * 0.03, bell(m, 2.0, 0.7), 0.045, big=0.5)
    for k in range(8):
        M.add('drums', 27.5 + k * 0.0625, SNARE[k % 4], 0.03 + 0.12 * k / 7, big=0.08)

    # ===== 6-7 SCORES + FIX 28-41.8: the main groove
    section(M, ('F', 'G', 'Am', 'C'), 28.0, 41.8, 0.07, 3400, 0.04, 0.15, seed=200)
    groove(M, 28.0, 41.8, 0.66, 0.15, 0.25, 0.06, R)
    for bt in np.arange(34.0, 41.8, 0.125):                         # 16th hats lift for scene 7
        M.add('drums', bt + 0.0625, HATS[R.integers(6)], 0.035, p=-0.3)
    M.add('drums', 28.0, OHAT, 0.12, big=0.2)
    for i in range(9):                                              # tiles sort worst first
        M.add('ui', E['sort'] + i * E['sort_step'], flip(i, 2200 + 120 * i), 0.24, p=-0.6 + 0.15 * i, room=0.2)
    M.add('fx', E['push_c07'], whoosh(0.6, 250, 1800, 23, 1.0), 0.08)
    M.add('ui', E['chip_flip'], flip(40, 1800), 0.3, room=0.2)
    M.add('ui', E['chip_flip'] + 0.09, pop(76, 41), 0.17, room=0.2)
    M.add('ui', E['chip_flip'] + 0.16, pop(72, 42), 0.12, room=0.2)
    M.add('fx', E['pan_hero'], whoosh(0.6, 300, 2400, 24, 1.0), 0.09)
    M.add('fx', E['diff_in'], whoosh(0.4, 400, 2800, 25), 0.10)
    M.add('ui', E['token'], pluck(88, 0.5, 1.0, 43), 0.07, big=0.3)
    M.add('ui', E['create_click'], CLICKS[2], 0.6, room=0.18)
    for i in range(5):                                              # 01 03 04 05 06 fan out
        M.add('fx', E['folders'] + i * E['folder_step'], paper(60 + i), 0.10, p=-0.5 + 0.25 * i)
        M.add('ui', E['folders'] + i * E['folder_step'] + 0.05, tick(hz(PENT[i] + 12), 0.06, 0.004), 0.04, room=0.2)
    M.add('ui', E['review'], pop(67, 44), 0.15, room=0.25)          # 99_review: notes, a lower, softer drop
    M.add('ui', E['badge'], chime((84, 91, 96)), 0.2, big=0.4)
    M.add('fx', 41.2, riser(0.6, 300, 7000, 26, tone=False), 0.16)

    # ===== 8 WRITE MODE 41.8-47.8: zoom, tension under the dialog, latch to amber
    M.add('fx', E['zoom_switch'], whoosh(0.5, 300, 2600, 27), 0.16)
    pad_chord(M, 'Am', 41.8, 2.1, 0.06, 2000)
    for bt in np.arange(41.8, 43.9, 0.5):
        M.kick(bt, 0.45)
        M.add('drums', bt + 0.25, HATS[R.integers(6)], 0.05, p=0.3)
    bass_line(M, 'Am', 41.8, 2.0, 0.11, 500, 0.5)
    M.add('ui', E['char_nervous'], pop(70, 45), 0.12, room=0.2)
    M.add('ui', E['char_nervous'] + 0.08, pop(66, 46), 0.1, room=0.2)
    M.add('ui', E['switch_click'], CLICKS[3], 0.6, room=0.18)
    M.add('ui', E['dialog'], pop(64, 47), 0.18, room=0.2)
    dd = E['enable_click'] - E['dialog'] + 0.1                       # tension pad: E sus, a held fifth, a heartbeat
    pad_chord(M, 'Esus', E['dialog'], dd, 0.06, 1800, att=0.6, rel=0.25)
    M.add('music', E['dialog'], drone(dd, (40, 47), 300) * attack(T(dd), 0.5), 0.1)
    for bt in np.arange(E['dialog'] + 0.2, E['enable_click'] - 0.1, 0.75):
        M.add('drums', bt, fgain(kick(80, 40, 0.15, 0.0), lp(380)), 0.45)
    M.add('fx', E['dialog_hl'], swept_noise(0.3, 2500, 6000, 0.7, 48) * np.sin(np.pi * T(0.3) / 0.3), 0.05)
    M.add('ui', E['enable_click'], CLICKS[4], 0.6, room=0.18)
    M.add('ui', E['amber'], latch(), 0.6, room=0.3)
    M.add('fx', E['amber'] + 0.05, shimmer(0.9, 49, 3500, 8000), 0.25, big=0.3)
    pad_chord(M, 'F', E['amber'], 47.8 - E['amber'], 0.06, 3000)
    M.add('music', E['amber'], bass_note(29, 1.6, 600), 0.14)

    # ===== 9 APPLY 47.8-53.3: folders ticked, the name typed, Apply wakes, Refused
    section(M, ('F', 'G', 'Am', 'G'), 47.8, 53.3, 0.06, 2600, 0.0, 0.12, seed=300)
    groove(M, 47.8, 53.3, 0.5, 0.0, 0.5, 0.05, R)
    M.add('fx', E['apply_panel'], whoosh(0.4, 400, 2800, 28), 0.10)
    for i in range(5):
        M.add('ui', E['ticks'] + i * 0.1, tick(hz(PENT[i] + 12), 0.06, 0.004), 0.06, room=0.2)
    for i in range(15):                                             # "SSDemo_Client05"
        M.add('keys', E['type'] + i * E['type_step'] + R.uniform(-0.008, 0.008), KEYS[i % 16], 0.42,
              p=R.uniform(-0.15, 0.15), room=0.25)
    M.add('ui', E['apply_wakes'], pop(76, 50), 0.16, room=0.2)
    M.add('ui', E['apply_wakes'] + 0.07, pop(83, 51), 0.12, room=0.2)
    M.add('fx', E['inset'], whoosh(0.45, 400, 3000, 29), 0.12)
    M.add('ui', E['banner'], alert(76, 52), 0.07, room=0.3)
    M.add('ui', E['refused'], stamp(), 0.55, room=0.3)

    # ===== 10 ONE FILE, ONE TRANSACTION 53.3-59.3: 34 ticks, accelerating and rising, then resolve
    section(M, ('F', 'G', 'Am'), 53.3, 59.3, 0.06, 3000, 0.0, 0.13, seed=400)
    groove(M, 53.3, 58.2, 0.58, 0.12, 0.125, 0.045, R)
    f0, f1 = E['files']; nf = E['n_files']
    for k in range(nf):
        u = k / (nf - 1); tt = f0 + (f1 - f0) * u ** 0.6
        m = 72 + int(round(19 * u))
        M.add('ui', tt, tick(hz(m), 0.05, 0.004), 0.07, p=-0.4 + 0.8 * u, room=0.2)
        M.add('ui', tt + 0.012, tick(1100, 0.02, 0.002), 0.025)
    M.add('fx', f0 + 0.5, riser(f1 - f0 - 0.2, 200, 8000, 30), 0.12)
    rb0, rb1 = E['rollback']                                         # illustration: file 13 fails, rolls back
    M.add('ui', rb0, alert(70, 54, two=False), 0.09, p=0.5, room=0.3)
    M.add('fx', rb1, whoosh(0.28, 2600, 500, 35), 0.07, p=0.5)
    for m in (60, 64, 67, 72, 76):                                   # resolve chord on the result line
        M.add('music', E['applied'], fgain(pad_note(m, 1.0, 0.5, 0.02, 5200), lp(4200)), 0.05, big=0.45)
    M.add('ui', E['applied'], chime((88, 91, 96)), 0.16, big=0.4)
    M.kick(E['applied'], 0.7, pump=False)

    # ===== 11 COMPARED AGAIN 59.3-64.8: chatter, count-up blips, bell + major chord at 100%
    pad_chord(M, 'Dm', 59.3, 1.3, 0.05, 2200); pad_chord(M, 'G', 60.6, E['hundred'] - 60.6, 0.055, 2800)
    for bt in np.arange(59.3, E['hundred'] - 0.05, 0.5):
        M.kick(bt, 0.45)
    M.add('ui', E['recompare'], data_chatter(1.0, 30, 53), 0.06, p=0.2)
    c0, c1 = E['count']
    for k in range(24):                                              # 97.6 -> 100.0
        u = k / 23
        M.add('ui', c0 + (c1 - c0 - 0.06) * u, tick(hz(79 + 0.6 * k), 0.03, 0.003), 0.06 + 0.04 * u, p=0.3)
    M.add('fx', c0 - 0.2, riser(c1 - c0 + 0.2, 300, 9000, 31), 0.2)
    h = E['hundred']
    M.add('fx', h, impact(0.5, 43), 0.5, big=0.45)
    M.add('ui', h, bell(84, 3.6, 1.2), 0.17, big=0.6)                # the bell
    M.add('ui', h + 0.06, bell(91, 3.0, 1.0), 0.08, big=0.6)
    M.add('fx', h + 0.05, sparkle(1.3), 0.25, big=0.4)
    section(M, ('C', 'F', 'G'), h, 64.8, 0.075, 4200, 0.045, 0.16, bar=1.0, seed=500)
    groove(M, h, 64.8, 0.7, 0.16, 0.125, 0.05, R)
    M.add('drums', h, OHAT, 0.14, big=0.25)
    M.add('fx', E['tile_slide'], whoosh(0.5, 400, 3000, 32), 0.12)

    # ===== 12 CLOSE 64.8-71.5: logo shimmer, title types, pills, swell, resolve
    M.add('fx', E['logo'], shimmer(1.6, 54), 0.35, big=0.4)
    for i, m in enumerate((72, 79, 84, 88, 91)):
        M.add('music', E['logo'] + i * 0.04, bell(m, 3.5, 1.0), 0.04, p=-0.4 + 0.2 * i, big=0.6)
    section(M, ('F', 'G', 'Am'), 64.8, E['end_chord'], 0.07, 3600, 0.035, 0.14, seed=600)
    groove(M, 66.8, E['end_chord'], 0.55, 0.12, 0.25, 0.05, R)
    for i in range(23):                                              # "Stop comparing in pairs."
        M.add('keys', E['title_type'] + i * 0.045 + R.uniform(-0.006, 0.006), KEYS[(i + 3) % 16], 0.26,
              p=R.uniform(-0.15, 0.15), room=0.25)
    M.add('fx', E['tagline'] - 0.25, whoosh(0.32, 500, 4000, 33), 0.18)
    for i in range(6):
        M.add('ui', E['pills'] + i * E['pill_step'], pop(PENT[i + 2], 60 + i), 0.13, p=-0.5 + 0.2 * i, room=0.2)
    M.add('ui', E['button'], pop(79, 66), 0.2, big=0.25)
    M.add('ui', E['button'] + 0.04, chime((91, 96)), 0.1, big=0.4)
    ec = E['end_chord']; dd = DUR - ec + 0.5
    for m in (48, 55, 60, 64, 67, 74):
        M.add('music', ec, fgain(pad_note(m, dd, 1.0, 0.08, 5200), lp(4000)), 0.065, big=0.5)
    M.add('music', ec, bass_note(36, dd - 0.3, 500), 0.16)
    M.kick(ec, 0.75, pump=False)
    for i, m in enumerate((72, 76, 79, 84)):
        M.add('music', ec + i * 0.03, bell(m, 3.0, 1.0), 0.05, big=0.6)


def score_loop(M):
    """8 s hero loop. Silent-friendly: the page autoplays it muted, so nothing here
    carries meaning; it is a light bed with the loop's own beats (cards, yellow block,
    sweep, scores, character), and the tail wraps so the loop is seamless."""
    R = rng(7)
    for name, t0 in (('F', 0.0), ('G', 2.0), ('Am', 4.0), ('C', 6.0)):
        pad_chord(M, name, t0, 2.0, 0.06, 2600, att=0.3)
        bass_line(M, name, t0, 2.0, 0.1, 600, 0.5)
        arp(M, name, t0, 2.0, 0.025, seed=int(t0 * 10))
    for bt in np.arange(0.0, 8.0, 0.5):
        if int(bt * 2) % 2 == 0:
            M.kick(bt, 0.45)
        M.add('drums', bt + 0.25, HATS[R.integers(6)], 0.045, p=0.3)
    for i in range(10):
        M.add('ui', 0.4 + i * 0.067, thud_card(i), 0.22, p=-0.6 + 0.13 * i, room=0.25)
    M.add('fx', 1.3, whoosh(0.3, 500, 4200, 14), 0.18)
    M.add('fx', 2.6, whoosh(0.75, 600, 5200, 17, 0.8), 0.16)
    for i, m in enumerate((72, 76, 79, 84)):
        M.add('music', 3.3 + i * 0.012, bell(m, 2.4, 0.8), 0.04, big=0.6)
    for i in range(9):
        M.add('ui', 3.34 + i * 0.03, tick(hz(PENT[i] + 12), 0.05, 0.004), 0.04, room=0.3)
    M.add('ui', 4.0, pop(84, 6), 0.13, room=0.2)
    M.add('fx', 6.6, whoosh(0.9, 2400, 400, 18, 1.0), 0.06)


LOOP_EV = {'cards': 0.4, 'card_step': 0.067, 'yellow': 1.6, 'sweep': (2.6, 3.3), 'scores_land': 3.3,
           'char_pop': 4.0, 'fade_out': (6.6, 8.0)}


# ------------------------------------------------------------------ render
def smooth_env(x, win_s):
    a = np.abs(x).max(axis=1); w = max(1, int(win_s * SR))
    c = np.cumsum(np.concatenate([[0], a])); m = (c[w:] - c[:-w]) / w
    return np.concatenate([m, np.full(len(a) - len(m), m[-1] if len(m) else 0)])

def read_wav_f32(path):
    """48 kHz mono 32-bit float WAV -> float array (what voice.py writes)."""
    b = open(path, 'rb').read(); i = 12; fmt = None
    while i + 8 <= len(b):
        cid, size = b[i:i + 4], struct.unpack('<I', b[i + 4:i + 8])[0]
        if cid == b'fmt ':
            fmt = struct.unpack('<HHIIHH', b[i + 8:i + 24])
        elif cid == b'data':
            assert fmt and fmt[0] in (3, 0xFFFE) and fmt[1] == 1 and fmt[2] == SR and fmt[5] == 32, f'{path}: need {SR} Hz mono float, got {fmt}'
            return np.frombuffer(b[i + 8:i + 8 + size], '<f4').astype(float)
        i += 8 + size + (size & 1)
    raise ValueError(f'{path}: no data chunk')

def voice_presence(v, n, look=0.06, hold=0.3, smooth=0.08):
    """0..1 curve that is 1 while the narrator speaks. It rises `look` s before a word and
    holds `hold` s after the last loud frame, so quiet word endings (the "v" of "dev")
    are not uncovered while they are still sounding."""
    hop = int(0.01 * SR); m = -(-n // hop)
    a = np.zeros(m * hop); a[:min(n, len(v))] = np.abs(v[:n])
    rms = np.sqrt(np.mean(a.reshape(m, hop) ** 2, axis=1))
    on = (rms > rms.max() * 0.03).astype(float)
    L, H = int(look * 100), int(hold * 100)
    held = np.array([on[max(0, i - H):i + L + 1].max() for i in range(m)])
    w = int(smooth * 100); held = np.convolve(held, np.ones(w) / w, 'same')
    return np.repeat(held, hop)[:n]

DUCK_DB = {'music': -8, 'drums': -8, 'keys': -6, 'fx': -5, 'ui': -2}   # ~8 dB under the voice

def render(mode, vo=None):
    M = Mix(mode)
    (score if mode == 'full' else score_loop)(M)
    n = M.n
    if vo is not None:                                       # make room for the narrator
        pres = voice_presence(vo, n)[:, None]
        for k, d in DUCK_DB.items():
            M.b[k] = M.b[k] * (1 - pres * (1 - 10 ** (d / 20)))
        M.big *= 1 - pres * 0.5; M.room *= 1 - pres * 0.4
    # sidechain: pump music on every kick (flow + chorus)
    duck = np.ones(n)
    for kt in M.kicks:
        i = int(kt * SR); L = int(0.3 * SR); tt = np.arange(min(L, n - i)) / SR
        duck[i:i + len(tt)] = np.minimum(duck[i:i + len(tt)], 1 - 0.45 * np.exp(-tt / 0.09))
    music = M.b['music'] * 1.3 * duck[:, None]
    # keep the music out of the way of the interface sounds
    fg = smooth_env(M.b['ui'] + M.b['keys'], 0.06)
    music *= (1 - 0.3 * np.clip(fg / 0.06, 0, 1))[:, None]
    dry = music + M.b['drums'] * 0.62 + M.b['ui'] + M.b['keys'] + M.b['fx']
    wet = convolve(M.big + music * 0.15, make_ir(2.2, 501, 0.02, 1100)) * 0.22 \
        + convolve(M.room, make_ir(0.45, 601, 0.006, 2500)) * 0.18
    x = dry + wet
    x = fgain(x, hp(28, 2))                                  # nothing useless below 28 Hz
    lo = 10 ** (-5 / 20)                                     # tame the sub, lift presence a touch
    x = fgain(x, lambda f: (lo + (1 - lo) * f ** 2 / (f ** 2 + 130 ** 2))
              * (1 + 0.3 * np.exp(-0.5 * (np.log2(np.maximum(f, 1) / 3800) / 0.9) ** 2)))
    if vo is not None:                                       # dynamic EQ: clear the consonant band under speech
        x = x - fgain(x, bp(2000, 8000)) * pres * (1 - 10 ** (VO_CLEAR_DB / 20))
    end = int(M.dur * SR)
    if mode == 'loop':                                       # wrap the tail so the loop is seamless
        tail = x[end:end + int(3.5 * SR)]
        if vo is not None:                                   # the wrapped tail lands under the opening line
            tail = tail * (1 - pres[:len(tail)] * (1 - 10 ** (DUCK_DB['music'] / 20)))
        x = x[:end].copy(); x[:len(tail)] += tail
    else:
        x = x[:end]
        f = int(0.08 * SR); x[-f:] *= np.linspace(1, 0, f)[:, None]
    for a, b in M.gates:                                     # hard stop: the whole bed, reverb tails too
        i, j = int(a * SR), int(b * SR); f = int(0.006 * SR)
        x[i - f:i] *= np.linspace(1, 0, f)[:, None]; x[i:j] = 0
    x = norm(x, 0.85)
    x = np.tanh(x * 1.25) / np.tanh(1.25)                    # gentle glue
    if vo is not None:                                       # voice goes on top of the glued bed, dry and centred
        v = np.zeros(len(x)); v[:min(len(x), len(vo))] = vo[:len(x)]
        v = fgain(v[:, None], lambda f: hp(85, 2)(f) * (1 + 0.25 * np.exp(-0.5 * (np.log2(np.maximum(f, 1) / 3000) / 0.8) ** 2)))[:, 0]
        v = norm(v[:, None], 1.0)[:, 0]
        v = np.tanh(v * 1.8) / np.tanh(1.8)                  # tame the peaks so loudness normalisation stays linear
        x = x * VO_BED + v[:, None] * VO_GAIN
    return norm(x, 0.89)

VO_BED, VO_GAIN = 0.55, 0.8
VO_CLEAR_DB = -9   # extra cut, 2-8 kHz, while the narrator speaks: hats, hiss and reverb hid the "v" of "dev"

def write_wav(path, x):
    x = np.asarray(x, np.float32); n, ch = x.shape
    data = x.tobytes()
    with open(path, 'wb') as f:
        f.write(b'RIFF' + struct.pack('<I', 36 + len(data)) + b'WAVE')
        f.write(b'fmt ' + struct.pack('<IHHIIHH', 16, 3, ch, SR, SR * ch * 4, ch * 4, 32))
        f.write(b'data' + struct.pack('<I', len(data)) + data)

if __name__ == '__main__':
    mode, out = sys.argv[1], sys.argv[2]
    if mode == 'events':
        import json
        json.dump({'full': EV, 'loop': LOOP_EV, 'duration': DUR, 'loop_duration': LOOP_DUR}, open(out, 'w'), indent=1)
        print('wrote', out); sys.exit()
    write_wav(out, render(mode, read_wav_f32(sys.argv[3]) if len(sys.argv) > 3 else None))
    print('wrote', out)
