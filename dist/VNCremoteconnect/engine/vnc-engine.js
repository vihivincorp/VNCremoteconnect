// VNC Remote Connect - Core Unified Engine
//
// Supports Host Mode, Viewer Mode, Relay Mode, and Relay Ping.
// Offers both human-readable console output and structured --ipc mode.
// Compliant with upstream TightVNC 2.8.90 GPL v2 specifications.

const net = require('net');
const tls = require('tls');
const os = require('os');
const fs = require('fs');
const path = require('path');
const { spawn, exec } = require('child_process');
const crypto = require('crypto');
const { generateSessionKeyPair, deriveSessionKeys, E2eeEncryptor, E2eeDecryptor } = require('./vnc-e2ee.js');

// ---------------------------------------------------------------------------
// Command-line Argument Parsing
// ---------------------------------------------------------------------------
const args = process.argv.slice(2);
const command = args[0] ? args[0].toLowerCase() : 'help';

function getArg(flag, defaultVal = null) {
  const idx = args.indexOf(flag);
  if (idx !== -1 && idx + 1 < args.length) {
    return args[idx + 1];
  }
  return defaultVal;
}

const isIpc = args.includes('--ipc');
const noTls = args.includes('--no-tls');

function emitEvent(event, data = {}) {
  if (isIpc) {
    const payload = JSON.stringify({ event, ...data });
    process.stdout.write(payload + '\n');
  } else {
    const ts = new Date().toISOString().split('T')[1].slice(0, 8);
    const text = data.text || data.message || JSON.stringify(data);
    console.log(`[${ts}] [${event}] ${text}`);
  }
}

function log(text) {
  emitEvent('LOG', { text });
}

