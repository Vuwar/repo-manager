#!/usr/bin/env node
'use strict';
// Claude Code hook. Reads the hook JSON on stdin and forwards a tiny event to the
// Agent Town server. It prints nothing and always exits 0, so it never blocks Claude.
const http = require('http');
const path = require('path');

const PORT = Number(process.env.AGENT_TOWN_PORT) || 4747;
const NEEDS_YOU = new Set(['permission_prompt', 'elicitation_dialog', 'elicitation_url_dialog', 'agent_needs_input']);

function clip(v, n) {
  if (typeof v !== 'string') return '';
  const line = v.replace(/\s+/g, ' ').trim();
  return line.length > n ? line.slice(0, n - 1) + '…' : line;
}

let raw = '';
process.stdin.setEncoding('utf8');
process.stdin.on('data', (d) => {
  raw += d;
});
process.stdin.on('end', () => {
  let input;
  try {
    input = JSON.parse(raw);
  } catch (e) {
    process.exit(0);
  }
  const id = String(input.session_id || '').replace(/[^\w.-]/g, '').slice(0, 80);
  if (!id) process.exit(0);
  const repo = path.basename(input.cwd || '') || 'unknown';

  let state = null;
  let msg = '';
  switch (input.hook_event_name) {
    case 'SessionStart':
      state = 'idle';
      msg = 'Session started';
      break;
    case 'UserPromptSubmit':
      state = 'working';
      msg = clip(input.prompt, 90) || 'Working on a new prompt';
      break;
    case 'PostToolUse':
      state = 'working';
      msg = input.tool_name ? 'Used ' + input.tool_name : '';
      break;
    case 'Notification':
      if (input.notification_type && !NEEDS_YOU.has(input.notification_type)) process.exit(0);
      state = 'waiting';
      msg = clip(input.message, 90) || 'Needs your attention';
      break;
    case 'Stop':
      state = 'done';
      msg = clip(input.last_assistant_message, 90) || 'Finished its turn';
      break;
    case 'SessionEnd':
      state = 'end';
      break;
    default:
      process.exit(0);
  }

  const body = JSON.stringify({ id, repo, state, msg });
  const req = http.request(
    {
      host: '127.0.0.1',
      port: PORT,
      path: '/event',
      method: 'POST',
      headers: { 'Content-Type': 'application/json', 'Content-Length': Buffer.byteLength(body) },
      timeout: 800,
    },
    (res) => {
      res.resume();
      res.on('end', () => process.exit(0));
    }
  );
  req.on('timeout', () => {
    req.destroy();
    process.exit(0);
  });
  req.on('error', () => process.exit(0));
  req.end(body);
});
