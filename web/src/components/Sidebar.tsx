import { forwardRef, useMemo, useState, type KeyboardEvent } from 'react';
import type { InstanceDto } from '../api/types';
import { filterInstances, groupProjects, groupSummary } from '../lib/groups';
import { groupAction, refreshAll } from '../state/actions';
import { dispatch, useAppState } from '../state/store';
import { Icon } from './Icon';
import { Button, Kbd, Spinner, StatusDot } from './ui';

const COLLAPSED_KEY = 'repomanager.collapsed';

function readCollapsed(): Set<string> {
  try {
    return new Set(JSON.parse(localStorage.getItem(COLLAPSED_KEY) ?? '[]') as string[]);
  } catch {
    return new Set();
  }
}

function saveCollapsed(s: Set<string>) {
  try {
    localStorage.setItem(COLLAPSED_KEY, JSON.stringify([...s]));
  } catch {
    /* ignore */
  }
}

function Dots({ inst }: { inst: InstanceDto }) {
  const svcs = inst.services.filter((s) => !s.disabled);
  if (!svcs.length) return null;
  return (
    <span className="dots">
      {svcs.slice(0, 8).map((s) => (
        <StatusDot key={s.id} state={s.state} title={`${s.name}: ${s.state}`} />
      ))}
      {svcs.length > 8 && <span className="muted small">+{svcs.length - 8}</span>}
    </span>
  );
}

function instanceLabel(inst: InstanceDto): string {
  return inst.worktree ?? 'main';
}

interface Props {
  onAddProject: () => void;
  onNavigate: () => void;
}

