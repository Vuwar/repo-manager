import type { ActionResultDto, AgentSessionDto, TownProgressDto, XpAwardDto, DaemonInfoDto, InstanceDto, LogLineDto, ProjectDto, ServiceDto, TaskDto } from '../api/types';
import { instanceOfSource, parseTime } from '../lib/format';

export const MAX_LOG_LINES = 5000;

export type View = { kind: 'instance'; key: string } | { kind: 'ports' } | { kind: 'none' };
export type ProjectTab = 'services' | 'config';
export type Connection = 'connecting' | 'open' | 'reconnecting';

export interface ToastAction {
  label: string;
  run: () => void;
}

export interface Toast {
  id: number;
  kind: 'error' | 'success' | 'info';
  title: string;
  message?: string;
  logTail?: string[];
  actions?: ToastAction[];
}

export interface AppState {
  projects: ProjectDto[] | null;
  projectsError: string | null;
  authFailed: boolean;
  connection: Connection;
  daemon: DaemonInfoDto | null;
  view: View;
  tab: ProjectTab;
  /** Selected log sources (service ids and "task:<taskId>"), in selection order. */
  logSources: string[];
  /** Log buffers of selected sources. */
  logs: Record<string, LogLineDto[]>;
  /** In-flight actions: key (service id, "instance:<key>", "group:<tag>", ...) -> label. */
  pending: Record<string, string>;
  toasts: Toast[];
  /** Bumped by "openConfig", so a view that has no config tab (simple mode) can switch to one. */
  configRequests: number;
  /** Claude Code sessions on this machine, by session id. */
  agents: Record<string, AgentSessionDto>;
  /** Recent status messages per session ("Used Bash"), oldest first; the dialogue box shows them while working. */
  agentLog: Record<string, string[]>;
  /** Agent Town XP for the user and each repo; null until loaded. */
  progress: TownProgressDto | null;
  /** The latest XP awards (at most 10, newest last), stamped with when they arrived (ms). */
  awards: (XpAwardDto & { at: number })[];
}

export type Action =
  | { type: 'projectsLoaded'; projects: ProjectDto[] }
  | { type: 'projectsFailed'; error: string }
  | { type: 'status'; service: ServiceDto }
  | { type: 'task'; task: TaskDto }
  | { type: 'logLines'; lines: LogLineDto[] }
  | { type: 'logHistory'; source: string; lines: LogLineDto[] }
  | { type: 'clearLogs'; sources: string[] }
  | { type: 'selectInstance'; key: string }
  | { type: 'showPorts' }
  | { type: 'setTab'; tab: ProjectTab }
  | { type: 'openConfig'; instance: string }
  | { type: 'setLogSources'; sources: string[] }
  | { type: 'connection'; state: Connection }
  | { type: 'daemon'; info: DaemonInfoDto }
  | { type: 'pending'; key: string; label: string | null }
  | { type: 'toast'; toast: Toast }
  | { type: 'dismissToast'; id: number }
  | { type: 'authFailed' }
  | { type: 'agentsLoaded'; agents: AgentSessionDto[] }
  | { type: 'agent'; agent: AgentSessionDto }
  | { type: 'agentEnd'; id: string }
  | { type: 'progressLoaded'; progress: TownProgressDto }
  | { type: 'progress'; progress: TownProgressDto; award: XpAwardDto; at: number };

export function initialState(persistedInstance?: string | null): AppState {
  return {
    projects: null,
    projectsError: null,
    authFailed: false,
    connection: 'connecting',
    daemon: null,
    view: persistedInstance ? { kind: 'instance', key: persistedInstance } : { kind: 'none' },
    tab: 'services',
    logSources: [],
    logs: {},
    pending: {},
    toasts: [],
    configRequests: 0,
    agents: {},
    agentLog: {},
    progress: null,
    awards: [],
  };
}

