// VNC Remote Connect - End-to-End Encryption (E2EE) Module
//
// Uses X25519 Ephemeral Key Exchange + HKDF Key Derivation + AES-256-GCM AEAD Framing.
// Native Node.js crypto standard library (zero external dependencies).

const crypto = require('crypto');
const { Transform } = require('stream');

/**
 * Generates an ephemeral X25519 key pair for a session.
 */
function generateSessionKeyPair() {
  const keyPair = crypto.generateKeyPairSync('x25519');
  const pubDer = keyPair.publicKey.export({ type: 'spki', format: 'der' });
  return {
    privateKey: keyPair.privateKey,
    publicKeyDer: pubDer,
    publicKeyBase64: pubDer.toString('base64')
  };
}

/**
 * Derives symmetric directional session keys from Diffie-Hellman secret and Session PIN.
 * Directional keys prevent reflection attacks.
 */
function deriveSessionKeys(myPrivateKey, peerPublicKeyBase64, sessionPin, isHost) {
  const peerPubDer = Buffer.from(peerPublicKeyBase64, 'base64');
  const peerPubKey = crypto.createPublicKey({
    key: peerPubDer,
    format: 'der',
    type: 'spki'
  });

  const sharedSecret = crypto.diffieHellman({
    privateKey: myPrivateKey,
    publicKey: peerPubKey
  });

  // Salt derived from Session PIN
  const salt = crypto.createHash('sha256').update(String(sessionPin)).digest();

  // Directional 32-byte keys
  const hostToViewerKey = crypto.hkdfSync('sha256', sharedSecret, salt, 'VNC_HOST_TO_VIEWER', 32);
  const viewerToHostKey = crypto.hkdfSync('sha256', sharedSecret, salt, 'VNC_VIEWER_TO_HOST', 32);

  if (isHost) {
    return {
      sendKey: Buffer.from(hostToViewerKey),
      recvKey: Buffer.from(viewerToHostKey)
    };
  } else {
    return {
      sendKey: Buffer.from(viewerToHostKey),
      recvKey: Buffer.from(hostToViewerKey)
    };
  }
}

/**
 * Creates an Encrypting Transform Stream: Plaintext -> [Length: 4][IV: 12][Tag: 16][Ciphertext]
 */
class E2eeEncryptor extends Transform {
  constructor(key) {
    super();
    this.key = key;
    this.nonceCounter = 0n;
  }

  _getNonce() {
    const iv = Buffer.alloc(12);
    iv.writeBigUInt64BE(this.nonceCounter, 4);
    this.nonceCounter++;
    return iv;
  }

  _transform(chunk, encoding, callback) {
    // Break into max 16KB blocks to prevent latency / buffer bloat on interactive RFB frames
    const CHUNK_SIZE = 16384;
    let offset = 0;

    while (offset < chunk.length) {
      const sliceLen = Math.min(chunk.length - offset, CHUNK_SIZE);
      const slice = chunk.slice(offset, offset + sliceLen);
      offset += sliceLen;

      const iv = this._getNonce();
      const cipher = crypto.createCipheriv('aes-256-gcm', this.key, iv);
      const encrypted = Buffer.concat([cipher.update(slice), cipher.final()]);
      const tag = cipher.getAuthTag();

      // Frame: [4 bytes payload length] [12 bytes IV] [16 bytes Tag] [Ciphertext]
      const frameLen = 12 + 16 + encrypted.length;
      const header = Buffer.alloc(4);
      header.writeUInt32BE(frameLen, 0);

      this.push(header);
      this.push(iv);
      this.push(tag);
      this.push(encrypted);
    }
    callback();
  }
}

/**
 * Creates a Decrypting Transform Stream: Ciphertext Frames -> Plaintext
 */
class E2eeDecryptor extends Transform {
  constructor(key) {
    super();
    this.key = key;
    this.buffer = Buffer.alloc(0);
  }

  _transform(chunk, encoding, callback) {
    this.buffer = Buffer.concat([this.buffer, chunk]);

    while (this.buffer.length >= 4) {
      const frameLen = this.buffer.readUInt32BE(0);
      if (frameLen < 28) { // 12 (IV) + 16 (Tag)
        return callback(new Error('Corrupted or invalid E2EE frame length'));
      }

      if (this.buffer.length < 4 + frameLen) {
        // Wait for more data
        break;
      }

      const iv = this.buffer.slice(4, 16);
      const tag = this.buffer.slice(16, 32);
      const ciphertext = this.buffer.slice(32, 4 + frameLen);
      this.buffer = this.buffer.slice(4 + frameLen);

      try {
        const decipher = crypto.createDecipheriv('aes-256-gcm', this.key, iv);
        decipher.setAuthTag(tag);
        const decrypted = Buffer.concat([decipher.update(ciphertext), decipher.final()]);
        this.push(decrypted);
      } catch (err) {
        return callback(new Error('E2EE Authentication Tag mismatch! Stream tampered or invalid PIN: ' + err.message));
      }
    }
    callback();
  }
}

module.exports = {
  generateSessionKeyPair,
  deriveSessionKeys,
  E2eeEncryptor,
  E2eeDecryptor
};
