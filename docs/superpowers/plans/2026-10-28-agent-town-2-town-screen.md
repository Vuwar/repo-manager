# Agent Town, Plan 2: Town screen (read-only) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** A full-screen "town" mode in the RepoManager window shows every project as a building on one scrolling street, every Claude session as a worker in front of it, sorted by urgency, with a minimap, edge arrows, labels and "(N) RepoManager" in the title.

**Architecture:** The web UI loads `/api/town/sessions` and follows the `agent` / `agentEnd` SSE events into the existing store. Pure modules in `web/src/lib/town/` turn projects plus sessions into an ordered list of lots (`street.ts`), hold that order while the pointer is over the town (`hold.ts`), and do camera, minimap and edge-arrow math (`camera.ts`). `render.ts` draws one frame on a canvas, ported from the prototype's pixel art. `TownView.tsx` puts them together with HTML labels on top. The WinForms shell maximises the window in town mode and toggles borderless full screen on F11.

**Tech Stack:** React 19, TypeScript, Vite, Vitest + Testing Library (jsdom); .NET 10 WinForms + WebView2 for the shell.

**Spec:** `docs/superpowers/specs/2026-10-27-agent-town-design.md`

**Series:** Plan 2 of 5. Plan 1 (backend) is merged: `GET /api/town/sessions` returns `AgentSessionDto[]`; SSE sends `{"type":"agent","payload":AgentSessionDto}` and `{"type":"agentEnd","payload":{"id":"..."}}`. Plan 3 adds the dialogue box, plan 4 focus reporting and notifications, plan 5 game elements. This plan is read-only: no clicks on workers, no sending, no answering.

## Global Constraints

- Nothing may block or delay real work. The town only reads; it never calls `/api/town/send`, `/permission/answer` or `/focus` in this plan.
- Layout is option B: one long horizontal street. Every RepoManager project is a building. Projects with no Claude session are included, smaller and dimmer, with lit windows while any of their dev servers runs.
- Order, left to right: repos with a session that needs the user, then working, then done, then idle sessions, then repos with no session. Within a group, longest-waiting first.
- Re-sorting is deferred while the pointer is over the town; it applies about 3 seconds after the pointer leaves, or immediately on Home.
- The camera starts at the left end. Pan by drag, wheel and arrow keys; Home returns to the left end.
- Minimap strip at the bottom: one dot per repo, coloured by state; clicking a dot jumps there. Edge arrows show off-screen counts such as "2 need you →".
- Several sessions in a repo means several workers. A worktree session stands at the main repo's building with a small sign showing the worktree name.
- A session whose folder is not a RepoManager project gets a temporary building named after the folder, marked "not in RepoManager", placed by the same sort rules; it disappears when its last session ends.
- The window title shows "(N) RepoManager" when N sessions need the user (in every mode, not only the town).
- States and visuals are the prototype's (`docs/superpowers/specs/agent-town/prototype/index.html`): needs you (waves at the door, `!` bubble, amber beacon), working (hammer at anvil, smoke), done (jump, confetti, green), failed (roof on fire), idle (sleeps). Day and night cycle stays. Colours: waiting `#ffc857`, failed `#ff6b5e`, working `#6cc3ff`, done `#7ee081`, idle `#8790c2`.
- The town is a fourth `UiMode`, `town`, of the existing main window. Entering it maximises the window; F11 toggles borderless full screen; leaving restores the previous bounds, like the Mini view.
- Honour `prefers-reduced-motion: reduce`: no day/night drift, no particles, workers hold one pose.
- Persist only per-viewer conveniences in `localStorage` (the mode), wrapped in try/catch like `simple.ts`.

## Review Focus

