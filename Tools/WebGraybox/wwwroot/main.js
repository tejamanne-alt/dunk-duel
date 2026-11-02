// Dunk Duel web graybox: renders the real C# simulation (compiled to WebAssembly) on a canvas.
// The sim runs at a fixed 60 Hz; this file only reads state and events (presentation never writes to the sim).
import { dotnet } from './_framework/dotnet.js';

// ------------------------------------------------------------------ constants

const B = { Left: 1, Right: 2, Up: 4, Down: 8, Shoot: 16, Shake: 32, Trick: 64, Turbo: 128, Finisher: 256, Amp: 512 };
const ST = { Simple: 1, Heat: 2, InContact: 8, Protecting: 32, Driving: 64, Grounded: 128 };
const TICK_MS = 1000 / 60;

const FIGHTER_LOOK = {
  kofi:  { main: '#F4A300', alt: '#128C86', skin: '#5E3A22', hair: '#1b120c', tip: '#F7C948', fx: '#FF7A1A', trim: '#0E6B66' },
  meera: { main: '#3B3F9E', alt: '#C9D1DC', skin: '#9C6B45', hair: '#0f0d10', tip: '#D9DEE6', fx: '#9FD8FF', trim: '#C9D1DC' },
  juno:  { main: '#E0218A', alt: '#21D3E6', skin: '#E7C3A0', hair: '#E0218A', tip: '#21D3E6', fx: '#21D3E6', trim: '#21D3E6' },
  matas: { main: '#1D8A58', alt: '#B8763A', skin: '#E6C4A4', hair: '#5A3B22', tip: '#5A3B22', fx: '#43E0A0', trim: '#B8763A' },
};

// Nine courts. Palettes stay away from the fighters' identity hues so fighters always pop.
const COURTS = [
  { id: 'monsoon_bazaar', name: 'Monsoon Bazaar', tier: 'Street', sky: ['#0a0f1f', '#25283a'], floor: '#3b3e46', floor2: '#2b2e35', line: '#e9e3d2', key: 'rgba(120,110,140,0.35)', glow: '#ffd9a0', crowd: ['#2a2836', '#3a3747', '#4c4858'], weather: 'rain', backdrop: 'arches', lights: 'bulbs' },
  { id: 'the_tracks', name: 'The Tracks', tier: 'Street', sky: ['#2a2234', '#d9876b'], floor: '#5d6066', floor2: '#4a4d52', line: '#f1efe8', key: 'rgba(200,90,70,0.25)', glow: '#ffcf9a', crowd: ['#3a2f36', '#51424a', '#6a5860'], weather: 'haze', backdrop: 'tracks', special: 'train' },
  { id: 'concrete_coast', name: 'Concrete Coast', tier: 'Street', sky: ['#8fd2ff', '#e6f6ff'], floor: '#bdb5a8', floor2: '#a69e91', line: '#ffffff', key: 'rgba(255,140,90,0.30)', glow: '#fff6d8', crowd: ['#c9a27e', '#a77e5c', '#e0c3a0'], weather: 'gulls', backdrop: 'sea' },
  { id: 'rivalry_night', name: 'Rivalry Night', tier: 'School', sky: ['#2a1d14', '#4d3826'], floor: '#c79251', floor2: '#b07e44', line: '#fdfaf2', key: 'rgba(170,40,40,0.35)', glow: '#ffe2b0', crowd: ['#8c2f2f', '#d9d2c3', '#3a2a22'], weather: 'none', backdrop: 'gym', lights: 'gym' },
  { id: 'lights_out', name: 'Lights Out', tier: 'School', sky: ['#05070b', '#10151d'], floor: '#5d4630', floor2: '#4b3826', line: '#cfc8b8', key: 'rgba(90,90,100,0.30)', glow: '#9fb3c8', crowd: ['#141820', '#1c222c', '#262d38'], weather: 'windowrain', backdrop: 'gymdark', special: 'lightning' },
  { id: 'campus_quad', name: 'Campus Quad', tier: 'School', sky: ['#f2a65a', '#f9dca8'], floor: '#827766', floor2: '#6f6556', line: '#fff8ec', key: 'rgba(150,60,40,0.28)', glow: '#ffe4b5', crowd: ['#7a4a2a', '#a35d3a', '#c9874f'], weather: 'leaves', backdrop: 'campus' },
  { id: 'old_barn', name: 'The Old Barn', tier: 'Pro', sky: ['#24170c', '#4d321a'], floor: '#b37840', floor2: '#9c6634', line: '#fbf3e2', key: 'rgba(120,40,30,0.33)', glow: '#ffcc8a', crowd: ['#5a3a20', '#7a5230', '#3a2614'], weather: 'dust', backdrop: 'barn' },
  { id: 'glass_court', name: 'Glass Court', tier: 'Pro', sky: ['#080c17', '#161f33'], floor: '#0f1724', floor2: '#0b111b', line: '#e8f0ff', key: 'rgba(255,255,255,0.06)', glow: '#e8f0ff', crowd: ['#1b2234', '#262f46', '#323d5a'], weather: 'none', backdrop: 'arena', special: 'led' },
  { id: 'the_crown', name: 'The Crown', tier: 'Pro', sky: ['#0a1322', '#1b2a44'], floor: '#9a6837', floor2: '#86592d', line: '#fff', key: 'rgba(40,60,100,0.40)', glow: '#d6e4ff', crowd: ['#232c3f', '#323d56', '#46526e'], weather: 'haze', backdrop: 'arena', special: 'spotlights' },
];

// ------------------------------------------------------------------ boot

const canvas = document.getElementById('game');
const ctx = canvas.getContext('2d');
let W = 0, H = 0, DPR = 1;
// Safe-area insets (notch / home indicator in landscape): the canvas runs edge to edge, the HUD stays clear of them.
const safeProbe = document.createElement('div');
safeProbe.style.cssText = 'position:fixed;inset:0;visibility:hidden;pointer-events:none;' +
  'padding:env(safe-area-inset-top,0px) env(safe-area-inset-right,0px) env(safe-area-inset-bottom,0px) env(safe-area-inset-left,0px)';
document.body.appendChild(safeProbe);
const SAFE = { t: 0, r: 0, b: 0, l: 0 };
function resize() {
  DPR = Math.min(2, window.devicePixelRatio || 1);
  W = window.innerWidth; H = window.innerHeight;
  canvas.width = Math.round(W * DPR); canvas.height = Math.round(H * DPR);
  const cs = getComputedStyle(safeProbe);
  SAFE.t = parseFloat(cs.paddingTop) || 0; SAFE.r = parseFloat(cs.paddingRight) || 0;
  SAFE.b = parseFloat(cs.paddingBottom) || 0; SAFE.l = parseFloat(cs.paddingLeft) || 0;
}
window.addEventListener('resize', resize);
resize();

const { getAssemblyExports, getConfig } = await dotnet.create();
const exports = await getAssemblyExports(getConfig().mainAssemblyName);
const G = exports.DunkDuel.Web.Graybox;
const INFO = JSON.parse(G.Init());
const EV = Object.fromEntries(INFO.events.map((n, i) => [n, i]));
const ACT = INFO.actions;
const PHASE = INFO.phases;
document.getElementById('loading').classList.add('hidden');

// ------------------------------------------------------------------ menu

const ATTR_NAMES = ['SPD', 'HND', 'RNG', 'FIN', 'DEF', 'STR'];
const pick = { p0: 0, p1: 1 };
function buildCards(el, key) {
  el.innerHTML = '';
  INFO.fighters.forEach((f, i) => {
    const look = FIGHTER_LOOK[f.id];
    const c = document.createElement('div');
    c.className = 'card' + (pick[key] === i ? ' sel' : '');
    c.style.setProperty('--c', look.main);
    c.innerHTML = `<div class="swatch"></div><div class="nm">${f.name}</div><div class="nk">"${f.nick}"</div>
      <div class="ms">${f.mastery.replace(/([A-Z])/g, ' $1').trim()} · ${f.height} m · ${f.tricks.join(' / ')}</div>
      <div class="bars">${f.attr.map((v, k) => `<span>${ATTR_NAMES[k]}</span><div class="bar"><i style="width:${v * 10}%"></i></div>`).join('')}</div>`;
    c.onclick = () => { pick[key] = i; buildCards(el, key); };
    el.appendChild(c);
  });
}
buildCards(document.getElementById('pickP0'), 'p0');
buildCards(document.getElementById('pickP1'), 'p1');
const tierSel = document.getElementById('tier');
INFO.tiers.forEach((t, i) => { const o = document.createElement('option'); o.value = i; o.textContent = t; tierSel.appendChild(o); });
tierSel.value = '2';
const courtSel = document.getElementById('court');
COURTS.forEach((c, i) => { const o = document.createElement('option'); o.value = i; o.textContent = `${c.tier}: ${c.name}`; courtSel.appendChild(o); });
const params = new URLSearchParams(location.search);
courtSel.value = params.get('court') ?? String(Math.floor(Math.random() * COURTS.length));
const modeSel = document.getElementById('mode');
if (params.get('mode')) modeSel.value = params.get('mode');

let running = false;
let court = COURTS[0];
let mode = 'play';
let showBoxes = false;
const isTouch = matchMedia('(pointer: coarse)').matches;

function startMatch() {
  mode = modeSel.value;
  court = COURTS[Number(courtSel.value)];
  const tier = Number(tierSel.value);
  const seed = Math.floor(Math.random() * 1e9);
  const human = mode !== 'watch';
  G.NewMatch(pick.p0, pick.p1, human ? -1 : 4, mode === 'training' ? 0 : tier, seed, 2, mode === 'training', document.getElementById('simple').checked);
  resetPresentation();
  document.getElementById('menu').classList.add('hidden');
  document.getElementById('touch').classList.toggle('hidden', !(isTouch && human));
  running = true;
}
document.getElementById('start').onclick = startMatch;
document.getElementById('btnMenu').onclick = () => openMenu();
function openMenu() {
  running = false;
  document.getElementById('menu').classList.remove('hidden');
  document.getElementById('touch').classList.add('hidden');
}

// ------------------------------------------------------------------ input

const keys = new Set();
const KEYMAP = {
  ArrowLeft: B.Left, KeyA: B.Left, ArrowRight: B.Right, KeyD: B.Right, ArrowUp: B.Up, KeyW: B.Up, ArrowDown: B.Down, KeyS: B.Down,
  KeyJ: B.Shoot, KeyK: B.Shake, KeyL: B.Trick, Space: B.Turbo, ShiftLeft: B.Turbo, KeyI: B.Finisher, KeyU: B.Amp,
};
window.addEventListener('keydown', e => {
  if (e.code === 'Escape') { if (running) openMenu(); return; }
  if (e.code === 'KeyH') { showBoxes = !showBoxes; return; }
  if (KEYMAP[e.code]) { keys.add(e.code); e.preventDefault(); }
});
window.addEventListener('keyup', e => keys.delete(e.code));
window.addEventListener('blur', () => keys.clear());

