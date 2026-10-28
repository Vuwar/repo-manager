import type { AgentSessionDto, PermissionPromptDto } from '../../api/types';

export type DialogueKind = 'permission' | 'answerInWindow' | 'question' | 'done' | 'working' | 'idle' | 'failed';

/** What a worker says in its dialogue box. */
export interface Dialogue {
  kind: DialogueKind;
  /** "KODEX · Session A" */
  nameTag: string;
  mood: '!' | '✓' | null;
  text: string;
  /** The prompt the permission menu answers. */
  prompt?: PermissionPromptDto;
  /** The box shows the ">" message line. */
  canSend: boolean;
}

/** The worker asks for permission in its own words. */
export function speechFor(p: PermissionPromptDto): string {
  return p.toolName === 'Bash' || p.toolName === 'PowerShell'
    ? `Boss! I need to run a command first: \`${p.summary}\` Can I?`
    : `Boss! I need to use ${p.toolName} first. Can I?`;
}

/** 0 → "A", 25 → "Z", 26 → "AA". */
export function sessionLetter(index: number): string {
  let n = index + 1;
  let s = '';
  while (n > 0) {
    const r = (n - 1) % 26;
    s = String.fromCharCode(65 + r) + s;
    n = Math.floor((n - 1) / 26);
  }
  return s;
}

export function dialogueFor(session: AgentSessionDto, lotName: string, indexInLot: number, log: string[]): Dialogue {
  const nameTag = `${lotName.toUpperCase()} · Session ${sessionLetter(indexInLot)}`;
  const answerable = session.prompts.find((p) => p.answerable);
  const released = session.prompts.find((p) => !p.answerable);
  let kind: DialogueKind;
  let text: string;
  if (answerable) {
    kind = 'permission';
    text = speechFor(answerable);
  } else if (released) {
    kind = 'answerInWindow';
    text = `I asked in my window: ${released.summary}. Answer it there.`;
  } else if (session.state === 'waiting') {
    kind = 'question';
    text = session.message;
  } else if (session.state === 'done') {
    kind = 'done';
    text = session.lastReply ?? session.message;
  } else if (session.state === 'working') {
    kind = 'working';
    text = [`Working on: ${session.prompt ?? session.message}`, log.length ? '\n' + log.join('\n') : ''].join('\n').trimEnd();
  } else if (session.state === 'failed') {
    kind = 'failed';
    text = `Something broke: ${session.message}`;
  } else {
    kind = 'idle';
    text = 'Ready when you are.';
  }
  const mood = kind === 'permission' || kind === 'answerInWindow' || kind === 'question' ? '!' : kind === 'done' ? '✓' : null;
  const canSend = session.canSend && (kind === 'done' || kind === 'working' || kind === 'idle' || kind === 'failed');
  return { kind, nameTag, mood, text, prompt: answerable, canSend };
}
