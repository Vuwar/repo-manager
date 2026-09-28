import { useState } from 'react';
import { dismissToast } from '../state/actions';
import type { Toast } from '../state/reducer';
import { useAppState } from '../state/store';
import { AnsiText } from './AnsiText';
import { Button } from './ui';
import { Icon } from './Icon';

function ToastItem({ t }: { t: Toast }) {
  const [open, setOpen] = useState(true);
  const hasTail = !!t.logTail?.length;
  return (
    <div className={`toast toast-${t.kind}`} role={t.kind === 'error' ? 'alert' : 'status'}>
      <div className="toast-icon">
        <Icon name={t.kind === 'error' ? 'alert' : t.kind === 'success' ? 'check' : 'info'} size={14} />
      </div>
      <div className="toast-main">
        <div className="toast-title">{t.title}</div>
        {t.message && <div className="toast-msg">{t.message}</div>}
        {hasTail && (
          <>
            <button type="button" className="linklike toast-tail-toggle" onClick={() => setOpen(!open)}>
              <Icon name={open ? 'chevronDown' : 'chevron'} size={11} /> Last {t.logTail!.length} log lines
            </button>
            {open && (
              <pre className="toast-tail">
                {t.logTail!.map((l, i) => (
                  <div key={i}>
                    <AnsiText text={l} />
                  </div>
                ))}
              </pre>
            )}
          </>
        )}
        {t.actions?.length ? (
          <div className="toast-actions">
            {t.actions.map((a) => (
              <Button
                key={a.label}
                size="sm"
                onClick={() => {
                  dismissToast(t.id);
                  a.run();
                }}
              >
                {a.label}
              </Button>
            ))}
          </div>
        ) : null}
      </div>
      <Button icon="x" variant="ghost" size="sm" aria-label="Dismiss" onClick={() => dismissToast(t.id)} />
    </div>
  );
}

export function Toasts() {
  const toasts = useAppState((s) => s.toasts);
  if (!toasts.length) return null;
  return (
    <div className="toasts" aria-live="polite">
      {toasts.map((t) => (
        <ToastItem key={t.id} t={t} />
      ))}
    </div>
  );
}
