// Mirrors src/RepoManager.Contracts/Dtos.cs. JSON is camelCase, enums are camelCase strings,
// null fields are omitted (hence optional `?:` for every nullable C# member).
// DateTimeOffset values arrive as ISO 8601 strings.

export type ServiceState =
  | 'stopped'
  | 'preparing'
  | 'waitingDeps'
  | 'starting'
  | 'running'
  | 'unhealthy'
  | 'stopping'
  | 'crashed'
  | 'failed';

export type LogStream = 'out' | 'err' | 'sys';

export interface ServiceDto {
  id: string;
  project: string;
  worktree?: string;
  name: string;
  state: ServiceState;
  port?: number;
  url?: string;
  pid?: number;
  startedAt?: string;
  restartCount: number;
  lastError?: string;
  lastExitCode?: number;
  worktreeReady: boolean;
  autoRestart: boolean;
  hasHealth: boolean;
  disabled: boolean;
  desired: boolean;
  command: string;
  dependsOn: string[];
}

export interface TaskDto {
  id: string;
  name: string;
  command: string;
  running: boolean;
  lastExitCode?: number;
  lastRunAt?: string;
}

export interface GitInfoDto {
  branch?: string;
  dirty: boolean;
  ahead: number;
  behind: number;
  upstream?: string;
}

export interface InstanceDto {
  /** "project" for the main checkout, "project@worktree" for a worktree. */
  key: string;
  worktree?: string;
  root: string;
  git?: GitInfoDto;
  services: ServiceDto[];
  tasks: TaskDto[];
}

export interface ProjectDto {
  name: string;
  root: string;
  tags: string[];
  notifications: boolean;
  valid: boolean;
  /** Entries starting with "warning: " are warnings, the rest are errors. */
  errors: string[];
  instances: InstanceDto[];
}

export interface LogLineDto {
  timestamp: string;
  stream: LogStream;
  text: string;
  /** Service id, or "task:<taskId>". */
  source: string;
}

export interface ActionResultDto {
  ok: boolean;
  message?: string;
  error?: string;
  affected: string[];
  logTail: string[];
}

export interface PortDto {
  port: number;
  pid: number;
  processName?: string;
  commandLine?: string;
  managedBy?: string;
  address: string;
}

export interface ResolveResultDto {
  project?: string;
  instance?: string;
  worktree?: string;
  serviceIds: string[];
  error?: string;
}

export interface ScanResultDto {
  path: string;
  name: string;
  registered: boolean;
  markers: string[];
}

/** Origin layer: "launch.json", "devservers.json", "central", "default" or "devservers.json+central" (env). */
export type ConfigOrigin = string;

export interface ConfigFieldDto {
  field: string;
  value?: string;
  origin: ConfigOrigin;
}

export interface EffectiveServiceDto {
  name: string;
  fields: ConfigFieldDto[];
}

export interface EffectiveConfigDto {
  project: string;
  worktree?: string;
  services: EffectiveServiceDto[];
  tasks: EffectiveServiceDto[];
  /** Raw JSON of the central (layer 3) overrides for this project. */
  overridesJson: string;
  errors: string[];
}

export interface DaemonInfoDto {
  version: string;
  pid: number;
  port: number;
  startedAt: string;
}

export type EventDto =
  | { type: 'status'; payload: ServiceDto }
  | { type: 'task'; payload: TaskDto }
  | { type: 'log'; payload: LogLineDto }
  | { type: 'projects'; payload?: undefined };

// ---- Requests ----

export interface ServiceActionRequest {
  target: string;
  cwd?: string;
  killOwner?: boolean;
  force?: boolean;
}

export interface InstanceActionRequest {
  instance: string;
}

export interface GroupActionRequest {
  tag: string;
}

export interface TaskRunRequest {
  target: string;
  cwd?: string;
}

export interface AddProjectRequest {
  path: string;
}

export interface RemoveProjectRequest {
  project: string;
}

export interface KillPidRequest {
  pid: number;
}

export type OpenTool = 'vscode' | 'rider' | 'explorer' | 'terminal';

export interface OpenRequest {
  instance: string;
  tool: OpenTool;
}

export interface ProjectSettingsRequest {
  project: string;
  tags?: string[];
  notifications?: boolean;
  overridesJson?: string;
}
