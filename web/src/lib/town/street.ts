import type { AgentSessionDto, AgentState, ProjectDto } from '../../api/types';
import { isActive } from '../format';

/** Sessions waiting on the user (a permission prompt or a question). */
export function needsYouCount(agents: Record<string, AgentSessionDto>): number {
  return Object.values(agents).filter((a) => a.state === 'waiting').length;
}

/** Window title: "(N) RepoManager" while N sessions need the user. */
export function windowTitle(n: number): string {
  return n > 0 ? `(${n}) RepoManager` : 'RepoManager';
}

/** A lot's place in the street order. "failed" follows "waiting", as in the prototype's priorities. */
export type LotGroup = AgentState | 'none';

export const GROUP_ORDER: LotGroup[] = ['waiting', 'failed', 'working', 'done', 'idle', 'none'];

/** One building on the street: a RepoManager project, or a temporary one for a folder that is not. */
export interface Lot {
  key: string;
  name: string;
  /** Not a loaded RepoManager project: shown as "not in RepoManager" and gone with its last session. */
  temporary: boolean;
  group: LotGroup;
  /** Earliest start (ms) of the workers in the lot's group; null without workers. */
  since: number | null;
  /** Most urgent first. Worktree sessions are here too, at their main project. */
  workers: AgentSessionDto[];
  /** A dev server of the project is up or starting: lit windows. */
  serversUp: boolean;
}

const rank = (g: LotGroup) => GROUP_ORDER.indexOf(g);
const time = (a: AgentSessionDto) => Date.parse(a.since);

/** "project:<name>" for a session the daemon placed in a project, else "folder:<cwd>" (full path, so same-named folders stay apart). */
export function lotKeyFor(agent: AgentSessionDto): string {
  if (agent.project) return 'project:' + agent.project.toLowerCase();
  return 'folder:' + agent.cwd.replace(/[\\/]+$/, '').toLowerCase();
}

/** Every project and every session's lot, sorted: by group, longest-waiting first, then by name. */
export function buildStreet(projects: ProjectDto[] | null, agents: Record<string, AgentSessionDto>): Lot[] {
  const lots = new Map<string, Lot>();
  for (const p of projects ?? []) {
    lots.set('project:' + p.name.toLowerCase(), {
      key: 'project:' + p.name.toLowerCase(),
      name: p.name,
      temporary: false,
      group: 'none',
      since: null,
      workers: [],
      serversUp: p.instances.some((i) => i.services.some((s) => isActive(s.state))),
    });
  }
  for (const a of Object.values(agents)) {
    const key = lotKeyFor(a);
    let lot = lots.get(key);
    if (!lot) {
      lot = { key, name: a.project ?? a.folder, temporary: true, group: 'none', since: null, workers: [], serversUp: false };
      lots.set(key, lot);
    }
    lot.workers.push(a);
  }
  for (const lot of lots.values()) {
    if (!lot.workers.length) continue;
    lot.workers.sort((x, y) => rank(x.state) - rank(y.state) || time(x) - time(y));
    lot.group = lot.workers[0].state;
    lot.since = Math.min(...lot.workers.filter((w) => w.state === lot.group).map(time));
  }
  return [...lots.values()].sort(
    (x, y) =>
      rank(x.group) - rank(y.group) ||
      (x.since ?? 0) - (y.since ?? 0) ||
      x.name.localeCompare(y.name, undefined, { sensitivity: 'base' }),
  );
}