1. **Session for a project that is not (or no longer) in the project list** (project removed, or `projects` not loaded yet). Expected: it still gets a lot, named after `project` (or the folder), marked "not in RepoManager"; nothing crashes. Test in Task 2.
2. **Two unregistered folders with the same name in different paths** (`C:\a\tool`, `D:\b\tool`). Expected: two separate temporary buildings, keyed by the full cwd, both labelled "tool". Test in Task 2.
3. **Events while the pointer is over the town** (a new session starts, a lot loses its last session). Expected: existing lots keep their place, new lots appear at the right end, removed lots disappear at once; the full re-sort applies 3 s after leaving or on Home. Test in Tasks 3 and 6.
4. **Street narrower than the screen, and window resizes.** Expected: camera never goes negative or past the end; with a short street the camera stays at 0. Test in Task 4.
5. **SSE drops and reconnects.** Expected: the session list is reloaded and replaced, so sessions that ended during the drop disappear. Test in Task 1.

---

## File Structure

- Modify `web/src/api/types.ts`: `AgentSessionDto`, `PermissionPromptDto`, `AgentState`; `EventDto` gains `agent` and `agentEnd`.
- Modify `web/src/api/client.ts`: `api.townSessions()`.
- Modify `web/src/state/reducer.ts`, `web/src/state/actions.ts`: `agents` slice, `loadAgents()`, event handling.
- Create `web/src/lib/town/street.ts`: lots, groups, ordering, needs-you count, window title.
- Create `web/src/lib/town/hold.ts`: order held during hover.
- Create `web/src/lib/town/camera.ts`: world geometry, camera clamp, pan, jump, minimap, edge counts.
- Create `web/src/lib/town/render.ts`: canvas frame drawing (ported art) and per-session animation state.
- Create `web/src/views/TownView.tsx`: the screen.
- Modify `web/src/lib/simple.ts` (`UiMode`), `web/src/lib/host.ts` (`postFullscreen`), `web/src/App.tsx` (route, title, agent loading), `web/src/views/SimpleView.tsx` and `web/src/components/Sidebar.tsx` (Town button), `web/src/components/Icon.tsx` (`town` icon), `web/src/styles.css` (town styles).
- Modify `src/RepoManager.App/Shell/MainForm.cs`: town mode window handling.
- Tests next to the code: `street.test.ts`, `hold.test.ts`, `camera.test.ts`, `render.test.ts`, `TownView.test.tsx`, additions to `reducer.test.ts`, `actions.test.ts`, `simple.test.ts`, `App.test.tsx`.

---

### Task 1: Session data in the web store, and the window title

**Files:**
- Modify: `web/src/api/types.ts`, `web/src/api/client.ts`, `web/src/state/reducer.ts`, `web/src/state/actions.ts`, `web/src/App.tsx`
- Create: `web/src/lib/town/street.ts` (only `needsYouCount` and `windowTitle` in this task)
- Test: `web/src/state/reducer.test.ts`, `web/src/state/actions.test.ts`, `web/src/lib/town/street.test.ts`, `web/src/App.test.tsx`

**Interfaces:**
- Produces (types, mirroring `src/RepoManager.Contracts/AgentDtos.cs` in camelCase):
  - `type AgentState = 'idle' | 'working' | 'waiting' | 'done' | 'failed'`
  - `interface PermissionPromptDto { requestId: string; toolName: string; summary: string; answerable: boolean }`
  - `interface AgentSessionDto { id: string; project?: string; worktree?: string; folder: string; cwd: string; state: AgentState; message: string; prompt?: string; lastReply?: string; surface: 'desktop' | 'terminal' | 'other'; hostSessionId?: string; since: string; touched: string; canSend: boolean; prompts: PermissionPromptDto[] }`
  - `EventDto` adds `| { type: 'agent'; payload: AgentSessionDto } | { type: 'agentEnd'; payload: { id: string } }`
- Produces (client): `api.townSessions(): Promise<AgentSessionDto[]>` (`GET /api/town/sessions`).
- Produces (state): `AppState.agents: Record<string, AgentSessionDto>` (initially `{}`); actions `{ type: 'agentsLoaded'; agents: AgentSessionDto[] }`, `{ type: 'agent'; agent: AgentSessionDto }`, `{ type: 'agentEnd'; id: string }`; `loadAgents(): Promise<void>` in `actions.ts`.
- Produces (street.ts): `needsYouCount(agents: Record<string, AgentSessionDto>): number` (sessions with `state === 'waiting'`), `windowTitle(n: number): string`.

