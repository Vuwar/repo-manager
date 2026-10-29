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
import { handleEvent, loadAgents, loadDaemonInfo, loadLogHistory, loadProjects } from './state/actions';
import { findInstance } from './state/reducer';
import { dispatch, useAppState } from './state/store';
import { shortName } from './lib/format';
import { onShellCommand, postMode, syncTitleBar } from './lib/host';
import { readMode, saveMode, type UiMode } from './lib/simple';
import { needsYouCount, windowTitle } from './lib/town/street';
import { NoToken } from './views/NoToken';
import { PortsView } from './views/PortsView';
import { NoInstance, ProjectView } from './views/ProjectView';
import { MiniView } from './views/MiniView';
import { SimpleView } from './views/SimpleView';
import { TownView } from './views/TownView';

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
  const [mode, setModeState] = useState<UiMode>(readMode);
  const setMode = (m: UiMode) => {
    saveMode(m);
    setModeState(m);
  };
  // "Leave town" returns to the mode the town was entered from (simple when the app opened in the town).
  const beforeTown = useRef<UiMode>('simple');
  const enterTown = () => {
    if (mode !== 'town') beforeTown.current = mode;
    setMode('town');
  };
  // A notification click the shell could not route: show the town with that worker's dialogue.
  const [openSessionId, setOpenSessionId] = useState<string | null>(null);
  const modeRef = useRef(mode);
  modeRef.current = mode;
  useEffect(
    () =>
      onShellCommand((cmd) => {
        if (modeRef.current !== 'town') beforeTown.current = modeRef.current;
        setMode('town');
        setOpenSessionId(cmd.id);
      }),
    [],
  );
  // The desktop shell shrinks the window for the compact view and restores it after.
  useEffect(() => postMode(mode), [mode]);
  // The window's title bar takes the app's colors, following the light/dark switch.
  useEffect(() => syncTitleBar(), []);
  const configRequests = useAppState((s) => s.configRequests);
  // "Edit config" (e.g. from a failed-start toast) needs the full view: simple mode has no config tab.
  useEffect(() => {
    if (configRequests > 0) setMode('full');
  }, [configRequests]);
  const filterRef = useRef<HTMLInputElement>(null);
  const [ready] = useState(() => {
    configureApi(token, () => dispatch({ type: 'authFailed' }));
    return true;
  });

  // "(N) RepoManager" while N Claude sessions need the user; the shell copies it into the window title.
  const waiting = useAppState((s) => needsYouCount(s.agents));
  useEffect(() => {
    document.title = windowTitle(waiting);
  }, [waiting]);

  // Initial load + periodic refresh (git info, worktrees).
  useEffect(() => {
    if (!ready) return;
    void loadProjects();
    void loadDaemonInfo();
    void loadAgents();
    // Agents too: an event lost while the stream reopens would otherwise leave a ghost worker and a stale "(N)".
    const t = setInterval(() => {
      if (document.visibilityState !== 'visible') return;
      void loadProjects();
      void loadAgents();
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
        // Every (re)open may have missed agent events, also when the stream reopens for new log sources.
        void loadAgents();
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

  // Ctrl+K or "/" focuses the sidebar quick filter (simple mode has its own search).
  useEffect(() => {
    if (mode !== 'full') return;
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
  }, [mode]);

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

  if (mode === 'town') return <TownView onLeave={() => setMode(beforeTown.current)} openSessionId={openSessionId} />;
  if (mode === 'mini') return <MiniView onExpand={() => setMode('simple')} />;
  if (mode === 'simple') return <SimpleView onFullView={() => setMode('full')} onCompact={() => setMode('mini')} onTown={enterTown} />;

  let main;
  if (view.kind === 'ports') main = <PortsView />;
  else if (projects === null && !projectsError) main = <div className="project-view" />;
  else if (current) main = <ProjectView project={current.project} instance={current.instance} />;
  else main = <NoInstance />;

  return (
    <div className={`app${drawer ? ' drawer-open' : ''}`}>
      <Sidebar ref={filterRef} onAddProject={() => setAddOpen(true)} onNavigate={() => setDrawer(false)} onSimpleView={() => setMode('simple')} onTown={enterTown} />
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
