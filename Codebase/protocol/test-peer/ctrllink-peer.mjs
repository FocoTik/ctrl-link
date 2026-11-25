#!/usr/bin/env node
// Ctrl Link Protocol v1 reference peer + CLI (see ../PROTOCOL.md). Node built-ins only, mDNS via macOS `dns-sd`.
import net from 'node:net';
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import crypto from 'node:crypto';
import readline from 'node:readline';
import { spawn } from 'node:child_process';
import { EventEmitter } from 'node:events';
import { parseArgs } from 'node:util';
import { x25519Generate, x25519Shared, pairingKeys, sessionKeys, seal, open, control, chunk } from './crypto.mjs';

const CHUNK = 262144;
const TEXT_MAX = 192 * 1024;
const MAX_FRAME = CHUNK + 64;
const SERVICE = '_ctrllink._tcp';
const APP = 'test-peer/1.0';
const UUID = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;
const MIME = { png: 'image/png', jpg: 'image/jpeg', jpeg: 'image/jpeg', heic: 'image/heic', gif: 'image/gif', txt: 'text/plain', pdf: 'application/pdf', mp4: 'video/mp4', mov: 'video/quicktime' };

const b64 = (b) => Buffer.from(b).toString('base64');
const b64url = (b) => Buffer.from(b).toString('base64url');
function unb64(s, len) {
  const b = Buffer.from(String(s), 'base64');
  if (len !== undefined && b.length !== len) throw new Error(`bad base64 length (want ${len})`);
  return b;
}
const hexId = (uuid) => uuid.replace(/-/g, '').toLowerCase();
const sameBytes = (a, b) => a.length === b.length && crypto.timingSafeEqual(a, b);
function parseJson(body) {
  const m = JSON.parse(body.toString('utf8'));
  if (!m || typeof m !== 'object' || Array.isArray(m)) throw new Error('not a JSON object');
  return m;
}
class PairError extends Error { constructor(reason) { super(`pairing failed: ${reason}`); this.reason = reason; } }

// ---- framing -------------------------------------------------------------------------------------------------
export class Conn {
  constructor(sock, isClient) {
    Object.assign(this, { sock, dir: isClient ? 1 : 2, buf: Buffer.alloc(0), q: [], waiters: [], closed: false, handler: null, tx: 0, rx: 0 });
    sock.on('data', (d) => this.onData(d));
    sock.on('error', () => {});
    sock.on('close', () => {
      this.closed = true;
      for (const w of this.waiters.splice(0)) w.rej(new Error('closed'));
      this.onClose?.();
    });
  }
  onData(d) {
    this.buf = this.buf.length ? Buffer.concat([this.buf, d]) : d;
    while (this.buf.length >= 4 && !this.closed) {
      const n = this.buf.readUInt32BE(0);
      if (n > MAX_FRAME) return this.close();
      if (this.buf.length < 4 + n) break;
      const body = this.buf.subarray(4, 4 + n);
      this.buf = this.buf.subarray(4 + n);
      if (this.handler) this.handler(body);
      else if (this.waiters.length) this.waiters.shift().res(body);
      else this.q.push(body);
    }
  }
  recv(ms = 30000) {
    if (this.q.length) return Promise.resolve(this.q.shift());
    if (this.closed) return Promise.reject(new Error('closed'));
    return new Promise((res, rej) => {
      const t = setTimeout(() => { this.waiters.splice(this.waiters.indexOf(w), 1); rej(new Error('timeout')); }, ms);
      const w = { res: (b) => { clearTimeout(t); res(b); }, rej: (e) => { clearTimeout(t); rej(e); } };
      this.waiters.push(w);
    });
  }
  setHandler(fn) { this.handler = fn; for (const b of this.q.splice(0)) fn(b); }
  sendRaw(body) {
    if (this.closed) return true;
    const len = Buffer.alloc(4);
    len.writeUInt32BE(body.length);
    return this.sock.write(Buffer.concat([len, body]));
  }
  sendJson(obj) { return this.sendRaw(Buffer.from(JSON.stringify(obj))); }
  setKeys(sendKey, recvKey) { this.sendKey = sendKey; this.recvKey = recvKey; }
  sendCtl(obj) { return this.sendRaw(seal(this.sendKey, this.dir, this.tx++, control(obj))); }
  sendChunk(fileIdHex, data) { return this.sendRaw(seal(this.sendKey, this.dir, this.tx++, chunk(fileIdHex, data))); }
  decrypt(body) { return open(this.recvKey, 3 - this.dir, this.rx++, body); }
  drain() { return new Promise((r) => { this.sock.once('drain', r); this.sock.once('close', r); }); }
  end() { this.sock.end(); setTimeout(() => this.sock.destroy(), 2000).unref(); }
  close() { this.closed = true; this.sock.destroy(); }
}

function dialTcp(host, port, ms = 5000) {
  return new Promise((res, rej) => {
    const sock = net.connect({ host, port });
    const t = setTimeout(() => { sock.destroy(); rej(new Error(`connect timeout ${host}:${port}`)); }, ms);
    sock.once('connect', () => { clearTimeout(t); sock.setNoDelay(true); res(sock); });
    sock.once('error', (e) => { clearTimeout(t); rej(e); });
  });
}

