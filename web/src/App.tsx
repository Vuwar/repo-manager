import { useEffect, useMemo, useRef, useState } from 'react';
import { configureApi } from './api/client';
import { openEventStream } from './api/sse';
import { initToken } from './api/token';
import { AddProjectDialog } from './components/AddProjectDialog';
import { Icon } from './components/Icon';
import { LogPane } from './components/LogPane';
import { Sidebar } from './components/Sidebar';
import { Toasts } from './components/Toasts';
import { Button } from './components/ui';
import { handleEvent, loadDaemonInfo, loadLogHistory, loadProjects } from './state/actions';
import { findInstance } from './state/reducer';
import { dispatch, useAppState } from './state/store';
import { shortName } from './lib/format';
import { NoToken } from './views/NoToken';
import { PortsView } from './views/PortsView';
import { NoInstance, ProjectView } from './views/ProjectView';

export function App() {
  const [token] = useState(initToken);
  const authFailed = useAppState((s) => s.authFailed);
  if (!token) return <NoToken />;
  if (authFailed) return <NoToken expired />;
  return <Shell token={token} />;
}

function isTypingTarget(el: EventTarget | null): boolean {
  const t = el as HTMLElement | null;
  if (!t) return false;
  return t.isContentEditable || t.tagName === 'INPUT' || t.tagName === 'TEXTAREA' || t.tagName === 'SELECT';
}

function Shell({ token }: { token: string }) {
  const projects = useAppState((s) => s.projects);
  const projectsError = useAppState((s) => s.projectsError);
  const view = useAppState((s) => s.view);
  const logSources = useAppState((s) => s.logSources);
  const [addOpen, setAddOpen] = useState(false);
  const [drawer, setDrawer] = useState(false);
  const filterRef = useRef<HTMLInputElement>(null);
  const [ready] = useState(() => {
    configureApi(token, () => dispatch({ type: 'authFailed' }));
    return true;
  });

  // Initial load + periodic refresh (git info, worktrees).
  useEffect(() => {
    if (!ready) return;
    void loadProjects();
    void loadDaemonInfo();
    const t = setInterval(() => {
      if (document.visibilityState === 'visible') void loadProjects();
    }, 30_000);
    const onVis = () => document.visibilityState === 'visible' && void loadProjects();
    document.addEventListener('visibilitychange', onVis);
    return () => {
      clearInterval(t);
      document.removeEventListener('visibilitychange', onVis);
    };
  }, [ready]);

  // Live events. Re-opened whenever the set of watched log sources changes.
  const sourcesKey = useMemo(() => [...logSources].sort().join(','), [logSources]);
  useEffect(() => {
    const close = openEventStream({
      logSources: sourcesKey ? sourcesKey.split(',') : [],
      onEvent: handleEvent,
      onOpen: (reconnected) => {
        dispatch({ type: 'connection', state: 'open' });
        if (reconnected) {
          void loadProjects();
          void loadDaemonInfo();
          for (const s of sourcesKey ? sourcesKey.split(',') : []) void loadLogHistory(s);
        }
      },
      onDown: () => dispatch({ type: 'connection', state: 'reconnecting' }),
      onUnauthorized: () => dispatch({ type: 'authFailed' }),
    });
    return close;
  }, [sourcesKey]);

  // History for newly selected log sources.
  const loaded = useRef(new Set<string>());
  useEffect(() => {
    const now = new Set(logSources);
    for (const s of logSources) if (!loaded.current.has(s)) void loadLogHistory(s);
    loaded.current = now;
  }, [logSources]);

  // Ctrl+K or "/" focuses the sidebar quick filter.
  useEffect(() => {
    const onKey = (e: KeyboardEvent) => {
      const ctrlK = (e.ctrlKey || e.metaKey) && e.key.toLowerCase() === 'k';
      const slash = e.key === '/' && !e.ctrlKey && !e.metaKey && !e.altKey && !isTypingTarget(e.target);
      if (!ctrlK && !slash) return;
      if (document.querySelector('.modal')) return;
      e.preventDefault();
      setDrawer(true);
      requestAnimationFrame(() => {
        filterRef.current?.focus();
        filterRef.current?.select();
      });
    };
    window.addEventListener('keydown', onKey);
    return () => window.removeEventListener('keydown', onKey);
  }, []);

  useEffect(() => {
    if (!drawer) return;
    const onKey = (e: KeyboardEvent) => e.key === 'Escape' && !document.querySelector('.modal') && setDrawer(false);
    window.addEventListener('keydown', onKey);
    return () => window.removeEventListener('keydown', onKey);
  }, [drawer]);

  const current = view.kind === 'instance' ? findInstance(projects, view.key) : null;

  const labelFor = useMemo(() => {
    return (source: string) => {
      const name = shortName(source);
      const inst = source.startsWith('task:') ? source.slice(5) : source;
      const prefix = current && inst.toLowerCase().startsWith(current.instance.key.toLowerCase() + '/') ? '' : inst.slice(0, inst.lastIndexOf('/')) + '/';
      return (source.startsWith('task:') ? 'task:' : '') + prefix + name;
    };
  }, [current]);

  let main;
  if (view.kind === 'ports') main = <PortsView />;
  else if (projects === null && !projectsError) main = <div className="project-view" />;
  else if (current) main = <ProjectView project={current.project} instance={current.instance} />;
  else main = <NoInstance />;

  return (
    <div className={`app${drawer ? ' drawer-open' : ''}`}>
      <Sidebar ref={filterRef} onAddProject={() => setAddOpen(true)} onNavigate={() => setDrawer(false)} />
      <div className="drawer-scrim" onClick={() => setDrawer(false)} />
      <main className="main">
        <div className="topbar">
          <Button size="sm" variant="ghost" icon="menu" aria-label="Show projects" onClick={() => setDrawer(true)} />
          <span className="topbar-title">
            {view.kind === 'ports' ? 'Ports' : current ? current.project.name + (current.instance.worktree ? ' @' + current.instance.worktree : '') : 'RepoManager'}
          </span>
        </div>
        {projectsError && (
          <div className="banner banner-error global-error">
            <Icon name="alert" size={14} />
            <span>Could not load projects: {projectsError}</span>
            <Button size="sm" variant="ghost" onClick={() => void loadProjects()}>
              Retry
            </Button>
          </div>
        )}
        <div className="content">{main}</div>
        {view.kind === 'instance' && current && <LogPane labelFor={labelFor} />}
      </main>
      <Toasts />
      {addOpen && <AddProjectDialog onClose={() => setAddOpen(false)} />}
    </div>
  );
}
