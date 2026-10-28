import type { AgentSessionDto, AgentState } from '../../api/types';
import { BW, GROUND, lotX, SCENE_H, SMALL_BW, visibleRange } from './camera';
import type { Lot } from './street';

// Pixel art ported from docs/superpowers/specs/agent-town/prototype/index.html. All drawing is in logical
// pixels; the view scales the canvas by an integer factor.

/** The canvas calls the art uses; tests pass a recording fake. */
export type Draw2D = Pick<
  CanvasRenderingContext2D,
  'fillRect' | 'clearRect' | 'save' | 'restore' | 'beginPath' | 'rect' | 'clip' | 'translate' | 'fillStyle' | 'globalAlpha'
>;

export interface TownFrame {
  /** In street order. */
  lots: Lot[];
  camX: number;
  viewW: number;
  now: number;
  /** Day/night position 0..1, as in the prototype. */
  phase: number;
  reducedMotion: boolean;
}

export const STATE_COLOR: Record<AgentState, string> = {
  waiting: '#ffc857',
  failed: '#ff6b5e',
  working: '#6cc3ff',
  done: '#7ee081',
  idle: '#8790c2',
};
/** Minimap and swatch colour for a lot with no session. */
export const NO_SESSION_COLOR = '#4a5068';

const TILE = 320; // hills, specks and trees repeat every TILE pixels
const NIGHT = '#090e2c';
const PANTS = '#2b2f4a', SKIN = '#f1c8a0', EYE = '#1b1f3a';
const HATS = ['#ff6b5e', '#6cc3ff', '#ffc857', '#7ee081', '#c79bff', '#ff9ad0'];
const SHIRTS = ['#3f6fb5', '#b5533f', '#4f9a6a', '#8a5fb0', '#c08a2c', '#3a8f9a'];
const CONF = ['#ff6b5e', '#6cc3ff', '#ffc857', '#7ee081', '#c79bff', '#ff9ad0', '#ffffff'];
const SKY = [['#4b5b8f', '#f4a98a'], ['#4f9fe0', '#bfe6f5'], ['#3a3478', '#f07b5a'], ['#080c2a', '#1c2658']];
const WIN = [[7, 6], [27, 6], [7, 19], [27, 19]];
/** Worker spots relative to the building's left edge: at the anvil, or by the door. */
const LANE = {
  work: [[12, 158], [30, 158], [21, 164], [5, 164], [36, 164]],
  door: [[20, 156], [11, 160], [29, 160], [20, 165], [5, 167]],
};
export const MAX_WORKERS = 5;

interface Palette { wall: string; shade: string; light: string; line: string; roof: string; roofL: string; shade2: string }
const PAL: Palette[] = [
  { wall: '#d98b6a', shade: '#b86f52', light: '#e8a483', line: '#c97d5e', roof: '#8c3b2f', roofL: '#a54a3b', shade2: '#6e2d24' },
  { wall: '#7fa6d6', shade: '#5f86b8', light: '#9bbde6', line: '#6f96c8', roof: '#33507d', roofL: '#426394', shade2: '#263c5f' },
  { wall: '#c8a96b', shade: '#a98c52', light: '#dcc088', line: '#b99b5e', roof: '#6d4b2a', roofL: '#835a33', shade2: '#523820' },
  { wall: '#8fb58a', shade: '#6f966b', light: '#a9cca4', line: '#7fa57a', roof: '#355f45', roofL: '#437556', shade2: '#264833' },
  { wall: '#ae92c4', shade: '#8e74a6', light: '#c4aad6', line: '#9e83b6', roof: '#5a3f78', roofL: '#6c4d8d', shade2: '#432e5a' },
  { wall: '#d6b24f', shade: '#b4932f', light: '#e6c872', line: '#c6a23f', roof: '#7a5a16', roofL: '#93711f', shade2: '#5a420e' },
];

// ---- colour helpers ----

