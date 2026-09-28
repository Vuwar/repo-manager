import { useEffect, useId, useMemo, useRef, useState } from 'react';
import type { FolderDto } from '../api/types';
import { filterFolders, initialExpanded, norm, parentOf, treeRows, type TreeRow } from '../lib/folderTree';
import { Icon } from './Icon';

/**
 * Text box for a folder relative to the repo root, with a dropdown of the repo's subfolders.
 * Shows a tree when opened; typing switches to a flat search. Typing still works for folders
 * the list does not show (not created yet, or deeper than the scan).
 */
export function FolderPicker({ value, placeholder, folders, onChange }: { value: string; placeholder?: string; folders: FolderDto[] | null; onChange: (v: string) => void }) {
  const [open, setOpen] = useState(false);
  const [searching, setSearching] = useState(false);
  const [expanded, setExpanded] = useState<Set<string>>(new Set());
  const [active, setActive] = useState(0);
  const listId = useId();
  const wrap = useRef<HTMLDivElement>(null);
  const listRef = useRef<HTMLUListElement>(null);

  const search = searching && value.trim() !== '';
  const rows = useMemo<TreeRow[]>(
    () => (!folders ? [] : search ? filterFolders(folders, value).map((f) => ({ folder: f, depth: 0, hasChildren: false, expanded: false })) : treeRows(folders, expanded)),
    [folders, search, value, expanded],
  );
  const absolute = /^([A-Za-z]:|\/|\\\\)/.test(value.trim());
  const exists = !value.trim() || absolute || !folders || folders.some((f) => norm(f.path) === norm(value));

  const show = () => {
    if (open || !folders) return setOpen(true);
    const exp = initialExpanded(folders, value);
    setExpanded(exp);
    setSearching(false);
    const i = treeRows(folders, exp).findIndex((r) => norm(r.folder.path) === norm(value || '.'));
    setActive(Math.max(i, 0));
    setOpen(true);
  };

  useEffect(() => {
    if (!open) return;
    const close = (e: MouseEvent) => {
      if (!wrap.current?.contains(e.target as Node)) setOpen(false);
    };
    document.addEventListener('mousedown', close);
    return () => document.removeEventListener('mousedown', close);
  }, [open]);

  useEffect(() => {
    listRef.current?.querySelector('li.active')?.scrollIntoView?.({ block: 'nearest' });
  }, [active, open]);

  const pick = (f: FolderDto) => {
    onChange(f.path === '.' ? '' : f.path);
    setOpen(false);
    setSearching(false);
  };

  const toggle = (path: string, to?: boolean) =>
    setExpanded((prev) => {
      const next = new Set(prev);
      if (to ?? !next.has(path)) next.add(path);
      else next.delete(path);
      return next;
    });

  return (
    <div className="folder-picker" ref={wrap}>
      <input
        className="input mono"
        value={value}
        placeholder={placeholder}
        aria-label="Folder"
        role="combobox"
        aria-expanded={open && rows.length > 0}
        aria-controls={listId}
        aria-autocomplete="list"
        autoComplete="off"
        onFocus={show}
        onClick={show}
        onChange={(e) => {
          onChange(e.target.value);
          setSearching(true);
          setActive(0);
          setOpen(true);
        }}
        onKeyDown={(e) => {
          const row = rows[active];
          if (e.key === 'ArrowDown') {
            e.preventDefault();
            if (!open) show();
            else setActive((a) => Math.min(a + 1, rows.length - 1));
          } else if (e.key === 'ArrowUp') {
            e.preventDefault();
            setActive((a) => Math.max(a - 1, 0));
          } else if (e.key === 'ArrowRight' && open && !search && row?.hasChildren) {
            e.preventDefault();
            if (!row.expanded) toggle(row.folder.path, true);
            else setActive(active + 1);
          } else if (e.key === 'ArrowLeft' && open && !search && row && row.folder.path !== '.') {
            e.preventDefault();
            if (row.expanded) toggle(row.folder.path, false);
            else {
              const p = rows.findIndex((r) => r.folder.path === parentOf(row.folder.path));
              if (p >= 0) setActive(p);
            }
          } else if (e.key === 'Enter' && open && row) {
            e.preventDefault();
            pick(row.folder);
          } else if (e.key === 'Escape' || e.key === 'Tab') {
            setOpen(false);
          }
        }}
      />
      {open && folders && rows.length > 0 && (
        <ul className="folder-list" id={listId} role={search ? 'listbox' : 'tree'} ref={listRef}>
          {rows.map((r, i) => {
            const name = r.folder.path === '.' ? '. (repo root)' : search ? r.folder.path : r.folder.path.slice(r.folder.path.lastIndexOf('/') + 1);
            return (
              <li
                key={r.folder.path}
                role={search ? 'option' : 'treeitem'}
                aria-selected={i === active}
                aria-expanded={r.hasChildren ? r.expanded : undefined}
                aria-level={search ? undefined : r.depth + 1}
                className={`${i === active ? 'active' : ''}${norm(r.folder.path) === norm(value) && value ? ' current' : ''}`}
                style={{ paddingLeft: 8 + r.depth * 16 }}
                onMouseEnter={() => setActive(i)}
                onMouseDown={(e) => {
                  e.preventDefault();
                  pick(r.folder);
                }}
              >
                {!search && (
                  <span
                    className={`folder-twist${r.hasChildren ? '' : ' leaf'}`}
                    aria-label={r.hasChildren ? (r.expanded ? 'Collapse' : 'Expand') : undefined}
                    onMouseDown={(e) => {
                      if (!r.hasChildren) return;
                      e.preventDefault();
                      e.stopPropagation();
                      toggle(r.folder.path);
                      setActive(i);
                    }}
                  >
                    {r.hasChildren && <Icon name="chevron" size={11} className={r.expanded ? 'open' : undefined} />}
                  </span>
                )}
                <Icon name="folder" size={13} className="folder-icon" />
                <span className="mono folder-name">{name}</span>
                {r.folder.markers.map((m) => (
                  <span key={m} className="chip chip-muted">
                    {m}
                  </span>
                ))}
              </li>
            );
          })}
        </ul>
      )}
      {!exists && <span className="hint text-warn">This folder does not exist in the repo yet.</span>}
    </div>
  );
}