- [ ] **Step 1: Write failing tests.**
  - `reducer.test.ts`: `agentsLoaded replaces the whole map` (load `[a, b]`, then `[b]`: only `b` remains); `agent upserts by id`; `agentEnd removes`; `agentEnd for unknown id returns the same state object`.
  - `actions.test.ts`: `handleEvent dispatches agent and agentEnd` (state reflects them).
  - `street.test.ts`: `needsYouCount counts waiting only` (one each of waiting, working, done, failed, plus a second waiting → 2); `windowTitle(0) === 'RepoManager'`, `windowTitle(3) === '(3) RepoManager'`.
  - `App.test.tsx`: `title shows the needs-you count` (dispatch two waiting agents → `document.title === '(2) RepoManager'`; dispatch `agentEnd` for one → `'(1) RepoManager'`). Mock `api.townSessions` the way the file mocks other api calls.
- [ ] **Step 2: Run** `cd web && npx vitest run src/state src/lib/town src/App.test.tsx`. Expected: FAIL (missing types and functions).
- [ ] **Step 3: Implement.** Reducer cases follow the existing `status` case style. `handleEvent` gets `case 'agent'` and `case 'agentEnd'`. `loadAgents` calls `api.townSessions()` and dispatches `agentsLoaded`; on error it does nothing (the town shows what it has). In `App.tsx` `Shell`: call `loadAgents()` in the initial-load effect and in `onOpen` when `reconnected`; add `const waiting = useAppState((s) => needsYouCount(s.agents))` and an effect `document.title = windowTitle(waiting)`. The shell already copies `document.title` into the window title (`MainForm.cs:83`).
- [ ] **Step 4: Run** the same command. Expected: PASS. Then `npx vitest run` and `npx tsc -b`: all pass.
- [ ] **Step 5: Commit** `feat(town): web store follows Claude sessions; title shows needs-you count`.

### Task 2: Street model

**Files:**
- Modify: `web/src/lib/town/street.ts`
- Test: `web/src/lib/town/street.test.ts`

**Interfaces:**
- Consumes: `AgentSessionDto`, `AgentState` (Task 1), `ProjectDto`, `isActive` from `lib/format`.
- Produces:
  - `type LotGroup = 'waiting' | 'failed' | 'working' | 'done' | 'idle' | 'none'`
  - `const GROUP_ORDER: LotGroup[] = ['waiting', 'failed', 'working', 'done', 'idle', 'none']`
  - `interface Lot { key: string; name: string; temporary: boolean; group: LotGroup; since: number | null; workers: AgentSessionDto[]; serversUp: boolean }`
  - `buildStreet(projects: ProjectDto[] | null, agents: Record<string, AgentSessionDto>): Lot[]` (already sorted)
  - `lotKeyFor(agent: AgentSessionDto, projectNames: Set<string>): string`

Rules (decisions the spec leaves to the builder are marked *ruling*):
- Lot key: `project:<name lowercased>` for a project; `folder:<cwd lowercased, trailing \ or / removed>` for a session with no `project`. A session whose `project` is set but is not in `projects` gets `project:<name>` and is a temporary lot named after `project` (*ruling*: the daemon resolved it, so the name is right; it is just not loaded or was removed).
- A worktree session (`worktree` set) belongs to its project's lot (`Lot.workers` keeps it; Task 6 draws the sign).
- `group` of a lot = the most urgent state among its workers, by `GROUP_ORDER`; `'none'` when it has no workers. *Ruling*: `failed` sorts right after `waiting`, as in the prototype's priorities (spec lists failed visuals but not its order).
- `since` = the earliest `Date.parse(worker.since)` among the workers in the lot's group; `null` for `'none'`.
- Sort: by `GROUP_ORDER` index, then `since` ascending (longest-waiting first), then `name` case-insensitive. `'none'` lots sort by name.
- `serversUp` = any service in any instance of the project `isActive`.
- `workers` sorted by the same group order, then `since`.

