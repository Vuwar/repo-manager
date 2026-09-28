import type { InstanceDto, ProjectDto, ServiceDto } from '../api/types';
import { filterInstances } from './groups';
import { isActive, stateTone, type StateTone } from './format';

export type UiMode = 'simple' | 'full';

const MODE_KEY = 'repomanager.mode';
const TABS_KEY = 'repomanager.simple.tabs';
const ACTIVE_KEY = 'repomanager.simple.active';

function read<T>(key: string, fallback: T): T {
  try {
    const raw = localStorage.getItem(key);
    return raw === null ? fallback : (JSON.parse(raw) as T);
  } catch {
    return fallback;
  }
}

function write(key: string, value: unknown): void {
  try {
    localStorage.setItem(key, JSON.stringify(value));
  } catch {
    /* ignore */
  }
}

export const readMode = (): UiMode => (read<string>(MODE_KEY, 'full') === 'simple' ? 'simple' : 'full');
export const saveMode = (m: UiMode) => write(MODE_KEY, m);
export const readTabs = (): string[] => {
  const v = read<unknown>(TABS_KEY, []);
  return Array.isArray(v) ? v.filter((x): x is string => typeof x === 'string') : [];
};
export const saveTabs = (tabs: string[]) => write(TABS_KEY, tabs);
export const readActiveTab = (): string | null => {
  const v = read<unknown>(ACTIVE_KEY, null);
  return typeof v === 'string' ? v : null;
};
export const saveActiveTab = (t: string | null) => write(ACTIVE_KEY, t);

/** Services shown in simple mode (disabled ones are hidden). */
export function visibleServices(inst: InstanceDto): ServiceDto[] {
  return inst.services.filter((s) => !s.disabled);
}

/** Names of projects with at least one service up or on its way up. */
export function activeProjectNames(projects: ProjectDto[]): string[] {
  return projects.filter((p) => p.instances.some((i) => i.services.some((s) => isActive(s.state)))).map((p) => p.name);
}

/** Tabs after projects became active: newly active ones are appended, order kept. */
export function withNewlyActive(tabs: string[], before: string[] | null, now: string[]): string[] {
  const prev = new Set(before ?? []);
  const have = new Set(tabs.map((t) => t.toLowerCase()));
  const add = now.filter((n) => !prev.has(n) && !have.has(n.toLowerCase()));
  return add.length ? [...tabs, ...add] : tabs;
}

/** Drops tabs whose project no longer exists (case-insensitive), fixing the case of the rest. */
export function pruneTabs(tabs: string[], projects: ProjectDto[]): string[] {
  const byLower = new Map(projects.map((p) => [p.name.toLowerCase(), p.name]));
  const next: string[] = [];
  for (const t of tabs) {
    const real = byLower.get(t.toLowerCase());
    if (real && !next.includes(real)) next.push(real);
  }
  return next.length === tabs.length && next.every((n, i) => n === tabs[i]) ? tabs : next;
}

/** One tone for a set of services: failed beats running beats busy beats stopped. */
export function summaryTone(services: ServiceDto[]): StateTone {
  const tones = new Set(services.map((s) => stateTone(s.state)));
  if (tones.has('bad')) return 'bad';
  if (tones.has('warn')) return 'warn';
  if (tones.has('ok')) return 'ok';
  if (tones.has('busy')) return 'busy';
  return 'off';
}

export function projectServices(p: ProjectDto): ServiceDto[] {
  return p.instances.flatMap(visibleServices);
}

export function runningCount(services: ServiceDto[]): number {
  return services.filter((s) => s.state === 'running' || s.state === 'unhealthy').length;
}

export interface FinderHit {
  project: ProjectDto;
  instance: InstanceDto;
  services: ServiceDto[];
}

/**
 * Finder results. Empty query: every project's main checkout.
 * Otherwise: matching instances; when only service names matched, just those services.
 */
export function finderHits(projects: ProjectDto[], query: string): FinderHit[] {
  if (!query.trim()) {
    return [...projects]
      .sort((a, b) => a.name.localeCompare(b.name))
      .filter((p) => p.instances.length)
      .map((p) => ({ project: p, instance: p.instances[0], services: visibleServices(p.instances[0]) }));
  }
  return filterInstances(projects, query)
    .map((h) => {
      const matched = h.services.filter((s) => !s.disabled);
      return { project: h.project, instance: h.instance, services: matched.length ? matched : visibleServices(h.instance) };
    })
    .filter((h) => h.services.length || !h.instance.worktree);
}

export type EnterAction = { kind: 'start'; service: ServiceDto } | { kind: 'open'; project: string } | null;

/** What Enter in the search box does: start the single matching stopped service, else open the first repo. */
export function enterAction(hits: FinderHit[], query: string): EnterAction {
  const q = query.trim().toLowerCase();
  if (!q || !hits.length) return null;
  const exact = hits.flatMap((h) => h.services).filter((s) => s.name.toLowerCase().includes(q));
  if (exact.length === 1 && !isActive(exact[0].state) && exact[0].state !== 'stopping') return { kind: 'start', service: exact[0] };
  return { kind: 'open', project: hits[0].project.name };
}
