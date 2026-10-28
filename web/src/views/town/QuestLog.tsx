import { waitText, type Quest } from '../../lib/town/quests';
import { STATE_COLOR } from '../../lib/town/render';

/** Everything waiting on the user (key Q). Clicking a quest opens that worker's dialogue. */
export function QuestLog({ quests, now, onOpen, onClose }: { quests: Quest[]; now: number; onOpen: (sessionId: string) => void; onClose: () => void }) {
  return (
    <aside className="quest-log" aria-label="Quest log">
      <header>
        <span>Quests</span>
        <button type="button" onClick={onClose} aria-label="Close quest log">
          ×
        </button>
      </header>
      {quests.length === 0 ? (
        <p className="quest-empty">Nothing is waiting on you.</p>
      ) : (
        <ul>
          {quests.map((q) => (
            <li key={q.sessionId}>
              <button
                type="button"
                onClick={() => onOpen(q.sessionId)}
                style={{ ['--c' as string]: q.kind === 'needsYou' ? STATE_COLOR.waiting : STATE_COLOR.done }}
              >
                <span className="quest-title">{q.title}</span>
                <span className="quest-wait">{waitText(now - q.since)}</span>
              </button>
            </li>
          ))}
        </ul>
      )}
    </aside>
  );
}
