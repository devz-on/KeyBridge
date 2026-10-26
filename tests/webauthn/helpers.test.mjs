import test from 'node:test';
import assert from 'node:assert/strict';
import { createHash, generateKeyPairSync, randomBytes, sign } from 'node:crypto';
import { decodeCbor } from './cbor.mjs';
import { ChallengePool, base64Url, fromBase64Url, concatenate, derToP1363, verifyRegistration, verifyAssertion } from './webauthn.mjs';

const origin = 'http://localhost:8787';
const encodeText = (value) => new TextEncoder().encode(value);
const digest = (value) => new Uint8Array(createHash('sha256').update(value).digest());
const uint32 = (value) => { const bytes = new Uint8Array(4); new DataView(bytes.buffer).setUint32(0, value); return bytes; };

// Test fixture encoder only; production code contains a decoder, never generated authenticator responses.
function encodeCbor(value) {
  const header = (major, length) => length < 24 ? Uint8Array.of((major << 5) | length)
    : length <= 255 ? Uint8Array.of((major << 5) | 24, length)
      : length <= 65535 ? Uint8Array.of((major << 5) | 25, length >> 8, length & 255)
        : concatenate(Uint8Array.of((major << 5) | 26), uint32(length));
  if (typeof value === 'number') return header(value < 0 ? 1 : 0, value < 0 ? -1 - value : value);
  if (typeof value === 'string') { const bytes = encodeText(value); return concatenate(header(3, bytes.length), bytes); }
  if (value instanceof Uint8Array) return concatenate(header(2, value.length), value);
  if (value instanceof Map) return concatenate(header(5, value.size), ...Array.from(value, ([key, item]) => concatenate(encodeCbor(key), encodeCbor(item))));
  if (Array.isArray(value)) return concatenate(header(4, value.length), ...value.map(encodeCbor));
  if (value === true) return Uint8Array.of(0xf5);
  if (value === false) return Uint8Array.of(0xf4);
  if (value === null) return Uint8Array.of(0xf6);
  throw new Error('Unsupported test fixture value');
}

function fixture() {
  const { privateKey, publicKey } = generateKeyPairSync('ec', { namedCurve: 'prime256v1' });
  const jwk = publicKey.export({ format: 'jwk' });
  const id = new Uint8Array(randomBytes(32));
  const userId = new Uint8Array(randomBytes(32));
  const challenge = new Uint8Array(randomBytes(32));
  const cose = new Map([[1, 2], [3, -7], [-1, 1], [-2, fromBase64Url(jwk.x)], [-3, fromBase64Url(jwk.y)]]);
  const clientData = (type, options = {}) => encodeText(JSON.stringify({ type, challenge: base64Url(challenge), origin, crossOrigin: false, ...options }));
  const registration = (options = {}) => {
    const authData = options.authData ?? concatenate(digest('localhost'), Uint8Array.of(options.flags ?? 0x45), uint32(options.counter ?? 0), new Uint8Array(16), Uint8Array.of(0, id.length), id, encodeCbor(options.cose ?? cose));
    return { id: base64Url(id), rawId: id, type: 'public-key', response: {
      clientDataJSON: clientData('webauthn.create', options.clientData),
      attestationObject: encodeCbor(new Map([['fmt', options.format ?? 'none'], ['attStmt', options.statement ?? new Map()], ['authData', authData]]))
    } };
  };
  const assertion = (options = {}) => {
    const authData = concatenate(digest(options.rpId ?? 'localhost'), Uint8Array.of(options.flags ?? 5), uint32(options.counter ?? 1));
    const json = clientData('webauthn.get', options.clientData);
    const signature = new Uint8Array(sign('sha256', concatenate(authData, digest(json)), privateKey));
    return { id: base64Url(id), rawId: id, type: 'public-key', response: { clientDataJSON: json, authenticatorData: authData, signature, userHandle: options.userHandle ?? userId } };
  };
  const options = { challenge, origin, userId };
  return { id, userId, challenge, cose, options, registration, assertion };
}

