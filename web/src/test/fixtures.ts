import type { InstanceDto, LogLineDto, ProjectDto, ServiceDto, TaskDto } from '../api/types';

export function svc(instance: string, name: string, over: Partial<ServiceDto> = {}): ServiceDto {
  const [project, worktree] = instance.split('@');
  return {
    id: `${instance}/${name}`,
    project,
    worktree,
    name,
    state: 'stopped',
    restartCount: 0,
    worktreeReady: true,
    autoRestart: false,
    hasHealth: false,
    disabled: false,
    desired: false,
    command: 'npm run dev',
    dependsOn: [],
    ...over,
  };
}

export function task(instance: string, name: string, over: Partial<TaskDto> = {}): TaskDto {
  return { id: `${instance}/${name}`, name, command: 'dotnet test', running: false, ...over };
}

export function instance(key: string, services: ServiceDto[], tasks: TaskDto[] = [], over: Partial<InstanceDto> = {}): InstanceDto {
  const worktree = key.includes('@') ? key.split('@')[1] : undefined;
  return {
    key,
    worktree,
    root: 'C:/repos/' + key.replace('@', '.wt/'),
    services,
    tasks,
    git: { branch: 'main', dirty: false, ahead: 0, behind: 0 },
    ...over,
  };
}

export function project(name: string, instances: InstanceDto[], over: Partial<ProjectDto> = {}): ProjectDto {
  return { name, root: 'C:/repos/' + name, tags: [], notifications: true, valid: true, errors: [], instances, ...over };
}

export function line(source: string, text: string, ts: string, stream: LogLineDto['stream'] = 'out'): LogLineDto {
  return { source, text, timestamp: ts, stream };
}

/** panel-pro with api (running), web (failed), docs (disabled), a worktree and a task; plus "other". */
export function sampleProjects(): ProjectDto[] {
  const main = instance(
    'panel-pro',
    [
      svc('panel-pro', 'api', {
        state: 'running',
        port: 5080,
        url: 'http://127.0.0.1:5080',
        startedAt: new Date(Date.now() - 65_000).toISOString(),
        pid: 1234,
        hasHealth: true,
        autoRestart: true,
        restartCount: 2,
      }),
      svc('panel-pro', 'web', { state: 'failed', port: 5173, lastError: 'port 5173 is in use by node.exe (PID 999)', lastExitCode: 1 }),
      svc('panel-pro', 'docs', { disabled: true }),
    ],
    [task('panel-pro', 'test', { lastExitCode: 0 })],
  );
  const wt = instance('panel-pro@feat-x', [svc('panel-pro@feat-x', 'api', { worktreeReady: false, port: 20001 })]);
  return [project('panel-pro', [main, wt], { tags: ['work'] }), project('other', [instance('other', [svc('other', 'site', { state: 'starting' })])])];
}