const hex2rgb = (h: string) => {
  const n = parseInt(h.slice(1), 16);
  return [(n >> 16) & 255, (n >> 8) & 255, n & 255];
};
const rgb2hex = (r: number, g: number, b: number) => '#' + ((1 << 24) | (r << 16) | (g << 8) | b).toString(16).slice(1);
function mix(a: string, b: string, t: number): string {
  const A = hex2rgb(a), B = hex2rgb(b);
  return rgb2hex(Math.round(A[0] + (B[0] - A[0]) * t), Math.round(A[1] + (B[1] - A[1]) * t), Math.round(A[2] + (B[2] - A[2]) * t));
}
function hash(s: string): number {
  let h = 2166136261;
  for (let i = 0; i < s.length; i++) h = Math.imul(h ^ s.charCodeAt(i), 16777619);
  return h >>> 0;
}
function mulberry(seed: number): () => number {
  let a = seed;
  return () => {
    a |= 0;
    a = (a + 0x6d2b79f5) | 0;
    let t = Math.imul(a ^ (a >>> 15), 1 | a);
    t = (t + Math.imul(t ^ (t >>> 7), 61 | t)) ^ t;
    return ((t ^ (t >>> 14)) >>> 0) / 4294967296;
  };
}
const clamp = (v: number, a: number, b: number) => Math.max(a, Math.min(b, v));

/** Palette index (0..5) of a lot, stable across re-sorting. */
export const paletteFor = (key: string): number => hash(key) % PAL.length;
const roofStyle = (key: string) => (hash(key) >>> 7) & 1;

// ---- static scenery, one tile ----

const rnd = mulberry(7);
const hillF: number[] = [], hillN: number[] = [];
for (let x = 0; x < TILE; x++) {
  hillF[x] = Math.round(98 + Math.sin(x * 0.021 + 1.3) * 9 + Math.sin(x * 0.055 + 0.4) * 4);
  hillN[x] = Math.round(113 + Math.sin(x * 0.016 + 4) * 6 + Math.sin(x * 0.07) * 3);
}
const specks = Array.from({ length: 260 }, () => {
  const upper = rnd() < 0.72;
  return { x: Math.floor(rnd() * TILE), y: upper ? 136 + Math.floor(rnd() * 15) : 169 + Math.floor(rnd() * 11), c: rnd() < 0.5 ? '#4f8f43' : '#7cc462' };
});
const roadSpecks = Array.from({ length: 70 }, () => ({ x: Math.floor(rnd() * TILE), y: 153 + Math.floor(rnd() * 14), c: rnd() < 0.5 ? '#b69660' : '#dcc085' }));
const stars = Array.from({ length: 80 }, () => ({ x: Math.floor(rnd() * TILE), y: Math.floor(rnd() * 88), tw: rnd() * 6.28, big: rnd() < 0.18 }));
const trees = Array.from({ length: 19 }, () => ({ x: Math.floor(rnd() * TILE), s: Math.floor(rnd() * 3) })).sort((a, b) => a.x - b.x);
const clouds = [{ x: 30, y: 26, w: 30, v: 2.2 }, { x: 150, y: 42, w: 22, v: 1.6 }, { x: 240, y: 18, w: 34, v: 2.8 }, { x: 96, y: 58, w: 18, v: 1.2 }];

interface Particle { x: number; y: number; vx: number; vy: number; g: number; life: number; max: number; c: string; s: number; k: 'spark' | 'conf' | 'smoke' | 'dark' | 'sweat' }
interface WorkerAnim { state: AgentState; stateAt: number; hit: number; hitT: number; confetti: boolean }

/** Per-session animation state and particles, plus drawing of one frame. */
export class TownScene {
  private readonly anims = new Map<string, WorkerAnim>();
  private readonly smokeAcc = new Map<string, number>();
  private particles: Particle[] = [];
  private ctx!: Draw2D;
  private night = 0;
  private tint = new Map<string, string>();

  constructor(private readonly reducedMotion: boolean) {}

  particleCount(): number {
    return this.particles.length;
  }

