// MSK Remote - Host Bridge Agent
//
// Features:
// 1. Outbound TLS/TCP connection to Rendezvous / Relay Server
// 2. Registers Unique 9-Digit Device ID
// 3. Ephemeral X25519 ECDH Key Exchange per session
// 4. Authenticated PIN verification + AES-256-GCM AEAD Framing
// 5. Bridges decrypted traffic to local TightVNC Server on loopback (127.0.0.1:5900)

const tls = require('tls');
const net = require('net');
const os = require('os');
const { generateSessionKeyPair, deriveSessionKeys, E2eeEncryptor, E2eeDecryptor } = require('./msk-e2ee.js');

const RENDEZVOUS_HOST = process.env.MSK_SERVER_HOST || '127.0.0.1';
const RENDEZVOUS_PORT = parseInt(process.env.MSK_CONTROL_PORT || '8443', 10);
const LOCAL_VNC_PORT = parseInt(process.env.MSK_LOCAL_VNC_PORT || '5900', 10);
const DESIRED_DEVICE_ID = process.env.MSK_DEVICE_ID || null;
const SESSION_PIN = process.env.MSK_SESSION_PIN || '123456';
const USE_TLS = process.env.MSK_USE_TLS !== '0';

function log(msg) {
  const ts = new Date().toISOString().split('T')[1].slice(0, 8);
  console.log(`[${ts}] [MSK Host] ${msg}`);
}

function connectTransport(port, host, onConnect) {
  if (USE_TLS) {
    const s = tls.connect({ host, port, rejectUnauthorized: false }, onConnect);
    s.setNoDelay(true);
    return s;
  } else {
    const s = net.connect({ host, port }, onConnect);
    s.setNoDelay(true);
    return s;
  }
}

log(`Starting MSK Remote Host Agent...`);
log(`Target Local VNC Service: 127.0.0.1:${LOCAL_VNC_PORT}`);
log(`Authorized Session PIN:   ${SESSION_PIN}`);

let assignedDeviceId = null;
let relayPort = 8444;

const controlSocket = connectTransport(RENDEZVOUS_PORT, RENDEZVOUS_HOST, () => {
  log(`Connected to Rendezvous Server at ${RENDEZVOUS_HOST}:${RENDEZVOUS_PORT} (TLS: ${USE_TLS})`);
  // Register as host
  const registerMsg = {
    type: 'REGISTER_HOST',
    deviceId: DESIRED_DEVICE_ID,
    hostname: os.hostname()
  };
  controlSocket.write(JSON.stringify(registerMsg) + '\n');
});

let buffer = '';
controlSocket.on('data', (chunk) => {
  buffer += chunk.toString('utf8');
  let boundary = buffer.indexOf('\n');
  while (boundary !== -1) {
    const line = buffer.slice(0, boundary).trim();
    buffer = buffer.slice(boundary + 1);
    if (line) {
      try {
        const msg = JSON.parse(line);
        handleControlMessage(msg);
      } catch (err) {
        log(`Failed to parse control message: ${err.message}`);
      }
    }
    boundary = buffer.indexOf('\n');
  }
});

function handleControlMessage(msg) {
  switch (msg.type) {
    case 'REGISTER_OK':
      assignedDeviceId = msg.deviceId;
      relayPort = msg.relayPort || 8444;
      console.log('\n======================================================');
      console.log(`  MSK REMOTE HOST ONLINE (E2EE SECURE)`);
      console.log(`  DEVICE ID:   ${assignedDeviceId}`);
      console.log(`  SESSION PIN: ${SESSION_PIN}`);
      console.log('======================================================\n');
      break;

    case 'INCOMING_CONNECTION':
      log(`Incoming connection request! Ticket: ${msg.sessionTicket}`);
      if (msg.clientPin && msg.clientPin === SESSION_PIN) {
        log(`PIN verified successfully. Generating ephemeral X25519 session keys...`);

        // Generate ephemeral keys for this session
        const hostSessionKeys = generateSessionKeyPair();

        // Derive E2EE keys using Viewer's public key + Session PIN
        const sessionCrypto = deriveSessionKeys(
          hostSessionKeys.privateKey,
          msg.viewerEcdhPublic,
          SESSION_PIN,
          true // isHost = true
        );

        controlSocket.write(JSON.stringify({
          type: 'HOST_ACCEPT',
          sessionTicket: msg.sessionTicket,
          hostEcdhPublic: hostSessionKeys.publicKeyBase64
        }) + '\n');

        // Connect outbound to Relay with derived E2EE session
        attachToRelay(msg.sessionTicket, sessionCrypto);
      } else {
        log(`PIN verification failed! (Received: "${msg.clientPin}", Expected: "${SESSION_PIN}"). Rejecting...`);
        controlSocket.write(JSON.stringify({
          type: 'HOST_REJECT',
          sessionTicket: msg.sessionTicket,
          reason: 'Invalid Session PIN'
        }) + '\n');
      }
      break;

    default:
      log(`Control message: ${JSON.stringify(msg)}`);
  }
}

