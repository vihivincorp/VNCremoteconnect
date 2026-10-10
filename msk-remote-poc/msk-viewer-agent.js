// MSK Remote - Viewer Bridge Agent
//
// Features:
// 1. Queries Host status by 9-digit Device ID
// 2. Performs Ephemeral X25519 ECDH Key Exchange
// 3. Authenticates with Session PIN & Derives Directional AEAD Keys
// 4. Outbound TLS Connection to Relay & End-to-End Encryption
// 5. Exposes Local Loopback Endpoint (127.0.0.1:port) for TightVNC Viewer

const tls = require('tls');
const net = require('net');
const { spawn } = require('child_process');
const path = require('path');
const fs = require('fs');
const { generateSessionKeyPair, deriveSessionKeys, E2eeEncryptor, E2eeDecryptor } = require('./msk-e2ee.js');

const RENDEZVOUS_HOST = process.env.MSK_SERVER_HOST || '127.0.0.1';
const RENDEZVOUS_PORT = parseInt(process.env.MSK_CONTROL_PORT || '8443', 10);
const TARGET_DEVICE_ID = process.argv[2] || process.env.MSK_TARGET_DEVICE_ID || '888-123-456';
const SESSION_PIN = process.argv[3] || process.env.MSK_SESSION_PIN || '123456';
const AUTO_LAUNCH_VIEWER = process.env.MSK_AUTO_LAUNCH !== '0';
const USE_TLS = process.env.MSK_USE_TLS !== '0';

function log(msg) {
  const ts = new Date().toISOString().split('T')[1].slice(0, 8);
  console.log(`[${ts}] [MSK Viewer] ${msg}`);
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

log(`Starting MSK Remote Viewer Agent...`);
log(`Target Device ID: ${TARGET_DEVICE_ID}`);
log(`Using Session PIN: ${SESSION_PIN}`);

// Ephemeral keys for this connection attempt
const viewerSessionKeys = generateSessionKeyPair();

const controlSocket = connectTransport(RENDEZVOUS_PORT, RENDEZVOUS_HOST, () => {
  log(`Connected to Rendezvous Server at ${RENDEZVOUS_HOST}:${RENDEZVOUS_PORT} (TLS: ${USE_TLS})`);
  // Step 1: Query host status
  controlSocket.write(JSON.stringify({
    type: 'QUERY_HOST',
    targetDeviceId: TARGET_DEVICE_ID
  }) + '\n');
});

let buffer = '';
let relayPort = 8444;

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
    case 'HOST_STATUS':
      if (msg.online) {
        log(`Host [${TARGET_DEVICE_ID}] is ONLINE (${msg.hostname || 'Windows'}). Sending connection request with ECDH public key...`);
        controlSocket.write(JSON.stringify({
          type: 'REQUEST_CONNECTION',
          targetDeviceId: TARGET_DEVICE_ID,
          pin: SESSION_PIN,
          viewerEcdhPublic: viewerSessionKeys.publicKeyBase64
        }) + '\n');
      } else {
        log(`Host [${TARGET_DEVICE_ID}] is OFFLINE. Cannot connect.`);
        process.exit(1);
      }
      break;

    case 'CONNECT_PENDING':
      relayPort = msg.relayPort || 8444;
      log(`Connection request pending host approval... (Session ticket: ${msg.sessionTicket})`);
      break;

    case 'CONNECT_ACCEPTED':
      relayPort = msg.relayPort || 8444;
      log(`>>> Connection ACCEPTED by host! Deriving E2EE session keys... <<<`);

      if (!msg.hostEcdhPublic) {
        log(`Error: Host did not provide ECDH public key! Aborting.`);
        process.exit(1);
      }

      // Derive E2EE keys using Host's public key + Session PIN
      const sessionCrypto = deriveSessionKeys(
        viewerSessionKeys.privateKey,
        msg.hostEcdhPublic,
        SESSION_PIN,
        false // isHost = false
      );

      startLocalProxyAndRelay(msg.sessionTicket, sessionCrypto);
      break;

    case 'CONNECT_REJECTED':
      log(`>>> Connection REJECTED by host! Reason: ${msg.reason} <<<`);
      process.exit(1);
      break;

    case 'CONNECT_ERROR':
      log(`Connection error: ${msg.message}`);
      process.exit(1);
      break;

    default:
      log(`Control message: ${JSON.stringify(msg)}`);
  }
}

