import { RP_ID, ChallengePool, base64Url, verifyRegistration, verifyAssertion } from './webauthn.mjs';

const element = (id) => document.getElementById(id);
const challenges = new ChallengePool();
let record = null;
let active = null;
let eventCount = 0;
const ready = location.protocol === 'http:' && location.hostname === 'localhost' && location.port === '8787' &&
  window.top === window.self && window.isSecureContext && typeof PublicKeyCredential !== 'undefined' && Boolean(navigator.credentials) && Boolean(crypto.subtle);

element('availability').textContent = ready
  ? 'Browser WebAuthn API is available. Windows driver and KeyBridge device detection are not verified by this page.'
  : 'Open http://localhost:8787 in a browser with WebAuthn and WebCrypto support, outside an iframe. No test can run in this context.';

function log(message) {
  if (eventCount++ === 0) element('events').replaceChildren();
  const row = document.createElement('li');
  row.textContent = `${new Date().toLocaleTimeString()} · ${message}`;
  element('events').prepend(row);
  while (element('events').children.length > 50) element('events').lastChild.remove();
}

function result(state, label, title, detail) {
  element('result').dataset.state = state;
  element('result-label').textContent = label;
  element('result-title').textContent = title;
  element('result-detail').textContent = detail;
}

function refresh() {
  element('register').disabled = !ready || Boolean(active) || Boolean(record);
  element('authenticate').disabled = !ready || Boolean(active) || !record;
  element('cancel').disabled = !active;
  element('clear-state').disabled = Boolean(active) || !record;
  element('credential').textContent = record ? `${base64Url(record.rawId).slice(0, 12)}… (${record.rawId.length} bytes, page memory only)` : 'None in page memory';
  element('attestation').textContent = record ? 'none · does not identify hardware or manufacturer' : 'Not tested';
  element('counter').textContent = record ? record.counter.toString() : 'Not available';
  element('assertions').textContent = record ? record.assertions.toString() : '0';
}

function readableError(error) {
  if (error?.name === 'AbortError') return 'The request was cancelled or reached the 90-second diagnostic deadline.';
  if (error?.name === 'NotAllowedError') return 'Windows or the authenticator did not complete the request. It may have been cancelled, timed out, unavailable, or rejected on the phone. Check the companion, driver, and phone.';
  if (error?.name === 'SecurityError') return 'The browser rejected the relying-party context. Use http://localhost:8787, not a file URL or IP address.';
  return error instanceof Error ? error.message : 'The browser operation failed.';
}

async function operate(operation) {
  if (!ready || active || (operation === 'register' && record) || (operation === 'authenticate' && !record)) return;
  const controller = new AbortController();
  active = { operation, controller };
  const challenge = challenges.issue(operation);
  const deadline = setTimeout(() => controller.abort(), 90000);
  refresh();
  result('pending', 'WAITING FOR YOU', operation === 'register' ? 'Registration requested from Windows.' : 'Authentication requested from Windows.', 'Choose the USB security key and inspect the request on Android. Approve with your fingerprint, or cancel. No success has been verified yet.');
  log(`${operation === 'register' ? 'Registration' : 'Authentication'} started with a fresh, one-use challenge.`);
  try {
    if (operation === 'register') {
      const userId = crypto.getRandomValues(new Uint8Array(32));
      const credential = await navigator.credentials.create({
        signal: controller.signal,
        publicKey: {
          challenge,
          rp: { id: RP_ID, name: 'KeyBridge local diagnostic' },
          user: { id: userId, name: 'diagnostic@localhost', displayName: 'Local KeyBridge diagnostic' },
          pubKeyCredParams: [{ type: 'public-key', alg: -7 }],
          authenticatorSelection: { authenticatorAttachment: 'cross-platform', residentKey: 'preferred', userVerification: 'required' },
          timeout: 90000,
          attestation: 'none',
          hints: ['security-key']
        }
      });
      const expected = challenges.consume(operation);
      if (controller.signal.aborted) throw new DOMException('Cancelled', 'AbortError');
      const registered = await verifyRegistration(credential, { challenge: expected, origin: location.origin, userId });
      if (controller.signal.aborted) throw new DOMException('Cancelled', 'AbortError');
      record = registered;
      result('passed', 'REGISTRATION VALIDATED', 'The public credential response is valid.', 'Challenge, origin, RP ID hash, ES256 public key, and presence/verification flags passed. None attestation supplies no key-possession proof; authenticate next to verify a real signature. Confirm that the phone handled this request.');
      log('Registration response validated. Assertion signature verification is still pending.');
    } else {
      const credential = await navigator.credentials.get({
        signal: controller.signal,
        publicKey: {
          challenge,
          rpId: RP_ID,
          allowCredentials: [{ type: 'public-key', id: record.rawId, transports: ['usb'] }],
          userVerification: 'required',
          timeout: 90000,
          hints: ['security-key']
        }
      });
      const expected = challenges.consume(operation);
      if (controller.signal.aborted) throw new DOMException('Cancelled', 'AbortError');
      const verified = await verifyAssertion(credential, record, { challenge: expected, origin: location.origin });
      if (controller.signal.aborted) throw new DOMException('Cancelled', 'AbortError');
      record.counter = verified.counter;
      record.flags = verified.flags;
      record.assertions++;
      result('passed', 'SIGNATURE VERIFIED', 'The assertion is cryptographically valid.', 'WebCrypto verified the ES256 signature over authenticator data and the client-data hash. Challenge, origin, RP hash, credential/user identity, UP/UV flags, and counter policy passed. Confirm KeyBridge and the phone were the authenticator; this page cannot identify a none-attested backend.');
      log(`Assertion signature verified. Accepted counter: ${verified.counter}.`);
    }
  } catch (error) {
    result('failed', 'NOT VERIFIED', 'The operation did not pass.', readableError(error));
    log(`Operation rejected: ${readableError(error)}`);
  } finally {
    clearTimeout(deadline);
    challenges.clear();
    active = null;
    refresh();
  }
}

element('register').addEventListener('click', () => operate('register'));
element('authenticate').addEventListener('click', () => operate('authenticate'));
element('cancel').addEventListener('click', () => active?.controller.abort());
element('clear-state').addEventListener('click', () => {
  if (active) return;
  record = null;
  challenges.clear();
  result('idle', 'PAGE STATE CLEARED', 'No credential remains in page memory.', 'The Android credential was not deleted. Remove the localhost test credential in the phone vault when finished.');
  log('Public page state cleared; authenticator credential remains on its device.');
  refresh();
});
window.addEventListener('pagehide', () => { active?.controller.abort(); challenges.clear(); record = null; });
refresh();