  /** Advances animations: state changes, hammer hits, confetti, smoke. */
  update(lots: Lot[], dt: number, now: number): void {
    const seen = new Set<string>();
    lots.forEach((lot, i) => {
      lot.workers.slice(0, MAX_WORKERS).forEach((w, k) => {
        seen.add(w.id);
        let a = this.anims.get(w.id);
        if (!a) {
          a = { state: w.state, stateAt: now - 5000, hit: 0, hitT: 0, confetti: false };
          this.anims.set(w.id, a);
        } else if (a.state !== w.state) {
          a.state = w.state;
          a.stateAt = now;
          a.confetti = w.state === 'done';
        }
        if (this.reducedMotion) return;
        const [x, y] = workerSpot(i, k, w.state);
        if (w.state === 'working') {
          a.hitT += dt;
          if (a.hitT > 0.22) {
            a.hitT = 0;
            a.hit ^= 1;
            if (!a.hit) this.spark(x + 10, y - 4);
          }
        }
        if (w.state === 'failed' && Math.random() < dt * 6) this.sweat(x, y - 10);
        if (a.confetti) {
          a.confetti = false;
          this.confetti(x, y - 10);
        }
      });
      if (this.reducedMotion || !lot.workers.length) return;
      const rate = lot.group === 'working' ? 2.5 : lot.group === 'failed' ? 9 : 0;
      let acc = (this.smokeAcc.get(lot.key) ?? 0) + rate * dt;
      const h = buildingHeight(lot), py = GROUND - h, px = lotX(i);
      while (acc >= 1) {
        acc -= 1;
        this.smoke(px + (roofStyle(lot.key) === 0 ? 32 : 31), py - 13, lot.group === 'failed');
      }
      this.smokeAcc.set(lot.key, acc);
    });
    for (const id of this.anims.keys()) if (!seen.has(id)) this.anims.delete(id);
    for (let k = this.particles.length - 1; k >= 0; k--) {
      const p = this.particles[k];
      p.life -= dt;
      if (p.life <= 0) {
        this.particles.splice(k, 1);
        continue;
      }
      p.x += p.vx * dt;
      p.y += p.vy * dt;
      p.vy += p.g * dt;
    }
  }

  draw(ctx: Draw2D, frame: TownFrame): void {
    this.ctx = ctx;
    // Reduced motion: a frozen clock, so nothing blinks, twinkles or drifts.
    const f = frame.reducedMotion ? { ...frame, now: 0 } : frame;
    const phase = f.reducedMotion ? 0.42 : f.phase;
    const L = 0.5 + 0.5 * Math.sin(2 * Math.PI * phase);
    const night = (1 - L) * 0.62;
    if (Math.abs(night - this.night) > 0.004) {
      this.night = night;
      this.tint.clear();
    }
    ctx.clearRect(0, 0, f.viewW, SCENE_H);
    this.drawSky(f, phase, L);
    ctx.save();
    ctx.translate(-Math.round(f.camX), 0);
    const x0 = Math.floor(f.camX), x1 = Math.ceil(f.camX + f.viewW);
    this.drawHills(x0, x1);
    this.drawGround(x0, x1);
    const [first, last] = visibleRange(f.camX, f.viewW, f.lots.length);
    for (let i = first; i <= last; i++) this.drawBuilding(f.lots[i], i);
    const workers: [AgentSessionDto, number, number][] = [];
    for (let i = first; i <= last; i++) f.lots[i].workers.slice(0, MAX_WORKERS).forEach((w, k) => workers.push([w, i, k]));
    workers
      .map(([w, i, k]) => ({ w, spot: workerSpot(i, k, w.state) }))
      .sort((a, b) => a.spot[1] - b.spot[1])
      .forEach(({ w, spot }) => this.drawWorker(w, spot, f.now, f.reducedMotion));
    for (let i = first; i <= last; i++) this.drawLit(f.lots[i], i, f.now);
    this.drawLamps(first, last, L);
    this.drawParticles(x0, x1);
    ctx.restore();
  }

  // ---- primitives ----

  private C(h: string): string {
    let v = this.tint.get(h);
    if (!v) {
      v = mix(h, NIGHT, this.night);
      this.tint.set(h, v);
    }
    return v;
  }
  private R(x: number, y: number, w: number, h: number, c: string): void {
    this.ctx.fillStyle = c;
    this.ctx.fillRect(Math.round(x), Math.round(y), w, h);
  }
  private disc(cx: number, cy: number, r: number, c: string): void {
    this.ctx.fillStyle = c;
    for (let dy = -r; dy <= r; dy++) {
      const dx = Math.floor(Math.sqrt(r * r - dy * dy + 0.5));
      this.ctx.fillRect(Math.round(cx) - dx, Math.round(cy) + dy, dx * 2 + 1, 1);
    }
  }
  private alpha(a: number): void {
    this.ctx.globalAlpha = a;
  }