const touch = { stickId: null, sx: 0, sy: 0, dx: 0, dy: 0, buttons: 0, trickId: null, trickY: 0, amp: false };
const stickZone = document.getElementById('stickZone');
const stickBase = document.getElementById('stickBase');
const knob = document.getElementById('stickKnob');
stickZone.addEventListener('pointerdown', e => {
  touch.stickId = e.pointerId; touch.sx = e.clientX; touch.sy = e.clientY; touch.dx = 0; touch.dy = 0;
  stickBase.style.left = (e.clientX - 60) + 'px'; stickBase.style.top = (e.clientY - 60) + 'px'; stickBase.style.bottom = 'auto';
  stickZone.setPointerCapture(e.pointerId);
});
stickZone.addEventListener('pointermove', e => {
  if (e.pointerId !== touch.stickId) return;
  touch.dx = e.clientX - touch.sx; touch.dy = e.clientY - touch.sy;
  const m = Math.hypot(touch.dx, touch.dy), k = m > 45 ? 45 / m : 1;
  knob.style.transform = `translate(${touch.dx * k}px, ${touch.dy * k}px)`;
});
const endStick = e => { if (e.pointerId === touch.stickId) { touch.stickId = null; touch.dx = touch.dy = 0; knob.style.transform = ''; } };
stickZone.addEventListener('pointerup', endStick);
stickZone.addEventListener('pointercancel', endStick);
document.querySelectorAll('#buttons button').forEach(btn => {
  const bit = B[btn.dataset.b];
  btn.addEventListener('pointerdown', e => {
    btn.setPointerCapture(e.pointerId); btn.classList.add('down');
    if (bit === B.Trick) { touch.trickId = e.pointerId; touch.trickY = e.clientY; touch.amp = false; touch.trickDelay = 4; }
    else touch.buttons |= bit;
  });
  btn.addEventListener('pointermove', e => { if (bit === B.Trick && e.pointerId === touch.trickId && touch.trickY - e.clientY > 28) touch.amp = true; });
  const up = e => { btn.classList.remove('down'); touch.buttons &= ~bit; if (bit === B.Trick) { touch.trickId = null; touch.amp = false; } };
  btn.addEventListener('pointerup', up);
  btn.addEventListener('pointercancel', up);
});

function inputBits() {
  let b = 0;
  for (const k of keys) b |= KEYMAP[k] || 0;
  if (touch.stickId !== null) {
    const dz = 14;
    if (touch.dx < -dz) b |= B.Left;
    if (touch.dx > dz) b |= B.Right;
    if (touch.dy < -dz * 1.6) b |= B.Up;
    if (touch.dy > dz * 1.2) b |= B.Down;
  }
  b |= touch.buttons;
  // TRICK on touch: wait a few ticks to see whether it becomes a swipe-up (Amped).
  if (touch.trickId !== null) {
    if (touch.trickDelay > 0 && !touch.amp) touch.trickDelay--;
    else b |= B.Trick | (touch.amp ? B.Amp : 0);
  }
  return b;
}

// ------------------------------------------------------------------ state

function decode(a) {
  let i = 0;
  const s = {
    tick: a[i++], frame: a[i++], phase: PHASE[a[i++]], phaseFrame: a[i++], round: a[i++], offense: a[i++], needsClear: !!a[i++],
    suddenDeath: !!a[i++], roundClock: a[i++], shotClock: a[i++], hitStop: a[i++], slow: a[i++], roundWinner: a[i++], matchWinner: a[i++],
    roundsToWin: a[i++], mode: a[i++], f: [],
  };
  for (let k = 0; k < 2; k++) {
    s.f.push({
      x: a[i++], y: a[i++], vx: a[i++], vy: a[i++], facing: a[i++], action: ACT[a[i++]], af: a[i++], slot: a[i++], peak: a[i++],
      takeoff: a[i++], kind: INFO.kinds[a[i++]], hype: a[i++], balance: a[i++], score: a[i++], rounds: a[i++], ci: a[i++],
      status: a[i++], streak: a[i++], rhythm: a[i++], combo: a[i++], beaten: a[i++], plant: a[i++], param: a[i++], flags: a[i++],
      fgm: a[i++], fga: a[i++],
    });
  }
  s.ball = { mode: a[i++], holder: a[i++], x: a[i++], y: a[i++], vx: a[i++], vy: a[i++] };
  s.shot = { active: !!a[i++], shooter: a[i++], kind: INFO.kinds[a[i++]], zone: INFO.zones[a[i++]], grade: INFO.grades[a[i++]], result: INFO.results[a[i++]], stage: a[i++], p: a[i++], q: a[i++], points: a[i++] };
  s.fin = { active: !!a[i++], attacker: a[i++], frame: a[i++], frames: a[i++], denyStart: a[i++], denyFrames: a[i++], ring: !!a[i++], attempted: !!a[i++] };
  s.in0 = a[i++]; s.in1 = a[i++];
  const n = a[i++];
  s.events = [];
  for (let k = 0; k < n; k++) s.events.push({ type: INFO.events[a[i++]], actor: a[i++], a: a[i++], b: a[i++], x: a[i++], y: a[i++] });
  return s;
}

let prev = null, cur = null;
window.__dd = { state: () => cur, pres: () => pres, running: () => running };
const pres = { callouts: [], particles: [], shake: 0, zoom: 0, zoomV: 0, tilt: 0, flash: 0, flashColor: '#fff', net: 0, heat: 0, highlights: 0,
  camX: -4, sweat: [0, 0], trails: [[], []], lastShot: null, train: 0, lightning: 0, cut: 0, crowdPop: 0, ledLeader: -1, denyFx: 0, time: 0 };
function resetPresentation() {
  prev = cur = null;
  Object.assign(pres, { callouts: [], particles: [], shake: 0, zoom: 0, zoomV: 0, tilt: 0, flash: 0, net: 0, heat: 0, highlights: 0, sweat: [0, 0], trails: [[], []], lastShot: null, cut: 0, crowdPop: 0 });
}

// ------------------------------------------------------------------ loop

let acc = 0, last = performance.now();
function loop(now) {
  try {
    step(now);
  } catch (e) {
    console.error('frame failed: ' + (e && e.stack ? e.stack : e));
  }
  requestAnimationFrame(loop);
}

function step(now) {
  const dt = Math.min(100, now - last);
  last = now;
  pres.time += dt / 1000;
  if (running) {
    acc += dt;
    let ticks = 0;
    while (acc >= TICK_MS && ticks < 4) {
      G.Tick(mode === 'watch' ? 0 : inputBits());
      acc -= TICK_MS;
      ticks++;
    }
    if (ticks > 0) {
      prev = cur;
      cur = decode(G.Frame());
      if (!prev) prev = cur;
      for (const e of cur.events) onEvent(e, cur);
    }
    if (acc > TICK_MS * 4) acc = 0;
  }
  render(running && cur ? Math.min(1, acc / TICK_MS) : 1, dt / 1000);
}

// Boot: either straight into a match (?autostart=1 locally, or #watch where only a hash survives the link) or the menu.
if (location.hash === '#watch') {
  modeSel.value = 'watch';
  startMatch();
} else if (params.get('autostart')) {
  if (params.get('p0')) pick.p0 = Number(params.get('p0'));
  if (params.get('p1')) pick.p1 = Number(params.get('p1'));
  if (params.get('tier')) tierSel.value = params.get('tier');
  startMatch();
} else {
  document.getElementById('menu').classList.remove('hidden');
}
requestAnimationFrame(loop);

// ------------------------------------------------------------------ events -> impact recipes

function look(ci) { return FIGHTER_LOOK[INFO.fighters[ci].id]; }

function callout(text, color, big = false) {
  pres.callouts.push({ text, color, t: 0, big });
  if (pres.callouts.length > 3) pres.callouts.shift();
}

function burst(x, y, color, n, speed, life, kind = 'spark') {
  for (let k = 0; k < n; k++) {
    const a = Math.random() * Math.PI * 2, v = speed * (0.4 + Math.random());
    pres.particles.push({ x, y, vx: Math.cos(a) * v, vy: Math.sin(a) * v + speed * 0.4, life, max: life, color, kind, size: 0.03 + Math.random() * 0.05 });
  }
}

function onEvent(e, s) {
  const imp = INFO.impacts[e.type];
  const actorLook = e.actor < 2 ? look(s.f[e.actor].ci) : null;
  if (imp) {
    if (imp.callout && e.type !== 'ShotClockViolation') callout(imp.callout, actorLook ? actorLook.main : '#fff', true);
    camera(imp.camera);
    if (imp.crowd === 'erupt' || imp.crowd === 'frenzy') pres.crowdPop = imp.crowd === 'frenzy' ? 1.6 : 1;
    pres.highlights = Math.min(3, pres.highlights + 1);
  }
  switch (e.type) {
    case 'ShotMade':
      pres.net = 1;
      if (e.a === 3) callout('+3', actorLook.main);
      if (s.shot.zone === 'Rim' && s.shot.kind === 'Dunk') { burst(0, 3.05, actorLook.fx, 26, 3.5, 0.6); pres.shake = Math.max(pres.shake, 0.5); }
      pres.sweat[e.actor] = Math.min(1, pres.sweat[e.actor] + 0.06);
      break;
    case 'FinisherScore':
      pres.net = 1; burst(0, 3.05, actorLook.fx, 60, 5, 1.1); pres.flash = 0.8; pres.flashColor = actorLook.fx; callout('FINISHER!', actorLook.main, true);
      if (court.special === 'spotlights') pres.crowdPop = 2;
      break;
    case 'ShotMissed': if (e.a === 5) callout('AIRBALL', '#9aa3b5'); break;
    case 'RimHit': burst(e.x, e.y, '#ffb36b', 6, 1.2, 0.25); pres.shake = Math.max(pres.shake, 0.15); break;
    case 'BackboardHit': pres.shake = Math.max(pres.shake, 0.12); break;
    case 'Bite': callout('BIT!', actorLook ? '#ffd166' : '#fff'); break;
    case 'BlowBy': callout('BLOW-BY', look(s.f[e.actor].ci).alt); break;
    case 'HeatCheckOn': callout('HEAT CHECK', '#ff6a3d', true); break;
    case 'RhythmStack': callout('RHYTHM ×' + e.a, look(s.f[e.actor].ci).alt); break;
    case 'ComboLink': if (e.a >= 3) callout('COMBO ×' + e.a, look(s.f[e.actor].ci).alt); break;
    case 'PostBump': burst(e.x + 0.3, 0.1, '#c8b08a', 8, 1, 0.5, 'dust'); pres.shake = Math.max(pres.shake, 0.18); break;
    case 'DriveBump': burst(e.x + 0.3, 0.6, '#ffffff', 4, 0.8, 0.25); break;
    case 'FinisherStart': {
      const f = INFO.fighters[e.a];
      callout(f.finisher.toUpperCase(), look(e.a).main, true);
      pres.flash = 0.5; pres.flashColor = look(e.a).fx;
      break;
    }
    case 'Denied': pres.cut = 1; break;
    case 'DenialAttempt': if (!e.a) callout('MISSED RING', '#9aa3b5'); break;
    case 'RoundEnd': callout(`ROUND ${s.round + 1} · ${INFO.fighters[s.f[e.a].ci].name.split(' ')[0].toUpperCase()}`, look(s.f[e.a].ci).main, true); break;
    case 'MatchEnd': callout(`${INFO.fighters[s.f[e.a].ci].name.toUpperCase()} WINS`, look(s.f[e.a].ci).main, true); break;
    case 'SuddenDeath': callout('NEXT BUCKET WINS', '#fff', true); break;
    case 'Cleared': break;
    case 'ShotClockViolation': callout('SHOT CLOCK', '#ff5d5d', true); break;
    case 'RoundIntro': callout(`ROUND ${e.a + 1}`, '#fff', true); break;
    case 'BallLive': break;
    case 'Steal': burst(e.x, 1.0, look(s.f[e.actor].ci).fx, 14, 2, 0.4); break;
    case 'Block': burst(s.ball.x, s.ball.y, '#ffffff', 18, 3, 0.45); break;
    case 'AnkleBreak': burst(s.f[e.actor].x, 0.1, '#ffffff', 16, 1.6, 0.5, 'dust'); break;
    case 'Charge': burst(e.x, 1.0, '#ffffff', 12, 1.8, 0.35); break;
    case 'Splash': pres.net = 1.6; break;
    case 'Jump': if (s.f[e.actor] && INFO.fighters[s.f[e.actor].ci].id === 'matas') burst(s.f[e.actor].x, 0.05, '#c8b08a', 6, 0.8, 0.4, 'dust'); break;
    case 'ShotRelease': pres.lastShot = { t: 0, shooter: e.actor, grade: INFO.grades[e.a], zone: INFO.zones[e.b] }; break;
  }
}

