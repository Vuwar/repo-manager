import type { GitInfoDto, InstanceDto, OpenTool, ProjectDto } from '../api/types';
import { Icon, type IconName } from '../components/Icon';
import { ServicesTable } from '../components/ServicesTable';
import { TasksList } from '../components/TasksList';
import { Button, EmptyState } from '../components/ui';
import { isActive } from '../lib/format';
import { instanceAction, openTool, serviceAction, taskAction } from '../state/actions';
import { dispatch, useAppState } from '../state/store';
import { ConfigView } from './ConfigView';

function GitBar({ git }: { git?: GitInfoDto }) {
  if (!git) return null;
  return (
    <div className="gitbar" aria-label="Git status">
      <span className="git-branch" title={git.upstream ? `tracking ${git.upstream}` : 'no upstream'}>
        <Icon name="branch" size={12} />
        <span className="mono">{git.branch ?? '(detached)'}</span>
      </span>
      {git.dirty ? (
        <span className="git-dirty" title="Uncommitted changes">
          <span className="dirty-dot" /> modified
        </span>
      ) : (
        <span className="git-clean" title="Working tree clean">
          <Icon name="check" size={11} /> clean
        </span>
      )}
      {(git.ahead > 0 || git.behind > 0) && (
        <span className="git-sync mono" title={`${git.ahead} ahead, ${git.behind} behind ${git.upstream ?? 'upstream'}`}>
          {git.ahead > 0 && (
            <span>
              <Icon name="arrowUp" size={11} />
              {git.ahead}
            </span>
          )}
          {git.behind > 0 && (
            <span>
              <Icon name="arrowDown" size={11} />
              {git.behind}
            </span>
          )}
        </span>
      )}
      {git.upstream && <span className="muted mono small git-up">{git.upstream}</span>}
    </div>
  );
}

export function ConfigBanner({ errors }: { errors: string[] }) {
  if (!errors.length) return null;
  const warn = errors.filter((e) => e.startsWith('warning: ')).map((e) => e.slice('warning: '.length));
  const err = errors.filter((e) => !e.startsWith('warning: '));
  return (
    <div className="banners">
      {err.length > 0 && (
        <div className="banner banner-error" role="alert">
          <Icon name="alert" size={14} />
          <div>
            <div className="banner-title">Configuration {err.length === 1 ? 'error' : `errors (${err.length})`} — this project cannot start</div>
            <ul>
              {err.map((e, i) => (
                <li key={i} className="mono">
                  {e}
                </li>
              ))}
            </ul>
          </div>
        </div>
      )}
      {warn.length > 0 && (
        <div className="banner banner-warn">
          <Icon name="info" size={14} />
          <div>
            <div className="banner-title">{warn.length === 1 ? 'Warning' : `Warnings (${warn.length})`}</div>
            <ul>
              {warn.map((e, i) => (
                <li key={i} className="mono">
                  {e}
                </li>
              ))}
            </ul>
          </div>
        </div>
      )}
    </div>
  );
}

const TOOLS: { tool: OpenTool; icon: IconName; label: string }[] = [
  { tool: 'vscode', icon: 'code', label: 'VS Code' },
  { tool: 'rider', icon: 'rider', label: 'Rider' },
  { tool: 'explorer', icon: 'folder', label: 'Explorer' },
  { tool: 'terminal', icon: 'terminal', label: 'Terminal' },
];

export function ProjectView({ project, instance }: { project: ProjectDto; instance: InstanceDto }) {
  const tab = useAppState((s) => s.tab);
  const selected = useAppState((s) => s.logSources);
  const pending = useAppState((s) => s.pending);
  const instPending = pending['instance:' + instance.key];
  const enabled = instance.services.filter((s) => !s.disabled);
  const running = enabled.filter((s) => isActive(s.state)).length;
  const setSources = (sources: string[]) => dispatch({ type: 'setLogSources', sources });

  return (
    <div className="project-view">
      <header className="pv-head">
        <div className="pv-title-row">
          <div className="pv-title">
            <h1>{project.name}</h1>
            {instance.worktree && (
              <span className="chip chip-accent" title="Git worktree instance (auto-assigned ports)">
                <Icon name="branch" size={11} />
                {instance.worktree}
              </span>
            )}
            {project.tags.map((t) => (
              <span key={t} className="chip chip-muted">
                {t}
              </span>
            ))}
          </div>
          <div className="pv-actions">
            <div className="tool-group" role="group" aria-label="Open in">
              {TOOLS.map((t) => (
                <Button
                  key={t.tool}
                  size="sm"
                  variant="ghost"
                  icon={t.icon}
                  title={`Open in ${t.label}`}
                  aria-label={`Open in ${t.label}`}
                  busy={!!pending[`open:${instance.key}:${t.tool}`]}
                  onClick={() => void openTool(instance.key, t.tool)}
                />
              ))}
            </div>
            <span className="divider" />
            <Button
              size="sm"
              icon="play"
              className="btn-go"
              busy={instPending === 'Starting'}
              disabled={!!instPending || !enabled.length || !project.valid}
              title={project.valid ? 'Start every enabled service' : 'Fix the configuration errors first'}
              onClick={() => void instanceAction('start', instance.key)}
            >
              Start all
            </Button>
            <Button size="sm" icon="stop" busy={instPending === 'Stopping'} disabled={!!instPending || running === 0} onClick={() => void instanceAction('stop', instance.key)}>
              Stop all
            </Button>
          </div>
        </div>
        <div className="pv-meta">
          <GitBar git={instance.git} />
          <span className="pv-root mono" title={instance.root}>
            {instance.root}
          </span>
        </div>
        <div className="tabs" role="tablist">
          <button type="button" role="tab" aria-selected={tab === 'services'} className={tab === 'services' ? 'on' : ''} onClick={() => dispatch({ type: 'setTab', tab: 'services' })}>
            Services
            <span className="tab-count">
              {running}/{enabled.length}
            </span>
          </button>
          <button type="button" role="tab" aria-selected={tab === 'config'} className={tab === 'config' ? 'on' : ''} onClick={() => dispatch({ type: 'setTab', tab: 'config' })}>
            Config
            {project.errors.some((e) => !e.startsWith('warning: ')) && <span className="tab-bad" aria-label="has errors" />}
          </button>
        </div>
      </header>

      <div className="pv-body">
        {tab === 'services' ? (
          <>
            <ConfigBanner errors={project.errors} />
            <section className="panel">
              <div className="panel-head">
                <h2>Services</h2>
                <span className="muted small">Click a row for its log · Ctrl/Shift+click to merge</span>
              </div>
              <ServicesTable
                services={instance.services}
                isWorktree={!!instance.worktree}
                selected={selected}
                pending={pending}
                onSelectionChange={setSources}
                onAction={(verb, id) => void serviceAction(verb, id)}
              />
            </section>
            {instance.tasks.length > 0 && (
              <section className="panel">
                <div className="panel-head">
                  <h2>Tasks</h2>
                </div>
                <TasksList
                  tasks={instance.tasks}
                  selected={selected}
                  pending={pending}
                  onSelectionChange={setSources}
                  onAction={(verb, id) => {
                    if (verb === 'run' && !selected.includes('task:' + id)) setSources(['task:' + id]);
                    void taskAction(verb, id);
                  }}
                />
              </section>
            )}
          </>
        ) : (
          <ConfigView project={project} instance={instance} />
        )}
      </div>
    </div>
  );
}

export function NoInstance() {
  return (
    <div className="project-view">
      <EmptyState icon="layers" title="No project selected">
        Pick a project in the sidebar, or add one with the + button.
      </EmptyState>
    </div>
  );
}
