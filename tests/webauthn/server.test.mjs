import assert from 'node:assert/strict';
import { spawn, spawnSync } from 'node:child_process';
import { request } from 'node:http';
import { createConnection } from 'node:net';
import { fileURLToPath } from 'node:url';

const dotnet = process.env.KEYBRIDGE_DOTNET ?? 'dotnet';
const project = fileURLToPath(new URL('./server/KeyBridge.WebAuthn.Server.csproj', import.meta.url));
const build = spawnSync(dotnet, ['build', project, '-c', 'Release', '--nologo'], { windowsHide: true, encoding: 'utf8' });
if (build.error || build.status !== 0) throw new Error(`Server build failed. Set KEYBRIDGE_DOTNET to your .NET 10 SDK executable if needed. ${build.error?.message ?? build.stdout + build.stderr}`);
const server = spawn(dotnet, [fileURLToPath(new URL('./server/bin/Release/net10.0/KeyBridge.WebAuthn.Server.dll', import.meta.url)), fileURLToPath(new URL('.', import.meta.url))], { windowsHide: true, stdio: ['ignore', 'pipe', 'pipe'] });
let output = '';
const started = new Promise((resolve, reject) => {
  const timeout = setTimeout(() => reject(new Error(`Server did not start: ${output}`)), 10000);
  server.stdout.on('data', (chunk) => { output += chunk; if (output.includes('diagnostic listening')) { clearTimeout(timeout); resolve(); } });
  server.stderr.on('data', (chunk) => { output += chunk; });
  server.once('error', (error) => { clearTimeout(timeout); reject(error); });
  server.once('exit', (code) => { clearTimeout(timeout); reject(new Error(`Server exited (${code}): ${output}`)); });
});
const probe = (path, options = {}) => new Promise((resolve, reject) => {
  const operation = request({ hostname: '127.0.0.1', port: 8787, path, method: options.method ?? 'GET', headers: { Host: 'localhost:8787', ...(options.headers ?? {}) }, timeout: 5000 }, (response) => {
    const chunks = [];
    response.on('data', (chunk) => chunks.push(chunk));
    response.on('end', () => resolve({ status: response.statusCode, headers: response.headers, body: Buffer.concat(chunks).toString('utf8') }));
  });
  operation.once('error', reject);
  operation.once('timeout', () => operation.destroy(new Error('HTTP probe timed out')));
  operation.end();
});
const rawProbe = (headers) => new Promise((resolve, reject) => {
  const connection = createConnection({ host: '127.0.0.1', port: 8787 });
  let response = '';
  connection.setTimeout(5000, () => connection.destroy(new Error('Raw HTTP probe timed out')));
  connection.once('connect', () => connection.write(headers));
  connection.on('data', (chunk) => { response += chunk; });
  connection.once('end', () => resolve(response));
  connection.once('error', reject);
});

try {
  await started;
  const root = await probe('/');
  assert.equal(root.status, 200);
  assert.match(root.body, /Local WebAuthn diagnostic/);
  assert.match(root.headers['content-security-policy'], /frame-ancestors 'none'/);
  assert.equal(root.headers['cache-control'], 'no-store');
  assert.equal(root.headers['access-control-allow-origin'], undefined);
  const module = await probe('/app.mjs');
  assert.equal(module.status, 200);
  assert.match(module.headers['content-type'], /text\/javascript/);
  assert.equal((await probe('/', { method: 'HEAD' })).body, '');
  for (const path of ['/../README.md', '/%2e%2e/README.md', '/helpers.test.mjs', '/serve.ps1']) assert.equal((await probe(path)).status, 404);
  assert.equal((await probe('/', { method: 'POST' })).status, 405);
  assert.equal((await probe('/', { headers: { Host: 'evil.invalid' } })).status, 403);
  assert.equal((await probe('/', { headers: { Origin: 'https://evil.invalid' } })).status, 403);
  assert.match(await rawProbe('GET / HTTP/1.1\r\nHost: localhost:8787\r\nHost: localhost:8787\r\n\r\n'), /^HTTP\/1.1 400/);
  assert.match(await rawProbe('GET / HTTP/1.1\r\nHost: localhost:8787\r\nBad Header: test\r\n\r\n'), /^HTTP\/1.1 400/);
  assert.equal((await probe('/')).status, 200, 'Malformed requests must not kill the server');
  console.log('PASS: local static server, module MIME, HEAD, CSP, no CORS, path whitelist, method/Host/Origin/header rejection and recovery. No browser/authenticator operation ran.');
} finally {
  // Terminate only the child server created by this test, never an existing listener.
  server.kill();
}
