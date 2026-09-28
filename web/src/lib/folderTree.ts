import type { FolderDto } from '../api/types';

// Folder list helpers for the folder picker: flat search and the collapsible tree.

const MAX_SHOWN = 60;

/** Normalised for matching: "/" separators, no leading "./", no trailing "/". */
export function norm(s: string): string {
  return s.trim().replace(/\\/g, '/').replace(/^\.\/+/, '').replace(/\/+$/, '').toLowerCase();
}

export function parentOf(path: string): string {
  const i = path.lastIndexOf('/');
  return i < 0 ? '' : path.slice(0, i);
}

export function ancestors(path: string): string[] {
  const out: string[] = [];
  for (let p = parentOf(path); p; p = parentOf(p)) out.push(p);
  return out;
}

/** Folders matching the typed text; project folders (package.json, csproj, ...) first. */
export function filterFolders(folders: FolderDto[], text: string): FolderDto[] {
  const q = norm(text);
  const hits = q ? folders.filter((f) => f.path.toLowerCase().includes(q)) : folders;
  return [...hits.filter((f) => f.markers.length > 0), ...hits.filter((f) => f.markers.length === 0)].slice(0, MAX_SHOWN);
}

export interface TreeRow {
  folder: FolderDto;
  depth: number;
  hasChildren: boolean;
  expanded: boolean;
}

/** Visible rows of the folder tree: the root first, then each expanded folder's children in order. */
export function treeRows(folders: FolderDto[], expanded: Set<string>): TreeRow[] {
  const children = new Map<string, FolderDto[]>();
  let root: FolderDto | undefined;
  for (const f of folders) {
    if (f.path === '.') {
      root = f;
      continue;
    }
    const p = parentOf(f.path);
    if (!children.has(p)) children.set(p, []);
    children.get(p)!.push(f);
  }
  const out: TreeRow[] = [];
  if (root) out.push({ folder: root, depth: 0, hasChildren: false, expanded: false });
  const walk = (parent: string, depth: number) => {
    for (const f of children.get(parent) ?? []) {
      const has = children.has(f.path);
      const open = has && expanded.has(f.path);
      out.push({ folder: f, depth, hasChildren: has, expanded: open });
      if (open) walk(f.path, depth + 1);
    }
  };
  walk('', 0);
  return out;
}

/** Starts with the way to every project folder and to the current value open, so they are visible at once. */
export function initialExpanded(folders: FolderDto[], value: string): Set<string> {
  const set = new Set<string>();
  for (const f of folders) if (f.markers.length > 0 && f.path !== '.') ancestors(f.path).forEach((a) => set.add(a));
  const cur = folders.find((f) => norm(f.path) === norm(value));
  if (cur) ancestors(cur.path).forEach((a) => set.add(a));
  return set;
}

