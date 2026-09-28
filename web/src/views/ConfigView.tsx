import { useCallback, useEffect, useMemo, useState } from 'react';
import { api } from '../api/client';
import type { EffectiveConfigDto, EffectiveServiceDto, InstanceDto, ProjectDto } from '../api/types';
import { Icon } from '../components/Icon';
import { Button, ConfirmDialog, Spinner } from '../components/ui';
import { removeProject, saveSettings } from '../state/actions';
import { useAppState } from '../state/store';

const ORIGINS: { key: string; label: string; layer: string }[] = [
  { key: 'launch.json', label: 'launch.json', layer: 'Layer 1 · .claude/launch.json' },
  { key: 'devservers.json', label: 'devservers.json', layer: 'Layer 2 · devservers.json in the repo' },
  { key: 'central', label: 'central', layer: 'Layer 3 · per-machine overrides (below)' },
  { key: 'default', label: 'default', layer: 'Built-in default' },
];

export function originClass(origin: string): string {
  const o = origin.toLowerCase();
  if (o.includes('+')) return 'origin-mixed';
  if (o.startsWith('launch')) return 'origin-launch';
  if (o.startsWith('devservers')) return 'origin-devservers';
  if (o === 'central') return 'origin-central';
  return 'origin-default';
}

export function OriginChip({ origin }: { origin: string }) {
  return (
    <span className={`origin ${originClass(origin)}`} title={`Value comes from ${origin}`}>
      {origin}
    </span>
  );
}

function prettyValue(v: string | undefined): string {
  if (v === undefined || v === null) return '';
  const t = v.trim();
  if ((t.startsWith('{') && t.endsWith('}')) || (t.startsWith('[') && t.endsWith(']'))) {
    try {
      const parsed = JSON.parse(t) as unknown;
      if (Array.isArray(parsed)) return parsed.length ? parsed.map((x) => (typeof x === 'string' ? x : JSON.stringify(x))).join('  ') : '';
      const entries = Object.entries(parsed as Record<string, unknown>);
      return entries.map(([k, x]) => `${k}=${typeof x === 'string' ? x : JSON.stringify(x)}`).join('\n');
    } catch {
      return v;
    }
  }
  return v;
}

function EffectiveCard({ item, kind }: { item: EffectiveServiceDto; kind: 'service' | 'task' }) {
  return (
    <div className="cfg-card">
      <div className="cfg-card-head">
        <span className="chip chip-muted">{kind}</span>
        <span className="cfg-name">{item.name}</span>
      </div>
      <dl className="cfg-fields">
        {item.fields.map((f) => {
          const pv = prettyValue(f.value);
          const empty = pv === '';
          return (
            <div key={f.field} className={`cfg-field${empty ? ' empty' : ''}`}>
              <dt>{f.field}</dt>
              <dd className="mono">{empty ? <span className="muted">—</span> : pv}</dd>
              <dd className="cfg-origin">{!empty && <OriginChip origin={f.origin} />}</dd>
            </div>
          );
        })}
      </dl>
    </div>
  );
}

function validateJson(text: string): string | null {
  if (!text.trim()) return null;
  try {
    const v = JSON.parse(text) as unknown;
    if (v === null || typeof v !== 'object' || Array.isArray(v)) return 'Overrides must be a JSON object like { "services": { … }, "tasks": { … } }';
    const o = v as Record<string, unknown>;
    for (const k of ['services', 'tasks'])
      if (o[k] !== undefined && (o[k] === null || typeof o[k] !== 'object' || Array.isArray(o[k]))) return `"${k}" must be an object keyed by name`;
    return null;
  } catch (e) {
    return (e as Error).message;
  }
}