// ---- mDNS via dns-sd ----------------------------------------------------------------------------------------
// dns-sd runs under a sh that kills it once our stdin pipe closes, so it dies with us even on SIGKILL/crash.
function dnssd(args) {
  const p = spawn('/bin/sh', ['-c', 'dns-sd "$@" & p=$!; read _; kill $p', 'sh', ...args], { stdio: ['pipe', 'pipe', 'ignore'] });
  p.on('error', () => {});
  p.stdin.on('error', () => {});
  return { stdout: p.stdout, kill: () => p.stdin.end(), on: (...a) => p.on(...a) };
}

export function dnssdAdvertise(name, port, txt) {
  return dnssd(['-R', name, SERVICE, 'local', String(port), ...Object.entries(txt).map(([k, v]) => `${k}=${v}`)]);
}

// Calls onAdd(instanceName) once per newly seen instance name.
export function dnssdBrowse(onAdd) {
  const p = dnssd(['-B', SERVICE, 'local']);
  const seen = new Set();
  readline.createInterface({ input: p.stdout }).on('line', (line) => {
    const m = line.match(/\s(Add|Rmv)\s+\d+\s+\d+\s+\S+\s+\S+\s+(.+)$/);
    if (!m) return;
    // shortcut: Rmv only forgets the name so a re-Add is resolved again; stale peers just fail to dial.
    if (m[1] === 'Rmv') return seen.delete(m[2]);
    if (!seen.has(m[2])) { seen.add(m[2]); onAdd(m[2]); }
  });
  return p;
}

export function dnssdResolve(name, ms = 5000) {
  return new Promise((res, rej) => {
    const p = dnssd(['-L', name, SERVICE, 'local']);
    let hit = null;
    const t = setTimeout(() => { p.kill(); rej(new Error(`resolve timeout ${name}`)); }, ms);
    p.on('error', (e) => { clearTimeout(t); rej(e); });
    readline.createInterface({ input: p.stdout }).on('line', (line) => {
      const m = line.match(/can be reached at (.+?)\.?:(\d+) /);
      if (m) { hit = { host: m[1], port: Number(m[2]) }; return; }
      if (hit && /^\s+\S+=/.test(line)) {
        const txt = Object.fromEntries([...line.matchAll(/(\w+)=((?:\\.|\S)*)/g)].map((x) => [x[1], x[2].replace(/\\(.)/g, '$1')]));
        clearTimeout(t); p.kill(); res({ name, ...hit, txt });
      }
    });
  });
}

// ---- peer -----------------------------------------------------------------------------------------------------
export class Peer extends EventEmitter {
  constructor(o = {}) {
    super();
    this.dir = path.resolve(o.state ?? './.peer-state');
    this.port = o.port ?? 47823;
    this.limit = (o.acceptLimitMb ?? 100) * 1024 * 1024;
    this.mdns = o.mdns ?? true;
    this.pingMs = o.pingMs ?? 15000;
    this.deadMs = o.deadMs ?? 45000;
    this.prompt = o.prompt ?? (async (kind) => kind === 'pair' && !!o.autoAccept);
    for (const d of ['received', 'tmp']) fs.mkdirSync(path.join(this.dir, d), { recursive: true });
    const idFile = path.join(this.dir, 'identity.json');
    const ident = fs.existsSync(idFile) ? JSON.parse(fs.readFileSync(idFile, 'utf8')) : { id: crypto.randomUUID(), name: os.hostname().replace(/\.local$/, ''), pf: 'mac' };
    if (o.name) ident.name = o.name;
    if (o.pf) ident.pf = o.pf;
    fs.writeFileSync(idFile, JSON.stringify(ident, null, 2));
    Object.assign(this, ident);
    const pFile = path.join(this.dir, 'pairings.json');
    this.pairings = fs.existsSync(pFile) ? JSON.parse(fs.readFileSync(pFile, 'utf8')) : {};
    this.sessions = new Map(); // peerId -> session
    this.incoming = new Map(); // clipId -> transfer
    this.inFiles = new Map(); // fileId hex -> file state
    this.outgoing = new Map(); // clipId -> transfer
    this.deadFiles = new Set(); // fileId hex of cancelled transfers, so their late chunks are ignored
    this.discovered = new Map(); // peerId -> {host, port, txt}
    this.conns = new Set();
    this.timers = new Set();
    this.window = null;
  }

  ev(name, data = {}) {
    this.emit('event', { ev: name, ...data });
    if (name !== 'error' || this.listenerCount('error')) this.emit(name, data); // unhandled 'error' would throw
  }
  later(fn, ms) { if (this.stopped) return; const t = setTimeout(() => { this.timers.delete(t); fn(); }, ms); this.timers.add(t); return t; }
  savePairings() { fs.writeFileSync(path.join(this.dir, 'pairings.json'), JSON.stringify(this.pairings, null, 2)); }

