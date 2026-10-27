# Agent Town

A pixel-art town that shows what all your Claude Code sessions are doing.
Each repo is a building, each session is a worker, and a beacon on the roof shows the state.

| State | What you see |
| --- | --- |
| Needs you | Worker waves at the door with a "!" bubble, roof beacon flashes amber |
| Working | Worker hammers at an anvil, chimney smoke, blue screens in the windows |
| Done | Worker jumps, confetti, green beacon, check bubble |
| Failed | Roof on fire, black smoke, worker panics |
| Idle | Worker sleeps on the road |

## Run it

1. Put this folder at `~/agent-town` (or anywhere, then edit the path in `hooks.json`).
2. Start the server: `node server.js` (Node 18+, no packages needed).
3. Merge `hooks.json` into `~/.claude/settings.json`. If you already have a `hooks` section, add these events to it.
4. Open http://localhost:4747 and start Claude Code in any repo.

The page opens in Live mode when served from localhost. The Demo button runs a fake town so you can preview it.

## How it works

`hook.js` runs on Claude Code hook events, reads the session id and working folder from stdin, and posts a small event to `server.js`. The server keeps the current state of every session and streams changes to the page over server-sent events.

| Hook event | Town state |
| --- | --- |
| SessionStart | idle |
| UserPromptSubmit, PostToolUse | working |
| Notification (permission or question) | waiting |
| Stop | done |
| SessionEnd | worker walks away |

Idle notifications are ignored, so a finished session stays "done" instead of flipping to "needs you".

Claude Code has no hook for "something failed", so the failed state is only reachable by hand for now:

```
curl -X POST localhost:4747/event -d '{"id":"test","repo":"demo","state":"failed","msg":"Build broke"}'
```

## Notes

- The server only listens on 127.0.0.1 and rejects requests with a non-local Host header.
- Up to 6 repos get a building. Extra repos share a building with an existing one.
- Set `PORT` for the server and `AGENT_TOWN_PORT` for `hook.js` to use a different port.
- The Sound button plays a bell when a session needs you.
