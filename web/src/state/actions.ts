import { api } from '../api/client';
import type { ActionResultDto, EventDto, LogLineDto, OpenTool, ProjectSettingsRequest } from '../api/types';
import { resultToast, type Toast, type ToastAction } from './reducer';
import { dispatch, store } from './store';

let toastSeq = 1;

export function toast(t: Omit<Toast, 'id'>, autoDismissMs?: number): number {
  const id = toastSeq++;
  dispatch({ type: 'toast', toast: { ...t, id } });
  const ms = autoDismissMs ?? (t.kind === 'error' ? 0 : 3500);
  if (ms > 0) setTimeout(() => dispatch({ type: 'dismissToast', id }), ms);
  return id;
}

export function dismissToast(id: number): void {
  dispatch({ type: 'dismissToast', id });
}

function showResult(title: string, r: ActionResultDto, actions?: ToastAction[], quietSuccess = false): void {
  if (r.ok && quietSuccess) return;
  const t = resultToast(0, title, r, actions);
  toast({ kind: t.kind, title: t.title, message: t.message, logTail: t.logTail, actions: t.actions });
}

// ---- projects ----

let loading: Promise<void> | null = null;
let again = false;

/** Fetches /api/projects. Concurrent calls coalesce into at most one follow-up request. */
export function loadProjects(): Promise<void> {
  if (loading) {
    again = true;
    return loading;
  }
  loading = (async () => {
    try {
      do {
        again = false;
        try {
          const projects = await api.projects(true);
          dispatch({ type: 'projectsLoaded', projects: projects ?? [] });
        } catch (e) {
          dispatch({ type: 'projectsFailed', error: (e as Error).message });
        }
      } while (again);
    } finally {
      loading = null;
    }
  })();
  return loading;
}

export async function loadDaemonInfo(): Promise<void> {
  try {
    dispatch({ type: 'daemon', info: await api.ping() });
  } catch {
    /* shown via connection state */
  }
}

// ---- generic action runner ----

interface RunOptions {
  /** Pending key (disables the matching buttons while in flight). */
  key: string;
  label: string;
  title: string;
  quietSuccess?: boolean;
  retry?: (r: ActionResultDto) => ToastAction[] | undefined;
  after?: (r: ActionResultDto) => void;
}

export async function run(opts: RunOptions, fn: () => Promise<ActionResultDto>): Promise<ActionResultDto> {
  dispatch({ type: 'pending', key: opts.key, label: opts.label });
  try {
    const r = await fn();
    showResult(opts.title, r, r.ok ? undefined : opts.retry?.(r), opts.quietSuccess);
    opts.after?.(r);
    return r;
  } finally {
    dispatch({ type: 'pending', key: opts.key, label: null });
  }
}

// ---- services ----

export type ServiceVerb = 'start' | 'stop' | 'restart';

export function serviceAction(verb: ServiceVerb, id: string, flags: { killOwner?: boolean; force?: boolean } = {}) {
  const fn = verb === 'start' ? api.startService : verb === 'stop' ? api.stopService : api.restartService;
  const label = verb === 'start' ? 'Starting' : verb === 'stop' ? 'Stopping' : 'Restarting';
  return run(
    {
      key: id,
      label,
      title: `${verb[0].toUpperCase() + verb.slice(1)} ${id}`,
      quietSuccess: true,
      retry: verb === 'stop' ? undefined : (r) => retryActions(verb, id, r),
    },
    () => fn({ target: id, ...flags }),
  );
}

/** Offers the obvious follow-ups for common start failures (port owned elsewhere, worktree not ready). */
function retryActions(verb: ServiceVerb, id: string, r: ActionResultDto): ToastAction[] | undefined {
  const text = (r.error ?? '').toLowerCase();
  const actions: ToastAction[] = [];
  if (/(port|in use|owned|owner|pid)/.test(text) && !/already running as/.test(text))
    actions.push({ label: 'Kill owner & retry', run: () => void serviceAction(verb, id, { killOwner: true }) });
  if (/worktree/.test(text) || /--force/.test(text))
    actions.push({ label: 'Force start', run: () => void serviceAction(verb, id, { force: true }) });
  return actions.length ? actions : undefined;
}

