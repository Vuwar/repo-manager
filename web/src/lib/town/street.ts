import type { AgentSessionDto } from '../../api/types';

/** Sessions waiting on the user (a permission prompt or a question). */
export function needsYouCount(agents: Record<string, AgentSessionDto>): number {
  return Object.values(agents).filter((a) => a.state === 'waiting').length;
}

/** Window title: "(N) RepoManager" while N sessions need the user. */
export function windowTitle(n: number): string {
  return n > 0 ? `(${n}) RepoManager` : 'RepoManager';
}
