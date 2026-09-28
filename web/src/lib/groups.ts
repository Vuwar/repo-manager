import type { InstanceDto, ProjectDto, ServiceDto } from '../api/types';

export interface ProjectGroup {
  /** null = untagged. */
  tag: string | null;
  projects: ProjectDto[];
}

/** Projects grouped by tag (a project with several tags shows in each), alphabetical, untagged last. */
export function groupProjects(projects: ProjectDto[]): ProjectGroup[] {
  const byTag = new Map<string, ProjectDto[]>();
  const untagged: ProjectDto[] = [];
  for (const p of projects) {
    if (!p.tags.length) untagged.push(p);
    for (const t of p.tags) {
      const key = t.trim();
      if (!key) continue;
      const list = byTag.get(key) ?? [];
      list.push(p);
      byTag.set(key, list);
    }
  }
  const byName = (a: ProjectDto, b: ProjectDto) => a.name.localeCompare(b.name);
  const groups: ProjectGroup[] = [...byTag.entries()]
    .sort(([a], [b]) => a.localeCompare(b))
    .map(([tag, ps]) => ({ tag, projects: ps.sort(byName) }));
  if (untagged.length) groups.push({ tag: null, projects: untagged.sort(byName) });
  return groups;
}

export interface FilterHit {
  project: ProjectDto;
  instance: InstanceDto;
  /** Services whose names matched (empty when the project/instance itself matched). */
  services: ServiceDto[];
}

/** Quick-filter: matches project name, worktree / instance key, branch or service name. */
export function filterInstances(projects: ProjectDto[], query: string): FilterHit[] {
  const q = query.trim().toLowerCase();
  if (!q) return [];
  const hits: FilterHit[] = [];
  for (const p of [...projects].sort((a, b) => a.name.localeCompare(b.name))) {
    for (const inst of p.instances) {
      const own =
        p.name.toLowerCase().includes(q) ||
        inst.key.toLowerCase().includes(q) ||
        (inst.git?.branch?.toLowerCase().includes(q) ?? false) ||
        p.tags.some((t) => t.toLowerCase().includes(q));
      const services = inst.services.filter((s) => s.name.toLowerCase().includes(q));
      if (own || services.length) hits.push({ project: p, instance: inst, services });
    }
  }
  return hits;
}

export function groupSummary(projects: ProjectDto[]): { running: number; total: number; bad: number } {
  let running = 0;
  let total = 0;
  let bad = 0;
  for (const p of projects)
    for (const i of p.instances)
      for (const s of i.services) {
        if (s.disabled) continue;
        total++;
        if (s.state === 'running' || s.state === 'unhealthy') running++;
        if (s.state === 'failed' || s.state === 'crashed') bad++;
      }
  return { running, total, bad };
}
