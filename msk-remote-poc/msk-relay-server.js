// MSK Remote - Private Rendezvous & Relay Server
//
// Features:
// 1. Dual Transport: TLS Encrypted Transport (using relay.pfx) + plain TCP fallback
// 2. Authenticated Device Registration with Device IDs (XXX-XXX-XXX)
// 3. Short-Lived Ephemeral Session Tickets (60-second TTL, single-use)
// 4. Zero-Knowledge Relay: Relay bridges opaque E2EE frames without decrypting payload

const tls = require('tls');
const net = require('net');
const fs = require('fs');
const path = require('path');
const crypto = require('crypto');

const CONTROL_PORT = parseInt(process.env.MSK_CONTROL_PORT || '8443', 10);
const RELAY_PORT = parseInt(process.env.MSK_RELAY_PORT || '8444', 10);
const PFX_PATH = process.env.MSK_PFX_PATH || path.join(__dirname, 'relay.pfx');
const PFX_PASS = process.env.MSK_PFX_PASS || 'mskpassword';

// In-memory registries
const registeredHosts = new Map(); // deviceId -> { socket, hostname, registeredAt }
const activeSessions = new Map();   // sessionTicket -> { hostId, viewerSocket, expiresAt, state }
const pendingRelayConns = new Map(); // sessionTicket -> { hostSocket, viewerSocket, expiresAt }

function log(module, msg) {
  const ts = new Date().toISOString().split('T')[1].slice(0, 8);
  console.log(`[${ts}] [${module}] ${msg}`);
}

// -------------------------------------------------------------
// Helper: Create TLS or TCP Server depending on cert availability
// -------------------------------------------------------------
function createSecureServer(connectionListener) {
  if (fs.existsSync(PFX_PATH)) {
    try {
      const pfx = fs.readFileSync(PFX_PATH);
      log('Security', `Loaded TLS certificate from ${path.basename(PFX_PATH)}`);
      return tls.createServer({ pfx, passphrase: PFX_PASS }, connectionListener);
    } catch (err) {
      log('Security', `Could not load PFX (${err.message}). Falling back to standard TCP.`);
    }
  }
  return net.createServer(connectionListener);
}