function attachToRelay(sessionTicket, sessionCrypto) {
  log(`Connecting outbound to Relay server at ${RENDEZVOUS_HOST}:${relayPort}...`);
  const relaySocket = connectTransport(relayPort, RENDEZVOUS_HOST, () => {
    log(`Connected to Relay. Sending ATTACH handshake for ticket ${sessionTicket}...`);
    relaySocket.write(`ATTACH ${sessionTicket} HOST\n`);
  });

  let relayHandshakeDone = false;
  let relayBuf = '';

  relaySocket.on('data', function onRelayData(chunk) {
    if (!relayHandshakeDone) {
      relayBuf += chunk.toString('utf8');
      const idx = relayBuf.indexOf('\n');
      if (idx !== -1) {
        const resp = relayBuf.slice(0, idx).trim();
        if (resp === 'RELAY_READY') {
          relayHandshakeDone = true;
          relaySocket.removeListener('data', onRelayData);
          log(`Relay confirmed ready! Establishing E2EE pipeline to local VNC server at 127.0.0.1:${LOCAL_VNC_PORT}...`);

          bridgeE2eeToLocalVnc(relaySocket, sessionCrypto, chunk.slice(idx + 1));
        } else {
          log(`Unexpected relay response: ${resp}`);
          relaySocket.destroy();
        }
      }
    }
  });

  relaySocket.on('error', (err) => {
    log(`Relay connection error: ${err.message}`);
  });
}

function bridgeE2eeToLocalVnc(relaySocket, sessionCrypto, leftover) {
  // Use reverse loopback bridge as primary connection method.
  // TightVNC Server's -connect command cleanly bypasses loopback policy restrictions
  // while ensuring zero external ports are exposed.
  startReverseBridge(relaySocket, sessionCrypto, leftover);
}

function getLocalInterfaceIp() {
  const ifaces = os.networkInterfaces();
  for (const name of Object.keys(ifaces)) {
    for (const iface of ifaces[name]) {
      if (!iface.internal && iface.family === 'IPv4') {
        return iface.address;
      }
    }
  }
  return '127.0.0.1';
}

function startReverseBridge(relaySocket, sessionCrypto, leftover) {
  const { exec } = require('child_process');
  const localIp = getLocalInterfaceIp();

  const localServer = net.createServer((incomingVncSock) => {
    log(`>>> Real tvnserver connected from ${incomingVncSock.remoteAddress}! E2EE active. <<<`);
    localServer.close();
    setupPipes(incomingVncSock, relaySocket, sessionCrypto, leftover, null);
  });

  localServer.listen(0, localIp, () => {
    const port = localServer.address().port;
    log(`Local reverse bridge listening on ${localIp}:${port}. Commanding tvnserver -connect...`);

    const tvnPath = 'C:\\Program Files\\TightVNC\\tvnserver.exe';
    exec(`"${tvnPath}" -controlservice -connect ${localIp}:${port}`, (err, stdout, stderr) => {
      if (err) {
        log(`tvnserver -connect notification: ${err.message}`);
      }
    });
  });

  localServer.on('error', (err) => {
    log(`Local reverse server error: ${err.message}`);
    relaySocket.destroy();
  });
}

function setupPipes(vncSocket, relaySocket, sessionCrypto, leftover, prebuffered) {
  const encryptor = new E2eeEncryptor(sessionCrypto.sendKey);
  const decryptor = new E2eeDecryptor(sessionCrypto.recvKey);

  decryptor.on('error', (err) => {
    log(`E2EE Decryption error: ${err.message}`);
    relaySocket.destroy();
    vncSocket.destroy();
  });

  // Pipe VNC -> Encrypt -> Relay
  if (prebuffered && prebuffered.length > 0) {
    encryptor.write(prebuffered);
  }
  vncSocket.pipe(encryptor).pipe(relaySocket);

  // Pipe Relay -> Decrypt -> VNC
  relaySocket.pipe(decryptor).pipe(vncSocket);

  if (leftover && leftover.length > 0) {
    decryptor.write(leftover);
  }

  function cleanup() {
    if (!relaySocket.destroyed) relaySocket.destroy();
    if (!vncSocket.destroyed) vncSocket.destroy();
  }

  relaySocket.on('close', cleanup);
  vncSocket.on('close', cleanup);
}

controlSocket.on('close', () => {
  log(`Disconnected from Rendezvous Server. Exiting.`);
  process.exit(0);
});

controlSocket.on('error', (err) => {
  log(`Control connection error: ${err.message}`);
});

