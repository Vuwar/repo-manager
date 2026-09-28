// Helpers for the central (layer 3) overrides edited in the config view.
// Same shape as devservers.json: { services: { name: {...} }, tasks: { name: {...} } }.

export interface PrepareStep {
  if?: string;
  run: string;
}

export interface ServiceOverride {
  command?: string;
  args?: string[];
  cwd?: string;
  env?: Record<string, string>;
  port?: number;
  autoPort?: boolean;
  url?: string;
  health?: string;
  dependsOn?: string[];
  prepare?: PrepareStep[];
  autoRestart?: boolean;
  readyTimeoutSeconds?: number;
  disabled?: boolean;
}

export interface TaskOverride {
  command?: string;
  args?: string[];
  cwd?: string;
  env?: Record<string, string>;
}

export interface Overrides {
  services: Record<string, ServiceOverride>;
  tasks: Record<string, TaskOverride>;
}

export type Kind = 'service' | 'task';

/** Parses the overrides JSON. Returns null when the text is not a valid overrides object. */
export function parseOverrides(text: string): Overrides | null {
  if (!text.trim()) return { services: {}, tasks: {} };
  try {
    const v = JSON.parse(text) as unknown;
    if (v === null || typeof v !== 'object' || Array.isArray(v)) return null;
    const o = v as Record<string, unknown>;
    const obj = (x: unknown) => x !== null && typeof x === 'object' && !Array.isArray(x);
    if (o.services !== undefined && !obj(o.services)) return null;
    if (o.tasks !== undefined && !obj(o.tasks)) return null;
    return {
      ...(o as object),
      services: (o.services as Record<string, ServiceOverride>) ?? {},
      tasks: (o.tasks as Record<string, TaskOverride>) ?? {},
    };
  } catch {
    return null;
  }
}

export function stringifyOverrides(o: Overrides): string {
  return JSON.stringify(o, null, 2);
}

/** Drops empty strings, empty objects and undefined values so the stored JSON stays small. */
export function cleanEntry<T extends object>(entry: T): T {
  const out: Record<string, unknown> = {};
  for (const [k, v] of Object.entries(entry)) {
    if (v === undefined || v === null || v === '') continue;
    if (typeof v === 'number' && Number.isNaN(v)) continue;
    if (k === 'env' && typeof v === 'object' && Object.keys(v as object).length === 0) continue;
    out[k] = v;
  }
  return out as T;
}

/** Splits a command line like a shell would: spaces separate, double quotes group. */
export function splitCommandLine(line: string): string[] {
  const out: string[] = [];
  let cur = '';
  let quoted = false;
  let has = false;
  for (let i = 0; i < line.length; i++) {
    const c = line[i];
    if (c === '\\' && line[i + 1] === '"') {
      cur += '"';
      has = true;
      i++;
    } else if (c === '"') {
      quoted = !quoted;
      has = true;
    } else if (!quoted && /\s/.test(c)) {
      if (has) out.push(cur);
      cur = '';
      has = false;
    } else {
      cur += c;
      has = true;
    }
  }
  if (has) out.push(cur);
  return out;
}

function quote(a: string): string {
  return a.length === 0 || /[\s"]/.test(a) ? '"' + a.replace(/"/g, '\\"') + '"' : a;
}

export function joinCommandLine(command: string | undefined, args: string[] | undefined): string {
  if (!command) return '';
  return [command, ...(args ?? [])].map(quote).join(' ');
}

/** Parses "if !exists(web/node_modules): npm ci" lines into prepare steps. */
export function parsePrepare(text: string): PrepareStep[] {
  return text
    .split('\n')
    .map((l) => l.trim())
    .filter(Boolean)
    .map((l) => {
      const m = /^if\s+(.+?):\s*(.+)$/i.exec(l);
      return m ? { if: m[1].trim(), run: m[2].trim() } : { run: l };
    });
}

export function formatPrepare(steps: PrepareStep[] | undefined): string {
  return (steps ?? []).map((s) => (s.if ? `if ${s.if}: ${s.run}` : s.run)).join('\n');
}

/** Names allowed for services and tasks: they end up in ids like "project/name". */
export function validateName(name: string, taken: string[]): string | null {
  const n = name.trim();
  if (!n) return 'Give it a name';
  if (!/^[A-Za-z0-9][A-Za-z0-9._-]*$/.test(n)) return 'Use letters, digits, "-", "_" or "." only';
  if (taken.some((t) => t.toLowerCase() === n.toLowerCase())) return `"${n}" already exists`;
  return null;
}
