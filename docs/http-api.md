# HTTP API reference

A backend controls a relay node over HTTP: it opens and closes rooms, looks up the clients in them, and drops clients. Each room can also call the backend back through webhooks when a client joins or leaves.

This page lists every endpoint, the settings a room accepts, and the webhooks it sends. For building and running a node, connection tokens, and the client protocol, see the [README](../README.md).

## Concepts

| Term | What it is |
|---|---|
| **Node** | One relay process. It serves this HTTP API and runs rooms. |
| **Room** | One UDP port with its own thread. It forwards messages between the clients that joined it. Rooms are identified by their port. |
| **Client** | One connection in a room, identified by a UID that's unique within that room. A client counts only after it has joined by sending `OnAuth`; clients that have connected over UDP but not yet joined don't appear in any response. |

## Conventions

**Base URL.** The API listens on the address given by the node's `httpListenerUrl` launch argument, such as `http://127.0.0.1:8080/`. Without that argument, the API and webhooks are off.

**Requests.** Send `GET` requests with parameters in the query string. Paths must end with a slash: `/api/room/status/` works, but `/api/room/status` returns 404.

**No authentication.** Anyone who can reach the API can open, inspect and close rooms. Keep it on a local address such as `127.0.0.1`, or behind a firewall.

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
  "error": "/api/room/status/: Internal server error: Unable to find room with port 9000.",
  "timestamp": "2026-10-12T15:54:47.784727Z"
}
```

## Endpoints

| Endpoint | Description |
|---|---|
| [`/api/status/`](#apistatus) | Health check. |
| [`/api/node/info/`](#apinodeinfo) | Reports the node's memory use and running threads. |
| [`/api/room/new/`](#apiroomnew) | Opens a room. |
| [`/api/room/status/`](#apiroomstatus) | Lists rooms and their clients. |
| [`/api/room/has_client/`](#apiroomhas_client) | Checks whether a client is in a room. |
| [`/api/room/get_client/`](#apiroomget_client) | Finds a client by UID or token. |
| [`/api/room/drop_client/`](#apiroomdrop_client) | Disconnects a client. |
| [`/api/room/close/`](#apiroomclose) | Closes one room or all of them. |

### `/api/status/`

Health check with a small response, suitable for frequent polling.

```json
{
  "status": "success",
  "pid": 37623,
  "timestamp": "2026-10-12T15:54:43.512094Z",
  "method": "GET"
}
```

`pid` is the node's process ID.

### `/api/node/info/`

Reports the node's memory use and its running threads.

```json
{
  "status": "success",
  "timestamp": "2026-10-12T15:54:49.380498Z",
  "method": "GET",
  "managedMemory": 1671768,
  "totalMemory": 74080256,
  "workersCount": 3,
  "workers": [
    { "type": "Keel.Utils.Debug.LogWorker", "name": "Keel.Utils.Debug.LogWorker" },
    { "type": "UdpRelay.Room", "name": "Room_53832" },
    { "type": "UdpRelay.Room", "name": "TestRoom" }
  ]
}
```

| Field | Description |
|---|---|
| `managedMemory` | Memory used by the .NET heap, in bytes. |
| `totalMemory` | Memory used by the whole process (working set), in bytes. |
| `workersCount` | Number of running worker threads. |
| `workers` | Each worker's type and name. Rooms have the type `UdpRelay.Room`; the log writer is also listed. |

### `/api/room/new/`

Opens a new room on its own thread.

| Parameter | Required | Description |
|---|---|---|
| `config` | No | The [room settings](#room-settings) as JSON, URL-encoded. |

```bash
curl -G "http://127.0.0.1:8080/api/room/new/" --data-urlencode 'config={"TargetPort": 0, "ConnectionKey": "6f9b2c1e-4d7a-4e8b-9a3f-1c5d7e9b2a40"}'
```

```json
{
  "status": "success",
  "timestamp": "2026-10-12T15:54:43.576478Z",
  "method": "GET",
  "room": {
    "name": "Room_58792",
    "port": 58792
  }
}
```

| Field | Description |
|---|---|
| `room.name` | The room's name, used in logs and in `/api/room/status/`. |
| `room.port` | The UDP port the room listens on. Use it to address the room in every other endpoint. |

Without `config`, the room opens on a free port with no `ConnectionKey`, so anyone can join it.

Node-wide launch arguments also apply to every room: `maxClientsPerRoom`, `protocolKey`, `roomUpdateIntervalMs`, `blacklistDurationMs` and `logConnectionRejections`. See [Launch arguments](../README.md#launch-arguments).

Fails when:

- `config` isn't valid JSON.
- `TargetPort` is outside 0–65535.
- `TargetPort` is already in use.

### `/api/room/status/`

Lists rooms and the clients in them.

| Parameter | Required | Description |
|---|---|---|
| `port` | No | Port of one room. Leave it out to list every room. |

Without `port`:

```json
{
  "status": "success",
  "timestamp": "2026-10-12T15:54:47.698547Z",
  "method": "GET",
  "roomsCount": 2,
  "rooms": [
    {
      "name": "Room_53832",
      "port": 53832,
      "clientsCount": 0,
      "clients": [],
      "bytesSent": 0,
      "bytesReceived": 0
    },
    {
      "name": "TestRoom",
      "port": 58792,
      "clientsCount": 1,
      "clients": [1],
      "bytesSent": 355,
      "bytesReceived": 790
    }
  ]
}
```

With `port`, the same fields come back as a single `room` object instead of the `rooms` list.

| Field | Description |
|---|---|
| `name` | The room's name. |
| `port` | The room's UDP port. |
| `clientsCount` | Number of clients in the room. |
| `clients` | UIDs of the clients in the room. |
| `bytesSent`, `bytesReceived` | Traffic since the room opened. Counted only when the room has `CaptureDataTransferAmount` on; otherwise `0`. |

Fails when `port` is given but no room uses it.

### `/api/room/has_client/`

Checks whether a client is in a room.

| Parameter | Required | Description |
|---|---|---|
| `port` | Yes | Port of the room. |
| `uid` | Yes | UID of the client. |

```json
{
  "status": "success",
  "timestamp": "2026-10-12T15:54:47.716751Z",
  "method": "GET",
  "hasClient": true
}
```

Fails when:

- `port` or `uid` is missing, or `port` is `0`.
- No room uses that port.

### `/api/room/get_client/`

Finds a client by UID, by the token it joined with, or both.

| Parameter | Required | Description |
|---|---|---|
| `port` | Yes | Port of the room. |
| `uid` | One of the two | UID of the client. |
| `token` | One of the two | The token the client joined with. URL-encode it. |

- With only `uid`, it succeeds if that client is in the room.
- With only `token`, it finds the client that joined with that token.
- With both, the client must be in the room and must have joined with that token.

```json
{
  "status": "success",
  "timestamp": "2026-10-12T15:54:47.737339Z",
  "method": "GET",
  "clientUid": 1
}
```

Fails when:

- `port` is missing or `0`.
- Neither `uid` nor `token` is given.
- No room uses that port.
- No client matches.

### `/api/room/drop_client/`

Disconnects a client from a room.

| Parameter | Required | Description |
|---|---|---|
| `port` | Yes | Port of the room. |
| `uid` | Yes | UID of the client. |

The response only confirms the request. The room disconnects the client on its next update, then sends `OnDisconnect` to the other clients and calls the `ClientLeft` webhook.

```json
{
  "status": "success",
  "timestamp": "2026-10-12T15:54:47.803263Z",
  "method": "GET"
}
```

Fails when:

- `port` or `uid` is missing, or `port` is `0`.
- No room uses that port.
- No client with that UID is in the room.

### `/api/room/close/`

Closes one room, or all of them.

| Parameter | Required | Description |
|---|---|---|
| `port` | No | Port of the room to close. Leave it out, or pass `0`, to close every room. |

```json
{
  "status": "success",
  "timestamp": "2026-10-12T15:54:49.439441Z",
  "method": "GET",
  "closedRooms": [
    { "name": "Room_53832", "port": 53832 },
    { "name": "TestRoom", "port": 58792 }
  ]
}
```

A `port` that no room uses isn't an error: the request succeeds with an empty `closedRooms` list.

Closing a room drops its clients without notice. They get no `OnDisconnect`, and no `ClientLeft` webhooks are sent for them. When the node's last room closes, the node exits.

## Room settings

`/api/room/new/` takes these settings in its `config` parameter. Every field is optional, and field names are case-sensitive.

```json
{
  "TargetPort": 0,
  "Name": "Lobby",
  "ConnectionKey": "6f9b2c1e-4d7a-4e8b-9a3f-1c5d7e9b2a40",
  "UdpEncryptionKey": "",
  "CaptureDataTransferAmount": true,
  "Webhooks": {
    "ClientJoined": "http://127.0.0.1:3000/webhooks/client_joined/",
    "ClientLeft": "http://127.0.0.1:3000/webhooks/client_left/"
  },
  "ClientRateLimiter": {
    "KBitPerSecLimit": 512
  }
}
```

| Field | Default | Description |
|---|---|---|
| `TargetPort` | `0` | UDP port to listen on. `0` picks a free port. |
| `Name` | `Room_<port>` | The room's name, shown in logs and status. |
| `ConnectionKey` | empty | Secret used to check [connection tokens](../README.md#connection-tokens). If it's empty, anyone can join, so use that only for testing. |
| `UdpEncryptionKey` | empty | If set, all UDP traffic is XOR-scrambled with this key, and clients must do the same (see [Writing a client](../README.md#writing-a-client)). This hides traffic from casual inspection but is not real encryption. |
| `CaptureDataTransferAmount` | `false` | Counts traffic, reported as `bytesSent` and `bytesReceived` by `/api/room/status/`. |
| `Webhooks.ClientJoined` | empty | URL to call when a client joins. It must end with `/`. Empty means no webhook. |
| `Webhooks.ClientLeft` | empty | URL to call when a client leaves. It must end with `/`. Empty means no webhook. |
| `ClientRateLimiter.KBitPerSecLimit` | `0` | Maximum incoming traffic per client, in kbit/s. A client that goes over it has its IP address blocked for `blacklistDurationMs`. `0` means no limit. |

## Webhooks

Each room sends webhooks to the URLs in its `Webhooks` settings. They're POST requests with a JSON body, and they're only sent while the HTTP API is on.

**Webhook URLs must end with `/`.** A URL without a trailing slash, such as `http://127.0.0.1:3000/webhooks/client_joined`, is rejected: the node logs an error each time and never calls it. Use `http://127.0.0.1:3000/webhooks/client_joined/` instead.

