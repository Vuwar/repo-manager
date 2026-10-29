import { act, fireEvent, render, screen } from '@testing-library/react';
import { describe, expect, it, vi } from 'vitest';
import type { ActionResultDto, TownSettingsDto } from '../../api/types';
import { TownSettings } from './TownSettings';

const allOn: TownSettingsDto = { tray: true, notifications: true, sound: true, hooks: true };
const ok: ActionResultDto = { ok: true, message: 'saved', affected: [], logTail: [] };

function show(settings: TownSettingsDto | null = allOn, result: ActionResultDto = ok) {
  const onSave = vi.fn((_: TownSettingsDto) => Promise.resolve(result));
  render(<TownSettings settings={settings} onSave={onSave} onClose={() => {}} />);
  return { onSave };
}

describe('TownSettings', () => {
  it('shows the four switches from the settings it is given', () => {
    show({ ...allOn, sound: false });
    for (const name of ['Blink the tray icon', 'Windows notifications', 'Agent Town hooks in Claude Code'])
      expect((screen.getByRole('checkbox', { name }) as HTMLInputElement).checked).toBe(true);
    expect((screen.getByRole('checkbox', { name: 'Sounds' }) as HTMLInputElement).checked).toBe(false);
    expect(screen.getByText(/Off removes the town hooks/)).toBeTruthy();
  });

  it('saves the whole set when one switch changes', async () => {
    const { onSave } = show();
    await act(async () => void fireEvent.click(screen.getByRole('checkbox', { name: 'Sounds' })));
    expect(onSave).toHaveBeenCalledWith({ ...allOn, sound: false });
  });

  it('shows what the daemon says, including a failure', async () => {
    show(allOn, { ok: false, error: "setting saved, but Claude Code's settings were not changed: x", affected: [], logTail: [] });
    await act(async () => void fireEvent.click(screen.getByRole('checkbox', { name: 'Agent Town hooks in Claude Code' })));
    expect(screen.getByText(/were not changed/)).toBeTruthy();
  });

  it('disables the switches while a change is saving', async () => {
    let finish: (r: ActionResultDto) => void = () => {};
    const onSave = vi.fn(() => new Promise<ActionResultDto>((r) => (finish = r)));
    render(<TownSettings settings={allOn} onSave={onSave} onClose={() => {}} />);
    fireEvent.click(screen.getByRole('checkbox', { name: 'Agent Town hooks in Claude Code' }));
    expect((screen.getByRole('checkbox', { name: 'Sounds' }) as HTMLInputElement).disabled).toBe(true);
    await act(async () => finish(ok));
    expect((screen.getByRole('checkbox', { name: 'Sounds' }) as HTMLInputElement).disabled).toBe(false);
  });

  it('says so while the settings load', () => {
    show(null);
    expect(screen.getByText('Loading…')).toBeTruthy();
  });
});