function camera(kind) {
  switch (kind) {
    case 'nudge': pres.shake = Math.max(pres.shake, 0.35); break;
    case 'snapZoom': pres.zoomV += 0.22; pres.shake = Math.max(pres.shake, 0.4); break;
    case 'dutchTilt': pres.tilt = 0.09; pres.zoomV += 0.12; break;
    case 'punchIn': pres.zoomV += 0.28; pres.shake = Math.max(pres.shake, 0.5); break;
    case 'bigPunchIn': pres.zoomV += 0.45; pres.shake = Math.max(pres.shake, 0.9); pres.flash = 0.6; pres.flashColor = '#ffffff'; break;
    case 'netWhip': pres.zoomV += 0.1; break;
    case 'hardCut': pres.cut = 1; pres.flash = 0.7; pres.flashColor = '#ffffff'; break;
    case 'followBall': pres.zoomV += 0.08; break;
  }
}

// ------------------------------------------------------------------ math helpers

const lerp = (a, b, t) => a + (b - a) * t;
const clamp = (v, a, b) => Math.max(a, Math.min(b, v));
const ease = t => t * t * (3 - 2 * t);

// ------------------------------------------------------------------ render

let scale = 80, groundY = 0, camX = -4;
function wx(x) { return (x - camX) * scale + W / 2; }
function wy(y) { return groundY - y * scale; }

function render(alpha, dt) {
  ctx.setTransform(DPR, 0, 0, DPR, 0, 0);
  if (!cur) { drawIdle(dt); return; }
  const s = interp(prev, cur, alpha);
  updateFx(s, dt);

  // Camera: frame both fighters, the ball and the rim.
  const xs = [s.f[0].x, s.f[1].x, s.ball.x];
  let minX = Math.min(...xs), maxX = Math.max(...xs);
  if (maxX > -6.5) maxX = Math.max(maxX, 0.4);
  const spread = maxX - minX;
  const viewMeters = clamp(spread + 7, 10.5, 15.5);
  let base = Math.min(W / viewMeters, H / 6.6);
  let targetX = (minX + maxX) / 2;
  if (s.fin.active) targetX = lerp(s.f[s.fin.attacker].x, 0, 0.35);
  pres.camX = lerp(pres.camX, targetX, Math.min(1, dt * 4));
  scale = base * (1 + pres.zoom);
  const halfView = W / scale / 2;
  camX = clamp(pres.camX, cur.f ? -10.5 + halfView - 0.5 : -6, 1.6 - halfView + 2.2);
  groundY = H * 0.74;

  ctx.save();
  const sh = pres.shake * pres.shake * 9;
  ctx.translate(W / 2 + (Math.random() - 0.5) * sh, H / 2 + (Math.random() - 0.5) * sh);
  ctx.rotate(pres.tilt);
  ctx.translate(-W / 2, -H / 2);

  drawBackdrop(s);
  drawFloor(s);
  drawHoopBack(s);
  const order = s.f[0].y > s.f[1].y ? [1, 0] : [0, 1];
  for (const i of order) drawTrail(i);
  for (const i of order) drawFighter(s, i);
  drawBall(s);
  drawHoopFront(s);
  drawParticles();
  if (showBoxes || s.mode === 1) drawBoxes(s);
  ctx.restore();

  drawWeatherFront(s, dt);
  drawHud(s);
  if (pres.flash > 0) { ctx.fillStyle = pres.flashColor; ctx.globalAlpha = pres.flash * 0.55; ctx.fillRect(0, 0, W, H); ctx.globalAlpha = 1; }
  if (pres.cut > 0) { ctx.fillStyle = '#000'; ctx.globalAlpha = pres.cut * 0.8; ctx.fillRect(0, 0, W, H); ctx.globalAlpha = 1; }
}

function interp(a, b, t) {
  if (!a || a.round !== b.round) return b;
  const s = Object.assign({}, b);
  s.f = b.f.map((f, i) => Object.assign({}, f, { x: lerp(a.f[i].x, f.x, t), y: lerp(a.f[i].y, f.y, t) }));
  s.ball = Object.assign({}, b.ball);
  if (a.ball.mode === b.ball.mode) { s.ball.x = lerp(a.ball.x, b.ball.x, t); s.ball.y = lerp(a.ball.y, b.ball.y, t); }
  return s;
}

function updateFx(s, dt) {
  pres.shake = Math.max(0, pres.shake - dt * 2.2);
  pres.zoom += pres.zoomV; pres.zoomV = 0;
  pres.zoom = lerp(pres.zoom, 0, Math.min(1, dt * 3.5));
  pres.tilt = lerp(pres.tilt, 0, Math.min(1, dt * 2.5));
  pres.flash = Math.max(0, pres.flash - dt * 2.5);
  pres.cut = Math.max(0, pres.cut - dt * 4);
  pres.net = Math.max(0, pres.net - dt * 2.2);
  pres.crowdPop = Math.max(0, pres.crowdPop - dt * 0.8);
  pres.highlights = Math.max(0, pres.highlights - dt * 0.12);
  // Stage Heat: score margin + recent highlights.
  const margin = Math.abs(s.f[0].score - s.f[1].score);
  const close = s.roundClock < 900 && margin <= 2 ? 1 : 0;
  pres.heat = lerp(pres.heat, clamp(0.25 + pres.highlights * 0.25 + close * 0.35 + (s.suddenDeath ? 0.5 : 0) - margin * 0.04, 0, 1.4), Math.min(1, dt));
  for (const c of pres.callouts) c.t += dt;
  pres.callouts = pres.callouts.filter(c => c.t < 1.4);
  if (pres.lastShot) { pres.lastShot.t += dt; if (pres.lastShot.t > 2.2) pres.lastShot = null; }
  for (const p of pres.particles) {
    p.life -= dt; p.x += p.vx * dt; p.y += p.vy * dt;
    if (p.kind === 'dust') { p.vy *= 0.92; p.vx *= 0.94; } else p.vy -= 9 * dt;
  }
  pres.particles = pres.particles.filter(p => p.life > 0);
  for (let i = 0; i < 2; i++) {
    const f = s.f[i];
    const id = INFO.fighters[f.ci].id;
    const fast = Math.abs(f.vx) > 0.07 || f.action === 'Shake' || f.action === 'Trick';
    if (id === 'juno' && fast) pres.trails[i].push({ x: f.x, y: f.y, facing: f.facing, action: f.action, af: f.af, t: 0.18 });
    for (const t of pres.trails[i]) t.t -= dt;
    pres.trails[i] = pres.trails[i].filter(t => t.t > 0).slice(-6);
  }
  if (court.special === 'train') { pres.train += dt; if (pres.train > 40) pres.train = 0; }
  if (court.special === 'lightning') { pres.lightning = Math.max(0, pres.lightning - dt * 3); if (Math.random() < dt * 0.08) pres.lightning = 1; }
}

// ------------------------------------------------------------------ backdrop and court

function drawIdle(dt) {
  const g = ctx.createLinearGradient(0, 0, 0, H);
  g.addColorStop(0, '#0b0d12'); g.addColorStop(1, '#1a1e28');
  ctx.fillStyle = g; ctx.fillRect(0, 0, W, H);
}

