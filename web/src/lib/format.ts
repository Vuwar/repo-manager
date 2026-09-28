import type { ServiceState } from '../api/types';

/** Parses .NET ISO timestamps (7 fractional digits) reliably across engines. */
export function parseTime(iso: string | undefined | null): number {
  if (!iso) return NaN;
  const t = Date.parse(iso);
  if (!Number.isNaN(t)) return t;
  return Date.parse(iso.replace(/(\.\d{3})\d+/, '$1'));
}

export function formatDuration(ms: number): string {
  if (!Number.isFinite(ms) || ms < 0) return '';
  const s = Math.floor(ms / 1000);
  if (s < 60) return `${s}s`;
  const m = Math.floor(s / 60);
  if (m < 60) return `${m}m ${String(s % 60).padStart(2, '0')}s`;
  const h = Math.floor(m / 60);
  if (h < 24) return `${h}h ${String(m % 60).padStart(2, '0')}m`;
  const d = Math.floor(h / 24);
  return `${d}d ${h % 24}h`;
}

export function formatClock(iso: string): string {
  const t = parseTime(iso);
  if (Number.isNaN(t)) return '';
  const d = new Date(t);
  const p = (n: number, w = 2) => String(n).padStart(w, '0');
  return `${p(d.getHours())}:${p(d.getMinutes())}:${p(d.getSeconds())}.${p(d.getMilliseconds(), 3)}`;
}

export function formatAgo(iso: string | undefined, now: number): string {
  const t = parseTime(iso);
  if (Number.isNaN(t)) return '';
  const d = now - t;
  if (d < 45_000) return 'just now';
  return formatDuration(d).split(' ')[0] + ' ago';
}

export type StateTone = 'ok' | 'warn' | 'bad' | 'busy' | 'off';

export function stateTone(state: ServiceState): StateTone {
  switch (state) {
    case 'running':
      return 'ok';
    case 'unhealthy':
      return 'warn';
    case 'failed':
    case 'crashed':
      return 'bad';
    case 'starting':
    case 'preparing':
    case 'waitingDeps':
    case 'stopping':
      return 'busy';
    default:
      return 'off';
  }
}

const LABELS: Record<ServiceState, string> = {
  stopped: 'Stopped',
  preparing: 'Preparing',
  waitingDeps: 'Waiting deps',
  starting: 'Starting',
  running: 'Running',
  unhealthy: 'Unhealthy',
  stopping: 'Stopping',
  crashed: 'Crashed',
  failed: 'Failed',
};

export function stateLabel(state: ServiceState): string {
  return LABELS[state] ?? state;
}

/** Service is up or on its way up (stop makes sense). */
export function isActive(state: ServiceState): boolean {
  return state === 'running' || state === 'unhealthy' || state === 'starting' || state === 'preparing' || state === 'waitingDeps';
}

/** Instance key of a log source: "p@wt/svc" -> "p@wt"; "task:p/build" -> "p". */
export function instanceOfSource(source: string): string {
  const id = source.startsWith('task:') ? source.slice(5) : source;
  const i = id.lastIndexOf('/');
  return i === -1 ? id : id.slice(0, i);
}

export function shortName(source: string): string {
  const id = source.startsWith('task:') ? source.slice(5) : source;
  const i = id.lastIndexOf('/');
  return i === -1 ? id : id.slice(i + 1);
}

/** Stable colour slot (0-7) for a log source prefix. */
export function sourceHue(source: string): number {
  let h = 0;
  for (let i = 0; i < source.length; i++) h = (h * 31 + source.charCodeAt(i)) | 0;
  return Math.abs(h) % 8;
}