### `ClientJoined`

Sent when a client joins, which is when it sends `OnAuth`, not when its UDP connection opens.

```json
{
  "roomPort": 59446,
  "clientUid": 1,
  "token": "<the token the client joined with>",
  "endpoint": "203.0.113.5:62729",
  "timestamp": "2026-10-12T15:57:10.219155Z"
}
```

| Field | Description |
|---|---|
| `roomPort` | Port of the room the client joined. |
| `clientUid` | The client's UID in that room. |
| `token` | The exact token the client connected with. Use it to match the client to the token you issued. Read it with a JSON parser: characters such as `+` arrive escaped (`+`). |
| `endpoint` | The client's IP address and port. |
| `timestamp` | When the client joined, in UTC (ISO 8601). |

### `ClientLeft`

Sent when a client leaves: it disconnects, its connection times out, or it's dropped through [`/api/room/drop_client/`](#apiroomdrop_client). It isn't sent when the room itself is closed.

```json
{
  "roomPort": 59446,
  "clientUid": 1,
  "timestamp": "2026-10-12T15:57:13.982483Z"
}
```

### Delivery

- **Retries.** If a URL can't be reached (a network error, or no answer within 2 seconds), the node retries every 4 seconds until it gets through. Pending retries are kept in memory, so they're lost if the node exits.
- **Responses.** Any HTTP response, including an error status, counts as delivered and isn't retried.
- **Order.** Each webhook is sent on its own, so a quick join and leave can arrive out of order. Use `timestamp` to order them.
- **Invalid URLs.** A URL that isn't an `http` or `https` address ending in `/` is logged as an error and never called.
