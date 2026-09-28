import { useCallback, useEffect, useMemo, useState, type ReactNode } from 'react';
import { api } from '../api/client';
import type { EffectiveConfigDto, EffectiveServiceDto, FolderDto, InstanceDto, ProjectDto } from '../api/types';
import { FolderPicker } from '../components/FolderPicker';
import { Icon } from '../components/Icon';
import { Button, ConfirmDialog, Spinner } from '../components/ui';
import {
  cleanEntry,
  formatPrepare,
  joinCommandLine,
  parseOverrides,
  parsePrepare,
  splitCommandLine,
  stringifyOverrides,
  validateName,
  type Kind,
  type Overrides,
  type ServiceOverride,
  type TaskOverride,
} from '../lib/overrides';
import { removeProject, saveSettings } from '../state/actions';
import { useAppState } from '../state/store';

// ---- Effective values from the daemon ----

type FieldMap = Record<string, { value?: string; origin: string }>;

function fieldMap(item: EffectiveServiceDto | undefined): FieldMap {
  const m: FieldMap = {};
  for (const f of item?.fields ?? []) m[f.field] = { value: f.value, origin: f.origin };
  return m;
}

function fromRepoFile(origin: string | undefined): boolean {
  const o = (origin ?? '').toLowerCase();
  return o.includes('launch') || o.includes('devservers');
}

function parseJsonValue<T>(v: string | undefined, fallback: T): T {
  if (!v) return fallback;
  try {
    return JSON.parse(v) as T;
  } catch {
    return fallback;
  }
}

/** What the repo files (layers 1 and 2) supply for one entry, without this machine's overrides. */
interface Inherited {
  fromRepo: boolean;
  command?: string;
  args: string[];
  cwd?: string;
  env: Record<string, string>;
  port?: string;
  autoPort: boolean;
  url?: string;
  health?: string;
  dependsOn: string[];
  autoRestart: boolean;
  readyTimeoutSeconds?: string;
  disabled: boolean;
}

function inheritedOf(item: EffectiveServiceDto | undefined, saved: ServiceOverride | undefined): Inherited {
  const m = fieldMap(item);
  const repo = (k: string) => (fromRepoFile(m[k]?.origin) ? m[k]?.value : undefined);
  const env = parseJsonValue<Record<string, string>>(fromRepoFile(m.env?.origin) ? m.env?.value : undefined, {});
  for (const k of Object.keys(saved?.env ?? {})) delete env[k];
  return {
    fromRepo: Object.values(m).some((f) => fromRepoFile(f.origin)),
    command: repo('command'),
    args: parseJsonValue<string[]>(repo('args'), []),
    cwd: repo('cwd'),
    env,
    port: repo('port'),
    autoPort: repo('autoPort') === 'true',
    url: repo('url'),
    health: repo('health'),
    dependsOn: parseJsonValue<string[]>(repo('dependsOn'), []),
    autoRestart: repo('autoRestart') === 'true',
    readyTimeoutSeconds: repo('readyTimeoutSeconds'),
    disabled: repo('disabled') === 'true',
  };
}

// ---- Small form pieces ----

function Field({ label, hint, wide, children }: { label: string; hint?: ReactNode; wide?: boolean; children: ReactNode }) {
  return (
    <label className={`field${wide ? ' wide' : ''}`}>
      <span className="field-label">{label}</span>
      {children}
      {hint && <span className="hint">{hint}</span>}
    </label>
  );
}

/** Trims, turns "\" into "/" and drops "./" and trailing slashes; empty or "." means the repo root. */
function normalizeFolder(s: string): string | undefined {
  const t = s.trim().replace(/\\/g, '/').replace(/^\.\/+/, '').replace(/\/+$/, '');
  return t === '' || t === '.' ? undefined : t;
}

function FolderHint({ cwd }: { cwd?: string }) {
  if (cwd && /^([A-Za-z]:|\/|\\\\)/.test(cwd.trim()))
    return <span className="text-warn">Absolute path: every worktree would run in this same folder. Prefer a path relative to the repo root.</span>;
  return (
    <>
      Where the command runs, relative to the repo root, e.g. <span className="mono">src/Api</span> or <span className="mono">web</span>. Empty = repo root.
    </>
  );
}

