import { act, cleanup, fireEvent, render, screen } from '@testing-library/react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import type { PastSessionDto, TranscriptPageDto } from '../../api/types';

const ok = { ok: true, affected: [], logTail: [] };
const api = {
  townHistory: vi.fn(),
  townTranscript: vi.fn(),
  townHandoff: vi.fn(),
  townStart: vi.fn(),
  townStartStatus: vi.fn(),
  townOpenHost: vi.fn(),
};
vi.mock('../../api/client', () => ({ api }));
const { RepoPanel } = await import('./RepoPanel');

const past = (id: string, over: Partial<PastSessionDto> = {}): PastSessionDto => ({
  id,
  project: 'demo',
  title: 'Session ' + id,
  surface: 'terminal',
  archived: false,
  started: '2026-10-01T09:00:00Z',
  lastActivity: '2026-10-01T10:00:00Z',
  cwd: 'C:\\repos\\demo',
  ...over,
});

const page = (start: number, total: number, texts: string[]): TranscriptPageDto => ({
  id: 'x',
  start,
  total,
  entries: texts.map((t, i) => ({ role: i % 2 ? 'assistant' : 'user', text: t })),
});

/** Lets pending promises (api calls) settle. */
const flush = () => act(async () => {});

function setup(sessions: PastSessionDto[]) {
  api.townHistory.mockResolvedValue(sessions);
  const onClose = vi.fn();
  render(<RepoPanel project="demo" onClose={onClose} />);
  return { onClose };
}

