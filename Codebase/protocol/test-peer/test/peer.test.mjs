import test from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import net from 'node:net';
import crypto from 'node:crypto';
import { once } from 'node:events';
import { Peer, Conn } from '../ctrllink-peer.mjs';
import { x25519Generate } from '../crypto.mjs';

const peers = [];
const tmp = fs.mkdtempSync(path.join(os.tmpdir(), 'ctrllink-test-'));
test.after(async () => { await Promise.all(peers.map((p) => p.stop())); fs.rmSync(tmp, { recursive: true, force: true }); });

async function mkPeer(name, opts = {}) {
  const p = new Peer({ state: fs.mkdtempSync(path.join(tmp, `${name}-`)), name, port: 0, mdns: false, prompt: async () => true, ...opts });
  await p.start();
  peers.push(p);
  return p;
}
const addr = (p) => ['127.0.0.1', p.port];
function waitEv(p, name, pred = () => true, ms = 10000) {
  return new Promise((res, rej) => {
    const t = setTimeout(() => { p.off(name, f); rej(new Error(`timeout waiting for ${name} on ${p.name}`)); }, ms);
    const f = (d) => { if (pred(d)) { clearTimeout(t); p.off(name, f); res(d); } };
    p.on(name, f);
  });
}
const sha = (file) => crypto.createHash('sha256').update(fs.readFileSync(file)).digest('hex');
function randomFile(name, size) {
  const f = path.join(tmp, name);
  fs.writeFileSync(f, crypto.randomBytes(size));
  return f;
}

// Pairs two fresh peers over QR and waits for the session on both sides.
async function pairedPair(aOpts, bOpts) {
  const host = await mkPeer('Host', aOpts), joiner = await mkPeer('Joiner', bOpts);
  const { uri } = host.openWindow();
  const conns = [waitEv(host, 'connected'), waitEv(joiner, 'connected')];
  await joiner.join(uri, { connect: addr(host) });
  await Promise.all(conns);
  return [host, joiner];
}

test('QR pairing stores the pairing on both sides and opens a session with ready', async () => {
  const host = await mkPeer('Mac'), joiner = await mkPeer('Pixel', { pf: 'android' });
  const { uri } = host.openWindow();
  const hc = waitEv(host, 'connected'), jc = waitEv(joiner, 'connected');
  const res = await joiner.join(uri, { connect: addr(host) });
  assert.equal(res.id, host.id);
  assert.equal(host.pairings[joiner.id].pairKey, joiner.pairings[host.id].pairKey);
  assert.equal(host.window, null, 'window closes after a successful pairing');
  const [h, j] = await Promise.all([hc, jc]);
  assert.deepEqual([h.name, h.pf, h.role], ['Pixel', 'android', 'server']);
  assert.deepEqual([j.name, j.pf, j.role, j.app], ['Mac', 'mac', 'client', 'test-peer/1.0']);
});

test('code pairing shows the same SAS on both sides', async () => {
  let asked;
  const host = await mkPeer('Host', { prompt: async (kind, info) => { asked = { kind, ...info }; return true; } });
  const joiner = await mkPeer('Joiner');
  const { code } = host.openWindow();
  const hs = waitEv(host, 'sas'), js = waitEv(joiner, 'sas');
  const res = await joiner.join(code, { connect: addr(host) });
  const [a, b] = await Promise.all([hs, js]);
  assert.match(a.sas, /^\d{6}$/);
  assert.equal(a.sas, b.sas);
  assert.equal(res.sas, a.sas);
  assert.deepEqual([asked.kind, asked.name, asked.sas], ['pair', 'Joiner', a.sas]);
  assert.ok(host.pairings[joiner.id]);
});

test('code pairing rejected by the host user fails with rejected', async () => {
  const host = await mkPeer('Host', { prompt: async () => false }), joiner = await mkPeer('Joiner');
  const { code } = host.openWindow();
  await assert.rejects(joiner.join(code, { connect: addr(host) }), { reason: 'rejected' });
  assert.deepEqual(host.pairings, {});
});

test('wrong code fails with bad_code; 5 failures close the window', async () => {
  const host = await mkPeer('Host'), joiner = await mkPeer('Joiner');
  const { code } = host.openWindow();
  const wrong = String((Number(code) + 1) % 1e6).padStart(6, '0');
  for (let i = 0; i < 5; i++) await assert.rejects(joiner.join(wrong, { connect: addr(host) }), { reason: 'bad_code' });
  assert.equal(host.window, null);
  await assert.rejects(joiner.join(code, { connect: addr(host) }), { reason: 'closed' });
  assert.deepEqual(joiner.pairings, {});
});