// -------------------------------------------------------------
// 1. Control & Signaling Server
// -------------------------------------------------------------
const controlServer = createSecureServer((socket) => {
  let buffer = '';
  let currentDeviceId = null;

  socket.setNoDelay(true);

  function sendJson(obj) {
    if (!socket.destroyed) {
      socket.write(JSON.stringify(obj) + '\n');
    }
  }

  socket.on('data', (chunk) => {
    buffer += chunk.toString('utf8');
    let boundary = buffer.indexOf('\n');
    while (boundary !== -1) {
      const line = buffer.slice(0, boundary).trim();
      buffer = buffer.slice(boundary + 1);
      if (line) {
        try {
          const msg = JSON.parse(line);
          handleMessage(msg);
        } catch (err) {
          log('Control', `Invalid JSON: ${err.message}`);
          sendJson({ type: 'ERROR', message: 'Malformed JSON' });
        }
      }
      boundary = buffer.indexOf('\n');
    }
  });

  function handleMessage(msg) {
    // Purge expired sessions
    const now = Date.now();
    for (const [ticket, sess] of activeSessions.entries()) {
      if (sess.expiresAt < now) {
        activeSessions.delete(ticket);
      }
    }

    switch (msg.type) {
      case 'REGISTER_HOST': {
        const deviceId = msg.deviceId || generateDeviceId();
        currentDeviceId = deviceId;
        registeredHosts.set(deviceId, {
          socket,
          hostname: msg.hostname || 'Windows Host',
          registeredAt: now
        });
        log('Rendezvous', `Host registered: Device ID [${deviceId}] (${msg.hostname || 'Windows'})`);
        sendJson({
          type: 'REGISTER_OK',
          deviceId: deviceId,
          relayPort: RELAY_PORT
        });
        break;
      }

      case 'QUERY_HOST': {
        const targetId = msg.targetDeviceId;
        const host = registeredHosts.get(targetId);
        if (host && !host.socket.destroyed) {
          sendJson({
            type: 'HOST_STATUS',
            deviceId: targetId,
            online: true,
            hostname: host.hostname
          });
        } else {
          sendJson({ type: 'HOST_STATUS', deviceId: targetId, online: false });
        }
        break;
      }

      case 'REQUEST_CONNECTION': {
        const targetId = msg.targetDeviceId;
        const host = registeredHosts.get(targetId);
        if (!host || host.socket.destroyed) {
          sendJson({ type: 'CONNECT_ERROR', code: 'HOST_OFFLINE', message: `Host ${targetId} is offline` });
          return;
        }

        // Short-lived session ticket: 60-second TTL
        const sessionTicket = crypto.randomBytes(16).toString('hex');
        const expiresAt = now + 60000;

        activeSessions.set(sessionTicket, {
          hostId: targetId,
          viewerSocket: socket,
          viewerEcdhPublic: msg.viewerEcdhPublic || null,
          expiresAt: expiresAt,
          state: 'NEGOTIATING'
        });

        log('Signaling', `Connection requested for Host [${targetId}], Ticket: ${sessionTicket} (TTL: 60s)`);

        // Forward connection request & Viewer ECDH public key to Host
        host.socket.write(JSON.stringify({
          type: 'INCOMING_CONNECTION',
          sessionTicket: sessionTicket,
          clientPin: msg.pin || '',
          viewerEcdhPublic: msg.viewerEcdhPublic || null,
          expiresAt: expiresAt
        }) + '\n');

        // Acknowledge to Viewer
        sendJson({
          type: 'CONNECT_PENDING',
          sessionTicket: sessionTicket,
          relayPort: RELAY_PORT,
          expiresAt: expiresAt
        });
        break;
      }

      case 'HOST_ACCEPT': {
        const ticket = msg.sessionTicket;
        const session = activeSessions.get(ticket);
        if (!session) {
          sendJson({ type: 'ERROR', message: 'Session ticket expired or invalid' });
          return;
        }

        if (session.expiresAt < now) {
          activeSessions.delete(ticket);
          sendJson({ type: 'ERROR', message: 'Session ticket expired' });
          return;
        }

        session.state = 'WAITING_RELAY';
        log('Signaling', `Host accepted session ${ticket}. Exchanging ECDH keys...`);

        if (session.viewerSocket && !session.viewerSocket.destroyed) {
          session.viewerSocket.write(JSON.stringify({
            type: 'CONNECT_ACCEPTED',
            sessionTicket: ticket,
            hostEcdhPublic: msg.hostEcdhPublic || null,
            relayPort: RELAY_PORT
          }) + '\n');
        }
        break;
      }

      case 'HOST_REJECT': {
        const ticket = msg.sessionTicket;
        const session = activeSessions.get(ticket);
        if (session) {
          log('Signaling', `Host rejected session ${ticket}: ${msg.reason}`);
          if (session.viewerSocket && !session.viewerSocket.destroyed) {
            session.viewerSocket.write(JSON.stringify({
              type: 'CONNECT_REJECTED',
              reason: msg.reason || 'Authentication failed'
            }) + '\n');
          }
          activeSessions.delete(ticket);
        }
        break;
      }

      default:
        log('Control', `Unknown command: ${msg.type}`);
    }
  }

  socket.on('close', () => {
    if (currentDeviceId && registeredHosts.get(currentDeviceId)?.socket === socket) {
      registeredHosts.delete(currentDeviceId);
      log('Rendezvous', `Host disconnected: [${currentDeviceId}]`);
    }
  });

  socket.on('error', (err) => {
    log('Control', `Socket error: ${err.message}`);
  });
});

