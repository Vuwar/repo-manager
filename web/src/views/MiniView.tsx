import { useEffect, useMemo, useRef, useState, type CSSProperties, type KeyboardEvent } from 'react';
import type { ServiceDto } from '../api/types';
import { Icon } from '../components/Icon';
import { Toasts } from '../components/Toasts';
import { Button, Spinner } from '../components/ui';
import { isActive, stateLabel, stateTone } from '../lib/format';
import { AMBIENT_LABELS, ambientStyle, nextAmbient, type AmbientState } from '../lib/ambient';
import { inDesktopShell, postTopmost, syncAmbient } from '../lib/host';
import { enterAction, miniGroups, readAmbient, readPinned, readTabs, runningCount, saveAmbient, savePinned, type FinderHit } from '../lib/simple';
import { instanceAction, loadProjects, serviceAction } from '../state/actions';
import { useAppState } from '../state/store';

interface Props {
  onExpand: () => void;
}

const AMBIENT_HINTS = {
  glass: 'Frosted glass: whatever is behind the window shows through, blurred.',
  match: 'Takes its colors from the screen around the window.',
  mixed: 'Frosted glass tinted with the colors around the window.',
} as const;

/** Follows the system light/dark switch (the app's theme). */
function useDarkTheme(): boolean {
  const query = '(prefers-color-scheme: light)';
  const [dark, setDark] = useState(() => !window.matchMedia?.(query).matches);
  useEffect(() => {
    const media = window.matchMedia?.(query);
    if (!media) return;
    const on = () => setDark(!media.matches);
    media.addEventListener('change', on);
    return () => media.removeEventListener('change', on);
  }, []);
  return dark;
}

function MiniRow({ s }: { s: ServiceDto }) {
  const busy = useAppState((st) => st.pending[s.id]);
  const active = isActive(s.state);
  const stopping = s.state === 'stopping';
  const up = s.state === 'running' || s.state === 'unhealthy';
  const tone = stateTone(s.state);
  const href = s.url ?? (s.port ? `http://localhost:${s.port}` : undefined);
  const status = s.lastError && (s.state === 'failed' || s.state === 'crashed') ? `${stateLabel(s.state)}: ${s.lastError}` : stateLabel(s.state);
  const on = active || stopping;
  return (
    <li className={`mv-row mv-${tone}`} data-testid={`mini-${s.id}`}>
      <span className="mv-lamp" aria-hidden="true" />
      <span className="mv-name" title={`${s.name}: ${status}`}>
        {s.name}
      </span>
      {up && href && (
        <a className="mv-port" href={href} target="_blank" rel="noreferrer" title={`Open ${href}`}>
          {s.port ? `:${s.port}` : 'open'}
        </a>
      )}
      {active && (
        <button
          type="button"
          className="mv-btn mv-restart"
          aria-label={`Restart ${s.name}`}
          title="Restart"
          disabled={!!busy}
          onClick={() => void serviceAction('restart', s.id)}
        >
          {busy === 'Restarting' ? <Spinner size={10} /> : <Icon name="restart" size={12} />}
        </button>
      )}
      <button
        type="button"
        className={`mv-btn mv-toggle${on ? ' is-on' : ''}`}
        aria-label={`${on ? 'Stop' : 'Start'} ${s.name}`}
        title={on ? 'Stop' : 'Start'}
        disabled={!!busy || stopping}
        onClick={() => void serviceAction(on ? 'stop' : 'start', s.id)}
      >
        {busy === 'Starting' || busy === 'Stopping' || stopping ? <Spinner size={10} /> : <Icon name={on ? 'stop' : 'play'} size={11} />}
      </button>
    </li>
  );
}

function MiniGroup({ hit }: { hit: FinderHit }) {
  const busy = useAppState((st) => st.pending['instance:' + hit.instance.key]);
  const { services } = hit;
  const anyActive = services.some((s) => isActive(s.state));
  const anyStopped = services.some((s) => !isActive(s.state) && s.state !== 'stopping');
  const label = hit.project.name + (hit.instance.worktree ? ' @ ' + hit.instance.worktree : '');
  return (
    <section className="mv-group" aria-label={label}>
      <header className="mv-group-head">
        <h2 title={hit.instance.root}>
          {hit.project.name}
          {hit.instance.worktree && <span className="mv-wt"> @{hit.instance.worktree}</span>}
        </h2>
        {services.length > 1 && (
          <button
            type="button"
            className="mv-all"
            disabled={!!busy || (anyActive ? false : !anyStopped)}
            onClick={() => void instanceAction(anyActive ? 'stop' : 'start', hit.instance.key)}
          >
            {busy ? <Spinner size={9} /> : null}
            {anyActive ? 'Stop all' : 'Start all'}
          </button>
        )}
      </header>
      {services.length ? (
        <ul className="mv-list">
          {services.map((s) => (
            <MiniRow key={s.id} s={s} />
          ))}
        </ul>
      ) : (
        <p className="mv-none">No services</p>
      )}
    </section>
  );
}

