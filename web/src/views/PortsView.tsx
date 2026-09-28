import { useCallback, useEffect, useMemo, useState } from 'react';
import { api } from '../api/client';
import type { PortDto, ServiceState } from '../api/types';
import { Icon } from '../components/Icon';
import { Button, ConfirmDialog, Spinner, StatusDot } from '../components/ui';
import { instanceOfSource } from '../lib/format';
import { killPid } from '../state/actions';
import { findInstance } from '../state/reducer';
import { dispatch, useAppState } from '../state/store';

export function PortsView() {
  const projects = useAppState((s) => s.projects);
  const pending = useAppState((s) => s.pending);
  const [ports, setPorts] = useState<PortDto[] | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [loading, setLoading] = useState(false);
  const [query, setQuery] = useState('');
  const [onlyExternal, setOnlyExternal] = useState(false);
  const [kill, setKill] = useState<PortDto | null>(null);

  const load = useCallback(async () => {
    setLoading(true);
    try {
      setPorts((await api.ports(true)) ?? []);
      setError(null);
    } catch (e) {
      setError((e as Error).message);
    } finally {
      setLoading(false);
    }
  }, []);

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
        if (onlyExternal && p.managedBy) return false;
        if (!q) return true;
        return [String(p.port), String(p.pid), p.processName, p.commandLine, p.managedBy, p.address].some((x) => x?.toLowerCase().includes(q));
      }),
    [ports, q, onlyExternal],
  );

  const goTo = (serviceId: string) => {
    const key = serviceById.get(serviceId.toLowerCase())?.instance ?? instanceOfSource(serviceId);
    const found = findInstance(projects, key);
    if (!found) return;
    dispatch({ type: 'selectInstance', key: found.instance.key });
    dispatch({ type: 'setTab', tab: 'services' });
    dispatch({ type: 'setLogSources', sources: [serviceId] });
  };

  return (
    <div className="ports-view">
      <header className="pv-head">
        <div className="pv-title-row">
          <div className="pv-title">
            <h1>Ports</h1>
            <span className="muted">Every listening TCP port on this machine</span>
          </div>
          <div className="pv-actions">
            <label className="side-filter inline">
              <Icon name="search" size={12} />
              <input value={query} onChange={(e) => setQuery(e.target.value)} placeholder="Filter port, pid, process, command…" aria-label="Filter ports" />
            </label>
            <label className="check-inline">
              <input type="checkbox" checked={onlyExternal} onChange={(e) => setOnlyExternal(e.target.checked)} />
              Unmanaged only
            </label>
            <Button size="sm" icon="refresh" busy={loading} onClick={() => void load()}>
              Refresh
            </Button>
          </div>
        </div>
      </header>
      <div className="pv-body">
        {error && <div className="inline-error">{error}</div>}
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
                  return (
                    <tr key={`${p.port}:${p.pid}`} className={p.managedBy ? 'managed' : ''}>
                      <td className="num mono strong">
                        <a href={`http://localhost:${p.port}`} target="_blank" rel="noreferrer" className="port-link">
                          {p.port}
                        </a>
                      </td>
                      <td className="mono muted">{p.address}</td>
                      <td className="num mono">{p.pid}</td>
                      <td>{p.processName ?? <span className="muted">?</span>}</td>
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
                        {!p.managedBy && (
                          <Button size="sm" variant="danger" busy={!!pending['kill:' + p.pid]} onClick={() => setKill(p)}>
                            Kill
                          </Button>
                        )}
                      </td>
                    </tr>
                  );
                })}
                {rows.length === 0 && (
                  <tr>
                    <td colSpan={7} className="table-empty">
                      {q || onlyExternal ? 'No ports match.' : 'No listening ports.'}
                    </td>
                  </tr>
                )}
              </tbody>
            </table>
          </section>
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