  async start() {
    this.server = net.createServer((sock) => this.onIncoming(sock));
    await new Promise((res, rej) => {
      this.server.once('error', (e) => {
        if (e.code !== 'EADDRINUSE' || this.port === 0) return rej(e);
        this.server.listen(0, res); // spec §2: if busy, any free port
      });
      this.server.listen(this.port, res);
    });
    this.port = this.server.address().port;
    this.ev('listening', { id: this.id, name: this.name, port: this.port });
    if (this.mdns) {
      this.advertise();
      this.browser = dnssdBrowse((n) => dnssdResolve(n).then((s) => this.onService(s), () => {}));
    }
    return this.port;
  }

  advertise() {
    if (!this.mdns || !this.server) return;
    this.adv?.kill();
    this.adv = dnssdAdvertise(this.name, this.port, { v: 1, id: this.id, pf: this.pf, pair: this.window ? 1 : 0 });
  }

  stop() {
    this.closeWindow();
    this.stopped = true;
    for (const t of this.timers) clearTimeout(t);
    clearInterval(this.pingTimer);
    this.adv?.kill(); this.browser?.kill();
    for (const c of this.conns) c.close();
    return new Promise((r) => (this.server ? this.server.close(() => r()) : r()));
  }

  track(conn) { this.conns.add(conn); conn.sock.on('close', () => this.conns.delete(conn)); return conn; }

  // ---- discovery / dialing (§2, §5.1) ----
  onService(s) {
    if (!s.txt.id || s.txt.id === this.id) return;
    this.discovered.set(s.txt.id, s);
    this.ev('peer', { id: s.txt.id, name: s.name, host: s.host, port: s.port, pf: s.txt.pf, pair: s.txt.pair === '1', paired: !!this.pairings[s.txt.id] });
    this.scheduleDial(s.txt.id);
  }

  scheduleDial(id, extraMs = 0) {
    if (!this.pairings[id] || this.sessions.has(id) || !this.discovered.has(id)) return;
    this.later(() => {
      const d = this.discovered.get(id);
      if (d && this.pairings[id] && !this.sessions.has(id)) this.connect(d.host, d.port, id).catch((e) => this.ev('error', { message: `dial ${d.host}:${d.port}: ${e.code ?? e.message}` }));
    }, extraMs + (this.id < id ? 0 : 10000));
  }

  // ---- incoming connection: pairing or session ----
  onIncoming(sock) {
    sock.setNoDelay(true);
    const conn = this.track(new Conn(sock, false));
    conn.recv(30000).then((b) => {
      const m = parseJson(b);
      if (m.v > 1) { conn.sendJson({ t: 'error', reason: 'version' }); return conn.end(); }
      if (m.t === 'pair_req') return this.hostPair(conn, m);
      if (m.t === 'hello') return this.serverHello(conn, m);
      conn.close();
    }).catch((e) => { conn.close(); if (e.message !== 'closed') this.ev('error', { message: e.message }); });
  }

  // ---- pairing, host side (§4) ----
  openWindow(ms = 5 * 60000) {
    this.closeWindow();
    const w = { kp: x25519Generate(), token: crypto.randomBytes(16), code: String(crypto.randomInt(0, 1000000)).padStart(6, '0'), failures: 0 };
    w.timer = this.later(() => this.closeWindow(), ms);
    this.window = w;
    this.advertise();
    const ips = Object.values(os.networkInterfaces()).flat().filter((i) => i.family === 'IPv4' && !i.internal).map((i) => i.address);
    const uri = `ctrllink://pair?v=1&id=${this.id}&n=${encodeURIComponent(this.name)}&pf=${this.pf}&h=${ips.join(',')}&p=${this.port}&k=${b64url(w.kp.pub)}&t=${b64url(w.token)}`;
    this.ev('pairing', { uri, code: w.code, port: this.port });
    return { uri, code: w.code };
  }

  closeWindow() {
    if (!this.window) return;
    clearTimeout(this.window.timer);
    this.window = null;
    this.advertise();
    this.ev('window_closed');
  }

  async hostPair(conn, req) {
    const w = this.window;
    const fail = (reason) => { conn.sendJson({ t: 'pair_fail', reason }); conn.end(); this.ev('pair_failed', { reason, name: req.name }); };
    if (!w) return fail('closed');
    if (!['qr', 'code'].includes(req.mode) || !UUID.test(req.id ?? '') || typeof req.name !== 'string') return conn.close();
    const joinerPub = unb64(req.pub, 32);
    conn.sendJson({ t: 'pair_hello', v: 1, id: this.id, name: this.name, pf: this.pf, pub: b64(w.kp.pub) });
    const shared = x25519Shared(w.kp, joinerPub);
    const k = pairingKeys(shared, req.mode === 'qr' ? w.token : Buffer.from(w.code), w.kp.pub, joinerPub);
    let m;
    try { m = parseJson(await conn.recv(60000)); } catch (e) { return fail(e.message === 'timeout' ? 'timeout' : 'closed'); }
    if (m.t !== 'pair_confirm' || !sameBytes(unb64(m.mac ?? ''), k.joinerMac)) {
      if (++w.failures >= 5 && this.window === w) this.closeWindow();
      return fail('bad_code');
    }
    this.ev('sas', { sas: k.sas, id: req.id, name: req.name, mode: req.mode });
    if (req.mode === 'code') {
      let to;
      const ok = await Promise.race([this.prompt('pair', { id: req.id, name: req.name, sas: k.sas }), new Promise((r) => { to = setTimeout(r, 60000, 'timeout'); })]);
      clearTimeout(to);
      if (conn.closed) return fail('closed');
      if (ok !== true) return fail(ok === 'timeout' ? 'timeout' : 'rejected');
    }
    if (this.window !== w) return fail('closed');
    this.pairings[req.id] = { name: req.name, pf: req.pf, pairKey: k.pairKey.toString('hex') };
    this.savePairings();
    conn.sendJson({ t: 'pair_ok', mac: b64(k.hostMac) });
    conn.end();
    this.closeWindow();
    this.ev('paired', { id: req.id, name: req.name, pf: req.pf, role: 'host' });
  }

