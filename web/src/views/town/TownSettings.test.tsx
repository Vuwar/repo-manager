import { act, fireEvent, render, screen } from '@testing-library/react';
import { beforeEach, describe, expect, it, vi } from 'vitest';

const allOn = { tray: true, notifications: true, sound: true, hooks: true };
const api = {
  townSettings: vi.fn(),
  saveTownSettings: vi.fn(),
};
vi.mock('../../api/client', () => ({ api }));

const { TownSettings } = await import('./TownSettings');

async function show() {
  const onChange = vi.fn();
  await act(async () => void render(<TownSettings onClose={() => {}} onChange={onChange} />));
  return { onChange };
}

describe('TownSettings', () => {
  beforeEach(() => {
    api.townSettings.mockReset().mockResolvedValue(allOn);
    api.saveTownSettings.mockReset().mockResolvedValue({ ok: true, message: 'saved', affected: [], logTail: [] });
  });

  it('shows the four switches as loaded', async () => {
    await show();
    for (const name of ['Blink the tray icon', 'Windows notifications', 'Sounds', 'Agent Town hooks in Claude Code'])
      expect((screen.getByRole('checkbox', { name }) as HTMLInputElement).checked).toBe(true);
    expect(screen.getByText(/Off removes the town hooks/)).toBeTruthy();
  });

  it('saves the whole set when one switch changes', async () => {
    const { onChange } = await show();
    await act(async () => void fireEvent.click(screen.getByRole('checkbox', { name: 'Sounds' })));
    expect(api.saveTownSettings).toHaveBeenCalledWith({ ...allOn, sound: false });
    expect(onChange).toHaveBeenCalledWith({ ...allOn, sound: false });
  });

  it('shows what the daemon says, including a failure', async () => {
    api.saveTownSettings.mockResolvedValue({ ok: false, error: "setting saved, but Claude Code's settings were not changed: x", affected: [], logTail: [] });
    await show();
    await act(async () => void fireEvent.click(screen.getByRole('checkbox', { name: 'Agent Town hooks in Claude Code' })));
    expect(screen.getByText(/were not changed/)).toBeTruthy();
  });
});