function Toggle({ checked, onChange, label }: { checked: boolean; onChange: (v: boolean) => void; label: string }) {
  return (
    <label className="toggle">
      <input type="checkbox" role="switch" checked={checked} onChange={(e) => onChange(e.target.checked)} aria-label={label} />
      <span className="toggle-track" />
      <span>{label}</span>
    </label>
  );
}

/** Command line as one text box. Keeps its own text so spaces survive while typing. */
function CommandInput({ command, args, inherited, onChange }: { command?: string; args?: string[]; inherited: Inherited; onChange: (command?: string, args?: string[]) => void }) {
  const set = command !== undefined || args !== undefined;
  const [text, setText] = useState(set ? joinCommandLine(command ?? inherited.command, args ?? inherited.args) : '');
  return (
    <input
      className="input mono"
      value={text}
      placeholder={joinCommandLine(inherited.command, inherited.args) || 'npm run dev -- --port ${port:web}'}
      aria-label="Command"
      onChange={(e) => {
        setText(e.target.value);
        const parts = splitCommandLine(e.target.value);
        if (parts.length === 0) onChange(undefined, undefined);
        else onChange(parts[0], parts.slice(1));
      }}
    />
  );
}

function EnvEditor({ env, inherited, onChange }: { env?: Record<string, string>; inherited: Record<string, string>; onChange: (env: Record<string, string>) => void }) {
  const [rows, setRows] = useState<[string, string][]>(() => Object.entries(env ?? {}));
  const update = (next: [string, string][]) => {
    setRows(next);
    const out: Record<string, string> = {};
    for (const [k, v] of next) if (k.trim()) out[k.trim()] = v;
    onChange(out);
  };
  const inheritedKeys = Object.keys(inherited);
  return (
    <div className="env-editor">
      {inheritedKeys.length > 0 && (
        <div className="env-inherited">
          {inheritedKeys.map((k) => (
            <div key={k} className="env-row inherited" title="From the repo files. Add the same name below to change it.">
              <span className="mono">{k}</span>
              <span className="mono">{inherited[k]}</span>
            </div>
          ))}
        </div>
      )}
      {rows.map(([k, v], i) => (
        <div key={i} className="env-row">
          <input className="input mono" value={k} placeholder="NAME" aria-label="Variable name" onChange={(e) => update(rows.map((r, j) => (j === i ? [e.target.value, r[1]] : r)))} />
          <input className="input mono" value={v} placeholder="value" aria-label="Variable value" onChange={(e) => update(rows.map((r, j) => (j === i ? [r[0], e.target.value] : r)))} />
          <Button size="sm" variant="ghost" icon="x" aria-label="Remove variable" title="Remove" onClick={() => update(rows.filter((_, j) => j !== i))} />
        </div>
      ))}
      <Button size="sm" variant="subtle" icon="plus" onClick={() => setRows([...rows, ['', '']])}>
        Add variable
      </Button>
    </div>
  );
}

function PrepareInput({ steps, inherited, onChange }: { steps?: ServiceOverride['prepare']; inherited?: string; onChange: (s: ServiceOverride['prepare']) => void }) {
  const [text, setText] = useState(formatPrepare(steps));
  return (
    <textarea
      className="input mono"
      rows={3}
      value={text}
      placeholder={inherited ? parseJsonValue<string[]>(inherited, []).join('\n') : 'if !exists(node_modules): npm ci'}
      aria-label="Before start"
      onChange={(e) => {
        setText(e.target.value);
        const s = parsePrepare(e.target.value);
        onChange(s.length ? s : undefined);
      }}
    />
  );
}

// ---- Editors ----

