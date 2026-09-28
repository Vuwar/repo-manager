import { useEffect, useMemo, useRef, useState, type KeyboardEvent, type MouseEvent } from 'react';
import type { InstanceDto, ProjectDto, ServiceDto } from '../api/types';
import { AddProjectDialog } from '../components/AddProjectDialog';
import { Icon } from '../components/Icon';
import { Toasts } from '../components/Toasts';
import { Button, Kbd, Spinner } from '../components/ui';
import { formatDuration, isActive, parseTime, stateLabel, stateTone } from '../lib/format';
import {
  activeProjectNames,
  enterAction,
  finderHits,
  projectServices,
  pruneTabs,
  readActiveTab,
  readTabs,
  runningCount,
  saveActiveTab,
  saveTabs,
  summaryTone,
  visibleServices,
  withNewlyActive,
  type FinderHit,
} from '../lib/simple';
import { useNow } from '../lib/useNow';
import { instanceAction, loadProjects, serviceAction } from '../state/actions';
import { useAppState } from '../state/store';

interface Props {
  onFullView: () => void;
}

function serviceHref(s: ServiceDto): string | undefined {
  return s.url ?? (s.port ? `http://localhost:${s.port}` : undefined);
}

function StatusLine({ s }: { s: ServiceDto }) {
  const now = useNow();
  if ((s.state === 'failed' || s.state === 'crashed') && s.lastError)
    return (
      <span className="sv-status sv-status-bad" title={s.lastError}>
        {stateLabel(s.state)}: {s.lastError}
      </span>
    );
  if ((s.state === 'running' || s.state === 'unhealthy') && s.startedAt) {
    const up = formatDuration(now - parseTime(s.startedAt));
    return (
      <span className={`sv-status sv-status-${stateTone(s.state)}`}>
        {s.state === 'unhealthy' ? 'Health check failing' : 'Running'} for {up}
      </span>
    );
  }
  return <span className={`sv-status sv-status-${stateTone(s.state)}`}>{stateLabel(s.state)}</span>;
}

function ServiceRow({ inst, s }: { inst: InstanceDto; s: ServiceDto }) {
  const busy = useAppState((st) => st.pending[s.id]);
  const active = isActive(s.state);
  const up = s.state === 'running' || s.state === 'unhealthy';
  const tone = stateTone(s.state);
  const href = serviceHref(s);
  const notReady = !!inst.worktree && !s.worktreeReady;
  return (
    <li className={`sv-row sv-${tone}`} data-testid={`simple-${s.id}`}>
      <span className="sv-lamp" aria-hidden="true" />
      <div className="sv-text">
        <span className="sv-name">{s.name}</span>
        <StatusLine s={s} />
      </div>
      <div className="sv-actions">
        {up && href && (
          <a className="btn btn-ghost btn-md sv-open" href={href} target="_blank" rel="noreferrer" title={href}>
            {s.port ? `:${s.port}` : 'Open'}
            <Icon name="external" size={12} />
          </a>
        )}
        {notReady && !active && (
          <span className="sv-notready" title="This service uses a fixed port. Starting it in a worktree needs a force start.">
            <Icon name="alert" size={13} />
          </span>
        )}
        {active && (
          <Button
            variant="ghost"
            icon="restart"
            title="Restart"
            aria-label={`Restart ${s.name}`}
            busy={busy === 'Restarting'}
            disabled={!!busy}
            onClick={() => void serviceAction('restart', s.id)}
          />
        )}
        {active || s.state === 'stopping' ? (
          <Button
            className="sv-go sv-go-stop"
            icon="stop"
            aria-label={`Stop ${s.name}`}
            busy={busy === 'Stopping' || s.state === 'stopping'}
            disabled={!!busy}
            onClick={() => void serviceAction('stop', s.id)}
          >
            Stop
          </Button>
        ) : (
          <Button
            className="sv-go sv-go-start"
            icon="play"
            aria-label={`Start ${s.name}`}
            busy={busy === 'Starting'}
            disabled={!!busy}
            onClick={() => void serviceAction('start', s.id)}
          >
            Start
          </Button>
        )}
      </div>
    </li>
  );
}

