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
  /** Follow-ups that can fix a failed start. */
  fixes?: StartFix[];
}

export type StartFix = 'kill-owner' | 'new-port' | 'force' | 'edit-config';

export interface PortDto {
  port: number;
  pid: number;
  processName?: string;
  commandLine?: string;
  managedBy?: string;
  address: string;
  processPath?: string;
  /** Runs as the current user. */
  mine: boolean;
  /** Windows or a service (PID 0/4, another account, or under %WINDIR%). */
  system: boolean;
  /** Process name is on the hidden list. */
  hidden: boolean;
  /** Shown in the default dev view. */
  dev: boolean;
  /** More than one process listens on this port. */
  conflict: boolean;
  canKill: boolean;
}

export interface ResolveResultDto {
  project?: string;
  instance?: string;
  worktree?: string;
  serviceIds: string[];
  error?: string;
}

/** Folder inside a checkout: "/"-separated path relative to its root ("." = root), plus project files found in it. */
export interface FolderDto {
  path: string;
  markers: string[];
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

// ---- Agent Town (mirrors RepoManager.Contracts/AgentDtos.cs) ----

export type AgentState = 'idle' | 'working' | 'waiting' | 'done' | 'failed';

export interface PermissionPromptDto {
  requestId: string;
  toolName: string;
  summary: string;
  /** False when it arrived while the town was not focused: answer it in the session window. */
  answerable: boolean;
}

/** One Claude Code session on this machine. */
export interface AgentSessionDto {
  id: string;
  project?: string;
  worktree?: string;
  /** Last segment of cwd; names the temporary building when project is not set. */
  folder: string;
  cwd: string;
  state: AgentState;
  message: string;
  prompt?: string;
  lastReply?: string;
  surface: 'desktop' | 'terminal' | 'other';
  hostSessionId?: string;
  /** When the current state began. */
  since: string;
  touched: string;
  canSend: boolean;
  prompts: PermissionPromptDto[];
  /** The session's title (the desktop app's name for it), else its first prompt shortened. */
  title?: string;
  /** Every assistant message of the last turn, separated by blank lines. */
  lastTurn?: string;
  /** A question the session left with suggested replies (the desktop app's wrap-up card). */
  wrapUp?: WrapUpDto;
  /** Grows with every change of this session. */
  seq?: number;
}

/** XP and level: the level starts at levelStart XP, the next one at nextLevel. */
export interface LevelDto {
  xp: number;
  level: number;
  levelStart: number;
  nextLevel: number;
}

/** The user's level and every repo's, by project name. */
export interface TownProgressDto {
  player: LevelDto;
  repos: Record<string, LevelDto>;
  /** Grows with every award; an older snapshot arriving late is dropped. */
  revision?: number;
}

/** One XP award: a prompt answered from the town, a town message that got a session working, or a finished turn. */
export interface XpAwardDto {
  kind: 'answer' | 'send' | 'turn';
  amount: number;
  sessionId?: string;
  repo?: string;
}

/** Agent Town attention settings and whether claude-setup installs the town hooks. */
export interface TownSettingsDto {
  tray: boolean;
  notifications: boolean;
  sound: boolean;
  hooks: boolean;
}

export interface WrapUpDto {
  recap?: string;
  ask?: string;
  actions: { label: string; message: string }[];
}

export type EventDto =
  | { type: 'status'; payload: ServiceDto }
  | { type: 'task'; payload: TaskDto }
  | { type: 'log'; payload: LogLineDto }
  | { type: 'projects'; payload?: undefined }
  | { type: 'agent'; payload: AgentSessionDto }
  | { type: 'agentEnd'; payload: { id: string } }
  | { type: 'progress'; payload: { progress: TownProgressDto; award: XpAwardDto } };

// ---- Requests ----

export interface ServiceActionRequest {
  target: string;
  cwd?: string;
  killOwner?: boolean;
  force?: boolean;
  newPort?: boolean;
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
