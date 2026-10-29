# Agent Town, Plan 5: Game elements Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** The town feels like a small RPG: a boss character you walk along the street, XP for you and for each repo, buildings that grow with their repo's level, workers that walk in and away, and a reward when a quest is done, without ever slowing real work.

**Architecture:** The daemon awards XP (`TownProgress` in Core, saved to `town-progress.json` in the data directory) when the user answers a prompt from the town, when a town message gets a session working again, and when a repo's session finishes a turn. It serves `GET /api/town/progress` and publishes a `progress` SSE event with the award. The web keeps progress in the store, the renderer draws buildings by level and animates workers walking, a pure `boss.ts` moves the boss, and `TownView` shows XP pop-ups and plays the reward sound.

**Tech Stack:** .NET 10, xUnit; React 19, TypeScript, Vitest, Web Audio.

**Spec:** `docs/superpowers/specs/2026-10-27-agent-town-design.md` ("Game elements", "Built so far").

**Series:** Plan 5 of 5. Plans 1 to 4 are merged.

## Global Constraints

- Every game action has a fast path. No cooldowns, energy, penalties or forced walking. Clicking a worker opens its box at once; the boss runs over in the background. Drag, wheel and the minimap still pan the street directly.
- Player XP: answering a permission prompt from the town = 10, plus 10 more when answered within 30 s of the prompt appearing. A message sent from the town that gets a session working again within 2 minutes = 5.
- Repo XP: a session of that repo finishing a turn (`Stop`) = 10. Temporary lots (no RepoManager project) earn nothing.
- Levels (player and repo): level `n` starts at `25 * (n - 1) * n` XP (1: 0, 2: 50, 3: 150, 4: 300, 5: 500 …).
- Buildings: repo level 1 = shack (lower, straw roof), 2–3 = house (today's building), 4+ = tower (an extra floor). Decorations: flower boxes under the windows from level 3, a flag on the roof from level 5. Lots with no session keep their smaller, dimmer look whatever the level.
- Workers do not level.
- XP survives restarts (`town-progress.json` next to `registry.json`); a corrupt or missing file starts from zero, never crashes.
- Boss: walks with ArrowLeft/ArrowRight at 90 scene px per second while held (no pan by arrows any more); the camera follows to keep the boss on screen. Enter next to a worker (within 14 scene px) opens its box. Home re-sorts and moves boss and camera to the left end.
- Quest completed (a quest leaves the quest log because the user answered or sent from the town): reward sound (three rising square-wave notes) and an XP pop-up ("+20 XP") above the boss for 1.5 s. Sound obeys the Sounds setting.
- Reduced motion: no walking animation (workers and boss jump to their spots), pop-ups appear without floating.
- Player level and XP show in the town's top bar ("Lv 3 · 180 XP").

## Review Focus

1. **XP file written while RepoManager quits, or corrupt.** Expected: written atomically (temp file + move); a bad file loads as zero. Test in Task 1.
2. **Answering a prompt that was already released.** Expected: no XP (only successful town answers count). Test in Task 1.
3. **Arrow keys while the dialogue or an input has focus.** Expected: the boss does not move. Test in Task 4.
4. **Many sessions ending at once.** Expected: each worker walks away once; removed after 1.5 s; no crash when a lot disappears with them. Test in Task 3.
5. **A repo renamed or removed.** Expected: its XP stays in the file under the old name; no crash; a new repo starts at zero. Test in Task 1.

---

### Task 1: Progress and awards in the daemon

**Files:** Create `src/RepoManager.Core/Agents/TownProgress.cs` (+ `tests/RepoManager.Tests/TownProgressTests.cs`); modify `AgentDtos.cs`, `PermissionBroker.cs` (prompt age), `DaemonHost.cs`, `EventHub.cs`, `Api.cs`; tests in `TownApiTests.cs`.

**Interfaces:**
- Contracts: `sealed record LevelDto(int Xp, int Level, int LevelStart, int NextLevel);`, `sealed record TownProgressDto(LevelDto Player, IReadOnlyDictionary<string, LevelDto> Repos);`, `sealed record XpAwardDto(string Kind, int Amount, string? SessionId, string? Repo);` (`Kind`: `answer`, `send`, `turn`). SSE `{"type":"progress","payload":{"progress":TownProgressDto,"award":XpAwardDto}}`.
- Core: `TownProgress(string dataDir, Func<DateTimeOffset> clock)` with `static LevelDto LevelOf(int xp)`, `XpAwardDto? Answered(string sessionId, TimeSpan promptAge)`, `void Sent(string sessionId)`, `XpAwardDto? SessionChanged(AgentSessionDto before, AgentSessionDto after)` (turn → repo XP; working within 2 min of `Sent` → send XP, once), `TownProgressDto Snapshot()`, `event Action<TownProgressDto, XpAwardDto>? Awarded`.
- `PermissionBroker.AgeOf(string requestId): TimeSpan?` (time since the prompt was added).
- Daemon: `/town/permission/answer` awards on success; `/town/send` calls `Sent` on success; registry `Changed` feeds `SessionChanged` (the registry keeps the previous DTO; raise `Changed` with both or keep a last-seen map in `TownProgress`). `GET /api/town/progress`.
- [ ] Tests: `LevelOf` boundaries (0, 49, 50, 149, 150, 300); answer within 30 s = 20, after = 10; released answer = nothing (API test); send then working within 2 min = 5 once, after 2 min = 0; Stop for a project = repo +10, temporary lot = 0; persistence round trip; corrupt file → zero; atomic write leaves no `.tmp`; renamed repo keeps old entry.
- [ ] Commit `feat(town): XP for the player and each repo`.

### Task 2: Progress in the web store

**Files:** `web/src/api/types.ts`, `client.ts`, `state/reducer.ts`, `state/actions.ts`, `App.tsx`; create `web/src/lib/town/progress.ts` (+ tests).

**Interfaces:** `AppState.progress: TownProgressDto | null`; `AppState.awards: XpAwardDto[]` (last 10, newest last, each with a client `at` timestamp); actions `progressLoaded`, `progress` (with award); `loadProgress()` on open/reconnect and with agents; `levelText(l: LevelDto): string` (`"Lv 3 · 180 XP"`); `buildingTier(level: number): 'shack' | 'house' | 'tower'`; `decorations(level): { flowers: boolean; flag: boolean }`.
- [ ] Tests: reducer keeps 10 awards; `levelText`; `buildingTier(1/2/3/4)`; `decorations(2/3/5)`.
- [ ] Commit `feat(town): progress in the web store`.

### Task 3: Buildings by level, walking workers

**Files:** `web/src/lib/town/render.ts` (+ tests).

**Interfaces:** `TownFrame` gains `levels: Record<string, number>` (lot key → repo level; missing = 1). `TownScene.update` keeps each worker's position: new workers start at the nearer screen edge of the street and walk to their spot at 38 px/s; workers whose session ended keep walking to the nearer edge and are dropped after 1.5 s (drawn from the scene's own memory, not from `lots`). Reduced motion: jump to the spot, drop at once.
- [ ] Tests (recording fake ctx): a level-1 lot draws a lower building than level 2; level 4 draws higher than level 2; flowers at level 3 (a fill with the flower colour), flag at level 5; a new worker's first frame is not at its spot, it reaches it after enough `update` time; an ended worker is still drawn 0.5 s later and gone after 1.6 s; ten ended at once do not throw (Review Focus 4).
- [ ] Commit `feat(town): buildings grow with their repo, workers walk in and away`.