/** Compact always-at-hand list: one click to start or stop each service. */
export function MiniView({ onExpand }: Props) {
  const projects = useAppState((s) => s.projects);
  const projectsError = useAppState((s) => s.projectsError);
  const connection = useAppState((s) => s.connection);
  const [query, setQuery] = useState('');
  const [pinned, setPinned] = useState(readPinned);
  const [tabs] = useState(readTabs);
  const [ambient, setAmbient] = useState(readAmbient);
  const [ambientState, setAmbientState] = useState<AmbientState>({ glass: false, rgb: null });
  const darkTheme = useDarkTheme();
  const searchRef = useRef<HTMLInputElement>(null);
  const desktop = inDesktopShell();

  useEffect(() => {
    saveAmbient(ambient);
    return syncAmbient(ambient, setAmbientState);
  }, [ambient]);

  useEffect(() => {
    savePinned(pinned);
    postTopmost(pinned);
    return () => postTopmost(false);
  }, [pinned]);

  useEffect(() => {
    const onKey = (e: globalThis.KeyboardEvent) => {
      if (!((e.ctrlKey || e.metaKey) && e.key.toLowerCase() === 'k')) return;
      e.preventDefault();
      searchRef.current?.focus();
      searchRef.current?.select();
    };
    window.addEventListener('keydown', onKey);
    return () => window.removeEventListener('keydown', onKey);
  }, []);

  const groups = useMemo(() => miniGroups(projects ?? [], tabs, query), [projects, tabs, query]);
  const running = useMemo(() => runningCount((projects ?? []).flatMap((p) => p.instances.flatMap((i) => i.services))), [projects]);
  const enter = enterAction(groups, query);

  const onSearchKey = (e: KeyboardEvent<HTMLInputElement>) => {
    if (e.key === 'Escape') setQuery('');
    else if (e.key === 'Enter' && enter?.kind === 'start') {
      e.preventDefault();
      void serviceAction('start', enter.service.id);
      setQuery('');
    }
  };

  let body;
  if (projects === null && !projectsError)
    body = (
      <div className="mv-empty">
        <Spinner size={14} />
      </div>
    );
  else if (projectsError)
    body = (
      <div className="mv-empty mv-error">
        <span>Could not load repos.</span>
        <Button size="sm" variant="ghost" onClick={() => void loadProjects()}>
          Retry
        </Button>
      </div>
    );
  else if (!projects?.length)
    body = (
      <div className="mv-empty">
        <span>No repos yet.</span>
        <Button size="sm" variant="ghost" onClick={onExpand}>
          Add one in the big view
        </Button>
      </div>
    );
  else if (!groups.length) body = <div className="mv-empty">Nothing matches “{query.trim()}”.</div>;
  else body = groups.map((g) => <MiniGroup key={g.instance.key} hit={g} />);

  const look = ambientStyle(ambient, ambientState, darkTheme);

  return (
    <div className={`mini${look.className ? ' ' + look.className : ''}`} style={look.style as CSSProperties} data-ambient={ambient}>
      <header className="mv-head">
        <span className={`mv-summary${running ? ' is-live' : ''}`} role="status">
          {connection !== 'open' ? (
            <>
              <Spinner size={10} /> {connection === 'reconnecting' ? 'Reconnecting' : 'Connecting'}
            </>
          ) : running ? (
            `${running} running`
          ) : (
            'All stopped'
          )}
        </span>
        <div className="mv-head-actions">
          {desktop && (
            <button
              type="button"
              className={`mv-btn mv-pin${pinned ? ' is-on' : ''}`}
              aria-pressed={pinned}
              aria-label="Keep on top"
              title={pinned ? 'Kept on top of other windows. Click to release.' : 'Keep on top of other windows'}
              onClick={() => setPinned((p) => !p)}
            >
              <svg viewBox="0 0 24 24" width="13" height="13" fill="none" stroke="currentColor" strokeWidth="1.8" strokeLinecap="round" strokeLinejoin="round" aria-hidden="true">
                <path d="M9 4h6l-1 5 3 3H7l3-3zM12 12v8" />
              </svg>
            </button>
          )}
          {desktop && (
            <button
              type="button"
              className="mv-btn mv-ambient"
              aria-label={`Background: ${AMBIENT_LABELS[ambient]}`}
              title={`Background: ${AMBIENT_LABELS[ambient]}. ${AMBIENT_HINTS[ambient]} Click for ${AMBIENT_LABELS[nextAmbient(ambient)]}.`}
              onClick={() => setAmbient(nextAmbient)}
            >
              <svg viewBox="0 0 24 24" width="13" height="13" fill="none" stroke="currentColor" strokeWidth="1.8" strokeLinecap="round" strokeLinejoin="round" aria-hidden="true">
                {ambient === 'glass' ? (
                  <path d="M5 4h14v16H5zM9 8l-2 2M13 8l-6 6" />
                ) : ambient === 'match' ? (
                  <path d="M12 3.5c3 3.6 5.5 6.6 5.5 10a5.5 5.5 0 0 1-11 0c0-3.4 2.5-6.4 5.5-10z" />
                ) : (
                  <path d="M5 4h14v16H5zM12 8c1.6 1.9 2.8 3.3 2.8 5a2.8 2.8 0 0 1-5.6 0c0-1.7 1.2-3.1 2.8-5z" />
                )}
              </svg>
            </button>
          )}
          <button type="button" className="mv-btn" aria-label="Expand" title="Back to the big view" onClick={onExpand}>
            <svg viewBox="0 0 24 24" width="13" height="13" fill="none" stroke="currentColor" strokeWidth="1.8" strokeLinecap="round" strokeLinejoin="round" aria-hidden="true">
              <path d="M14 4h6v6M10 20H4v-6M20 4l-7 7M4 20l7-7" />
            </svg>
          </button>
        </div>
      </header>
      <label className="mv-search">
        <Icon name="search" size={12} />
        <input
          ref={searchRef}
          value={query}
          onChange={(e) => setQuery(e.target.value)}
          onKeyDown={onSearchKey}
          placeholder="Find a service"
          aria-label="Find a service"
        />
        {enter?.kind === 'start' && <span className="mv-hint">Enter starts {enter.service.name}</span>}
      </label>
      <main className="mv-body">{body}</main>
      <Toasts />
    </div>
  );
}