- [ ] **Step 1: Write failing tests** (use `project()`, `instance()`, `svc()` from `test/fixtures.ts`; add an `agent(id, over)` fixture there with defaults `{ folder: 'demo', cwd: 'C:\\r\\demo', state: 'idle', message: '', surface: 'desktop', since: '2026-10-28T10:00:00Z', touched: same, canSend: false, prompts: [] }`):
  - `orders groups waiting, failed, working, done, idle, then no session`
  - `within a group, longest waiting first` (two waiting lots, since 10:05 and 10:01 → the 10:01 one first)
  - `projects without sessions are included, sorted by name, serversUp from running services`
  - `lot group is its most urgent worker` (one working + one waiting worker → `waiting`, both in `workers`, waiting first)
  - `worktree session joins the main project lot`
  - `session for unknown project gets a temporary lot named after the project` (Review Focus 1; also with `projects === null`)
  - `unregistered folders with the same name stay separate` (Review Focus 2: cwds `C:\\a\\tool` and `D:\\b\\tool` → two lots, both `name === 'tool'`, `temporary === true`)
  - `temporary lot disappears with its last session` (build with the agent, then without: no lot)
- [ ] **Step 2: Run** `npx vitest run src/lib/town/street.test.ts`. Expected: FAIL.
- [ ] **Step 3: Implement** `buildStreet` and `lotKeyFor`.
- [ ] **Step 4: Run** the same. Expected: PASS.
- [ ] **Step 5: Commit** `feat(town): street model sorted by urgency`.

### Task 3: Order held during hover

**Files:**
- Create: `web/src/lib/town/hold.ts`
- Test: `web/src/lib/town/hold.test.ts`

**Interfaces:**
- Produces: `heldOrder(shown: string[], wanted: string[], hold: boolean): string[]` and `const RESORT_DELAY_MS = 3000`.

Rule: when `hold` is false, return `wanted`. When true, keep the keys of `shown` that are still in `wanted`, in `shown`'s order, then append keys of `wanted` that are not in `shown`, in `wanted`'s order. Return `shown` itself (same array) when nothing changed, so React can skip work.

- [ ] **Step 1: Write failing tests:**
  - `not holding: takes the wanted order`
  - `holding: keeps the shown order` (`shown ['a','b','c']`, `wanted ['c','a','b']` → `['a','b','c']`)
  - `holding: new keys go to the right end, removed keys disappear` (`shown ['a','b']`, `wanted ['c','b']` → `['b','c']`)
  - `holding with no change returns the same array` (`toBe`)
- [ ] **Step 2: Run** `npx vitest run src/lib/town/hold.test.ts`. Expected: FAIL.
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run** the same. Expected: PASS.
- [ ] **Step 5: Commit** `feat(town): hold the street order while the pointer is over it`.

### Task 4: Camera, minimap and edge arrows

**Files:**
- Create: `web/src/lib/town/camera.ts`
- Test: `web/src/lib/town/camera.test.ts`

**Interfaces:**
- Consumes: `Lot`, `LotGroup` (Task 2).
- Produces (all in logical pixels; the scene is 180 px high like the prototype):
  - `const SCENE_H = 180, GROUND = 140, SLOT0 = 12, SLOT_W = 51, BW = 40, SMALL_BW = 28`
  - `lotX(index: number): number` = `SLOT0 + index * SLOT_W` (left edge of building)
  - `worldWidth(lotCount: number): number` = `SLOT0 * 2 + lotCount * SLOT_W`
  - `scaleFor(viewHeightPx: number): number` = `Math.max(1, Math.floor(viewHeightPx / SCENE_H))`
  - `clampCamera(x: number, world: number, view: number): number` (0 when `world <= view`, else within `[0, world - view]`)
  - `cameraToShow(index: number, world: number, view: number): number` (centres lot `index`, clamped)
  - `visibleRange(camX: number, view: number, lotCount: number): [first: number, last: number]` (inclusive, lots at least partly on screen)
  - `interface EdgeHint { side: 'left' | 'right'; group: LotGroup; count: number; target: number }`
  - `edgeHints(lots: Lot[], camX: number, view: number): EdgeHint[]`
  - `edgeText(h: EdgeHint): string`
  - `const ARROW_PAN = 24` (logical px per arrow-key press), and `WHEEL_FACTOR = 0.5`

