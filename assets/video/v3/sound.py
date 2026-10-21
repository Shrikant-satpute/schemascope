#!/usr/bin/env python3
"""SchemaScope explainer v3 soundtrack ("Compare databases.", 65.4 s, white theme).

Calmer and warmer than v2: 80 bpm, soft electric piano and wide pads in C (F C Am G),
a sub bass instead of a running bass line, a soft heartbeat kick only from the
walkthrough on, no claps, a light sidechain. It builds gently into the walkthrough,
rises softly at Compare and resolves on a C maj9 chord at 100%.

Every sound is synthesised here from sine waves and noise - no samples, no stock
music - so the audio can be published under the repo's licence. Event times live in
EV below: they come from v3/script.json's scene windows and the narration's measured
phrase onsets, and v3/scene.html should place its visual beats on them (or EV moves
with the scene).

  python sound.py full snd/full.wav snd/vo.wav   # 65.4 s with the voiceover; the bed ducks ~8 dB under it
  python sound.py full snd/bed.wav               # music + SFX only
  python sound.py loop snd/loop.wav              # 6 s hook-loop bed, tail wrapped so it loops (no VO)
  python sound.py events snd/events.json         # EV and LOOP_EV, for syncing scene.html

Needs numpy only. A voiceover must be a 48 kHz mono float WAV already placed on the
same timeline (voice.py writes one). Output is 48 kHz stereo float WAV, not loudness-normalised; the
delivery step does that (see README.md).
"""
import sys, struct
import numpy as np

SR = 48000


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
        self.gates = []          # (start, end): whole bed silenced (unused in v3)

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


# ------------------------------------------------------------------ v3 instruments (calm, warm)
def epiano(m, d=1.8, vel=1.0, seed=0):
    """Soft electric piano: two-operator FM with a decaying index, a faint tine and slow tremolo."""
    f = hz(m); t = T(d)
    idx = (0.9 + 0.6 * vel) * ex(t, 0.35)
    x = np.sin(2 * np.pi * f * t + idx * np.sin(2 * np.pi * f * t))
    x += 0.12 * vel * np.sin(2 * np.pi * f * 7.0 * t) * ex(t, 0.04)
    x += 0.25 * np.sin(np.pi * f * t) * ex(t, 1.2)                 # body an octave down
    env = attack(t, 0.004) * ex(t, 0.9 + 0.4 * (60 - min(m, 84)) / 24) * (1 - 0.06 * np.sin(2 * np.pi * 4.2 * t))
    tail = int(0.05 * SR); env[-tail:] *= np.linspace(1, 0, tail)
    return x * env * vel

def warm_pad(m, d, att=0.9, rel=1.4, cut=1600):
    return fgain(pad_note(m, d, rel, att, 3200, detune=9.0), lp(cut, 2))

def sub(m, d, att=0.06, rel=0.5):
    t = T(d + rel)
    env = attack(t, att) * np.where(t < d, 1.0, ex(np.maximum(t - d, 0), rel / 3))
    return (np.sin(2 * np.pi * hz(m) * t) + 0.18 * np.sin(4 * np.pi * hz(m) * t)) * env

def shaker(seed=0, g=1.0):
    t = T(0.09)
    return fgain(noise(0.09, seed), bp(4500, 12000)) * attack(t, 0.012) * ex(t, 0.025) * g

def soft_hit():
    """The one 'bass hit' of the hook and the close: a round, low thump, no click."""
    return fgain(kick(110, 40, 0.5, 0.08), lp(700))


# cached one-shots (variation comes from per-hit gain and seed lists)
KICK = fgain(kick(95, 44, 0.24, 0.04), lp(900, 2))      # soft: a heartbeat, not a club kick
HIT = soft_hit()
CLICKS = [mouse_click(s) for s in range(8)]
KEYS = [key(s) for s in range(16)]
SHAKE = [shaker(s) for s in range(8)]

# Warm, open voicings in C: maj7 / add9 colours, nothing minor-heavy.
CHORDS = {
    'F': (41, [57, 60, 64, 67]), 'C': (36, [55, 60, 64, 74]), 'Am': (45, [55, 60, 64, 71]),
    'G': (43, [59, 62, 67, 69]), 'Dm': (38, [57, 60, 65, 69]), 'Gsus': (43, [60, 62, 67, 74]),
    'Esus': (40, [57, 59, 64, 69]),
}
PENT = [72, 74, 76, 79, 81, 84, 86, 88, 91, 93]

