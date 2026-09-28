import { useEffect, useRef, type ButtonHTMLAttributes, type ReactNode } from 'react';
import type { ServiceState } from '../api/types';
import { stateLabel, stateTone } from '../lib/format';
import { Icon, type IconName } from './Icon';

export function StatusDot({ state, title }: { state: ServiceState; title?: string }) {
  const tone = stateTone(state);
  return <span className={`dot dot-${tone}`} title={title ?? stateLabel(state)} aria-label={stateLabel(state)} role="img" />;
}

export function StateBadge({ state }: { state: ServiceState }) {
  const tone = stateTone(state);
  return (
    <span className={`badge badge-${tone}`} data-state={state}>
      <span className={`dot dot-${tone}`} aria-hidden="true" />
      {stateLabel(state)}
    </span>
  );
}

export function Spinner({ size = 12 }: { size?: number }) {
  return <span className="spinner" style={{ width: size, height: size }} role="status" aria-label="running" />;
}

type BtnProps = ButtonHTMLAttributes<HTMLButtonElement> & {
  icon?: IconName;
  variant?: 'default' | 'primary' | 'ghost' | 'danger' | 'subtle';
  size?: 'sm' | 'md';
  busy?: boolean;
};

export function Button({ icon, variant = 'default', size = 'md', busy, children, className, disabled, ...rest }: BtnProps) {
  return (
    <button
      type="button"
      className={`btn btn-${variant} btn-${size}${children ? '' : ' btn-icon'}${className ? ' ' + className : ''}`}
      disabled={disabled || busy}
      aria-busy={busy || undefined}
      {...rest}
    >
      {busy ? <Spinner size={11} /> : icon ? <Icon name={icon} size={size === 'sm' ? 12 : 14} /> : null}
      {children}
    </button>
  );
}

export function Kbd({ children }: { children: ReactNode }) {
  return <kbd className="kbd">{children}</kbd>;
}

export function Modal({
  title,
  onClose,
  children,
  footer,
  width = 520,
}: {
  title: ReactNode;
  onClose: () => void;
  children: ReactNode;
  footer?: ReactNode;
  width?: number;
}) {
  const ref = useRef<HTMLDivElement>(null);
  useEffect(() => {
    const onKey = (e: KeyboardEvent) => {
      if (e.key === 'Escape') {
        e.stopPropagation();
        onClose();
      }
    };
    window.addEventListener('keydown', onKey, true);
    const prev = document.activeElement as HTMLElement | null;
    const first = ref.current?.querySelector<HTMLElement>('[data-autofocus], input, textarea, button');
    first?.focus();
    return () => {
      window.removeEventListener('keydown', onKey, true);
      prev?.focus?.();
    };
  }, [onClose]);
  return (
    <div className="modal-backdrop" onMouseDown={(e) => e.target === e.currentTarget && onClose()}>
      <div className="modal" role="dialog" aria-modal="true" aria-label={typeof title === 'string' ? title : undefined} style={{ width }} ref={ref}>
        <div className="modal-head">
          <div className="modal-title">{title}</div>
          <Button icon="x" variant="ghost" size="sm" onClick={onClose} aria-label="Close" />
        </div>
        <div className="modal-body">{children}</div>
        {footer && <div className="modal-foot">{footer}</div>}
      </div>
    </div>
  );
}

export function ConfirmDialog({
  title,
  message,
  confirmLabel = 'Confirm',
  danger,
  onConfirm,
  onCancel,
}: {
  title: string;
  message: ReactNode;
  confirmLabel?: string;
  danger?: boolean;
  onConfirm: () => void;
  onCancel: () => void;
}) {
  return (
    <Modal
      title={title}
      onClose={onCancel}
      width={420}
      footer={
        <>
          <Button variant="ghost" onClick={onCancel}>
            Cancel
          </Button>
          <Button variant={danger ? 'danger' : 'primary'} onClick={onConfirm} data-autofocus>
            {confirmLabel}
          </Button>
        </>
      }
    >
      <div className="confirm-msg">{message}</div>
    </Modal>
  );
}

export function EmptyState({ icon, title, children }: { icon?: IconName; title: string; children?: ReactNode }) {
  return (
    <div className="empty">
      {icon && (
        <div className="empty-icon">
          <Icon name={icon} size={20} />
        </div>
      )}
      <div className="empty-title">{title}</div>
      {children && <div className="empty-body">{children}</div>}
    </div>
  );
}