  // ---- scenery ----

  private drawSky(f: TownFrame, phase: number, L: number): void {
    const W = f.viewW, t = phase * 4, k = Math.floor(t) % 4, j = (k + 1) % 4, u = t - Math.floor(t);
    const top = mix(SKY[k][0], SKY[j][0], u), bottom = mix(SKY[k][1], SKY[j][1], u);
    for (let y = 0; y < 128; y += 4) this.R(0, y, W, 4, mix(top, bottom, Math.pow(y / 124, 1.25)));
    if (L < 0.62) {
      const a = Math.min(1, (0.62 - L) * 2.2);
      for (let x = 0; x < W; x += TILE)
        for (const s of stars) {
          this.alpha(a * (0.55 + 0.45 * Math.sin(f.now / 380 + s.tw)));
          this.R(x + s.x, s.y, s.big ? 2 : 1, 1, '#ffffff');
        }
      this.alpha(1);
    }
    // Sun or moon crosses the visible sky.
    const half = phase < 0.5, v = half ? phase / 0.5 : (phase - 0.5) / 0.5;
    const sx = 22 + v * (W - 44), sy = 122 - Math.sin(Math.PI * v) * 96;
    if (half) {
      this.alpha(0.28); this.disc(sx, sy, 11, '#ffd98a');
      this.alpha(0.5); this.disc(sx, sy, 9, '#ffe7a8');
      this.alpha(1); this.disc(sx, sy, 7, '#fff1c4');
    } else {
      this.alpha(0.18); this.disc(sx, sy, 10, '#cfe0ff');
      this.alpha(1); this.disc(sx, sy, 6, '#f4f1de');
      this.disc(sx + 3, sy - 2, 5, mix(top, bottom, clamp(sy / 124, 0, 1)));
    }
    const dusk = clamp(1 - Math.abs(L - 0.5) * 2, 0, 1), cc = mix('#ffffff', '#ffb39a', dusk);
    this.alpha(0.3 + 0.65 * L);
    for (let x = 0; x < W; x += TILE)
      for (const c of clouds) {
        const cx = x + ((c.x + (f.reducedMotion ? 0 : f.now / 1000) * c.v) % (TILE + 60)) - 30, col = mix(cc, NIGHT, this.night);
        this.R(cx, c.y, c.w, 3, col); this.R(cx + 4, c.y - 2, c.w - 10, 3, col); this.R(cx + 9, c.y - 4, Math.max(4, c.w - 22), 3, col);
      }
    this.alpha(1);
  }

  private drawHills(x0: number, x1: number): void {
    const cF = this.C('#6b7fb0'), cF2 = this.C('#8aa0cf'), cN = this.C('#4e8068'), cN2 = this.C('#63987c');
    for (let x = x0; x < x1; x++) {
      const m = ((x % TILE) + TILE) % TILE;
      this.R(x, hillF[m], 1, 136 - hillF[m], cF); this.R(x, hillF[m], 1, 1, cF2);
      this.R(x, hillN[m], 1, 136 - hillN[m], cN); this.R(x, hillN[m], 1, 1, cN2);
    }
    for (let base = x0 - (x0 % TILE); base < x1; base += TILE)
      for (const t of trees) {
        const px = base + t.x, rows = 4 + t.s;
        if (px < x0 - 8 || px > x1 + 8) continue;
        const py = hillN[t.x] + 4, c = this.C('#2e5c46'), c2 = this.C('#3d7358');
        this.R(px, py - 2, 1, 2, this.C('#5a3d2b'));
        for (let k = 0; k < rows; k++) this.R(px - k, py - 2 - rows * 2 + k * 2, 1 + 2 * k, 2, k % 2 ? c2 : c);
      }
  }