function parseTags(s: string): string[] {
  return Array.from(new Set(s.split(',').map((t) => t.trim()).filter(Boolean)));
}

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

  useEffect(() => {
    const ctl = new AbortController();
    setLoadError(null);
    api
      .config(instance.key, ctl.signal)
      .then((c) => {
        setCfg(c);
        setJson(c.overridesJson ?? '{}');
      })
      .catch((e: Error) => {
        if (e.name !== 'AbortError') setLoadError(e.message);
      });
    return () => ctl.abort();
  }, [instance.key, reload]);

  useEffect(() => {
    setTags(project.tags.join(', '));
    setNotify(project.notifications);
  }, [project.name, project.tags, project.notifications]);

  const jsonError = useMemo(() => validateJson(json), [json]);
  const tagList = parseTags(tags);
  const dirtyJson = cfg !== null && json !== (cfg.overridesJson ?? '{}');
  const dirtyTags = tagList.join('\u0000') !== project.tags.join('\u0000');
  const dirtyNotify = notify !== project.notifications;
  const dirty = dirtyJson || dirtyTags || dirtyNotify;
  const saving = !!pending['settings:' + project.name];

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
      <section className="panel">
        <div className="panel-head">
          <h2>Effective config</h2>
          <div className="origin-legend">
            {ORIGINS.map((o) => (
              <span key={o.key} className={`origin ${originClass(o.key)}`} title={o.layer}>
                {o.label}
              </span>
            ))}
          </div>
          <Button size="sm" variant="ghost" icon="refresh" aria-label="Reload config" title="Reload" onClick={() => setReload((n) => n + 1)} />
        </div>
        {instance.worktree && (
          <p className="hint">
            Showing the <b>{instance.worktree}</b> worktree instance. Overrides below apply to every instance of <b>{project.name}</b>.
          </p>
        )}
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
            {cfg.services.length === 0 && cfg.tasks.length === 0 && <div className="table-empty">No services or tasks. Define them in the overrides below.</div>}
            <div className="cfg-grid">
              {cfg.services.map((s) => (
                <EffectiveCard key={'s:' + s.name} item={s} kind="service" />
              ))}
              {cfg.tasks.map((t) => (
                <EffectiveCard key={'t:' + t.name} item={t} kind="task" />
              ))}
            </div>
          </>
        )}
      </section>

      <section className="panel">
        <div className="panel-head">
          <h2>Project settings</h2>
          <span className="muted small">Stored centrally in registry.json on this machine</span>
        </div>
        <div className="settings-grid">
          <label className="field">
            <span className="field-label">Tags (groups)</span>
            <input className="input" value={tags} onChange={(e) => setTags(e.target.value)} placeholder="work, frontend" aria-label="Tags" />
            <span className="hint">Comma separated. Projects are grouped by tag in the sidebar; groups can be started together.</span>
          </label>
          <label className="field toggle-field">
            <span className="field-label">Notifications</span>
            <span className="toggle">
              <input type="checkbox" role="switch" checked={notify} onChange={(e) => setNotify(e.target.checked)} aria-label="Notifications" />
              <span className="toggle-track" />
              <span>{notify ? 'Toast on crash, failure and long unhealthy' : 'Off'}</span>
            </span>
          </label>
        </div>

        <div className="field">
          <div className="field-label-row">
            <span className="field-label">
              Central overrides <OriginChip origin="central" />
            </span>
            <span className="muted small">Same shape as devservers.json. Replaces the whole layer 3 block for this project.</span>
            <Button size="sm" variant="ghost" onClick={format} disabled={!!jsonError}>
              Format
            </Button>
          </div>
          <textarea
            className={`input mono json-editor${jsonError ? ' invalid' : ''}`}
            value={json}
            onChange={(e) => setJson(e.target.value)}
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
          {saveError && (
            <div className="inline-error" role="alert">
              {saveError}
            </div>
          )}
        </div>

        <div className="form-actions spread">
          <Button variant="danger" icon="trash" onClick={() => setConfirmRemove(true)} busy={!!pending['remove:' + project.name]}>
            Remove project
          </Button>
          <div className="form-actions">
            {dirty && (
              <Button
                variant="ghost"
                onClick={() => {
                  setJson(cfg?.overridesJson ?? '{}');
                  setTags(project.tags.join(', '));
                  setNotify(project.notifications);
                  setSaveError(null);
                }}
              >
                Discard
              </Button>
            )}
            <Button variant="primary" icon="check" disabled={!dirty || !!jsonError} busy={saving} onClick={() => void save()} title="Save (Ctrl+S in the editor)">
              Save
            </Button>
          </div>
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
