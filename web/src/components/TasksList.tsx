import { useRef, type MouseEvent } from 'react';
import type { TaskDto } from '../api/types';
import { formatAgo } from '../lib/format';
import { modeFromEvent, nextSelection, type SelectMode } from '../lib/selection';
import { useNow } from '../lib/useNow';
import { Button, Spinner } from './ui';

export interface TasksListProps {
  tasks: TaskDto[];
  selected: string[];
  pending: Record<string, string>;
  onSelectionChange: (sources: string[]) => void;
  onAction: (verb: 'run' | 'stop', taskId: string) => void;
}

function ExitCode({ t }: { t: TaskDto }) {
  const now = useNow();
  if (t.running) return <Spinner size={11} />;
  if (t.lastExitCode === undefined) return <span className="muted">never run</span>;
  return (
    <span className="task-exit">
      <span className={`exit ${t.lastExitCode === 0 ? 'exit-ok' : 'exit-bad'}`}>exit {t.lastExitCode}</span>
      {t.lastRunAt && <span className="muted">{formatAgo(t.lastRunAt, now)}</span>}
    </span>
  );
}

export function TasksList({ tasks, selected, pending, onSelectionChange, onAction }: TasksListProps) {
  const anchor = useRef<string | null>(null);
  const order = tasks.map((t) => 'task:' + t.id);
  const select = (src: string, mode: SelectMode) => {
    onSelectionChange(nextSelection(order, selected, src, mode, anchor.current));
    if (mode !== 'range') anchor.current = src;
  };
  const onClick = (e: MouseEvent, src: string) => {
    if ((e.target as HTMLElement).closest('button, a, input')) return;
    select(src, modeFromEvent(e));
  };

  return (
    <ul className="tasks" aria-label="Tasks">
      {tasks.map((t) => {
        const src = 'task:' + t.id;
        const isSel = selected.includes(src);
        const busy = pending[src];
        return (
          <li key={t.id} className={`task-row${isSel ? ' selected' : ''}${t.running ? ' running' : ''}`} onClick={(e) => onClick(e, src)}>
            <input type="checkbox" checked={isSel} aria-label={`Show logs of task ${t.name}`} onChange={() => select(src, 'toggle')} />
            <span className="task-name">{t.name}</span>
            <span className="task-cmd mono" title={t.command}>
              {t.command}
            </span>
            <ExitCode t={t} />
            {t.running ? (
              <Button size="sm" variant="subtle" icon="stop" busy={busy === 'Stopping'} disabled={!!busy} onClick={() => onAction('stop', t.id)}>
                Stop
              </Button>
            ) : (
              <Button size="sm" variant="subtle" icon="play" className="btn-go" busy={busy === 'Running'} disabled={!!busy} onClick={() => onAction('run', t.id)}>
                Run
              </Button>
            )}
          </li>
        );
      })}
    </ul>
  );
}