export function findInstance(projects: ProjectDto[] | null, key: string): { project: ProjectDto; instance: InstanceDto } | null {
  if (!projects) return null;
  for (const project of projects)
    for (const instance of project.instances) if (instance.key.toLowerCase() === key.toLowerCase()) return { project, instance };
  return null;
}

function firstInstance(projects: ProjectDto[]): string | null {
  for (const p of projects) if (p.instances.length) return p.instances[0].key;
  return null;
}

/** Default log selection for an instance: the first running service, else the first enabled one. */
function defaultSources(inst: InstanceDto | undefined): string[] {
  if (!inst) return [];
  const pick =
    inst.services.find((s) => s.state === 'running' || s.state === 'unhealthy') ??
    inst.services.find((s) => !s.disabled) ??
    inst.services[0];
  return pick ? [pick.id] : [];
}

function selectInstance(state: AppState, key: string): AppState {
  const found = findInstance(state.projects, key);
  const realKey = found?.instance.key ?? key;
  const keep = state.logSources.filter((s) => instanceOfSource(s).toLowerCase() === realKey.toLowerCase());
  const sources = keep.length ? keep : defaultSources(found?.instance);
  return withSources({ ...state, view: { kind: 'instance', key: realKey } }, sources);
}

function withSources(state: AppState, sources: string[]): AppState {
  const logs: Record<string, LogLineDto[]> = {};
  for (const s of sources) if (state.logs[s]) logs[s] = state.logs[s];
  return { ...state, logSources: sources, logs };
}

function mapInstances(projects: ProjectDto[], fn: (i: InstanceDto) => InstanceDto): ProjectDto[] {
  let changed = false;
  const next = projects.map((p) => {
    let pChanged = false;
    const instances = p.instances.map((i) => {
      const n = fn(i);
      if (n !== i) pChanged = true;
      return n;
    });
    if (!pChanged) return p;
    changed = true;
    return { ...p, instances };
  });
  return changed ? next : projects;
}

function cap(lines: LogLineDto[]): LogLineDto[] {
  return lines.length > MAX_LOG_LINES ? lines.slice(lines.length - MAX_LOG_LINES) : lines;
}

/** Keeps the newer snapshot: awards are published outside the daemon's lock and can arrive out of order. */
function newerProgress(current: TownProgressDto | null, next: TownProgressDto): TownProgressDto {
  return current && (current.revision ?? 0) > (next.revision ?? 0) ? current : next;
}

const AGENT_LOG_MAX = 5;

/** Appends a status message unless it repeats the last one; keeps the newest AGENT_LOG_MAX. */
function logWith(log: string[] | undefined, message: string): string[] {
  const list = log ?? [];
  if (!message || list[list.length - 1] === message) return list;
  return [...list, message].slice(-AGENT_LOG_MAX);
}

