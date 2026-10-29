import { useEffect, useLayoutEffect, useMemo, useRef, useState } from 'react';
import { api } from '../api/client';
import { postFullscreen } from '../lib/host';
import { inOrder, startFocusReporting } from '../lib/town/focus';
import { questsFor } from '../lib/town/quests';
import { useNow } from '../lib/useNow';
import { DialogueBox } from './town/DialogueBox';
import { QuestLog } from './town/QuestLog';
import { TownSettings } from './town/TownSettings';
import type { TownSettingsDto } from '../api/types';
import {
  ARROW_PAN,
  BW,
  cameraToShow,
  clampCamera,
  edgeHints,
  edgeText,
  LABEL_Y,
  lotX,
  scaleFor,
  SLOT_W,
  visibleRange,
  WHEEL_FACTOR,
  worldWidth,
} from '../lib/town/camera';
import { heldOrder, RESORT_DELAY_MS } from '../lib/town/hold';
import { MAX_WORKERS, NO_SESSION_COLOR, STATE_COLOR, TownScene, workerAt, workerSpot } from '../lib/town/render';
import { buildStreet, type Lot, type LotGroup } from '../lib/town/street';
import { useAppState } from '../state/store';

/** Seconds for one full day and night. */
const DAY_CYCLE_S = 150;
/** Pointer travel (CSS px) below which a press and release counts as a click, not a drag. */
const CLICK_SLOP_PX = 4;

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
/** A request (from a notification click) to open one session's dialogue; a new nonce repeats it. */
export interface OpenRequest {
  id: string;
  nonce: number;
}