test('QR mode aborts when pair_hello.pub differs from k in the QR', async () => {
  const host = await mkPeer('Host'), joiner = await mkPeer('Joiner');
  const { uri } = host.openWindow();
  host.window.kp = x25519Generate(); // host (or a MITM) now answers with a key that is not the one in the QR
  await assert.rejects(joiner.join(uri, { connect: addr(host) }), { reason: 'key_mismatch' });
  assert.deepEqual(joiner.pairings, {});
  assert.deepEqual(host.pairings, {});
});

test('text and url clips', async () => {
  const [a, b] = await pairedPair();
  let got = waitEv(b, 'clip');
  a.sendText('hello 👋');
  assert.deepEqual(await got, { from: a.id, name: 'Host', id: (await got).id, kind: 'text', text: 'hello 👋' });
  got = waitEv(a, 'clip');
  b.sendText('https://example.com/x?y=1');
  const u = await got;
  assert.deepEqual([u.kind, u.text], ['url', 'https://example.com/x?y=1']);
});

test('5 MB file arrives with a matching hash', async () => {
  const [a, b] = await pairedPair();
  const src = randomFile('big.bin', 5 * 1024 * 1024);
  const offer = waitEv(b, 'offer'), file = waitEv(b, 'file', () => true, 30000);
  a.sendFiles([src]);
  assert.equal((await offer).auto, true);
  const f = await file;
  assert.equal(f.size, 5 * 1024 * 1024);
  assert.equal(f.sha256, sha(src));
  assert.equal(sha(f.path), sha(src));
  assert.equal(path.dirname(f.path), path.join(b.dir, 'received'));
  assert.deepEqual(fs.readdirSync(path.join(b.dir, 'tmp')), []);
});

test('image clip and name clash get " (1)"', async () => {
  const [a, b] = await pairedPair();
  const src = randomFile('shot.png', 1000);
  for (const expect of ['shot.png', 'shot (1).png']) {
    const f = waitEv(b, 'file');
    a.sendFiles([src], 'image');
    const got = await f;
    assert.equal(got.kind, 'image');
    assert.equal(path.basename(got.path), expect);
  }
});

test('over the auto-accept limit the user is asked; declining sends reject', async () => {
  let asked = null;
  const [a, b] = await pairedPair({}, { acceptLimitMb: 1, prompt: async (kind, info) => { asked = { ...info, promptKind: kind }; return false; } });
  const src = randomFile('2mb.bin', 2 * 1024 * 1024);
  const rejected = waitEv(a, 'rejected');
  const [id] = a.sendFiles([src]);
  assert.equal((await rejected).id, id);
  assert.equal(asked.promptKind, 'accept');
  assert.equal(asked.size, 2 * 1024 * 1024);
  assert.equal(a.outgoing.size, 0);
});

test('cancel mid-transfer deletes the partial file', async () => {
  const [a, b] = await pairedPair();
  const src = randomFile('huge.bin', 64 * 1024 * 1024);
  let id;
  const chunkSeen = new Promise((res) => {
    const orig = b.onChunk.bind(b);
    b.onChunk = (s, hex, data) => { orig(s, hex, data); if (fs.readdirSync(path.join(b.dir, 'tmp')).length) res(); };
  });
  const cancelled = waitEv(b, 'cancelled');
  [id] = a.sendFiles([src]);
  await chunkSeen;
  a.cancel(id);
  assert.equal((await cancelled).id, id);
  await new Promise((r) => setTimeout(r, 200));
  assert.deepEqual(fs.readdirSync(path.join(b.dir, 'tmp')), []);
  assert.deepEqual(fs.readdirSync(path.join(b.dir, 'received')), []);
  assert.equal(b.incoming.size, 0);
});

test('ping/pong keeps an idle session alive; silence closes it', async () => {
  const fast = { pingMs: 100, deadMs: 300 };
  const [a, b] = await pairedPair(fast, fast);
  await new Promise((r) => setTimeout(r, 800));
  assert.ok(a.sessions.has(b.id) && b.sessions.has(a.id), 'pings keep it alive past deadMs');
  const gone = waitEv(a, 'disconnected', () => true, 3000);
  b.sessions.get(a.id).conn.sock.pause(); // b stops reading: no pongs, and its own timer is quieted below
  b.deadMs = 1e9;
  b.pingMs = 1e9;
  assert.equal((await gone).reason, 'timeout');
});