function connectTransport(port, host, useTls, onConnect) {
  if (useTls) {
    const s = tls.connect({ host, port, rejectUnauthorized: false }, onConnect);
    s.setNoDelay(true);
    return s;
  } else {
    const s = net.connect({ host, port }, onConnect);
    s.setNoDelay(true);
    return s;
  }
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

function findTightVncViewer() {
  const candidates = [
    'C:\\Program Files\\TightVNC\\tvnviewer.exe',
    'C:\\Program Files (x86)\\TightVNC\\tvnviewer.exe',
    path.resolve(__dirname, '..', 'tightvnc-bin', 'vncviewer.exe'),
    path.resolve(__dirname, '..', 'tightvnc-bin', 'tvnviewer.exe'),
    path.resolve(__dirname, 'vnc', 'tvnviewer.exe'),
    path.resolve(__dirname, 'vnc', 'vncviewer.exe')
  ];
  for (const c of candidates) {
    if (fs.existsSync(c)) return c;
  }
  return null;
}

function findTightVncServer() {
  const candidates = [
    'C:\\Program Files\\TightVNC\\tvnserver.exe',
    'C:\\Program Files (x86)\\TightVNC\\tvnserver.exe',
    path.resolve(__dirname, '..', 'tightvnc-bin', 'WinVNC.exe'),
    path.resolve(__dirname, 'vnc', 'tvnserver.exe'),
    path.resolve(__dirname, 'vnc', 'WinVNC.exe')
  ];
  for (const c of candidates) {
    if (fs.existsSync(c)) return c;
  }
  return null;
}

// ---------------------------------------------------------------------------
// 1. RELAY SERVER MODE
// ---------------------------------------------------------------------------
function runRelay() {
  const controlPort = parseInt(getArg('--control-port', process.env.VNC_CONTROL_PORT || '8443'), 10);
  const relayPort = parseInt(getArg('--relay-port', process.env.VNC_RELAY_PORT || '8444'), 10);
  const pfxPath = getArg('--pfx', process.env.VNC_PFX_PATH || path.join(__dirname, 'relay.pfx'));
  const pfxPass = getArg('--pfx-pass', process.env.VNC_PFX_PASS || 'vncpassword');

  const registeredHosts = new Map();
  const activeSessions = new Map();
  const pendingRelayConns = new Map();

  function createSecureServer(listener) {
    if (!noTls && fs.existsSync(pfxPath)) {
      try {
        const pfx = fs.readFileSync(pfxPath);
        return { server: tls.createServer({ pfx, passphrase: pfxPass }, listener), tls: true };
      } catch (err) {
        log(`Could not load PFX (${err.message}). Falling back to plain TCP.`);
      }
    }
    return { server: net.createServer(listener), tls: false };
  }

  const { server: controlServer, tls: controlTls } = createSecureServer((socket) => {
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
            sendJson({ type: 'ERROR', message: 'Malformed JSON' });
          }
        }
        boundary = buffer.indexOf('\n');
      }
    });

    function handleMessage(msg) {
      const now = Date.now();
      for (const [ticket, sess] of activeSessions.entries()) {
        if (sess.expiresAt < now) activeSessions.delete(ticket);
      }

      switch (msg.type) {
        case 'REGISTER_HOST': {
          let devId = msg.deviceId;
          if (!devId) {
            const num = Math.floor(100000000 + Math.random() * 900000000);
            devId = `${String(num).slice(0, 3)}-${String(num).slice(3, 6)}-${String(num).slice(6, 9)}`;
          }
          currentDeviceId = devId;
          registeredHosts.set(devId, {
            socket,
            hostname: msg.hostname || 'Windows Host',
            registeredAt: now
          });
          emitEvent('RELAY_HOST_REGISTERED', { deviceId: devId, hostname: msg.hostname });
          sendJson({ type: 'REGISTER_OK', deviceId: devId, relayPort });
          break;
        }

        case 'QUERY_HOST': {
          const host = registeredHosts.get(msg.targetDeviceId);
          if (host && !host.socket.destroyed) {
            sendJson({ type: 'HOST_STATUS', deviceId: msg.targetDeviceId, online: true, hostname: host.hostname });
          } else {
            sendJson({ type: 'HOST_STATUS', deviceId: msg.targetDeviceId, online: false });
          }
          break;
        }

        case 'REQUEST_CONNECTION': {
          const host = registeredHosts.get(msg.targetDeviceId);
          if (!host || host.socket.destroyed) {
            sendJson({ type: 'CONNECT_ERROR', code: 'HOST_OFFLINE', message: `Host ${msg.targetDeviceId} is offline` });
            return;
          }

          const sessionTicket = crypto.randomBytes(16).toString('hex');
          activeSessions.set(sessionTicket, {
            hostId: msg.targetDeviceId,
            viewerSocket: socket,
            viewerEcdhPublic: msg.viewerEcdhPublic || null,
            expiresAt: now + 60000
          });

          emitEvent('RELAY_SESSION_REQUESTED', { target: msg.targetDeviceId, ticket: sessionTicket });

          host.socket.write(JSON.stringify({
            type: 'INCOMING_CONNECTION',
            sessionTicket,
            clientPin: msg.pin || '',
            viewerEcdhPublic: msg.viewerEcdhPublic || null
          }) + '\n');

          sendJson({ type: 'CONNECT_PENDING', sessionTicket, relayPort });
          break;
        }

        case 'HOST_ACCEPT': {
          const sess = activeSessions.get(msg.sessionTicket);
          if (sess && !sess.viewerSocket.destroyed) {
            sess.viewerSocket.write(JSON.stringify({
              type: 'CONNECT_ACCEPTED',
              sessionTicket: msg.sessionTicket,
              hostEcdhPublic: msg.hostEcdhPublic,
              relayPort
            }) + '\n');
            emitEvent('RELAY_SESSION_ACCEPTED', { ticket: msg.sessionTicket });
          }
          break;
        }

        case 'HOST_REJECT': {
          const sess = activeSessions.get(msg.sessionTicket);
          if (sess && !sess.viewerSocket.destroyed) {
            sess.viewerSocket.write(JSON.stringify({
              type: 'CONNECT_REJECTED',
              sessionTicket: msg.sessionTicket,
              reason: msg.reason || 'Host rejected connection'
            }) + '\n');
            activeSessions.delete(msg.sessionTicket);
            emitEvent('RELAY_SESSION_REJECTED', { ticket: msg.sessionTicket, reason: msg.reason });
          }
          break;
        }
      }
    }

    socket.on('close', () => {
      if (currentDeviceId && registeredHosts.get(currentDeviceId)?.socket === socket) {
        registeredHosts.delete(currentDeviceId);
        emitEvent('RELAY_HOST_UNREGISTERED', { deviceId: currentDeviceId });
      }
    });

    socket.on('error', () => {});
  });

  const { server: dataRelayServer, tls: relayTls } = createSecureServer((socket) => {
    socket.setNoDelay(true);
    let attached = false;
    let handshakeBuf = '';

    socket.on('data', function onHandshake(chunk) {
      if (attached) return;
      handshakeBuf += chunk.toString('utf8');
      const idx = handshakeBuf.indexOf('\n');
      if (idx !== -1) {
        const line = handshakeBuf.slice(0, idx).trim();
        const leftover = chunk.slice(Buffer.byteLength(handshakeBuf.slice(0, idx + 1)));
        const parts = line.split(' ');
        if (parts[0] === 'ATTACH' && parts.length === 3) {
          const ticket = parts[1];
          const role = parts[2].toUpperCase();
          attached = true;
          socket.removeListener('data', onHandshake);
          handleRelayAttach(socket, ticket, role, leftover);
        } else {
          socket.destroy();
        }
      }
    });
  });

  function handleRelayAttach(socket, ticket, role, leftover) {
    let entry = pendingRelayConns.get(ticket);
    if (!entry) {
      entry = { hostSocket: null, viewerSocket: null, hostLeftover: null, viewerLeftover: null, expiresAt: Date.now() + 60000 };
      pendingRelayConns.set(ticket, entry);
    }

    if (role === 'HOST') {
      entry.hostSocket = socket;
      entry.hostLeftover = leftover;
    } else if (role === 'VIEWER') {
      entry.viewerSocket = socket;
      entry.viewerLeftover = leftover;
    }

    if (entry.hostSocket && entry.viewerSocket) {
      pendingRelayConns.delete(ticket);
      const hSock = entry.hostSocket;
      const vSock = entry.viewerSocket;

      hSock.write('RELAY_READY\n');
      vSock.write('RELAY_READY\n');

      hSock.pipe(vSock);
      vSock.pipe(hSock);

      if (entry.hostLeftover && entry.hostLeftover.length > 0) vSock.write(entry.hostLeftover);
      if (entry.viewerLeftover && entry.viewerLeftover.length > 0) hSock.write(entry.viewerLeftover);

      emitEvent('RELAY_PIPELINE_ACTIVE', { ticket });

      function cleanup() {
        if (!hSock.destroyed) hSock.destroy();
        if (!vSock.destroyed) vSock.destroy();
      }
      hSock.on('close', cleanup);
      vSock.on('close', cleanup);
      hSock.on('error', cleanup);
      vSock.on('error', cleanup);
    }
  }

  controlServer.listen(controlPort, '0.0.0.0', () => {
    dataRelayServer.listen(relayPort, '0.0.0.0', () => {
      emitEvent('RELAY_STARTED', {
        controlPort,
        relayPort,
        tls: controlTls
      });
    });
  });
}