Edge rule (*ruling*): per side, look at lots wholly off that side. Pick the most urgent group among `waiting`, `failed`, `working`, `done` present there; `count` = number of workers in that group on that side; `target` = index of the nearest such lot. No hint for `idle`/`none`. Text: waiting `"N need you"` (singular `"1 needs you"`), failed `"N failed"`, working `"N working"`, done `"N done"`; right side ends `" →"`, left side starts `"← "`.

- [ ] **Step 1: Write failing tests:**
  - `world narrower than the view: camera stays 0` (Review Focus 4: `clampCamera(50, 200, 400) === 0`)
  - `camera clamps to both ends` (`clampCamera(-10, 1000, 300) === 0`, `clampCamera(900, 1000, 300) === 700`)
  - `cameraToShow centres the lot` (index 10 → `lotX(10) + BW/2 - view/2`, clamped)
  - `visibleRange covers partly visible lots`
  - `edge hints count off-screen workers of the most urgent group` (two waiting workers in one lot and one in another, all off the right: `{ side: 'right', group: 'waiting', count: 3, target: nearest }`; `edgeText` → `"3 need you →"`)
  - `edge hint text for one waiting worker on the left is "← 1 needs you"`
  - `no hint for idle or empty lots`
  - `scaleFor(1080) === 6`, `scaleFor(100) === 1`
- [ ] **Step 2: Run** `npx vitest run src/lib/town/camera.test.ts`. Expected: FAIL.
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run** the same. Expected: PASS.
- [ ] **Step 5: Commit** `feat(town): camera, minimap and edge-arrow geometry`.

### Task 5: Canvas renderer

**Files:**
- Create: `web/src/lib/town/render.ts`
- Test: `web/src/lib/town/render.test.ts`

**Interfaces:**
- Consumes: `Lot` (Task 2), geometry constants and `lotX`, `visibleRange` (Task 4).
- Produces:
  - `type Draw2D = Pick<CanvasRenderingContext2D, 'fillRect' | 'clearRect' | 'save' | 'restore' | 'beginPath' | 'rect' | 'clip' | 'translate' | 'fillStyle'>` (the subset the art uses; lets tests pass a recording fake)
  - `interface TownFrame { lots: Lot[]; camX: number; viewW: number; now: number; phase: number; reducedMotion: boolean }` (`phase` 0..1 is the day/night position, as the prototype)
  - `class TownScene { constructor(reducedMotion: boolean); update(lots: Lot[], dt: number, now: number): void; draw(ctx: Draw2D, frame: TownFrame): void; particleCount(): number }`
  - `paletteFor(key: string): number` (index into the 6 prototype palettes)
  - `const STATE_COLOR: Record<'waiting' | 'failed' | 'working' | 'done' | 'idle', string>` (values in Global Constraints)

Port the prototype's art from `docs/superpowers/specs/agent-town/prototype/index.html`: colour helpers and night tint `C()` (lines 152-197), sky, hills, ground, lamps (200-260), building styles, windows, beacon, flames, lit windows (261-347), bubbles and workers (348-399), particles (400-421). Differences from the prototype:
- Draw in world coordinates translated by `-camX`; draw only lots in `visibleRange` (and the sky/ground across the view width; hills and specks repeat every 320 px).
- Building palette and roof style come from `hash(lot.key)` (prototype `hash`), so a repo keeps its look when the order changes.
- `'none'` lots: width `SMALL_BW`, wall colours mixed 35% toward `#4a5068` (dimmer), no beacon; lit windows (`#ffd36b`) when `serversUp`, dark windows otherwise.
- Temporary lots: a small sign post right of the door (drawn; the "not in RepoManager" text is HTML in Task 6).
- Workers: up to 5 per lot on the prototype's `LANE` positions (`work` lanes when working, `door` lanes otherwise); pose per state from `drawWorker`; hat/shirt colours from `hash(session.id)`. Extra workers are not drawn (Task 6 shows "+N").
- `TownScene.update` keeps per-session animation state (previous state, particle list); a change into `done` spawns confetti, `failed` dark smoke, `working` sparks and chimney smoke, as the prototype's `onState`/`update`. With `reducedMotion`, spawn nothing and freeze `phase` at 0.42.

