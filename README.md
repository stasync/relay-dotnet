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

The script runs the tests and builds in release mode. The result goes to `artifacts/bin/relay/release/`, which git ignores. If the tests or the build fail, the script stops with a non-zero exit code, so CI can call it as is.

For a quick debug build, use `dotnet build runtime/relay.csproj`.

### Keel

The Keel DLLs are not checked in. On the first build, `runtime/relay.csproj` downloads `keel-<version>-net10.0.zip` from [Keel's GitHub releases](https://github.com/stasync/keel-dotnet/releases) and unpacks it into `plugins/keel/<version>/net10.0/`. Later builds reuse that copy. The `plugins/` folder is ignored by git.

The version comes from the `KeelVersion` property in `runtime/relay.csproj`. The default, `latest-build`, is a pre-release that Keel rebuilds after every change to its `main` branch.

- **Getting a newer `latest-build`:** the download only happens when the DLLs are missing. Delete `plugins/keel/` and build again.
- **Pinning a version:** set `KeelVersion` to a release tag. Then update the three `HintPath`s in `tests/relay_tests.csproj`, which point at the `latest-build` folder.

## Running

The quickest way to run the relay is `startup.sh`. It builds the relay with `dotnet run` and runs it in the foreground, with the HTTP API on `http://127.0.0.1:8080/`. Stop it with Ctrl+C.

```bash
sh startup.sh
```

Set `HTTP_LISTENER_URL` to use a different address. Any arguments are passed on to the relay:

```bash
HTTP_LISTENER_URL=http://127.0.0.1:9000/ sh startup.sh -maxConnectionsPerPort 32
```

When started this way, the relay's log files and `.cmdparam` files live in `runtime/bin/Release/net10.0/`.

To run the output of `build.sh` instead:

```bash
dotnet artifacts/bin/relay/release/relay.dll -httpListenerUrl http://127.0.0.1:8080/
```

Either way, the relay starts with the HTTP API and no relays open. Open relays through the API, as shown under [Opening a relay](#opening-a-relay).

The HTTP address must name a specific host, such as `127.0.0.1` or the machine's IP. `0.0.0.0` doesn't bind, and wildcards like `*` aren't accepted.

For a quick local test without tokens, also open a relay at startup:

```bash
sh startup.sh -relayPort 7777
```

### Launch arguments

Arguments use a single dash: `-name value`. Each one can also be set with a file named `<name>.cmdparam` next to `relay.dll`, containing just the value.

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
| `logOutputRelativePath` | `logs` | Folder for log files, relative to `relay.dll`. Each run writes `app_<pid>.log`. |

### Shutting down

The process exits when its last relay closes. That happens after `/api/relay/shutdown/` closes the final relay, or on Ctrl+C or `SIGTERM`, which close every relay first.

## HTTP API

The backend controls the relay through these endpoints. Every endpoint answers `GET` requests and returns JSON. **Paths must end with a slash:** `/api/relay/status/` works, but `/api/relay/status` returns 404. The API has no authentication, so keep it on a local address such as `127.0.0.1`.

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

A failed request returns HTTP 500, or 404 for an unknown path, with the reason in an `error` field.

For each endpoint's parameters, responses and errors, see the [HTTP API reference](docs/http-api.md).

### Opening a relay

Pass the relay settings as JSON in the `config` query parameter:

```bash
curl -G "http://127.0.0.1:8080/api/relay/new/" --data-urlencode 'config={
  "TargetPort": 0,
  "ConnectionKey": "6f9b2c1e-4d7a-4e8b-9a3f-1c5d7e9b2a40",
  "Webhooks": {
    "UserConnected": "http://127.0.0.1:3000/webhooks/relay_user_connected/",
    "UserDisconnected": "http://127.0.0.1:3000/webhooks/relay_user_disconnected/"
  }
}'
```

```json
{"status":"success","timestamp":"2026-10-10T10:56:28.239751Z","method":"GET","relayPort":49791,"workerName":"RelayThread_49791"}
```

`TargetPort` `0` picks a free port. Leave out `ConnectionKey` only for testing: without it, anyone can join. Every setting is listed under [Relay settings](docs/http-api.md#relay-settings).

## Webhooks

Each relay can call two URLs on the backend, set in its `Webhooks` settings:

- `UserConnected` receives the player's UID, the token they joined with, and their address. It fires when the player sends `OnAuth`, not when the UDP connection opens.
- `UserDisconnected` receives the player's UID when they leave: they disconnect, time out, or are dropped through the API. It isn't sent when the relay itself is closed.

**Webhook URLs must end with `/`.** Without it, the relay logs an error and never calls the URL. A URL that can't be reached is retried every 4 seconds. The payloads and delivery rules are in the [HTTP API reference](docs/http-api.md#webhooks).

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
dotnet test tests/relay_tests.csproj
```

The tests cover parsing of relay settings and token encryption.

To format the code, run `sh dotnet_format.sh`.

## Project layout

```
runtime/                       the relay server (relay.csproj)
  Entry.cs                     startup, launch arguments, HTTP routes, shutdown
  RelayServer.cs               one relay port: token checks, routing, blocking
  HttpServer.cs                HTTP listener
  HttpRequestHandler.cs        HTTP API endpoints
  HttpWebhookSender.cs         webhooks, with retry
  IncomingDataAmountTracker.cs per-player rate limit
tests/                         xUnit tests
docs/http-api.md               HTTP API and webhook reference
build.sh                       tests and builds into artifacts/
startup.sh                     builds and runs the relay in the foreground
dotnet_format.sh               runs dotnet format on every project
```
