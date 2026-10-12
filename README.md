# Relay

Relay is a UDP relay server for multiplayer games. Players connect to a relay port, and the relay forwards each player's messages to the other players on that port, so players never need to reach each other directly.

A backend service controls the relay through a small HTTP API. It opens relay ports, hands out short-lived connection tokens, and receives a webhook whenever a player joins or leaves. One process can host many relay ports, each on its own thread.

Networking is built on [Keel](https://github.com/stasync/keel-dotnet)'s reliable UDP, so each message can be sent unreliably, reliably, or reliably and in order.

## How it works

1. The backend starts the relay process with an HTTP control URL.
2. The backend calls `/api/relay/new/` to open a relay port, passing a secret `ConnectionKey`.
3. The backend gives each player the relay's address and a token encrypted with that key.
4. The player connects over UDP with the token. The relay checks the token, and the player sends an `OnAuth` message to join.
5. The relay forwards packets between players on the same port, and calls the backend's webhooks when players join or leave.

## Requirements

- [.NET 10 SDK](https://dotnet.microsoft.com/download)

Keel is downloaded automatically during the first build.

## Building

```bash
sh build.sh
```

The script runs the tests, builds in release mode, and copies the result to `build/bin/rt_server/`.

For a quick debug build, use `dotnet build runtime/rt_server.csproj`.

### Keel

The Keel DLLs are not checked in. On the first build, `runtime/rt_server.csproj` downloads `keel-<version>-net10.0.zip` from [Keel's GitHub releases](https://github.com/stasync/keel-dotnet/releases) and unpacks it into `plugins/keel/<version>/net10.0/`. Later builds reuse that copy. The `plugins/` folder is ignored by git.

The version comes from the `KeelVersion` property in `runtime/rt_server.csproj`. The default, `latest-build`, is a pre-release that Keel rebuilds after every change to its `main` branch.

- **Getting a newer `latest-build`:** the download only happens when the DLLs are missing. Delete `plugins/keel/` and build again.
- **Pinning a version:** set `KeelVersion` to a release tag. Then update the three `HintPath`s in `tests/rt_server_tests.csproj`, which point at the `latest-build` folder.

## Running

```bash
dotnet build/bin/rt_server/rt_server.dll -httpListenerUrl http://127.0.0.1:8080/
```

This starts the HTTP API with no relays open. Open relays through the API, as shown under [Opening a relay](#opening-a-relay).

For a quick local test without the API or tokens:

```bash
dotnet build/bin/rt_server/rt_server.dll -relayPort 7777
```

### Launch arguments

Arguments use a single dash: `-name value`. Each one can also be set with a file named `<name>.cmdparam` next to `rt_server.dll`, containing just the value.

| Argument | Default | Description |
|---|---|---|
| `httpListenerUrl` | empty | Address for the HTTP API, such as `http://127.0.0.1:8080/`. It must end with `/`. Without it, the API and webhooks are off. |
| `relayPort` | none | Opens a relay on this UDP port at startup. Repeat it to open more ports. `0` picks a free port. These relays have no connection key, so anyone can join: use them only for local testing. |
| `maxConnectionsPerPort` | `16` | Maximum number of players on one relay port. |
| `relayProtocolKey` | `0` | Key that UDP packets must carry to be accepted. Keel's `ReliableUdpClient` always sends `0`, so keep the default unless your clients use a different key. |
| `relayMillisecondsTimeStep` | `50` | How often each relay thread processes traffic, in milliseconds. |
| `blacklistDurationMs` | `10000` | How long a misbehaving IP address stays blocked. `0` turns blocking off. |
| `logConnectionRejections` | `false` | Logs every rejected connection attempt. |
| `logLevel` | `All` | Lowest level to log: `All`, `Warning`, `Error`, `Exception` or `Disabled`. |
| `logOutputRelativePath` | `logs` | Folder for log files, relative to `rt_server.dll`. Each run writes `app_<pid>.log`. |

### Shutting down

The process exits when its last relay closes. That happens after `/api/relay/shutdown/` closes the final relay, or on Ctrl+C or `SIGTERM`, which close every relay first.

## HTTP API

Every endpoint answers `GET` requests and returns JSON. **Paths must end with a slash:** `/api/relay/status/` works, but `/api/relay/status` returns 404.

| Endpoint | Query | What it does |
|---|---|---|
| `/api/status/` | | Health check. Returns the process ID as `pid`. |
| `/api/relay/new/` | `config` (optional) | Opens a relay. Returns `relayPort` and `workerName`. |
| `/api/relay/status/` | `port` (optional) | Returns every relay, or just one, with its connection UIDs and traffic counters. |
| `/api/relay/has_connection/` | `port`, `uid` | Returns `hasConnection`: whether that UID is joined. |
| `/api/relay/get_connection/` | `port`, plus `uid` or `token` | Finds a connection by UID, or by the token it joined with. Returns `connectionUid`. |
| `/api/relay/drop_connection/` | `port`, `uid` | Disconnects a player. The other players get `OnDisconnect` and the disconnect webhook fires. |
| `/api/relay/shutdown/` | `port` (optional) | Closes one relay, or all of them. Returns `closedRelays`. |
| `/api/system/info/` | | Returns memory use and the running worker threads. |

A successful response always includes `"status": "success"`, a `timestamp`, and the request `method`. A failed request returns HTTP 500, or 404 for an unknown path, with a body like this:

```json
{
  "code": 500,
  "error": "/api/relay/status/: Internal server error: Unable to find relay worker with port 9000.",
  "timestamp": "2026-10-10T10:56:57.399358Z"
}
```

### Opening a relay

Pass the relay settings as JSON in the `config` query parameter:

```bash
curl -G "http://127.0.0.1:8080/api/relay/new/" --data-urlencode 'config={
  "TargetPort": 0,
  "ConnectionKey": "6f9b2c1e-4d7a-4e8b-9a3f-1c5d7e9b2a40",
  "Webhooks": {
    "UserConnected": "http://127.0.0.1:3000/webhooks/relay_user_connected",
    "UserDisconnected": "http://127.0.0.1:3000/webhooks/relay_user_disconnected"
  }
}'
```

```json
{"status":"success","timestamp":"2026-10-10T10:56:28.239751Z","method":"GET","relayPort":49791,"workerName":"RelayThread_49791"}
```

Every field is optional, and field names are case-sensitive.

| Field | Default | Description |
|---|---|---|
| `TargetPort` | `0` | UDP port to listen on. `0` picks a free port. |
| `WorkerName` | `RelayThread_<port>` | Thread name shown in logs and status. |
| `ConnectionKey` | empty | Secret used to check connection tokens. If it's empty, anyone can join, so use that only for testing. |
| `UdpEncryptionKey` | empty | If set, all UDP traffic is XOR-scrambled with this key, and clients must do the same (see [Writing a client](#writing-a-client)). This hides traffic from casual inspection but is not real encryption. |
| `CaptureDataTransferAmount` | `false` | Counts traffic, reported as `bytesSent` and `bytesReceived` in the status endpoint. When off, both are `0`. |
| `Webhooks.UserConnected` | empty | URL to POST to when a player joins. |
| `Webhooks.UserDisconnected` | empty | URL to POST to when a player leaves. |
| `ClientRateLimiter.KBitPerSecLimit` | `0` | Maximum incoming traffic per player, in kbit/s. A player who goes over it has their IP address blocked. `0` means no limit. |

## Webhooks

Each relay can POST JSON to the two URLs in its `Webhooks` config. Webhooks are only sent when the HTTP API is on.

A player joining sends this to `UserConnected`. It fires when the player sends `OnAuth`, not when the UDP connection opens:

```json
{
  "relayPort": 49791,
  "connUid": 1,
  "token": "<the token the player connected with>",
  "endpoint": "203.0.113.5:54012",
  "timestamp": "2026-10-10T10:56:28.239751Z"
}
```

A player leaving, for any reason, sends this to `UserDisconnected`:

```json
{
  "relayPort": 49791,
  "connUid": 1,
  "timestamp": "2026-10-10T10:56:31.102938Z"
}
```

If a webhook URL can't be reached (a network error or no answer within 2 seconds), the relay keeps retrying it every 4 seconds. Any HTTP response, including an error status, counts as delivered.

## Connection tokens

When a relay has a `ConnectionKey`, each player must present a token when connecting. A token is this JSON, encrypted with the relay's key, with times in Unix milliseconds:

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
    TargetPort = relayPort,
    IssueUnixTime = now,
    ExpirationUnixTime = now + 30_000
});
var token = EncryptionUtility.Encrypt(json, connectionKey);
```

The encryption is AES-256-CBC with an HMAC-SHA256 integrity check, written as three base64 parts: `iv:ciphertext:mac`. `crypto_utils.ts` in lro_server produces the same format, so its tokens work without changes.

The relay accepts a token only if all of these hold:

- It decrypts with the relay's `ConnectionKey`, and the integrity check passes.
- `TargetPort` matches the relay's port.
- It hasn't expired.
- No one else has used it yet. A token stays reserved until it expires, even after its player disconnects, so a reconnecting player needs a new token.

When a check fails, the relay rejects the connection and blocks the player's IP address for `blacklistDurationMs`. Everyone else behind that address, such as players on the same home network, is blocked too.

## Writing a client

Clients connect with Keel's `ReliableUdpClient`. The first byte of every message is its type. Values marked *packed* are written with `NetWriter.WritePackedUInt32` and read with `NetReader.ReadPackedUInt32`.

Messages from the client:

| Type | Byte | Contents |
|---|---|---|
| `OnAuth` | `0` | Nothing else. Send it once, right after `Connected` fires. The relay ignores a player until it arrives. |
| `OnPacket` | `1` | Op code (packed), receiver count (packed), each receiver's UID (packed), then the payload (`WriteBytesAndSize`). |

A receiver count of `0` sends the packet to everyone except the sender. Otherwise only the listed UIDs get it. The relay forwards each packet with the same delivery method the client used.

Messages from the relay:

| Type | Byte | Contents |
|---|---|---|
| `OnJoined` | `0` | The new player's UID (packed), the player count (packed), then every UID on the relay, including the new one (packed). Sent to everyone, including the new player. |
| `OnPacket` | `1` | Sender's UID (packed), op code (packed), payload (`ReadBytesAndSize`). |
| `OnDisconnect` | `2` | The UID of the player who left (packed). |

The relay never reads the op code or payload, so their meaning is up to you. A client can spot its own `OnJoined` by comparing the UID with `client.ConnectionUid`.

The relay blocks the sender's IP address if a message is malformed, has an unknown type, or is an `OnPacket` sent before `OnAuth`.

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

// Join the relay as soon as the UDP connection is up.
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
            var playerCount = reader.ReadPackedUInt32();
            Console.WriteLine($"Player {joinedUid} joined, {playerCount} players here");
            break;

        case 1: // OnPacket
            var senderUid = reader.ReadPackedUInt32();
            var opCode = reader.ReadPackedUInt32();
            var payload = reader.ReadBytesAndSize();
            Console.WriteLine($"Player {senderUid} sent op {opCode}: {Encoding.UTF8.GetString(payload)}");
            break;

        case 2: // OnDisconnect
            Console.WriteLine($"Player {reader.ReadPackedUInt32()} left");
            break;
    }
};

client.Connect(new IPEndPoint(IPAddress.Parse(relayHost), relayPort), connectionData: token);

while (true)
{
    client.Update();
    Thread.Sleep(10);
}
```

Once the client has joined, this sends "hello" with op code `7` to everyone else on the relay:

```csharp
var writer = new NetWriter();
writer.WriteByte(1);          // OnPacket
writer.WritePackedUInt32(7);  // op code
writer.WritePackedUInt32(0);  // no receivers listed: everyone except me
writer.WriteBytesAndSize(Encoding.UTF8.GetBytes("hello"));

client.Send(writer.AsArraySegment(), UdpProtocol.DeliveryMethod.ReliableOrdered);
```

If the relay has a `UdpEncryptionKey`, register the same key on the client right after `Connect()`, because the client creates its socket there:

```csharp
using Keel.Networking.Udp.LowLevel.DataTransferLayers;

client.Connect(relayEndPoint, connectionData: token);
client.RegisterDataTransferLayer(new DataTransferXorObfuscationLayer(udpEncryptionKey));
```

## Tests

```bash
dotnet test tests/rt_server_tests.csproj
```

The tests cover parsing of relay settings and token encryption.

To format the code, run `sh dotnet_format.sh`.

## Project layout

```
runtime/                       the relay server (rt_server.csproj)
  Entry.cs                     startup, launch arguments, HTTP routes, shutdown
  RelayServer.cs               one relay port: token checks, routing, blocking
  HttpServer.cs                HTTP listener
  HttpRequestHandler.cs        HTTP API endpoints
  HttpWebhookSender.cs         webhooks, with retry
  IncomingDataAmountTracker.cs per-player rate limit
tests/                         xUnit tests
build.sh                       tests, builds and packages into build/bin/
dotnet_format.sh               runs dotnet format on every project
```
