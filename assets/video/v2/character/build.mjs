// Generates the "Dev" character SVGs (original character, shared palette).
//   node build.mjs   -> character.svg, character-frustrated.svg, character-happy.svg
import { writeFileSync } from 'node:fs';
import { dirname, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const here = dirname(fileURLToPath(import.meta.url));

export const P = {
  skin: '#eaa982', skinShade: '#d48b64', skinDeep: '#bf7652', blush: '#ef7f6c',
  hair: '#2a2231', hairHi: '#433852',
  hood: '#3b4b6e', hoodShade: '#2c3957', hoodLight: '#4a5c84', shirt: '#f4f6fa',
  string: '#eef1f6',
  band: '#171b24', cup: '#2a78d6', cupShade: '#1f5fae', cushion: '#1d222d',
  ink: '#1c1720', white: '#ffffff', iris: '#3a2619',
  mouth: '#5a1d2a', tongue: '#e8646c',
  lid: '#e7ebf2', lidEdge: '#c9cfda', lidShade: '#d6dbe4', bolt: '#2a78d6',
  sweat: '#8cc8ff',
};

const BOLT = 'M15.914 4a1.5 1.5 0 00-2.474-1.561l-9 9A1.5 1.5 0 005.5 14h4.002a.5.5 0 01.471.666L8.086 20a1.5 1.5 0 002.475 1.56l9-9A1.5 1.5 0 0018.5 10h-3.997a.5.5 0 01-.472-.667z';

const FACE = 'M300,160 C374,160 420,214 420,298 C420,372 392,428 342,450 C322,459 278,459 258,450 C208,428 180,372 180,298 C180,214 226,160 300,160 Z';

function eyes(expr) {
  const one = (cx, flip) => {
    if (expr === 'frustrated') {
      // heavy, half-closed lids; pupils looking sideways at the slow tool
      return `
      <ellipse cx="${cx}" cy="310" rx="25" ry="24" fill="${P.white}"/>
      <circle cx="${cx - 9}" cy="318" r="13" fill="${P.iris}"/>
      <circle cx="${cx - 13}" cy="313" r="3.6" fill="${P.white}"/>
      <path d="M${cx - 30},300 C${cx - 18},292 ${cx + 18},292 ${cx + 30},300 L${cx + 30},284 L${cx - 30},284 Z" fill="${P.skin}"/>
      <path d="M${cx - 29},301 C${cx - 16},295 ${cx + 16},295 ${cx + 29},301" fill="none" stroke="${P.ink}" stroke-width="6" stroke-linecap="round"/>`;
    }
    const ry = expr === 'surprised' ? 33 : 28;
    const r = expr === 'surprised' ? 14 : 15;
    const k = flip ? -1 : 1;
    return `
      <ellipse cx="${cx}" cy="306" rx="26" ry="${ry}" fill="${P.white}"/>
      <circle cx="${cx + 1 * k}" cy="310" r="${r}" fill="${P.iris}"/>
      <circle cx="${cx + 1 * k}" cy="310" r="${r * 0.48}" fill="${P.ink}"/>
      <circle cx="${cx + 6 * k}" cy="303" r="4.6" fill="${P.white}"/>
      <circle cx="${cx - 4 * k}" cy="317" r="2" fill="${P.white}" opacity=".85"/>
      <path d="M${cx - 27},${306 - ry * 0.35} C${cx - 20},${306 - ry - 3} ${cx + 20},${306 - ry - 3} ${cx + 27},${306 - ry * 0.35}" fill="none" stroke="${P.ink}" stroke-width="6" stroke-linecap="round"/>
      <path d="M${cx + 25 * k},${306 - ry * 0.5} l${9 * k},-8" stroke="${P.ink}" stroke-width="5" stroke-linecap="round"/>`;
  };
  return one(250, true) + one(350, false);
}

function brows(expr) {
  const s = `fill="none" stroke="${P.hair}" stroke-width="12" stroke-linecap="round"`;
  if (expr === 'surprised') return `<path d="M220,248 Q248,226 278,240" ${s}/><path d="M322,240 Q352,226 380,248" ${s}/>`;
  if (expr === 'frustrated') return `<path d="M222,262 Q250,264 280,278" ${s}/><path d="M320,278 Q350,264 378,262" ${s}/>`;
  return `<path d="M222,258 Q250,242 278,252" ${s}/><path d="M322,252 Q350,242 378,258" ${s}/>`;
}

function mouth(expr) {
  if (expr === 'frustrated') {
    return `<path d="M266,414 C278,404 290,404 300,412 C310,420 322,420 336,410" fill="none" stroke="${P.ink}" stroke-width="8" stroke-linecap="round"/>`;
  }
  const d = expr === 'surprised'
    ? 'M262,388 C276,382 324,382 338,388 C340,428 322,450 300,450 C278,450 260,428 262,388 Z'
    : 'M246,384 C270,392 330,392 354,384 C350,424 328,446 300,446 C272,446 250,424 246,384 Z';
  return `
    <clipPath id="m-${expr}"><path d="${d}"/></clipPath>
    <path d="${d}" fill="${P.mouth}"/>
    <g clip-path="url(#m-${expr})">
      <rect x="230" y="370" width="140" height="${expr === 'surprised' ? 25 : 26}" rx="6" fill="${P.white}"/>
      <ellipse cx="300" cy="452" rx="34" ry="20" fill="${P.tongue}"/>
    </g>
    <path d="${d}" fill="none" stroke="${P.ink}" stroke-width="5" stroke-linejoin="round"/>`;
}

function head(expr) {
  return `
  <g id="head" transform="translate(0,16)">
    <!-- neck -->
    <path d="M262,420 L262,530 Q300,548 338,530 L338,420 Z" fill="${P.skin}"/>
    <clipPath id="neck"><path d="M262,420 L262,530 Q300,548 338,530 L338,420 Z"/></clipPath>
    <ellipse cx="300" cy="438" rx="78" ry="46" fill="${P.skinShade}" clip-path="url(#neck)"/>
    <!-- headset cushions behind face edge -->
    <rect x="182" y="270" width="26" height="92" rx="12" fill="${P.cushion}"/>
    <rect x="392" y="270" width="26" height="92" rx="12" fill="${P.cushion}"/>
    <!-- face -->
    <path d="${FACE}" fill="${P.skin}"/>
    <clipPath id="face"><path d="${FACE}"/></clipPath>
    <g clip-path="url(#face)">
      <path d="M392,170 C440,240 430,380 352,452 L460,470 L460,160 Z" fill="${P.skinShade}" opacity=".55"/>
      <path d="M176,250 C210,236 236,226 262,206 C296,232 352,236 404,236 L430,252 L430,150 L170,150 Z" fill="${P.skinShade}" opacity=".9"/>
    </g>
    <ellipse cx="220" cy="370" rx="24" ry="13" fill="${P.blush}" opacity=".45"/>
    <ellipse cx="380" cy="370" rx="24" ry="13" fill="${P.blush}" opacity=".45"/>
    <!-- nose -->
    <path d="M300,322 C294,340 290,352 298,357 C304,360 312,358 316,352" fill="none" stroke="${P.skinDeep}" stroke-width="6" stroke-linecap="round"/>
    ${eyes(expr)}
    ${brows(expr)}
    ${mouth(expr)}
    <!-- hair -->
    <path d="M182,300 C164,196 214,112 304,112 C392,112 440,190 420,300 C414,270 406,246 396,232 C352,236 300,228 264,204 C246,226 222,240 202,248 C192,264 186,282 182,300 Z" fill="${P.hair}"/>
    <path d="M232,144 C262,114 336,108 372,140 C340,126 290,124 252,150 Z" fill="${P.hairHi}"/>
    <path d="M264,204 C276,176 300,158 332,152 C312,170 300,190 296,214 Z" fill="${P.hairHi}" opacity=".7"/>
    <path d="M226,156 C214,104 254,62 312,58 C350,56 384,70 404,96 C388,90 374,90 362,94 C386,106 402,128 406,156 C378,128 340,116 300,118 C268,120 242,134 226,156 Z" fill="${P.hair}"/>
    <path d="M268,90 C288,72 322,66 350,76 C328,78 304,86 286,102 Z" fill="${P.hairHi}"/>
    <!-- headset band + cups -->
    <path d="M178,296 C166,170 226,96 300,96 C374,96 434,170 422,296" fill="none" stroke="${P.band}" stroke-width="15" stroke-linecap="round"/>
    <path d="M196,182 C222,128 262,108 300,106" fill="none" stroke="#3a4152" stroke-width="4" stroke-linecap="round"/>
    <rect x="148" y="262" width="50" height="104" rx="22" fill="${P.cup}"/>
    <rect x="148" y="262" width="22" height="104" rx="11" fill="${P.cupShade}"/>
    <rect x="402" y="262" width="50" height="104" rx="22" fill="${P.cup}"/>
    <rect x="430" y="262" width="22" height="104" rx="11" fill="${P.cupShade}"/>
    <rect x="160" y="276" width="8" height="30" rx="4" fill="#ffffff" opacity=".35"/>
    <rect x="414" y="276" width="8" height="30" rx="4" fill="#ffffff" opacity=".35"/>
  </g>`;
}

function body() {
  const torso = 'M36,800 C36,664 104,566 232,532 L368,532 C496,566 564,664 564,800 Z';
  return `
  <g id="body">
    <path d="M196,566 C204,500 252,474 300,474 C348,474 396,500 404,566 Z" fill="${P.hoodShade}"/>
    <path d="${torso}" fill="${P.hood}"/>
    <clipPath id="torso"><path d="${torso}"/></clipPath>
    <g clip-path="url(#torso)">
      <path d="M420,540 C500,600 540,700 520,800 L600,800 L600,520 Z" fill="${P.hoodShade}" opacity=".8"/>
      <path d="M150,580 C120,640 110,720 116,800" fill="none" stroke="${P.hoodShade}" stroke-width="6" stroke-linecap="round" opacity=".7"/>
      <path d="M450,580 C480,640 490,720 484,800" fill="none" stroke="#1f2a44" stroke-width="6" stroke-linecap="round" opacity=".6"/>
    </g>
    <path d="M250,532 Q300,606 350,532 Z" fill="${P.shirt}"/>
    <path d="M262,532 Q300,560 338,532" fill="${P.skinShade}" opacity=".6"/>
    <path d="M224,530 C236,612 364,612 376,530" fill="none" stroke="${P.hoodLight}" stroke-width="20" stroke-linecap="round"/>
    <path d="M276,594 C272,630 270,660 268,690" fill="none" stroke="${P.string}" stroke-width="7" stroke-linecap="round"/>
    <path d="M324,594 C328,630 330,660 332,690" fill="none" stroke="${P.string}" stroke-width="7" stroke-linecap="round"/>
    <rect x="262" y="684" width="12" height="22" rx="5" fill="${P.lidEdge}"/>
    <rect x="326" y="684" width="12" height="22" rx="5" fill="${P.lidEdge}"/>
  </g>`;
}

function laptop() {
  const lens = [44, 50, 48, 40];
  const hand = (edge, dir) => {
    const palm = `<ellipse cx="${edge - 6 * dir}" cy="690" rx="28" ry="56" fill="${P.skinShade}"/>`;
    const f = lens.map((L, i) => {
      const y = 630 + i * 24;
      const x = dir < 0 ? edge - 14 : edge + 14 - L;
      const tip = dir < 0 ? x + L - 16 : x;
      return `<rect x="${x}" y="${y}" width="${L}" height="22" rx="11" fill="${P.skin}"/>
      <rect x="${tip + (dir < 0 ? 2 : 2)}" y="${y + 4}" width="12" height="14" rx="6" fill="#f6c6a6" opacity=".9"/>`;
    }).join('');
    return { palm, f };
  };
  const L = hand(136, -1), R = hand(464, 1);
  return `
  <g id="laptop">
    <rect x="58" y="604" width="84" height="150" rx="36" fill="${P.hoodShade}"/>
    <rect x="458" y="604" width="84" height="150" rx="36" fill="${P.hoodShade}"/>
    <rect x="96" y="640" width="40" height="110" rx="18" fill="${P.hoodLight}"/>
    <rect x="464" y="640" width="40" height="110" rx="18" fill="${P.hoodLight}"/>
    ${L.palm}${R.palm}
    <rect x="136" y="596" width="328" height="230" rx="22" fill="${P.lidEdge}"/>
    <rect x="136" y="590" width="328" height="228" rx="22" fill="${P.lid}"/>
    <path d="M158,600 L300,600 L230,810 L158,810 Z" fill="#ffffff" opacity=".35"/>
    <circle cx="300" cy="706" r="40" fill="#ffffff"/>
    <g transform="translate(276,682) scale(2)"><path d="${BOLT}" fill="${P.bolt}"/></g>
    ${L.f}${R.f}
  </g>`;
}

function pointingArm() {
  // character's right arm (viewer's left) thrown out to point at the headline
  const arm = 'M186,612 C130,606 86,584 46,556';
  return `
  <g id="arm">
    <path d="${arm}" fill="none" stroke="${P.hood}" stroke-width="66" stroke-linecap="round"/>
    <path d="M186,640 C130,634 84,612 44,584" fill="none" stroke="${P.hoodShade}" stroke-width="14" stroke-linecap="round" opacity=".9"/>
    <g transform="translate(46,556) rotate(34)">
      <rect x="-14" y="-36" width="34" height="72" rx="15" fill="${P.hoodLight}"/>
      <!-- fist -->
      <rect x="-64" y="-26" width="58" height="54" rx="22" fill="${P.skin}"/>
      <path d="M-58,4 C-46,8 -30,8 -16,4 M-56,17 C-44,21 -30,21 -18,17" fill="none" stroke="${P.skinShade}" stroke-width="4.5" stroke-linecap="round"/>
      <!-- index finger -->
      <rect x="-118" y="-26" width="76" height="22" rx="11" fill="${P.skin}"/>
      <rect x="-114" y="-22" width="16" height="14" rx="7" fill="#f6c6a6"/>
      <!-- thumb -->
      <rect x="-58" y="-10" width="40" height="18" rx="9" fill="${P.skin}" stroke="${P.skinShade}" stroke-width="4"/>
    </g>
  </g>`;
}


function waveArm(a) {
  // character's right arm (viewer's left) raised, open hand; a = forearm angle (deg)
  return `
  <g id="arm">
    <path d="M190,616 C140,612 108,600 92,560" fill="none" stroke="${P.hood}" stroke-width="66" stroke-linecap="round"/>
    <g transform="translate(92,560) rotate(${a})">
      <path d="M0,0 L0,-120" fill="none" stroke="${P.hood}" stroke-width="58" stroke-linecap="round"/>
      <path d="M14,-8 L14,-112" fill="none" stroke="${P.hoodShade}" stroke-width="12" stroke-linecap="round" opacity=".8"/>
      <rect x="-26" y="-150" width="52" height="34" rx="14" fill="${P.hoodLight}"/>
      <ellipse cx="0" cy="-184" rx="34" ry="38" fill="${P.skin}"/>
      ${[-24, -8, 8, 24].map((x, i) => `<rect x="${x - 8}" y="${-246 + Math.abs(i - 1.5) * 8}" width="16" height="${58 - Math.abs(i - 1.5) * 8}" rx="8" fill="${P.skin}"/>`).join('')}
      <rect x="26" y="-196" width="40" height="17" rx="8.5" fill="${P.skin}" transform="rotate(-30 30 -188)"/>
      <path d="M-18,-170 C-6,-162 8,-162 20,-170" fill="none" stroke="${P.skinShade}" stroke-width="4" stroke-linecap="round"/>
    </g>
  </g>`;
}

function sweat() {
  return `<path d="M430,196 C440,214 446,224 446,232 C446,242 438,248 430,248 C422,248 414,242 414,232 C414,224 420,214 430,196 Z" fill="${P.sweat}"/>
  <path d="M424,232 C424,226 427,221 430,216" fill="none" stroke="#ffffff" stroke-width="3.5" stroke-linecap="round" opacity=".8"/>`;
}

function svg({ title, expr, pose }) {
  return `<svg xmlns="http://www.w3.org/2000/svg" viewBox="-90 50 690 750" width="690" height="750" role="img" aria-label="${title}">
  <title>${title}</title>
  ${body()}
  ${head(expr)}
  ${pose === 'laptop' ? laptop() : ''}
  ${pose === 'point' ? pointingArm() : ''}
  ${typeof pose === 'number' ? waveArm(pose) : ''}
  ${expr === 'frustrated' ? sweat() : ''}
</svg>
`;
}

const out = [
  ['character.svg', { title: 'Dev (SchemaScope character), surprised and delighted', expr: 'surprised', pose: 'laptop' }],
  ['character-frustrated.svg', { title: 'Dev, frustrated, waiting on a slow tool', expr: 'frustrated', pose: 'laptop' }],
  ['character-happy.svg', { title: 'Dev, happy, pointing', expr: 'happy', pose: 'point' }],
  ['character-wave-a.svg', { title: 'Dev, happy, waving', expr: 'happy', pose: -22 }],
  ['character-wave-b.svg', { title: 'Dev, happy, waving', expr: 'happy', pose: 0 }],
];
for (const [f, o] of out) writeFileSync(resolve(here, f), svg(o));
console.log('wrote', out.map(o => o[0]).join(', '));