  // ---- pairing, joiner side (§4) ----
  // target: ctrllink:// URI or 6-digit code. connect: [host, port] to skip h/mDNS.
  async join(target, { connect } = {}) {
    let qr = null, code = null, addrs;
    if (target.startsWith('ctrllink://')) {
      const u = new URL(target);
      const q = Object.fromEntries(u.searchParams);
      if (q.v > 1) throw new PairError('version');
      qr = { id: q.id, k: Buffer.from(q.k ?? '', 'base64url'), t: Buffer.from(q.t ?? '', 'base64url'), port: Number(q.p) };
      if (qr.k.length !== 32 || qr.t.length !== 16) throw new PairError('bad_uri');
      addrs = connect ? [connect] : (q.h ? q.h.split(',') : []).map((h) => [h.replace(/^\[|\]$/g, ''), qr.port]);
    } else if (/^\d{6}$/.test(target)) {
      code = target;
      addrs = connect ? [connect] : null;
    } else throw new PairError('bad_target');

    if (!addrs) addrs = (await this.findServices((s) => s.txt.pair === '1', 8000, true)).map((s) => [s.host, s.port]);
    else if (qr && !connect) addrs.push(async () => (await this.findServices((s) => s.txt.id === qr.id, 8000, true)).map((s) => [s.host, s.port]));

    for (let i = 0; i < addrs.length; i++) {
      if (typeof addrs[i] === 'function') { addrs.splice(i + 1, 0, ...(await addrs[i]())); continue; }
      const [host, port] = addrs[i];
      let sock;
      try { sock = await dialTcp(host, port); } catch { continue; }
      const res = await this.joinOn(this.track(new Conn(sock, true)), qr, code);
      // Spec gap: who dials the first session is unspecified; the joiner dials the address that just worked.
      await this.connect(host, port, res.id).catch((e) => this.ev('error', { message: `session: ${e.message}` }));
      return res;
    }
    throw new PairError('unreachable');
  }

  async joinOn(conn, qr, code) {
    const kp = x25519Generate();
    const fail = (reason) => { conn.close(); this.ev('pair_failed', { reason }); throw new PairError(reason); };
    try {
      conn.sendJson({ t: 'pair_req', v: 1, mode: qr ? 'qr' : 'code', id: this.id, name: this.name, pf: this.pf, pub: b64(kp.pub) });
      const m = parseJson(await conn.recv(10000));
      if (m.t === 'pair_fail') return fail(m.reason);
      if (m.t === 'error' || m.v > 1) return fail('version');
      if (m.t !== 'pair_hello') return fail('protocol');
      const hostPub = unb64(m.pub, 32);
      if (qr && (!hostPub.equals(qr.k) || m.id !== qr.id)) return fail('key_mismatch');
      const k = pairingKeys(x25519Shared(kp, hostPub), qr ? qr.t : Buffer.from(code), hostPub, kp.pub);
      this.ev('sas', { sas: k.sas, id: m.id, name: m.name, mode: qr ? 'qr' : 'code' });
      conn.sendJson({ t: 'pair_confirm', mac: b64(k.joinerMac) });
      const r = parseJson(await conn.recv(75000));
      if (r.t === 'pair_fail') return fail(r.reason);
      if (r.t !== 'pair_ok' || !sameBytes(unb64(r.mac ?? ''), k.hostMac)) return fail('bad_mac');
      this.pairings[m.id] = { name: m.name, pf: m.pf, pairKey: k.pairKey.toString('hex') };
      this.savePairings();
      conn.end();
      this.ev('paired', { id: m.id, name: m.name, pf: m.pf, role: 'joiner' });
      return { id: m.id, name: m.name, pf: m.pf, sas: k.sas };
    } catch (e) {
      if (e instanceof PairError) throw e;
      return fail(e.message === 'timeout' ? 'timeout' : 'closed');
    }
  }