function startLocalProxyAndRelay(sessionTicket, sessionCrypto) {
  log(`Connecting outbound to Relay server at ${RENDEZVOUS_HOST}:${relayPort}...`);
  const relaySocket = connectTransport(relayPort, RENDEZVOUS_HOST, () => {
    log(`Connected to Relay server. Sending ATTACH handshake as VIEWER...`);
    relaySocket.write(`ATTACH ${sessionTicket} VIEWER\n`);
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
          log(`Relay link active! Creating local loopback port for TightVNC Viewer...`);

          setupLocalListener(relaySocket, sessionCrypto, chunk.slice(idx + 1));
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

function setupLocalListener(relaySocket, sessionCrypto, leftover) {
  let earlyBuffers = [];
  if (leftover && leftover.length > 0) {
    earlyBuffers.push(leftover);
  }

  function onEarlyData(chunk) {
    earlyBuffers.push(chunk);
  }
  relaySocket.on('data', onEarlyData);

  // Bind on ephemeral local port
  const localServer = net.createServer((vncViewerSocket) => {
    log(`TightVNC Viewer connected locally! Establishing E2EE pipeline...`);
    localServer.close();

    relaySocket.removeListener('data', onEarlyData);

    const encryptor = new E2eeEncryptor(sessionCrypto.sendKey);
    const decryptor = new E2eeDecryptor(sessionCrypto.recvKey);

    decryptor.on('error', (err) => {
      log(`E2EE Decryption error: ${err.message}`);
      relaySocket.destroy();
      vncViewerSocket.destroy();
    });

    // Pipeline:
    // Local Viewer -> Encryptor -> Relay Socket
    // Relay Socket -> Decryptor -> Local Viewer
    vncViewerSocket.pipe(encryptor).pipe(relaySocket);
    relaySocket.pipe(decryptor).pipe(vncViewerSocket);

    // Push any early frames
    for (const buf of earlyBuffers) {
      decryptor.write(buf);
    }
    earlyBuffers = [];

    vncViewerSocket.on('close', () => {
      log('TightVNC Viewer disconnected.');
      relaySocket.destroy();
      process.exit(0);
    });
  });

  localServer.listen(0, '127.0.0.1', () => {
    const localPort = localServer.address().port;
    console.log('\n======================================================');
    console.log(`  MSK REMOTE TUNNEL ESTABLISHED! (E2EE SECURE)`);
    console.log(`  Local Endpoint: 127.0.0.1:${localPort}`);
    console.log('======================================================\n');

    // Find TightVNC Viewer executable (installed or portable)
    let vncViewerPath = null;
    const candidates = [
      'C:\\Program Files\\TightVNC\\tvnviewer.exe',
      path.resolve(__dirname, '..', 'tightvnc-bin', 'vncviewer.exe'),
      path.resolve(__dirname, '..', 'tightvnc-bin', 'tvnviewer.exe')
    ];

    for (const c of candidates) {
      if (fs.existsSync(c)) {
        vncViewerPath = c;
        break;
      }
    }

    if (AUTO_LAUNCH_VIEWER && vncViewerPath) {
      log(`Launching TightVNC Viewer: "${vncViewerPath}" 127.0.0.1:${localPort}...`);
      try {
        const child = spawn(vncViewerPath, [`127.0.0.1:${localPort}`], {
          detached: true,
          stdio: 'ignore'
        });
        child.unref();
      } catch (err) {
        log(`Could not launch viewer automatically: ${err.message}`);
        console.log(`Please launch manually: "${vncViewerPath}" 127.0.0.1:${localPort}`);
      }
    } else {
      console.log(`Connect with: vncviewer.exe 127.0.0.1:${localPort}`);
    }
  });
}

controlSocket.on('error', (err) => {
  log(`Control connection error: ${err.message}`);
});
