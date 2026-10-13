#!/usr/bin/env python3
"""SchemaScope explainer soundtrack: music bed, UI clicks, keyboard and transitions.

Every sound is synthesised here from sine waves and noise - no samples, no stock
music - so the audio can be published under the repo's licence. Event times are
taken from scene.html (CUR clicks, chipT, words() reveals, scene windows) and have to
move with it if the timeline changes.

  python sound.py full out/explainer.wav   # 55 s, matches the explainer
  python sound.py loop out/hook.wav        # 8 s: 0-4 s + 11-15 s, tail wrapped so it loops
  python sound.py full out.wav vo.wav      # with a voiceover (voice.py); the bed ducks under it

Needs numpy only. A voiceover must be a 48 kHz mono float WAV already placed on the
same timeline (voice.py writes one). Output is 48 kHz stereo float WAV, not loudness-normalised; the
delivery step does that (see README.md).
"""
import sys, struct
import numpy as np

SR = 48000
BEAT = 0.5          # 120 bpm, beat grid starts at t=0 so 11.0 and 15.0 land on beats


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


# ------------------------------------------------------------------ mixer
BUSES = ('music', 'drums', 'ui', 'keys', 'fx')

class Mix:
    def __init__(self, mode):
        self.mode = mode
        self.dur = 55.0 if mode == 'full' else 8.0
        self.n = int((self.dur + 4.0) * SR)
        self.b = {k: np.zeros((self.n, 2)) for k in BUSES}
        self.big = np.zeros((self.n, 2)); self.room = np.zeros((self.n, 2))
        self.kicks = []

    def tmap(self, t, only):
        if self.mode == 'full':
            return None if only == 'loop' else t
        if only == 'full':
            return None
        if 0.0 <= t < 4.0:
            return t
        if 11.0 <= t < 15.0:
            return t - 7.0
        return None

    def add(self, bus, t, sig, g=1.0, p=0.0, big=0.0, room=0.0, only=None):
        t = self.tmap(t, only)
        if t is None:
            return
        s = pan(np.asarray(sig, float), p) * g
        i = int(round(t * SR)); j = min(self.n, i + len(s))
        if i >= self.n:
            return
        self.b[bus][i:j] += s[:j - i]
        if big:
            self.big[i:j] += s[:j - i] * big
        if room:
            self.room[i:j] += s[:j - i] * room

    def kick(self, t, g=1.0, only=None):
        if self.tmap(t, only) is not None:
            self.kicks.append(self.tmap(t, only))
        self.add('drums', t, KICK, g, only=only)


# cached one-shots (variation comes from per-hit gain and seed lists)
KICK = kick()
SNARE = [snare(seed=s) for s in range(4)]
CLAP = clap()
HATS = [hat(seed=s) for s in range(6)]
OHAT = hat(True, 9)
CLICKS = [mouse_click(s) for s in range(6)]
KEYS = [key(s) for s in range(16)]
SPACE = key(77, 1.0, space=True)

CHORDS = {
    'Am': (45, [57, 60, 64]), 'F': (41, [57, 60, 65]), 'C': (48, [55, 60, 64]),
    'G': (43, [55, 59, 62]), 'Dm': (38, [57, 62, 65]), 'E': (40, [56, 59, 64]),
}