function drawBackdrop(s) {
  const g = ctx.createLinearGradient(0, 0, 0, groundY);
  g.addColorStop(0, court.sky[0]); g.addColorStop(1, court.sky[1]);
  ctx.fillStyle = g;
  ctx.fillRect(-50, -50, W + 100, groundY + 50);
  const par = (x, depth) => (x - camX * depth) * scale * 0.6 + W / 2;
  ctx.save();
  // Far layer: painted backdrop cards.
  ctx.globalAlpha = 0.9;
  switch (court.backdrop) {
    case 'arches':
      for (let k = -6; k < 8; k++) {
        const x = par(k * 3.2, 0.25); ctx.fillStyle = '#1c1e2b';
        ctx.fillRect(x, groundY - 260, 120, 220);
        ctx.beginPath(); ctx.arc(x + 60, groundY - 260, 60, Math.PI, 0); ctx.fill();
        ctx.fillStyle = 'rgba(255,214,160,0.18)'; ctx.fillRect(x + 20, groundY - 230, 80, 140);
      }
      bulbs(s, groundY - 210, '#ffd9a0');
      break;
    case 'tracks': {
      ctx.fillStyle = '#251d29';
      for (let k = -8; k < 10; k++) { const x = par(k * 2.5, 0.2); ctx.fillRect(x, groundY - 140 - (k * 37 % 5) * 30, 90, 260); }
      ctx.fillStyle = '#17121a'; ctx.fillRect(-10, groundY - 300, W + 20, 26);
      for (let k = -4; k < 12; k++) { const x = par(k * 4, 0.35); ctx.fillRect(x, groundY - 300, 18, 300); }
      if (pres.train < 3) {
        const tx = W * 1.3 - (pres.train / 3) * W * 2.2;
        ctx.fillStyle = '#4d5560'; ctx.fillRect(tx, groundY - 345, W * 0.9, 46);
        ctx.fillStyle = '#ffd88a'; for (let k = 0; k < 14; k++) ctx.fillRect(tx + 20 + k * 60, groundY - 335, 30, 14);
        burstSparks(tx + W * 0.45, groundY - 300);
      }
      fence();
      break;
    }
    case 'sea': {
      ctx.fillStyle = '#4fb4d8'; ctx.fillRect(-10, groundY - 220, W + 20, 120);
      ctx.fillStyle = '#7fd0ea';
      for (let k = 0; k < 30; k++) ctx.fillRect(((k * 97 + pres.time * 20) % (W + 200)) - 100, groundY - 210 + (k % 5) * 22, 40, 3);
      ctx.fillStyle = '#d8c8a8'; ctx.fillRect(-10, groundY - 100, W + 20, 30);
      for (let k = -6; k < 10; k++) { const x = par(k * 3.6, 0.3); ctx.fillStyle = '#5b4a35'; ctx.fillRect(x + 30, groundY - 250, 8, 160); ctx.fillStyle = '#3f7d4a'; ctx.beginPath(); ctx.ellipse(x + 34, groundY - 252, 46, 14, 0.2, 0, Math.PI * 2); ctx.fill(); }
      break;
    }
    case 'gym': case 'gymdark': {
      ctx.fillStyle = court.backdrop === 'gym' ? '#3a2a1c' : '#0b0e14'; ctx.fillRect(-10, groundY - 340, W + 20, 340);
      for (let k = -5; k < 9; k++) {
        const x = par(k * 3.4, 0.3);
        ctx.fillStyle = court.backdrop === 'gym' ? 'rgba(255,225,170,0.35)' : 'rgba(120,150,190,0.12)'; ctx.fillRect(x, groundY - 330, 90, 60);
        if (court.backdrop === 'gym') { ctx.fillStyle = '#7a1f1f'; ctx.fillRect(x + 100, groundY - 260, 70, 90); ctx.fillStyle = '#e8dcc5'; ctx.fillRect(x + 108, groundY - 250, 54, 12); }
      }
      if (court.backdrop === 'gymdark' && pres.lightning > 0.5) { ctx.fillStyle = 'rgba(210,225,255,0.35)'; ctx.fillRect(-10, 0, W + 20, groundY); }
      break;
    }
    case 'campus': {
      ctx.fillStyle = '#7d5a3c'; const cx = par(-2, 0.18); ctx.fillRect(cx, groundY - 380, 70, 300);
      ctx.beginPath(); ctx.moveTo(cx - 10, groundY - 380); ctx.lineTo(cx + 35, groundY - 450); ctx.lineTo(cx + 80, groundY - 380); ctx.fill();
      ctx.fillStyle = '#f6e7c8'; ctx.beginPath(); ctx.arc(cx + 35, groundY - 340, 18, 0, Math.PI * 2); ctx.fill();
      for (let k = -8; k < 12; k++) { const x = par(k * 2.8, 0.32); ctx.fillStyle = ['#c46a2a', '#d98b3a', '#a54e24'][Math.abs(k) % 3]; ctx.beginPath(); ctx.arc(x, groundY - 150, 70, 0, Math.PI * 2); ctx.fill(); ctx.fillStyle = '#4b3423'; ctx.fillRect(x - 6, groundY - 110, 12, 110); }
      fence();
      break;
    }
    case 'barn': {
      ctx.fillStyle = '#3a2412'; ctx.fillRect(-10, groundY - 360, W + 20, 360);
      ctx.strokeStyle = '#1f130a'; ctx.lineWidth = 6;
      for (let k = -6; k < 10; k++) { const x = par(k * 3, 0.3); ctx.beginPath(); ctx.moveTo(x, groundY - 360); ctx.lineTo(x + 150, groundY - 250); ctx.lineTo(x + 300, groundY - 360); ctx.stroke(); }
      for (let k = -5; k < 9; k++) { const x = par(k * 3.8, 0.33); ctx.fillStyle = k % 2 ? '#7c2d22' : '#d8cfb6'; ctx.fillRect(x, groundY - 300, 46, 56); ctx.fillStyle = '#e9e1cc'; ctx.font = 'bold 20px system-ui'; ctx.fillText(String(10 + ((k * 7 + 50) % 40)), x + 10, groundY - 264); }
      ctx.fillStyle = 'rgba(255,200,120,0.08)';
      for (let k = 0; k < 6; k++) { ctx.beginPath(); ctx.moveTo(W * (0.1 + k * 0.16), 0); ctx.lineTo(W * (0.05 + k * 0.16), groundY); ctx.lineTo(W * (0.15 + k * 0.16), groundY); ctx.fill(); }
      break;
    }
    case 'arena': {
      ctx.fillStyle = '#0d1424'; ctx.fillRect(-10, groundY - 380, W + 20, 380);
      for (let k = -5; k < 9; k++) { const x = par(k * 4.2, 0.25); ctx.fillStyle = '#1f2a42'; ctx.fillRect(x, groundY - 360, 150, 90); ctx.fillStyle = 'rgba(214,228,255,0.25)'; ctx.fillRect(x + 8, groundY - 352, 134, 74); }
      if (court.special === 'spotlights') {
        for (let k = 0; k < 3; k++) {
          const sx = W * (0.2 + k * 0.3) + Math.sin(pres.time * 0.6 + k) * 120;
          const gr = ctx.createLinearGradient(sx, 0, sx, groundY);
          gr.addColorStop(0, 'rgba(214,228,255,0.18)'); gr.addColorStop(1, 'rgba(214,228,255,0)');
          ctx.fillStyle = gr; ctx.beginPath(); ctx.moveTo(sx - 20, 0); ctx.lineTo(sx + 20, 0); ctx.lineTo(sx + 160, groundY); ctx.lineTo(sx - 160, groundY); ctx.fill();
        }
      }
      break;
    }
  }
  ctx.restore();
  drawCrowd(s);
}

function bulbs(s, y, color) {
  ctx.strokeStyle = 'rgba(0,0,0,0.5)'; ctx.lineWidth = 1.5;
  ctx.beginPath(); for (let x = -20; x < W + 40; x += 40) { const yy = y + Math.sin(x * 0.05) * 6; if (x === -20) ctx.moveTo(x, yy); else ctx.lineTo(x, yy); } ctx.stroke();
  for (let x = -20; x < W + 40; x += 40) { ctx.fillStyle = color; ctx.globalAlpha = 0.6 + 0.4 * Math.sin(pres.time * 3 + x); ctx.beginPath(); ctx.arc(x, y + Math.sin(x * 0.05) * 6 + 5, 3.5, 0, Math.PI * 2); ctx.fill(); }
  ctx.globalAlpha = 1;
}

function fence() {
  ctx.strokeStyle = 'rgba(200,200,210,0.18)'; ctx.lineWidth = 1;
  const top = groundY - 170;
  for (let x = -40; x < W + 40; x += 14) { ctx.beginPath(); ctx.moveTo(x, top); ctx.lineTo(x + 40, groundY - 40); ctx.moveTo(x + 40, top); ctx.lineTo(x, groundY - 40); ctx.stroke(); }
}

function burstSparks(x, y) {
  ctx.fillStyle = '#ffd36b';
  for (let k = 0; k < 8; k++) ctx.fillRect(x + Math.random() * 200, y + Math.random() * 30, 2, 2);
}

function drawCrowd(s) {
  const rows = 3, heat = pres.heat + pres.crowdPop;
  const baseY = groundY - 44;
  for (let r = 0; r < rows; r++) {
    const y = baseY - r * 26;
    const spacing = 22 - r * 2;
    const offset = -((camX * scale * (0.75 - r * 0.1)) % spacing);
    for (let x = offset - spacing; x < W + spacing; x += spacing) {
      const seed = Math.floor((x - offset) / spacing) * 31 + r * 7;
      const bob = Math.max(0, Math.sin(pres.time * (4 + heat * 6) + seed)) * (2 + heat * 7);
      ctx.fillStyle = court.crowd[(seed % 3 + 3) % 3];
      ctx.beginPath(); ctx.ellipse(x, y - bob, 8, 11, 0, 0, Math.PI * 2); ctx.fill();
      ctx.beginPath(); ctx.arc(x, y - 15 - bob, 5.5, 0, Math.PI * 2); ctx.fill();
      if (heat > 0.9 && (seed % 4 === 0)) { ctx.fillRect(x - 1, y - 30 - bob - 10, 2, 14); }
    }
  }
}

function drawFloor(s) {
  const left = wx(-10.5), right = wx(1.6);
  // Floor band (depth) under the play line.
  const g = ctx.createLinearGradient(0, groundY - 30, 0, H);
  g.addColorStop(0, court.floor2); g.addColorStop(0.15, court.floor); g.addColorStop(1, court.floor2);
  ctx.fillStyle = g;
  ctx.fillRect(-50, groundY - 30, W + 100, H - groundY + 80);
  if (court.special === 'led') {
    const leader = s.f[0].score === s.f[1].score ? -1 : (s.f[0].score > s.f[1].score ? 0 : 1);
    const c = leader < 0 ? '#e8f0ff' : look(s.f[leader].ci).main;
    ctx.fillStyle = c; ctx.globalAlpha = 0.12 + 0.05 * Math.sin(pres.time * 2);
    ctx.fillRect(-50, groundY - 30, W + 100, H);
    ctx.globalAlpha = 0.25;
    for (let k = 0; k < 2; k++) { const f = s.f[k]; const gx = wx(f.x); const rg = ctx.createRadialGradient(gx, groundY, 4, gx, groundY, 90); rg.addColorStop(0, look(f.ci).main); rg.addColorStop(1, 'rgba(0,0,0,0)'); ctx.fillStyle = rg; ctx.fillRect(gx - 90, groundY - 40, 180, 90); }
    ctx.globalAlpha = 1;
  }
  // Perspective boards.
  ctx.strokeStyle = 'rgba(0,0,0,0.12)'; ctx.lineWidth = 1;
  for (let k = 0; k < 6; k++) { const y = groundY - 30 + (H - groundY + 30) * (k / 6) ** 1.6; ctx.beginPath(); ctx.moveTo(0, y); ctx.lineTo(W, y); ctx.stroke(); }
  // The key (paint), arc, free-throw and check marks drawn on the floor band.
  const fy = groundY - 30, by = H;
  ctx.fillStyle = court.key;
  trapez(wx(-5.8), wx(1.6), fy, by, 0.25);
  ctx.strokeStyle = court.line; ctx.lineWidth = 2.5;
  ctx.beginPath(); ctx.moveTo(wx(1.6), fy); ctx.lineTo(wx(1.6) + 40, by); ctx.stroke();
  const arcX = wx(-6.75);
  ctx.beginPath(); ctx.moveTo(arcX, fy); ctx.quadraticCurveTo(arcX - 90, (fy + by) / 2, arcX, by); ctx.stroke();
  ctx.setLineDash([6, 6]); ctx.beginPath(); ctx.moveTo(wx(-4.2), fy); ctx.lineTo(wx(-4.2) - 20, by); ctx.stroke(); ctx.setLineDash([]);
  ctx.fillStyle = court.line; ctx.beginPath(); ctx.ellipse(wx(-7.6), groundY + 10, 9, 3.5, 0, 0, Math.PI * 2); ctx.fill();
  ctx.font = '600 11px system-ui'; ctx.globalAlpha = 0.55;
  ctx.fillText('3PT', arcX - 34, groundY + 34); ctx.globalAlpha = 1;
  // Play line.
  ctx.strokeStyle = 'rgba(255,255,255,0.10)'; ctx.lineWidth = 1;
  ctx.beginPath(); ctx.moveTo(left, groundY); ctx.lineTo(right, groundY); ctx.stroke();
  // Court name watermark.
  ctx.globalAlpha = 0.08; ctx.fillStyle = court.line; ctx.font = `900 ${Math.round(scale * 0.7)}px system-ui`;
  ctx.fillText(court.name.toUpperCase(), wx(-9.4), groundY + scale * 0.95); ctx.globalAlpha = 1;
}

