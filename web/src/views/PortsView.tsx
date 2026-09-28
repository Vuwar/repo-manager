import { useCallback, useEffect, useMemo, useState } from 'react';
import { api } from '../api/client';
import type { PortDto, ServiceState } from '../api/types';
import { Icon } from '../components/Icon';
import { Button, ConfirmDialog, Spinner, StatusDot } from '../components/ui';
import { instanceOfSource } from '../lib/format';
import { killPid } from '../state/actions';
import { findInstance } from '../state/reducer';
import { dispatch, useAppState } from '../state/store';

const MODE_KEY = 'rm.ports.mode';

type Mode = 'dev' | 'all';

function loadMode(): Mode {
  try {
    return sessionStorage.getItem(MODE_KEY) === 'all' ? 'all' : 'dev';
  } catch {
    return 'dev';
  }
}

export function PortsView() {
  const projects = useAppState((s) => s.projects);
  const pending = useAppState((s) => s.pending);
  const [mode, setModeState] = useState<Mode>(loadMode);
  const [ports, setPorts] = useState<PortDto[] | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [loading, setLoading] = useState(false);
  const [query, setQuery] = useState('');
  const [kill, setKill] = useState<PortDto | null>(null);

  const setMode = (m: Mode) => {
    setModeState(m);
    try {
      sessionStorage.setItem(MODE_KEY, m);
    } catch {
      /* private mode */
    }
  };

  const load = useCallback(async () => {
    setLoading(true);
    try {
      setPorts((await api.ports(true, mode === 'all')) ?? []);
      setError(null);
    } catch (e) {
      setError((e as Error).message);
    } finally {
      setLoading(false);
    }
  }, [mode]);

  useEffect(() => {
    void load();
  }, [load]);

  const serviceById = useMemo(() => {
    const m = new Map<string, { state: ServiceState; instance: string }>();
    for (const p of projects ?? []) for (const i of p.instances) for (const s of i.services) m.set(s.id.toLowerCase(), { state: s.state, instance: i.key });
    return m;
  }, [projects]);

  const q = query.trim().toLowerCase();
  const rows = useMemo(
    () =>
      (ports ?? []).filter((p) => {
        if (!q) return true;
        return [String(p.port), String(p.pid), p.processName, p.commandLine, p.managedBy, p.address].some((x) => x?.toLowerCase().includes(q));
      }),
    [ports, q],
  );
  const conflicts = useMemo(() => [...new Set((ports ?? []).filter((p) => p.conflict).map((p) => p.port))], [ports]);
  const hiddenNames = useMemo(() => [...new Set((ports ?? []).filter((p) => p.hidden && p.processName).map((p) => p.processName!))], [ports]);

  const goTo = (serviceId: string) => {
    const key = serviceById.get(serviceId.toLowerCase())?.instance ?? instanceOfSource(serviceId);
    const found = findInstance(projects, key);
    if (!found) return;
    dispatch({ type: 'selectInstance', key: found.instance.key });
    dispatch({ type: 'setTab', tab: 'services' });
    dispatch({ type: 'setLogSources', sources: [serviceId] });
  };

  const setHidden = async (name: string, hidden: boolean) => {
    const r = await api.hidePortProcess(name, hidden);
    if (!r.ok) setError(r.error ?? 'could not update the hidden list');
    await load();
  };

  return (
    <div className="ports-view">
      <header className="pv-head">
        <div className="pv-title-row">
          <div className="pv-title">
            <h1>Ports</h1>
            <span className="muted">{mode === 'dev' ? 'Managed services and your own processes' : 'Every listening TCP port on this machine'}</span>
          </div>
          <div className="pv-actions">
            <div className="seg seg-inline" role="tablist" aria-label="Which ports">
              <button type="button" role="tab" aria-selected={mode === 'dev'} className={mode === 'dev' ? 'on' : ''} onClick={() => setMode('dev')}>
                Dev ports
              </button>
              <button type="button" role="tab" aria-selected={mode === 'all'} className={mode === 'all' ? 'on' : ''} onClick={() => setMode('all')}>
                All ports
              </button>
            </div>
            <label className="side-filter inline">
              <Icon name="search" size={12} />
              <input value={query} onChange={(e) => setQuery(e.target.value)} placeholder="Filter port, pid, process, command…" aria-label="Filter ports" />
            </label>
            <Button size="sm" icon="refresh" busy={loading} onClick={() => void load()}>
              Refresh
            </Button>
          </div>
        </div>
      </header>
      <div className="pv-body">
        {error && <div className="inline-error">{error}</div>}
        {conflicts.length > 0 && (
          <div className="banner banner-error port-conflict-banner" role="alert">
            <b>Port {conflicts.join(', ')}:</b> several processes are listening on it (for example one on 0.0.0.0 and another on [::1]). Which one
            &nbsp;<span className="mono">localhost:{conflicts[0]}</span> reaches depends on IPv4/IPv6 — stop one of them or move it to another port.
          </div>
        )}
        {!ports ? (
          !error && (
            <div className="side-loading">
              <Spinner /> Reading port table…
            </div>
          )
        ) : (
          <section className="panel">
            <table className="grid ports-table" aria-label="Listening ports">
              <thead>
                <tr>
                  <th className="num">Port</th>
                  <th>Address</th>
                  <th className="num">PID</th>
                  <th>Process</th>
                  <th>Command line</th>
                  <th>Managed by</th>
                  <th className="col-actions" />
                </tr>
              </thead>
              <tbody>
                {rows.map((p) => {
                  const svc = p.managedBy ? serviceById.get(p.managedBy.toLowerCase()) : undefined;
                  const cls = [p.managedBy ? 'managed' : '', p.conflict ? 'conflict' : '', p.system || p.hidden ? 'dim' : ''].join(' ').trim();
                  return (
                    <tr key={`${p.port}:${p.pid}`} className={cls}>
                      <td className="num mono strong">
                        <a href={`http://localhost:${p.port}`} target="_blank" rel="noreferrer" className="port-link">
                          {p.port}
                        </a>
                        {p.conflict && (
                          <span className="chip chip-bad conflict-chip" title="More than one process listens on this port">
                            conflict
                          </span>
                        )}
                      </td>
                      <td className="mono muted">{p.address}</td>
                      <td className="num mono">{p.pid}</td>
                      <td title={p.processPath}>
                        {p.processName ?? <span className="muted">?</span>}
                        {p.system && <span className="chip chip-muted">system</span>}
                        {p.hidden && <span className="chip chip-muted">hidden</span>}
                      </td>
                      <td className="mono cmdline" title={p.commandLine}>
                        {p.commandLine ?? <span className="muted">—</span>}
                      </td>
                      <td>
                        {p.managedBy ? (
                          <button type="button" className="linklike managed-link" onClick={() => goTo(p.managedBy!)}>
                            {svc && <StatusDot state={svc.state} />}
                            <span className="mono">{p.managedBy}</span>
                          </button>
                        ) : (
                          <span className="muted">—</span>
                        )}
                      </td>
                      <td className="col-actions">
                        <div className="row-actions">
                          {!p.managedBy && p.mine && !p.system && p.processName && (
                            <Button
                              size="sm"
                              variant="ghost"
                              title={p.hidden ? `Show ${p.processName} in Dev ports again` : `Hide every ${p.processName} port from Dev ports`}
                              onClick={() => void setHidden(p.processName!, !p.hidden)}
                            >
                              {p.hidden ? 'Unhide' : 'Hide'}
                            </Button>
                          )}
                          {p.canKill && (
                            <Button size="sm" variant="danger" busy={!!pending['kill:' + p.pid]} onClick={() => setKill(p)}>
                              Kill
                            </Button>
                          )}
                        </div>
                      </td>
                    </tr>
                  );
                })}
                {rows.length === 0 && (
                  <tr>
                    <td colSpan={7} className="table-empty">
                      {q ? 'No ports match.' : mode === 'dev' ? 'No dev ports in use. Switch to "All ports" to see everything.' : 'No listening ports.'}
                    </td>
                  </tr>
                )}
              </tbody>
            </table>
          </section>
        )}
        {mode === 'all' && hiddenNames.length > 0 && (
          <p className="muted pv-hidden-note">Hidden from Dev ports: {hiddenNames.join(', ')}</p>
        )}
      </div>
      {kill && (
        <ConfirmDialog
          title={`Kill PID ${kill.pid}?`}
          danger
          confirmLabel="Kill process tree"
          message={
            <>
              <p>
                <b>{kill.processName ?? 'Unknown process'}</b> is listening on port <b className="mono">{kill.port}</b>. Killing it ends the process and its children.
              </p>
              {kill.commandLine && <pre className="confirm-cmd mono">{kill.commandLine}</pre>}
            </>
          }
          onCancel={() => setKill(null)}
          onConfirm={() => {
            const pid = kill.pid;
            setKill(null);
            void killPid(pid).then((r) => r.ok && void load());
          }}
        />
      )}
    </div>
  );
}
