import { useEffect, useState } from 'react';
import { api } from '../../api/client';
import type { TownSettingsDto } from '../../api/types';

const SWITCHES: { key: keyof TownSettingsDto; label: string; note?: string }[] = [
  { key: 'tray', label: 'Blink the tray icon', note: 'While a session needs you and the town is not on screen.' },
  { key: 'notifications', label: 'Windows notifications', note: 'When a session starts needing you and the town is not on screen.' },
  { key: 'sound', label: 'Sounds', note: 'A chime when a session needs you, a softer one when it finishes.' },
  { key: 'hooks', label: 'Agent Town hooks in Claude Code', note: "Off removes the town hooks from Claude Code's settings; the town then stops seeing sessions." },
];

/** Agent Town settings panel. Every change saves the whole set; the daemon's answer is shown under it. */
export function TownSettings({ onClose, onChange }: { onClose: () => void; onChange?: (s: TownSettingsDto) => void }) {
  const [settings, setSettings] = useState<TownSettingsDto | null>(null);
  const [note, setNote] = useState<{ ok: boolean; text: string } | null>(null);
  useEffect(() => {
    let live = true;
    api.townSettings().then((s) => live && setSettings(s), () => live && setNote({ ok: false, text: 'Could not load the settings.' }));
    return () => {
      live = false;
    };
  }, []);

  const toggle = async (key: keyof TownSettingsDto) => {
    if (!settings) return;
    const next = { ...settings, [key]: !settings[key] };
    setSettings(next);
    onChange?.(next);
    const r = await api.saveTownSettings(next);
    setNote(r.ok ? (r.message && r.message !== 'saved' ? { ok: true, text: r.message } : null) : { ok: false, text: r.error ?? 'Could not save.' });
  };

  return (
    <aside className="town-settings" aria-label="Town settings">
      <header>
        <span>Settings</span>
        <button type="button" onClick={onClose} aria-label="Close settings">
          ×
        </button>
      </header>
      {settings &&
        SWITCHES.map((s) => (
          <label key={s.key} className="town-switch">
            <input
              type="checkbox"
              aria-label={s.label}
              aria-describedby={'town-note-' + s.key}
              checked={settings[s.key]}
              onChange={() => void toggle(s.key)}
            />
            <span>
              <span className="town-switch-label">{s.label}</span>
              {s.note && (
                <span id={'town-note-' + s.key} className="town-switch-note">
                  {s.note}
                </span>
              )}
            </span>
          </label>
        ))}
      {note && <p className={`town-settings-note${note.ok ? '' : ' bad'}`}>{note.text}</p>}
    </aside>
  );
}
