import { Fragment, useRef, type MouseEvent } from 'react';
import type { ServiceDto } from '../api/types';
import { formatDuration, isActive, parseTime } from '../lib/format';
import { modeFromEvent, nextSelection, type SelectMode } from '../lib/selection';
import { useNow } from '../lib/useNow';
import { Icon } from './Icon';
import { Button, StateBadge } from './ui';

export type ServiceVerb = 'start' | 'stop' | 'restart';

export interface ServicesTableProps {
  services: ServiceDto[];
  isWorktree: boolean;
  /** Currently selected log sources (may include tasks). */
  selected: string[];
  /** Pending action labels by service id. */
  pending: Record<string, string>;
  onSelectionChange: (sources: string[]) => void;
  onAction: (verb: ServiceVerb, id: string) => void;
}

function Uptime({ startedAt, active }: { startedAt?: string; active: boolean }) {
  const now = useNow();
  if (!active || !startedAt) return <span className="muted">—</span>;
  const t = parseTime(startedAt);
  return <span title={new Date(t).toLocaleString()}>{formatDuration(now - t)}</span>;
}

function PortCell({ s }: { s: ServiceDto }) {
  if (!s.port && !s.url) return <span className="muted">—</span>;
  const href = s.url ?? (s.port ? `http://localhost:${s.port}` : undefined);
  const up = s.state === 'running' || s.state === 'unhealthy';
  return (
    <a
      className={`port-link${up ? '' : ' port-link-off'}`}
      href={href}
      target="_blank"
      rel="noreferrer"
      onClick={(e) => e.stopPropagation()}
      title={href}
    >
      <span className="mono">{s.port ?? 'url'}</span>
      <Icon name="external" size={11} />
    </a>
  );
}

function Health({ s }: { s: ServiceDto }) {
  if (!s.hasHealth) return <span className="muted" title="No health URL configured">—</span>;
  const cls = s.state === 'running' ? 'health-ok' : s.state === 'unhealthy' ? 'health-warn' : 'health-off';
  const title = s.state === 'running' ? 'Health check passing' : s.state === 'unhealthy' ? 'Health check failing' : 'Health check configured';
  return (
    <span className={`health ${cls}`} title={title} aria-label={title}>
      <Icon name="pulse" size={14} />
    </span>
  );
}

export function ServicesTable({ services, isWorktree, selected, pending, onSelectionChange, onAction }: ServicesTableProps) {
  const anchor = useRef<string | null>(null);
  const order = services.map((s) => s.id);

  const select = (id: string, mode: SelectMode) => {
    onSelectionChange(nextSelection(order, selected, id, mode, anchor.current));
    if (mode !== 'range') anchor.current = id;
  };

  const onRowClick = (e: MouseEvent, id: string) => {
    if ((e.target as HTMLElement).closest('button, a, input')) return;
    if (e.shiftKey) window.getSelection()?.removeAllRanges();
    select(id, modeFromEvent(e));
  };

  if (!services.length)
    return <div className="table-empty">No services configured. Add them in devservers.json, .claude/launch.json or the Config tab.</div>;

  return (
    <table className="grid services-table" aria-label="Services">
      <thead>
        <tr>
          <th className="col-check" aria-label="Show logs" />
          <th>Service</th>
          <th>State</th>
          <th>Port</th>
          <th className="num">Uptime</th>
          <th className="num" title="Restart count">
            <Icon name="restart" size={12} />
          </th>
          <th className="center">Health</th>
          <th className="col-actions" aria-label="Actions" />
        </tr>
      </thead>
      <tbody>
        {services.map((s) => {
          const isSel = selected.includes(s.id);
          const busy = pending[s.id];
          const active = isActive(s.state);
          const notReady = isWorktree && !s.worktreeReady;
          const showError = (s.state === 'failed' || s.state === 'crashed') && !!s.lastError;
          return (
            <Fragment key={s.id}>
              <tr
                className={`svc-row${isSel ? ' selected' : ''}${s.disabled ? ' disabled' : ''}${showError ? ' has-error' : ''}`}
                onClick={(e) => onRowClick(e, s.id)}
                data-testid={`svc-${s.name}`}
                aria-selected={isSel}
              >
                <td className="col-check">
                  <input
                    type="checkbox"
                    checked={isSel}
                    aria-label={`Show logs of ${s.name}`}
                    onChange={() => select(s.id, 'toggle')}
                  />
                </td>
                <td className="svc-name-cell">
                  <div className="svc-name">
                    <span className="name">{s.name}</span>
                    {s.autoRestart && (
                      <span className="chip chip-muted" title="Auto restart on crash">
                        <Icon name="loop" size={11} />
                        auto
                      </span>
                    )}
                    {s.disabled && <span className="chip chip-muted">disabled</span>}
                    {notReady && (
                      <span
                        className="chip chip-warn"
                        title="Fixed port and no ${port:…} reference in args/env. Starting here is refused unless forced. Read the port from an env variable (launchSettings.json / vite.config.ts) and use ${port:<service>}."
                      >
                        <Icon name="alert" size={11} />
                        not worktree-ready
                      </span>
                    )}
                  </div>
                  {s.command && (
                    <div className="svc-cmd mono" title={s.command}>
                      {s.command}
                    </div>
                  )}
                </td>
                <td>
                  <StateBadge state={s.state} />
                </td>
                <td>
                  <PortCell s={s} />
                </td>
                <td className="num mono">
                  <Uptime startedAt={s.startedAt} active={active} />
                </td>
                <td className={`num mono${s.restartCount > 0 ? ' warn-text' : ' muted'}`}>{s.restartCount}</td>
                <td className="center">
                  <Health s={s} />
                </td>
                <td className="col-actions">
                  <div className="row-actions">
                    {active || s.state === 'stopping' ? (
                      <Button
                        size="sm"
                        variant="subtle"
                        icon="stop"
                        aria-label={`Stop ${s.name}`}
                        title="Stop"
                        busy={busy === 'Stopping'}
                        disabled={!!busy || s.state === 'stopping'}
                        onClick={() => onAction('stop', s.id)}
                      />
                    ) : (
                      <Button
                        size="sm"
                        variant="subtle"
                        icon="play"
                        className="btn-go"
                        aria-label={`Start ${s.name}`}
                        title={s.disabled ? 'Disabled in config' : 'Start'}
                        busy={busy === 'Starting'}
                        disabled={!!busy || s.disabled}
                        onClick={() => onAction('start', s.id)}
                      />
                    )}
                    <Button
                      size="sm"
                      variant="subtle"
                      icon="restart"
                      aria-label={`Restart ${s.name}`}
                      title="Restart"
                      busy={busy === 'Restarting'}
                      disabled={!!busy || s.disabled}
                      onClick={() => onAction('restart', s.id)}
                    />
                  </div>
                </td>
              </tr>
              {showError && (
                <tr className={`svc-error-row${isSel ? ' selected' : ''}`} onClick={(e) => onRowClick(e, s.id)}>
                  <td />
                  <td colSpan={7}>
                    <div className="svc-error" role="note">
                      <Icon name="alert" size={12} />
                      <span>{s.lastError}</span>
                      {s.lastExitCode !== undefined && <span className="mono muted">exit {s.lastExitCode}</span>}
                    </div>
                  </td>
                </tr>
              )}
            </Fragment>
          );
        })}
      </tbody>
    </table>
  );
}