DUR, LOOP_DUR = 65.4, 6.0
BAR = 3.0           # 80 bpm, 4/4: calmer than v2's 120

# Event times: v3/script.json scene windows + the narration's measured phrase onsets
# (snd/plan.json, snd/vo-words.json). v3/scene.html should put its visual beats on
# these; `python sound.py events snd/events.json` writes them out.
EV = {
    # hook 0-4.4 (the character's only scene; he slides out at 4.0-4.4)
    'yellow_hook': 0.05, 'pills': (1.95, 2.25, 2.9), 'fan': 3.2, 'fan_step': 0.04, 'char_pop': 0.9, 'char_out': 4.0,
    # story 4.4-15
    'story': 4.4, 'lines': 5.3, 'line_step': 0.09, 'tags': (9.4, 10.5, 12.4), 'support_folder': 13.0,
    # solution 15-18.4
    'riser': (13.8, 15.0), 'app_in': 15.0,
    # walkthrough: pill k appears at its step start
    'step': {1: 18.4, 2: 21.4, 3: 25.4, 4: 30.0, 5: 33.4, 6: 41.2, 7: 45.2, 8: 48.8},
    'src_click': 18.85, 'picker': 19.0, 'use_click': 20.3,
    'add_click': 21.85, 'chips': 21.95, 'chip_step': 0.33, 'n_chips': 9,
    'compare_click': 25.95, 'reading': (26.0, 27.1), 'dashboard': 27.1,
    'underline': (31.0, 32.8), 'dissolve': 31.7,
    'ring_c04': 35.0, 'slide_c03': 37.4, 'label_c03': 37.8,
    'diff_click': 41.55, 'diff_open': 41.7, 'wash': 43.4,
    'create_click': 45.55, 'dialog7': 45.95, 'written': 45.95, 'green_line': 47.6,   # green ring draws at 47.6 (scene.html)
    'new_tag': 48.95, 'switch_click': 50.6, 'wm_dialog': 50.7, 'enable_click': 51.3, 'amber': 51.35,
    'apply_dialog': 52.0, 'type': 52.1, 'type_step': 0.075, 'n_letters': 15, 'apply_enabled': 53.25,
    'apply_click': 54.6, 'run': (54.62, 54.84), 'n_files': 34, 'applied': 54.9, 'hundred': 56.0,
    'protected': 57.6,
    # close 59.6-65.4
    'close': 59.6, 'yellow_close': 59.85, 'close_pills': 61.0, 'pill_step': 0.22, 'button': 61.8, 'end_chord': 62.6,
}


def chord(M, name, t0, d, g=0.05, cut=1600, att=0.9, rel=1.4, big=0.35, sub_g=0.10):
    root, notes = CHORDS[name]
    for m in notes:
        M.add('music', t0, warm_pad(m, d, att, rel, cut), g, big=big)
    if sub_g:
        M.add('music', t0, sub(root - 12 if root > 40 else root, d, rel=rel), sub_g)

