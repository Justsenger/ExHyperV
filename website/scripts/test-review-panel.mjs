import assert from 'node:assert/strict';
import http from 'node:http';
import { once } from 'node:events';
import { mkdir, mkdtemp, rm, writeFile } from 'node:fs/promises';
import os from 'node:os';
import path from 'node:path';
import test from 'node:test';
import { createReviewPanelBridge } from './review-panel.mjs';

async function fixture(t) {
  const root = await mkdtemp(path.join(os.tmpdir(), 'exhv-review-bridge-'));
  const servers = [];
  t.after(async () => {
    for (const server of servers.reverse()) {
      server.closeAllConnections();
      await new Promise(resolve => server.close(resolve));
    }
    assert.equal(path.dirname(root), path.resolve(os.tmpdir()));
    assert.ok(path.basename(root).startsWith('exhv-review-bridge-'));
    await rm(root, { recursive: true, force: true });
  });
  await mkdir(path.join(root, '.cache'));
  const bridge = createReviewPanelBridge(root);
  async function listen(handler) {
    const server = http.createServer(handler);
    servers.push(server);
    server.listen(0, '127.0.0.1');
    await once(server, 'listening');
    return `http://127.0.0.1:${server.address().port}`;
  }
  const origin = await listen((req, res) => bridge.middleware(req, res, () => { res.writeHead(404); res.end(); }));
  const configure = target => writeFile(path.join(root, '.cache/api-review.json'), JSON.stringify({ origin: target }));
  return { bridge, origin, listen, configure };
}

test('review panel is opt-in and injects only into development HTML', async t => {
  const f = await fixture(t);
  const html = '<body>API</body>';
  assert.equal(await f.bridge.inject(html), html);
  assert.equal((await fetch(f.origin + '/__review/session')).status, 404);
  await f.configure('http://127.0.0.1:5186');
  assert.match(await f.bridge.inject(html), /src="\/__review\/panel.js"/);
});

test('same-origin writes preserve token and body; cross-origin writes never reach storage', async t => {
  const f = await fixture(t);
  let received;
  let calls = 0;
  const upstream = await f.listen(async (req, res) => {
    calls++;
    let body = '';
    for await (const chunk of req) body += chunk;
    received = { headers: req.headers, body, url: req.url, method: req.method };
    res.writeHead(200, { 'Content-Type': 'application/json' });
    res.end('{"saved":true}');
  });
  await f.configure(upstream);
  const payload = JSON.stringify({ text: '审核意见' });
  const send = origin => fetch(f.origin + '/__review/notes', { method: 'POST',
    headers: { ...(origin ? { Origin: origin } : {}), 'Content-Type': 'application/json', 'X-Review-Token': 'original-token' }, body: payload });
  assert.equal((await send(f.origin)).status, 200);
  assert.equal(received.headers.origin, upstream);
  assert.equal(received.headers['x-review-token'], 'original-token');
  assert.equal(received.body, payload);
  assert.equal(received.method, 'POST');
  assert.equal(received.url, '/__review/notes');
  assert.equal((await send('https://unrelated.invalid')).status, 403);
  assert.equal((await send()).status, 403);
  assert.equal(calls, 1);
});

test('event stream reaches the panel before the upstream response ends', async t => {
  const f = await fixture(t);
  const upstream = await f.listen((req, res) => {
    res.writeHead(200, { 'Content-Type': 'text/event-stream' });
    res.write('data: {"revision":7}\n\n');
  });
  await f.configure(upstream);
  const response = await fetch(f.origin + '/__review/events');
  assert.equal(response.headers.get('content-type'), 'text/event-stream');
  const reader = response.body.getReader();
  assert.match(new TextDecoder().decode((await reader.read()).value), /"revision":7/);
  await reader.cancel();
});

test('remote targets are rejected without forwarding requests', async t => {
  const f = await fixture(t);
  await f.configure('https://example.invalid:5186');
  assert.equal((await fetch(f.origin + '/__review/session')).status, 503);
  await assert.rejects(f.bridge.inject('<body>API</body>'), /loopback/);
});