// ---------------------------------------------------------------------------
// 2. HOST MODE
// ---------------------------------------------------------------------------
function runHost() {
  const relayHost = getArg('--relay', process.env.VNC_SERVER_HOST || '127.0.0.1');
  const controlPort = parseInt(getArg('--control-port', process.env.VNC_CONTROL_PORT || '8443'), 10);
  const desiredId = getArg('--device-id', process.env.VNC_DEVICE_ID || null);
  const sessionPin = getArg('--pin', process.env.VNC_SESSION_PIN || '123456');
  const useTls = !noTls && (process.env.VNC_USE_TLS !== '0');

  let assignedDeviceId = desiredId;
  let relayPort = 8444;
  let controlSocket = null;
  let isReconnecting = false;
  let reconnectTimer = null;

  function connectToRelay() {
    if (reconnectTimer) {
      clearTimeout(reconnectTimer);
      reconnectTimer = null;
    }
    log(`Connecting to Relay Server at ${relayHost}:${controlPort} (TLS: ${useTls})...`);

    controlSocket = connectTransport(controlPort, relayHost, useTls, () => {
      isReconnecting = false;
      log(`Connected to Relay. Registering permanent Device ID [${desiredId || 'auto'}]...`);
      controlSocket.write(JSON.stringify({
        type: 'REGISTER_HOST',
        deviceId: desiredId,
        hostname: os.hostname()
      }) + '\n');
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
            handleControlMessage(JSON.parse(line));
          } catch (e) {}
        }
        boundary = buffer.indexOf('\n');
      }
    });

    controlSocket.on('close', () => {
      emitEvent('HOST_OFFLINE', { message: 'Relay connection lost. Auto-reconnecting in 3 seconds...' });
      scheduleReconnect();
    });

    controlSocket.on('error', (err) => {
      emitEvent('ERROR', { message: `Relay connection error: ${err.message}` });
      scheduleReconnect();
    });
  }

  function scheduleReconnect() {
    if (isReconnecting || reconnectTimer) return;
    isReconnecting = true;
    reconnectTimer = setTimeout(() => {
      reconnectTimer = null;
      connectToRelay();
    }, 3000);
  }

  function handleControlMessage(msg) {
    switch (msg.type) {
      case 'REGISTER_OK':
        assignedDeviceId = msg.deviceId;
        relayPort = msg.relayPort || 8444;
        emitEvent('HOST_READY', {
          deviceId: assignedDeviceId,
          pin: sessionPin,
          relayHost,
          controlPort,
          relayPort
        });
        break;

      case 'INCOMING_CONNECTION':
        emitEvent('HOST_INCOMING', { ticket: msg.sessionTicket });
        if (msg.clientPin && msg.clientPin === sessionPin) {
          log(`Password match! Establishing encrypted session...`);
          const hostSessionKeys = generateSessionKeyPair();
          const sessionCrypto = deriveSessionKeys(
            hostSessionKeys.privateKey,
            msg.viewerEcdhPublic,
            sessionPin,
            true
          );

          controlSocket.write(JSON.stringify({
            type: 'HOST_ACCEPT',
            sessionTicket: msg.sessionTicket,
            hostEcdhPublic: hostSessionKeys.publicKeyBase64
          }) + '\n');

          attachHostToRelay(msg.sessionTicket, sessionCrypto);
        } else {
          log(`Password mismatch! Rejecting connection attempt.`);
          controlSocket.write(JSON.stringify({
            type: 'HOST_REJECT',
            sessionTicket: msg.sessionTicket,
            reason: 'Invalid Session Password'
          }) + '\n');
        }
        break;
    }
  }

  function attachHostToRelay(sessionTicket, sessionCrypto) {
    const relaySocket = connectTransport(relayPort, relayHost, useTls, () => {
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
            emitEvent('HOST_TUNNEL_ACTIVE', { ticket: sessionTicket });
            bridgeHostVnc(relaySocket, sessionCrypto, chunk.slice(idx + 1));
          } else {
            relaySocket.destroy();
          }
        }
      }
    });

    relaySocket.on('error', (err) => {
      emitEvent('ERROR', { message: `Relay socket error: ${err.message}` });
    });
  }

  function bridgeHostVnc(relaySocket, sessionCrypto, leftover) {
    const localIp = getLocalInterfaceIp();
    const localServer = net.createServer((incomingVncSock) => {
      localServer.close();
      setupPipes(incomingVncSock, relaySocket, sessionCrypto, leftover);
    });

    localServer.listen(0, localIp, () => {
      const port = localServer.address().port;
      log(`Reverse bridge listening on ${localIp}:${port}. Triggering TightVNC server...`);

      const tvnPath = findTightVncServer();
      if (tvnPath) {
        exec(`"${tvnPath}" -controlservice -connect ${localIp}:${port}`, (err) => {
          if (err) {
            exec(`"${tvnPath}" -controlapp -connect ${localIp}:${port}`, (err2) => {
              if (err2) {
                exec(`"${tvnPath}" -connect ${localIp}:${port}`, () => {});
              }
            });
          }
        });
      }
    });

    localServer.on('error', (err) => {
      relaySocket.destroy();
    });
  }

  function setupPipes(vncSocket, relaySocket, sessionCrypto, leftover) {
    const encryptor = new E2eeEncryptor(sessionCrypto.sendKey);
    const decryptor = new E2eeDecryptor(sessionCrypto.recvKey);

    decryptor.on('error', (err) => {
      emitEvent('ERROR', { message: `Decryption error: ${err.message}` });
      relaySocket.destroy();
      vncSocket.destroy();
    });

    vncSocket.pipe(encryptor).pipe(relaySocket);
    relaySocket.pipe(decryptor).pipe(vncSocket);

    if (leftover && leftover.length > 0) {
      decryptor.write(leftover);
    }

    emitEvent('HOST_SESSION_CONNECTED', { status: 'STREAMING' });

    function cleanup() {
      if (!relaySocket.destroyed) relaySocket.destroy();
      if (!vncSocket.destroyed) vncSocket.destroy();
      emitEvent('HOST_SESSION_CLOSED');
      log(`Remote partner disconnected. Host remains online & ready for reconnection.`);
    }

    relaySocket.on('close', cleanup);
    vncSocket.on('close', cleanup);
  }

  connectToRelay();
}