  // Browse until services matching pred show up; with first=true return as soon as one is found.
  findServices(pred, ms, first) {
    return new Promise((res) => {
      const found = [];
      const done = () => { clearTimeout(t); b.kill(); res(found); };
      const t = setTimeout(done, ms);
      const b = dnssdBrowse((n) => dnssdResolve(n).then((s) => {
        if (s.txt.id !== this.id && pred(s)) { found.push(s); if (first) done(); }
      }, () => {}));
    });
  }

  // ---- session (§5) ----
  async connect(host, port, expectId) {
    const conn = this.track(new Conn(await dialTcp(host, port), true));
    try {
      const cn = crypto.randomBytes(32);
      conn.sendJson({ t: 'hello', v: 1, id: this.id, nonce: b64(cn) });
      const m = parseJson(await conn.recv(10000));
      if (m.t === 'unknown') { conn.close(); this.ev('unknown', { host, port, role: 'client' }); return null; }
      if (m.t === 'error' || m.v > 1) throw new Error('peer requires newer protocol version');
      const p = this.pairings[m.id];
      if (m.t !== 'hello' || !p || (expectId && m.id !== expectId)) throw new Error(`unexpected server ${m.id}`);
      const k = sessionKeys(Buffer.from(p.pairKey, 'hex'), cn, unb64(m.nonce, 32));
      conn.setKeys(k.c2s, k.s2c);
      return await this.ready(conn, m.id, this.id);
    } catch (e) { conn.close(); throw e; }
  }

  serverHello(conn, m) {
    const p = this.pairings[m.id];
    if (!p) { conn.sendJson({ t: 'unknown' }); conn.end(); this.ev('unknown', { id: m.id, role: 'server' }); return; }
    const sn = crypto.randomBytes(32);
    const cn = unb64(m.nonce, 32);
    conn.sendJson({ t: 'hello', v: 1, id: this.id, nonce: b64(sn) });
    const k = sessionKeys(Buffer.from(p.pairKey, 'hex'), cn, sn);
    conn.setKeys(k.s2c, k.c2s);
    return this.ready(conn, m.id, m.id);
  }

  async ready(conn, peerId, clientId) {
    conn.sendCtl({ t: 'ready', name: this.name, pf: this.pf, app: APP });
    const pt = conn.decrypt(await conn.recv(10000)); // throws on bad key → caller closes
    const m = JSON.parse(pt.subarray(1));
    if (pt[0] !== 1 || m.t !== 'ready') throw new Error('first frame was not ready');
    const s = { id: peerId, conn, clientId, name: m.name, pf: m.pf, app: m.app, lastRx: Date.now(), lastPing: 0 };
    const old = this.sessions.get(peerId);
    if (old) {
      // §5.1: keep the session whose client has the lower deviceId (same client = reconnect, keep the new one).
      const keepNew = old.clientId === clientId || clientId < old.clientId;
      (keepNew ? old : s).conn.close();
      if (!keepNew) return null;
    }
    this.sessions.set(peerId, s);
    if (this.pairings[peerId].name !== m.name) { this.pairings[peerId].name = m.name; this.savePairings(); }
    conn.onClose = () => this.onSessionClosed(s);
    conn.setHandler((b) => this.onFrame(s, b));
    this.pingTimer ??= setInterval(() => this.tick(), Math.min(1000, this.pingMs / 3));
    this.pingTimer.unref?.();
    this.ev('connected', { id: peerId, name: m.name, pf: m.pf, app: m.app, role: clientId === this.id ? 'client' : 'server', local: conn.sock.localPort, remote: conn.sock.remotePort });
    return s;
  }

  tick() {
    const now = Date.now();
    for (const s of this.sessions.values()) {
      if (now - s.lastRx > this.deadMs) { s.reason = 'timeout'; s.conn.close(); }
      else if (now - s.lastRx > this.pingMs && now - s.lastPing > this.pingMs) { s.lastPing = now; s.conn.sendCtl({ t: 'ping' }); }
    }
  }

  onSessionClosed(s) {
    if (this.sessions.get(s.id) !== s) return;
    this.sessions.delete(s.id);
    for (const [id, x] of this.incoming) if (x.s === s) this.dropIncoming(id);
    for (const o of this.outgoing.values()) if (o.s === s) { o.cancelled = true; clearTimeout(o.timer); }
    this.ev('disconnected', { id: s.id, name: s.name, reason: s.reason ?? 'closed' });
    this.scheduleDial(s.id, 2000);
  }

