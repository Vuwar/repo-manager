import { useEffect, useLayoutEffect, useMemo, useRef, useState } from 'react';
import { postFullscreen } from '../lib/host';
import {
  ARROW_PAN,
  BW,
  cameraToShow,
  clampCamera,
  edgeHints,
  edgeText,
  GROUND,
  lotX,
  scaleFor,
  visibleRange,
  WHEEL_FACTOR,
  worldWidth,
} from '../lib/town/camera';
import { heldOrder, RESORT_DELAY_MS } from '../lib/town/hold';
import { MAX_WORKERS, NO_SESSION_COLOR, STATE_COLOR, TownScene, workerSpot } from '../lib/town/render';
import { buildStreet, type Lot, type LotGroup } from '../lib/town/street';
import { useAppState } from '../state/store';

/** Seconds for one full day and night. */
const DAY_CYCLE_S = 150;

const swatch = (g: LotGroup) => (g === 'none' ? NO_SESSION_COLOR : STATE_COLOR[g]);

function isTypingTarget(el: EventTarget | null): boolean {
  const t = el as HTMLElement | null;
  return !!t && (t.isContentEditable || t.tagName === 'INPUT' || t.tagName === 'TEXTAREA' || t.tagName === 'SELECT');
}

function prefersReducedMotion(): boolean {
  return typeof window.matchMedia === 'function' && window.matchMedia('(prefers-reduced-motion: reduce)').matches;
}

function countsText(lots: Lot[]): string {
  const n = (s: string) => lots.reduce((k, l) => k + l.workers.filter((w) => w.state === s).length, 0);
  const parts: string[] = [];
  const waiting = n('waiting'), failed = n('failed'), working = n('working'), done = n('done');
  if (waiting) parts.push(waiting === 1 ? '1 needs you' : `${waiting} need you`);
  if (failed) parts.push(`${failed} failed`);
  if (working) parts.push(`${working} working`);
  if (done) parts.push(`${done} done`);
  return parts.join(' · ') || 'No sessions';
}

