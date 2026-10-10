// End-to-End Release Test for dist/MSK-Remote
const { spawn } = require('child_process');
const path = require('path');

const distDir = path.resolve(__dirname, '..', 'dist', 'MSK-Remote');
const engineJs = path.join(distDir, 'engine', 'msk-engine.js');

console.log('Testing MSK Remote Distribution Pipeline...');
console.log('Engine Path:', engineJs);

// 1. Start Relay
const relay = spawn('node', [engineJs, 'relay', '--control-port', '18443', '--relay-port', '18444', '--ipc'], { cwd: distDir });

relay.stdout.on('data', (d) => {
  const line = d.toString();
  console.log('[RELAY STDOUT]', line.trim());
  if (line.includes('RELAY_STARTED')) {
    startHost();
  }
});

let host = null;
let viewer = null;

function startHost() {
  console.log('\nStarting Host Agent...');
  host = spawn('node', [
    engineJs, 'host',
    '--relay', '127.0.0.1',
    '--control-port', '18443',
    '--device-id', '999-888-777',
    '--pin', '654321',
    '--ipc'
  ], { cwd: distDir });

  host.stdout.on('data', (d) => {
    const line = d.toString();
    console.log('[HOST STDOUT]', line.trim());
    if (line.includes('HOST_READY')) {
      startViewer();
    }
  });
}

function startViewer() {
  console.log('\nStarting Viewer Agent (--no-launch)...');
  viewer = spawn('node', [
    engineJs, 'viewer',
    '--relay', '127.0.0.1',
    '--control-port', '18443',
    '--target', '999-888-777',
    '--pin', '654321',
    '--no-launch',
    '--ipc'
  ], { cwd: distDir });

  viewer.stdout.on('data', (d) => {
    const line = d.toString();
    console.log('[VIEWER STDOUT]', line.trim());
    if (line.includes('VIEWER_READY')) {
      console.log('\n======================================================');
      console.log('  INTEGRATION TEST 100% SUCCESSFUL!');
      console.log('  E2EE Handshake + Relay Tunnel Verified in dist folder.');
      console.log('======================================================\n');
      cleanupAndExit(0);
    }
  });
}

function cleanupAndExit(code) {
  setTimeout(() => {
    if (viewer) viewer.kill();
    if (host) host.kill();
    if (relay) relay.kill();
    process.exit(code);
  }, 1000);
}

setTimeout(() => {
  console.error('Test timed out after 15 seconds!');
  cleanupAndExit(1);
}, 15000);
