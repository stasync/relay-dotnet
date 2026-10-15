# Relay

[![CI](https://github.com/stasync/relay-dotnet/actions/workflows/ci.yml/badge.svg)](https://github.com/stasync/relay-dotnet/actions/workflows/ci.yml)

Relay is a .NET server that forwards UDP messages between clients. Clients join a room, and every message a client sends is passed on to the other clients in that room, so clients never need to reach each other directly. It fits any app where a group of clients exchanges messages in real time, such as multiplayer games, collaboration tools, chat, or device-to-device messaging.

A backend service controls Relay through a small HTTP API. It opens rooms, hands out short-lived connection tokens, and receives a webhook whenever a client joins or leaves.

Networking is built on [Keel](https://github.com/stasync/keel-dotnet)'s reliable UDP, so each message can be sent unreliably, reliably, or reliably and in order. Relay runs on .NET 10. Clients use Keel too, which has builds for .NET 10 and .NET Standard 2.1, so a client can be almost any .NET app.

## Concepts

| Term | What it is |
|---|---|
| **Node** | One relay process. It serves the HTTP API and runs rooms. |
| **Room** | One UDP port with its own thread. It forwards messages between the clients that joined it. |
| **Client** | One connection in a room, identified by a UID that's unique within that room. |

```
Node  (one process, HTTP API)
├─ Room :50001
│  ├─ Client 1
│  └─ Client 2
└─ Room :50002
   └─ Client 3
```

## How it works

1. The backend starts a node with an HTTP control URL.
2. The backend calls `POST /api/rooms` to open a room, passing a secret `ConnectionKey`.
3. The backend gives each client the room's address and a token encrypted with that key.
4. The client connects to the room over UDP with the token. The room checks the token, and the client sends an `OnAuth` message to join.
5. The room forwards packets between its clients, and calls the backend's webhooks when clients join or leave.

## Requirements

- [.NET 10 SDK](https://dotnet.microsoft.com/download)

Keel is downloaded automatically during the first build.

## Building

```bash
sh build.sh
```

The script runs the tests and builds in release mode. The result goes to `artifacts/bin/relay/release/`, which git ignores. If the tests or the build fail, the script stops with a non-zero exit code.

For a quick debug build, use `dotnet build runtime/relay.csproj`.

### Keel

The Keel DLLs are not checked in. On the first build, `runtime/relay.csproj` downloads `keel-<version>-net10.0.zip` from [Keel's GitHub releases](https://github.com/stasync/keel-dotnet/releases) and unpacks it into `plugins/keel/<version>/net10.0/`. Later builds reuse that copy. The `plugins/` folder is ignored by git.

The version comes from the `KeelVersion` property in `runtime/relay.csproj`. The default, `latest-build`, is a pre-release that Keel rebuilds after every change to its `main` branch.

- **Getting a newer `latest-build`:** the download only happens when the DLLs are missing. Delete `plugins/keel/` and build again.
- **Pinning a version:** set `KeelVersion` to a release tag. Then update the three `HintPath`s in `tests/relay_tests.csproj`, which point at the `latest-build` folder.

## Running

The quickest way to start a node is `startup.sh`. It builds Relay with `dotnet run` and runs the node in the foreground, with the HTTP API on `http://127.0.0.1:8080/` and a new random [API key](#http-api), which it prints. Stop it with Ctrl+C.

```bash
sh startup.sh
```

Set `HTTP_LISTENER_URL` to use a different address, and `HTTP_API_KEY` to use a key of your own. Any arguments are passed on to the node:

```bash
HTTP_LISTENER_URL=http://127.0.0.1:9000/ sh startup.sh -maxClientsPerRoom 32
```

When started this way, the node's log files and `.cmdparam` files live in `runtime/bin/Release/net10.0/`.

To run the output of `build.sh` instead:

```bash
dotnet artifacts/bin/relay/release/relay.dll -httpListenerUrl http://127.0.0.1:8080/ -httpApiKey <key>
```

Either way, the node starts with the HTTP API and no rooms open. Open rooms through the API, as shown under [Opening a room](#opening-a-room).

The HTTP address must name a specific host, such as `127.0.0.1` or the machine's IP. `0.0.0.0` doesn't bind, and wildcards like `*` aren't accepted.

For a quick local test without tokens, also open a room at startup:

```bash
sh startup.sh -roomPort 7777
```

Then join it from two other terminals with the [example chat client](examples/chat_client/Program.cs), and type messages in either one:

```bash
dotnet run --project examples/chat_client -- 127.0.0.1 7777
```

### Launch arguments

Launch arguments configure the node and apply to all of its rooms. They use a single dash: `-name value`. Each one can also be set with a file named `<name>.cmdparam` next to `relay.dll`, containing just the value.

| Argument | Default | Description |
|---|---|---|
| `httpListenerUrl` | empty | Address for the HTTP API, such as `http://127.0.0.1:8080/`. It must end with `/`. Without it, the API and webhooks are off. |
| `httpApiKey` | empty | Secret that every HTTP API request must send as `Authorization: Bearer <key>`. Use a long random value, such as the output of `openssl rand -hex 32`. Without it, the API and webhooks are off. |
| `roomPort` | none | Opens a room on this UDP port at startup. Repeat it to open more rooms. `0` picks a free port. These rooms have no connection key, so anyone can join: use them only for local testing. |
| `maxClientsPerRoom` | `16` | Maximum number of clients in one room. |
| `protocolKey` | `0` | Key that UDP packets must carry to be accepted. Keel's `ReliableUdpClient` always sends `0`, so keep the default unless your clients use a different key. |
| `roomUpdateIntervalMs` | `50` | How often each room's thread processes traffic, in milliseconds. |
| `blacklistDurationMs` | `10000` | How long a misbehaving IP address stays blocked. `0` turns blocking off. |
| `logConnectionRejections` | `false` | Logs every rejected connection attempt. |
| `logLevel` | `All` | Lowest level to log: `All`, `Warning`, `Error`, `Exception` or `Disabled`. |
| `logOutputRelativePath` | `logs` | Folder for log files, relative to `relay.dll`. Each run writes `app_<pid>.log`. |

### Shutting down

A node exits when its last room closes. That happens after the final room is closed through the API (`DELETE /api/rooms` closes them all), or on Ctrl+C or `SIGTERM`, which close every room first.

## HTTP API

The backend controls a node through a REST API. Every response is JSON, and each path accepts only the methods listed for it; any other method gets 405.

Every request must send the node's API key (the `httpApiKey` launch argument) as `Authorization: Bearer <key>`, or it gets 401. The key travels as plain text, so keep the API on `127.0.0.1` or a private network. [Known issues](docs/known-issues.md) lists what the key doesn't protect, such as webhooks and log files.

| Method | Path | What it does |
|---|---|---|
| `GET` | `/api/status` | Health check. Returns the node's process ID as `pid`. |
| `GET` | `/api/node` | Returns the node's memory use and running threads. |
| `GET` | `/api/rooms` | Lists every room with its client UIDs and traffic counters. |
| `POST` | `/api/rooms` | Opens a room. The body holds the room settings as JSON. Returns 201 with the room's `name` and `port`. |
| `DELETE` | `/api/rooms` | Closes every room. |
| `GET` | `/api/rooms/{port}` | Returns one room. |
| `DELETE` | `/api/rooms/{port}` | Closes one room. |
| `GET` | `/api/rooms/{port}/clients` | Lists a room's clients with their tokens. Add `?token=` to find the client that joined with a token. |
| `GET` | `/api/rooms/{port}/clients/{uid}` | Returns one client, or 404 if it isn't in the room. |
| `DELETE` | `/api/rooms/{port}/clients/{uid}` | Disconnects a client. The other clients get `OnDisconnect` and the `ClientLeft` webhook fires. |

Errors use the matching status code (400, 401, 404, 405, 409, or 500 for anything unexpected), with the reason in an `error` field.

[Using the HTTP API](docs/http-api-guide.md) walks through a room's whole life, from opening it to closing it, with `curl`, C# and Node.js examples. For each endpoint's parameters, responses and errors, see the [HTTP API reference](docs/http-api.md).

### Opening a room

Send the room settings as the JSON body of a `POST`:

```bash
curl -X POST "http://127.0.0.1:8080/api/rooms" -H "Authorization: Bearer $HTTP_API_KEY" -H "Content-Type: application/json" --data '{
  "TargetPort": 0,
  "ConnectionKey": "6f9b2c1e-4d7a-4e8b-9a3f-1c5d7e9b2a40",
  "Webhooks": {
    "ClientJoined": "http://127.0.0.1:3000/webhooks/client_joined/",
    "ClientLeft": "http://127.0.0.1:3000/webhooks/client_left/"
  }
}'
```

```json
{"status":"success","timestamp":"2026-10-12T16:13:03.333247Z","method":"POST","room":{"name":"Room_53247","port":53247}}
```

The response is 201 Created, and its `Location` header points at the new room, such as `http://127.0.0.1:8080/api/rooms/53247/`. `TargetPort` `0` picks a free port. Leave out `ConnectionKey` only for testing: without it, anyone can join. Every setting is listed under [Room settings](docs/http-api.md#room-settings).

## Webhooks

Each room can call two URLs on the backend, set in its `Webhooks` settings:

- `ClientJoined` receives the client's UID, the token it joined with, and its address. It fires when the client sends `OnAuth`, not when the UDP connection opens.
- `ClientLeft` receives the client's UID when it leaves: it disconnects, times out, or is dropped through the API. It isn't sent when the room itself is closed.

**Webhook URLs must end with `/`.** Without it, the node logs an error and never calls the URL. A URL that can't be reached is retried every 4 seconds. The payloads and delivery rules are in the [HTTP API reference](docs/http-api.md#webhooks).

## Connection tokens

When a room has a `ConnectionKey`, each client must present a token when connecting. A token is this JSON, encrypted with the room's key, with times in Unix milliseconds:

```json
{ "TargetPort": 49791, "IssueUnixTime": 1791622588239, "ExpirationUnixTime": 1791622618239 }
```

In C#, Keel's `EncryptionUtility` creates one:

```csharp
using System;
using System.Text.Json;
using Keel.Utils.Security;

var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
var json = JsonSerializer.Serialize(new
{
    TargetPort = roomPort,
    IssueUnixTime = now,
    ExpirationUnixTime = now + 30_000
});
var token = EncryptionUtility.Encrypt(json, connectionKey);
```

In Node.js, [`relay-token.mjs`](examples/node_backend/relay-token.mjs) creates one with the built-in `crypto` module. Copy it into your backend:

```js
import { createToken } from './relay-token.mjs';

const token = createToken(roomPort, connectionKey); // valid for 30 seconds
```

The encryption is AES-256-CBC with an HMAC-SHA256 integrity check, written as three base64 parts: `iv:ciphertext:mac`. To produce it in another language: the AES key is the SHA-256 of the `ConnectionKey`, the HMAC key is the SHA-256 of `mac:` followed by the `ConnectionKey`, the IV is 16 random bytes, and the MAC covers the IV followed by the ciphertext. The `ConnectionKey` must be at least 8 characters long, or the room rejects every token.

A room accepts a token only if all of these hold:

- It decrypts with the room's `ConnectionKey`, and the integrity check passes.
- `TargetPort` matches the room's port.
- It hasn't expired.
- No one else has used it yet. A token stays reserved until it expires, even after its client disconnects, so a reconnecting client needs a new token.

When a check fails, the room rejects the connection and blocks the client's IP address for `blacklistDurationMs`. Every other client behind that address, such as one on the same home or office network, is blocked too.

## Writing a client

Clients connect with Keel's `ReliableUdpClient`. The first byte of every message is its type. Values marked *packed* are written with `NetWriter.WritePackedUInt32` and read with `NetReader.ReadPackedUInt32`. The [UDP protocol reference](docs/udp-protocol.md) has the byte-level layout of everything on the wire, including Keel's own framing, and the size limits.

Messages from the client:

| Type | Byte | Contents |
|---|---|---|
| `OnAuth` | `0` | Nothing else. Send it once, right after `Connected` fires. The room ignores a client until it arrives. |
| `OnPacket` | `1` | Op code (packed), receiver count (packed), each receiver's UID (packed), then the payload (`WriteBytesAndSize`). |

A receiver count of `0` sends the packet to everyone except the sender. Otherwise only the listed UIDs get it. The room forwards each packet with the same delivery method the client used.

Messages from the room:

| Type | Byte | Contents |
|---|---|---|
| `OnJoined` | `0` | The new client's UID (packed), the client count (packed), then every UID in the room, including the new one (packed). Sent to everyone, including the new client. |
| `OnPacket` | `1` | Sender's UID (packed), op code (packed), payload (`ReadBytesAndSize`). |
| `OnDisconnect` | `2` | The UID of the client that left (packed). |

The room never reads the op code or payload, so their meaning is up to you. A client can spot its own `OnJoined` by comparing the UID with `client.ConnectionUid`.

The room blocks the sender's IP address if a message is malformed, has an unknown type, or is an `OnPacket` sent before `OnAuth`.

### Example

This is the core of a client. [`examples/chat_client`](examples/chat_client/Program.cs) is a complete one you can run: it joins a room, sends each line you type to the other clients, and prints what they send.

```csharp
using System;
using System.Net;
using System.Text;
using System.Threading;
using Keel.Networking;
using Keel.Networking.Udp;
using Keel.Networking.Udp.LowLevel;

var client = new ReliableUdpClient();

// Join the room as soon as the UDP connection is up.
client.Connected += () =>
    client.Send(new byte[] { 0 }, UdpProtocol.DeliveryMethod.ReliableOrdered); // OnAuth

client.ConnectionRejected += reason => Console.WriteLine($"Rejected: {reason}");

client.DataReceived += (data, deliveryMethod) =>
{
    var reader = new NetReader(data);
    switch (reader.ReadByte())
    {
        case 0: // OnJoined
            var joinedUid = reader.ReadPackedUInt32();
            var clientCount = reader.ReadPackedUInt32();
            Console.WriteLine($"Client {joinedUid} joined, {clientCount} clients here");
            break;

        case 1: // OnPacket
            var senderUid = reader.ReadPackedUInt32();
            var opCode = reader.ReadPackedUInt32();
            var payload = reader.ReadBytesAndSize();
            Console.WriteLine($"Client {senderUid} sent op {opCode}: {Encoding.UTF8.GetString(payload)}");
            break;

        case 2: // OnDisconnect
            Console.WriteLine($"Client {reader.ReadPackedUInt32()} left");
            break;
    }
};

client.Connect(new IPEndPoint(IPAddress.Parse(nodeHost), roomPort), connectionData: token);

while (true)
{
    client.Update();
    Thread.Sleep(10);
}
```

Once the client has joined, this sends "hello" with op code `7` to everyone else in the room:

```csharp
var writer = new NetWriter();
writer.WriteByte(1);          // OnPacket
writer.WritePackedUInt32(7);  // op code
writer.WritePackedUInt32(0);  // no receivers listed: everyone except me
writer.WriteBytesAndSize(Encoding.UTF8.GetBytes("hello"));

client.Send(writer.AsArraySegment(), UdpProtocol.DeliveryMethod.ReliableOrdered);
```

If the room has a `UdpObfuscationKey`, register the same key on the client right after `Connect()`, because the client creates its socket there:

```csharp
using Keel.Networking.Udp.LowLevel.DataTransferLayers;

client.Connect(roomEndPoint, connectionData: token);
client.RegisterDataTransferLayer(new DataTransferXorObfuscationLayer(udpObfuscationKey));
```

### Reusable client

[`examples/relay_client`](examples/relay_client/RelayClient.cs) wraps all of the above in one class, `RelayClient`, for use in your own app. Copy `RelayClient.cs` into a project that references `Keel.Utils.dll` and `Keel.Networking.dll`, or reference `relay_client.csproj`. It builds for .NET 10 and .NET Standard 2.1 with C# 8, so it works wherever Keel does.

It handles the protocol for you:

- It sends `OnAuth` once connected, and keeps `Clients` up to date as clients join and leave.
- It raises `Disconnected` exactly once per `Connect()`, with the reason: `Left`, `Unreachable`, `RoomFull`, `InvalidToken` or `ConnectionLost`.
- It throws instead of sending a packet before the room confirms the join, which would get the client's IP address blocked.

```csharp
using System;
using System.Text;
using System.Threading;
using UdpRelay.Client;

var client = new RelayClient();

client.Joined += () => Console.WriteLine($"Joined as client {client.Uid}. Clients here: {string.Join(", ", client.Clients)}");
client.ClientJoined += uid => Console.WriteLine($"Client {uid} joined");
client.ClientLeft += uid => Console.WriteLine($"Client {uid} left");
client.PacketReceived += (senderUid, opCode, payload, deliveryMethod) =>
    Console.WriteLine($"Client {senderUid} sent op {opCode}: {Encoding.UTF8.GetString(payload)}");
client.Disconnected += reason => Console.WriteLine($"Disconnected: {reason}");

client.Connect(nodeHost, roomPort, token);

while (true)
{
    // Events are raised from inside Update(), on this thread.
    client.Update();
    Thread.Sleep(10);
}
```

Once `Joined` fires, send with an op code and a payload. The delivery method defaults to `ReliableOrdered`:

```csharp
client.Send(7, Encoding.UTF8.GetBytes("hello"));                              // everyone except me
client.SendTo(otherUid, 7, payload, UdpProtocol.DeliveryMethod.Unreliable);  // one client
client.SendTo(new[] { firstUid, secondUid }, 7, payload);                    // the listed clients
```

Each packet must fit in one UDP datagram, which usually leaves 1429 bytes for the payload. A bigger one throws, so split large data across packets. [Size limits](docs/udp-protocol.md#size-limits) has the exact numbers.

A handler can call `Disconnect()`. To reconnect, call `Connect()` again with a new token, even from the `Disconnected` handler.

## Data transfer layers and packet simulators

Keel lets a room or a client hook into every datagram right at the socket:

- **Data transfer layers** see each datagram in both directions and can change its bytes. A room registers up to three, depending on its settings: XOR obfuscation for `UdpObfuscationKey`, the rate limiter for `ClientRateLimiter`, and traffic counters for `CaptureDataTransferAmount`.
- **Packet simulators** drop, delay or duplicate incoming datagrams, to test how a client copes with a bad network. Keel has six, from random loss to dropping everything. Relay doesn't register any.

Register both once, at startup: a room in its constructor, and a client right after each `Connect()`. [Data transfer layers and packet simulators](docs/layers-and-simulators.md) covers the order they run in, the ones Keel provides, how to write your own, and what each simulated problem looks like to your app.

## Tests

```bash
dotnet test tests/relay_tests.csproj
```

The tests cover parsing of room settings, token encryption, and matching of API paths.

To format the code, run `sh dotnet_format.sh`.

## Continuous integration

GitHub Actions runs [`ci.yml`](.github/workflows/ci.yml) on every push:

- **Any branch:** runs the tests and builds the example clients.
- **`main`:** runs the tests, builds the relay in release mode, and publishes it as the [`latest-build`](https://github.com/stasync/relay-dotnet/releases/tag/latest-build) pre-release. Each push to `main` replaces that release and moves its tag to the new commit.

The release contains one file, `relay-latest-build-net10.0.zip`. Unzip it and run `dotnet relay.dll -httpListenerUrl http://127.0.0.1:8080/ -httpApiKey <key>` on a machine with the .NET 10 runtime. Other projects can download it from a fixed URL:

```
https://github.com/stasync/relay-dotnet/releases/download/latest-build/relay-latest-build-net10.0.zip
```

## Project layout

```
.github/workflows/ci.yml       tests every push; builds and releases main
runtime/                       the relay node (relay.csproj)
  Entry.cs                     startup, launch arguments, HTTP routes, shutdown
  Room.cs                      one room: token checks, routing, blocking
  RoomSettings.cs              settings accepted by POST /api/rooms
  HttpServer.cs                HTTP listener: checks the API key, then matches each request's method and path to a handler
  RouteTemplate.cs             path templates such as /api/rooms/{port}/clients/{uid}
  HttpRequestHandler.cs        HTTP API endpoints
  HttpWebhookSender.cs         webhooks, with retry
  IncomingDataAmountTracker.cs per-client rate limit
tests/                         xUnit tests
docs/http-api.md               HTTP API and webhook reference
docs/http-api-guide.md         walkthrough of the HTTP API, with curl, C# and Node.js examples
docs/udp-protocol.md           byte-level layout of the UDP protocol, with size limits
docs/layers-and-simulators.md  data transfer layers and packet simulators
docs/known-issues.md           security gaps and limitations, with workarounds
examples/chat_client/          runnable chat client that joins a room
examples/relay_client/         reusable client class to copy into your app
examples/node_backend/         Node.js backend: opens a room, issues tokens, receives webhooks
build.sh                       tests and builds into artifacts/
startup.sh                     builds and runs a node in the foreground
dotnet_format.sh               runs dotnet format on every project
```
