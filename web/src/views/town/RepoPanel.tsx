import { useEffect, useState } from 'react';
import { api } from '../../api/client';
import type { PastSessionDto, StartStatusDto, TownStartRequest, TranscriptEntryDto } from '../../api/types';
import { Markdown } from './Markdown';

const POLL_MS = 2000;

/** yyyy-MM-dd HH:mm in local time. */
function when(iso: string): string {
  const d = new Date(iso);
  const p = (n: number) => String(n).padStart(2, '0');
  return `${d.getFullYear()}-${p(d.getMonth() + 1)}-${p(d.getDate())} ${p(d.getHours())}:${p(d.getMinutes())}`;
}

const errorText = (e: unknown) => (e instanceof Error ? e.message : 'request failed');

interface Opened {
  session: PastSessionDto;
  entries: TranscriptEntryDto[];
  start: number;
}

/**
 * A repo's past sessions and new sessions. Reading a past session only reads its transcript (no Claude, no tokens).
 * A desktop session resumes in the desktop app; a terminal one continues in a new terminal session with a handoff.
 */
export function RepoPanel({ project, onClose }: { project: string; onClose: () => void }) {
  const [sessions, setSessions] = useState<PastSessionDto[] | null>(null);
  const [listError, setListError] = useState<string | null>(null);
  const [opened, setOpened] = useState<Opened | null>(null);
  const [handoff, setHandoff] = useState<string | null>(null);
  const [prompt, setPrompt] = useState('');
  const [status, setStatus] = useState<{ state: StartStatusDto['state'] | 'busy'; id?: string; message?: string | null } | null>(null);
  const [note, setNote] = useState<string | null>(null);

  useEffect(() => {
    let live = true;
    api.townHistory(project).then(
      (s) => live && setSessions(s ?? []),
      (e) => live && setListError(errorText(e)),
    );
    return () => void (live = false);
  }, [project]);

  // A held first prompt: ask every POLL_MS until it is sent, expired or failed.
  const pendingId = status?.state === 'pending' ? status.id : undefined;
  useEffect(() => {
    if (!pendingId) return;
    const timer = setInterval(() => {
      api.townStartStatus(pendingId).then(
        (s) => s.state !== 'pending' && setStatus({ state: s.state, id: s.id, message: s.message }),
        (e) => setStatus({ state: 'failed', message: errorText(e) }),
      );
    }, POLL_MS);
    return () => clearInterval(timer);
  }, [pendingId]);

  const open = async (session: PastSessionDto) => {
    setHandoff(null);
    setStatus(null);
    setNote(null);
    try {
      const page = await api.townTranscript(session.id, undefined);
      setOpened({ session, entries: page.entries, start: page.start });
    } catch (e) {
      setNote(errorText(e));
    }
  };

  const loadEarlier = async () => {
    if (!opened) return;
    try {
      const page = await api.townTranscript(opened.session.id, opened.start);
      setOpened({ ...opened, entries: [...page.entries, ...opened.entries], start: page.start });
    } catch (e) {
      setNote(errorText(e));
    }
  };

  const resume = async (hostSessionId: string) => {
    try {
      const r = await api.townOpenHost(hostSessionId);
      setNote(r.ok ? null : (r.error ?? 'Could not open it in Claude.'));
    } catch (e) {
      setNote(errorText(e));
    }
  };

  const continueInNew = async (id: string) => {
    try {
      setHandoff((await api.townHandoff(id)).text);
    } catch (e) {
      setNote(errorText(e));
    }
  };

  const start = async (req: TownStartRequest) => {
    setStatus({ state: 'busy' });
    try {
      const s = await api.townStart(req);
      setStatus({ state: s.state, id: s.id, message: s.message });
    } catch (e) {
      setStatus({ state: 'failed', message: errorText(e) });
    }
  };
  const startTerminal = () => void start({ surface: 'terminal', project, ...(prompt.trim() ? { text: prompt } : {}) });
  const startDesktop = () => void start({ surface: 'desktop', project });

  const statusText = (() => {
    switch (status?.state) {
      case 'busy':
        return 'Starting…';
      case 'pending':
        return 'Waiting for the session…';
      case 'sent':
        return 'Sent';
      case 'none':
        return 'Started';
      case 'expired':
      case 'failed':
        return 'Not sent: ' + (status.message ?? 'no session started in time');
      default:
        return null;
    }
  })();
  const statusLine = statusText && <p className={`town-settings-note${status?.state === 'failed' || status?.state === 'expired' ? ' bad' : ''}`}>{statusText}</p>;

  return (
    <aside
      className="town-settings town-repo"
      aria-label={project + ' sessions'}
      onKeyDown={(e) => {
        if (e.key !== 'Escape') return;
        e.stopPropagation();
        onClose();
      }}
    >
      <header>
        <span>{opened ? opened.session.title : project}</span>
        <span>
          {opened && (
            <button type="button" onClick={() => setOpened(null)}>
              ← Back
            </button>
          )}
          <button type="button" onClick={onClose} aria-label="Close">
            ×
          </button>
        </span>
      </header>

      {!opened && (
        <>
          <section className="town-repo-new">
            <span className="town-switch-label">New session</span>
            <div className="town-repo-buttons">
              <button type="button" onClick={startTerminal} disabled={status?.state === 'busy'}>
                Terminal
              </button>
              <button type="button" onClick={startDesktop} disabled={status?.state === 'busy'}>
                Desktop
              </button>
            </div>
            <textarea
              aria-label="First prompt"
              placeholder="First prompt for the terminal session (optional)"
              rows={2}
              value={prompt}
              onChange={(e) => setPrompt(e.target.value)}
              onKeyDown={(e) => {
                if (e.key === 'Enter' && !e.shiftKey) {
                  e.preventDefault();
                  startTerminal();
                }
              }}
            />
            {statusLine}
          </section>
          <section className="town-repo-list">
            <span className="town-switch-label">Past sessions</span>
            {listError && <p className="town-settings-note bad">{listError}</p>}
            {!sessions && !listError && <p className="town-settings-note">Loading…</p>}
            {sessions?.length === 0 && <p className="town-settings-note">No past sessions.</p>}
            {sessions?.map((s) => (
              <button key={s.id} type="button" className="town-repo-row" onClick={() => void open(s)}>
                <span className="town-repo-title">{s.title}</span>
                <span className="town-switch-note">
                  {when(s.lastActivity)} · <span className="town-repo-tag">{s.surface === 'desktop' ? 'Desktop' : 'Terminal'}</span>
                  {s.worktree && <> · {s.worktree}</>}
                  {s.archived && <> · archived</>}
                </span>
              </button>
            ))}
            {note && <p className="town-settings-note bad">{note}</p>}
          </section>
        </>
      )}

      {opened && (
        <section className="town-repo-transcript">
          {opened.start > 0 && (
            <button type="button" className="town-repo-more" onClick={() => void loadEarlier()}>
              Load earlier
            </button>
          )}
          {opened.entries.map((e, i) => (
            <div key={opened.start + i} className={`town-entry town-entry-${e.role}`}>
              {e.role === 'user' && <span className="town-entry-who">You</span>}
              {e.role === 'assistant' ? <Markdown text={e.text} /> : <span>{e.text}</span>}
            </div>
          ))}
          <footer className="town-repo-actions">
            {opened.session.hostSessionId ? (
              <button type="button" onClick={() => void resume(opened.session.hostSessionId!)}>
                Resume in desktop
              </button>
            ) : handoff === null ? (
              <button type="button" onClick={() => void continueInNew(opened.session.id)}>
                Continue in new session
              </button>
            ) : (
              <>
                <textarea aria-label="Handoff" rows={8} value={handoff} onChange={(e) => setHandoff(e.target.value)} />
                <button
                  type="button"
                  disabled={status?.state === 'busy' || !handoff.trim()}
                  onClick={() =>
                    void start({
                      surface: 'terminal',
                      project,
                      worktree: opened.session.worktree ?? null,
                      fromSessionId: opened.session.id,
                      text: handoff,
                    })
                  }
                >
                  Start in terminal
                </button>
              </>
            )}
            {statusLine}
            {note && <p className="town-settings-note bad">{note}</p>}
          </footer>
        </section>
      )}
    </aside>
  );
}
