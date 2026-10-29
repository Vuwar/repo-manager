import { useState } from 'react';
import type { ActionResultDto, TownSettingsDto } from '../../api/types';

const SWITCHES: { key: keyof TownSettingsDto; label: string; note?: string }[] = [
  { key: 'tray', label: 'Blink the tray icon', note: 'While a session needs you and the town is not on screen.' },
  { key: 'notifications', label: 'Windows notifications', note: 'When a session starts needing you and the town is not on screen.' },
  { key: 'sound', label: 'Sounds', note: 'A chime when a session needs you, a softer one when it finishes.' },
  { key: 'hooks', label: 'Agent Town hooks in Claude Code', note: "Off removes the town hooks from Claude Code's settings; the town then stops seeing sessions." },
];

interface Props {
  /** The town's settings (null while they load); the town owns them, so the speaker and this panel agree. */
  settings: TownSettingsDto | null;
  onSave: (next: TownSettingsDto) => Promise<ActionResultDto>;
  onClose: () => void;
}

/** Agent Town settings panel. Every change saves the whole set; the daemon's answer is shown under it. */
export function TownSettings({ settings, onSave, onClose }: Props) {
  const [note, setNote] = useState<{ ok: boolean; text: string } | null>(null);
  const [saving, setSaving] = useState(false);

  const toggle = async (key: keyof TownSettingsDto) => {
    if (!settings || saving) return;
    setSaving(true);
    const r = await onSave({ ...settings, [key]: !settings[key] });
    setSaving(false);
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
      {!settings && <p className="town-settings-note">Loading…</p>}
      {settings &&
        SWITCHES.map((s) => (
          <label key={s.key} className="town-switch">
            <input
              type="checkbox"
              aria-label={s.label}
              aria-describedby={'town-note-' + s.key}
              checked={settings[s.key]}
              disabled={saving}
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