export const Sidebar = forwardRef<HTMLInputElement, Props>(function Sidebar({ onAddProject, onNavigate }, filterRef) {
  const projects = useAppState((s) => s.projects);
  const view = useAppState((s) => s.view);
  const pending = useAppState((s) => s.pending);
  const connection = useAppState((s) => s.connection);
  const daemon = useAppState((s) => s.daemon);
  const [query, setQuery] = useState('');
  const [collapsed, setCollapsed] = useState(readCollapsed);

  const groups = useMemo(() => groupProjects(projects ?? []), [projects]);
  const hits = useMemo(() => filterInstances(projects ?? [], query), [projects, query]);
  const activeKey = view.kind === 'instance' ? view.key : null;

  const toggle = (id: string) => {
    const next = new Set(collapsed);
    if (next.has(id)) next.delete(id);
    else next.add(id);
    setCollapsed(next);
    saveCollapsed(next);
  };

  const go = (key: string, service?: string) => {
    dispatch({ type: 'selectInstance', key });
    if (service) dispatch({ type: 'setLogSources', sources: [service] });
    onNavigate();
  };

  const onFilterKey = (e: KeyboardEvent<HTMLInputElement>) => {
    if (e.key === 'Escape') {
      setQuery('');
      (e.target as HTMLInputElement).blur();
    } else if (e.key === 'Enter' && hits.length) {
      const h = hits[0];
      go(h.instance.key, h.services[0]?.id);
      setQuery('');
      (e.target as HTMLInputElement).blur();
    }
  };

  const renderInstance = (inst: InstanceDto) => (
    <button
      type="button"
      key={inst.key}
      className={`nav-item nav-instance nested${activeKey === inst.key ? ' active' : ''}`}
      onClick={() => go(inst.key)}
      title={inst.root}
    >
      {inst.worktree ? <Icon name="branch" size={12} /> : <span className="nav-bullet" />}
      <span className="nav-label">{instanceLabel(inst)}</span>
      {inst.git?.branch && inst.git.branch !== instanceLabel(inst) && <span className="nav-branch">{inst.git.branch}</span>}
      <Dots inst={inst} />
    </button>
  );

  return (
    <aside className="sidebar">
      <div className="side-top">
        <div className="brand">
          <span className="brand-mark" aria-hidden="true">
            <svg viewBox="0 0 20 20" width="18" height="18">
              <rect x="1" y="1" width="18" height="18" rx="5" fill="var(--accent)" />
              <path d="M6 13.5V6.5h4a2.2 2.2 0 0 1 0 4.4H6m4 0 3 2.6" fill="none" stroke="#fff" strokeWidth="1.8" strokeLinecap="round" strokeLinejoin="round" />
            </svg>
          </span>
          <span className="brand-name">RepoManager</span>
        </div>
        <div className="side-actions">
          <Button size="sm" variant="ghost" icon="plus" title="Add project" aria-label="Add project" onClick={onAddProject} />
          <Button
            size="sm"
            variant="ghost"
            icon="plug"
            title="Ports"
            aria-label="Ports"
            className={view.kind === 'ports' ? 'on' : ''}
            onClick={() => {
              dispatch({ type: 'showPorts' });
              onNavigate();
            }}
          />
          <Button size="sm" variant="ghost" icon="refresh" title="Reload config and worktrees" aria-label="Refresh" busy={!!pending.refresh} onClick={() => void refreshAll()} />
        </div>
      </div>

      <label className="side-filter">
        <Icon name="search" size={12} />
        <input ref={filterRef} value={query} onChange={(e) => setQuery(e.target.value)} onKeyDown={onFilterKey} placeholder="Filter projects & services" aria-label="Filter projects and services" />
        {!query && <Kbd>Ctrl K</Kbd>}
        {query && (
          <button type="button" className="clear-x" aria-label="Clear filter" onClick={() => setQuery('')}>
            <Icon name="x" size={11} />
          </button>
        )}
      </label>

      <nav className="side-scroll" aria-label="Projects">
        {projects === null ? (
          <div className="side-loading">
            <Spinner /> Loading projects…
          </div>
        ) : query ? (
          hits.length === 0 ? (
            <div className="side-note">Nothing matches “{query}”.</div>
          ) : (
            <div className="nav-group">
              <div className="group-head">
                <span className="group-name">Results</span>
                <span className="group-count">{hits.length}</span>
              </div>
              {hits.map((h) => (
                <div key={h.instance.key}>
                  <button type="button" className={`nav-item nav-instance${activeKey === h.instance.key ? ' active' : ''}`} onClick={() => go(h.instance.key)}>
                    {h.instance.worktree ? <Icon name="branch" size={12} /> : <span className="nav-bullet" />}
                    <span className="nav-label">
                      {h.project.name}
                      {h.instance.worktree && <span className="muted">@{h.instance.worktree}</span>}
                    </span>
                    <Dots inst={h.instance} />
                  </button>
                  {h.services.map((s) => (
                    <button type="button" key={s.id} className="nav-item nav-service nested" onClick={() => go(h.instance.key, s.id)}>
                      <StatusDot state={s.state} />
                      <span className="nav-label">{s.name}</span>
                      {s.port && <span className="nav-branch mono">:{s.port}</span>}
                    </button>
                  ))}
                </div>
              ))}
            </div>
          )
        ) : projects.length === 0 ? (
          <div className="side-note">
            No projects yet.
            <button type="button" className="linklike" onClick={onAddProject}>
              Add a project
            </button>
          </div>
        ) : (
          groups.map((g) => {
            const gid = 'group:' + (g.tag ?? '');
            const isCollapsed = collapsed.has(gid);
            const sum = groupSummary(g.projects);
            return (
              <div className="nav-group" key={gid}>
                <div className="group-head">
                  <button type="button" className="group-toggle" onClick={() => toggle(gid)} aria-expanded={!isCollapsed}>
                    <Icon name={isCollapsed ? 'chevron' : 'chevronDown'} size={11} />
                    <span className="group-name">{g.tag ?? 'Ungrouped'}</span>
                    <span className="group-count" title={`${sum.running} of ${sum.total} services running`}>
                      {sum.running}/{sum.total}
                    </span>
                  </button>
                  {g.tag && (
                    <span className="group-actions">
                      <Button
                        size="sm"
                        variant="ghost"
                        icon="play"
                        className="btn-go"
                        title={`Start group ${g.tag}`}
                        aria-label={`Start group ${g.tag}`}
                        busy={pending['group:' + g.tag] === 'Starting'}
                        disabled={!!pending['group:' + g.tag]}
                        onClick={() => void groupAction('start', g.tag!)}
                      />
                      <Button
                        size="sm"
                        variant="ghost"
                        icon="stop"
                        title={`Stop group ${g.tag}`}
                        aria-label={`Stop group ${g.tag}`}
                        busy={pending['group:' + g.tag] === 'Stopping'}
                        disabled={!!pending['group:' + g.tag]}
                        onClick={() => void groupAction('stop', g.tag!)}
                      />
                    </span>
                  )}
                </div>
                {!isCollapsed &&
                  g.projects.map((p) => {
                    const pid = `${gid}/${p.name}`;
                    const pCollapsed = collapsed.has(pid);
                    const main = p.instances.find((i) => !i.worktree) ?? p.instances[0];
                    const worktrees = p.instances.filter((i) => i !== main);
                    const bad = p.errors.some((e) => !e.startsWith('warning: '));
                    return (
                      <div className="nav-project" key={pid}>
                        <div className={`nav-item nav-proj${main && activeKey === main.key ? ' active' : ''}`}>
                          <button
                            type="button"
                            className="proj-chevron"
                            aria-label={pCollapsed ? `Expand ${p.name}` : `Collapse ${p.name}`}
                            aria-expanded={!pCollapsed}
                            onClick={() => toggle(pid)}
                            disabled={!worktrees.length}
                          >
                            {worktrees.length ? <Icon name={pCollapsed ? 'chevron' : 'chevronDown'} size={11} /> : null}
                          </button>
                          <button type="button" className="proj-main" onClick={() => main && go(main.key)} title={p.root}>
                            <span className="nav-label proj-name">{p.name}</span>
                            {main?.git?.branch && (pCollapsed || !worktrees.length) && <span className="nav-branch">{main.git.branch}</span>}
                            {(bad || !p.valid) && <Icon name="alert" size={12} className="icon bad-text" />}
                            {main && (pCollapsed || !worktrees.length) && <Dots inst={main} />}
                          </button>
                        </div>
                        {!pCollapsed && worktrees.length > 0 && [main, ...worktrees].map((w) => renderInstance(w))}
                      </div>
                    );
                  })}
              </div>
            );
          })
        )}
      </nav>

      <div className="side-foot">
        <span className={`conn conn-${connection}`}>
          <span className="conn-dot" />
          {connection === 'open' ? 'Connected' : connection === 'connecting' ? 'Connecting…' : 'Reconnecting…'}
        </span>
        {daemon && <span className="muted mono small">v{daemon.version} · :{daemon.port}</span>}
      </div>
    </aside>
  );
});
