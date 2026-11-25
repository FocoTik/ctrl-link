// Reference crypto for Ctrl Link Protocol v1 (see ../PROTOCOL.md). Node built-ins only.
import crypto from 'node:crypto';

const PKCS8_X25519 = Buffer.from('302e020100300506032b656e04220420', 'hex');

export function x25519FromPriv(priv) {
  const key = crypto.createPrivateKey({ key: Buffer.concat([PKCS8_X25519, priv]), format: 'der', type: 'pkcs8' });
  const pub = crypto.createPublicKey(key).export({ format: 'der', type: 'spki' }).subarray(-32);
  return { priv, pub: Buffer.from(pub), key };
}

export const x25519Generate = () => x25519FromPriv(crypto.randomBytes(32));

export function x25519Shared(me, peerPub) {
  const pubKey = crypto.createPublicKey({
    key: Buffer.concat([Buffer.from('302a300506032b656e032100', 'hex'), peerPub]), format: 'der', type: 'spki',
  });
  return crypto.diffieHellman({ privateKey: me.key, publicKey: pubKey });
}

export const hkdf = (ikm, salt, info, len) => Buffer.from(crypto.hkdfSync('sha256', ikm, salt, info, len));
export const hmac = (key, data) => crypto.createHmac('sha256', key).update(data).digest();

export function pairingKeys(shared, secret, hostPub, joinerPub) {
  const pairKey = hkdf(shared, secret, Buffer.concat([Buffer.from('ctrllink pair v1'), hostPub, joinerPub]), 32);
  const sasBytes = hkdf(shared, Buffer.alloc(0), Buffer.concat([Buffer.from('ctrllink sas v1'), hostPub, joinerPub]), 4);
  const sas = String(sasBytes.readUInt32BE(0) % 1000000).padStart(6, '0');
  const joinerMac = hmac(pairKey, Buffer.concat([Buffer.from('joiner'), hostPub, joinerPub]));
  const hostMac = hmac(pairKey, Buffer.concat([Buffer.from('host'), hostPub, joinerPub]));
  return { pairKey, sas, joinerMac, hostMac };
}

export function sessionKeys(pairKey, clientNonce, serverNonce) {
  const k = hkdf(pairKey, Buffer.concat([clientNonce, serverNonce]), Buffer.from('ctrllink session v1'), 64);
  return { c2s: k.subarray(0, 32), s2c: k.subarray(32, 64) };
}

export function nonce(dir, counter) {
  const n = Buffer.alloc(12);
  n.writeUInt32BE(dir, 0);
  n.writeBigUInt64BE(BigInt(counter), 4);
  return n;
}

export function seal(key, dir, counter, plaintext) {
  const c = crypto.createCipheriv('aes-256-gcm', key, nonce(dir, counter));
  return Buffer.concat([c.update(plaintext), c.final(), c.getAuthTag()]);
}

export function open(key, dir, counter, body) {
  const d = crypto.createDecipheriv('aes-256-gcm', key, nonce(dir, counter));
  d.setAuthTag(body.subarray(-16));
  return Buffer.concat([d.update(body.subarray(0, -16)), d.final()]);
}

export const control = (obj) => Buffer.concat([Buffer.from([1]), Buffer.from(JSON.stringify(obj))]);
export const chunk = (fileIdHex, data) => Buffer.concat([Buffer.from([2]), Buffer.from(fileIdHex.replace(/-/g, ''), 'hex'), data]);
