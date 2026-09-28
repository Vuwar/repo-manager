import { memo, useCallback, useEffect, useLayoutEffect, useMemo, useRef, useState, type PointerEvent as ReactPointerEvent } from 'react';
import type { LogLineDto } from '../api/types';
import { formatClock, shortName } from '../lib/format';
import { mergeLogs } from '../lib/logs';
import { dispatch, useAppState } from '../state/store';
import { AnsiText, segmentsOf } from './AnsiText';
import { Icon } from './Icon';
import { Button } from './ui';

const HEIGHT_KEY = 'repomanager.logHeight';
const PREFS_KEY = 'repomanager.logPrefs';

function readNum(key: string, fallback: number): number {
  try {
    const v = Number(localStorage.getItem(key));
    return Number.isFinite(v) && v > 0 ? v : fallback;
  } catch {
    return fallback;
  }
}

function readPrefs(): { wrap: boolean; time: boolean } {
  try {
    const v = JSON.parse(localStorage.getItem(PREFS_KEY) ?? '');
    return { wrap: !!v.wrap, time: v.time !== false };
  } catch {
    return { wrap: true, time: true };
  }
}

// Stable React keys and plain text per line object.
const ids = new WeakMap<LogLineDto, number>();
const plain = new WeakMap<LogLineDto, string>();
let seq = 0;
function lineId(l: LogLineDto): number {
  let id = ids.get(l);
  if (id === undefined) ids.set(l, (id = ++seq));
  return id;
}
function plainText(l: LogLineDto): string {
  let t = plain.get(l);
  if (t === undefined) {
    t = segmentsOf(l.text)
      .map((s) => s.text)
      .join('')
      .toLowerCase();
    plain.set(l, t);
  }
  return t;
}

const LogRow = memo(function LogRow({
  line,
  needle,
  showTime,
  prefix,
  hue,
}: {
  line: LogLineDto;
  needle: string;
  showTime: boolean;
  prefix: string | null;
  hue: number;
}) {
  return (
    <div className={`log-line log-${line.stream}`}>
      {showTime && <span className="log-time">{formatClock(line.timestamp)}</span>}
      {prefix !== null && <span className={`log-src hue-${hue}`}>{prefix}</span>}
      <span className="log-text">
        <AnsiText text={line.text} needle={needle} />
      </span>
    </div>
  );
});

