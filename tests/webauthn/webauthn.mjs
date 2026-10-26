import { decodeCbor, decodeCborPrefix } from './cbor.mjs';

const encoder = new TextEncoder();
const decoder = new TextDecoder('utf-8', { fatal: true });
export const RP_ID = 'localhost';

export function asBytes(value, maximum = 65536) {
  const bytes = value instanceof Uint8Array ? value : value instanceof ArrayBuffer ? new Uint8Array(value) : null;
  if (!bytes || bytes.length > maximum) throw new Error('Missing or oversized binary response.');
  return bytes;
}

export function equalBytes(first, second) {
  const a = asBytes(first);
  const b = asBytes(second);
  if (a.length !== b.length) return false;
  let difference = 0;
  for (let i = 0; i < a.length; i++) difference |= a[i] ^ b[i];
  return difference === 0;
}

export function base64Url(bytes) {
  let binary = '';
  for (const value of asBytes(bytes)) binary += String.fromCharCode(value);
  return btoa(binary).replaceAll('+', '-').replaceAll('/', '_').replace(/=+$/, '');
}

export function fromBase64Url(value, maximum = 65536) {
  if (typeof value !== 'string' || value.length === 0 || value.length > Math.ceil(maximum * 4 / 3) || !/^[A-Za-z0-9_-]+$/.test(value) || value.length % 4 === 1) throw new Error('Invalid base64url value.');
  const binary = atob(value.replaceAll('-', '+').replaceAll('_', '/') + '='.repeat((4 - value.length % 4) % 4));
  const bytes = Uint8Array.from(binary, (character) => character.charCodeAt(0));
  if (bytes.length > maximum || base64Url(bytes) !== value) throw new Error('Non-canonical base64url value.');
  return bytes;
}

export function concatenate(...arrays) {
  const length = arrays.reduce((sum, value) => sum + value.length, 0);
  if (length > 131072) throw new Error('Combined response is too large.');
  const result = new Uint8Array(length);
  let offset = 0;
  for (const array of arrays) { result.set(array, offset); offset += array.length; }
  return result;
}