function trapez(x0, x1, y0, y1, slant) {
  ctx.beginPath(); ctx.moveTo(x0, y0); ctx.lineTo(x1, y0); ctx.lineTo(x1 + (y1 - y0) * slant, y1); ctx.lineTo(x0 - (y1 - y0) * slant, y1); ctx.closePath(); ctx.fill();
}

function drawHoopBack(s) {
  const c = INFO.court;
  // Stanchion and backboard.
  ctx.fillStyle = '#2b2f38';
  ctx.fillRect(wx(1.75), wy(3.6), 0.14 * scale, 3.6 * scale);
  ctx.fillRect(wx(c.boardX + 0.04), wy(3.5), wx(1.8) - wx(c.boardX + 0.04), 0.1 * scale);
  ctx.fillStyle = 'rgba(220,235,255,0.25)'; ctx.strokeStyle = '#e8eef8'; ctx.lineWidth = 2;
  ctx.fillRect(wx(c.boardX), wy(c.boardTop), 0.06 * scale, (c.boardTop - c.boardBottom) * scale);
  ctx.strokeRect(wx(c.boardX), wy(c.boardTop), 0.06 * scale, (c.boardTop - c.boardBottom) * scale);
  // Back half of the rim.
  ctx.strokeStyle = '#ff6a1a'; ctx.lineWidth = 3;
  ctx.beginPath(); ctx.ellipse(wx(c.rimX), wy(c.rimY), c.rimR * scale, 0.05 * scale, 0, Math.PI, Math.PI * 2); ctx.stroke();
}

function drawHoopFront(s) {
  const c = INFO.court;
  const rx = wx(c.rimX), ry = wy(c.rimY), rr = c.rimR * scale;
  // Net: snaps and whips on makes.
  const snap = pres.net, len = 0.42 * scale * (1 + snap * 0.35);
  ctx.strokeStyle = 'rgba(255,255,255,0.85)'; ctx.lineWidth = 1.2;
  for (let k = 0; k <= 6; k++) {
    const t = k / 6, top = rx - rr + 2 * rr * t, bot = rx - rr * 0.55 + 1.1 * rr * t + Math.sin(pres.time * 30) * snap * 4;
    ctx.beginPath(); ctx.moveTo(top, ry); ctx.lineTo(bot, ry + len); ctx.stroke();
  }
  for (let k = 1; k <= 3; k++) {
    const t = k / 4, w = lerp(rr, rr * 0.55, t);
    ctx.beginPath(); ctx.moveTo(rx - w, ry + len * t); ctx.lineTo(rx + w, ry + len * t); ctx.stroke();
  }
  ctx.strokeStyle = '#ff6a1a'; ctx.lineWidth = 3.5;
  ctx.beginPath(); ctx.ellipse(rx, ry, rr, 0.05 * scale, 0, 0, Math.PI); ctx.stroke();
}

// ------------------------------------------------------------------ fighters

function ik(ax, ay, bx, by, l1, l2, bend) {
  let dx = bx - ax, dy = by - ay, d = Math.hypot(dx, dy) || 1e-6;
  if (d > l1 + l2 - 1e-4) { const k = (l1 + l2 - 1e-4) / d; dx *= k; dy *= k; d = l1 + l2 - 1e-4; bx = ax + dx; by = ay + dy; }
  const a = Math.atan2(dy, dx);
  const c = clamp((l1 * l1 + d * d - l2 * l2) / (2 * l1 * d), -1, 1);
  const ang = Math.acos(c) * bend;
  return [ax + l1 * Math.cos(a + ang), ay + l1 * Math.sin(a + ang), bx, by];
}

/** Pose in meters, x forward (facing), y up, feet origin. Frame-locked: derived from action + action frame. */
function pose(s, i, H) {
  const f = s.f[i];
  const id = INFO.fighters[f.ci].id;
  const holder = s.ball.mode === 0 && s.ball.holder === i;
  const defender = s.ball.mode === 0 && s.ball.holder !== i;
  const t = pres.time;
  const thigh = 0.25 * H, shin = 0.25 * H, torso = 0.30 * H, upper = 0.18 * H, fore = 0.17 * H;
  let crouch = 0.04 * H, lean = 0.08, hipX = 0;
  let footF = [0.1 * H, 0], footB = [-0.1 * H, 0];
  let handF = null, handB = null, headTilt = 0, spin = 0, lie = 0;
  const speed = Math.abs(f.vx) * 60;
  const grounded = f.y < 0.02;

  // Locomotion cycle.
  if (grounded && speed > 0.5) {
    const ph = (f.x * 3.2) * (f.facing >= 0 ? 1 : -1);
    const stride = Math.min(0.24 * H, 0.05 * H * speed);
    footF = [Math.sin(ph) * stride, Math.max(0, Math.cos(ph)) * 0.07 * H];
    footB = [-Math.sin(ph) * stride, Math.max(0, -Math.cos(ph)) * 0.07 * H];
    lean = 0.1 + speed * 0.025;
  }
  if (defender && grounded) { crouch = 0.1 * H; footF[0] += 0.08 * H; footB[0] -= 0.08 * H; handF = [0.3 * H, 0.62 * H]; handB = [-0.05 * H, 0.66 * H]; }

  const a = f.action, af = f.af;
  switch (a) {
    case 'Idle':
      crouch += Math.sin(t * 6 + i) * 0.006 * H;
      break;
    case 'Wall': crouch = 0.17 * H; footF = [0.2 * H, 0]; footB = [-0.2 * H, 0]; handF = [0.16 * H, 0.55 * H]; handB = [0.12 * H, 0.58 * H]; lean = 0.02; break;
    case 'Unplant': crouch = 0.12 * H; break;
    case 'Contest': {
      if (f.y > 0.02) { footF = [0.03 * H, -0.02 * H]; footB = [-0.04 * H, 0.02 * H]; crouch = 0; lean = -0.02; }
      else crouch = 0.12 * H;
      handF = [0.12 * H, 1.32 * H]; handB = [0.02 * H, 1.25 * H]; headTilt = -0.2;
      break;
    }
    case 'Swipe': lean = 0.35; crouch = 0.1 * H; handF = [0.55 * H, 0.45 * H]; footF = [0.22 * H, 0]; break;
    case 'OffBalance': lean = -0.3; handF = [0.2 * H, 0.95 * H]; handB = [-0.25 * H, 0.9 * H]; footB = [-0.25 * H, 0]; break;
    case 'Stumble': { const k = clamp(af / 18, 0, 1); lie = ease(k) * 1.2; lean = -0.4; handF = [0.3 * H, 0.8 * H]; handB = [-0.3 * H, 0.9 * H]; break; }
    case 'Knockdown': lie = clamp(af / 10, 0, 1) * 1.45; lean = -0.2; break;
    case 'Celebrate': handF = [0.12 * H, 1.3 * H]; handB = [-0.12 * H, 1.3 * H]; crouch = Math.abs(Math.sin(af * 0.2)) * 0.05 * H; break;
    case 'Shake': {
      const k = Math.sin(clamp(af / 18, 0, 1) * Math.PI);
      if (f.slot === 0) { lean = 0.25 + 0.15 * k; crouch = 0.1 * H; }
      if (f.slot === 1) { lean = -0.15 * k; footF = [0.2 * H, 0.05 * H * k]; crouch = 0.08 * H; }
      if (f.slot === 2) { spin = k * Math.PI; crouch = 0.1 * H; }
      if (f.slot === 3) { lean = 0.05; crouch = 0.12 * H * k; }
      break;
    }
    case 'GatherJumper': case 'GatherFinish': case 'ShotRecover': {
      const finish = a === 'GatherFinish';
      const airborne = f.y > 0.02;
      if (!airborne && a !== 'ShotRecover') {
        crouch = 0.06 * H + 0.1 * H * clamp(af / Math.max(1, f.takeoff), 0, 1);
        handF = [0.2 * H, 0.95 * H]; handB = [0.14 * H, 0.9 * H];
        if (finish) { footF = [0.24 * H, 0]; footB = [-0.16 * H, 0.05 * H]; lean = 0.25; }
      } else if (a === 'ShotRecover') {
        crouch = 0; handF = [0.24 * H, 1.28 * H]; handB = [0.12 * H, 1.15 * H]; footF = [0.04 * H, -0.02 * H]; footB = [-0.03 * H, 0.03 * H];
      } else if (finish) {
        crouch = 0; footF = [0.12 * H, 0.28 * H]; footB = [-0.08 * H, -0.02 * H];
        handF = [0.34 * H, 1.32 * H]; handB = [0.14 * H, 1.0 * H]; lean = 0.12;
        if (f.kind === 'Dunk') { handF = [0.3 * H, 1.36 * H]; handB = [0.22 * H, 1.3 * H]; }
      } else {
        crouch = 0; footF = [0.03 * H, -0.03 * H]; footB = [-0.03 * H, -0.02 * H];
        handF = [0.12 * H, 1.2 * H]; handB = [0.04 * H, 1.12 * H]; lean = 0.02;
        if (f.flags & 2048) { handF = [-0.05 * H, 1.36 * H]; handB = [0.18 * H, 0.7 * H]; lean = -0.12; } // hook
      }
      break;
    }
    case 'PostUp': lean = -0.18; crouch = 0.14 * H; footF = [0.2 * H, 0]; footB = [-0.18 * H, 0]; handB = [-0.25 * H, 0.6 * H]; break;
    case 'Trick': {
      const behaviorIdx = f.slot;
      lean = 0.2; crouch = 0.1 * H;
      if (id === 'meera' && behaviorIdx === 1) { lean = -0.25; footF = [0.25 * H, 0.08 * H]; }
      if (id === 'juno' && behaviorIdx === 1) { lean = 0; crouch = 0.15 * H; }
      if (id === 'matas' && behaviorIdx === 0) { spin = clamp(af / 20, 0, 1) * Math.PI * 2; }
      if (id === 'matas' && behaviorIdx === 2) { crouch = 0.12 * H; handF = [0.1 * H, 1.25 * H]; handB = [-0.1 * H, 1.25 * H]; footF = [0.22 * H, 0]; footB = [-0.22 * H, 0]; }
      if (behaviorIdx === 2 && id !== 'matas') { lean = 0.45; handF = [0.6 * H, 0.5 * H]; }
      break;
    }
    case 'Finisher': {
      const k = s.fin.frames ? s.fin.frame / s.fin.frames : 0;
      if (s.fin.attacker === i) { spin = k * Math.PI * 4; handF = [0.2 * H, 1.3 * H]; handB = [-0.1 * H, 1.2 * H]; crouch = 0; }
      else { lean = -0.1; handF = [0.15 * H, 1.1 * H]; }
      break;
    }
  }

  const hipY = 0.53 * H - crouch;
  const hip = [hipX, hipY];
  const neck = [hipX + Math.sin(lean) * torso, hipY + Math.cos(lean) * torso];
  const head = [neck[0] + Math.sin(lean + headTilt) * 0.09 * H, neck[1] + 0.09 * H];
  const sh = [neck[0] - 0.01 * H, neck[1] - 0.03 * H];
  // Ball hand follows the dribble when holding the ball on the ground.
  if (holder && !handF) {
    const dribble = Math.abs(Math.sin(t * 7.5 + i)) * (0.55 * H);
    handF = [0.3 * H, 0.18 * H + dribble * 0.55];
    handB = [0.02 * H, 0.65 * H];
  }
  if (!handF) handF = [0.18 * H, 0.5 * H];
  if (!handB) handB = [-0.08 * H, 0.48 * H];
  const kneeF = ik(hip[0], hip[1], footF[0], footF[1], thigh, shin, 1);
  const kneeB = ik(hip[0], hip[1], footB[0], footB[1], thigh, shin, 1);
  const elbF = ik(sh[0], sh[1], handF[0], handF[1], upper, fore, -1);
  const elbB = ik(sh[0], sh[1], handB[0], handB[1], upper, fore, -1);
  return { hip, neck, head, sh, kneeF, kneeB, elbF, elbB, spin, lie, H };
}