export function LogPane({ labelFor }: { labelFor?: (source: string) => string }) {
  const sources = useAppState((s) => s.logSources);
  const logs = useAppState((s) => s.logs);
  const [height, setHeight] = useState(() => readNum(HEIGHT_KEY, Math.round(Math.min(300, window.innerHeight * 0.36))));
  const [viewportH, setViewportH] = useState(() => window.innerHeight);
  useEffect(() => {
    const onResize = () => setViewportH(window.innerHeight);
    window.addEventListener('resize', onResize);
    return () => window.removeEventListener('resize', onResize);
  }, []);
  // Never let the log pane squeeze the project view below ~200px.
  const shownHeight = Math.max(90, Math.min(height, viewportH - 200));
  const [collapsed, setCollapsed] = useState(false);
  const [query, setQuery] = useState('');
  const [follow, setFollow] = useState(true);
  const [prefs, setPrefs] = useState(readPrefs);
  const bodyRef = useRef<HTMLDivElement>(null);
  const searchRef = useRef<HTMLInputElement>(null);

  useEffect(() => {
    try {
      localStorage.setItem(PREFS_KEY, JSON.stringify(prefs));
    } catch {
      /* ignore */
    }
  }, [prefs]);

  const buffers = useMemo(() => sources.map((s) => logs[s] ?? []), [sources, logs]);
  const merged = useMemo(() => mergeLogs(buffers), [buffers]);
  const needle = query.trim().toLowerCase();
  const visible = useMemo(() => (needle ? merged.filter((l) => plainText(l).includes(needle)) : merged), [merged, needle]);
  const multi = sources.length > 1;
  const hueOf = useMemo(() => new Map(sources.map((s, i) => [s, i % 8])), [sources]);
  const label = useCallback((s: string) => (labelFor ? labelFor(s) : shortName(s)), [labelFor]);

  // Autoscroll to the newest line while following.
  useLayoutEffect(() => {
    const el = bodyRef.current;
    if (el && follow) el.scrollTop = el.scrollHeight;
  }, [visible, follow, collapsed, shownHeight]);

  // Only user-initiated scrolling (wheel, keys, scrollbar drag) pauses autoscroll; content growth never does.
  const lastUser = useRef(0);
  const markUser = () => {
    lastUser.current = performance.now();
  };
  const onScroll = () => {
    const el = bodyRef.current;
    if (!el) return;
    if (!pointerDown.current && performance.now() - lastUser.current > 500) return;
    const atBottom = el.scrollHeight - el.scrollTop - el.clientHeight < 24;
    if (!atBottom && follow) setFollow(false);
    else if (atBottom && !follow) setFollow(true);
  };
  const pointerDown = useRef(false);
  useEffect(() => {
    const up = () => {
      pointerDown.current = false;
    };
    window.addEventListener('pointerup', up);
    return () => window.removeEventListener('pointerup', up);
  }, []);

  // Drag handle to resize.
  const onDragStart = (e: ReactPointerEvent) => {
    e.preventDefault();
    const startY = e.clientY;
    const startH = collapsed ? 34 : shownHeight;
    setCollapsed(false);
    const move = (ev: PointerEvent) => {
      const max = Math.max(160, window.innerHeight - 160);
      setHeight(Math.round(Math.min(max, Math.max(90, startH + (startY - ev.clientY)))));
    };
    const up = () => {
      window.removeEventListener('pointermove', move);
      window.removeEventListener('pointerup', up);
      document.body.classList.remove('resizing');
      setHeight((h) => {
        try {
          localStorage.setItem(HEIGHT_KEY, String(h));
        } catch {
          /* ignore */
        }
        return h;
      });
    };
    document.body.classList.add('resizing');
    window.addEventListener('pointermove', move);
    window.addEventListener('pointerup', up);
  };

  const remove = (s: string) => dispatch({ type: 'setLogSources', sources: sources.filter((x) => x !== s) });

  return (
    <section className={`logpane${collapsed ? ' collapsed' : ''}`} style={{ height: collapsed ? undefined : shownHeight }} aria-label="Logs">
      <div className="log-handle" onPointerDown={onDragStart} onDoubleClick={() => setCollapsed(!collapsed)} title="Drag to resize, double-click to collapse" />
      <div className="log-head">
        <Button
          size="sm"
          variant="ghost"
          icon={collapsed ? 'arrowUp' : 'chevronDown'}
          aria-label={collapsed ? 'Expand logs' : 'Collapse logs'}
          onClick={() => setCollapsed(!collapsed)}
        />
        <span className="log-title">Logs</span>
        <div className="log-sources">
          {sources.length === 0 && <span className="muted">no source selected</span>}
          {sources.map((s) => (
            <span key={s} className={`src-chip hue-${hueOf.get(s)}`} title={s}>
              <span className="src-swatch" />
              {label(s)}
              <button type="button" aria-label={`Remove ${s} from log view`} onClick={() => remove(s)}>
                <Icon name="x" size={10} />
              </button>
            </span>
          ))}
        </div>
        <div className="log-tools">
          <label className="log-search">
            <Icon name="search" size={12} />
            <input
              ref={searchRef}
              value={query}
              placeholder="Filter log…"
              aria-label="Filter log"
              onChange={(e) => setQuery(e.target.value)}
              onKeyDown={(e) => e.key === 'Escape' && setQuery('')}
            />
            {needle && <span className="log-count">{visible.length}</span>}
          </label>
          <Button
            size="sm"
            variant="ghost"
            className={prefs.time ? 'on' : ''}
            icon="clock"
            aria-pressed={prefs.time}
            title="Timestamps"
            onClick={() => setPrefs({ ...prefs, time: !prefs.time })}
          />
          <Button
            size="sm"
            variant="ghost"
            className={prefs.wrap ? 'on' : ''}
            icon="wrap"
            aria-pressed={prefs.wrap}
            title="Wrap lines"
            onClick={() => setPrefs({ ...prefs, wrap: !prefs.wrap })}
          />
          <Button
            size="sm"
            variant="ghost"
            className={follow ? '' : 'on'}
            icon={follow ? 'pause' : 'play'}
            aria-label={follow ? 'Pause autoscroll' : 'Resume autoscroll'}
            title={follow ? 'Pause autoscroll' : 'Resume autoscroll'}
            onClick={() => setFollow(!follow)}
          />
          <Button
            size="sm"
            variant="ghost"
            icon="eraser"
            title="Clear view"
            aria-label="Clear view"
            disabled={!sources.length}
            onClick={() => dispatch({ type: 'clearLogs', sources })}
          />
        </div>
      </div>
      {!collapsed && (
        <div
          className={`log-body${prefs.wrap ? ' wrap' : ''}`}
          ref={bodyRef}
          onScroll={onScroll}
          onWheel={markUser}
          onKeyDown={markUser}
          onPointerDown={() => {
            pointerDown.current = true;
            markUser();
          }}
          tabIndex={0}
          role="log"
        >
          {sources.length === 0 ? (
            <div className="log-empty">Select a service or task to see its output. Ctrl+click or tick several to merge them.</div>
          ) : visible.length === 0 ? (
            <div className="log-empty">{needle ? 'No lines match the filter.' : 'No output yet.'}</div>
          ) : (
            visible.map((l) => (
              <LogRow
                key={lineId(l)}
                line={l}
                needle={needle}
                showTime={prefs.time}
                prefix={multi ? label(l.source) : null}
                hue={hueOf.get(l.source) ?? 0}
              />
            ))
          )}
        </div>
      )}
      {!collapsed && !follow && sources.length > 0 && (
        <button type="button" className="log-jump" onClick={() => setFollow(true)}>
          <Icon name="arrowDown" size={12} /> Autoscroll paused — jump to latest
        </button>
      )}
    </section>
  );
}
