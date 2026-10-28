import { act, fireEvent, render, screen } from '@testing-library/react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import type { AgentSessionDto } from '../../api/types';
import { agent } from '../../test/fixtures';

const ok = { ok: true, affected: [], logTail: [] };
const api = {
  townSend: vi.fn(),
  townAnswer: vi.fn(),
  townOpen: vi.fn(),
};
vi.mock('../../api/client', () => ({ api }));

const { DialogueBox } = await import('./DialogueBox');
const { dispatch } = await import('../../state/store');

const text = () => document.querySelector('.dlg-text')!.textContent;

function show(session: AgentSessionDto, index = 0, lotName = 'kodex') {
  const onClose = vi.fn();
  const view = render(<DialogueBox session={session} lotName={lotName} index={index} onClose={onClose} />);
  return { onClose, view };
}

describe('DialogueBox', () => {
  beforeEach(() => {
    vi.useFakeTimers();
    api.townSend.mockReset().mockResolvedValue(ok);
    api.townAnswer.mockReset().mockResolvedValue(ok);
    api.townOpen.mockReset().mockResolvedValue(ok);
  });

  afterEach(() => vi.useRealTimers());

  it('types the text out and Space finishes it', () => {
    show(agent('a', { state: 'idle' }));
    expect(text()).not.toBe('Ready when you are.');
    fireEvent.keyDown(window, { key: ' ' });
    expect(text()).toBe('Ready when you are.');
  });

  it('finishes typing on its own', () => {
    show(agent('a', { state: 'idle' }));
    act(() => vi.advanceTimersByTime(5000));
    expect(text()).toBe('Ready when you are.');
    expect(document.querySelector('.dlg-more')).toBeTruthy();
  });

  it('name tag is "KODEX · Session B" for index 1 of lot kodex', () => {
    show(agent('a'), 1);
    expect(screen.getByText('KODEX · Session B')).toBeTruthy();
  });

  it("done shows Claude's last message unchanged", () => {
    const reply = 'All done.\nNothing else to report.';
    show(agent('a', { state: 'done', lastReply: reply }));
    fireEvent.keyDown(window, { key: ' ' });
    expect(text()).toBe(reply);
  });

  it("formats Claude's Markdown", () => {
    show(agent('a', { state: 'done', lastReply: 'All **done**.\n\n- fixed `a`\n- fixed b' }));
    fireEvent.keyDown(window, { key: ' ' });
    const box = document.querySelector('.dlg-text')!;
    expect(box.querySelector('strong')?.textContent).toBe('done');
    expect([...box.querySelectorAll('li')].map((l) => l.textContent)).toEqual(['fixed a', 'fixed b']);
    expect(box.querySelector('code')?.textContent).toBe('a');
  });

  it('permission menu: arrows and Enter send allow with always', async () => {
    show(agent('a', { state: 'waiting', prompts: [{ requestId: 'r1', toolName: 'Bash', summary: 'npm i', answerable: true }] }));
    expect(screen.getAllByRole('menuitem').map((m) => m.textContent)).toEqual(['Go ahead', 'Always OK', "No, don't"]);
    fireEvent.keyDown(window, { key: 'ArrowDown' });
    await act(async () => void fireEvent.keyDown(window, { key: 'Enter' }));
    expect(api.townAnswer).toHaveBeenCalledWith('r1', 'allow', true);
  });

  it('No, don\'t sends deny when clicked', async () => {
    show(agent('a', { state: 'waiting', prompts: [{ requestId: 'r1', toolName: 'Bash', summary: 'npm i', answerable: true }] }));
    await act(async () => void fireEvent.click(screen.getByRole('menuitem', { name: "No, don't" })));
    expect(api.townAnswer).toHaveBeenCalledWith('r1', 'deny', false);
  });

  it('a second pick while the first is on its way sends nothing', async () => {
    let finish: (v: typeof ok) => void = () => {};
    api.townAnswer.mockReturnValue(new Promise((r) => (finish = r)));
    show(agent('a', { state: 'waiting', prompts: [{ requestId: 'r1', toolName: 'Bash', summary: 'npm i', answerable: true }] }));
    const go = screen.getByRole('menuitem', { name: 'Go ahead' });
    fireEvent.click(go);
    fireEvent.click(go);
    fireEvent.keyDown(window, { key: 'Enter' });
    await act(async () => finish(ok));
    expect(api.townAnswer).toHaveBeenCalledTimes(1);
  });

  it('other answer failures show their own message', async () => {
    api.townAnswer.mockResolvedValue({ ok: false, error: 'RepoManager is not reachable', affected: [], logTail: [] });
    show(agent('a', { state: 'waiting', prompts: [{ requestId: 'r1', toolName: 'Bash', summary: 'npm i', answerable: true }] }));
    await act(async () => void fireEvent.keyDown(window, { key: 'Enter' }));
    expect(screen.getByText('RepoManager is not reachable')).toBeTruthy();
  });

  it('an answer that comes too late says so and hides the menu', async () => {
    api.townAnswer.mockResolvedValue({ ok: false, error: 'that prompt is no longer waiting; answer it in the session window', affected: [], logTail: [] });
    show(agent('a', { state: 'waiting', prompts: [{ requestId: 'r1', toolName: 'Bash', summary: 'npm i', answerable: true }] }));
    await act(async () => void fireEvent.keyDown(window, { key: 'Enter' }));
    expect(screen.getByText('Already answered in the window.')).toBeTruthy();
    expect(screen.queryByRole('menu')).toBeNull();
  });

  it('Enter sends, Shift+Enter adds a line', async () => {
    show(agent('a', { state: 'done', canSend: true }));
    const box = screen.getByRole('textbox', { name: 'Message' }) as HTMLTextAreaElement;
    fireEvent.change(box, { target: { value: 'next step' } });
    fireEvent.keyDown(box, { key: 'Enter', shiftKey: true });
    expect(api.townSend).not.toHaveBeenCalled();
    await act(async () => void fireEvent.keyDown(box, { key: 'Enter' }));
    expect(api.townSend).toHaveBeenCalledWith('a', 'next step');
    expect(box.value).toBe('');
    expect(screen.getByText('Sent.')).toBeTruthy();
  });

  it('the message line keeps focus while sending', async () => {
    let finish: (v: typeof ok) => void = () => {};
    api.townSend.mockReturnValue(new Promise((r) => (finish = r)));
    show(agent('a', { state: 'done', canSend: true }));
    const box = screen.getByRole('textbox', { name: 'Message' }) as HTMLTextAreaElement;
    box.focus();
    fireEvent.change(box, { target: { value: 'one' } });
    await act(async () => void fireEvent.keyDown(box, { key: 'Enter' }));
    expect(box.disabled).toBe(false);
    expect(box.readOnly).toBe(true);
    await act(async () => finish(ok));
    expect(box.readOnly).toBe(false);
    expect(document.activeElement).toBe(box);
  });

  it('a working box does not retype what is already shown', () => {
    const first = agent('a', { state: 'working', prompt: 'fix it', message: 'Used Read' });
    dispatch({ type: 'agentsLoaded', agents: [first] });
    show(first);
    fireEvent.keyDown(window, { key: ' ' });
    const before = text()!;
    expect(before).toContain('Used Read');
    act(() => dispatch({ type: 'agent', agent: { ...first, message: 'Used Bash' } }));
    expect(text()!.startsWith(before)).toBe(true);
  });

  it('a failed send keeps the text and offers Open window', async () => {
    api.townSend.mockResolvedValue({ ok: false, error: 'could not reach the session inbox: gone', affected: [], logTail: [] });
    show(agent('a', { state: 'done', canSend: true }));
    const box = screen.getByRole('textbox', { name: 'Message' }) as HTMLTextAreaElement;
    fireEvent.change(box, { target: { value: 'keep me' } });
    await act(async () => void fireEvent.keyDown(box, { key: 'Enter' }));
    expect(box.value).toBe('keep me');
    expect(screen.getByText(/could not reach the session inbox/)).toBeTruthy();
    await act(async () => void fireEvent.click(screen.getByRole('button', { name: 'Open window' })));
    expect(api.townOpen).toHaveBeenCalledWith('a');
  });

  it('no message line without an inbox', () => {
    show(agent('a', { state: 'done', canSend: false }));
    expect(screen.queryByRole('textbox')).toBeNull();
  });

  it('Esc closes, also from the message line', () => {
    const { onClose } = show(agent('a', { state: 'done', canSend: true }));
    fireEvent.keyDown(screen.getByRole('textbox', { name: 'Message' }), { key: 'Escape' });
    expect(onClose).toHaveBeenCalledTimes(1);
    fireEvent.keyDown(window, { key: 'Escape' });
    expect(onClose).toHaveBeenCalledTimes(2);
  });
});