function InstanceBlock({ inst }: { inst: InstanceDto }) {
  const busy = useAppState((st) => st.pending['instance:' + inst.key]);
  const services = visibleServices(inst);
  const anyActive = services.some((s) => isActive(s.state));
  const anyStopped = services.some((s) => !isActive(s.state) && s.state !== 'stopping');
  return (
    <section className="sv-instance" aria-label={inst.worktree ? `Worktree ${inst.worktree}` : 'Main checkout'}>
      <header className="sv-instance-head">
        <h2>
          {inst.worktree ? (
            <>
              <Icon name="layers" size={13} /> {inst.worktree}
            </>
          ) : (
            'Main checkout'
          )}
        </h2>
        {inst.git?.branch && (
          <span className="sv-branch">
            <Icon name="branch" size={12} />
            {inst.git.branch}
          </span>
        )}
        {services.length > 1 && (
          <div className="sv-instance-actions">
            <Button size="sm" variant="ghost" icon="play" disabled={!!busy || !anyStopped} busy={busy === 'Starting'} onClick={() => void instanceAction('start', inst.key)}>
              Start all
            </Button>
            <Button size="sm" variant="ghost" icon="stop" disabled={!!busy || !anyActive} busy={busy === 'Stopping'} onClick={() => void instanceAction('stop', inst.key)}>
              Stop all
            </Button>
          </div>
        )}
      </header>
      {services.length ? (
        <ul className="sv-list">
          {services.map((s) => (
            <ServiceRow key={s.id} inst={inst} s={s} />
          ))}
        </ul>
      ) : (
        <p className="sv-none">No services here. Add them in the full view under Config.</p>
      )}
    </section>
  );
}

function RepoPanel({ project }: { project: ProjectDto }) {
  const [main, ...worktrees] = project.instances;
  const live = worktrees.filter((w) => w.services.some((s) => isActive(s.state)));
  const idle = worktrees.filter((w) => !live.includes(w));
  return (
    <div className="sv-panel">
      <div className="sv-repo-head">
        <h1>{project.name}</h1>
        <span className="sv-root" title={project.root}>
          {project.root}
        </span>
      </div>
      {main && <InstanceBlock inst={main} />}
      {live.map((w) => (
        <InstanceBlock key={w.key} inst={w} />
      ))}
      {idle.length > 0 && (
        <details className="sv-more">
          <summary>
            {idle.length === 1 ? '1 more worktree' : `${idle.length} more worktrees`}
            <Icon name="chevronDown" size={12} />
          </summary>
          {idle.map((w) => (
            <InstanceBlock key={w.key} inst={w} />
          ))}
        </details>
      )}
    </div>
  );
}

function Finder({ hits, query, onOpen }: { hits: FinderHit[]; query: string; onOpen: (p: string) => void }) {
  if (!hits.length)
    return (
      <div className="sv-empty">
        <p>Nothing matches “{query.trim()}”.</p>
        <p className="muted">Search looks at repo, worktree, branch and service names.</p>
      </div>
    );
  return (
    <div className="sv-panel sv-finder">
      {hits.map((h) => (
        <section key={h.instance.key} className="sv-hit">
          <header className="sv-hit-head">
            <button type="button" className="sv-hit-name" onClick={() => onOpen(h.project.name)} title={`Open ${h.project.name} as a tab`}>
              {h.project.name}
              {h.instance.worktree && <span className="sv-hit-wt"> @ {h.instance.worktree}</span>}
            </button>
            {h.instance.git?.branch && (
              <span className="sv-branch">
                <Icon name="branch" size={12} />
                {h.instance.git.branch}
              </span>
            )}
          </header>
          {h.services.length ? (
            <ul className="sv-list">
              {h.services.map((s) => (
                <ServiceRow key={s.id} inst={h.instance} s={s} />
              ))}
            </ul>
          ) : (
            <p className="sv-none">No services configured.</p>
          )}
        </section>
      ))}
    </div>
  );
}

