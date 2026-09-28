import { useMemo, useState, type FormEvent } from 'react';
import { api } from '../api/client';
import type { ScanResultDto } from '../api/types';
import { addProject, toast } from '../state/actions';
import { useAppState } from '../state/store';
import { Button, Modal, Spinner } from './ui';

/** Parent folder shared by the registered projects: a good default for "Scan folder". */
function guessScanDir(roots: string[]): string {
  if (!roots.length) return '';
  const split = (p: string) => p.replace(/[\\/]+$/, '').split(/[\\/]/);
  const parents = roots.map((r) => split(r).slice(0, -1));
  let common = parents[0];
  for (const p of parents.slice(1)) {
    let i = 0;
    while (i < common.length && i < p.length && common[i].toLowerCase() === p[i].toLowerCase()) i++;
    common = common.slice(0, i);
  }
  return common.join('\\');
}

export function AddProjectDialog({ onClose }: { onClose: () => void }) {
  const projects = useAppState((s) => s.projects);
  const [tab, setTab] = useState<'path' | 'scan'>('path');
  const [path, setPath] = useState('');
  const [adding, setAdding] = useState(false);
  const [scanDir, setScanDir] = useState(() => guessScanDir((projects ?? []).map((p) => p.root)));
  const [scanning, setScanning] = useState(false);
  const [scanError, setScanError] = useState<string | null>(null);
  const [results, setResults] = useState<ScanResultDto[] | null>(null);
  const [picked, setPicked] = useState<Set<string>>(new Set());

  const addable = useMemo(() => (results ?? []).filter((r) => !r.registered), [results]);

  const submitPath = async (e: FormEvent) => {
    e.preventDefault();
    const p = path.trim().replace(/^"|"$/g, '');
    if (!p) return;
    setAdding(true);
    const r = await addProject(p);
    setAdding(false);
    if (r.ok) onClose();
  };

  const scan = async (e?: FormEvent) => {
    e?.preventDefault();
    const d = scanDir.trim().replace(/^"|"$/g, '');
    if (!d) return;
    setScanning(true);
    setScanError(null);
    try {
      const r = (await api.scan(d)) ?? [];
      setResults(r);
      setPicked(new Set(r.filter((x) => !x.registered).map((x) => x.path)));
    } catch (err) {
      setScanError((err as Error).message);
      setResults(null);
    } finally {
      setScanning(false);
    }
  };

  const addPicked = async () => {
    setAdding(true);
    let ok = 0;
    for (const p of picked) {
      const r = await api.addProject(p);
      if (r.ok) ok++;
      else toast({ kind: 'error', title: `Add ${p}`, message: r.error, logTail: r.logTail });
    }
    setAdding(false);
    if (ok) toast({ kind: 'success', title: `Added ${ok} project${ok === 1 ? '' : 's'}` });
    if (ok === picked.size) onClose();
    else void scan();
  };

  const togglePick = (p: string) => {
    const n = new Set(picked);
    if (n.has(p)) n.delete(p);
    else n.add(p);
    setPicked(n);
  };

  return (
    <Modal
      title="Add project"
      onClose={onClose}
      width={620}
      footer={
        tab === 'scan' && results ? (
          <>
            <span className="muted small foot-note">
              {picked.size} of {addable.length} selected
            </span>
            <Button variant="ghost" onClick={onClose}>
              Cancel
            </Button>
            <Button variant="primary" icon="plus" busy={adding} disabled={!picked.size} onClick={() => void addPicked()}>
              Add selected
            </Button>
          </>
        ) : undefined
      }
    >
      <div className="seg" role="tablist">
        <button type="button" role="tab" aria-selected={tab === 'path'} className={tab === 'path' ? 'on' : ''} onClick={() => setTab('path')}>
          Folder path
        </button>
        <button type="button" role="tab" aria-selected={tab === 'scan'} className={tab === 'scan' ? 'on' : ''} onClick={() => setTab('scan')}>
          Scan folder
        </button>
      </div>

      {tab === 'path' ? (
        <form onSubmit={submitPath} className="form-stack">
          <label className="field">
            <span className="field-label">Repository folder</span>
            <input className="input mono" value={path} onChange={(e) => setPath(e.target.value)} placeholder="C:\Users\me\source\repos\my-app" autoFocus spellCheck={false} />
          </label>
          <p className="hint">
            Services come from <code>.claude/launch.json</code> and <code>devservers.json</code> in the repo. Repos without either can be configured later in the
            Config tab.
          </p>
          <div className="form-actions">
            <Button variant="ghost" onClick={onClose}>
              Cancel
            </Button>
            <Button type="submit" variant="primary" icon="plus" busy={adding} disabled={!path.trim()}>
              Add project
            </Button>
          </div>
        </form>
      ) : (
        <div className="form-stack">
          <form onSubmit={scan} className="inline-form">
            <input
              className="input mono"
              value={scanDir}
              onChange={(e) => setScanDir(e.target.value)}
              placeholder="C:\Users\me\source\repos"
              aria-label="Folder to scan"
              spellCheck={false}
              autoFocus
            />
            <Button type="submit" variant="primary" icon="search" busy={scanning} disabled={!scanDir.trim()}>
              Scan
            </Button>
          </form>
          {scanError && <div className="inline-error">{scanError}</div>}
          {scanning && !results && (
            <div className="side-loading">
              <Spinner /> Scanning…
            </div>
          )}
          {results && results.length === 0 && <div className="hint">No repositories with launch.json, devservers.json, package.json or *.csproj found.</div>}
          {results && results.length > 0 && (
            <div className="scan-list">
              <label className="scan-row scan-all">
                <input
                  type="checkbox"
                  checked={addable.length > 0 && picked.size === addable.length}
                  ref={(el) => {
                    if (el) el.indeterminate = picked.size > 0 && picked.size < addable.length;
                  }}
                  disabled={!addable.length}
                  onChange={(e) => setPicked(e.target.checked ? new Set(addable.map((a) => a.path)) : new Set())}
                />
                <span className="muted small">Select all</span>
              </label>
              {results.map((r) => (
                <label key={r.path} className={`scan-row${r.registered ? ' registered' : ''}`}>
                  <input type="checkbox" checked={r.registered || picked.has(r.path)} disabled={r.registered} onChange={() => togglePick(r.path)} />
                  <span className="scan-name">{r.name}</span>
                  <span className="scan-path mono" title={r.path}>
                    {r.path}
                  </span>
                  <span className="scan-markers">
                    {r.registered && <span className="chip chip-ok">added</span>}
                    {r.markers.map((m) => (
                      <span key={m} className="chip chip-muted mono">
                        {m}
                      </span>
                    ))}
                  </span>
                </label>
              ))}
            </div>
          )}
        </div>
      )}
    </Modal>
  );
}
