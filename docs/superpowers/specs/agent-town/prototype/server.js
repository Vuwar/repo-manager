#!/usr/bin/env node
'use strict';
// Agent Town server. No dependencies.
//   GET  /          the town (index.html)
//   GET  /events    server-sent events: a snapshot, then live updates
//   POST /event     {"id","repo","state","msg"}  state: idle|working|waiting|done|failed|end
const http = require('http');
const fs = require('fs');
const path = require('path');

const PORT = Number(process.env.PORT) || 4747;
const STATES = new Set(['idle', 'working', 'waiting', 'done', 'failed']);
const STALE_MS = 8 * 60 * 60 * 1000;
const sessions = new Map();
const clients = new Set();

function send(res, obj) {
  res.write('data: ' + JSON.stringify(obj) + '\n\n');
}
function broadcast(obj) {
  for (const c of clients) send(c, obj);
}
function publicSession(s) {
  return { id: s.id, repo: s.repo, state: s.state, msg: s.msg, since: s.since };
}

function handleEvent(e) {
  if (!e || typeof e.id !== 'string' || !/^[\w.-]{1,80}$/.test(e.id)) return false;
  if (e.state === 'end') {
    if (sessions.delete(e.id)) broadcast({ type: 'end', id: e.id });
    return true;
  }
  if (!STATES.has(e.state)) return false;
  const prev = sessions.get(e.id);
  const repo = typeof e.repo === 'string' && e.repo.trim() ? e.repo.trim().slice(0, 60) : (prev ? prev.repo : 'unknown');
  const msg = typeof e.msg === 'string' && e.msg.trim() ? e.msg.trim().slice(0, 140) : (prev ? prev.msg : '');
  const s = {
    id: e.id,
    repo,
    state: e.state,
    msg,
    since: prev && prev.state === e.state ? prev.since : Date.now(),
    touched: Date.now(),
  };
  sessions.set(e.id, s);
  broadcast(Object.assign({ type: 'event' }, publicSession(s)));
  return true;
}

const server = http.createServer((req, res) => {
  const hostOk = /^(localhost|127\.0\.0\.1|\[::1\])(:\d+)?$/.test(req.headers.host || '');
  if (!hostOk) {
    res.writeHead(403);
    return res.end();
  }
  const url = req.url.split('?')[0];

  if (req.method === 'GET' && url === '/events') {
    res.writeHead(200, {
      'Content-Type': 'text/event-stream',
      'Cache-Control': 'no-cache',
      Connection: 'keep-alive',
    });
    res.write('retry: 2000\n\n');
    send(res, { type: 'snapshot', sessions: Array.from(sessions.values()).map(publicSession) });
    clients.add(res);
    req.on('close', () => clients.delete(res));
    return;
  }

  if (req.method === 'POST' && url === '/event') {
    let body = '';
    req.on('data', (d) => {
      body += d;
      if (body.length > 65536) req.destroy();
    });
    req.on('end', () => {
      let ok = false;
      try {
        ok = handleEvent(JSON.parse(body));
      } catch (err) {
        ok = false;
      }
      res.writeHead(ok ? 204 : 400);
      res.end();
    });
    return;
  }

  if (req.method === 'GET' && (url === '/' || url === '/index.html')) {
    fs.readFile(path.join(__dirname, 'index.html'), (err, buf) => {
      if (err) {
        res.writeHead(500);
        return res.end('index.html not found next to server.js');
      }
      res.writeHead(200, { 'Content-Type': 'text/html; charset=utf-8', 'Cache-Control': 'no-cache' });
      res.end(buf);
    });
    return;
  }

  res.writeHead(404);
  res.end();
});

setInterval(() => {
  for (const c of clients) c.write(': ping\n\n');
}, 25000).unref();

setInterval(() => {
  const cutoff = Date.now() - STALE_MS;
  for (const s of Array.from(sessions.values())) {
    if (s.touched < cutoff) handleEvent({ id: s.id, state: 'end' });
  }
}, 60000).unref();

server.listen(PORT, '127.0.0.1', () => {
  console.log('Agent Town is running at http://localhost:' + PORT);
});