- [ ] **Step 1: Write failing tests** with a recording fake `Draw2D` (count `fillRect` calls and record the `translate` arguments):
  - `draws only visible lots` (40 lots, camera at 0, view 320: no `fillRect` with x beyond `camX + viewW + SLOT_W` in world coordinates)
  - `translates by the camera` (`camX = 200` → a `translate(-200, 0)` call)
  - `confetti on a change to done, none with reduced motion` (`update` twice with a worker going `working` → `done`; `scene.particleCount() > 0`; a `new TownScene(true)` stays 0)
  - `same lot key keeps the same palette after reordering` (`paletteFor` equal for equal keys, in range 0..5)
- [ ] **Step 2: Run** `npx vitest run src/lib/town/render.test.ts`. Expected: FAIL.
- [ ] **Step 3: Implement** the port.
- [ ] **Step 4: Run** the same. Expected: PASS. `npx tsc -b` clean.
- [ ] **Step 5: Commit** `feat(town): pixel-art street renderer`.

### Task 6: Town view and the `town` mode

**Files:**
- Create: `web/src/views/TownView.tsx`, `web/src/views/TownView.test.tsx`
- Modify: `web/src/lib/simple.ts` (+ `simple.test.ts`), `web/src/lib/host.ts`, `web/src/App.tsx`, `web/src/views/SimpleView.tsx`, `web/src/components/Sidebar.tsx`, `web/src/components/Icon.tsx`, `web/src/styles.css`

**Interfaces:**
- Consumes: everything above; `postMode` from `host.ts`.
- Produces:
  - `UiMode = 'simple' | 'full' | 'mini' | 'town'`; `readMode` accepts `'town'`.
  - `postFullscreen(): void` in `host.ts` (posts `{ type: 'fullscreen' }`; the shell toggles).
  - `TownView({ onLeave }: { onLeave: () => void })`.
  - `SimpleView` and `Sidebar` get an `onTown: () => void` prop and a "Town" button (icon `town`, title `"Agent Town: every Claude session at a glance"`).
  - `App` remembers the mode the town was entered from and returns to it on leave (`'simple'` if it was entered from `'town'` on load).

Screen layout (all text is HTML over the canvas, so it stays crisp and testable):
- Canvas fills the view; device pixels = logical × `scaleFor(height)`; `imageSmoothingEnabled = false`. One `requestAnimationFrame` loop calls `scene.update` and `scene.draw`; it stops when the view unmounts or the document is hidden.
- Top bar: "Agent Town", counts per state (`2 need you · 3 working · 1 done`), and a "Leave town" button (calls `onLeave`).
- Per visible lot a label under the building: state swatch + name (CSS ellipsis past 12 characters), `"not in RepoManager"` below it for temporary lots, `"+N"` when more than 5 workers. Per worktree worker a small sign above it with the worktree name.
- Edge arrows at the left and right edges from `edgeHints`; clicking one moves the camera to `cameraToShow(target)`.
- Minimap strip at the bottom: one dot per lot coloured by `STATE_COLOR[group]` (grey `#4a5068` for `'none'`), a frame showing the visible part; clicking a dot moves the camera to `cameraToShow(index)`.
- Empty state when there are no lots: "No projects yet. Add a repo in RepoManager, or start Claude Code in any folder."
- Input: drag pans (pointer capture), wheel pans by `deltaY * WHEEL_FACTOR` (and `deltaX`), ArrowLeft/ArrowRight pan by `ARROW_PAN`, Home sets camera 0 and re-sorts at once, F11 calls `postFullscreen()` and `preventDefault`. Keys are ignored while focus is in an input.
- Order: `wanted = buildStreet(...).map(l => l.key)`; shown order via `heldOrder(shown, wanted, hovering)`. `pointerenter` sets hovering; `pointerleave` starts a `RESORT_DELAY_MS` timer that clears hovering; re-entering cancels it.
- `App.tsx`: `if (mode === 'town') return <TownView onLeave={...} />`; `postMode(mode)` already runs on every change.