function limb(points, width, color) {
  ctx.strokeStyle = color; ctx.lineWidth = width; ctx.lineCap = 'round'; ctx.lineJoin = 'round';
  ctx.beginPath(); ctx.moveTo(points[0], points[1]);
  for (let k = 2; k < points.length; k += 2) ctx.lineTo(points[k], points[k + 1]);
  ctx.stroke();
}

function drawFighter(s, i, ghost = null) {
  const f = ghost || s.f[i];
  const def = INFO.fighters[f.ci];
  const L = FIGHTER_LOOK[def.id];
  const H = def.height;
  const P = pose(ghost ? Object.assign({}, s, { f: s.f.map((x, k) => (k === i ? Object.assign({}, x, ghost) : x)) }) : s, i, H);
  const dir = f.facing >= 0 ? 1 : -1;
  const ox = wx(f.x), oy = wy(f.y);
  const S = scale;
  // Shadow.
  if (!ghost) {
    const sw = (0.35 + 0.1 * H) * S * (1 - Math.min(0.6, f.y * 0.5));
    ctx.fillStyle = 'rgba(0,0,0,0.35)'; ctx.beginPath(); ctx.ellipse(ox, groundY + 4, sw, sw * 0.18, 0, 0, Math.PI * 2); ctx.fill();
  }
  ctx.save();
  ctx.translate(ox, oy);
  if (P.lie) { ctx.translate(0, -0.1 * S); ctx.rotate(-P.lie * dir); }
  const spinScale = Math.cos(P.spin);
  ctx.scale(dir * (Math.abs(spinScale) < 0.15 ? 0.15 * Math.sign(spinScale || 1) : spinScale), 1);
  const X = p => p[0] * S, Y = p => -p[1] * S;
  const lw = Math.max(3, 0.075 * S * (H / 1.9));
  const outline = 'rgba(0,0,0,0.55)';
  // Rim light (fighters lit like heroes): a soft identity-colored halo.
  if (!ghost) {
    const g = ctx.createRadialGradient(X(P.neck), Y(P.neck), 2, X(P.neck), Y(P.neck), 0.9 * S);
    g.addColorStop(0, hexA(L.main, 0.22 + 0.2 * pres.heat * 0.3)); g.addColorStop(1, hexA(L.main, 0));
    ctx.fillStyle = g; ctx.beginPath(); ctx.arc(X(P.neck), Y(P.neck), 0.9 * S, 0, Math.PI * 2); ctx.fill();
  }
  const skin = L.skin, shorts = L.main, jersey = L.main, trim = L.trim;
  // Back limbs.
  limb([X(P.hip), Y(P.hip), P.kneeB[0] * S, -P.kneeB[1] * S, P.kneeB[2] * S, -P.kneeB[3] * S], lw * 1.25 + 2, outline);
  limb([X(P.hip), Y(P.hip), P.kneeB[0] * S, -P.kneeB[1] * S, P.kneeB[2] * S, -P.kneeB[3] * S], lw * 1.25, shade(skin, -0.25));
  limb([X(P.sh), Y(P.sh), P.elbB[0] * S, -P.elbB[1] * S, P.elbB[2] * S, -P.elbB[3] * S], lw + 2, outline);
  limb([X(P.sh), Y(P.sh), P.elbB[0] * S, -P.elbB[1] * S, P.elbB[2] * S, -P.elbB[3] * S], lw, shade(skin, -0.25));
  hand(P.elbB[2] * S, -P.elbB[3] * S, lw * 0.8, shade(skin, -0.25));
  // Shoes.
  shoe(P.kneeB[2] * S, -P.kneeB[3] * S, lw, def.id === 'juno' ? L.alt : '#f2f2f2', def.id === 'juno');
  // Torso: jersey with trim, shorts.
  const hx = X(P.hip), hy = Y(P.hip), nx = X(P.neck), ny = Y(P.neck);
  ctx.fillStyle = shade(shorts, -0.15); ctx.strokeStyle = outline; ctx.lineWidth = 2;
  ctx.beginPath(); ctx.ellipse(hx, hy, lw * 1.25, lw * 1.0, 0, 0, Math.PI * 2); ctx.fill(); ctx.stroke();
  ctx.save();
  ctx.beginPath();
  const tw = lw * (def.id === 'matas' ? 1.55 : 1.3);
  ctx.moveTo(hx - tw * 0.9, hy); ctx.lineTo(nx - tw, ny + lw * 0.2); ctx.lineTo(nx + tw, ny + lw * 0.2); ctx.lineTo(hx + tw * 0.9, hy); ctx.closePath();
  ctx.fillStyle = jersey; ctx.fill(); ctx.stroke();
  ctx.fillStyle = trim; ctx.globalAlpha = 0.9; ctx.fillRect(Math.min(hx, nx) - tw, hy - lw * 0.55, tw * 2.2, lw * 0.32); ctx.globalAlpha = 1;
  // Sweat sheen builds over the match.
  const sweat = pres.sweat[i];
  if (sweat > 0) { ctx.fillStyle = `rgba(255,255,255,${0.1 + sweat * 0.25})`; ctx.beginPath(); ctx.ellipse(nx + tw * 0.4, ny + lw, tw * 0.25, lw * 0.9, 0.3, 0, Math.PI * 2); ctx.fill(); }
  ctx.restore();
  // Head + hair.
  const hdx = X(P.head), hdy = Y(P.head), hr = 0.07 * H * S;
  ctx.fillStyle = skin; ctx.strokeStyle = outline; ctx.lineWidth = 2;
  ctx.beginPath(); ctx.arc(hdx, hdy, hr, 0, Math.PI * 2); ctx.fill(); ctx.stroke();
  hair(def.id, hdx, hdy, hr, L);
  // Front limbs.
  limb([X(P.hip), Y(P.hip), P.kneeF[0] * S, -P.kneeF[1] * S, P.kneeF[2] * S, -P.kneeF[3] * S], lw * 1.3 + 2, outline);
  limb([X(P.hip), Y(P.hip), P.kneeF[0] * S, -P.kneeF[1] * S, P.kneeF[2] * S, -P.kneeF[3] * S], lw * 1.3, skin);
  if (def.id === 'matas') { limb([P.kneeF[0] * S - 1, -P.kneeF[1] * S - 4, P.kneeF[0] * S + 1, -P.kneeF[1] * S + 4], lw * 1.4, '#2a2a2a'); }
  shoe(P.kneeF[2] * S, -P.kneeF[3] * S, lw, def.id === 'juno' ? L.alt : '#ffffff', def.id === 'juno');
  limb([X(P.sh), Y(P.sh), P.elbF[0] * S, -P.elbF[1] * S, P.elbF[2] * S, -P.elbF[3] * S], lw * 1.05 + 2, outline);
  limb([X(P.sh), Y(P.sh), P.elbF[0] * S, -P.elbF[1] * S, P.elbF[2] * S, -P.elbF[3] * S], lw * 1.05, skin);
  if (def.id === 'kofi') limb([P.elbF[0] * S, -P.elbF[1] * S, P.elbF[2] * S, -P.elbF[3] * S], lw * 1.1, L.alt); // kente-pattern sleeve
  if (def.id === 'meera') limb([X(P.sh) * 0.5 + P.elbF[0] * S * 0.5, (Y(P.sh) - P.elbF[1] * S) * 0.5, P.elbF[2] * S, -P.elbF[3] * S], lw * 1.1, L.alt); // silver shooting sleeve
  hand(P.elbF[2] * S, -P.elbF[3] * S, lw * 0.85, skin);
  ctx.restore();

  // Heat check flame / stumble stars.
  if (!ghost && (f.status & ST.Heat)) {
    for (let k = 0; k < 3; k++) { ctx.fillStyle = `rgba(255,${120 + k * 40},40,${0.5 - k * 0.12})`; ctx.beginPath(); ctx.arc(ox + (Math.random() - 0.5) * 10, wy(f.y + H * 1.1) - k * 6, 6 - k, 0, Math.PI * 2); ctx.fill(); }
  }
  if (!ghost && f.action === 'Stumble') { ctx.font = '16px system-ui'; ctx.fillStyle = '#ffe066'; ctx.fillText('✦ ✦', ox - 14, wy(H * 0.6) + Math.sin(pres.time * 10) * 3); }
}