# ------------------------------------------------------------------ score
def score(M):
    R = rng(99)

    # ===== HOOK 0-4: attention hit, heartbeat, drone, drift alerts, typed question
    M.add('fx', 0.0, impact(0.75, 40), 0.85, big=0.35)
    M.add('fx', 0.02, glitch(21), 0.32, p=-0.2, room=0.2)
    t = T(4.4)
    drone = (np.sin(2 * np.pi * 55 * t) * 0.8 + fgain(saw(110, 4.4, 900) + saw(82.41, 4.4, 900), lp(320)) * 0.5)
    drone *= attack(t, 0.25) * (1 - 0.18 * (0.5 + 0.5 * np.sin(2 * np.pi * 0.5 * t)))
    drone *= np.where(t < 4.0, 1, ex(np.maximum(t - 4.0, 0), 0.08))
    M.add('music', 0.0, drone, 0.30, big=0.15)
    air = fgain(noise(4.2, 50), bp(3000, 12000)) * attack(T(4.2), 1.5) * 0.03
    M.add('fx', 0.0, air, 1.0)
    for b in (1.0, 2.0, 3.0):                             # lub-dub
        M.add('drums', b, fgain(kick(90, 40, 0.18, 0.0), lp(400)), 0.55)
        M.add('drums', b + 0.2, fgain(kick(80, 38, 0.15, 0.0), lp(380)), 0.35)
    for i in range(16):                                   # ticking 16ths 2.0-4.0, rising
        M.add('drums', 2.0 + i * 0.125, HATS[i % 6], 0.06 + 0.12 * i / 15, p=0.25 * (-1) ** i)
    reds = [0.418, 0.435, 0.49, 0.502, 0.658, 0.669, 0.837, 0.866, 0.96, 1.087, 1.102,
            1.106, 1.441, 1.643, 1.81, 2.474, 2.527, 2.707, 2.94]
    for i, rt in enumerate(reds):
        M.add('ui', rt, alert(R.choice([76, 79, 81, 84]), i, two=False), 0.05, p=R.uniform(-0.8, 0.8), room=0.3)
    M.add('fx', 2.35, riser(1.65, 250, 9000, 12), 0.22, big=0.1)
    for i in range(7):                                    # "Is production still the same as dev?"
        M.add('keys', 0.12 + i * 0.11 + R.uniform(-0.01, 0.01), KEYS[i], 0.55, p=R.uniform(-0.15, 0.15), room=0.25)
    M.add('keys', 0.12 + 7 * 0.11 + 0.04, SPACE, 0.5, room=0.25)
    for i in range(6):                                    # "Most teams can't say for sure."
        M.add('keys', 2.45 + i * 0.07 + R.uniform(-0.006, 0.006), KEYS[(i + 7) % 16], 0.38, p=R.uniform(-0.15, 0.15), room=0.25)
    M.add('fx', 3.85, whoosh(0.5, 300, 2600, 14), 0.25, only='full')

    # ===== PROBLEM 4-11: tense pulse, drift alerts, ticking clock, build, drop
    prog = [('Am', 4.0, 2.0), ('F', 6.0, 2.0), ('Dm', 8.0, 2.0), ('E', 10.0, 0.92)]
    for name, t0, d in prog:
        root, notes = CHORDS[name]
        for m in notes:
            M.add('music', t0, fgain(pad_note(m, d, 0.25, 0.35, 2200), lp(1100)), 0.11, big=0.25, only='full')
        for k in range(int(d / 0.25 + 0.01)):
            M.add('music', t0 + k * 0.25, bass_note(root - 12 if root > 40 else root, 0.2, 520), 0.20, only='full')
    for b in np.arange(4.0, 10.0, BEAT):
        M.kick(b, 0.7, only='full')
        M.add('drums', b + 0.25, HATS[int(b * 4) % 6], 0.10, p=0.3, only='full')
    for i in range(6):                                    # drift badges pop red
        M.add('ui', 5.4 + i * 0.28 + 0.05, alert(83 - (i % 3) * 2, 100 + i), 0.09, p=-0.5 + 0.2 * i, room=0.3, only='full')
    M.add('ui', 7.7, pop(72, 3), 0.204, only='full')        # clock appears
    for k, tt in enumerate(np.arange(7.8, 10.75, 0.5)):    # tick / tock
        M.add('ui', tt, woodblock(2100 if k % 2 == 0 else 1500, k), 0.13, p=0.35, room=0.35, only='full')
    for i, tt in enumerate((8.0, 8.9, 9.8)):               # one pair at a time
        M.add('ui', tt, pluck(64 - i, 0.5, 0.7, i), 0.10, p=0.4, big=0.2, only='full')
    for i in range(16):                                    # snare roll 10.0-10.9
        tt = 10.0 + i * (0.9 / 16)
        M.add('drums', tt, SNARE[i % 4], 0.05 + 0.25 * (i / 15) ** 1.5, p=0.1 * (-1) ** i, big=0.08, only='full')
    M.add('fx', 9.9, riser(1.0, 300, 10000, 15), 0.35, big=0.1, only='full')

    # ===== REVEAL 11-15: impact, bells, logo bars, wordmark, promise
    M.add('fx', 11.0, impact(1.0, 41), 0.72, big=0.5)
    for i, m in enumerate((72, 76, 79, 84)):
        M.add('music', 11.0 + i * 0.03, bell(m, 3.5, 1.0), 0.07, p=-0.4 + 0.27 * i, big=0.6)
    for m in (55, 60, 64, 67):
        M.add('music', 11.0, fgain(pad_note(m, 3.6, 0.9, 0.9, 4200), lp(2600)), 0.09, big=0.4)
    M.add('music', 11.0, fgain(saw(65.41, 4.0, 400) + np.sin(2 * np.pi * 65.41 * T(4.0)), lp(180)) * attack(T(4.0), 0.4) * np.minimum(1, (4.0 - T(4.0)) / 0.6), 0.22)
    for i, (tt, m) in enumerate(((11.35, 76), (11.45, 79), (11.55, 84))):
        M.add('ui', tt, pop(m, i), 0.272, p=-0.3 + 0.3 * i, big=0.3)
    M.add('fx', 12.0, swept_noise(1.0, 2000, 9000, 1.0, 16) * np.sin(np.pi * T(1.0)) ** 2, 0.05, big=0.4)
    M.add('fx', 12.15, whoosh(0.55, 900, 6000, 17, 0.8), 0.16, big=0.2)
    for i in range(14):                                   # promise line, light keys
        M.add('keys', 12.9 + i * 0.07 + R.uniform(-0.006, 0.006), KEYS[i % 16], 0.28, p=R.uniform(-0.2, 0.2), room=0.3)
    M.add('fx', 14.45, riser(0.45, 600, 7000, 18, tone=False), 0.18)
    M.add('fx', 14.85, whoosh(0.55, 400, 3600, 19), 0.24)

    # ===== FLOW 15-44: groove under the product walkthrough
    seq = []
    t0 = 15.0
    while t0 < 44.0 - 1e-6:
        for name in ('Am', 'F', 'C', 'G'):
            if t0 >= 44.0 - 1e-6:
                break
            seq.append((name, t0, min(2.0, 44.0 - t0))); t0 += 2.0
    for name, c0, d in seq:
        root, notes = CHORDS[name]
        calm = 30.0 <= c0 < 36.0
        cut = 1500 if calm else (2400 if c0 < 25.3 else 3200)
        for m in notes:
            M.add('music', c0, fgain(pad_note(m, d, 0.6, 0.25, 4200), lp(cut)), 0.075, big=0.3, only='full')
        for k in range(int(d / 0.25 + 0.01)):
            M.add('music', c0 + k * 0.25, bass_note(root - 12 if root > 40 else root, 0.2, 650 if not calm else 450), 0.16, only='full')
        tones = [n + 12 for n in notes] + [notes[0] + 24]
        pat = [0, 1, 2, 3, 2, 1, 2, 3]
        for k in range(int(d / 0.125 + 0.01)):
            tt = c0 + k * 0.125
            lvl = 0.035 if tt < 25.3 else 0.05
            if calm:
                lvl = 0.025
            M.add('music', tt, pluck(tones[pat[k % 8]], 0.45, 0.8, k), lvl * (1.15 if k % 4 == 0 else 1.0),
                  p=0.35 * np.sin(k * 0.7), big=0.25, only='full')
    for b in np.arange(15.0, 44.0, BEAT):
        calm = 30.0 <= b < 36.0
        on_bar = abs((b - 15.0) % 2.0) < 1e-6
        if not calm or on_bar:
            M.kick(b, 0.62 if b < 25.3 else 0.72, only='full')
        beat_in_bar = int(round((b - 15.0) / BEAT)) % 4
        if b >= 25.3 and not calm and beat_in_bar in (1, 3):
            M.add('drums', b, CLAP, 0.16, big=0.12, only='full')
        sub = 0.25 if b < 25.3 or calm else 0.125
        for s in np.arange(0, BEAT - 1e-6, sub):
            g = (0.07 if s > 0 else 0.045) * (0.6 if calm else 1.0)
            M.add('drums', b + s + R.uniform(0, 0.004), HATS[R.integers(6)], g, p=0.3, only='full')
    M.add('drums', 25.3, OHAT, 0.12, p=-0.2, big=0.2, only='full')

    # camera moves: soft air
    for a, bb in ((15.9, 16.6), (18.6, 19.4), (22.7, 23.5), (27.8, 28.6), (31.6, 32.4), (35.6, 36.4), (39.9, 40.9)):
        M.add('fx', a, whoosh(bb - a + 0.2, 250, 1800, int(a * 10), 1.0), 0.07, only='full')

    # UI: every cursor click from CUR
    for i, ct in enumerate((16.1, 17.2, 18.0, 19.5, 20.15, 20.8, 21.45, 23.4, 29.9, 36.3, 37.55, 40.2)):
        M.add('ui', ct, CLICKS[i % 6], 0.62, p=-0.1 + 0.2 * ((i * 37) % 7) / 6, room=0.18, only='full')
    M.add('ui', 16.2, pop(67, 1), 0.170, room=0.2, only='full')                         # picker opens
    M.add('ui', 18.06, pluck(76, 0.4, 0.9, 5), 0.09, big=0.15, only='full')            # source chosen
    for i, (ct, m) in enumerate(zip((19.5, 20.15, 20.8, 21.45), (72, 74, 76, 79))):     # target chips
        M.add('ui', ct + 0.03, pop(m, i), 0.221, p=-0.3 + 0.2 * i, room=0.2, only='full')
    # compare: engine, chatter, riser, success
    M.add('fx', 23.45, swept_noise(1.75, 300, 2600, 1.2, 33) * np.sin(np.pi * np.clip(T(1.75) / 1.75, 0, 1)) ** 0.7, 0.06, only='full')
    M.add('ui', 23.6, data_chatter(1.4, 34, 35), 0.07, p=0.2, only='full')
    M.add('fx', 23.6, riser(1.65, 300, 7000, 36), 0.14, only='full')
    M.add('ui', 25.3, chime((84, 88, 91)), 0.242, big=0.35, only='full')
    for i, m in enumerate((72, 76, 79, 84)):                                          # tiles land
        M.add('ui', 25.4 + i * 0.12, pop(m, 10 + i), 0.153, p=-0.45 + 0.3 * i, room=0.2, only='full')
    for i in range(4):                                                                 # objects-by-type bars
        M.add('ui', 26.2 + i * 0.1, tick(2600 + 300 * i), 0.110, room=0.2, only='full')
    M.add('fx', 26.6, whoosh(0.4, 600, 3200, 37), 0.07, only='full')                    # matrix in
    pent = [84, 86, 88, 91]
    for ri in range(12):
        for ci in range(4):
            M.add('ui', 27.5 + ri * 0.07 + ci * 0.11, tick(hz(pent[ci] + 12 * (ri % 2)), 0.066, 0.004), 0.022,
                  p=-0.6 + 0.4 * ci, room=0.25, only='full')
    M.add('fx', 30.3, whoosh(0.45, 400, 2800, 38), 0.10, only='full')                   # diff panel
    M.add('fx', 30.7, swept_noise(1.2, 3000, 9000, 0.8, 39) * np.sin(np.pi * T(1.2) / 1.2) ** 2, 0.025, big=0.3, only='full')
    last = -1                                                                          # +0 -> +18 counter
    for ms in range(0, 1501):
        tt = 31.0 + ms / 1000; u = min(1, (tt - 31.0) / 1.5); v = round(18 * (1 - (1 - u) ** 3))
        if v != last and v > 0:
            M.add('ui', tt, tick(3600, 0.02, 0.0025), 0.066, p=0.4, only='full')
        last = v
    M.add('ui', 33.6, pluck(88, 0.5, 1.0, 40), 0.07, big=0.3, only='full')             # matches highlighted
    # scripts dialog
    M.add('fx', 36.35, whoosh(0.35, 500, 3000, 41), 0.10, only='full')
    M.add('ui', 36.42, pop(64, 4), 0.170, room=0.2, only='full')
    for i in range(9):
        M.add('ui', 36.55 + i * 0.05, tick(hz(72 + [0, 2, 4, 7, 9, 12, 14, 16, 19][i]), 0.066, 0.004), 0.035, room=0.2, only='full')
    M.add('fx', 37.6, swept_noise(0.95, 400, 4000, 1.0, 42) * np.sin(np.pi * np.clip(T(0.95) / 0.95, 0, 1)), 0.07, only='full')
    M.add('ui', 37.62, data_chatter(0.9, 40, 43, 2600, 6000), 0.06, only='full')
    M.add('ui', 38.85, chime((84, 91, 96)), 0.264, big=0.4, only='full')                  # checked offline
    # share
    M.add('ui', 40.35, bell(88, 1.4, 0.6), 0.128, big=0.3, only='full')  # link copied
    M.add('ui', 40.35, pluck(81, 0.5, 1.0, 44), 0.08, big=0.3, only='full')
    M.add('fx', 40.85, whoosh(0.7, 300, 3000, 45), 0.18, only='full')                   # browser slides in
    M.add('fx', 41.2, swept_noise(0.7, 800, 8000, 0.6, 46) * np.sin(np.pi * T(0.7) / 0.7) ** 2, 0.05, big=0.3, only='full')
    for i in range(9):
        M.add('ui', 41.4 + i * 0.05, tick(hz(79 + (i % 4) * 2), 0.066, 0.004), 0.02, room=0.2, only='full')
    for i, m in enumerate((76, 79, 83)):                                              # teammates join
        M.add('ui', 42.2 + i * 0.18, pop(m, 20 + i), 0.170, p=-0.3 + 0.3 * i, room=0.2, only='full')
    M.add('fx', 43.55, riser(0.45, 400, 8000, 47, tone=False), 0.18, only='full')

    # ===== BENEFITS 44-50: chorus lift, cards, counter
    for name, c0 in (('C', 44.0), ('G', 46.0), ('Am', 48.0), ('F', 49.0)):
        root, notes = CHORDS[name]; d = 2.0 if c0 < 48 else 1.0
        for m in notes + [notes[-1] + 12]:
            M.add('music', c0, fgain(pad_note(m, d, 0.5, 0.2, 5200), lp(4200)), 0.07, big=0.35, only='full')
        for k in range(int(d / 0.25 + 0.01)):
            M.add('music', c0 + k * 0.25, bass_note(root - 12 if root > 40 else root, 0.2, 800), 0.17, only='full')
        tones = [n + 24 for n in notes]
        for k in range(int(d / 0.125 + 0.01)):
            M.add('music', c0 + k * 0.125, pluck(tones[[0, 1, 2, 1][k % 4]], 0.4, 1.0, 60 + k), 0.04,
                  p=0.4 * np.sin(k), big=0.3, only='full')
    for b in np.arange(44.0, 50.0, BEAT):
        M.kick(b, 0.75, only='full')
        if int(round((b - 44.0) / BEAT)) % 4 in (1, 3):
            M.add('drums', b, CLAP, 0.17, big=0.12, only='full')
        for s in (0.0, 0.125, 0.25, 0.375):
            M.add('drums', b + s, HATS[R.integers(6)], 0.07 if s else 0.05, p=0.3, only='full')
    M.add('fx', 44.0, whoosh(0.5, 300, 3000, 48), 0.18, only='full')
    M.add('drums', 44.0, OHAT, 0.14, big=0.25, only='full')
    for i, m in enumerate((72, 76, 79, 84)):
        M.add('ui', 44.2 + i * 0.22, pop(m, 30 + i), 0.221, p=-0.45 + 0.3 * i, room=0.2, only='full')
    last = -1                                                                          # 0 -> 806 ms counter
    for ms in range(0, 1301):
        tt = 44.3 + ms / 1000; u = min(1, (tt - 44.3) / 1.3); v = int(806 * (1 - (1 - u) ** 3) // 40)
        if v != last and v > 0:
            M.add('ui', tt, tick(3000, 0.02, 0.0025), 0.077, p=-0.5, only='full')
        last = v
    M.add('fx', 49.55, riser(0.55, 300, 9000, 49), 0.25, only='full')

    # ===== CTA 50-55: land, type the line, button, url, resolve
    M.add('fx', 50.1, impact(0.6, 42), 0.6, big=0.45, only='full')
    for i, m in enumerate((72, 76, 79, 84, 88)):
        M.add('music', 50.1 + i * 0.04, bell(m, 4.2, 1.2), 0.05, p=-0.4 + 0.2 * i, big=0.6, only='full')
    for m in (48, 55, 60, 64, 67):
        M.add('music', 50.1, fgain(pad_note(m, 3.4, 1.3, 0.25, 4200), lp(2800)), 0.075, big=0.45, only='full')
    M.add('music', 50.1, fgain(saw(65.41, 4.2, 300) + np.sin(2 * np.pi * 65.41 * T(4.2)), lp(160)) * attack(T(4.2), 0.05) * ex(T(4.2), 2.0), 0.2, only='full')
    for i in range(7):
        M.add('keys', 50.45 + i * 0.08 + R.uniform(-0.008, 0.008), KEYS[(i + 3) % 16], 0.42, p=R.uniform(-0.15, 0.15), room=0.25, only='full')
    M.add('keys', 50.45 + 7 * 0.08 + 0.05, SPACE, 0.4, room=0.25, only='full')
    M.add('ui', 51.45, pop(79, 50), 0.238, big=0.25, only='full')                         # button
    M.add('ui', 51.5, chime((91, 96)), 0.132, big=0.4, only='full')
    for i in range(10):                                                                # url appears
        M.add('keys', 51.9 + i * 0.05 + R.uniform(-0.006, 0.006), KEYS[(i + 5) % 16], 0.26, p=R.uniform(-0.2, 0.2), room=0.25, only='full')


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

DUCK_DB = {'music': -10, 'drums': -9, 'keys': -9, 'fx': -5, 'ui': -2}

def render(mode, vo=None):
    M = Mix(mode)
    score(M)
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
    write_wav(out, render(mode, read_wav_f32(sys.argv[3]) if len(sys.argv) > 3 else None))
    print('wrote', out)
