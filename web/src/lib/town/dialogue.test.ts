import { describe, expect, it } from 'vitest';
import type { PermissionPromptDto } from '../../api/types';
import { agent } from '../../test/fixtures';
import { dialogueFor, sessionLetter, speechFor } from './dialogue';

const prompt = (over: Partial<PermissionPromptDto> = {}): PermissionPromptDto => ({
  requestId: 'r1',
  toolName: 'Bash',
  summary: 'npm install',
  answerable: true,
  ...over,
});

describe('speechFor', () => {
  it('asks to run a shell command', () => {
    expect(speechFor(prompt())).toBe('Boss! I need to run a command first: `npm install` Can I?');
    expect(speechFor(prompt({ toolName: 'PowerShell', summary: 'Get-Item x' }))).toBe('Boss! I need to run a command first: `Get-Item x` Can I?');
  });

  it('asks to use another tool', () => {
    expect(speechFor(prompt({ toolName: 'Write', summary: 'Write' }))).toBe('Boss! I need to use Write first. Can I?');
  });
});

describe('sessionLetter', () => {
  it('counts A to Z, then AA', () => {
    expect([0, 1, 25, 26, 27].map(sessionLetter)).toEqual(['A', 'B', 'Z', 'AA', 'AB']);
  });
});

describe('dialogueFor', () => {
  it('builds the name tag from the lot and the session index', () => {
    expect(dialogueFor(agent('a'), 'kodex', 1, []).nameTag).toBe('KODEX · Session B');
  });

  it('uses the session title in the name tag when there is one', () => {
    expect(dialogueFor(agent('a', { title: 'Error investigation' }), 'panel-pro', 1, []).nameTag).toBe('PANEL-PRO · Error investigation');
  });

  it('done shows the whole last turn when known', () => {
    const d = dialogueFor(agent('a', { state: 'done', lastReply: 'second', lastTurn: 'first\n\nsecond' }), 'k', 0, []);
    expect(d.text).toBe('first\n\nsecond');
  });

  it('carries the wrap-up question', () => {
    const wrapUp = { ask: 'Commit?', recap: 'Applied.', actions: [{ label: 'Commit', message: 'Commit it' }] };
    expect(dialogueFor(agent('a', { state: 'done', wrapUp }), 'k', 0, []).wrapUp).toEqual(wrapUp);
  });

  it('an answerable prompt wins over a released one', () => {
    const d = dialogueFor(
      agent('a', { state: 'waiting', canSend: true, prompts: [prompt({ requestId: 'old', answerable: false }), prompt({ requestId: 'new' })] }),
      'kodex',
      0,
      [],
    );
    expect(d.kind).toBe('permission');
    expect(d.prompt?.requestId).toBe('new');
    expect(d.mood).toBe('!');
    expect(d.canSend).toBe(false);
  });

  it('a released prompt is answered in the window', () => {
    const d = dialogueFor(agent('a', { state: 'waiting', prompts: [prompt({ answerable: false })] }), 'kodex', 0, []);
    expect(d.kind).toBe('answerInWindow');
    expect(d.text).toBe('I asked in my window: npm install. Answer it there.');
  });

  it('waiting with no prompt is a question', () => {
    const d = dialogueFor(agent('a', { state: 'waiting', message: 'Which database?' }), 'kodex', 0, []);
    expect(d).toMatchObject({ kind: 'question', text: 'Which database?', mood: '!' });
  });

  it("done shows Claude's last message unchanged", () => {
    const reply = 'All done.\n\n- fixed `a`\n- fixed b  ';
    const d = dialogueFor(agent('a', { state: 'done', lastReply: reply, message: 'x', canSend: true }), 'kodex', 0, []);
    expect(d).toMatchObject({ kind: 'done', text: reply, mood: '✓', canSend: true });
  });

  it('working shows the prompt and recent tools', () => {
    const d = dialogueFor(agent('a', { state: 'working', prompt: 'fix the bug' }), 'kodex', 0, ['Used Read', 'Used Bash']);
    expect(d.text).toBe('Working on: fix the bug\n\nUsed Read\nUsed Bash');
    expect(d.mood).toBeNull();
  });

  it('idle and failed', () => {
    expect(dialogueFor(agent('a', { state: 'idle' }), 'k', 0, []).text).toBe('Ready when you are.');
    expect(dialogueFor(agent('a', { state: 'failed', message: 'API Error: Rate limit' }), 'k', 0, []).text).toBe(
      'Something broke: API Error: Rate limit',
    );
  });

  it('cannot send without an inbox', () => {
    expect(dialogueFor(agent('a', { state: 'done', canSend: false }), 'k', 0, []).canSend).toBe(false);
  });
});