describe('RepoPanel', () => {
  beforeEach(() => {
    Object.values(api).forEach((f) => f.mockReset());
    api.townOpenHost.mockResolvedValue(ok);
    api.townStart.mockResolvedValue({ id: 's1', state: 'none' });
  });
  afterEach(() => {
    cleanup();
    vi.useRealTimers();
  });

  it('lists past sessions with their tags', async () => {
    setup([past('a', { surface: 'desktop', hostSessionId: 'local_a', archived: true }), past('b', { worktree: 'feat-x' })]);
    await flush();
    expect(api.townHistory).toHaveBeenCalledWith('demo');
    const rows = screen.getAllByRole('button', { name: /^Session / });
    expect(rows.map((r) => r.textContent)).toEqual([
      expect.stringMatching(/Session a.*Desktop.*archived/),
      expect.stringMatching(/Session b.*Terminal.*feat-x/),
    ]);
  });

  it('says so when there are no past sessions', async () => {
    setup([]);
    await flush();
    expect(screen.getByText('No past sessions.')).toBeTruthy();
  });

  it('opens a transcript, and Load earlier fetches the entries before it', async () => {
    setup([past('a')]);
    await flush();
    api.townTranscript.mockResolvedValueOnce(page(150, 300, ['late prompt', 'late reply'])).mockResolvedValueOnce(page(0, 300, ['early prompt', 'early reply']));
    fireEvent.click(screen.getByRole('button', { name: /^Session a/ }));
    await flush();
    expect(api.townTranscript).toHaveBeenCalledWith('a', undefined);
    expect(screen.getByText('late reply')).toBeTruthy();
    fireEvent.click(screen.getByRole('button', { name: 'Load earlier' }));
    await flush();
    expect(api.townTranscript).toHaveBeenLastCalledWith('a', 150);
    const texts = [...document.querySelectorAll('.town-entry')].map((e) => e.textContent);
    expect(texts.join('|')).toMatch(/early prompt.*early reply.*late prompt.*late reply/);
    expect(screen.queryByRole('button', { name: 'Load earlier' })).toBeNull();
  });

  it('resumes a desktop session in the desktop app', async () => {
    setup([past('a', { surface: 'desktop', hostSessionId: 'local_a' })]);
    await flush();
    api.townTranscript.mockResolvedValue(page(0, 2, ['p', 'r']));
    fireEvent.click(screen.getByRole('button', { name: /^Session a/ }));
    await flush();
    expect(screen.queryByRole('button', { name: 'Continue in new session' })).toBeNull();
    fireEvent.click(screen.getByRole('button', { name: 'Resume in desktop' }));
    await flush();
    expect(api.townOpenHost).toHaveBeenCalledWith('local_a');
  });

  it('continues a terminal session with the edited handoff', async () => {
    setup([past('a', { worktree: 'feat-x' })]);
    await flush();
    api.townTranscript.mockResolvedValue(page(0, 2, ['p', 'r']));
    api.townHandoff.mockResolvedValue({ text: 'This continues an earlier session' });
    fireEvent.click(screen.getByRole('button', { name: /^Session a/ }));
    await flush();
    expect(screen.queryByRole('button', { name: 'Resume in desktop' })).toBeNull();
    fireEvent.click(screen.getByRole('button', { name: 'Continue in new session' }));
    await flush();
    const box = screen.getByRole('textbox', { name: 'Handoff' }) as HTMLTextAreaElement;
    expect(box.value).toBe('This continues an earlier session');
    fireEvent.change(box, { target: { value: 'This continues an earlier session\nNow add tests.' } });
    fireEvent.click(screen.getByRole('button', { name: 'Start in terminal' }));
    await flush();
    expect(api.townStart).toHaveBeenCalledWith({
      surface: 'terminal',
      project: 'demo',
      worktree: 'feat-x',
      fromSessionId: 'a',
      text: 'This continues an earlier session\nNow add tests.',
    });
  });

  it('starts a new terminal session on Enter, Shift+Enter makes a new line', async () => {
    setup([]);
    await flush();
    const box = screen.getByRole('textbox', { name: 'First prompt' });
    fireEvent.change(box, { target: { value: 'hello' } });
    fireEvent.keyDown(box, { key: 'Enter', shiftKey: true });
    expect(api.townStart).not.toHaveBeenCalled();
    fireEvent.keyDown(box, { key: 'Enter' });
    await flush();
    expect(api.townStart).toHaveBeenCalledWith({ surface: 'terminal', project: 'demo', text: 'hello' });
  });

  it('starts a new desktop session without text', async () => {
    setup([]);
    await flush();
    fireEvent.change(screen.getByRole('textbox', { name: 'First prompt' }), { target: { value: 'ignored' } });
    fireEvent.click(screen.getByRole('button', { name: 'Desktop' }));
    await flush();
    expect(api.townStart).toHaveBeenCalledWith({ surface: 'desktop', project: 'demo' });
  });

  it('polls a pending start until it is sent', async () => {
    vi.useFakeTimers();
    setup([]);
    await flush();
    api.townStart.mockResolvedValue({ id: 's1', state: 'pending' });
    api.townStartStatus.mockResolvedValueOnce({ id: 's1', state: 'pending' }).mockResolvedValueOnce({ id: 's1', state: 'sent' });
    fireEvent.change(screen.getByRole('textbox', { name: 'First prompt' }), { target: { value: 'hello' } });
    fireEvent.click(screen.getByRole('button', { name: 'Terminal' }));
    await flush();
    expect(screen.getByText('Waiting for the session…')).toBeTruthy();
    await act(async () => void vi.advanceTimersByTime(2000));
    await act(async () => void vi.advanceTimersByTime(2000));
    expect(api.townStartStatus).toHaveBeenCalledTimes(2);
    expect(screen.getByText('Sent')).toBeTruthy();
    await act(async () => void vi.advanceTimersByTime(4000));
    expect(api.townStartStatus).toHaveBeenCalledTimes(2);
  });

  it('keeps the text when a start expires or fails', async () => {
    vi.useFakeTimers();
    setup([]);
    await flush();
    api.townStart.mockResolvedValue({ id: 's1', state: 'pending' });
    api.townStartStatus.mockResolvedValue({ id: 's1', state: 'expired', message: 'no session started in time' });
    fireEvent.change(screen.getByRole('textbox', { name: 'First prompt' }), { target: { value: 'keep me' } });
    fireEvent.click(screen.getByRole('button', { name: 'Terminal' }));
    await flush();
    await act(async () => void vi.advanceTimersByTime(2000));
    expect(screen.getByText('Not sent: no session started in time')).toBeTruthy();
    expect((screen.getByRole('textbox', { name: 'First prompt' }) as HTMLTextAreaElement).value).toBe('keep me');
  });

  it('shows a failed call and keeps the text', async () => {
    setup([]);
    await flush();
    api.townStart.mockRejectedValue(new Error('RepoManager is not reachable'));
    fireEvent.change(screen.getByRole('textbox', { name: 'First prompt' }), { target: { value: 'keep me' } });
    fireEvent.click(screen.getByRole('button', { name: 'Terminal' }));
    await flush();
    expect(screen.getByText('Not sent: RepoManager is not reachable')).toBeTruthy();
    expect((screen.getByRole('textbox', { name: 'First prompt' }) as HTMLTextAreaElement).value).toBe('keep me');
  });

  it('closes on Esc and on the close button', async () => {
    const { onClose } = setup([]);
    await flush();
    fireEvent.keyDown(screen.getByRole('textbox', { name: 'First prompt' }), { key: 'Escape' });
    fireEvent.click(screen.getByRole('button', { name: 'Close' }));
    expect(onClose).toHaveBeenCalledTimes(2);
  });
});