  private drawGround(x0: number, x1: number): void {
    const w = x1 - x0;
    this.R(x0, 134, w, 46, this.C('#5ea24f')); this.R(x0, 134, w, 2, this.C('#7bc35f'));
    this.R(x0, 152, w, 16, this.C('#c9a870')); this.R(x0, 152, w, 1, this.C('#a98a54'));
    this.R(x0, 167, w, 1, this.C('#a98a54')); this.R(x0, 168, w, 1, this.C('#4d8c42'));
    for (let base = x0 - (x0 % TILE); base < x1; base += TILE) {
      for (const s of specks) this.R(base + s.x, s.y, 1, 1, this.C(s.c));
      for (const s of roadSpecks) this.R(base + s.x, s.y, 1, 1, this.C(s.c));
    }
    for (let x = x0 - (x0 % 22) + 4; x < x1; x += 22) this.R(x, 159, 9, 1, this.C('#e5cf97'));
  }

  private drawLamps(first: number, last: number, L: number): void {
    for (let i = first; i <= last; i++) {
      const lx = lotX(i) + BW + 5;
      this.R(lx, 133, 1, 13, this.C('#3a3f55')); this.R(lx - 1, 131, 3, 3, this.C('#5a6078'));
      this.R(lx - 2, 130, 5, 1, this.C('#2f3348')); this.R(lx - 1, 146, 3, 1, this.C('#2f3348'));
      if (L < 0.58) {
        const a = clamp((0.58 - L) * 1.8, 0, 1);
        this.alpha(0.16 * a); this.disc(lx, 133, 10, '#ffd98a');
        this.alpha(0.3 * a); this.disc(lx, 133, 5, '#ffd98a');
        this.alpha(1); this.R(lx - 1, 132, 3, 2, '#fff0b8');
      }
    }
  }

  // ---- buildings ----

  private drawBuilding(lot: Lot, i: number): void {
    const small = lot.group === 'none';
    const w = small ? SMALL_BW : BW, h = buildingHeight(lot);
    const px = lotX(i) + (BW - w) / 2, py = GROUND - h;
    const base = PAL[paletteFor(lot.key)];
    const pal = small ? (Object.fromEntries(Object.entries(base).map(([k, v]) => [k, mix(v, NO_SESSION_COLOR, 0.35)])) as unknown as Palette) : base;
    const C = (c: string) => this.C(c);
    this.R(px, py, w, h, C(pal.wall));
    for (let ly = py + 5; ly < GROUND - 3; ly += 6) this.R(px + 2, ly, w - 7, 1, C(pal.line));
    this.R(px + w - 5, py, 5, h, C(pal.shade)); this.R(px, py, 2, h, C(pal.light));
    this.R(px, GROUND - 2, w, 2, C('#6b6b78'));
    const windows = small ? [[5, 6], [w - 11, 6]] : WIN;
    windows.forEach(([ox, oy]) => {
      const wx = px + ox, wy = py + oy;
      this.R(wx - 1, wy - 1, 8, 9, C('#f3ead2'));
      this.R(wx, wy, 6, 7, small && lot.serversUp ? '#ffd36b' : C('#1f2a44'));
      this.R(wx + 3, wy, 1, 7, C('#f3ead2')); this.R(wx, wy + 3, 6, 1, C('#f3ead2'));
    });
    const door = px + w / 2 - 4;
    this.R(door, GROUND - 12, 8, 12, C('#5b3a2a')); this.R(door + 1, GROUND - 11, 6, 5, C('#6e4733'));
    this.R(door + 6, GROUND - 6, 1, 1, C('#e0c070'));
    if (roofStyle(lot.key) === 0) {
      for (let r = 0; r < 11 && w + 6 - 4 * r > 0; r++) this.R(px - 3 + 2 * r, py - 1 - r, w + 6 - 4 * r, 1, C(r % 3 === 0 ? pal.roofL : pal.roof));
      this.R(px - 3, py, w + 6, 1, C(pal.shade2));
      if (!small) { this.R(px + 30, py - 11, 5, 7, C('#7a4a3a')); this.R(px + 29, py - 12, 7, 2, C('#5e3528')); this.R(px + 20, py - 16, 1, 6, C('#444a63')); }
    } else {
      this.R(px - 2, py - 3, w + 4, 3, C(pal.roof)); this.R(px - 2, py - 5, w + 4, 2, C(pal.roofL)); this.R(px - 2, py, w + 4, 1, C(pal.shade2));
      if (!small) { this.R(px + 30, py - 11, 3, 6, C('#7b7f93')); this.R(px + 29, py - 12, 5, 1, C('#9aa0b5')); this.R(px + 23, py - 13, 1, 8, C('#444a63')); }
    }
    if (lot.temporary) {
      // Sign post right of the door; the "not in RepoManager" text is HTML over the canvas.
      this.R(px + w - 3, GROUND - 9, 1, 9, C('#7a5a3a')); this.R(px + w - 6, GROUND - 12, 7, 4, C('#e8d9b0'));
    }
  }