function shoe(x, y, lw, color, glow) {
  ctx.fillStyle = color; ctx.strokeStyle = 'rgba(0,0,0,0.6)'; ctx.lineWidth = 1.5;
  ctx.beginPath(); ctx.ellipse(x + lw * 0.5, y + lw * 0.2, lw * 1.05, lw * 0.5, 0, 0, Math.PI * 2); ctx.fill(); ctx.stroke();
  if (glow) { ctx.fillStyle = 'rgba(33,211,230,0.6)'; ctx.fillRect(x - lw * 0.4, y + lw * 0.45, lw * 1.8, 2); }
}

function hand(x, y, r, color) {
  ctx.fillStyle = color; ctx.strokeStyle = 'rgba(0,0,0,0.55)'; ctx.lineWidth = 1.5;
  ctx.beginPath(); ctx.arc(x, y, r * 0.75, 0, Math.PI * 2); ctx.fill(); ctx.stroke();
}

function hair(id, x, y, r, L) {
  ctx.lineWidth = 2;
  switch (id) {
    case 'kofi':
      for (let k = -3; k <= 3; k++) {
        const a = -Math.PI / 2 + k * 0.32;
        const x1 = x + Math.cos(a) * r * 0.85, y1 = y + Math.sin(a) * r * 0.85, x2 = x + Math.cos(a) * r * 1.55, y2 = y + Math.sin(a) * r * 1.55;
        limb([x1, y1, x2, y2], r * 0.28, L.hair); ctx.fillStyle = L.tip; ctx.beginPath(); ctx.arc(x2, y2, r * 0.14, 0, Math.PI * 2); ctx.fill();
      }
      break;
    case 'meera':
      ctx.fillStyle = L.hair; ctx.beginPath(); ctx.arc(x, y - r * 0.15, r * 1.02, Math.PI * 0.95, Math.PI * 2.05); ctx.fill();
      limb([x - r * 0.9, y, x - r * 1.5, y + r * 1.4, x - r * 1.3, y + r * 2.6], r * 0.32, L.hair);
      ctx.fillStyle = L.tip; for (let k = 1; k <= 3; k++) { ctx.beginPath(); ctx.arc(x - r * (1.2 + k * 0.08), y + r * (0.8 + k * 0.55), r * 0.13, 0, Math.PI * 2); ctx.fill(); }
      break;
    case 'juno':
      ctx.fillStyle = L.hair; ctx.beginPath(); ctx.ellipse(x - r * 0.1, y - r * 0.05, r * 1.12, r * 1.05, 0, Math.PI * 0.85, Math.PI * 2.15); ctx.fill();
      ctx.fillRect(x - r * 1.15, y - r * 0.2, r * 0.5, r * 0.9);
      ctx.fillStyle = L.tip; ctx.fillRect(x - r * 0.3, y - r * 1.0, r * 0.22, r * 0.8);
      break;
    case 'matas':
      ctx.fillStyle = L.hair; ctx.beginPath(); ctx.arc(x, y - r * 0.2, r * 0.98, Math.PI * 1.05, Math.PI * 1.95); ctx.fill();
      ctx.beginPath(); ctx.ellipse(x + r * 0.35, y + r * 0.6, r * 0.6, r * 0.42, 0.2, 0, Math.PI); ctx.fill();
      break;
  }
}

function drawTrail(i) {
  if (!cur) return;
  for (const t of pres.trails[i]) {
    ctx.save(); ctx.globalAlpha = t.t * 2.2; ctx.globalCompositeOperation = 'lighter';
    const tint = i === 0 ? '#ff2a7a' : '#21d3e6';
    ctx.filter = 'none';
    drawGhost(cur, i, t, tint);
    ctx.restore();
  }
}

function drawGhost(s, i, t, tint) {
  const f = s.f[i];
  const ox = wx(t.x), oy = wy(t.y);
  const H = INFO.fighters[f.ci].height;
  ctx.fillStyle = tint;
  ctx.fillRect(ox - 0.12 * scale, oy - H * scale * 0.95, 0.24 * scale, H * scale * 0.9);
}

function hexA(hex, a) {
  const n = parseInt(hex.slice(1), 16);
  return `rgba(${(n >> 16) & 255},${(n >> 8) & 255},${n & 255},${a})`;
}

function shade(hex, k) {
  const n = parseInt(hex.slice(1), 16);
  const f = c => Math.round(clamp(k < 0 ? c * (1 + k) : c + (255 - c) * k, 0, 255));
  return `rgb(${f((n >> 16) & 255)},${f((n >> 8) & 255)},${f(n & 255)})`;
}

// ------------------------------------------------------------------ ball

function drawBall(s) {
  const r = INFO.court.ballR * scale * 1.15;
  let bx = s.ball.x, by = s.ball.y;
  if (s.ball.mode === 0) {
    const f = s.f[s.ball.holder];
    const H = INFO.fighters[f.ci].height;
    const dir = f.facing >= 0 ? 1 : -1;
    const busy = f.action !== 'Idle' && f.action !== 'Shake' && f.action !== 'PostUp';
    if (busy) { bx = f.x + dir * 0.16 * H; by = f.y + 1.2 * H; }
    else { bx = f.x + dir * 0.3 * H; by = 0.12 + Math.abs(Math.sin(pres.time * 7.5 + s.ball.holder)) * 0.55 * H * 0.55 + 0.05; }
    if (s.fin.active) { by = f.y + 1.35 * H; }
  }
  const x = wx(bx), y = wy(by);
  ctx.fillStyle = 'rgba(0,0,0,0.3)'; ctx.beginPath(); ctx.ellipse(x, groundY + 3, r * 0.9, r * 0.25, 0, 0, Math.PI * 2); ctx.fill();
  const g = ctx.createRadialGradient(x - r * 0.4, y - r * 0.4, r * 0.1, x, y, r);
  g.addColorStop(0, '#ffb066'); g.addColorStop(1, '#d9590b');
  ctx.fillStyle = g; ctx.strokeStyle = '#3a1a05'; ctx.lineWidth = 1.5;
  ctx.beginPath(); ctx.arc(x, y, r, 0, Math.PI * 2); ctx.fill(); ctx.stroke();
  const rot = pres.time * (s.ball.mode === 1 ? -14 : 0);
  ctx.beginPath(); ctx.moveTo(x + Math.cos(rot) * r, y + Math.sin(rot) * r); ctx.lineTo(x - Math.cos(rot) * r, y - Math.sin(rot) * r); ctx.stroke();
  ctx.beginPath(); ctx.ellipse(x, y, r * 0.45, r, rot, 0, Math.PI * 2); ctx.stroke();
}

function drawParticles() {
  for (const p of pres.particles) {
    ctx.globalAlpha = clamp(p.life / p.max, 0, 1);
    ctx.fillStyle = p.color;
    const sz = p.size * scale * (p.kind === 'dust' ? 2.4 : 1);
    ctx.beginPath(); ctx.arc(wx(p.x), wy(p.y), sz, 0, Math.PI * 2); ctx.fill();
  }
  ctx.globalAlpha = 1;
}

function drawBoxes(s) {
  for (let i = 0; i < 2; i++) {
    const f = s.f[i], d = INFO.fighters[f.ci];
    ctx.strokeStyle = 'rgba(80,255,120,0.9)'; ctx.lineWidth = 1.5;
    ctx.strokeRect(wx(f.x - d.push), wy(f.y + d.height), d.push * 2 * scale, d.height * scale);
    ctx.strokeStyle = 'rgba(80,160,255,0.8)';
    ctx.beginPath(); ctx.moveTo(wx(f.x), wy(f.y + d.reach)); ctx.lineTo(wx(f.x), wy(f.y + d.reach + d.jump)); ctx.stroke();
    ctx.fillStyle = 'rgba(255,255,255,0.85)'; ctx.font = '11px ui-monospace, monospace';
    ctx.fillText(`${f.action} f${f.af}${f.peak ? ' pk' + f.peak : ''} bal ${f.balance.toFixed(0)} pl ${f.plant}`, wx(f.x) - 50, wy(f.y + d.height) - 8);
  }
}

// ------------------------------------------------------------------ weather & set pieces

const drops = [];
function drawWeatherFront(s, dt) {
  const w = court.weather;
  if (w === 'rain' || w === 'windowrain') {
    const heavy = s.fin.active && w === 'rain' ? 3 : 1;
    while (drops.length < 160 * heavy) drops.push({ x: Math.random() * W, y: Math.random() * H, v: 600 + Math.random() * 500 });
    ctx.strokeStyle = w === 'rain' ? 'rgba(180,200,255,0.35)' : 'rgba(160,180,210,0.15)'; ctx.lineWidth = 1;
    ctx.beginPath();
    for (const d of drops) { d.y += d.v * dt; d.x -= d.v * dt * 0.15; if (d.y > H) { d.y = -10; d.x = Math.random() * W * 1.2; } ctx.moveTo(d.x, d.y); ctx.lineTo(d.x + 3, d.y - 12); }
    ctx.stroke();
    if (drops.length > 160 * heavy) drops.length = 160 * heavy;
  } else if (w === 'leaves') {
    while (drops.length < 40) drops.push({ x: Math.random() * W, y: Math.random() * H, v: 40 + Math.random() * 60, a: Math.random() * 6 });
    for (const d of drops) { d.x += (d.v + (s.fin.active ? 300 : 0)) * dt; d.y += Math.sin(pres.time + d.a) * 30 * dt + 10 * dt; d.a += dt; if (d.x > W + 20) { d.x = -20; d.y = Math.random() * H * 0.8; }
      ctx.fillStyle = ['#d9822b', '#b54a1f', '#e0a33a'][Math.floor(d.a * 10) % 3]; ctx.save(); ctx.translate(d.x, d.y); ctx.rotate(d.a); ctx.fillRect(-4, -2, 8, 4); ctx.restore(); }
  } else if (w === 'dust' || w === 'haze') {
    ctx.fillStyle = w === 'dust' ? 'rgba(255,220,170,0.05)' : 'rgba(200,210,230,0.05)';
    for (let k = 0; k < 4; k++) { ctx.beginPath(); ctx.ellipse(((pres.time * 12 + k * 300) % (W + 400)) - 200, H * (0.25 + k * 0.12), 260, 60, 0, 0, Math.PI * 2); ctx.fill(); }
  } else if (w === 'gulls') {
    ctx.strokeStyle = 'rgba(40,40,50,0.7)'; ctx.lineWidth = 2;
    for (let k = 0; k < 4; k++) { const gx = ((pres.time * 40 + k * 260) % (W + 200)) - 100, gy = H * 0.15 + Math.sin(pres.time + k) * 20; ctx.beginPath(); ctx.moveTo(gx - 10, gy); ctx.quadraticCurveTo(gx - 5, gy - 6, gx, gy); ctx.quadraticCurveTo(gx + 5, gy - 6, gx + 10, gy); ctx.stroke(); }
  }
  if (court.special === 'lightning' && pres.lightning > 0.6) { ctx.fillStyle = 'rgba(255,40,40,0.08)'; ctx.fillRect(0, 0, W, H); }
}

