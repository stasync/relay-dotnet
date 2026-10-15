// Creates Relay connection tokens in Node.js, using only the built-in crypto module. Copy this file into your backend.
//
// A token is JSON encrypted with the room's ConnectionKey, in the format of Keel's EncryptionUtility,
// which the room decrypts it with. See "Connection tokens" in the README.

import crypto from 'node:crypto';

// Keel refuses shorter keys, so a room opened with one rejects every token.
const MIN_KEY_LENGTH = 8;

/**
 * Creates a token that lets one client join one room.
 * @param {number} roomPort - The room's port, as returned by POST /api/rooms.
 * @param {string} connectionKey - The ConnectionKey the room was opened with.
 * @param {number} [lifetimeMs] - How long the client has to join with the token.
 * @returns {string} The token to give the client.
 */
export function createToken(roomPort, connectionKey, lifetimeMs = 30_000) {
    const now = Date.now();
    return encrypt(
        JSON.stringify({ TargetPort: roomPort, IssueUnixTime: now, ExpirationUnixTime: now + lifetimeMs }),
        connectionKey);
}

// AES-256-CBC, then an HMAC-SHA256 over the IV and ciphertext, written as "iv:ciphertext:mac" in base64.
function encrypt(data, secret) {
    if (secret.length < MIN_KEY_LENGTH)
        throw new Error(`The connection key must be at least ${MIN_KEY_LENGTH} characters long.`);

    const key = sha256(secret);
    const macKey = sha256('mac:' + secret);
    const iv = crypto.randomBytes(16);

    const cipher = crypto.createCipheriv('aes-256-cbc', key, iv);
    const ciphertext = Buffer.concat([cipher.update(data, 'utf8'), cipher.final()]);
    const mac = crypto.createHmac('sha256', macKey).update(iv).update(ciphertext).digest();

    return `${iv.toString('base64')}:${ciphertext.toString('base64')}:${mac.toString('base64')}`;
}

function sha256(text) {
    return crypto.createHash('sha256').update(text, 'utf8').digest();
}