  private drawLit(lot: Lot, i: number, now: number): void {
    if (lot.group === 'none') return;
    const st = lot.group, px = lotX(i), py = GROUND - buildingHeight(lot);
    if (st !== 'idle')
      WIN.forEach(([ox, oy], k) => {
        const col = winColor(st, k, now), wx = px + ox, wy = py + oy;
        if (!col) return;
        this.alpha(0.16); this.R(wx - 2, wy - 2, 10, 11, col); this.alpha(1);
        this.R(wx, wy, 6, 7, col); this.R(wx + 3, wy, 1, 7, '#f3ead2'); this.R(wx, wy + 3, 6, 1, '#f3ead2');
      });
    if (st === 'waiting') { this.alpha(0.45 + 0.4 * Math.sin(now / 240)); this.R(px + 16, GROUND - 12, 8, 12, '#ffc857'); this.alpha(1); }
    if (st === 'failed') this.flames(lot, px, py, now);
    // Beacon on the roof.
    const bx = px + (roofStyle(lot.key) === 0 ? 20 : 23), by = py - (roofStyle(lot.key) === 0 ? 17 : 14), col = STATE_COLOR[st];
    if (st === 'idle') { this.R(bx - 1, by - 1, 3, 3, this.C('#555d85')); return; }
    const on = st === 'waiting' ? Math.sin(now / 170) > 0 : st === 'failed' ? Math.floor(now / 90) % 2 === 0 : true;
    if (on) {
      const pulse = st === 'working' ? 0.7 + 0.3 * Math.sin(now / 300) : 1;
      this.alpha(0.2 * pulse); this.disc(bx, by, 8, col);
      this.alpha(0.38 * pulse); this.disc(bx, by, 5, col);
      this.alpha(1); this.R(bx - 1, by - 1, 3, 3, col); this.R(bx, by - 1, 1, 1, '#ffffff');
    } else this.R(bx - 1, by - 1, 3, 3, mix(col, '#1b1f3a', 0.65));
  }

  private flames(lot: Lot, px: number, py: number, now: number): void {
    const style = roofStyle(lot.key);
    const top = (c: number) => (style === 1 ? py - 5 : py - 1 - clamp(Math.min(Math.floor((c - (px - 3)) / 2), Math.floor((px + BW + 2 - c) / 2)), 0, 10));
    for (let k = 0; k < 7; k++) {
      const c = px + 4 + k * 5, hh = Math.round(7 + 3 * Math.sin(now / 90 + k * 1.9) + 2 * Math.sin(now / 53 + k)), by = top(c);
      const h2 = Math.round(hh * 0.7), h3 = Math.round(hh * 0.4);
      this.R(c, by - hh, 3, hh, '#ff5a3c'); this.R(c, by - h2, 3, h2, '#ff9a3c'); this.R(c + 1, by - h3, 1, h3, '#fff0a0');
    }
  }

  // ---- workers ----