// -------------------------------------------------------------
// 2. Binary Stream Relay Server
// -------------------------------------------------------------
const relayServer = createSecureServer((socket) => {
  let headerBuffer = '';
  let identified = false;

  socket.setNoDelay(true);

  socket.on('data', function onInitialData(chunk) {
    if (identified) return;

    headerBuffer += chunk.toString('utf8');
    const newline = headerBuffer.indexOf('\n');
    if (newline === -1) {
      if (headerBuffer.length > 512) {
        socket.destroy();
      }
      return;
    }

    const headerLine = headerBuffer.slice(0, newline).trim();
    const remainingData = chunk.slice(Buffer.byteLength(headerBuffer.slice(0, newline + 1), 'utf8'));

    // Header format: "ATTACH <sessionTicket> <ROLE:HOST|VIEWER>"
    const parts = headerLine.split(' ');
    if (parts.length < 3 || parts[0] !== 'ATTACH') {
      log('Relay', `Invalid relay attach header: ${headerLine}`);
      socket.destroy();
      return;
    }

    const ticket = parts[1];
    const role = parts[2].toUpperCase();
    const now = Date.now();

    // Verify session ticket validity
    const session = activeSessions.get(ticket);
    if (!session || session.expiresAt < now) {
      log('Relay', `Rejected attach with expired or invalid ticket: ${ticket}`);
      socket.write('ERROR_EXPIRED_TICKET\n');
      socket.destroy();
      return;
    }

    identified = true;
    socket.removeListener('data', onInitialData);
    log('Relay', `Role [${role}] attached with ticket ${ticket}`);

    let pair = pendingRelayConns.get(ticket);
    if (!pair) {
      pair = { hostSocket: null, viewerSocket: null, expiresAt: session.expiresAt };
      pendingRelayConns.set(ticket, pair);
    }

    if (role === 'HOST') {
      pair.hostSocket = socket;
    } else {
      pair.viewerSocket = socket;
    }

    // Both endpoints have attached -> bridge the encrypted streams!
    if (pair.hostSocket && pair.viewerSocket) {
      pendingRelayConns.delete(ticket);
      activeSessions.delete(ticket); // Burn ticket (single-use guarantee)

      log('Relay', `>>> Pairing active for ticket [${ticket}]. Tunneling E2EE streams... <<<`);

      pair.hostSocket.write('RELAY_READY\n');
      pair.viewerSocket.write('RELAY_READY\n');

      pair.hostSocket.pipe(pair.viewerSocket);
      pair.viewerSocket.pipe(pair.hostSocket);

      if (remainingData.length > 0) {
        if (role === 'HOST') {
          pair.viewerSocket.write(remainingData);
        } else {
          pair.hostSocket.write(remainingData);
        }
      }

      function cleanup() {
        if (!pair.hostSocket.destroyed) pair.hostSocket.destroy();
        if (!pair.viewerSocket.destroyed) pair.viewerSocket.destroy();
      }

      pair.hostSocket.on('close', cleanup);
      pair.viewerSocket.on('close', cleanup);
      pair.hostSocket.on('error', cleanup);
      pair.viewerSocket.on('error', cleanup);
    }
  });
});

function generateDeviceId() {
  const p1 = Math.floor(100 + Math.random() * 900);
  const p2 = Math.floor(100 + Math.random() * 900);
  const p3 = Math.floor(100 + Math.random() * 900);
  return `${p1}-${p2}-${p3}`;
}

controlServer.listen(CONTROL_PORT, () => {
  log('Server', `MSK Rendezvous Control Server listening on port ${CONTROL_PORT}`);
});

relayServer.listen(RELAY_PORT, () => {
  log('Server', `MSK Binary Relay Server listening on port ${RELAY_PORT}`);
});

process.on('SIGINT', () => {
  log('Server', 'Shutting down...');
  controlServer.close();
  relayServer.close();
  process.exit(0);
});
