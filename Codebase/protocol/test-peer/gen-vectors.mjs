// Regenerates ../test-vectors.json from fixed inputs. Run: node protocol/test-peer/gen-vectors.mjs
import fs from 'node:fs';
import { x25519FromPriv, x25519Shared, pairingKeys, sessionKeys, seal, control, chunk } from './crypto.mjs';

const hex = (b) => Buffer.from(b).toString('hex');
const fill = (byte, n = 32) => Buffer.alloc(n, byte);

const host = x25519FromPriv(fill(0x11));
const joiner = x25519FromPriv(fill(0x22));
const shared = x25519Shared(host, joiner.pub);
if (!shared.equals(x25519Shared(joiner, host.pub))) throw new Error('x25519 mismatch');

const token = fill(0x33, 16);
const code = '042917';
const qr = pairingKeys(shared, token, host.pub, joiner.pub);
const cd = pairingKeys(shared, Buffer.from(code), host.pub, joiner.pub);

const clientNonce = fill(0x44);
const serverNonce = fill(0x55);
const s = sessionKeys(qr.pairKey, clientNonce, serverNonce);

const c0 = control({ t: 'ready', name: 'Test Mac', pf: 'mac', app: '1.0' });
const c1 = control({ t: 'clip', id: '00000000-0000-4000-8000-000000000001', ts: 1731050000000, kind: 'text', text: 'hello 👋' });
const s0 = control({ t: 'ready', name: 'Test Pixel', pf: 'android', app: '1.0' });
const s1 = chunk('00000000-0000-4000-8000-0000000000aa', Buffer.from('chunk-data'));

const out = {
  note: 'Fixed inputs → expected outputs for Ctrl Link Protocol v1. All byte values are hex. Plaintexts include the 1-byte kind prefix.',
  x25519: { hostPriv: hex(host.priv), hostPub: hex(host.pub), joinerPriv: hex(joiner.priv), joinerPub: hex(joiner.pub), shared: hex(shared) },
  pairing_qr: { token: hex(token), pairKey: hex(qr.pairKey), sas: qr.sas, joinerMac: hex(qr.joinerMac), hostMac: hex(qr.hostMac) },
  pairing_code: { code, pairKey: hex(cd.pairKey), sas: cd.sas, joinerMac: hex(cd.joinerMac), hostMac: hex(cd.hostMac) },
  session: {
    pairKey: hex(qr.pairKey), clientNonce: hex(clientNonce), serverNonce: hex(serverNonce), c2sKey: hex(s.c2s), s2cKey: hex(s.s2c),
    frames: [
      { from: 'client', dir: 1, counter: 0, plaintext: hex(c0), body: hex(seal(s.c2s, 1, 0, c0)) },
      { from: 'client', dir: 1, counter: 1, plaintext: hex(c1), body: hex(seal(s.c2s, 1, 1, c1)) },
      { from: 'server', dir: 2, counter: 0, plaintext: hex(s0), body: hex(seal(s.s2c, 2, 0, s0)) },
      { from: 'server', dir: 2, counter: 1, plaintext: hex(s1), body: hex(seal(s.s2c, 2, 1, s1)) },
    ],
  },
};

fs.writeFileSync(new URL('../test-vectors.json', import.meta.url), JSON.stringify(out, null, 2) + '\n');
console.log('wrote test-vectors.json; qr sas', qr.sas, 'code sas', cd.sas);
