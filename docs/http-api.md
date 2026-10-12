# HTTP API reference

The relay process is controlled over HTTP: a backend uses it to open and close relays, look up connected players, and drop them. Each relay can also call the backend back through webhooks when a player joins or leaves.

This page lists every endpoint, the settings a relay accepts, and the webhooks it sends. For building and running the relay, connection tokens, and the client protocol, see the [README](../README.md).

## Conventions

**Base URL.** The API listens on the address given by the `httpListenerUrl` launch argument, such as `http://127.0.0.1:8080/`. Without that argument, the API and webhooks are off.

**Requests.** Send `GET` requests with parameters in the query string. Paths must end with a slash: `/api/relay/status/` works, but `/api/relay/status` returns 404.

**No authentication.** Anyone who can reach the API can open, inspect and close relays. Keep it on a local address such as `127.0.0.1`, or behind a firewall.

**Responses.** Every response is JSON. A successful one has HTTP status 200 and always contains:

| Field | Description |
|---|---|
| `status` | Always `"success"`. |
| `timestamp` | When the response was made, in UTC (ISO 8601). |
| `method` | The HTTP method of the request. |

**Errors.** An unknown path returns 404. Any other failure, including a missing or invalid parameter, returns 500. Both use this body:

```json
{
  "code": 500,
  "error": "/api/relay/status/: Internal server error: Unable to find relay worker with port 9000.",
  "timestamp": "2026-10-10T10:56:57.399358Z"
}
```

## Endpoints