  onFrame(s, body) {
    let pt;
    try { pt = s.conn.decrypt(body); } catch { s.reason = 'decrypt'; return s.conn.close(); }
    s.lastRx = Date.now();
    if (pt[0] === 2 && pt.length >= 17) return this.onChunk(s, pt.subarray(1, 17).toString('hex'), pt.subarray(17));
    let m;
    try { if (pt[0] !== 1) throw 0; m = JSON.parse(pt.subarray(1)); } catch { s.reason = 'malformed'; return s.conn.close(); }
    switch (m.t) {
      case 'ping': return s.conn.sendCtl({ t: 'pong' });
      case 'clip': return this.onClip(s, m).catch((e) => this.ev('error', { message: e.message }));
      case 'accept': return this.onAccept(s, m.id);
      case 'reject': {
        const o = this.outgoing.get(m.id);
        if (o?.s !== s) return;
        clearTimeout(o.timer); this.outgoing.delete(m.id);
        return this.ev('rejected', { id: m.id, from: s.id });
      }
      case 'done': return this.onDone(s, m).catch((e) => this.ev('error', { message: e.message }));
      case 'cancel': return this.cancel(m.id, false);
      case 'unpair':
        delete this.pairings[s.id]; this.savePairings();
        s.reason = 'unpaired'; s.conn.close();
        return this.ev('unpaired', { id: s.id, name: s.name, by: 'peer' });
      case 'error': s.reason = `error:${m.reason}`; s.conn.close(); return this.ev('error', { message: `peer error: ${m.reason}`, id: s.id });
      default: // ready again, pong, unknown types: ignore (§7)
    }
  }

  // ---- sending ----
  sendText(text) {
    if (Buffer.byteLength(text) > TEXT_MAX) { // §6.1: larger text goes as one .txt file
      const p = path.join(this.dir, 'tmp', `clip-${crypto.randomUUID()}.txt`);
      fs.writeFileSync(p, text);
      return this.sendFiles([p], 'files', 'clipboard.txt');
    }
    const kind = /^https?:\/\/\S+$/i.test(text.trim()) ? 'url' : 'text';
    const ids = [];
    for (const s of this.sessions.values()) {
      const id = crypto.randomUUID();
      s.conn.sendCtl({ t: 'clip', id, ts: Date.now(), kind, text });
      ids.push(id);
      this.ev('sent', { id, to: s.id, kind });
    }
    return ids;
  }

  sendFiles(paths, kind = 'files', nameOverride) {
    const stats = paths.map((p) => ({ path: p, size: fs.statSync(p).size, name: nameOverride ?? path.basename(p) }));
    if (kind === 'image' && stats.length !== 1) throw new Error('image clip needs exactly one file');
    const ids = [];
    for (const s of this.sessions.values()) {
      const id = crypto.randomUUID();
      const files = stats.map((f) => ({ ...f, fileId: crypto.randomUUID(), mime: MIME[path.extname(f.name).slice(1).toLowerCase()] ?? 'application/octet-stream' }));
      const o = { id, s, files, cancelled: false };
      o.timer = this.later(() => { if (this.outgoing.get(id) === o) { this.cancel(id); this.ev('error', { message: `no answer for clip ${id}` }); } }, 5 * 60000);
      this.outgoing.set(id, o);
      s.conn.sendCtl({ t: 'clip', id, ts: Date.now(), kind, files: files.map(({ fileId, name, mime, size }) => ({ fileId, name, mime, size })) });
      this.ev('offered', { id, to: s.id, kind, files: files.map((f) => f.name) });
      ids.push(id);
    }
    return ids;
  }

  async onAccept(s, id) {
    const o = this.outgoing.get(id);
    if (o?.s !== s || o.started) return;
    o.started = true;
    clearTimeout(o.timer);
    try {
      for (const f of o.files) {
        const h = crypto.createHash('sha256');
        for await (const data of fs.createReadStream(f.path, { highWaterMark: CHUNK })) {
          if (o.cancelled || s.conn.closed) return;
          h.update(data);
          if (!s.conn.sendChunk(hexId(f.fileId), data)) await s.conn.drain();
        }
        if (o.cancelled || s.conn.closed) return;
        s.conn.sendCtl({ t: 'done', id, fileId: f.fileId, sha256: h.digest('hex') });
      }
      this.ev('sent', { id, to: s.id, kind: 'files' });
    } catch (e) {
      this.cancel(id);
      this.ev('error', { message: `send failed: ${e.message}` });
    } finally {
      if (this.outgoing.get(id) === o) this.outgoing.delete(id);
    }
  }

  cancel(id, notify = true) {
    const o = this.outgoing.get(id);
    if (o) {
      o.cancelled = true; clearTimeout(o.timer); this.outgoing.delete(id);
      if (notify) o.s.conn.sendCtl({ t: 'cancel', id });
      return this.ev('cancelled', { id, by: notify ? 'me' : 'peer' });
    }
    const x = this.incoming.get(id);
    if (x) {
      if (notify) x.s.conn.sendCtl({ t: 'cancel', id });
      this.dropIncoming(id);
      this.ev('cancelled', { id, by: notify ? 'me' : 'peer' });
    }
  }