test('bounded CBOR decodes maps, bytes, negative labels and booleans', () => {
  const value = decodeCbor(encodeCbor(new Map([['value', Uint8Array.of(1, 2)], [-7, [true, false, null, 65536]]])));
  assert.deepEqual(value.get('value'), Uint8Array.of(1, 2));
  assert.deepEqual(value.get(-7), [true, false, null, 65536]);
});

test('CBOR rejects truncation, duplicate keys, trailing data, indefinite values and invalid UTF-8', () => {
  for (const bytes of [Uint8Array.of(0x58, 24), Uint8Array.of(0xa2, 1, 2, 1, 3), Uint8Array.of(0, 0), Uint8Array.of(0x9f, 0xff), Uint8Array.of(0x61, 0xff), Uint8Array.of(0x18, 1), Uint8Array.of(0xf9, 0, 0), Uint8Array.of(0xc0, 0)]) assert.throws(() => decodeCbor(bytes));
});

test('CBOR rejects nesting, item, and input size abuse', () => {
  assert.throws(() => decodeCbor(Uint8Array.from([...Array(18).fill(0x81), 0])), /nesting/);
  assert.throws(() => decodeCbor(Uint8Array.of(0x99, 2, 1)), /container/);
  assert.throws(() => decodeCbor(new Uint8Array(65537)), /size/);
});

test('base64url is canonical and round-trips binary IDs', () => {
  const bytes = Uint8Array.of(0, 255, 128, 21);
  assert.deepEqual(fromBase64Url(base64Url(bytes)), bytes);
  for (const invalid of ['', '+/8=', 'AB', 'a', 'AA=']) assert.throws(() => fromBase64Url(invalid));
});

test('challenges are unpredictable, one-use and invalidated on wrong operation', () => {
  const pool = new ChallengePool();
  const first = pool.issue('register');
  assert.equal(first.length, 32);
  assert.throws(() => pool.issue('register'), /pending/);
  assert.deepEqual(pool.consume('register'), first);
  assert.throws(() => pool.consume('register'), /consumed/);
  const second = pool.issue('authenticate');
  assert.notDeepEqual(first, second);
  assert.throws(() => pool.consume('register'));
  assert.throws(() => pool.consume('authenticate'));
  pool.issue('register'); pool.clear();
  assert.throws(() => pool.consume('register'));
});

test('DER known short integers are padded to fixed-width r||s', () => {
  const converted = derToP1363(Uint8Array.of(0x30, 6, 2, 1, 1, 2, 1, 2));
  assert.equal(converted.length, 64);
  assert.equal(converted[31], 1);
  assert.equal(converted[63], 2);
  assert.equal(converted.slice(0, 31).every((value) => value === 0), true);
});

test('DER rejects negative, redundant, zero, length-mismatched and oversized integers', () => {
  for (const invalid of [Uint8Array.of(0x30, 6, 2, 1, 0x80, 2, 1, 2), Uint8Array.of(0x30, 7, 2, 2, 0, 1, 2, 1, 2), Uint8Array.of(0x30, 6, 2, 1, 0, 2, 1, 2), Uint8Array.of(0x30, 5, 2, 1, 1, 2, 1, 2), new Uint8Array(73)]) assert.throws(() => derToP1363(invalid));
});

test('registration validates none attestation and imports the public COSE key', async () => {
  const f = fixture();
  const record = await verifyRegistration(f.registration(), f.options);
  assert.equal(record.publicKey.type, 'public');
  assert.equal(record.publicKey.algorithm.namedCurve, 'P-256');
  assert.deepEqual(record.rawId, f.id);
  assert.equal(record.counter, 0);
});

test('independent OpenSSL-produced ES256 assertion verifies through WebCrypto', async () => {
  const f = fixture();
  const record = await verifyRegistration(f.registration(), f.options);
  const verified = await verifyAssertion(f.assertion(), record, f.options);
  assert.equal(verified.counter, 1);
  assert.equal(verified.flags, 5);
});

