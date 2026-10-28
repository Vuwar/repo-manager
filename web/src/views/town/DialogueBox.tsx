import { useEffect, useLayoutEffect, useRef, useState } from 'react';
import { api } from '../../api/client';
import type { AgentSessionDto } from '../../api/types';
import { dialogueFor } from '../../lib/town/dialogue';
import { waitText } from '../../lib/town/quests';
import { drawPortrait, STATE_COLOR } from '../../lib/town/render';
import { useNow } from '../../lib/useNow';
import { useAppState } from '../../state/store';
import { Markdown } from './Markdown';

/** Characters per second while the text types out. */
const TYPE_CPS = 40;

const CHOICES = [
  { label: 'Go ahead', behavior: 'allow', always: false },
  { label: 'Always OK', behavior: 'allow', always: true },
  { label: "No, don't", behavior: 'deny', always: false },
] as const;

const NO_LOG: string[] = [];

function isTypingTarget(el: EventTarget | null): boolean {
  const t = el as HTMLElement | null;
  return !!t && (t.isContentEditable || t.tagName === 'INPUT' || t.tagName === 'TEXTAREA' || t.tagName === 'SELECT');
}

const reducedMotion = () => typeof window.matchMedia === 'function' && window.matchMedia('(prefers-reduced-motion: reduce)').matches;

/** RPG dialogue box for one session: what it says, its permission menu or a message line, and "Open window". */
interface Props {
  session: AgentSessionDto;
  lotName: string;
  index: number;
  onClose: () => void;
  /** The typed message, kept by the town so it survives the box closing (quest log, Esc). */
  draft?: string;
  onDraft?: (text: string) => void;
}

