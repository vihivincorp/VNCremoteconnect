// End-to-end verification test for:
// 1. Permanent Device ID & Password
// 2. Unattended auto-hosting (no sharing prompts)
// 3. Disconnect and seamless reconnect with the same credentials

const { spawn } = require('child_process');
const path = require('path');
const net = require('net');

const enginePath = path.join(__dirname, 'vnc-engine.js');
const TEST_ID = '777-888-999';
const TEST_PASS = 'secretpass123';
const CONTROL_PORT = 19443;
const RELAY_PORT = 19444;

console.log('--- STARTING PERMANENT UNATTENDED RECONNECT TEST ---');

// 1. Start Relay Server
const relay = spawn('node', [enginePath, 'relay', '--control-port', String(CONTROL_PORT), '--relay-port', String(RELAY_PORT), '--ipc', '--no-tls']);

relay.stdout.on('data', (d) => {
  const line = d.toString();
  if (line.includes('RELAY_STARTED')) {
    console.log('[TEST] 1. Relay Server Started successfully.');
    startHost();
  }
});

let host = null;
function startHost() {
  console.log(`[TEST] 2. Starting Host with Permanent ID [${TEST_ID}] and Password [${TEST_PASS}]...`);
  host = spawn('node', [
    enginePath, 'host',
    '--relay', '127.0.0.1',
    '--control-port', String(CONTROL_PORT),
    '--device-id', TEST_ID,
    '--pin', TEST_PASS,
    '--ipc',
    '--no-tls'
  ]);

  host.stdout.on('data', (d) => {
    const text = d.toString();
    if (text.includes('HOST_READY')) {
      console.log('[TEST] 3. Host is ONLINE & REGISTERED with permanent ID!');
      startViewer1();
    }
  });

  host.stderr.on('data', (d) => console.error('[HOST ERR]', d.toString()));
}

let viewer1 = null;
function startViewer1() {
  console.log('[TEST] 4. Launching Viewer #1 (First Connection)...');
  viewer1 = spawn('node', [
    enginePath, 'viewer',
    '--relay', '127.0.0.1',
    '--control-port', String(CONTROL_PORT),
    '--target', TEST_ID,
    '--pin', TEST_PASS,
    '--ipc',
    '--no-tls',
    '--no-launch'
  ]);

  viewer1.stdout.on('data', (d) => {
    const text = d.toString();
    if (text.includes('VIEWER_READY')) {
      console.log('[TEST] 5. Viewer #1 CONNECTED & SECURE TUNNEL ESTABLISHED!');
      // Now disconnect viewer #1 to simulate network drop / session end
      setTimeout(() => {
        console.log('[TEST] 6. Simulating disconnect: Killing Viewer #1...');
        viewer1.kill();
        setTimeout(startViewer2, 1000);
      }, 1500);
    }
  });

  viewer1.stderr.on('data', (d) => console.error('[VIEWER1 ERR]', d.toString()));
}

let viewer2 = null;
function startViewer2() {
  console.log('[TEST] 7. Testing RECONNECTION with the EXACT SAME ID and Password...');
  viewer2 = spawn('node', [
    enginePath, 'viewer',
    '--relay', '127.0.0.1',
    '--control-port', String(CONTROL_PORT),
    '--target', TEST_ID,
    '--pin', TEST_PASS,
    '--ipc',
    '--no-tls',
    '--no-launch'
  ]);

  viewer2.stdout.on('data', (d) => {
    const text = d.toString();
    if (text.includes('VIEWER_READY')) {
      console.log('[TEST] 8. SUCCESS! Viewer #2 RECONNECTED effortlessly using the same ID & Password!');
      cleanupAndExit(0);
    }
  });

  viewer2.stderr.on('data', (d) => console.error('[VIEWER2 ERR]', d.toString()));
}

function cleanupAndExit(code) {
  console.log('[TEST] Cleaning up test processes...');
  try { if (viewer1) viewer1.kill(); } catch (e) {}
  try { if (viewer2) viewer2.kill(); } catch (e) {}
  try { if (host) host.kill(); } catch (e) {}
  try { if (relay) relay.kill(); } catch (e) {}
  console.log('--- TEST FINISHED WITH CODE ' + code + ' ---');
  process.exit(code);
}

setTimeout(() => {
  console.error('[TEST] Timeout reached!');
  cleanupAndExit(1);
}, 15000);
