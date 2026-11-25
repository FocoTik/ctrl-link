import test from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs';
import { x25519FromPriv, x25519Shared, pairingKeys, sessionKeys, seal, open } from '../crypto.mjs';

const V = JSON.parse(fs.readFileSync(new URL('../../test-vectors.json', import.meta.url)));
const h = (s) => Buffer.from(s, 'hex');
const hex = (b) => Buffer.from(b).toString('hex');

test('test-vectors.json reproduces byte for byte', () => {
  const host = x25519FromPriv(h(V.x25519.hostPriv));
  const joiner = x25519FromPriv(h(V.x25519.joinerPriv));
  assert.equal(hex(host.pub), V.x25519.hostPub);
  assert.equal(hex(joiner.pub), V.x25519.joinerPub);
  const shared = x25519Shared(host, joiner.pub);
  assert.equal(hex(shared), V.x25519.shared);
  assert.equal(hex(x25519Shared(joiner, host.pub)), V.x25519.shared);

  for (const [vec, secret] of [[V.pairing_qr, h(V.pairing_qr.token)], [V.pairing_code, Buffer.from(V.pairing_code.code)]]) {
    const k = pairingKeys(shared, secret, host.pub, joiner.pub);
    assert.deepEqual({ pairKey: hex(k.pairKey), sas: k.sas, joinerMac: hex(k.joinerMac), hostMac: hex(k.hostMac) },
      { pairKey: vec.pairKey, sas: vec.sas, joinerMac: vec.joinerMac, hostMac: vec.hostMac });
  }

  const S = V.session;
  const s = sessionKeys(h(S.pairKey), h(S.clientNonce), h(S.serverNonce));
  assert.equal(hex(s.c2s), S.c2sKey);
  assert.equal(hex(s.s2c), S.s2cKey);
  for (const f of S.frames) {
    const key = f.dir === 1 ? s.c2s : s.s2c;
    assert.equal(hex(seal(key, f.dir, f.counter, h(f.plaintext))), f.body);
    assert.equal(hex(open(key, f.dir, f.counter, h(f.body))), f.plaintext);
  }
});