export function instanceAction(verb: 'start' | 'stop', instance: string) {
  return run(
    { key: 'instance:' + instance, label: verb === 'start' ? 'Starting' : 'Stopping', title: `${verb === 'start' ? 'Start' : 'Stop'} all in ${instance}` },
    () => (verb === 'start' ? api.startInstance({ instance }) : api.stopInstance({ instance })),
  );
}

export function groupAction(verb: 'start' | 'stop', tag: string) {
  return run(
    { key: 'group:' + tag, label: verb === 'start' ? 'Starting' : 'Stopping', title: `${verb === 'start' ? 'Start' : 'Stop'} group ${tag}` },
    () => (verb === 'start' ? api.startGroup({ tag }) : api.stopGroup({ tag })),
  );
}

// ---- tasks ----

export function taskAction(verb: 'run' | 'stop', taskId: string) {
  return run(
    { key: 'task:' + taskId, label: verb === 'run' ? 'Running' : 'Stopping', title: `${verb === 'run' ? 'Run' : 'Stop'} task ${taskId}`, quietSuccess: true },
    () => (verb === 'run' ? api.runTask(taskId) : api.stopTask(taskId)),
  );
}

// ---- misc ----

export function openTool(instance: string, tool: OpenTool) {
  return run({ key: `open:${instance}:${tool}`, label: 'Opening', title: `Open in ${tool}`, quietSuccess: true }, () => api.open(instance, tool));
}

export function refreshAll() {
  return run({ key: 'refresh', label: 'Refreshing', title: 'Refresh', quietSuccess: true, after: () => void loadProjects() }, () => api.refresh());
}

export function killPid(pid: number) {
  return run({ key: 'kill:' + pid, label: 'Killing', title: `Kill PID ${pid}` }, () => api.killPid(pid));
}

export function addProject(path: string) {
  return run({ key: 'add:' + path, label: 'Adding', title: `Add ${path}`, after: (r) => r.ok && void loadProjects() }, () => api.addProject(path));
}

export function removeProject(project: string) {
  return run({ key: 'remove:' + project, label: 'Removing', title: `Remove ${project}`, after: (r) => r.ok && void loadProjects() }, () =>
    api.removeProject(project),
  );
}

export function saveSettings(req: ProjectSettingsRequest) {
  return run({ key: 'settings:' + req.project, label: 'Saving', title: `Save settings for ${req.project}`, after: (r) => r.ok && void loadProjects() }, () =>
    api.projectSettings(req),
  );
}

// ---- logs ----

export async function loadLogHistory(source: string): Promise<void> {
  try {
    const lines = await api.logs(source, { tail: 500 });
    dispatch({ type: 'logHistory', source, lines: lines ?? [] });
  } catch (e) {
    dispatch({
      type: 'logHistory',
      source,
      lines: [{ source, stream: 'sys', text: `could not load history: ${(e as Error).message}`, timestamp: new Date().toISOString() }],
    });
  }
}

let logQueue: LogLineDto[] = [];
let flushTimer: ReturnType<typeof setTimeout> | null = null;

/** Log lines are batched (~16 fps) so a chatty service does not re-render per line. */
function queueLog(line: LogLineDto): void {
  logQueue.push(line);
  if (flushTimer) return;
  flushTimer = setTimeout(() => {
    flushTimer = null;
    const lines = logQueue;
    logQueue = [];
    dispatch({ type: 'logLines', lines });
  }, 60);
}

export function handleEvent(e: EventDto): void {
  switch (e.type) {
    case 'status':
      if (e.payload) dispatch({ type: 'status', service: e.payload });
      break;
    case 'task':
      if (e.payload) dispatch({ type: 'task', task: e.payload });
      break;
    case 'log':
      if (e.payload) queueLog(e.payload);
      break;
    case 'projects':
      void loadProjects();
      break;
  }
}

export function getState() {
  return store.getState();
}