- [ ] **Step 1: Write failing tests** (`TownView.test.tsx`, fixtures from Task 2; mock nothing but `state/actions` like `MiniView.test.tsx`; stub `HTMLCanvasElement.prototype.getContext` to return a recording fake):
  - `shows a label per lot in urgency order` (projects `alpha`, `beta`, `gamma`; a waiting session in `gamma`, working in `alpha` → label order `gamma, alpha, beta`)
  - `marks temporary lots` (session with no project, folder `tool` → text `not in RepoManager`)
  - `shows a worktree sign` (worktree `feat-x` → text `feat-x`)
  - `edge arrow names off-screen sessions and jumps there` (40 projects, waiting session in the last one, view width set via a stubbed `clientWidth` → button `1 needs you →`; click → that lot's label becomes visible)
  - `minimap has one dot per lot; clicking one jumps there`
  - `holds the order while hovered, re-sorts 3 s after leaving` (Review Focus 3; `vi.useFakeTimers()`: hover, dispatch a waiting session into the last lot → order unchanged; `pointerleave`, advance 2999 ms → unchanged, advance 1 ms → that lot first)
  - `Home re-sorts at once and returns to the left end`
  - `empty state with no projects and no sessions`
  - `Leave town calls onLeave`
  - `simple.test.ts`: `readMode accepts town`.
- [ ] **Step 2: Run** `npx vitest run src/views/TownView.test.tsx src/lib/simple.test.ts`. Expected: FAIL.
- [ ] **Step 3: Implement** `TownView`, the mode, the buttons, the `town` icon (a small house outline in the style of the existing icons) and the styles (dark sky background `#090e2c` behind the canvas; labels `font: 11px/1.2 ui-monospace`, white on `rgba(13,17,40,.8)`, like the prototype's `.lbl`).
- [ ] **Step 4: Run** `npx vitest run` and `npx tsc -b`. Expected: all pass.
- [ ] **Step 5: Commit** `feat(town): town screen with street, labels, minimap and edge arrows`.

### Task 7: Window handling in the shell

**Files:**
- Modify: `src/RepoManager.App/Shell/MainForm.cs` (`OnUiMessage` at `:96-129`, `SetMini` at `:131-161`)

**Interfaces:**
- Consumes: UI messages `{ type: 'mode', mode: 'simple'|'full'|'mini'|'town' }` and `{ type: 'fullscreen' }` (Task 6).

Behaviour:
- Replace `SetMini(bool)` with `SetMode(string mode)`. Leaving `mini` and entering `mini` behave exactly as today.
- Entering `town`: remember `Bounds` and `WindowState` as `_beforeTown`, then `WindowState = Maximized`.
- `fullscreen` while in `town`: toggle `FormBorderStyle.None` + `Maximized` (covers the taskbar) and back to `Sizable` + `Maximized`. Ignored in other modes.
- Leaving `town`: restore `FormBorderStyle.Sizable`, then `WindowState` and `Bounds` from `_beforeTown`.
- `town` → `mini` and `mini` → `town` go through the "leave" step first, so bounds never mix.

- [ ] **Step 1: Build** `dotnet build src/RepoManager.App`. Expected: 0 errors.
- [ ] **Step 2: Run** `dotnet test`. Expected: all pass (no shell tests exist; this guards the rest).
- [ ] **Step 3: Commit** `feat(town): shell maximises the town and toggles full screen on F11`.

There is no automated test for WinForms window state in this repo; Task 8 checks it by hand.

### Task 8: Live check

**Files:** none (verification only).

- [ ] **Step 1:** Ask the user to approve rebuilding and reinstalling (`install.ps1`; it restarts RepoManager, so running dev servers stop and come back). Do not proceed without that approval.
- [ ] **Step 2:** With several Claude sessions open, enter the town from the simple view: the window maximises, buildings are sorted waiting → working → done → idle → no session, labels match `GET /api/town/sessions`.
- [ ] **Step 3:** Title shows "(N) RepoManager" while a session waits for permission, in town and simple mode.
- [ ] **Step 4:** Drag, wheel, arrows and Home pan; minimap dots and edge arrows jump; F11 toggles full screen; "Leave town" restores the earlier window size.
- [ ] **Step 5:** Report results to the user; no commit.
