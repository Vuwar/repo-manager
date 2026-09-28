import type {
  ActionResultDto,
  DaemonInfoDto,
  EffectiveConfigDto,
  FolderDto,
  GroupActionRequest,
  InstanceActionRequest,
  LogLineDto,
  OpenTool,
  PortDto,
  ProjectDto,
  ProjectSettingsRequest,
  ScanResultDto,
  ServiceActionRequest,
} from './types';

export const TOKEN_HEADER = 'X-RepoManager-Token';

export class ApiError extends Error {
  readonly status: number;
  constructor(status: number, message: string) {
    super(message);
    this.status = status;
    this.name = 'ApiError';
  }
}

let token: string | null = null;
let onUnauthorized: (() => void) | null = null;

export function configureApi(t: string | null, unauthorized?: () => void): void {
  token = t;
  onUnauthorized = unauthorized ?? null;
}

export function authHeaders(): Record<string, string> {
  return token ? { [TOKEN_HEADER]: token } : {};
}

function reportUnauthorized(): void {
  onUnauthorized?.();
}

async function request<T>(method: 'GET' | 'POST', path: string, body?: unknown, signal?: AbortSignal): Promise<T> {
  const headers: Record<string, string> = { ...authHeaders(), Accept: 'application/json' };
  if (body !== undefined) headers['Content-Type'] = 'application/json';
  let res: Response;
  try {
    res = await fetch(path, { method, headers, body: body === undefined ? undefined : JSON.stringify(body), signal });
  } catch (e) {
    if ((e as Error).name === 'AbortError') throw e;
    throw new ApiError(0, 'RepoManager is not reachable');
  }
  if (res.status === 401) {
    reportUnauthorized();
    throw new ApiError(401, 'missing or wrong token');
  }
  const text = await res.text();
  let data: unknown = undefined;
  if (text) {
    try {
      data = JSON.parse(text);
    } catch {
      data = undefined;
    }
  }
  if (!res.ok) {
    const err = (data as Partial<ActionResultDto> | undefined)?.error;
    throw new ApiError(res.status, err || `${res.status} ${res.statusText || 'request failed'}`);
  }
  return data as T;
}

function qs(params: Record<string, string | number | boolean | undefined | null>): string {
  const p = new URLSearchParams();
  for (const [k, v] of Object.entries(params)) if (v !== undefined && v !== null && v !== '') p.set(k, String(v));
  const s = p.toString();
  return s ? '?' + s : '';
}

export function normalizeResult(r: Partial<ActionResultDto> | undefined | null): ActionResultDto {
  return {
    ok: !!r?.ok,
    message: r?.message,
    error: r?.error ?? (r?.ok ? undefined : 'unknown error'),
    affected: r?.affected ?? [],
    logTail: r?.logTail ?? [],
  };
}

/** Runs an action endpoint; transport failures become ok=false results so callers only handle one shape. */
async function action(path: string, body: unknown): Promise<ActionResultDto> {
  try {
    return normalizeResult(await request<ActionResultDto>('POST', path, body));
  } catch (e) {
    return normalizeResult({ ok: false, error: (e as Error).message });
  }
}

export const api = {
  ping: () => request<DaemonInfoDto>('GET', '/api/ping'),
  projects: (git = true, signal?: AbortSignal) =>
    request<ProjectDto[]>('GET', '/api/projects' + qs({ git: git ? undefined : false }), undefined, signal),

  startService: (req: ServiceActionRequest) => action('/api/services/start', req),
  stopService: (req: ServiceActionRequest) => action('/api/services/stop', req),
  restartService: (req: ServiceActionRequest) => action('/api/services/restart', req),

  startInstance: (req: InstanceActionRequest) => action('/api/instances/start', req),
  stopInstance: (req: InstanceActionRequest) => action('/api/instances/stop', req),

  startGroup: (req: GroupActionRequest) => action('/api/groups/start', req),
  stopGroup: (req: GroupActionRequest) => action('/api/groups/stop', req),

  runTask: (target: string) => action('/api/tasks/run', { target }),
  stopTask: (target: string) => action('/api/tasks/stop', { target }),

  logs: (target: string, opts: { tail?: number; grep?: string; since?: string } = {}, signal?: AbortSignal) =>
    request<LogLineDto[]>('GET', '/api/logs' + qs({ target, tail: opts.tail ?? 500, grep: opts.grep, since: opts.since }), undefined, signal),

  ports: (cmd = true, all = false) => request<PortDto[]>('GET', '/api/ports' + qs({ cmd, all })),
  killPid: (pid: number) => action('/api/ports/kill', { pid }),
  hidePortProcess: (processName: string, hidden: boolean) => action('/api/ports/hide', { processName, hidden }),

  addProject: (path: string) => action('/api/projects/add', { path }),
  removeProject: (project: string) => action('/api/projects/remove', { project }),
  projectSettings: (req: ProjectSettingsRequest) => action('/api/projects/settings', req),
  scan: (dir: string) => request<ScanResultDto[]>('GET', '/api/scan' + qs({ dir })),

  config: (instance: string, signal?: AbortSignal) =>
    request<EffectiveConfigDto>('GET', '/api/config' + qs({ instance }), undefined, signal),

  folders: (instance: string, signal?: AbortSignal) => request<FolderDto[]>('GET', '/api/folders' + qs({ instance }), undefined, signal),

  open: (instance: string, tool: OpenTool) => action('/api/open', { instance, tool }),
  refresh: () => action('/api/refresh', {}),
};