// ---------------------------------------------------------------------------
// 3. VIEWER MODE
// ---------------------------------------------------------------------------
function runViewer() {
  const relayHost = getArg('--relay', process.env.VNC_SERVER_HOST || '127.0.0.1');
  const controlPort = parseInt(getArg('--control-port', process.env.VNC_CONTROL_PORT || '8443'), 10);
  const targetId = getArg('--target', process.env.VNC_TARGET_DEVICE_ID || '');
  const sessionPin = getArg('--pin', process.env.VNC_SESSION_PIN || '');
  const autoLaunch = !args.includes('--no-launch');
  const quality = getArg('--quality', 'balanced'); // fast, balanced, best
  const useTls = !noTls && (process.env.VNC_USE_TLS !== '0');

  if (!targetId || !sessionPin) {
    emitEvent('ERROR', { message: 'Target Device ID and Session PIN are required' });
    process.exit(1);
  }

  const viewerSessionKeys = generateSessionKeyPair();
  let relayPort = 8444;

  emitEvent('VIEWER_STATUS', { step: 'CONNECTING_RELAY', text: `Connecting to Relay at ${relayHost}:${controlPort}...` });

  const controlSocket = connectTransport(controlPort, relayHost, useTls, () => {
    emitEvent('VIEWER_STATUS', { step: 'QUERYING_HOST', text: `Querying status for Host [${targetId}]...` });
    controlSocket.write(JSON.stringify({
      type: 'QUERY_HOST',
      targetDeviceId: targetId
    }) + '\n');
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
          handleControlMessage(JSON.parse(line));
        } catch (e) {}
      }
      boundary = buffer.indexOf('\n');
    }
  });

  function handleControlMessage(msg) {
    switch (msg.type) {
      case 'HOST_STATUS':
        if (msg.online) {
          emitEvent('VIEWER_STATUS', { step: 'AUTHENTICATING', text: `Host is ONLINE. Authenticating Session PIN and sending ECDH public key...` });
          controlSocket.write(JSON.stringify({
            type: 'REQUEST_CONNECTION',
            targetDeviceId: targetId,
            pin: sessionPin,
            viewerEcdhPublic: viewerSessionKeys.publicKeyBase64
          }) + '\n');
        } else {
          emitEvent('ERROR', { message: `Remote PC [${targetId}] is OFFLINE.` });
          process.exit(1);
        }
        break;

      case 'CONNECT_PENDING':
        relayPort = msg.relayPort || 8444;
        emitEvent('VIEWER_STATUS', { step: 'WAITING_HOST', text: `Awaiting Host response...` });
        break;

      case 'CONNECT_ACCEPTED':
        relayPort = msg.relayPort || 8444;
        emitEvent('VIEWER_STATUS', { step: 'DERIVING_KEYS', text: `Connection ACCEPTED! Deriving E2EE session keys...` });

        const sessionCrypto = deriveSessionKeys(
          viewerSessionKeys.privateKey,
          msg.hostEcdhPublic,
          sessionPin,
          false
        );

        attachViewerToRelay(msg.sessionTicket, sessionCrypto);
        break;

      case 'CONNECT_REJECTED':
        emitEvent('ERROR', { message: `Connection REJECTED: ${msg.reason || 'Invalid PIN'}` });
        process.exit(1);
        break;

      case 'CONNECT_ERROR':
        emitEvent('ERROR', { message: msg.message || 'Connection error' });
        process.exit(1);
        break;
    }
  }

  function attachViewerToRelay(sessionTicket, sessionCrypto) {
    emitEvent('VIEWER_STATUS', { step: 'ATTACHING_RELAY', text: `Attaching E2EE tunnel to Relay...` });
    const relaySocket = connectTransport(relayPort, relayHost, useTls, () => {
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
            setupLocalViewerListener(relaySocket, sessionCrypto, chunk.slice(idx + 1));
          } else {
            relaySocket.destroy();
          }
        }
      }
    });

    relaySocket.on('error', (err) => {
      emitEvent('ERROR', { message: `Relay error: ${err.message}` });
    });
  }

  function setupLocalViewerListener(relaySocket, sessionCrypto, leftover) {
    let earlyBuffers = [];
    if (leftover && leftover.length > 0) earlyBuffers.push(leftover);

    function onEarlyData(c) { earlyBuffers.push(c); }
    relaySocket.on('data', onEarlyData);

    const localServer = net.createServer((viewerSock) => {
      localServer.close();
      relaySocket.removeListener('data', onEarlyData);

      const encryptor = new E2eeEncryptor(sessionCrypto.sendKey);
      const decryptor = new E2eeDecryptor(sessionCrypto.recvKey);

      decryptor.on('error', (err) => {
        emitEvent('ERROR', { message: `E2EE Decryption error: ${err.message}` });
        relaySocket.destroy();
        viewerSock.destroy();
      });

      viewerSock.pipe(encryptor).pipe(relaySocket);
      relaySocket.pipe(decryptor).pipe(viewerSock);

      for (const b of earlyBuffers) decryptor.write(b);
      earlyBuffers = [];

      viewerSock.on('close', () => {
        emitEvent('VIEWER_SESSION_CLOSED');
        relaySocket.destroy();
        process.exit(0);
      });
    });

    localServer.listen(0, '127.0.0.1', () => {
      const port = localServer.address().port;
      const vncPath = findTightVncViewer();

      emitEvent('VIEWER_READY', {
        localPort: port,
        vncPath,
        quality
      });

      if (autoLaunch && vncPath) {
        let vncArgs = [`127.0.0.1:${port}`];
        if (quality === 'fast') {
          // Optimized for College Wi-Fi / Low Bandwidth
          vncArgs.push('-encoding', 'tight', '-quality', '6', '-compresslevel', '7');
        } else if (quality === 'best') {
          vncArgs.push('-encoding', 'tight', '-quality', '9', '-compresslevel', '9');
        }

        try {
          const child = spawn(vncPath, vncArgs, { detached: true, stdio: 'ignore' });
          child.unref();
          emitEvent('VIEWER_LAUNCHED', { pid: child.pid, vncPath });
        } catch (err) {
          emitEvent('ERROR', { message: `Could not launch TightVNC Viewer: ${err.message}` });
        }
      }
    });
  }
}