test('registration rejects wrong challenge, origin, operation and cross-origin state', async () => {
  const f = fixture();
  for (const clientData of [{ challenge: base64Url(new Uint8Array(32)) }, { origin: 'https://evil.invalid' }, { type: 'webauthn.get' }, { crossOrigin: true }, { topOrigin: origin }]) await assert.rejects(verifyRegistration(f.registration({ clientData }), f.options));
});

test('registration rejects missing UV/UP and invalid flag combinations', async () => {
  const f = fixture();
  for (const flags of [0x41, 0x44, 0x47, 0x55]) await assert.rejects(verifyRegistration(f.registration({ flags }), f.options));
});

test('registration rejects unsupported attestation and private COSE material', async () => {
  const f = fixture();
  await assert.rejects(verifyRegistration(f.registration({ format: 'packed' }), f.options), /none attestation/);
  await assert.rejects(verifyRegistration(f.registration({ statement: new Map([['sig', Uint8Array.of(1)]]) }), f.options), /empty statement/);
  const key = new Map(f.cose); key.set(-4, new Uint8Array(32));
  await assert.rejects(verifyRegistration(f.registration({ cose: key }), f.options), /public ES256/);
});

test('registration rejects attested credential ID mismatch and malformed key coordinates', async () => {
  const f = fixture();
  const changed = f.registration(); changed.rawId = new Uint8Array(32); changed.id = base64Url(changed.rawId);
  await assert.rejects(verifyRegistration(changed, f.options), /Attested credential ID/);
  const key = new Map(f.cose); key.set(-2, Uint8Array.of(1));
  await assert.rejects(verifyRegistration(f.registration({ cose: key }), f.options), /coordinates/);
});

test('assertion rejects wrong challenge, origin and operation type', async () => {
  const f = fixture();
  const record = await verifyRegistration(f.registration(), f.options);
  for (const clientData of [{ challenge: base64Url(new Uint8Array(32)) }, { origin: 'https://evil.invalid' }, { type: 'webauthn.create' }]) await assert.rejects(verifyAssertion(f.assertion({ clientData }), record, f.options));
});

test('assertion rejects wrong RP hash, missing verification and changed backup eligibility', async () => {
  const f = fixture();
  const record = await verifyRegistration(f.registration(), f.options);
  await assert.rejects(verifyAssertion(f.assertion({ rpId: 'example.invalid' }), record, f.options), /RP ID hash/);
  await assert.rejects(verifyAssertion(f.assertion({ flags: 1 }), record, f.options), /verification/);
  await assert.rejects(verifyAssertion(f.assertion({ flags: 13 }), record, f.options), /Backup eligibility/);
});

test('assertion rejects another credential or user handle', async () => {
  const f = fixture();
  const record = await verifyRegistration(f.registration(), f.options);
  const changed = f.assertion(); changed.rawId = new Uint8Array(32); changed.id = base64Url(changed.rawId);
  await assert.rejects(verifyAssertion(changed, record, f.options), /unexpected credential/);
  await assert.rejects(verifyAssertion(f.assertion({ userHandle: new Uint8Array(32) }), record, f.options), /User handle/);
});

test('assertion rejects a corrupted signature without advancing stored counter', async () => {
  const f = fixture();
  const record = await verifyRegistration(f.registration(), f.options);
  const changed = f.assertion(); changed.response.signature[10] ^= 1;
  await assert.rejects(verifyAssertion(changed, record, f.options), /signature verification/);
  assert.equal(record.counter, 0);
});

test('nonzero counters must increase, including reset-to-zero rejection', async () => {
  const f = fixture();
  const record = await verifyRegistration(f.registration(), f.options);
  record.counter = 2;
  for (const counter of [0, 1, 2]) await assert.rejects(verifyAssertion(f.assertion({ counter }), record, f.options), /did not increase/);
  assert.equal((await verifyAssertion(f.assertion({ counter: 3 }), record, f.options)).counter, 3);
});

test('consistent zero counter policy is accepted without claiming a monotonic counter', async () => {
  const f = fixture();
  const record = await verifyRegistration(f.registration(), f.options);
  assert.equal((await verifyAssertion(f.assertion({ counter: 0 }), record, f.options)).counter, 0);
});
