// A minimal Relay backend in Node.js: opens a room with its own connection key, hands out tokens,
// and logs who joins and leaves through the room's webhooks. Needs Node.js 18 or newer, and no packages.
//
// Usage: node examples/node_backend/backend.mjs [nodeUrl]
//   nodeUrl  HTTP API of a running relay node, default http://127.0.0.1:8080/
//
// Start a node first with `sh startup.sh`. Press Enter to get a command that joins the room with the
// chat client, using a new token. Ctrl+C closes the room, which also stops the node if it was its last room.

import crypto from 'node:crypto';
import http from 'node:http';
import readline from 'node:readline';
import { createToken } from './relay-token.mjs';

const nodeUrl = process.argv[2] ?? 'http://127.0.0.1:8080/';
const webhookUrl = 'http://127.0.0.1:3000/';

// `dotnet run` builds the chat client before it connects, so give each token a minute instead of the usual 30 seconds.
const tokenLifetimeMs = 60_000;

// Calls the node's HTTP API and returns the JSON reply. A failed call throws with the node's reason.
async function api(method, path, body) {
    const response = await fetch(new URL(path, nodeUrl), {
        method,
        headers: { 'Content-Type': 'application/json' },
        body: body && JSON.stringify(body),
    });
    const json = await response.json();
    if (!response.ok)
        throw new Error(`${method} /${path} failed with ${response.status}: ${json.error}`);

    return json;
}

const userByToken = new Map(); // tokens we issued, and who we issued them to
const userByUid = new Map();   // clients in the room
let userCount = 0;

// Webhooks are JSON POSTs. Any reply counts as delivered, so answer right away.
const webhookServer = http.createServer(async (request, response) => {
    let body = '';
    for await (const chunk of request)
        body += chunk;
    response.end();

    const payload = JSON.parse(body);
    switch (request.url) {
        case '/webhooks/client_joined/': {
            // The token tells us which of our users this client is.
            const user = userByToken.get(payload.token) ?? 'someone with an unknown token';
            userByUid.set(payload.clientUid, user);
            console.log(`${user} joined as client ${payload.clientUid}, from ${payload.endpoint}.`);
            break;
        }

        case '/webhooks/client_left/':
            console.log(`${userByUid.get(payload.clientUid)} (client ${payload.clientUid}) left.`);
            userByUid.delete(payload.clientUid);
            break;
    }
});
await new Promise(resolve => webhookServer.listen(new URL(webhookUrl).port, new URL(webhookUrl).hostname, resolve));

// Open a room with its own connection key. Webhook URLs must end with '/'.
const connectionKey = crypto.randomUUID();
let room;
try {
    ({ room } = await api('POST', 'api/rooms', {
        Name: 'Lobby',
        ConnectionKey: connectionKey,
        Webhooks: {
            ClientJoined: new URL('webhooks/client_joined/', webhookUrl).href,
            ClientLeft: new URL('webhooks/client_left/', webhookUrl).href,
        },
    }));
} catch (error) {
    console.error(`Couldn't open a room on ${nodeUrl}: ${error.cause?.message ?? error.message}`);
    console.error('Start a node with `sh startup.sh` first.');
    process.exit(1);
}

console.log(`Opened room "${room.name}" on port ${room.port}. Press Enter for a token, Ctrl+C to close the room.`);

// In a real backend, this is where a user asks to join. Each token is good for one join.
function issueToken() {
    const user = `User ${++userCount}`;
    const token = createToken(room.port, connectionKey, tokenLifetimeMs);
    userByToken.set(token, user);
    console.log(`Token for ${user}, valid for ${tokenLifetimeMs / 1000} seconds. Join with:`);
    console.log(`  dotnet run --project examples/chat_client -- ${new URL(nodeUrl).hostname} ${room.port} "${token}"`);
}

let closing = false;
async function closeRoom() {
    if (closing)
        return;

    closing = true;
    try {
        await api('DELETE', `api/rooms/${room.port}`);
        console.log(`Closed room ${room.port}.`);
    } catch (error) {
        console.error(`Couldn't close room ${room.port}: ${error.cause?.message ?? error.message}`);
    }

    process.exit(0);
}

const input = readline.createInterface({ input: process.stdin, output: process.stdout });
input.on('line', issueToken);

// While reading from a terminal, readline catches Ctrl+C itself instead of letting it reach the process.
input.on('SIGINT', closeRoom);
process.on('SIGINT', closeRoom);
process.on('SIGTERM', closeRoom);
