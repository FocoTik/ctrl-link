import test from 'node:test';
import assert from 'node:assert/strict';
import { spawnSync } from 'node:child_process';
import crypto from 'node:crypto';
import { dnssdAdvertise, dnssdBrowse, dnssdResolve } from '../ctrllink-peer.mjs';

const hasDnssd = spawnSync('which', ['dns-sd']).status === 0;

test('dns-sd registers and browses itself', { skip: !hasDnssd && 'dns-sd not found', timeout: 20000 }, async () => {
  const id = crypto.randomUUID();
  const name = `ctrllink-test-${id.slice(0, 8)}`;
  const adv = dnssdAdvertise(name, 47999, { v: 1, id, pf: 'mac', pair: 1 });
  let browser;
  try {
    const found = await new Promise((res, rej) => {
      const t = setTimeout(() => rej(new Error('not found via browse')), 15000);
      browser = dnssdBrowse((n) => { if (n === name) dnssdResolve(n).then((s) => { clearTimeout(t); res(s); }, rej); });
    });
    assert.equal(found.port, 47999);
    assert.deepEqual(found.txt, { v: '1', id, pf: 'mac', pair: '1' });
  } finally { adv.kill(); browser?.kill(); }
});