test('an unknown device gets {"t":"unknown"}', async () => {
  const b = await mkPeer('Server');
  const sock = net.connect(b.port, '127.0.0.1');
  await once(sock, 'connect');
  const conn = new Conn(sock, true);
  const ev = waitEv(b, 'unknown');
  conn.sendJson({ t: 'hello', v: 1, id: crypto.randomUUID(), nonce: crypto.randomBytes(32).toString('base64') });
  assert.deepEqual(JSON.parse(await conn.recv(5000)), { t: 'unknown' });
  await ev;
  await once(sock, 'close');
});

test('higher protocol version gets an error frame', async () => {
  const b = await mkPeer('Server');
  const sock = net.connect(b.port, '127.0.0.1');
  await once(sock, 'connect');
  const conn = new Conn(sock, true);
  conn.sendJson({ t: 'hello', v: 2, id: crypto.randomUUID(), nonce: crypto.randomBytes(32).toString('base64') });
  assert.deepEqual(JSON.parse(await conn.recv(5000)), { t: 'error', reason: 'version' });
});

test('duplicate connections: both keep the one whose client has the lower deviceId', async () => {
  const [a, b] = await pairedPair();
  a.sessions.get(b.id).conn.close();
  await Promise.all([waitEv(a, 'disconnected'), waitEv(b, 'disconnected')]);
  await Promise.all([a.connect(...addr(b), b.id), b.connect(...addr(a), a.id)]);
  await new Promise((r) => setTimeout(r, 300));
  const sa = a.sessions.get(b.id), sb = b.sessions.get(a.id);
  const lower = a.id < b.id ? a.id : b.id;
  assert.equal(sa.clientId, lower);
  assert.equal(sb.clientId, lower);
  assert.equal(sa.conn.sock.localPort, sb.conn.sock.remotePort, 'same TCP connection kept on both ends');
  assert.ok(!sa.conn.closed && !sb.conn.closed);
  const got = waitEv(b, 'clip');
  a.sendText('still works');
  assert.equal((await got).text, 'still works');
});

test('unpair notifies the peer and deletes both pairings', async () => {
  const [a, b] = await pairedPair();
  const ev = waitEv(b, 'unpaired');
  a.unpair(b.id);
  assert.equal((await ev).by, 'peer');
  assert.deepEqual([a.pairings, b.pairings], [{}, {}]);
});

test('after a 5 MB file the same session still delivers a text clip', async () => {
  const [a, b] = await pairedPair();
  const src = randomFile('then-text.bin', 5 * 1024 * 1024);
  const file = waitEv(b, 'file', () => true, 30000);
  a.sendFiles([src]);
  assert.equal((await file).sha256, sha(src));
  const got = waitEv(b, 'clip', () => true, 3000);
  a.sendText('after file');
  assert.equal((await got).text, 'after file');
  assert.equal(b.sessions.get(a.id).conn.sock.isPaused(), false);
});

test('safeName follows §6.3', async () => {
  const { safeName } = await import('../ctrllink-peer.mjs');
  assert.equal(safeName('../../etc/passwd'), 'passwd');
  assert.equal(safeName('C:\\Users\\x\\evil.txt'), 'evil.txt');
  assert.equal(safeName('a:b\x01c\x7f.txt'), 'a_b_c_.txt');
  assert.equal(safeName('...hidden'), 'hidden');
  assert.equal(safeName('dir/'), 'file');
  assert.equal(safeName('..'), 'file');
  assert.equal(safeName(''), 'file');
  assert.equal(Buffer.byteLength(safeName('x'.repeat(300))), 200);
  const emoji = safeName('👋'.repeat(60)); // 4 bytes each: cut must not split one
  assert.equal(emoji, '👋'.repeat(50));
});

test('text over 192 KiB is sent as kind files with one .txt', async () => {
  const [a, b] = await pairedPair();
  const small = 'x'.repeat(192 * 1024);
  let got = waitEv(b, 'clip');
  a.sendText(small);
  assert.equal((await got).text.length, small.length);
  const big = 'y'.repeat(192 * 1024 + 1);
  const offer = waitEv(b, 'offer'), file = waitEv(b, 'file');
  a.sendText(big);
  const o = await offer;
  assert.equal(o.kind, 'files');
  assert.equal(o.files.length, 1);
  assert.match(o.files[0].name, /\.txt$/);
  assert.equal(o.files[0].mime, 'text/plain');
  assert.equal(fs.readFileSync((await file).path, 'utf8'), big);
});

test('a chunk for a fileId of no accepted clip closes the session', async () => {
  const [a, b] = await pairedPair();
  const gone = waitEv(b, 'disconnected');
  a.sessions.get(b.id).conn.sendChunk(crypto.randomUUID().replace(/-/g, ''), Buffer.from('rogue'));
  assert.equal((await gone).reason, 'protocol');
});