def keys_bar(M, name, t0, d, g=0.05, pat=None, step=0.75, seed=0):
    """Electric piano on the beat grid; pat = chord-tone index per step (None = rest)."""
    _, notes = CHORDS[name]
    pat = pat or (0, None, 2, None)
    for k in range(int(d / step + 0.01)):
        i = pat[k % len(pat)]
        if i is None:
            continue
        m = notes[i % len(notes)] + 12 * (i // len(notes))
        M.add('music', t0 + k * step, epiano(m, 1.8, 0.8 + 0.2 * (k % 4 == 0), seed + k), g,
              p=0.25 * np.sin(1.3 * k + seed), big=0.3)

def pulse(M, a, b, kick_g=0.32, shake_g=0.0, R=None):
    """Light heartbeat: soft kick on 1 and 3, optional shaker on the off-beats."""
    for bt in np.arange(a, b - 1e-6, 0.75):
        k = int(round((bt - a) / 0.75))
        if kick_g and k % 2 == 0:
            M.kick(bt, kick_g)
        if shake_g:
            M.add('drums', bt + 0.375 + R.uniform(0, 0.006), SHAKE[R.integers(8)], shake_g, p=0.3)
            M.add('drums', bt + R.uniform(0, 0.006), SHAKE[R.integers(8)], shake_g * 0.55, p=-0.3)

def progression(M, prog, a, b, pad_g=0.05, cut=1700, keys_g=0.05, pat=None, seed=0):
    t0, i = a, 0
    while t0 < b - 1e-6:
        name = prog[i % len(prog)]; d = min(BAR, b - t0)
        chord(M, name, t0, d, pad_g, cut, att=0.6 if i else 0.9)
        if keys_g:
            keys_bar(M, name, t0, d, keys_g, pat, seed=seed + 10 * i)
        t0 += BAR; i += 1


# ------------------------------------------------------------------ score
def score(M):
    R = rng(303); E = EV; S = E['step']

    # ===== HOOK 0-4.4: one soft bass hit on the yellow bar, three light ticks, the bed starts
    y = E['yellow_hook']
    M.add('drums', y, HIT, 0.7)
    M.add('music', y, sub(29, 1.2, 0.005, 1.0), 0.22)
    for i, m in enumerate((65, 69, 72, 76)):
        M.add('music', y + i * 0.015, epiano(m, 2.6, 0.9, i), 0.07, p=-0.3 + 0.2 * i, big=0.5)
    M.add('fx', max(0.0, y - 0.12), whoosh(0.26, 600, 3500, 14), 0.08)
    chord(M, 'F', 0.2, 4.2, 0.035, 1300, att=1.6, rel=1.2, sub_g=0.06)
    for i, pt in enumerate(E['pills']):
        M.add('ui', pt, tick(hz(PENT[2 + 2 * i] + 12), 0.05, 0.005), 0.10, p=-0.3 + 0.3 * i, room=0.3)
        M.add('ui', pt + 0.005, tick(1300, 0.02, 0.002), 0.03)
    for i in range(8):                                                  # the pill fans into 8 client icons
        M.add('ui', E['fan'] + i * E['fan_step'], tick(hz(PENT[i] + 24), 0.04, 0.003), 0.035, p=-0.5 + 0.14 * i, room=0.3)
    M.add('ui', E['char_pop'], pop(79, 3), 0.12, room=0.2)
    M.add('fx', E['char_out'], whoosh(0.4, 1800, 600, 15, 0.8), 0.05)

    # ===== STORY 4.4-15: low, calm bed; one soft paper tick per question tag
    progression(M, ('Am', 'F', 'C', 'G'), E['story'], E['app_in'], 0.045, 1400, 0.035,
                pat=(0, None, None, None, 2, None, None, None), seed=10)
    for i in range(8):                                                  # source card -> 8 client lines
        M.add('ui', E['lines'] + i * E['line_step'], tick(hz(PENT[i] + 12), 0.04, 0.004), 0.025, p=-0.5 + 0.14 * i, room=0.3)
    for i, tg in enumerate(E['tags']):
        M.add('fx', tg - 0.02, paper(80 + i, 0.18), 0.05, p=-0.3 + 0.3 * i)
        M.add('ui', tg + 0.06, woodblock(1500 + 120 * i, i), 0.05, room=0.35)
    M.add('music', E['support_folder'], epiano(52, 2.2, 0.6, 9), 0.06, big=0.4)   # a low, questioning note
    a, b = E['riser']
    M.add('fx', a, riser(b - a, 300, 6000, 16), 0.09)

    # ===== SOLUTION 15-18.4: a gentle swell into the real app
    s = E['app_in']
    for i, m in enumerate((72, 76, 79, 84)):
        M.add('music', s + i * 0.03, bell(m, 2.6, 0.8), 0.03, p=-0.3 + 0.2 * i, big=0.6)
    chord(M, 'C', s, S[1] - s, 0.05, 2000, att=0.25, sub_g=0.1)
    keys_bar(M, 'C', s, S[1] - s, 0.04, (0, 1, 2, 3), 0.75, seed=40)

    # ===== WALKTHROUGH 18.4-48.8: F C Am G, a light pulse that builds step by step
    progression(M, ('F', 'C', 'Am', 'G'), S[1], S[8], 0.045, 1900, 0.045,
                pat=(0, 2, 1, 3, 2, None, 1, None), seed=100)
    pulse(M, S[1], S[2], 0.22, 0.0, R)
    pulse(M, S[2], S[3], 0.28, 0.035, R)
    pulse(M, S[3], E['compare_click'], 0.28, 0.04, R)
    pulse(M, E['dashboard'], S[8] - 0.05, 0.32, 0.045, R)
    for k, t0 in S.items():                                             # step pills
        M.add('ui', t0 + 0.05, pop(PENT[k % 6 + 1], 20 + k, 0.12), 0.08, room=0.25)
        M.add('ui', t0 + 0.05, tick(hz(PENT[k % 6 + 1] + 12), 0.04, 0.004), 0.06, room=0.25)
    # 1 pick the source
    M.add('ui', E['src_click'], CLICKS[0], 0.45, room=0.15)
    M.add('fx', E['picker'], whoosh(0.25, 800, 3000, 30), 0.04)
    M.add('ui', E['use_click'], CLICKS[1], 0.45, room=0.15)
    M.add('ui', E['use_click'] + 0.08, pop(76, 31), 0.08, room=0.2)
    # 2 add every target: nine quiet pops
    M.add('ui', E['add_click'], CLICKS[2], 0.45, room=0.15)
    for i in range(E['n_chips']):
        M.add('ui', E['chips'] + i * E['chip_step'], pop(PENT[i], 40 + i, 0.12), 0.09, p=-0.5 + 0.125 * i, room=0.2)
    # 3 compare: click, a soft rise while all ten read, a clean chime on the dashboard
    M.add('ui', E['compare_click'], CLICKS[3], 0.5, room=0.15)
    r0, r1 = E['reading']
    M.add('fx', r0, riser(r1 - r0, 400, 5000, 32, tone=False), 0.10)
    M.add('fx', r0 + 0.15, whoosh(r1 - r0 - 0.1, 500, 2800, 33, 1.0), 0.06)
    M.add('ui', r0 + 0.05, data_chatter(r1 - r0 - 0.15, 18, 34, 2500, 4500), 0.025, p=0.2)
    d = E['dashboard']
    M.add('ui', d, chime((84, 88, 91)), 0.09, big=0.45)
    for m in (60, 64, 67, 72, 76):                                      # the music opens up a little
        M.add('music', d, warm_pad(m, 2.4, 0.05, 1.4, 3200), 0.03, big=0.5)
    # 4 scores, worst first: nine soft ticks under the sweep
    u0, u1 = E['underline']
    for i in range(9):
        M.add('ui', u0 + (u1 - u0) * i / 8, tick(hz(PENT[i] + 12), 0.04, 0.004), 0.05, p=-0.6 + 0.15 * i, room=0.3)
    M.add('fx', E['dissolve'], whoosh(0.5, 300, 1800, 35, 1.0), 0.04)
    # 5 what is missing: a soft low tone as the red ring draws
    M.add('music', E['ring_c04'], fgain(epiano(48, 2.4, 0.7, 50), lp(1200)), 0.10, big=0.3)
    M.add('music', E['ring_c04'], sub(36, 0.9, 0.02, 0.8), 0.08)
    M.add('fx', E['slide_c03'], whoosh(0.5, 300, 2000, 36, 1.0), 0.05)
    M.add('music', E['label_c03'], fgain(epiano(50, 2.0, 0.6, 51), lp(1200)), 0.07, big=0.3)
    # 6 diff: a click and a soft page turn
    M.add('ui', E['diff_click'], CLICKS[4], 0.45, room=0.15)
    M.add('fx', E['diff_open'], paper(90, 0.32), 0.07)
    M.add('ui', E['wash'], tick(hz(88), 0.08, 0.01), 0.04, big=0.3)
    # 7 create the fix scripts: click, paper swish, a small chime on the green line
    M.add('ui', E['create_click'], CLICKS[5], 0.45, room=0.15)
    M.add('fx', E['dialog7'], paper(91, 0.3), 0.07)
    for i in range(5):                                                  # five numbered folders written
        M.add('ui', E['written'] + i * 0.08, tick(hz(PENT[i] + 12), 0.05, 0.004), 0.035, p=-0.4 + 0.2 * i, room=0.2)
    M.add('ui', E['green_line'], chime((88, 95)), 0.08, big=0.4)

    # ===== 8 WRITE MODE 48.8-54.4: lighter, a held sus under the dialog, a solid latch to amber
    w = S[8]; s8b = w + 5.6
    chord(M, 'Dm', w, E['wm_dialog'] - w + 0.1, 0.045, 1700, att=0.4)
    keys_bar(M, 'Dm', w, E['wm_dialog'] - w, 0.04, (0, None, 2, None), seed=200)
    pulse(M, w, E['wm_dialog'], 0.2, 0.0, R)
    M.add('ui', E['new_tag'], pop(84, 60, 0.12), 0.08, room=0.25)
    M.add('ui', E['switch_click'], CLICKS[6], 0.5, room=0.15)
    chord(M, 'Esus', E['wm_dialog'], E['amber'] - E['wm_dialog'] + 0.15, 0.045, 1500, att=0.15, rel=0.5)
    M.add('ui', E['wm_dialog'], pop(67, 61, 0.14), 0.09, room=0.2)
    M.add('ui', E['enable_click'], CLICKS[7], 0.5, room=0.15)
    M.add('ui', E['amber'], latch(), 0.45, room=0.3)
    M.add('fx', E['amber'] + 0.05, shimmer(0.8, 62, 3500, 8000), 0.12, big=0.3)
    chord(M, 'F', E['amber'], s8b - E['amber'], 0.045, 1800, att=0.3)
    keys_bar(M, 'F', E['amber'] + 0.35, 2.25, 0.035, (0, None, 2, None), seed=210)
    pulse(M, E['amber'] + 0.35, E['apply_click'] - 0.05, 0.2, 0.0, R)
    M.add('fx', E['apply_dialog'], whoosh(0.3, 500, 2600, 63), 0.05)
    for i in range(E['n_letters']):                                     # "SSDemo_Client05"
        M.add('keys', E['type'] + i * E['type_step'] + R.uniform(-0.008, 0.008), KEYS[i % 16], 0.26,
              p=R.uniform(-0.15, 0.15), room=0.2)
    M.add('ui', E['apply_enabled'], pop(76, 64, 0.12), 0.09, room=0.2)
    M.add('ui', E['apply_enabled'] + 0.07, pop(83, 65, 0.12), 0.07, room=0.2)

    # ===== 8 APPLY 54.4-59.6: tiny run ticks, a warm resolve at 100%, a dull thud on Protected
    M.add('ui', E['apply_click'], CLICKS[0], 0.5, room=0.15)
    chord(M, 'Gsus', s8b, E['hundred'] - s8b + 0.1, 0.045, 1900, att=0.3)
    f0, f1 = E['run']
    for k in range(E['n_files']):
        u = k / (E['n_files'] - 1)
        M.add('ui', f0 + (f1 - f0) * u, tick(hz(74 + int(round(14 * u))), 0.03, 0.003), 0.03, p=-0.4 + 0.8 * u, room=0.2)
    M.add('fx', f0 + 0.1, riser(E['hundred'] - f0 - 0.1, 300, 5000, 66, tone=False), 0.07)
    M.add('ui', E['applied'], tick(hz(91), 0.08, 0.01), 0.05, big=0.3)
    h = E['hundred']; pr = E['protected']
    for m in (48, 55, 60, 64, 67, 71, 74):                              # warm C maj9 resolve
        M.add('music', h, warm_pad(m, pr - h + 0.2, 0.08, 1.2, 2600), 0.045, big=0.55)
    M.add('music', h, sub(36, pr - h, 0.02, 0.6), 0.14)
    for i, m in enumerate((72, 76, 79, 84, 88)):
        M.add('music', h + i * 0.05, epiano(m, 2.4, 0.9, 70 + i), 0.06, p=-0.4 + 0.2 * i, big=0.5)
    M.add('ui', h, bell(84, 3.2, 1.0), 0.07, big=0.6)
    M.add('drums', h, HIT, 0.45)
    M.add('fx', h + 0.05, sparkle(0.9, 75, 14), 0.08, big=0.4)
    chord(M, 'Am', pr, E['close'] - pr + 0.1, 0.04, 1300, att=0.2, rel=0.8)
    M.add('ui', pr + 0.05, stamp(), 0.32, room=0.3)

    # ===== CLOSE 59.6-65.4: gentle swell, one soft hit on the yellow bar, resolve
    c = E['close']
    chord(M, 'F', c, 1.5, 0.05, 2200, att=0.3)
    chord(M, 'G', c + 1.5, E['end_chord'] - c - 1.5, 0.05, 2400, att=0.3)
    keys_bar(M, 'F', c, 1.5, 0.045, (0, 2), seed=300)
    keys_bar(M, 'G', c + 1.5, E['end_chord'] - c - 1.5, 0.045, (0, 2), seed=310)
    yc = E['yellow_close']
    M.add('drums', yc, HIT, 0.55)
    for i in range(5):
        M.add('ui', E['close_pills'] + i * E['pill_step'], pop(PENT[i + 2], 82 + i, 0.12), 0.07, p=-0.5 + 0.25 * i, room=0.2)
    M.add('ui', E['button'], chime((91, 96)), 0.06, big=0.4)
    ec = E['end_chord']; dd = DUR - ec
    for m in (48, 55, 60, 64, 67, 74):
        M.add('music', ec, warm_pad(m, dd, 0.3, 1.0, 2400), 0.05, big=0.55)
    M.add('music', ec, sub(36, dd, 0.05, 0.8), 0.13)
    for i, m in enumerate((72, 76, 79, 84)):
        M.add('music', ec + i * 0.06, epiano(m, 3.0, 0.8, 90 + i), 0.05, big=0.6)
    M.add('music', ec, bell(84, 3.0, 1.0), 0.03, big=0.6)


def score_loop(M):
    """6 s silent hook loop (no VO, no character): yellow bar, the tile row slides up, the
    nine scores land together, a blue sweep, then a fade back to the empty grid. The page
    autoplays it muted, so nothing here carries meaning; the tail wraps so it loops."""
    R = rng(7); E = LOOP_EV
    for name, t0 in (('F', 0.0), ('C', 1.5), ('Am', 3.0), ('G', 4.5)):
        chord(M, name, t0, 1.5, 0.04, 1700, att=0.3, rel=0.8, sub_g=0.07)
        keys_bar(M, name, t0, 1.5, 0.035, (0, 2), seed=int(t0 * 10))
    pulse(M, 0.0, 6.0, 0.22, 0.03, R)
    M.add('ui', E['yellow'], pop(79, 1, 0.12), 0.08, room=0.2)
    M.add('fx', E['tiles_up'], whoosh(0.4, 400, 2400, 2), 0.06)
    for i, m in enumerate((72, 76, 79, 84)):
        M.add('music', E['scores_land'] + i * 0.012, bell(m, 2.0, 0.8), 0.03, big=0.6)
    s0, s1 = E['sweep']
    for i in range(9):
        M.add('ui', s0 + (s1 - s0) * i / 8, tick(hz(PENT[i] + 12), 0.04, 0.004), 0.035, room=0.3)
    M.add('fx', E['fade_out'][0], whoosh(1.0, 2400, 400, 3, 1.0), 0.04)


LOOP_EV = {'yellow': 0.4, 'tiles_up': 1.2, 'scores_land': 2.0, 'sweep': (2.6, 3.6), 'fade_out': (4.8, 6.0)}


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

DUCK_DB = {'music': -8, 'drums': -8, 'keys': -1, 'fx': -5, 'ui': -1}   # ~8 dB under the voice; clicks and typing stay clearly audible

def render(mode, vo=None):
    M = Mix(mode)
    (score if mode == 'full' else score_loop)(M)
    n = M.n
    if vo is not None:                                       # make room for the narrator
        pres = voice_presence(vo, n)[:, None]
        for k, d in DUCK_DB.items():
            M.b[k] = M.b[k] * (1 - pres * (1 - 10 ** (d / 20)))
        M.big *= 1 - pres * 0.5; M.room *= 1 - pres * 0.4
    # sidechain: a light breath on every kick (v2 pumped 0.45; v3 is calm)
    duck = np.ones(n)
    for kt in M.kicks:
        i = int(kt * SR); L = int(0.3 * SR); tt = np.arange(min(L, n - i)) / SR
        duck[i:i + len(tt)] = np.minimum(duck[i:i + len(tt)], 1 - 0.15 * np.exp(-tt / 0.12))
    music = M.b['music'] * 1.3 * duck[:, None]
    # keep the music out of the way of the interface sounds
    fg = smooth_env(M.b['ui'] + M.b['keys'], 0.06)
    music *= (1 - 0.3 * np.clip(fg / 0.06, 0, 1))[:, None]
    dry = music + M.b['drums'] * 0.62 + M.b['ui'] * UI_GAIN + M.b['keys'] * KEYS_GAIN + M.b['fx']
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
UI_GAIN, KEYS_GAIN = 1.8, 2.6   # mouse clicks and keyboard taps, loud enough to hear clearly under the voice
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