  private drawWorker(w: AgentSessionDto, [x, y]: [number, number], now: number, still: boolean): void {
    const a = this.anims.get(w.id), st = w.state, t = still ? 0 : now / 1000, age = a ? (now - a.stateAt) / 1000 : 99;
    const hh = hash(w.id), shirt = this.C(SHIRTS[(hh >>> 5) % 6]), hat = this.C(HATS[hh % 6]), skin = this.C(SKIN), pants = this.C(PANTS);
    const sit = st === 'idle', frame = Math.floor(t * 4.5) % 2;
    let fx = x, fy = y, arms: 'down' | 'up' | 'wave' | 'head' | 'hammer' = 'down';
    if (st === 'done' && age < 3.2 && !still) { fy -= Math.round(Math.abs(Math.sin(age * 7)) * 5 * (1 - age / 3.2)); arms = 'up'; }
    else if (st === 'waiting') arms = 'wave';
    else if (st === 'failed') { if (!still) fx += Math.floor(t * 14) % 2 ? 1 : -1; arms = 'head'; }
    else if (st === 'working') arms = 'hammer';
    this.alpha(0.3); this.R(fx - 3, y, 7, 1, '#000000'); this.alpha(1);
    const hY = sit ? fy - 6 : fy - 8, bY = sit ? fy - 3 : fy - 5, bH = sit ? 2 : 3;
    if (st === 'working') { this.R(fx + 8, fy - 3, 5, 1, this.C('#7d849b')); this.R(fx + 9, fy - 2, 3, 1, this.C('#555c73')); this.R(fx + 8, fy - 1, 5, 1, this.C('#3d4358')); }
    if (sit) this.R(fx - 2, fy - 1, 5, 1, pants);
    else { this.R(fx - 2, fy - 2, 2, 2, pants); this.R(fx + 1, fy - 2, 2, 2, pants); }
    this.R(fx - 2, bY, 5, bH, shirt);
    this.R(fx - 1, hY, 3, 3, skin); this.R(fx - 2, hY - 1, 5, 1, hat); this.R(fx - 1, hY - 2, 3, 1, hat);
    const eye = st === 'failed' ? '#d9402f' : EYE;
    this.R(fx - 1, hY + 1, 1, 1, eye); this.R(fx + 1, hY + 1, 1, 1, eye);
    const armDown = (ax: number) => { this.R(ax, bY, 1, 2, shirt); this.R(ax, bY + 2, 1, 1, skin); };
    const armUp = (ax: number) => { this.R(ax, bY - 2, 1, 3, shirt); this.R(ax, bY - 4, 1, 2, skin); };
    if (arms === 'up') { armUp(fx - 3); armUp(fx + 3); }
    else if (arms === 'wave') { if (frame) { armUp(fx - 3); armDown(fx + 3); } else { armDown(fx - 3); armUp(fx + 3); } }
    else if (arms === 'head') { this.R(fx - 3, bY - 3, 1, 3, skin); this.R(fx + 3, bY - 3, 1, 3, skin); }
    else if (arms === 'hammer') {
      armDown(fx - 3);
      if (a?.hit) { this.R(fx + 3, bY - 2, 1, 2, shirt); this.R(fx + 3, bY - 3, 1, 1, skin); this.R(fx + 3, bY - 6, 1, 4, this.C('#8a6a4a')); this.R(fx + 2, bY - 7, 3, 2, this.C('#b8bcc8')); }
      else { this.R(fx + 3, bY, 1, 2, shirt); this.R(fx + 3, bY + 2, 1, 1, skin); this.R(fx + 4, bY + 1, 3, 1, this.C('#8a6a4a')); this.R(fx + 6, bY, 2, 3, this.C('#b8bcc8')); }
    } else { armDown(fx - 3); armDown(fx + 3); }
    if (sit && !still)
      for (let z = 0; z < 3; z++) {
        const zt = ((t * 0.55 + z * 0.5) % 1.5) / 1.5, zx = fx + 3 + Math.round(zt * 6) + z, zy = fy - 9 - Math.round(zt * 10);
        this.alpha(1 - zt); this.R(zx, zy, 3, 1, '#cfd6ff'); this.R(zx + 1, zy + 1, 1, 1, '#cfd6ff'); this.R(zx, zy + 2, 3, 1, '#cfd6ff');
      }
    this.alpha(1);
    if (st === 'waiting' || st === 'failed' || (st === 'done' && (age >= 3.2 || still))) {
      const bounce = st === 'waiting' && !still ? Math.round(Math.abs(Math.sin(t * 5)) * 2) : 0;
      this.bubble(x, y - 23 - bounce, st);
    }
  }