function isTypingTarget(el: EventTarget | null): boolean {
  const t = el as HTMLElement | null;
  return !!t && (t.isContentEditable || t.tagName === 'INPUT' || t.tagName === 'TEXTAREA' || t.tagName === 'SELECT');
}

export function SimpleView({ onFullView }: Props) {
  const projects = useAppState((s) => s.projects);
  const projectsError = useAppState((s) => s.projectsError);
  const connection = useAppState((s) => s.connection);
  const [tabs, setTabs] = useState(readTabs);
  const [activeTab, setActiveTab] = useState<string | null>(readActiveTab);
  const [query, setQuery] = useState('');
  const [addOpen, setAddOpen] = useState(false);
  const searchRef = useRef<HTMLInputElement>(null);
  const prevActive = useRef<string[] | null>(null);

  useEffect(() => saveTabs(tabs), [tabs]);
  useEffect(() => saveActiveTab(activeTab), [activeTab]);

  // Repos that start running (here, in the full view, from devm or Claude) open as tabs.
  useEffect(() => {
    if (!projects) return;
    const now = activeProjectNames(projects);
    setTabs((t) => withNewlyActive(pruneTabs(t, projects), prevActive.current, now));
    prevActive.current = now;
  }, [projects]);

  const byName = useMemo(() => new Map((projects ?? []).map((p) => [p.name.toLowerCase(), p])), [projects]);
  const current = activeTab ? byName.get(activeTab.toLowerCase()) : undefined;
  const hits = useMemo(() => finderHits(projects ?? [], query), [projects, query]);
  const enter = enterAction(hits, query);
  const searching = query.trim() !== '';

  const openTab = (name: string) => {
    setTabs((t) => (t.some((x) => x.toLowerCase() === name.toLowerCase()) ? t : [...t, name]));
    setActiveTab(name);
    setQuery('');
  };

  const closeTab = (name: string) => {
    const i = tabs.indexOf(name);
    const next = tabs.filter((t) => t !== name);
    setTabs(next);
    if (activeTab === name) setActiveTab(next[Math.min(i, next.length - 1)] ?? null);
  };

  // Ctrl+K or "/" focuses the search.
  useEffect(() => {
    const onKey = (e: globalThis.KeyboardEvent) => {
      const ctrlK = (e.ctrlKey || e.metaKey) && e.key.toLowerCase() === 'k';
      const slash = e.key === '/' && !e.ctrlKey && !e.metaKey && !e.altKey && !isTypingTarget(e.target);
      if ((!ctrlK && !slash) || document.querySelector('.modal')) return;
      e.preventDefault();
      searchRef.current?.focus();
      searchRef.current?.select();
    };
    window.addEventListener('keydown', onKey);
    return () => window.removeEventListener('keydown', onKey);
  }, []);

  const onSearchKey = (e: KeyboardEvent<HTMLInputElement>) => {
    if (e.key === 'Escape') {
      setQuery('');
      return;
    }
    if (e.key !== 'Enter' || !enter) return;
    e.preventDefault();
    if (enter.kind === 'start') void serviceAction('start', enter.service.id);
    else openTab(enter.project);
  };

  const onTabMouseDown = (e: MouseEvent, name: string) => {
    if (e.button === 1) {
      e.preventDefault();
      closeTab(name);
    }
  };

  let body;
  if (projects === null && !projectsError) body = <div className="sv-empty">{<Spinner size={16} />}</div>;
  else if (projects && !projects.length)
    body = (
      <div className="sv-empty">
        <p>No repos yet.</p>
        <Button variant="primary" icon="plus" onClick={() => setAddOpen(true)}>
          Add a repo
        </Button>
      </div>
    );
  else if (searching || !current) body = <Finder hits={hits} query={query} onOpen={openTab} />;
  else body = <RepoPanel project={current} />;

  const hint = enter?.kind === 'start' ? `Enter starts ${enter.service.name}` : enter?.kind === 'open' ? `Enter opens ${enter.project}` : null;

  return (
    <div className="simple">
      <header className="sv-head">
        <div className="brand">
          <span className="brand-mark" aria-hidden="true">
            <svg viewBox="0 0 20 20" width="18" height="18">
              <rect x="1" y="1" width="18" height="18" rx="5" fill="var(--accent)" />
              <path d="M6 13.5V6.5h4a2.2 2.2 0 0 1 0 4.4H6m4 0 3 2.6" fill="none" stroke="#fff" strokeWidth="1.8" strokeLinecap="round" strokeLinejoin="round" />
            </svg>
          </span>
          <span className="brand-name">RepoManager</span>
        </div>
        <label className="sv-search">
          <Icon name="search" size={15} />
          <input
            ref={searchRef}
            value={query}
            onChange={(e) => setQuery(e.target.value)}
            onKeyDown={onSearchKey}
            placeholder="Find a repo or service to run"
            aria-label="Find a repo or service"
            autoFocus
          />
          {hint ? <span className="sv-hint">{hint}</span> : !query && <Kbd>Ctrl K</Kbd>}
        </label>
        <div className="sv-head-right">
          {connection !== 'open' && (
            <span className="sv-conn" role="status">
              <Spinner size={11} /> {connection === 'reconnecting' ? 'Reconnecting' : 'Connecting'}
            </span>
          )}
          <Button variant="ghost" icon="menu" onClick={onFullView} title="Logs, config, ports and tasks">
            Full view
          </Button>
        </div>
      </header>

      <nav className="sv-tabs" aria-label="Repos">
        <button
          type="button"
          className={`sv-tab sv-tab-all${!current && !searching ? ' on' : ''}`}
          aria-current={!current && !searching ? 'page' : undefined}
          onClick={() => {
            setActiveTab(null);
            setQuery('');
          }}
        >
          <Icon name="layers" size={13} />
          All repos
        </button>
        {tabs.map((name) => {
          const p = byName.get(name.toLowerCase());
          if (!p) return null;
          const svcs = projectServices(p);
          const n = runningCount(svcs);
          const on = !searching && current?.name === p.name;
          return (
            <div key={name} className={`sv-tab${on ? ' on' : ''}`} onMouseDown={(e) => onTabMouseDown(e, name)}>
              <button type="button" className="sv-tab-main" aria-current={on ? 'page' : undefined} onClick={() => openTab(p.name)}>
                <span className={`dot dot-${summaryTone(svcs)}`} aria-hidden="true" />
                {p.name}
                {n > 0 && (
                  <span className="sv-tab-count" title={`${n} running`}>
                    {n}
                  </span>
                )}
              </button>
              <button
                type="button"
                className="sv-tab-x"
                aria-label={`Close ${p.name} tab`}
                title={n > 0 ? 'Close tab. Services keep running.' : 'Close tab'}
                onClick={() => closeTab(name)}
              >
                <Icon name="x" size={11} />
              </button>
            </div>
          );
        })}
      </nav>

      {projectsError && (
        <div className="banner banner-error global-error">
          <Icon name="alert" size={14} />
          <span>Could not load repos: {projectsError}</span>
          <Button size="sm" variant="ghost" onClick={() => void loadProjects()}>
            Retry
          </Button>
        </div>
      )}
      <main className="sv-body">{body}</main>
      <Toasts />
      {addOpen && <AddProjectDialog onClose={() => setAddOpen(false)} />}
    </div>
  );
}