export class ChallengePool {
  #pending = null;
  issue(operation) {
    if (this.#pending) throw new Error('Another challenge is pending.');
    const bytes = crypto.getRandomValues(new Uint8Array(32));
    this.#pending = { operation, bytes: bytes.slice() };
    return bytes;
  }
  consume(operation) {
    const challenge = this.#pending;
    this.#pending = null;
    if (!challenge || challenge.operation !== operation) throw new Error('Challenge is missing, consumed, or belongs to another operation.');
    return challenge.bytes;
  }
  clear() { this.#pending = null; }
}

async function validateClientData(input, type, challenge, origin) {
  const bytes = asBytes(input, 16384);
  const data = JSON.parse(decoder.decode(bytes));
  if (!data || typeof data !== 'object' || Array.isArray(data)) throw new Error('Invalid client data.');
  if (data.type !== type) throw new Error('Wrong WebAuthn operation type.');
  if (!equalBytes(fromBase64Url(data.challenge, 1024), challenge)) throw new Error('Challenge does not match the one-use request.');
  if (data.origin !== origin) throw new Error('Browser origin does not match this diagnostic.');
  if (data.crossOrigin === true || (data.crossOrigin !== undefined && data.crossOrigin !== false) || data.topOrigin !== undefined) throw new Error('Cross-origin WebAuthn responses are not accepted.');
  return new Uint8Array(await crypto.subtle.digest('SHA-256', bytes));
}

async function parseAuthData(input, rpId, registration) {
  const data = asBytes(input);
  if (data.length < 37) throw new Error('Authenticator data is truncated.');
  const expectedHash = new Uint8Array(await crypto.subtle.digest('SHA-256', encoder.encode(rpId)));
  if (!equalBytes(data.slice(0, 32), expectedHash)) throw new Error('RP ID hash mismatch.');
  const flags = data[32];
  if ((flags & 1) === 0) throw new Error('User presence flag is missing.');
  if ((flags & 4) === 0) throw new Error('User verification flag is missing.');
  if (flags & 0x22) throw new Error('Reserved authenticator flags are set.');
  if ((flags & 0x10) && !(flags & 0x08)) throw new Error('Backup state requires backup eligibility.');
  if (Boolean(flags & 0x40) !== registration) throw new Error('Attested credential flag does not match the operation.');
  const counter = new DataView(data.buffer, data.byteOffset, data.byteLength).getUint32(33);
  return { data, flags, counter };
}

function validateCredentialEnvelope(credential) {
  if (!credential || credential.type !== 'public-key' || !credential.response) throw new Error('The browser did not return a public-key credential.');
  const id = asBytes(credential.rawId, 1023);
  if (id.length === 0 || credential.id !== base64Url(id)) throw new Error('Credential ID encoding does not match rawId.');
  return id;
}

function consumeExtensions(data, offset, flags) {
  if (flags & 0x80) {
    const extensions = decodeCborPrefix(data, offset);
    if (!(extensions.value instanceof Map)) throw new Error('Authenticator extensions must be a CBOR map.');
    offset = extensions.offset;
  }
  if (offset !== data.length) throw new Error('Unexpected bytes after authenticator data.');
}

export async function verifyRegistration(credential, { challenge, origin, rpId = RP_ID, userId }) {
  const rawId = validateCredentialEnvelope(credential);
  await validateClientData(credential.response.clientDataJSON, 'webauthn.create', challenge, origin);
  const attestation = decodeCbor(asBytes(credential.response.attestationObject));
  if (!(attestation instanceof Map) || attestation.get('fmt') !== 'none') throw new Error('This diagnostic accepts only none attestation.');
  const statement = attestation.get('attStmt');
  if (!(statement instanceof Map) || statement.size !== 0) throw new Error('None attestation must have an empty statement.');
  const parsed = await parseAuthData(attestation.get('authData'), rpId, true);
  const { data, flags, counter } = parsed;
  if (data.length < 55) throw new Error('Attested credential data is truncated.');
  const idLength = data[53] * 256 + data[54];
  if (idLength < 1 || idLength > 1023 || data.length < 55 + idLength) throw new Error('Invalid attested credential ID length.');
  if (!equalBytes(data.slice(55, 55 + idLength), rawId)) throw new Error('Attested credential ID does not match the browser response.');
  const cose = decodeCborPrefix(data, 55 + idLength);
  const key = cose.value;
  if (!(key instanceof Map) || key.get(1) !== 2 || key.get(3) !== -7 || key.get(-1) !== 1 || key.has(-4)) throw new Error('Expected a public ES256/P-256 COSE key.');
  const x = asBytes(key.get(-2), 32);
  const y = asBytes(key.get(-3), 32);
  if (x.length !== 32 || y.length !== 32) throw new Error('P-256 coordinates must be 32 bytes.');
  consumeExtensions(data, cose.offset, flags);
  const publicJwk = { kty: 'EC', crv: 'P-256', x: base64Url(x), y: base64Url(y), ext: true };
  const publicKey = await crypto.subtle.importKey('jwk', publicJwk, { name: 'ECDSA', namedCurve: 'P-256' }, false, ['verify']);
  const account = asBytes(userId, 64);
  if (account.length === 0) throw new Error('The test user handle must not be empty.');
  return { rawId: rawId.slice(), publicKey, publicJwk, counter, flags, userId: account.slice(), aaguid: data.slice(37, 53), assertions: 0 };
}

// WebAuthn ES256 signatures use DER; WebCrypto ECDSA uses fixed-width r || s.
export function derToP1363(input) {
  const bytes = asBytes(input, 72);
  if (bytes.length < 8 || bytes[0] !== 0x30 || bytes[1] !== bytes.length - 2) throw new Error('Invalid DER ECDSA sequence.');
  let offset = 2;
  const readInteger = () => {
    if (bytes[offset++] !== 0x02) throw new Error('Invalid DER ECDSA integer.');
    const length = bytes[offset++];
    if (length < 1 || length > 33 || offset + length > bytes.length) throw new Error('Invalid DER integer length.');
    const integer = bytes.slice(offset, offset + length);
    offset += length;
    if ((integer[0] & 0x80) || (length > 1 && integer[0] === 0 && !(integer[1] & 0x80))) throw new Error('Non-canonical DER integer.');
    const magnitude = integer[0] === 0 ? integer.slice(1) : integer;
    if (magnitude.length > 32 || magnitude.every((value) => value === 0)) throw new Error('Invalid P-256 integer.');
    const padded = new Uint8Array(32);
    padded.set(magnitude, 32 - magnitude.length);
    return padded;
  };
  const r = readInteger();
  const s = readInteger();
  if (offset !== bytes.length) throw new Error('Trailing DER signature data.');
  return concatenate(r, s);
}

export async function verifyAssertion(credential, record, { challenge, origin, rpId = RP_ID }) {
  const rawId = validateCredentialEnvelope(credential);
  if (!equalBytes(rawId, record.rawId)) throw new Error('Assertion returned an unexpected credential ID.');
  const clientHash = await validateClientData(credential.response.clientDataJSON, 'webauthn.get', challenge, origin);
  const { data, flags, counter } = await parseAuthData(credential.response.authenticatorData, rpId, false);
  if ((flags & 8) !== (record.flags & 8)) throw new Error('Backup eligibility changed after registration.');
  consumeExtensions(data, 37, flags);
  if (credential.response.userHandle !== null && credential.response.userHandle !== undefined && !equalBytes(asBytes(credential.response.userHandle, 64), record.userId)) throw new Error('User handle does not match the registered test account.');
  const verified = await crypto.subtle.verify({ name: 'ECDSA', hash: 'SHA-256' }, record.publicKey, derToP1363(credential.response.signature), concatenate(data, clientHash));
  if (!verified) throw new Error('Assertion signature verification failed.');
  if ((counter !== 0 || record.counter !== 0) && counter <= record.counter) throw new Error('Signature counter did not increase; assertion rejected.');
  return { counter, flags, backupEligible: Boolean(flags & 8), backedUp: Boolean(flags & 16) };
}