| Endpoint | Description |
|---|---|
| [`/api/status/`](#apistatus) | Health check. |
| [`/api/relay/new/`](#apirelaynew) | Opens a relay. |
| [`/api/relay/status/`](#apirelaystatus) | Lists relays and their players. |
| [`/api/relay/has_connection/`](#apirelayhas_connection) | Checks whether a player is on a relay. |
| [`/api/relay/get_connection/`](#apirelayget_connection) | Finds a player by UID or token. |
| [`/api/relay/drop_connection/`](#apirelaydrop_connection) | Disconnects a player. |
| [`/api/relay/shutdown/`](#apirelayshutdown) | Closes one relay or all of them. |
| [`/api/system/info/`](#apisysteminfo) | Reports memory use and running threads. |

A *player* below means a client that has joined a relay by sending `OnAuth`. Clients that have connected over UDP but not yet joined don't appear in any response.

### `/api/status/`

Health check with a small response, suitable for frequent polling.

```json
{
  "status": "success",
  "pid": 37623,
  "timestamp": "2026-10-10T10:55:52.340485Z",
  "method": "GET"
}
```

`pid` is the process ID of the relay process.

### `/api/relay/new/`

Opens a new relay on its own thread.

| Parameter | Required | Description |
|---|---|---|
| `config` | No | The [relay settings](#relay-settings) as JSON, URL-encoded. |

```bash
curl -G "http://127.0.0.1:8080/api/relay/new/" --data-urlencode 'config={"TargetPort": 0, "ConnectionKey": "6f9b2c1e-4d7a-4e8b-9a3f-1c5d7e9b2a40"}'
```

```json
{
  "status": "success",
  "timestamp": "2026-10-10T10:56:28.239751Z",
  "method": "GET",
  "relayPort": 49791,
  "workerName": "RelayThread_49791"
}
```

| Field | Description |
|---|---|
| `relayPort` | The UDP port the relay listens on. |
| `workerName` | The relay's thread name, used in logs and in `/api/relay/status/`. |

Without `config`, the relay opens on a free port with no `ConnectionKey`, so anyone can join it.

Process-wide launch arguments also apply to every relay: `maxConnectionsPerPort`, `relayProtocolKey`, `relayMillisecondsTimeStep`, `blacklistDurationMs` and `logConnectionRejections`. See [Launch arguments](../README.md#launch-arguments).

Fails when:

- `config` isn't valid JSON.
- `TargetPort` is outside 0–65535.
- `TargetPort` is already in use.

### `/api/relay/status/`

Lists relays and the players on them.

| Parameter | Required | Description |
|---|---|---|
| `port` | No | Port of one relay. Leave it out to list every relay. |

Without `port`:

```json
{
  "status": "success",
  "timestamp": "2026-10-10T10:55:52.513354Z",
  "method": "GET",
  "relayWorkersCount": 1,
  "relayWorkers": [
    {
      "name": "RelayThread_49791",
      "port": 49791,
      "connectionsCount": 2,
      "connections": [1, 2],
      "bytesSent": 0,
      "bytesReceived": 0
    }
  ]
}
```

With `port`, the same relay fields come back as a single `relayWorker` object instead of the `relayWorkers` list.

| Field | Description |
|---|---|
| `name` | The relay's thread name. |
| `port` | The relay's UDP port. |
| `connectionsCount` | Number of players on the relay. |
| `connections` | UIDs of the players on the relay. |
| `bytesSent`, `bytesReceived` | Traffic since the relay opened. Counted only when the relay has `CaptureDataTransferAmount` on; otherwise `0`. |

Fails when `port` is given but no relay uses it.

### `/api/relay/has_connection/`

Checks whether a player is on a relay.

| Parameter | Required | Description |
|---|---|---|
| `port` | Yes | Port of the relay. |
| `uid` | Yes | UID of the player. |

```json
{
  "status": "success",
  "timestamp": "2026-10-10T10:56:30.112094Z",
  "method": "GET",
  "hasConnection": true
}
```

Fails when:

- `port` or `uid` is missing, or `port` is `0`.
- No relay uses that port.

### `/api/relay/get_connection/`

Finds a player by UID, by the token they joined with, or both.

| Parameter | Required | Description |
|---|---|---|
| `port` | Yes | Port of the relay. |
| `uid` | One of the two | UID of the player. |
| `token` | One of the two | The token the player joined with. |

- With only `uid`, it succeeds if that player is on the relay.
- With only `token`, it finds the player who joined with that token.
- With both, the player must be on the relay and must have joined with that token.

```json
{
  "status": "success",
  "timestamp": "2026-10-10T10:56:30.214552Z",
  "method": "GET",
  "connectionUid": 1
}
```

Fails when:

- `port` is missing or `0`.
- Neither `uid` nor `token` is given.
- No relay uses that port.
- No player matches.

### `/api/relay/drop_connection/`

Disconnects a player.

| Parameter | Required | Description |
|---|---|---|
| `port` | Yes | Port of the relay. |
| `uid` | Yes | UID of the player. |

The response only confirms the request. The relay disconnects the player on its next update, then sends `OnDisconnect` to the other players and calls the `UserDisconnected` webhook.

```json
{
  "status": "success",
  "timestamp": "2026-10-10T10:56:31.003317Z",
  "method": "GET"
}
```

Fails when:

- `port` or `uid` is missing, or `port` is `0`.
- No relay uses that port.
- No player with that UID is on the relay.

### `/api/relay/shutdown/`

Closes one relay, or all of them.

| Parameter | Required | Description |
|---|---|---|
| `port` | No | Port of the relay to close. Leave it out, or pass `0`, to close every relay. |

```json
{
  "status": "success",
  "timestamp": "2026-10-10T10:55:52.561603Z",
  "method": "GET",
  "closedRelays": [
    { "name": "RelayThread_49791", "port": 49791 }
  ]
}
```

A `port` that no relay uses isn't an error: the request succeeds with an empty `closedRelays` list.

Closing a relay drops its players without notice. They get no `OnDisconnect`, and no `UserDisconnected` webhooks are sent for them. When the last relay closes, the process exits.

### `/api/system/info/`

Reports the process's memory use and its running threads.

```json
{
  "status": "success",
  "timestamp": "2026-10-10T10:55:52.540984Z",
  "method": "GET",
  "managedMemory": 886192,
  "totalMemory": 55189504,
  "workersCount": 2,
  "workers": [
    { "type": "Keel.Utils.Debug.LogWorker", "name": "Keel.Utils.Debug.LogWorker" },
    { "type": "UdpRelay.RelayServer", "name": "RelayThread_49791" }
  ]
}
```

| Field | Description |
|---|---|
| `managedMemory` | Memory used by the .NET heap, in bytes. |
| `totalMemory` | Memory used by the whole process (working set), in bytes. |
| `workersCount` | Number of running worker threads. |
| `workers` | Each worker's type and name. Relays have the type `UdpRelay.RelayServer`; the log writer is also listed. |

## Relay settings

`/api/relay/new/` takes these settings in its `config` parameter. Every field is optional, and field names are case-sensitive.

```json
{
  "TargetPort": 0,
  "WorkerName": "RelayThread_49791",
  "ConnectionKey": "6f9b2c1e-4d7a-4e8b-9a3f-1c5d7e9b2a40",
  "UdpEncryptionKey": "",
  "CaptureDataTransferAmount": true,
  "Webhooks": {
    "UserConnected": "http://127.0.0.1:3000/webhooks/relay_user_connected/",
    "UserDisconnected": "http://127.0.0.1:3000/webhooks/relay_user_disconnected/"
  },
  "ClientRateLimiter": {
    "KBitPerSecLimit": 512
  }
}
```

| Field | Default | Description |
|---|---|---|
| `TargetPort` | `0` | UDP port to listen on. `0` picks a free port. |
| `WorkerName` | `RelayThread_<port>` | Thread name shown in logs and status. |
| `ConnectionKey` | empty | Secret used to check [connection tokens](../README.md#connection-tokens). If it's empty, anyone can join, so use that only for testing. |
| `UdpEncryptionKey` | empty | If set, all UDP traffic is XOR-scrambled with this key, and clients must do the same (see [Writing a client](../README.md#writing-a-client)). This hides traffic from casual inspection but is not real encryption. |
| `CaptureDataTransferAmount` | `false` | Counts traffic, reported as `bytesSent` and `bytesReceived` by `/api/relay/status/`. |
| `Webhooks.UserConnected` | empty | URL to call when a player joins. It must end with `/`. Empty means no webhook. |
| `Webhooks.UserDisconnected` | empty | URL to call when a player leaves. It must end with `/`. Empty means no webhook. |
| `ClientRateLimiter.KBitPerSecLimit` | `0` | Maximum incoming traffic per player, in kbit/s. A player who goes over it has their IP address blocked for `blacklistDurationMs`. `0` means no limit. |

## Webhooks

Each relay sends webhooks to the URLs in its `Webhooks` settings. They're POST requests with a JSON body, and they're only sent while the HTTP API is on.

**Webhook URLs must end with `/`.** A URL without a trailing slash, such as `http://127.0.0.1:3000/webhooks/relay_user_connected`, is rejected: the relay logs an error each time and never calls it. Use `http://127.0.0.1:3000/webhooks/relay_user_connected/` instead.

### `UserConnected`

Sent when a player joins, which is when they send `OnAuth`, not when their UDP connection opens.

```json
{
  "relayPort": 49791,
  "connUid": 1,
  "token": "<the token the player joined with>",
  "endpoint": "203.0.113.5:54012",
  "timestamp": "2026-10-10T10:56:28.239751Z"
}
```

| Field | Description |
|---|---|
| `relayPort` | Port of the relay the player joined. |
| `connUid` | The player's UID on that relay. |
| `token` | The exact token the player connected with. Use it to match the player to the token you issued. |
| `endpoint` | The player's IP address and port. |
| `timestamp` | When the player joined, in UTC (ISO 8601). |

### `UserDisconnected`

Sent when a player leaves: they disconnect, their connection times out, or they're dropped through [`/api/relay/drop_connection/`](#apirelaydrop_connection). It isn't sent when the relay itself is closed.

```json
{
  "relayPort": 49791,
  "connUid": 1,
  "timestamp": "2026-10-10T10:56:31.102938Z"
}
```

### Delivery

- **Retries.** If a URL can't be reached (a network error, or no answer within 2 seconds), the relay retries every 4 seconds until it gets through. Pending retries are kept in memory, so they're lost if the process exits.
- **Responses.** Any HTTP response, including an error status, counts as delivered and isn't retried.
- **Order.** Each webhook is sent on its own, so a quick join and leave can arrive out of order. Use `timestamp` to order them.
- **Invalid URLs.** A URL that isn't an `http` or `https` address ending in `/` is logged as an error and never called.