  // ---- receiving ----
  async onClip(s, m) {
    if (typeof m.id !== 'string') return;
    if (m.kind === 'text' || m.kind === 'url') {
      if (typeof m.text === 'string' && Buffer.byteLength(m.text) <= TEXT_MAX) this.ev('clip', { from: s.id, name: s.name, id: m.id, kind: m.kind, text: m.text });
      return;
    }
    if (m.kind !== 'image' && m.kind !== 'files') return; // unknown kind: ignore
    const files = Array.isArray(m.files) ? m.files : [];
    const valid = files.length > 0 && files.every((f) => f && UUID.test(f.fileId ?? '') && typeof f.name === 'string' && Number.isSafeInteger(f.size) && f.size >= 0);
    if (!valid || (m.kind === 'image' && files.length !== 1)) return s.conn.sendCtl({ t: 'reject', id: m.id });
    const total = files.reduce((a, f) => a + f.size, 0);
    const auto = total <= this.limit;
    const info = { from: s.id, name: s.name, id: m.id, kind: m.kind, size: total, files: files.map((f) => ({ name: f.name, size: f.size, mime: f.mime })) };
    this.ev('offer', { ...info, auto });
    const ok = auto || (await this.prompt('accept', info)) === true;
    if (s.conn.closed || this.sessions.get(s.id) !== s) return;
    if (!ok) { s.conn.sendCtl({ t: 'reject', id: m.id }); return this.ev('declined', { id: m.id, from: s.id }); }
    const x = { s, id: m.id, kind: m.kind, left: files.length, files: [] };
    for (const f of files) {
      const st = { x, meta: f, hex: hexId(f.fileId), tmp: path.join(this.dir, 'tmp', `${hexId(f.fileId)}.part`), hash: crypto.createHash('sha256'), got: 0, ws: null };
      x.files.push(st);
      this.inFiles.set(st.hex, st);
    }
    this.incoming.set(m.id, x);
    s.conn.sendCtl({ t: 'accept', id: m.id });
  }

  onChunk(s, hex, data) {
    const f = this.inFiles.get(hex);
    if (!f || f.x.s !== s) {
      if (this.deadFiles.has(hex)) return; // still in flight when we cancelled
      s.reason = 'protocol'; return s.conn.close(); // §6.2: chunk for no accepted clip
    }
    f.got += data.length;
    if (f.got > f.meta.size) return this.cancel(f.x.id);
    f.hash.update(data);
    if (!f.ws) { f.ws = fs.createWriteStream(f.tmp); f.ws.on('error', () => this.cancel(f.x.id)); }
    if (!f.ws.write(data)) { s.conn.sock.pause(); f.ws.once('drain', () => s.conn.sock.resume()); }
    // the last chunk's drain may never fire once onDone calls end(); onDone resumes the socket itself
  }

  async onDone(s, m) {
    const f = this.inFiles.get(hexId(String(m.fileId ?? '')));
    if (!f || f.x.s !== s || f.x.id !== m.id) return;
    const sha = f.hash.digest('hex');
    if (f.got !== f.meta.size || sha !== String(m.sha256).toLowerCase()) {
      this.ev('error', { message: `hash/size mismatch for ${f.meta.name}` });
      return this.cancel(f.x.id);
    }
    this.inFiles.delete(f.hex);
    const ws = f.ws ?? fs.createWriteStream(f.tmp);
    await new Promise((res, rej) => ws.end((e) => (e ? rej(e) : res())));
    s.conn.sock.resume(); // end() replaces a pending 'drain', so resume here or the session stalls
    const dest = uniquePath(path.join(this.dir, 'received'), safeName(f.meta.name));
    fs.renameSync(f.tmp, dest);
    this.ev('file', { from: s.id, id: m.id, kind: f.x.kind, fileId: m.fileId, name: f.meta.name, path: dest, size: f.got, sha256: sha });
    if (--f.x.left === 0) { this.incoming.delete(m.id); this.ev('received', { from: s.id, id: m.id }); }
  }

  dropIncoming(id) {
    const x = this.incoming.get(id);
    if (!x) return;
    this.incoming.delete(id);
    x.s.conn.sock.resume(); // may be paused waiting for a drain that will never come
    for (const f of x.files) {
      this.inFiles.delete(f.hex);
      this.deadFiles.add(f.hex);
      f.ws?.destroy();
      fs.rmSync(f.tmp, { force: true });
    }
  }

  // ---- misc ----
  peers() {
    return Object.entries(this.pairings).map(([id, p]) => ({ id, name: p.name, pf: p.pf, online: this.sessions.has(id) }));
  }

  unpair(id) {
    const s = this.sessions.get(id);
    if (s) { s.conn.sendCtl({ t: 'unpair' }); s.reason = 'unpaired'; s.conn.end(); }
    if (!this.pairings[id]) return false;
    delete this.pairings[id];
    this.savePairings();
    this.ev('unpaired', { id, by: 'me' });
    return true;
  }
}

// §6.3: last path component, `/ \ :` and control chars → `_`, no leading dots, ≤ 200 bytes, empty → `file`.
export function safeName(name) {
  let n = String(name).split(/[\/\\]/).pop().replace(/[:\x00-\x1f\x7f]/g, '_').replace(/^\.+/, '');
  if (Buffer.byteLength(n) > 200) n = Buffer.from(n).subarray(0, 200).toString().replace(/\uFFFD+$/, ''); // drop a cut multi-byte char
  return n || 'file';
}

export function uniquePath(dir, name) {
  const ext = path.extname(name), stem = name.slice(0, name.length - ext.length);
  for (let i = 0; ; i++) {
    const p = path.join(dir, i ? `${stem} (${i})${ext}` : name);
    if (!fs.existsSync(p)) return p;
  }
}