export function reducer(state: AppState, action: Action): AppState {
  switch (action.type) {
    case 'projectsLoaded': {
      let next: AppState = { ...state, projects: action.projects, projectsError: null };
      if (state.view.kind === 'instance') {
        const found = findInstance(action.projects, state.view.key);
        if (!found) {
          const first = firstInstance(action.projects);
          next = first ? selectInstance(next, first) : withSources({ ...next, view: { kind: 'none' } }, []);
        } else if (state.logSources.length === 0 && state.projects === null) {
          next = selectInstance(next, found.instance.key);
        }
      } else if (state.view.kind === 'none') {
        const first = firstInstance(action.projects);
        if (first) next = selectInstance(next, first);
      }
      return next;
    }
    case 'projectsFailed':
      return { ...state, projectsError: action.error };
    case 'status': {
      if (!state.projects) return state;
      const svc = action.service;
      const projects = mapInstances(state.projects, (inst) => {
        const idx = inst.services.findIndex((s) => s.id === svc.id);
        if (idx === -1) return inst;
        const services = inst.services.slice();
        services[idx] = svc;
        return { ...inst, services };
      });
      return projects === state.projects ? state : { ...state, projects };
    }
    case 'task': {
      if (!state.projects) return state;
      const t = action.task;
      const projects = mapInstances(state.projects, (inst) => {
        const idx = inst.tasks.findIndex((x) => x.id === t.id);
        if (idx === -1) return inst;
        const tasks = inst.tasks.slice();
        tasks[idx] = t;
        return { ...inst, tasks };
      });
      return projects === state.projects ? state : { ...state, projects };
    }
    case 'logLines': {
      const wanted = new Set(state.logSources);
      const add: Record<string, LogLineDto[]> = {};
      for (const l of action.lines) {
        if (!wanted.has(l.source)) continue;
        (add[l.source] ??= []).push(l);
      }
      const keys = Object.keys(add);
      if (!keys.length) return state;
      const logs = { ...state.logs };
      for (const k of keys) logs[k] = cap((logs[k] ?? []).concat(add[k]));
      return { ...state, logs };
    }
    case 'logHistory': {
      if (!state.logSources.includes(action.source)) return state;
      const history = action.lines;
      const lastTs = history.length ? parseTime(history[history.length - 1].timestamp) : -Infinity;
      // Keep live lines that arrived while history was loading and are newer than it.
      const live = (state.logs[action.source] ?? []).filter((l) => parseTime(l.timestamp) > lastTs);
      return { ...state, logs: { ...state.logs, [action.source]: cap(history.concat(live)) } };
    }
    case 'clearLogs': {
      const logs = { ...state.logs };
      for (const s of action.sources) if (logs[s]) logs[s] = [];
      return { ...state, logs };
    }
    case 'selectInstance':
      return selectInstance(state, action.key);
    case 'showPorts':
      return { ...state, view: { kind: 'ports' } };
    case 'setTab':
      return { ...state, tab: action.tab };
    case 'openConfig':
      return { ...selectInstance(state, action.instance), tab: 'config', configRequests: state.configRequests + 1 };
    case 'setLogSources': {
      const uniq = Array.from(new Set(action.sources));
      return withSources(state, uniq);
    }
    case 'connection':
      return state.connection === action.state ? state : { ...state, connection: action.state };
    case 'daemon':
      return { ...state, daemon: action.info };
    case 'pending': {
      const pending = { ...state.pending };
      if (action.label === null) delete pending[action.key];
      else pending[action.key] = action.label;
      return { ...state, pending };
    }
    case 'toast':
      return { ...state, toasts: [...state.toasts.slice(-5), action.toast] };
    case 'dismissToast':
      return { ...state, toasts: state.toasts.filter((t) => t.id !== action.id) };
    case 'agentsLoaded':
      return {
        ...state,
        agents: Object.fromEntries(action.agents.map((a) => [a.id, a])),
        agentLog: Object.fromEntries(action.agents.map((a) => [a.id, logWith(state.agentLog[a.id], a.message)])),
      };
    case 'agent': {
      const a = action.agent;
      return { ...state, agents: { ...state.agents, [a.id]: a }, agentLog: { ...state.agentLog, [a.id]: logWith(state.agentLog[a.id], a.message) } };
    }
    case 'agentEnd': {
      if (!(action.id in state.agents)) return state;
      const { [action.id]: _ended, ...agents } = state.agents;
      const { [action.id]: _log, ...agentLog } = state.agentLog;
      return { ...state, agents, agentLog };
    }
    case 'progressLoaded':
      // A fresh load is the truth; only live events can arrive out of order.
      return { ...state, progress: action.progress };
    case 'progress':
      return {
        ...state,
        progress: newerProgress(state.progress, action.progress),
        awards: [...state.awards, { ...action.award, at: action.at }].slice(-10),
      };
    case 'authFailed':
      return { ...state, authFailed: true };
    default:
      return state;
  }
}

export function resultToast(id: number, title: string, r: ActionResultDto, actions?: ToastAction[]): Toast {
  return r.ok
    ? { id, kind: 'success', title, message: r.message }
    : { id, kind: 'error', title, message: r.error, logTail: r.logTail, actions };
}