export function DialogueBox({ session, lotName, index, onClose, draft: keptDraft, onDraft }: Props) {
  const log = useAppState((s) => s.agentLog[session.id] ?? NO_LOG);
  const d = dialogueFor(session, lotName, index, log);
  const now = useNow();

  // Typewriter: restarts when the text changes.
  const [shown, setShown] = useState(0);
  const done = shown >= d.text.length;
  // New text that only extends what is already shown (a working session's next tool) keeps the shown part.
  const typed = useRef('');
  useLayoutEffect(() => {
    setShown((n) => (reducedMotion() ? d.text.length : d.text.startsWith(typed.current.slice(0, n)) ? n : 0));
    typed.current = d.text;
  }, [d.text]);
  useEffect(() => {
    if (done) return;
    const t = setInterval(() => setShown((n) => n + 1), 1000 / TYPE_CPS);
    return () => clearInterval(t);
  }, [done, d.text]);
  const finish = () => setShown(d.text.length);

  // Permission menu.
  const [choice, setChoice] = useState(0);
  const [answered, setAnswered] = useState<string | null>(null);
  const requestId = d.prompt?.requestId;
  useEffect(() => {
    setChoice(0);
    setAnswered(null);
    picking.current = false;
  }, [requestId]);
  const menu = d.kind === 'permission' && !answered;
  const picking = useRef(false);
  const pick = async (i: number) => {
    // One answer per prompt: a double click or Enter while the first answer travels sends nothing.
    if (!d.prompt || picking.current) return;
    picking.current = true;
    const c = CHOICES[i];
    setAnswered('Answering…');
    const r = await api.townAnswer(d.prompt.requestId, c.behavior, c.always);
    const late = !r.ok && (r.error ?? '').includes('no longer waiting');
    setAnswered(r.ok ? `${c.label}.` : late ? 'Already answered in the window.' : (r.error ?? 'Could not answer.'));
  };

  // Message line.
  const [localDraft, setLocalDraft] = useState('');
  const draft = keptDraft ?? localDraft;
  const setDraft = onDraft ?? setLocalDraft;
  const [sending, setSending] = useState(false);
  const input = useRef<HTMLTextAreaElement>(null);
  const [status, setStatus] = useState<{ ok: boolean; text: string } | null>(null);
  const send = async () => {
    const text = draft.trim();
    if (!text || sending) return;
    setSending(true);
    const r = await api.townSend(session.id, text);
    setSending(false);
    // Keep typing in the box: follow-up keys must not reach the town.
    input.current?.focus();
    if (r.ok) setDraft('');
    setStatus(r.ok ? { ok: true, text: 'Sent.' } : { ok: false, text: r.error ?? 'Could not send.' });
  };
  const [openError, setOpenError] = useState<string | null>(null);
  const openWindow = async () => {
    const r = await api.townOpen(session.id);
    setOpenError(r.ok ? null : (r.error ?? 'Could not open the window.'));
  };

  // Keys: Space/Enter finish typing, arrows and Enter drive the menu, Esc walks away.
  const keys = useRef({ done, menu, choice, finish, pick, onClose });
  keys.current = { done, menu, choice, finish, pick, onClose };
  useEffect(() => {
    const onKey = (e: KeyboardEvent) => {
      if (isTypingTarget(e.target)) return;
      const k = keys.current;
      if (e.key === 'Escape') k.onClose();
      else if (k.menu && e.key === 'ArrowDown') setChoice((c) => (c + 1) % CHOICES.length);
      else if (k.menu && e.key === 'ArrowUp') setChoice((c) => (c + CHOICES.length - 1) % CHOICES.length);
      else if (k.menu && e.key === 'Enter') void k.pick(k.choice);
      else if ((e.key === ' ' || e.key === 'Enter') && !k.done) k.finish();
      else return;
      e.preventDefault();
    };
    window.addEventListener('keydown', onKey);
    return () => window.removeEventListener('keydown', onKey);
  }, []);

  const portrait = useRef<HTMLCanvasElement>(null);
  useEffect(() => {
    const ctx = portrait.current?.getContext('2d');
    if (ctx) drawPortrait(ctx, session);
  }, [session]);

  const tag = STATE_COLOR[session.state];
  return (
    <div className="dlg" role="dialog" aria-label={d.nameTag}>
      {menu && (
        <div className="dlg-menu" role="menu">
          {CHOICES.map((c, i) => (
            <button key={c.label} type="button" role="menuitem" className={i === choice ? 'cur' : ''} onClick={() => void pick(i)} onMouseEnter={() => setChoice(i)}>
              {c.label}
            </button>
          ))}
        </div>
      )}
      <div className="dlg-portrait">
        <canvas ref={portrait} width={16} height={16} aria-hidden="true" />
        {d.mood && <span className={`dlg-mood${d.mood === '✓' ? ' ok' : ''}`}>{d.mood}</span>}
      </div>
      <div className="dlg-body" onClick={finish}>
        <span className="dlg-name" style={{ background: tag }}>
          {d.nameTag}
        </span>
        <span className="dlg-wait">⏳ {waitText(now - Date.parse(session.since))}</span>
        <div className="dlg-text">
          <Markdown text={d.text.slice(0, shown)} />
        </div>
        {done && <span className="dlg-more">▼</span>}
        {answered && <div className="dlg-note">{answered}</div>}
        {d.canSend && (
          <label className="dlg-input">
            <span aria-hidden="true">&gt;</span>
            <textarea
              aria-label="Message"
              rows={1}
              value={draft}
              ref={input}
              // readOnly, not disabled: a disabled textarea drops focus and the next keys would reach the town.
              readOnly={sending}
              onChange={(e) => setDraft(e.target.value)}
              onKeyDown={(e) => {
                if (e.key === 'Escape') {
                  e.preventDefault();
                  onClose();
                } else if (e.key === 'Enter' && !e.shiftKey) {
                  e.preventDefault();
                  void send();
                }
              }}
            />
          </label>
        )}
        {status && <div className={`dlg-note${status.ok ? '' : ' bad'}`}>{status.text}</div>}
        {openError && <div className="dlg-note bad">{openError}</div>}
        <div className="dlg-actions">
          <button type="button" onClick={(e) => (e.stopPropagation(), void openWindow())}>
            Open window
          </button>
          <span className="dlg-hint">{menu ? '↑↓ choose · Enter confirm · Esc walk away' : 'Esc walk away'}</span>
        </div>
      </div>
    </div>
  );
}