// ---------------------------------------------------------------------------
// 4. PING / DIAGNOSTICS MODE
// ---------------------------------------------------------------------------
function runPing() {
  const relayHost = getArg('--relay', '127.0.0.1');
  const controlPort = parseInt(getArg('--control-port', '8443'), 10);
  const useTls = !noTls;

  const t0 = Date.now();
  const sock = connectTransport(controlPort, relayHost, useTls, () => {
    const latency = Date.now() - t0;
    emitEvent('PING_OK', { host: relayHost, port: controlPort, latencyMs: latency, tls: useTls });
    sock.destroy();
    process.exit(0);
  });

  sock.setTimeout(4000, () => {
    emitEvent('PING_TIMEOUT', { host: relayHost, port: controlPort });
    sock.destroy();
    process.exit(1);
  });

  sock.on('error', (err) => {
    emitEvent('PING_ERROR', { host: relayHost, port: controlPort, message: err.message });
    process.exit(1);
  });
}

// ---------------------------------------------------------------------------
// Main Entrypoint
// ---------------------------------------------------------------------------
switch (command) {
  case 'relay':
    runRelay();
    break;
  case 'host':
    runHost();
    break;
  case 'viewer':
    runViewer();
    break;
  case 'ping':
    runPing();
    break;
  default:
    console.log(`VNC Remote Connect Core Engine`);
    console.log(`Usage:`);
    console.log(`  node vnc-engine.js relay  [--control-port 8443] [--relay-port 8444] [--pfx path]`);
    console.log(`  node vnc-engine.js host   [--relay host] [--control-port 8443] [--pin 123456] [--device-id id]`);
    console.log(`  node vnc-engine.js viewer [--relay host] [--control-port 8443] --target id --pin 123456 [--quality fast|balanced|best]`);
    console.log(`  node vnc-engine.js ping   --relay host [--control-port 8443]`);
    break;
}

