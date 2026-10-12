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
2. The backend calls `/api/room/new/` to open a room, passing a secret `ConnectionKey`.
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

The quickest way to start a node is `startup.sh`. It builds Relay with `dotnet run` and runs the node in the foreground, with the HTTP API on `http://127.0.0.1:8080/`. Stop it with Ctrl+C.

```bash
sh startup.sh
```

Set `HTTP_LISTENER_URL` to use a different address. Any arguments are passed on to the node:

```bash
HTTP_LISTENER_URL=http://127.0.0.1:9000/ sh startup.sh -maxClientsPerRoom 32
```

When started this way, the node's log files and `.cmdparam` files live in `runtime/bin/Release/net10.0/`.

To run the output of `build.sh` instead:

```bash
dotnet artifacts/bin/relay/release/relay.dll -httpListenerUrl http://127.0.0.1:8080/
```

Either way, the node starts with the HTTP API and no rooms open. Open rooms through the API, as shown under [Opening a room](#opening-a-room).

The HTTP address must name a specific host, such as `127.0.0.1` or the machine's IP. `0.0.0.0` doesn't bind, and wildcards like `*` aren't accepted.

For a quick local test without tokens, also open a room at startup:

```bash
sh startup.sh -roomPort 7777
```

### Launch arguments

Launch arguments configure the node and apply to all of its rooms. They use a single dash: `-name value`. Each one can also be set with a file named `<name>.cmdparam` next to `relay.dll`, containing just the value.

| Argument | Default | Description |
|---|---|---|
| `httpListenerUrl` | empty | Address for the HTTP API, such as `http://127.0.0.1:8080/`. It must end with `/`. Without it, the API and webhooks are off. |
| `roomPort` | none | Opens a room on this UDP port at startup. Repeat it to open more rooms. `0` picks a free port. These rooms have no connection key, so anyone can join: use them only for local testing. |
| `maxClientsPerRoom` | `16` | Maximum number of clients in one room. |
| `protocolKey` | `0` | Key that UDP packets must carry to be accepted. Keel's `ReliableUdpClient` always sends `0`, so keep the default unless your clients use a different key. |
| `roomUpdateIntervalMs` | `50` | How often each room's thread processes traffic, in milliseconds. |
| `blacklistDurationMs` | `10000` | How long a misbehaving IP address stays blocked. `0` turns blocking off. |
| `logConnectionRejections` | `false` | Logs every rejected connection attempt. |
| `logLevel` | `All` | Lowest level to log: `All`, `Warning`, `Error`, `Exception` or `Disabled`. |
| `logOutputRelativePath` | `logs` | Folder for log files, relative to `relay.dll`. Each run writes `app_<pid>.log`. |

### Shutting down

A node exits when its last room closes. That happens after `/api/room/close/` closes the final room, or on Ctrl+C or `SIGTERM`, which close every room first.

## HTTP API

The backend controls a node through these endpoints. Every endpoint answers `GET` requests and returns JSON. **Paths must end with a slash:** `/api/room/status/` works, but `/api/room/status` returns 404. The API has no authentication, so keep it on a local address such as `127.0.0.1`.

| Endpoint | Query | What it does |
|---|---|---|
| `/api/status/` | | Health check. Returns the node's process ID as `pid`. |
| `/api/node/info/` | | Returns the node's memory use and running threads. |
| `/api/room/new/` | `config` (optional) | Opens a room. Returns its `name` and `port`. |
| `/api/room/status/` | `port` (optional) | Returns every room, or just one, with its client UIDs and traffic counters. |
| `/api/room/has_client/` | `port`, `uid` | Returns `hasClient`: whether that client is in the room. |
| `/api/room/get_client/` | `port`, plus `uid` or `token` | Finds a client by UID, or by the token it joined with. Returns `clientUid`. |
| `/api/room/drop_client/` | `port`, `uid` | Disconnects a client. The other clients get `OnDisconnect` and the `ClientLeft` webhook fires. |
| `/api/room/close/` | `port` (optional) | Closes one room, or all of them. Returns `closedRooms`. |

A failed request returns HTTP 500, or 404 for an unknown path, with the reason in an `error` field.

For each endpoint's parameters, responses and errors, see the [HTTP API reference](docs/http-api.md).

### Opening a room

Pass the room settings as JSON in the `config` query parameter:

```bash
curl -G "http://127.0.0.1:8080/api/room/new/" --data-urlencode 'config={
  "TargetPort": 0,
  "ConnectionKey": "6f9b2c1e-4d7a-4e8b-9a3f-1c5d7e9b2a40",
  "Webhooks": {
    "ClientJoined": "http://127.0.0.1:3000/webhooks/client_joined/",
    "ClientLeft": "http://127.0.0.1:3000/webhooks/client_left/"
  }
}'
```

```json
{"status":"success","timestamp":"2026-10-12T15:54:43.576478Z","method":"GET","room":{"name":"Room_58792","port":58792}}
```

`TargetPort` `0` picks a free port. Leave out `ConnectionKey` only for testing: without it, anyone can join. Every setting is listed under [Room settings](docs/http-api.md#room-settings).

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

The encryption is AES-256-CBC with an HMAC-SHA256 integrity check, written as three base64 parts: `iv:ciphertext:mac`. `crypto_utils.ts` in lro_server produces the same format, so its tokens work without changes.

A room accepts a token only if all of these hold:

- It decrypts with the room's `ConnectionKey`, and the integrity check passes.
- `TargetPort` matches the room's port.
- It hasn't expired.
- No one else has used it yet. A token stays reserved until it expires, even after its client disconnects, so a reconnecting client needs a new token.

When a check fails, the room rejects the connection and blocks the client's IP address for `blacklistDurationMs`. Every other client behind that address, such as one on the same home or office network, is blocked too.

## Writing a client

Clients connect with Keel's `ReliableUdpClient`. The first byte of every message is its type. Values marked *packed* are written with `NetWriter.WritePackedUInt32` and read with `NetReader.ReadPackedUInt32`.

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

If the room has a `UdpEncryptionKey`, register the same key on the client right after `Connect()`, because the client creates its socket there:

```csharp
using Keel.Networking.Udp.LowLevel.DataTransferLayers;

client.Connect(roomEndPoint, connectionData: token);
client.RegisterDataTransferLayer(new DataTransferXorObfuscationLayer(udpEncryptionKey));
```

## Tests

```bash
dotnet test tests/relay_tests.csproj
```

The tests cover parsing of room settings and token encryption.

To format the code, run `sh dotnet_format.sh`.

## Continuous integration

GitHub Actions runs [`ci.yml`](.github/workflows/ci.yml) on every push:

- **Any branch:** runs the tests.
- **`main`:** runs the tests, builds the relay in release mode, and publishes it as the [`latest-build`](https://github.com/stasync/relay-dotnet/releases/tag/latest-build) pre-release. Each push to `main` replaces that release and moves its tag to the new commit.

The release contains one file, `relay-latest-build-net10.0.zip`. Unzip it and run `dotnet relay.dll -httpListenerUrl http://127.0.0.1:8080/` on a machine with the .NET 10 runtime. Other projects can download it from a fixed URL:

```
https://github.com/stasync/relay-dotnet/releases/download/latest-build/relay-latest-build-net10.0.zip
```

## Project layout

```
.github/workflows/ci.yml       tests every push; builds and releases main
runtime/                       the relay node (relay.csproj)
  Entry.cs                     startup, launch arguments, HTTP routes, shutdown
  Room.cs                      one room: token checks, routing, blocking
  RoomSettings.cs              settings accepted by /api/room/new/
  HttpServer.cs                HTTP listener
  HttpRequestHandler.cs        HTTP API endpoints
  HttpWebhookSender.cs         webhooks, with retry
  IncomingDataAmountTracker.cs per-client rate limit
tests/                         xUnit tests
docs/http-api.md               HTTP API and webhook reference
build.sh                       tests and builds into artifacts/
startup.sh                     builds and runs a node in the foreground
dotnet_format.sh               runs dotnet format on every project
```