function ServiceForm({ entry, inherited, otherServices, rawPrepare, folders, onChange }: { entry: ServiceOverride; folders: FolderDto[] | null; inherited: Inherited; otherServices: string[]; rawPrepare?: string; onChange: (e: ServiceOverride) => void }) {
  const set = <K extends keyof ServiceOverride>(k: K, v: ServiceOverride[K]) => onChange(cleanEntry({ ...entry, [k]: v }));
  // Booleans are only stored when they differ from what the repo files say.
  const setBool = (k: 'autoPort' | 'autoRestart' | 'disabled', v: boolean) => set(k, v === inherited[k] ? undefined : v);
  const deps = entry.dependsOn ?? inherited.dependsOn;
  const num = (s: string) => (s.trim() === '' ? undefined : Number(s));

  return (
    <div className="entry-form">
      <Field label="Command" wide hint={<>What you would type in a terminal. Use <span className="mono">{'${port:NAME}'}</span> to insert a service's port.</>}>
        <CommandInput command={entry.command} args={entry.args} inherited={inherited} onChange={(c, a) => onChange(cleanEntry({ ...entry, command: c, args: a }))} />
      </Field>
      <Field label="Folder" hint={<FolderHint cwd={entry.cwd} />}>
        <FolderPicker value={entry.cwd ?? ''} placeholder={inherited.cwd ?? '. (repo root)'} folders={folders} onChange={(v) => set('cwd', v)} />
      </Field>
      <Field label="Port">
        <input className="input mono" type="number" min={1} max={65535} value={entry.port ?? ''} placeholder={inherited.port ?? 'none'} onChange={(e) => set('port', num(e.target.value))} aria-label="Port" />
      </Field>
      <Field label="Open in browser" hint="Address shown as a link for this service.">
        <input className="input mono" value={entry.url ?? ''} placeholder={inherited.url ?? 'http://localhost:${port:web}'} onChange={(e) => set('url', e.target.value)} aria-label="URL" />
      </Field>
      <Field label="Health check" hint="Optional. The service counts as ready when this returns 2xx.">
        <input className="input mono" value={entry.health ?? ''} placeholder={inherited.health ?? 'http://localhost:${port:api}/health'} onChange={(e) => set('health', e.target.value)} aria-label="Health URL" />
      </Field>
      {otherServices.length > 0 && (
        <Field label="Start after" wide hint="These services are started first.">
          <span className="pill-group">
            {otherServices.map((s) => (
              <label key={s} className={`pill${deps.includes(s) ? ' on' : ''}`}>
                <input type="checkbox" checked={deps.includes(s)} onChange={(e) => set('dependsOn', e.target.checked ? [...deps, s] : deps.filter((d) => d !== s))} />
                {s}
              </label>
            ))}
          </span>
        </Field>
      )}
      <div className="field wide toggles">
        <Toggle label="Restart automatically when it crashes" checked={entry.autoRestart ?? inherited.autoRestart} onChange={(v) => setBool('autoRestart', v)} />
        <Toggle label="Use another free port when this one is busy" checked={entry.autoPort ?? inherited.autoPort} onChange={(v) => setBool('autoPort', v)} />
      </div>
      <Field label="Environment variables" wide>
        <EnvEditor env={entry.env} inherited={inherited.env} onChange={(env) => set('env', env)} />
      </Field>
      <details className="field wide more">
        <summary>More options</summary>
        <div className="entry-form">
          <Field label="Before start" wide hint={<>One command per line. Prefix with <span className="mono">if !exists(path):</span> to run only when a file is missing.</>}>
            <PrepareInput steps={entry.prepare} inherited={rawPrepare} onChange={(p) => set('prepare', p)} />
          </Field>
          <Field label="Ready timeout (seconds)">
            <input className="input mono" type="number" min={1} value={entry.readyTimeoutSeconds ?? ''} placeholder={inherited.readyTimeoutSeconds ?? '120'} onChange={(e) => set('readyTimeoutSeconds', num(e.target.value))} aria-label="Ready timeout" />
          </Field>
          <div className="field">
            <span className="field-label">Status</span>
            <Toggle label="Disabled (hidden from start all)" checked={entry.disabled ?? inherited.disabled} onChange={(v) => setBool('disabled', v)} />
          </div>
        </div>
      </details>
    </div>
  );
}

function TaskForm({ entry, inherited, folders, onChange }: { entry: TaskOverride; folders: FolderDto[] | null; inherited: Inherited; onChange: (e: TaskOverride) => void }) {
  const set = <K extends keyof TaskOverride>(k: K, v: TaskOverride[K]) => onChange(cleanEntry({ ...entry, [k]: v }));
  return (
    <div className="entry-form">
      <Field label="Command" wide hint="What you would type in a terminal, e.g. dotnet test or publish.bat.">
        <CommandInput command={entry.command} args={entry.args} inherited={inherited} onChange={(c, a) => onChange(cleanEntry({ ...entry, command: c, args: a }))} />
      </Field>
      <Field label="Folder" hint={<FolderHint cwd={entry.cwd} />}>
        <FolderPicker value={entry.cwd ?? ''} placeholder={inherited.cwd ?? '. (repo root)'} folders={folders} onChange={(v) => set('cwd', v)} />
      </Field>
      <Field label="Environment variables" wide>
        <EnvEditor env={entry.env} inherited={inherited.env} onChange={(env) => set('env', env)} />
      </Field>
    </div>
  );
}

function AddForm({ kind, taken, folders, onAdd, onCancel }: { kind: Kind; folders: FolderDto[] | null; taken: string[]; onAdd: (name: string, command: string, cwd?: string, port?: number) => void; onCancel: () => void }) {
  const [name, setName] = useState('');
  const [command, setCommand] = useState('');
  const [cwd, setCwd] = useState('');
  const [port, setPort] = useState('');
  const [touched, setTouched] = useState(false);
  const nameError = validateName(name, taken);
  const commandError = command.trim() ? null : 'Enter the command to run';
  const submit = () => {
    setTouched(true);
    if (nameError || commandError) return;
    onAdd(name.trim(), command, normalizeFolder(cwd), port.trim() ? Number(port) : undefined);
  };
  return (
    <form
      className="entry add-entry"
      onSubmit={(e) => {
        e.preventDefault();
        submit();
      }}
    >
      <div className="entry-head">
        <span className="chip chip-accent">new {kind}</span>
      </div>
      <div className="entry-form">
        <Field label="Name" hint={touched && nameError ? <span className="text-bad">{nameError}</span> : kind === 'service' ? 'Short id, e.g. api or web.' : 'Short id, e.g. test or publish.'}>
          <input className="input" autoFocus value={name} onChange={(e) => setName(e.target.value)} placeholder={kind === 'service' ? 'web' : 'test'} aria-label="Name" />
        </Field>
        {kind === 'service' && (
          <Field label="Port" hint="Optional. The port the server listens on.">
            <input className="input mono" type="number" min={1} max={65535} value={port} onChange={(e) => setPort(e.target.value)} placeholder="5173" aria-label="Port" />
          </Field>
        )}
        <Field label="Folder" wide hint={<FolderHint cwd={cwd} />}>
          <FolderPicker value={cwd} placeholder=". (repo root)" folders={folders} onChange={setCwd} />
        </Field>
        <Field label="Command" wide hint={touched && commandError ? <span className="text-bad">{commandError}</span> : 'What you would type in a terminal in that folder.'}>
          <input className="input mono" value={command} onChange={(e) => setCommand(e.target.value)} placeholder={kind === 'service' ? 'npm run dev' : 'dotnet test'} aria-label="Command" />
        </Field>
      </div>
      <div className="form-actions entry-actions">
        <Button variant="ghost" onClick={onCancel}>
          Cancel
        </Button>
        <Button variant="primary" icon="plus" type="submit">
          Add {kind}
        </Button>
      </div>
    </form>
  );
}

// ---- One row in the list ----

interface Row {
  kind: Kind;
  name: string;
  item?: EffectiveServiceDto;
  entry?: ServiceOverride | TaskOverride;
  inherited: Inherited;
}

function summary(row: Row): { command: string; port?: string; url?: string } {
  const e = row.entry as ServiceOverride | undefined;
  const i = row.inherited;
  const hasCmd = e?.command !== undefined || e?.args !== undefined;
  return {
    command: hasCmd ? joinCommandLine(e?.command ?? i.command, e?.args ?? i.args) : joinCommandLine(i.command, i.args),
    port: e?.port !== undefined ? String(e.port) : i.port,
    url: e?.url ?? i.url,
  };
}

function EntryCard({ row, open, onToggle, onRemove, children }: { row: Row; open: boolean; onToggle: () => void; onRemove: () => void; children: ReactNode }) {
  const s = summary(row);
  const edited = !!row.entry && Object.keys(row.entry).length > 0;
  const disabled = (row.entry as ServiceOverride | undefined)?.disabled ?? row.inherited.disabled;
  return (
    <div className={`entry${open ? ' open' : ''}${disabled ? ' disabled' : ''}`}>
      <div className="entry-head" onClick={onToggle} role="button" tabIndex={0} aria-expanded={open} onKeyDown={(e) => (e.key === 'Enter' || e.key === ' ') && (e.preventDefault(), onToggle())}>
        <Icon name="chevron" size={12} className={`entry-chevron${open ? ' open' : ''}`} />
        <span className="entry-name">{row.name}</span>
        <span className="chip chip-muted">{row.kind}</span>
        {!row.inherited.fromRepo ? (
          <span className="chip chip-accent" title="Defined on this machine only">added here</span>
        ) : edited ? (
          <span className="chip chip-accent" title="Repo settings with changes made on this machine">edited here</span>
        ) : (
          <span className="chip chip-muted" title="Comes from launch.json or devservers.json in the repo">from repo</span>
        )}
        {disabled && <span className="chip chip-muted">disabled</span>}
        <span className="entry-summary mono">{s.command || <span className="text-bad">no command set</span>}</span>
        {s.port && <span className="chip chip-muted mono">:{s.port}</span>}
        <span className="entry-buttons" onClick={(e) => e.stopPropagation()}>
          {!row.inherited.fromRepo ? (
            <Button size="sm" variant="ghost" icon="trash" aria-label={`Delete ${row.name}`} title="Delete" onClick={onRemove} />
          ) : (
            edited && (
              <Button size="sm" variant="ghost" icon="eraser" onClick={onRemove} title="Drop the changes made on this machine">
                Reset
              </Button>
            )
          )}
        </span>
      </div>
      {open && <div className="entry-body">{children}</div>}
    </div>
  );
}

// ---- JSON (advanced) ----

function validateJson(text: string): string | null {
  if (!text.trim()) return null;
  try {
    JSON.parse(text);
  } catch (e) {
    return (e as Error).message;
  }
  return parseOverrides(text) ? null : 'Overrides must be a JSON object like { "services": { … }, "tasks": { … } }';
}

function normalizeJson(text: string): string {
  try {
    return JSON.stringify(JSON.parse(text.trim() || '{}'));
  } catch {
    return text;
  }
}

function parseTags(s: string): string[] {
  return Array.from(new Set(s.split(',').map((t) => t.trim()).filter(Boolean)));
}

// ---- View ----

export function ConfigView({ project, instance }: { project: ProjectDto; instance: InstanceDto }) {
  const pending = useAppState((s) => s.pending);
  const [cfg, setCfg] = useState<EffectiveConfigDto | null>(null);
  const [loadError, setLoadError] = useState<string | null>(null);
  const [reload, setReload] = useState(0);
  const [json, setJson] = useState('');
  const [tags, setTags] = useState(project.tags.join(', '));
  const [notify, setNotify] = useState(project.notifications);
  const [saveError, setSaveError] = useState<string | null>(null);
  const [confirmRemove, setConfirmRemove] = useState(false);
  const [openKey, setOpenKey] = useState<string | null>(null);
  const [adding, setAdding] = useState<Kind | null>(null);
  // Bumped when the overrides change outside the forms, so form inputs re-read them.
  const [epoch, setEpoch] = useState(0);
  const [folders, setFolders] = useState<FolderDto[] | null>(null);

  useEffect(() => {
    const ctl = new AbortController();
    setLoadError(null);
    api
      .config(instance.key, ctl.signal)
      .then((c) => {
        setCfg(c);
        setJson(c.overridesJson ?? '{}');
        setEpoch((n) => n + 1);
      })
      .catch((e: Error) => {
        if (e.name !== 'AbortError') setLoadError(e.message);
      });
    return () => ctl.abort();
  }, [instance.key, reload]);

  // Folder list for the pickers. An older daemon has no /api/folders: the pickers then work as plain text boxes.
  useEffect(() => {
    const ctl = new AbortController();
    api
      .folders(instance.key, ctl.signal)
      .then(setFolders)
      .catch(() => setFolders(null));
    return () => ctl.abort();
  }, [instance.key, reload]);

  useEffect(() => {
    setTags(project.tags.join(', '));
    setNotify(project.notifications);
  }, [project.name, project.tags, project.notifications]);

  const jsonError = useMemo(() => validateJson(json), [json]);
  const draft = useMemo(() => parseOverrides(json), [json]);
  const saved = useMemo(() => parseOverrides(cfg?.overridesJson ?? '{}') ?? { services: {}, tasks: {} }, [cfg]);
  const tagList = parseTags(tags);
  const dirtyJson = cfg !== null && normalizeJson(json) !== normalizeJson(cfg.overridesJson ?? '{}');
  const dirtyTags = tagList.join('\u0000') !== project.tags.join('\u0000');
  const dirtyNotify = notify !== project.notifications;
  const dirty = dirtyJson || dirtyTags || dirtyNotify;
  const saving = !!pending['settings:' + project.name];

  const rows = useMemo<Row[]>(() => {
    if (!cfg) return [];
    const out: Row[] = [];
    const build = (kind: Kind, items: EffectiveServiceDto[], draftMap: Record<string, object>, savedMap: Record<string, object>) => {
      const names = new Map<string, string>();
      for (const i of items) names.set(i.name.toLowerCase(), i.name);
      for (const n of Object.keys(draftMap)) if (!names.has(n.toLowerCase())) names.set(n.toLowerCase(), n);
      for (const name of names.values()) {
        const item = items.find((i) => i.name.toLowerCase() === name.toLowerCase());
        const key = Object.keys(draftMap).find((k) => k.toLowerCase() === name.toLowerCase());
        const savedKey = Object.keys(savedMap).find((k) => k.toLowerCase() === name.toLowerCase());
        out.push({ kind, name, item, entry: key ? draftMap[key] : undefined, inherited: inheritedOf(item, savedKey ? (savedMap[savedKey] as ServiceOverride) : undefined) });
      }
    };
    build('service', cfg.services, draft?.services ?? {}, saved.services);
    build('task', cfg.tasks, draft?.tasks ?? {}, saved.tasks);
    return out;
  }, [cfg, draft, saved]);

  const serviceNames = rows.filter((r) => r.kind === 'service').map((r) => r.name);

  const writeDraft = (fn: (o: Overrides) => void, external = false) => {
    if (!draft) return;
    const next: Overrides = structuredClone(draft);
    fn(next);
    setJson(stringifyOverrides(next));
    if (external) setEpoch((n) => n + 1);
  };
  const mapOf = (o: Overrides, kind: Kind) => (kind === 'service' ? o.services : o.tasks) as Record<string, object>;
  const keyIn = (m: Record<string, object>, name: string) => Object.keys(m).find((k) => k.toLowerCase() === name.toLowerCase()) ?? name;

  const setEntry = (row: Row, entry: object) => writeDraft((o) => void (mapOf(o, row.kind)[keyIn(mapOf(o, row.kind), row.name)] = entry));
  const removeEntry = (row: Row) =>
    writeDraft((o) => {
      delete mapOf(o, row.kind)[keyIn(mapOf(o, row.kind), row.name)];
      // A service defined only here is gone after this, so nothing may wait for it any more.
      if (row.kind === 'service' && !row.inherited.fromRepo)
        for (const s of Object.values(o.services)) if (s.dependsOn) s.dependsOn = s.dependsOn.filter((d) => d.toLowerCase() !== row.name.toLowerCase());
    }, true);

  const discard = () => {
    setJson(cfg?.overridesJson ?? '{}');
    setTags(project.tags.join(', '));
    setNotify(project.notifications);
    setSaveError(null);
    setAdding(null);
    setEpoch((n) => n + 1);
  };

  const save = useCallback(async () => {
    setSaveError(null);
    const r = await saveSettings({
      project: project.name,
      tags: dirtyTags ? tagList : undefined,
      notifications: dirtyNotify ? notify : undefined,
      overridesJson: dirtyJson ? (json.trim() ? json : '{}') : undefined,
    });
    if (!r.ok) setSaveError(r.error ?? 'save failed');
    else setReload((n) => n + 1);
  }, [project.name, dirtyTags, tagList, dirtyNotify, notify, dirtyJson, json]);

  const format = () => {
    try {
      setJson(JSON.stringify(JSON.parse(json), null, 2));
    } catch {
      /* leave invalid JSON alone */
    }
  };

  return (
    <div className="config-view">
      {(dirty || saveError) && (
        <div className="cfg-savebar" role="status">
          <Icon name="info" size={14} />
          <span>{saveError ? <span className="text-bad">{saveError}</span> : 'You have unsaved changes.'}</span>
          <div className="form-actions">
            <Button variant="ghost" onClick={discard}>
              Discard
            </Button>
            <Button variant="primary" icon="check" disabled={!dirty || !!jsonError} busy={saving} onClick={() => void save()} title="Save (Ctrl+S)">
              Save
            </Button>
          </div>
        </div>
      )}

      <section
        className="panel"
        onKeyDown={(e) => {
          if (e.key === 's' && (e.ctrlKey || e.metaKey)) {
            e.preventDefault();
            if (dirty && !jsonError) void save();
          }
        }}
      >
        <div className="panel-head">
          <h2>Services &amp; tasks</h2>
          <div className="form-actions">
            <Button size="sm" icon="plus" onClick={() => setAdding('service')} disabled={!draft}>
              Add service
            </Button>
            <Button size="sm" icon="plus" onClick={() => setAdding('task')} disabled={!draft}>
              Add task
            </Button>
            <Button size="sm" variant="ghost" icon="refresh" aria-label="Reload config" title="Reload" onClick={() => setReload((n) => n + 1)} />
          </div>
        </div>
        <p className="hint">
          Services run in the background (dev servers). Tasks run once (build, test). Anything you add or change here is saved on this machine only and wins over the repo's
          <span className="mono"> launch.json</span> and <span className="mono">devservers.json</span>.
          {instance.worktree && (
            <>
              {' '}
              It applies to every checkout of <b>{project.name}</b>, including the <b>{instance.worktree}</b> worktree shown here.
            </>
          )}
        </p>
        {loadError ? (
          <div className="inline-error">{loadError}</div>
        ) : !cfg ? (
          <div className="side-loading">
            <Spinner /> Loading…
          </div>
        ) : (
          <>
            {cfg.errors.length > 0 && (
              <div className="banner banner-error">
                <Icon name="alert" size={14} />
                <ul>
                  {cfg.errors.map((e, i) => (
                    <li key={i} className="mono">
                      {e}
                    </li>
                  ))}
                </ul>
              </div>
            )}
            {!draft && <div className="inline-error">The JSON under Advanced is invalid. Fix it there to use this editor again.</div>}
            <div className="entry-list">
              {adding && (
                <AddForm
                  folders={folders}
                  kind={adding}
                  taken={rows.filter((r) => r.kind === adding).map((r) => r.name)}
                  onCancel={() => setAdding(null)}
                  onAdd={(name, command, cwd, port) => {
                    const parts = splitCommandLine(command);
                    const entry = cleanEntry({ command: parts[0], args: parts.length > 1 ? parts.slice(1) : undefined, cwd, ...(adding === 'service' ? { port } : {}) });
                    writeDraft((o) => void (mapOf(o, adding)[name] = entry), true);
                    setAdding(null);
                    setOpenKey(`${adding}:${name.toLowerCase()}`);
                  }}
                />
              )}
              {rows.length === 0 && !adding && (
                <div className="table-empty">
                  Nothing to run yet. Click <b>Add service</b> for a dev server or <b>Add task</b> for a one-off command.
                </div>
              )}
              {rows.map((row) => {
                const key = `${row.kind}:${row.name.toLowerCase()}`;
                const formKey = `${key}:${epoch}`;
                return (
                  <EntryCard key={key} row={row} open={openKey === key} onToggle={() => setOpenKey(openKey === key ? null : key)} onRemove={() => removeEntry(row)}>
                    {!draft ? null : row.kind === 'service' ? (
                      <ServiceForm
                        folders={folders}
                        key={formKey}
                        entry={(row.entry as ServiceOverride) ?? {}}
                        inherited={row.inherited}
                        rawPrepare={fromRepoFile(fieldMap(row.item).prepare?.origin) ? fieldMap(row.item).prepare?.value : undefined}
                        otherServices={serviceNames.filter((n) => n !== row.name)}
                        onChange={(e) => setEntry(row, e)}
                      />
                    ) : (
                      <TaskForm key={formKey} folders={folders} entry={(row.entry as TaskOverride) ?? {}} inherited={row.inherited} onChange={(e) => setEntry(row, e)} />
                    )}
                  </EntryCard>
                );
              })}
            </div>
          </>
        )}
      </section>

      <section className="panel">
        <div className="panel-head">
          <h2>Project settings</h2>
        </div>
        <div className="settings-grid">
          <label className="field">
            <span className="field-label">Groups</span>
            <input className="input" value={tags} onChange={(e) => setTags(e.target.value)} placeholder="work, frontend" aria-label="Tags" />
            <span className="hint">Comma separated. The sidebar groups projects by these names, and you can start a whole group at once.</span>
          </label>
          <label className="field toggle-field">
            <span className="field-label">Notifications</span>
            <span className="toggle">
              <input type="checkbox" role="switch" checked={notify} onChange={(e) => setNotify(e.target.checked)} aria-label="Notifications" />
              <span className="toggle-track" />
              <span>{notify ? 'Notify me when a service crashes, fails or stays unhealthy' : 'Off'}</span>
            </span>
          </label>
        </div>

        <details className="field advanced">
          <summary>Advanced: edit as JSON</summary>
          <div className="field-label-row">
            <span className="muted small">
              Same format as <span className="mono">devservers.json</span>. Stored in registry.json on this machine.
            </span>
            <Button size="sm" variant="ghost" onClick={format} disabled={!!jsonError}>
              Format
            </Button>
          </div>
          <textarea
            className={`input mono json-editor${jsonError ? ' invalid' : ''}`}
            value={json}
            onChange={(e) => {
              setJson(e.target.value);
              setEpoch((n) => n + 1);
            }}
            spellCheck={false}
            rows={Math.min(24, Math.max(8, json.split('\n').length + 1))}
            aria-label="Central overrides JSON"
            aria-invalid={!!jsonError}
            onKeyDown={(e) => {
              if (e.key === 's' && (e.ctrlKey || e.metaKey)) {
                e.preventDefault();
                if (dirty && !jsonError) void save();
              }
              if (e.key === 'Tab' && !e.shiftKey) {
                e.preventDefault();
                const el = e.currentTarget;
                const { selectionStart: a, selectionEnd: b } = el;
                const next = json.slice(0, a) + '  ' + json.slice(b);
                setJson(next);
                requestAnimationFrame(() => el.setSelectionRange(a + 2, a + 2));
              }
            }}
          />
          {jsonError && (
            <div className="inline-error" role="alert">
              Invalid JSON: {jsonError}
            </div>
          )}
        </details>

        <div className="form-actions spread">
          <Button variant="danger" icon="trash" onClick={() => setConfirmRemove(true)} busy={!!pending['remove:' + project.name]}>
            Remove project
          </Button>
        </div>
      </section>

      {confirmRemove && (
        <ConfirmDialog
          title={`Remove ${project.name}?`}
          danger
          confirmLabel="Remove"
          message={
            <>
              Its running services are stopped and it is removed from RepoManager. Files in <span className="mono">{project.root}</span> are not touched.
            </>
          }
          onCancel={() => setConfirmRemove(false)}
          onConfirm={() => {
            setConfirmRemove(false);
            void removeProject(project.name);
          }}
        />
      )}
    </div>
  );
}