  private bubble(cx: number, y: number, kind: AgentState): void {
    const x0 = cx - 4;
    this.R(x0, y, 9, 8, '#2a2f55'); this.R(x0 + 1, y + 1, 7, 6, '#fff6dc'); this.R(cx - 1, y + 8, 3, 1, '#2a2f55'); this.R(cx, y + 8, 1, 1, '#fff6dc');
    if (kind === 'waiting') { this.R(cx, y + 2, 1, 3, '#d9402f'); this.R(cx, y + 6, 1, 1, '#d9402f'); }
    else if (kind === 'done') for (const [dx, dy] of [[2, 4], [3, 5], [4, 4], [5, 3], [6, 2]]) this.R(x0 + dx, y + dy, 1, 1, '#2f9e44');
    else if (kind === 'failed') for (const [dx, dy] of [[2, 2], [3, 3], [4, 4], [5, 5], [6, 6], [6, 2], [5, 3], [3, 5], [2, 6]]) this.R(x0 + dx, y + dy, 1, 1, '#d9402f');
  }

  // ---- particles ----

  private drawParticles(x0: number, x1: number): void {
    for (const p of this.particles) {
      if (p.x < x0 - 8 || p.x > x1 + 8) continue;
      const f = p.life / p.max, puff = p.k === 'smoke' || p.k === 'dark';
      this.alpha(clamp(f * 1.6, 0, 1) * (puff ? 0.75 : 1));
      if (puff) { const sz = 2 + Math.floor((1 - f) * 3); this.R(p.x, p.y, sz, sz, this.C(p.c)); }
      else this.R(p.x, p.y, p.s, p.s, p.c);
    }
    this.alpha(1);
  }
  private spark(x: number, y: number): void {
    for (let k = 0; k < 5; k++) this.particles.push({ x, y, vx: (Math.random() - 0.3) * 44, vy: -22 - Math.random() * 30, g: 130, life: 0.35 + Math.random() * 0.2, max: 0.5, c: Math.random() < 0.5 ? '#ffd36b' : '#ff9a3c', s: 1, k: 'spark' });
  }
  private confetti(x: number, y: number): void {
    for (let k = 0; k < 26; k++) this.particles.push({ x, y, vx: (Math.random() - 0.5) * 80, vy: -44 - Math.random() * 54, g: 95, life: 1.5 + Math.random(), max: 2.3, c: CONF[k % CONF.length], s: Math.random() < 0.4 ? 2 : 1, k: 'conf' });
  }
  private smoke(x: number, y: number, dark: boolean): void {
    this.particles.push({ x: x + Math.random() * 2, y, vx: 5 + Math.random() * 4, vy: -9 - Math.random() * 5, g: 0, life: 1.9, max: 1.9, c: dark ? '#2b2b36' : '#e9ecf5', s: 2, k: dark ? 'dark' : 'smoke' });
  }
  private sweat(x: number, y: number): void {
    this.particles.push({ x, y, vx: (Math.random() - 0.5) * 26, vy: -18, g: 95, life: 0.6, max: 0.6, c: '#8fd3ff', s: 1, k: 'sweat' });
  }
}

/** Building height: steady per lot, shorter for repos with no session. */
function buildingHeight(lot: Lot): number {
  return lot.group === 'none' ? 26 : 34 + (hash(lot.key) % 3) * 4;
}

/** Where worker `k` of lot `i` stands: at the anvil while working, by the door otherwise. */
export function workerSpot(i: number, k: number, state: AgentState): [number, number] {
  const lane = state === 'working' ? LANE.work : LANE.door;
  const [ox, y] = lane[k % lane.length];
  return [lotX(i) + ox, y];
}

function winColor(st: AgentState, k: number, now: number): string | null {
  if (st === 'working') return (Math.floor(now / (260 + k * 70)) + k) % 3 === 0 ? '#cfeaff' : '#7cc4ff';
  if (st === 'waiting') return Math.sin(now / 260) > 0 ? '#ffd36b' : '#ffb73a';
  if (st === 'done') return '#9bf0a0';
  if (st === 'failed') return Math.floor(now / 120 + k) % 2 ? '#ff6b5e' : '#c9382d';
  return null;
}