// ---- CLI ------------------------------------------------------------------------------------------------------
const HELP = `usage: ctrllink-peer.mjs <host|join|run> [target] [options]
  host                         open a pairing window (QR/URI + code), then stay as session peer
  join <ctrllink://...|code>   pair with a host, then stay as session peer
  run                          advertise, browse, auto-connect to paired peers
options: --state <dir> (./.peer-state)  --name <name>  --pf mac|android|windows  --port N (47823)
         --auto-accept  --accept-limit-mb N (100)  --connect host:port  --no-mdns  --json
stdin:   <text> | /file <path...> | /image <png> | /peers | /unpair <id> | /cancel <clipId> | y/n to answer prompts`;

function format(e) {
  const { ev, ...d } = e;
  switch (ev) {
    case 'pairing': return `pairing window open on port ${d.port}\n  code: ${d.code}\n  uri:  ${d.uri}`;
    case 'sas': return `SAS ${d.sas} with ${d.name}${d.mode === 'code' ? ' — confirm it matches on both screens' : ''}`;
    case 'clip': return `[${d.kind} from ${d.name}] ${d.text}`;
    case 'file': return `[file from ${d.from}] ${d.path} (${d.size} bytes, sha256 ${d.sha256} OK)`;
    case 'connected': return `connected to ${d.name} (${d.pf}, ${d.app}) as ${d.role}`;
    case 'prompt': return d.question;
    default: return `${ev} ${JSON.stringify(d)}`;
  }
}

async function main() {
  const { values: o, positionals: [cmd, target] } = parseArgs({
    allowPositionals: true,
    options: {
      state: { type: 'string', default: './.peer-state' }, name: { type: 'string' }, pf: { type: 'string' }, port: { type: 'string' },
      'auto-accept': { type: 'boolean' }, 'accept-limit-mb': { type: 'string' }, connect: { type: 'string' },
      'no-mdns': { type: 'boolean' }, json: { type: 'boolean' }, help: { type: 'boolean', short: 'h' },
    },
  });
  if (o.help || !['host', 'join', 'run'].includes(cmd) || (cmd === 'join' && !target)) { console.log(HELP); process.exit(o.help ? 0 : 2); }
  const print = (e) => console.log(o.json ? JSON.stringify(e) : format(e));
  const connect = o.connect ? (([h, p]) => [h, Number(p)])(o.connect.split(/:(?=\d+$)/)) : null;

  const pending = []; // prompts answered by the next stdin line(s)
  const prompt = (kind, info) => {
    if (kind === 'pair' && o['auto-accept']) return Promise.resolve(true);
    const question = kind === 'pair' ? `Accept ${info.name} SAS ${info.sas}? [y/N]` : `Accept ${info.files.length} file(s), ${info.size} bytes from ${info.name}? [y/N]`;
    print({ ev: 'prompt', kind, question, ...info });
    return new Promise((res) => pending.push(res));
  };

  const peer = new Peer({ state: o.state, name: o.name, pf: o.pf, port: o.port !== undefined ? Number(o.port) : 47823, acceptLimitMb: o['accept-limit-mb'] !== undefined ? Number(o['accept-limit-mb']) : 100, mdns: !connect && !o['no-mdns'], prompt });
  peer.on('event', print);
  await peer.start();

  readline.createInterface({ input: process.stdin }).on('line', (line) => {
    try {
      if (pending.length) return pending.shift()(/^\s*y(es)?\s*$/i.test(line));
      const [c, ...args] = [...line.matchAll(/"([^"]*)"|(\S+)/g)].map((x) => x[1] ?? x[2]);
      if (c === '/file') return peer.sendFiles(args, 'files');
      if (c === '/image') return peer.sendFiles(args.slice(0, 1), 'image');
      if (c === '/peers') return print({ ev: 'peers', peers: peer.peers() });
      if (c === '/unpair') return peer.unpair(args[0]) || print({ ev: 'error', message: `not paired: ${args[0]}` });
      if (c === '/cancel') return peer.cancel(args[0]);
      if (line.length) peer.sendText(line);
    } catch (e) { print({ ev: 'error', message: e.message }); }
  });
  process.on('SIGINT', () => peer.stop().then(() => process.exit(0)));

  if (cmd === 'host') {
    const { uri } = peer.openWindow();
    if (!o.json) (await import('qrcode-terminal')).default.generate(uri, { small: true });
  } else if (cmd === 'join') {
    try { await peer.join(target, { connect }); } catch (e) { print({ ev: 'error', message: e.message }); process.exitCode = 1; return peer.stop().then(() => process.exit(1)); }
  }
  if (connect && cmd !== 'host') {
    // --connect: keep a session to that address (emulators via adb, tests)
    const dial = () => { if (!peer.sessions.size) peer.connect(...connect).catch((e) => print({ ev: 'error', message: `dial: ${e.code ?? e.message}` })); };
    dial();
    setInterval(dial, 3000);
  }
}

if (import.meta.main) main().catch((e) => { console.error(e); process.exit(1); });