// ------------------------------------------------------------------ HUD

function drawHud(s) {
  const pad = 14 + (W > 900 ? 10 : 0);
  const top = pad + SAFE.t, left = pad + SAFE.l, right = pad + SAFE.r;
  const pw = Math.min(360, (W - SAFE.l - SAFE.r) * 0.36);
  panel(s, 0, left, top, pw, false);
  panel(s, 1, W - right - pw, top, pw, true);
  // Clock.
  const secs = Math.ceil(s.roundClock / 60);
  const cl = s.suddenDeath ? 'SD' : `${Math.floor(secs / 60)}:${String(secs % 60).padStart(2, '0')}`;
  ctx.textAlign = 'center';
  ctx.fillStyle = 'rgba(10,12,18,0.75)'; roundRect(W / 2 - 52, top, 104, 58, 12); ctx.fill();
  ctx.fillStyle = '#fff'; ctx.font = '900 26px system-ui'; ctx.fillText(cl, W / 2, top + 30);
  const sc = s.shotClock / 60;
  ctx.font = '700 13px system-ui'; ctx.fillStyle = sc < 3 ? '#ff6b6b' : '#9aa3b5';
  ctx.fillText(s.mode === 1 ? 'TRAINING' : `SHOT ${sc < 3 ? sc.toFixed(1) : Math.ceil(sc)}  ·  R${s.round + 1}`, W / 2, top + 50);
  ctx.textAlign = 'left';

  // Callouts.
  let cy = H * 0.36;
  for (const c of pres.callouts) {
    const k = c.t < 0.12 ? ease(c.t / 0.12) : 1;
    const fade = c.t > 1.0 ? 1 - (c.t - 1.0) / 0.4 : 1;
    const size = (c.big ? 64 : 34) * (0.6 + 0.4 * k) * Math.min(1, W / 900 + 0.3);
    ctx.save(); ctx.globalAlpha = fade; ctx.textAlign = 'center'; ctx.font = `italic 900 ${size}px system-ui`;
    ctx.lineWidth = 7; ctx.strokeStyle = 'rgba(0,0,0,0.65)'; ctx.strokeText(c.text, W / 2, cy);
    ctx.fillStyle = c.color; ctx.fillText(c.text, W / 2, cy);
    ctx.restore();
    cy += size * 0.95;
  }

  // Clear indicator and shot readout.
  if (s.needsClear && s.ball.mode === 0) {
    const holder = s.ball.holder;
    ctx.textAlign = 'center'; ctx.font = '800 15px system-ui'; ctx.fillStyle = '#ffd166';
    ctx.fillText(`CLEAR IT PAST THE ARC${mode !== 'watch' && holder === 0 ? ' ←' : ''}`, wx(s.f[holder].x), wy(s.f[holder].y + 2.6));
    ctx.textAlign = 'left';
  }
  if (pres.lastShot && s.shot.active) {
    const f = s.f[pres.lastShot.shooter];
    ctx.textAlign = 'center'; ctx.font = '700 12px ui-monospace, monospace'; ctx.fillStyle = gradeColor(pres.lastShot.grade);
    ctx.fillText(`${pres.lastShot.grade.toUpperCase()} · ${pres.lastShot.zone} · P ${(s.shot.p * 100).toFixed(0)}% · Q ${s.shot.q.toFixed(2)}`, wx(f.x), wy(f.y + 2.45));
    ctx.textAlign = 'left';
  }

  // Denial ring during an opponent Finisher.
  if (s.fin.active) {
    const defender = 1 - s.fin.attacker;
    const name = INFO.fighters[s.f[s.fin.attacker].ci].finisher;
    ctx.textAlign = 'center'; ctx.font = 'italic 900 22px system-ui'; ctx.fillStyle = '#fff';
    ctx.fillText(name.toUpperCase(), W / 2, H * 0.2);
    if (s.fin.ring && !s.fin.attempted) {
      const cx = W / 2, cyy = H * 0.62, target = 34;
      const toWindow = s.fin.denyStart - s.fin.frame;
      const r = target + Math.max(0, toWindow) * 2.2;
      const inside = toWindow <= 0 && toWindow > -s.fin.denyFrames;
      ctx.lineWidth = 6; ctx.strokeStyle = inside ? '#2fd3c8' : 'rgba(255,255,255,0.85)';
      ctx.beginPath(); ctx.arc(cx, cyy, r, 0, Math.PI * 2); ctx.stroke();
      ctx.lineWidth = 3; ctx.strokeStyle = 'rgba(47,211,200,0.7)'; ctx.beginPath(); ctx.arc(cx, cyy, target, 0, Math.PI * 2); ctx.stroke();
      ctx.font = '800 14px system-ui'; ctx.fillStyle = '#fff';
      ctx.fillText(defender === 0 && mode !== 'watch' ? 'DENY! (I / FIN)' : 'DENIAL RING', cx, cyy + 5);
    }
    ctx.textAlign = 'left';
  }
  // Touch: FIN button lights at full Hype, or during an opponent Finisher.
  const fin = document.getElementById('btnFin');
  if (fin) { const me = s.f[0]; fin.classList.toggle('lit', me.hype >= INFO.match.hypeMax || (s.fin.active && s.fin.attacker === 1 && s.fin.ring)); fin.textContent = s.fin.active && s.fin.attacker === 1 ? 'DENY' : 'FIN'; }

  if (s.phase === 'MatchEnd' && s.phaseFrame > 90) {
    ctx.fillStyle = 'rgba(0,0,0,0.5)'; ctx.fillRect(0, 0, W, H);
    ctx.textAlign = 'center'; ctx.fillStyle = '#fff'; ctx.font = 'italic 900 48px system-ui';
    const w = s.f[s.matchWinner];
    ctx.fillText(`${INFO.fighters[w.ci].name.toUpperCase()} WINS`, W / 2, H * 0.45);
    ctx.font = '600 16px system-ui'; ctx.fillStyle = '#c9d1dc';
    ctx.fillText(`Rounds ${s.f[0].rounds}-${s.f[1].rounds} · FG ${s.f[0].fgm}/${s.f[0].fga} vs ${s.f[1].fgm}/${s.f[1].fga} · press Esc for the menu`, W / 2, H * 0.45 + 36);
    ctx.textAlign = 'left';
  }
}

function gradeColor(g) { return g === 'Perfect' ? '#2fd3c8' : g === 'Good' ? '#ffd166' : '#ff6b6b'; }

function panel(s, i, x, y, w, right) {
  const f = s.f[i];
  const def = INFO.fighters[f.ci];
  const L = FIGHTER_LOOK[def.id];
  ctx.fillStyle = 'rgba(10,12,18,0.75)'; roundRect(x, y, w, 78, 14); ctx.fill();
  ctx.fillStyle = L.main; roundRect(right ? x + w - 6 : x, y, 6, 78, 3); ctx.fill();
  const tx = right ? x + w - 16 : x + 16;
  ctx.textAlign = right ? 'right' : 'left';
  ctx.fillStyle = '#fff'; ctx.font = '800 15px system-ui';
  ctx.fillText(`${def.name.split(' ')[0].toUpperCase()}  "${def.nick}"`, tx, y + 22);
  ctx.font = '900 30px system-ui';
  const scoreX = right ? x + 18 : x + w - 18;
  ctx.textAlign = right ? 'left' : 'right';
  ctx.fillText(String(f.score), scoreX, y + 34);
  // Score bar toward 7.
  const bx = x + 16, bw = w - 32;
  const target = INFO.match.target;
  ctx.fillStyle = 'rgba(255,255,255,0.08)'; roundRect(bx, y + 40, bw, 7, 3); ctx.fill();
  ctx.fillStyle = (f.status & ST.Heat) ? '#ff6a3d' : L.main;
  const sw = bw * Math.min(1, f.score / target);
  roundRect(right ? bx + bw - sw : bx, y + 40, sw, 7, 3); ctx.fill();
  // Hype: 3 segments.
  const seg = (bw - 8) / 3;
  for (let k = 0; k < 3; k++) {
    const fill = clamp((f.hype - k * INFO.match.hypeBar) / INFO.match.hypeBar, 0, 1);
    const sx = right ? bx + bw - (k + 1) * seg - k * 4 : bx + k * (seg + 4);
    ctx.fillStyle = 'rgba(255,255,255,0.08)'; roundRect(sx, y + 52, seg, 9, 3); ctx.fill();
    ctx.fillStyle = fill >= 1 ? '#2fd3c8' : 'rgba(47,211,200,0.55)';
    if (fill > 0) { roundRect(right ? sx + seg * (1 - fill) : sx, y + 52, seg * fill, 9, 3); ctx.fill(); }
  }
  // Balance.
  const bal = clamp(f.balance / 100, 0, 1);
  ctx.fillStyle = 'rgba(255,255,255,0.08)'; roundRect(bx, y + 66, bw * 0.6, 5, 2); ctx.fill();
  ctx.fillStyle = bal > 0.5 ? '#7bd88f' : bal > 0.25 ? '#ffd166' : '#ff6b6b';
  roundRect(right ? bx + bw * 0.4 + bw * 0.6 * (1 - bal) : bx, y + 66, bw * 0.6 * bal, 5, 2); ctx.fill();
  // Round pips + Rhythm + combo.
  ctx.textAlign = 'left';
  for (let k = 0; k < s.roundsToWin; k++) {
    ctx.fillStyle = k < f.rounds ? L.main : 'rgba(255,255,255,0.18)';
    const px = right ? bx + k * 14 : bx + bw - 10 - k * 14;
    ctx.beginPath(); ctx.arc(px + 4, y + 69, 4, 0, Math.PI * 2); ctx.fill();
  }
  ctx.font = '700 11px system-ui'; ctx.fillStyle = L.alt;
  const extras = [];
  if (f.rhythm > 0) extras.push('RHYTHM ' + '●'.repeat(f.rhythm));
  if (f.combo > 1 && f.action === 'Shake') extras.push('COMBO ×' + f.combo);
  if (f.status & ST.Heat) extras.push('HEAT CHECK');
  if (extras.length) { ctx.textAlign = right ? 'right' : 'left'; ctx.fillText(extras.join('  '), right ? x + w - 16 : x + 16, y + 94); }
  ctx.textAlign = 'left';
}

function roundRect(x, y, w, h, r) {
  if (w <= 0) { ctx.beginPath(); return; }
  r = Math.min(r, w / 2, h / 2);
  ctx.beginPath(); ctx.moveTo(x + r, y); ctx.arcTo(x + w, y, x + w, y + h, r); ctx.arcTo(x + w, y + h, x, y + h, r); ctx.arcTo(x, y + h, x, y, r); ctx.arcTo(x, y, x + w, y, r); ctx.closePath();
}