/** Agent Town: every project as a building on one street, every Claude session as a worker. Read-only. */
export function TownView({ onLeave }: { onLeave: () => void }) {
  const projects = useAppState((s) => s.projects);
  const agents = useAppState((s) => s.agents);
  const street = useMemo(() => buildStreet(projects, agents), [projects, agents]);
  const wanted = useMemo(() => street.map((l) => l.key), [street]);

  // Order: held while the pointer is over the town, re-sorted RESORT_DELAY_MS after it leaves or on Home.
  const [hovering, setHovering] = useState(false);
  const [shown, setShown] = useState<string[]>(wanted);
  const leaveTimer = useRef<ReturnType<typeof setTimeout> | null>(null);
  useLayoutEffect(() => setShown((prev) => heldOrder(prev, wanted, hovering)), [wanted, hovering]);
  useEffect(() => () => void (leaveTimer.current && clearTimeout(leaveTimer.current)), []);
  const lots = useMemo(() => {
    const byKey = new Map(street.map((l) => [l.key, l]));
    return shown.map((k) => byKey.get(k)).filter((l): l is Lot => !!l);
  }, [street, shown]);

  // Size: integer zoom so the 180 px scene fills the height.
  const stageRef = useRef<HTMLDivElement>(null);
  const [size, setSize] = useState({ w: 0, h: 0 });
  useLayoutEffect(() => {
    const measure = () => {
      const el = stageRef.current;
      if (el) setSize((s) => (s.w === el.clientWidth && s.h === el.clientHeight ? s : { w: el.clientWidth, h: el.clientHeight }));
    };
    measure();
    window.addEventListener('resize', measure);
    return () => window.removeEventListener('resize', measure);
  }, []);
  const scale = scaleFor(size.h);
  const viewW = size.w / scale;
  const world = worldWidth(lots.length);

  const [camRaw, setCam] = useState(0);
  const camX = clampCamera(camRaw, world, viewW);
  const pan = (dx: number) => setCam((c) => clampCamera(c + dx, world, viewW));
  const jump = (index: number) => setCam(cameraToShow(index, world, viewW));
  const home = () => {
    setCam(0);
    setShown(wanted);
  };

  // Keys: arrows pan, Home re-sorts and goes to the left end, F11 toggles full screen.
  const keys = useRef({ pan, home });
  keys.current = { pan, home };
  useEffect(() => {
    const onKey = (e: KeyboardEvent) => {
      if (isTypingTarget(e.target) || e.ctrlKey || e.altKey || e.metaKey) return;
      if (e.key === 'ArrowLeft') keys.current.pan(-ARROW_PAN);
      else if (e.key === 'ArrowRight') keys.current.pan(ARROW_PAN);
      else if (e.key === 'Home') keys.current.home();
      else if (e.key === 'F11') postFullscreen();
      else return;
      e.preventDefault();
    };
    window.addEventListener('keydown', onKey);
    return () => window.removeEventListener('keydown', onKey);
  }, []);

  // Drag to pan.
  const drag = useRef<{ x: number; cam: number } | null>(null);
  const onPointerDown = (e: React.PointerEvent) => {
    // A press on a button (edge arrow) must stay a click: capturing would retarget it to the stage.
    if (e.button !== 0 || (e.target as Element).closest('button')) return;
    drag.current = { x: e.clientX, cam: camX };
    e.currentTarget.setPointerCapture?.(e.pointerId);
  };
  const onPointerMove = (e: React.PointerEvent) => {
    if (drag.current) setCam(clampCamera(drag.current.cam - (e.clientX - drag.current.x) / scale, world, viewW));
  };
  const onPointerUp = () => void (drag.current = null);
  const onPointerEnter = () => {
    if (leaveTimer.current) clearTimeout(leaveTimer.current);
    leaveTimer.current = null;
    setHovering(true);
  };
  const onPointerLeave = () => {
    if (leaveTimer.current) clearTimeout(leaveTimer.current);
    leaveTimer.current = setTimeout(() => setHovering(false), RESORT_DELAY_MS);
  };

  // Canvas loop.
  const canvasRef = useRef<HTMLCanvasElement>(null);
  const frameInput = useRef({ lots, camX, viewW });
  frameInput.current = { lots, camX, viewW };
  useEffect(() => {
    const canvas = canvasRef.current;
    const ctx = canvas?.getContext('2d');
    if (!canvas || !ctx || !size.w || !size.h || typeof requestAnimationFrame !== 'function') return;
    canvas.width = size.w;
    canvas.height = size.h;
    ctx.setTransform(scale, 0, 0, scale, 0, 0);
    ctx.imageSmoothingEnabled = false;
    const reduced = prefersReducedMotion();
    const scene = new TownScene(reduced);
    let last = performance.now();
    let phase = 0.42;
    let raf = 0;
    const tick = (t: number) => {
      raf = requestAnimationFrame(tick);
      if (document.visibilityState === 'hidden') return;
      const dt = Math.min(0.05, (t - last) / 1000);
      last = t;
      if (!reduced) phase = (phase + dt / DAY_CYCLE_S) % 1;
      const f = frameInput.current;
      scene.update(f.lots, dt, t);
      scene.draw(ctx, { lots: f.lots, camX: f.camX, viewW: f.viewW, now: t, phase, reducedMotion: reduced });
    };
    raf = requestAnimationFrame(tick);
    return () => cancelAnimationFrame(raf);
  }, [size, scale]);

  const [first, last] = visibleRange(camX, viewW, lots.length);
  const visible = lots.slice(first, last + 1).map((lot, k) => ({ lot, i: first + k }));
  const hints = edgeHints(lots, camX, viewW);
  const toPx = (x: number) => (x - camX) * scale;

  return (
    // Hover covers the stage and the minimap, so dots do not re-sort under the pointer.
    <div className="town" onPointerEnter={onPointerEnter} onPointerLeave={onPointerLeave}>
      <header className="town-bar">
        <span className="town-title">Agent Town</span>
        <span className="town-counts">{countsText(lots)}</span>
        <button type="button" className="town-leave" onClick={onLeave}>
          Leave town
        </button>
      </header>
      <div
        ref={stageRef}
        className="town-stage"
        onPointerDown={onPointerDown}
        onPointerMove={onPointerMove}
        onPointerUp={onPointerUp}
        onPointerCancel={onPointerUp}
        onLostPointerCapture={onPointerUp}
        onWheel={(e) => pan((e.deltaY + e.deltaX) * WHEEL_FACTOR)}
      >
        <canvas ref={canvasRef} className="town-canvas" aria-hidden="true" />
        {lots.length === 0 && (
          <div className="town-empty">No projects yet. Add a repo in RepoManager, or start Claude Code in any folder.</div>
        )}
        {visible.map(({ lot, i }) => (
          <div key={lot.key} className="town-label" style={{ left: toPx(lotX(i) + BW / 2), top: (GROUND + 2) * scale }}>
            <span className="town-lot-name" style={{ ['--c' as string]: swatch(lot.group) }}>
              {lot.name}
            </span>
            {lot.temporary && <span className="town-lot-note">not in RepoManager</span>}
            {lot.workers.length > MAX_WORKERS && <span className="town-lot-note">+{lot.workers.length - MAX_WORKERS}</span>}
          </div>
        ))}
        {visible.flatMap(({ lot, i }) =>
          lot.workers.slice(0, MAX_WORKERS).flatMap((w, k) => {
            if (!w.worktree) return [];
            const [x, y] = workerSpot(i, k, w.state);
            return [
              <span key={w.id} className="town-sign" style={{ left: toPx(x), top: (y - 34) * scale }}>
                {w.worktree}
              </span>,
            ];
          }),
        )}
        {hints.map((h) => (
          <button key={h.side} type="button" className={`town-edge town-edge-${h.side}`} style={{ ['--c' as string]: swatch(h.group) }} onClick={() => jump(h.target)}>
            {edgeText(h)}
          </button>
        ))}
      </div>
      <nav className="town-minimap" aria-label="Minimap">
        {lots.length > 0 && (
          <span
            className="town-minimap-frame"
            style={{ left: `${(camX / world) * 100}%`, width: `${Math.min(100, (viewW / world) * 100)}%` }}
          />
        )}
        {lots.map((lot, i) => (
          <button
            key={lot.key}
            type="button"
            className="town-dot"
            aria-label={'Go to ' + lot.name}
            title={lot.name}
            style={{ left: `${((lotX(i) + BW / 2) / world) * 100}%`, ['--c' as string]: swatch(lot.group) }}
            onClick={() => jump(i)}
          />
        ))}
      </nav>
    </div>
  );
}