export function TownView({ onLeave, openRequest }: { onLeave: () => void; openRequest?: OpenRequest | null }) {
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

  // Size: a whole number of device pixels per scene pixel (crisp at 125% and 150% display scaling), as large as
  // fits the height. `zoom` is the same in CSS pixels, for everything laid out over the canvas.
  const stageRef = useRef<HTMLDivElement>(null);
  const [size, setSize] = useState({ w: 0, h: 0, dpr: 1 });
  useLayoutEffect(() => {
    const measure = () => {
      const el = stageRef.current;
      const dpr = window.devicePixelRatio || 1;
      if (el)
        setSize((s) => (s.w === el.clientWidth && s.h === el.clientHeight && s.dpr === dpr ? s : { w: el.clientWidth, h: el.clientHeight, dpr }));
    };
    measure();
    // The stage also resizes without the window (the top bar wrapping, full screen); the window event covers zoom changes.
    const observer = typeof ResizeObserver === 'function' && stageRef.current ? new ResizeObserver(measure) : null;
    if (observer && stageRef.current) observer.observe(stageRef.current);
    window.addEventListener('resize', measure);
    return () => {
      observer?.disconnect();
      window.removeEventListener('resize', measure);
    };
  }, []);
  const deviceScale = scaleFor(size.h * size.dpr);
  const zoom = deviceScale / size.dpr;
  const viewW = size.w / zoom;
  const world = worldWidth(lots.length);

  const [camRaw, setCam] = useState(0);
  const camX = clampCamera(camRaw, world, viewW);
  const pan = (dx: number) => setCam((c) => clampCamera(c + dx, world, viewW));
  const jump = (index: number) => setCam(cameraToShow(index, world, viewW));
  const home = () => {
    setCam(0);
    setShown(wanted);
  };

  // Dialogue and quest log. The town tells the daemon while it has focus, so permission prompts wait for it.
  const [open, setOpen] = useState<string | null>(null);
  const [seen, setSeen] = useState<Record<string, string>>({});
  const [questsOpen, setQuestsOpen] = useState(false);
  const now = useNow();
  // In order: a late "true" overtaking a "false" would hold prompts for up to the 15 s lease.
  useEffect(() => startFocusReporting(inOrder((f) => api.townFocus(f))), []);
  const [drafts, setDrafts] = useState<Record<string, string>>({});
  // Settings: the town owns one copy, shared by the gear's panel and the speaker button (the sound mute).
  const [settingsOpen, setSettingsOpen] = useState(false);
  const [settings, setSettings] = useState<TownSettingsDto | null>(null);
  useEffect(() => {
    let live = true;
    api.townSettings().then(
      (s) => live && setSettings(s),
      () => {},
    );
    return () => {
      live = false;
    };
  }, []);
  /** Shows the change at once; a failed save puts the previous settings back. */
  const saveSettings = async (next: TownSettingsDto) => {
    const before = settings;
    setSettings(next);
    const r = await api.saveTownSettings(next);
    if (!r.ok && before) setSettings(before);
    return r;
  };
  const toggleSound = () => void (settings && saveSettings({ ...settings, sound: !settings.sound }));
  // A notification click the shell could not route opens that worker's dialogue.
  useEffect(() => {
    if (openRequest) setOpen(openRequest.id);
  }, [openRequest]);
  const openSession = open ? agents[open] : undefined;
  const openLot = openSession ? lots.find((l) => l.workers.some((w) => w.id === openSession.id)) : undefined;
  useEffect(() => {
    if (open && !openSession) setOpen(null);
  }, [open, openSession]);
  // Looking at a finished session takes it off the quest log.
  useEffect(() => {
    if (openSession?.state === 'done' && seen[openSession.id] !== openSession.since)
      setSeen((s) => ({ ...s, [openSession.id]: openSession.since }));
  }, [openSession, seen]);
  const talkTo = (id: string) => {
    setQuestsOpen(false);
    setOpen(id);
  };
  /** A reply linked another desktop session: talk to it here when it is in town, else open it in Claude. */
  const openLinkedSession = (hostSessionId: string) => {
    const target = Object.values(agents).find((a) => a.hostSessionId === hostSessionId);
    if (target) talkTo(target.id);
    else void api.townOpenHost(hostSessionId);
  };
  /** Next worker that needs the user, in street order, after the open one. */
  const nextWaiting = (): boolean => {
    const waiting = lots.flatMap((l) => l.workers).filter((w) => w.state === 'waiting');
    if (!waiting.length) return false;
    const at = waiting.findIndex((w) => w.id === open);
    talkTo(waiting[(at + 1) % waiting.length].id);
    return true;
  };

  // Keys: arrows pan, Home re-sorts and goes to the left end, F11 toggles full screen, Tab and Q talk.
  // The dialogue box handles its own keys (Esc, menu arrows) while it is open.
  const keys = useRef({ pan, home, nextWaiting, boxOpen: false, questsOpen });
  keys.current = { pan, home, nextWaiting, boxOpen: !!openSession, questsOpen };
  useEffect(() => {
    const onKey = (e: KeyboardEvent) => {
      if (isTypingTarget(e.target) || e.ctrlKey || e.altKey || e.metaKey) return;
      const k = keys.current;
      if (e.key === 'ArrowLeft' && !k.boxOpen) k.pan(-ARROW_PAN);
      else if (e.key === 'ArrowRight' && !k.boxOpen) k.pan(ARROW_PAN);
      else if (e.key === 'Home') k.home();
      else if (e.key === 'F11') postFullscreen();
      // Tab stays normal keyboard navigation when nobody needs the user.
      else if (e.key === 'Tab') {
        if (!k.nextWaiting()) return;
      }
      else if (e.key === 'q' || e.key === 'Q') setQuestsOpen((q) => !q);
      else if (e.key === 'Escape' && k.questsOpen) setQuestsOpen(false);
      // Nothing open: Esc leaves borderless full screen (the shell ignores it otherwise).
      else if (e.key === 'Escape' && !k.boxOpen) postFullscreen(false);
      else return;
      e.preventDefault();
    };
    window.addEventListener('keydown', onKey);
    return () => window.removeEventListener('keydown', onKey);
  }, []);

  // Drag to pan; a press and release within CLICK_SLOP_PX is a click on a worker.
  const drag = useRef<{ x: number; y: number; cam: number; moved: boolean } | null>(null);
  const onPointerDown = (e: React.PointerEvent) => {
    // A press on a button (edge arrow) must stay a click: capturing would retarget it to the stage.
    if (e.button !== 0 || (e.target as Element).closest('button')) return;
    drag.current = { x: e.clientX, y: e.clientY, cam: camX, moved: false };
    e.currentTarget.setPointerCapture?.(e.pointerId);
  };
  const onPointerMove = (e: React.PointerEvent) => {
    const d = drag.current;
    if (!d) return;
    if (Math.hypot(e.clientX - d.x, e.clientY - d.y) >= CLICK_SLOP_PX) d.moved = true;
    setCam(clampCamera(d.cam - (e.clientX - d.x) / zoom, world, viewW));
  };
  const onPointerUp = (e: React.PointerEvent) => {
    const d = drag.current;
    drag.current = null;
    if (!d || d.moved || e.type !== 'pointerup') return;
    const rect = e.currentTarget.getBoundingClientRect();
    const hit = workerAt(lots, (e.clientX - rect.left) / zoom + camX, (e.clientY - rect.top) / zoom);
    if (hit) talkTo(hit.session.id);
  };
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
    canvas.width = Math.round(size.w * size.dpr);
    canvas.height = Math.round(size.h * size.dpr);
    ctx.setTransform(deviceScale, 0, 0, deviceScale, 0, 0);
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
  }, [size, deviceScale]);

  const [first, last] = visibleRange(camX, viewW, lots.length);
  const visible = lots.slice(first, last + 1).map((lot, k) => ({ lot, i: first + k }));
  const hints = edgeHints(lots, camX, viewW);
  const toPx = (x: number) => (x - camX) * zoom;

  return (
    // Hover covers the stage and the minimap, so dots do not re-sort under the pointer.
    <div className="town" onPointerEnter={onPointerEnter} onPointerLeave={onPointerLeave}>
      <header className="town-bar">
        <span className="town-title">Agent Town</span>
        <span className="town-counts">{countsText(lots)}</span>
        <button
          type="button"
          className="town-leave"
          aria-label="Sounds"
          aria-pressed={settings?.sound ?? true}
          disabled={!settings}
          title={settings?.sound === false ? 'Sounds are off' : 'Sounds are on'}
          onClick={toggleSound}
        >
          {settings?.sound === false ? '🔇' : '🔊'}
        </button>
        <button type="button" className="town-leave" aria-label="Town settings" title="Town settings" onClick={() => setSettingsOpen((o) => !o)}>
          ⚙
        </button>
        <button type="button" className="town-leave" onClick={onLeave}>
          Leave town
        </button>
      </header>
      <div
        ref={stageRef}
        className={`town-stage${openSession ? ' town-dim' : ''}`}
        onPointerDown={onPointerDown}
        onPointerMove={onPointerMove}
        onPointerUp={onPointerUp}
        onPointerCancel={onPointerUp}
        onLostPointerCapture={onPointerUp}
        onWheel={(e) => pan((e.deltaY + e.deltaX) * WHEEL_FACTOR)}
      >
        <canvas ref={canvasRef} className="town-canvas" aria-hidden="true" />
        {lots.length === 0 && (
          <div className="town-empty">
            {projects === null ? 'Loading the town…' : 'No projects yet. Add a repo in RepoManager, or start Claude Code in any folder.'}
          </div>
        )}
        {visible.map(({ lot, i }) => (
          <div key={lot.key} className="town-label" style={{ left: toPx(lotX(i) + BW / 2), top: LABEL_Y * zoom }}>
            <span className="town-lot-name" style={{ ['--c' as string]: swatch(lot.group), maxWidth: (SLOT_W - 4) * zoom }}>
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
              <span key={w.id} className="town-sign" style={{ left: toPx(x), top: (y - 34) * zoom }}>
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
      {openSession && openLot && !questsOpen && (
        <DialogueBox
          key={openSession.id}
          session={openSession}
          lotName={openLot.name}
          index={openLot.workers.findIndex((w) => w.id === openSession.id)}
          onClose={() => setOpen(null)}
          onSessionLink={openLinkedSession}
          draft={drafts[openSession.id] ?? ''}
          onDraft={(text) => setDrafts((d) => ({ ...d, [openSession.id]: text }))}
        />
      )}
      {settingsOpen && <TownSettings settings={settings} onSave={saveSettings} onClose={() => setSettingsOpen(false)} />}
      {questsOpen && <QuestLog quests={questsFor(lots, seen)} now={now} onOpen={talkTo} onClose={() => setQuestsOpen(false)} />}
    </div>
  );
}