### Task 4: The boss

**Files:** Create `web/src/lib/town/boss.ts` (+ tests); modify `render.ts` (draw the boss), `TownView.tsx` (+ tests).

**Interfaces:** `interface Boss { x: number; target: number | null }`; `stepBoss(b: Boss, dir: -1 | 0 | 1, dt: number, world: number): Boss` (speed 90 px/s, clamped to the street; a `target` makes it run there at 120 px/s when `dir` is 0); `followCamera(camX: number, bossX: number, view: number, world: number): number` (keeps the boss at least 40 px inside the view); `nearestWorker(lots, bossX, maxDist = 14)`.
- TownView: ArrowLeft/Right held move the boss (keydown starts, keyup stops), not the camera; Enter opens `nearestWorker`; clicking a worker opens its box and sets the boss's `target`; Home also moves the boss to the left end. All ignored while typing or while a box is open (Review Focus 3).
- [ ] Tests: `stepBoss` speed and clamping, running to a target; `followCamera`; `nearestWorker` within and beyond 14 px; TownView: arrow moves the boss (camera follows past the edge), Enter next to a worker opens it, arrows in the message box do nothing.
- [ ] Commit `feat(town): a boss to walk the street`.

### Task 5: Rewards and the level in the top bar

**Files:** `TownView.tsx` (+ tests), create `web/src/lib/town/reward.ts` (+ tests), `styles.css`.

**Interfaces:** `playReward(ctx: AudioContext)` (notes 523, 659, 784 Hz, 70 ms each, square, gain 0.08); TownView shows each new award from `state.awards` as a pop-up ("+20 XP") above the boss for 1.5 s and plays the reward sound for `answer` and `send` awards when the Sounds setting is on; top bar shows `levelText(progress.player)`.
- [ ] Tests: a new award shows "+20 XP" and disappears after 1.5 s (fake timers); no sound with Sounds off (AudioContext stub not created); the top bar shows "Lv 2 · 60 XP".
- [ ] Commit `feat(town): rewards and the player's level`.

### Task 6: Live check

- [ ] Ask to reinstall (`install.ps1`); proceed only with approval.
- [ ] Walk the boss, Enter on a worker, click a far worker (box opens at once, boss runs).
- [ ] Answer a prompt from the town: "+20 XP" pop-up and the reward sound; top bar level updates; XP survives a RepoManager restart.
- [ ] A session finishing grows its repo's XP; buildings show their tier.
- [ ] Start and end a session: worker walks in and away.
