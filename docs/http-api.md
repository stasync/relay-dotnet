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

**Paths.** Paths are case-insensitive, and a trailing slash is optional: `/api/rooms` and `/api/rooms/` are the same.

**Methods.** Each path accepts only the methods listed for it. Any other method gets 405 with an `Allow` header naming the right ones.

**Request bodies.** Only `POST /api/rooms` takes a body: JSON, sent with a `Content-Length` (or chunked). Most clients, including `curl --data` and `fetch`, set it for you. A `POST` without a body length is answered with 411 and ignored.

**No authentication.** Anyone who can reach the API can open, inspect and close rooms. Keep it on a local address such as `127.0.0.1`, or behind a firewall.

**Responses.** Every response is JSON. A successful one always contains:

| Field | Description |
|---|---|
| `status` | Always `"success"`. |
| `timestamp` | When the response was made, in UTC (ISO 8601). |
| `method` | The HTTP method of the request. |

**Status codes.**

| Code | When |
|---|---|
| 200 OK | The request succeeded. |
| 201 Created | A room was opened. The `Location` header points at it. |
| 400 Bad Request | A port, client UID or room settings value is invalid. |
| 404 Not Found | No such room, client or path. |
| 405 Method Not Allowed | The path doesn't accept that method. `Allow` lists the ones it does. |
| 409 Conflict | The requested room port is already in use. |
| 411 Length Required | A `POST` without a body length. This one comes from the HTTP listener itself, with an HTML body. |
| 500 Internal Server Error | Anything unexpected. |

**Errors.** Every error except 411 uses this body:

```json
{
  "code": 404,
  "error": "Unable to find room with port 9000.",
  "timestamp": "2026-10-12T16:13:07.603054Z"
}
```

## Endpoints

| Method | Path | Description |
|---|---|---|
| `GET` | [`/api/status`](#get-apistatus) | Health check. |
| `GET` | [`/api/node`](#get-apinode) | Reports the node's memory use and running threads. |
| `GET` | [`/api/rooms`](#get-apirooms) | Lists rooms and their clients. |
| `POST` | [`/api/rooms`](#post-apirooms) | Opens a room. |
| `DELETE` | [`/api/rooms`](#delete-apirooms) | Closes every room. |
| `GET` | [`/api/rooms/{port}`](#get-apiroomsport) | Returns one room. |
| `DELETE` | [`/api/rooms/{port}`](#delete-apiroomsport) | Closes one room. |
| `GET` | [`/api/rooms/{port}/clients`](#get-apiroomsportclients) | Lists a room's clients, or finds one by token. |
| `GET` | [`/api/rooms/{port}/clients/{uid}`](#get-apiroomsportclientsuid) | Returns one client. |
| `DELETE` | [`/api/rooms/{port}/clients/{uid}`](#delete-apiroomsportclientsuid) | Disconnects a client. |

In every path, `{port}` is a room's port and `{uid}` is a client's UID. A value that isn't a valid number gets 400; a room or client that doesn't exist gets 404.

### `GET /api/status`

Health check with a small response, suitable for frequent polling.

```json
{
  "status": "success",
  "pid": 40634,
  "timestamp": "2026-10-12T16:13:03.241445Z",
  "method": "GET"
}
```

`pid` is the node's process ID.

### `GET /api/node`

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
    { "type": "UdpRelay.Room", "name": "Lobby" }
  ]
}
```

| Field | Description |
|---|---|
| `managedMemory` | Memory used by the .NET heap, in bytes. |
| `totalMemory` | Memory used by the whole process (working set), in bytes. |
| `workersCount` | Number of running worker threads. |
| `workers` | Each worker's type and name. Rooms have the type `UdpRelay.Room`; the log writer is also listed. |

### `GET /api/rooms`

Lists every room and the clients in it.

```json
{
  "status": "success",
  "timestamp": "2026-10-12T16:13:07.504486Z",
  "method": "GET",
  "roomsCount": 2,
  "rooms": [
    {
      "name": "Lobby",
      "port": 53247,
      "clientsCount": 1,
      "clients": [1],
      "bytesSent": 355,
      "bytesReceived": 790
    },
    {
      "name": "Room_63829",
      "port": 63829,
      "clientsCount": 0,
      "clients": [],
      "bytesSent": 0,
      "bytesReceived": 0
    }
  ]
}
```

| Field | Description |
|---|---|
| `name` | The room's name. |
| `port` | The room's UDP port. |
| `clientsCount` | Number of clients in the room. |
| `clients` | UIDs of the clients in the room. |
| `bytesSent`, `bytesReceived` | Traffic since the room opened. Counted only when the room has `CaptureDataTransferAmount` on; otherwise `0`. |

### `POST /api/rooms`

Opens a new room on its own thread. The body holds the [room settings](#room-settings) as JSON. An empty body opens a room with the defaults: a free port and no `ConnectionKey`, so anyone can join it.

```bash
curl -X POST "http://127.0.0.1:8080/api/rooms" \
  -H "Content-Type: application/json" \
  --data '{"Name": "Lobby", "ConnectionKey": "6f9b2c1e-4d7a-4e8b-9a3f-1c5d7e9b2a40"}'
```

The response is 201 Created, with a `Location` header such as `http://127.0.0.1:8080/api/rooms/53247/`:

```json
{
  "status": "success",
  "timestamp": "2026-10-12T16:13:03.333247Z",
  "method": "POST",
  "room": {
    "name": "Lobby",
    "port": 53247
  }
}
```

| Field | Description |
|---|---|
| `room.name` | The room's name, used in logs and in `GET /api/rooms`. |
| `room.port` | The UDP port the room listens on. Use it as `{port}` in the other endpoints. |

Node-wide launch arguments also apply to every room: `maxClientsPerRoom`, `protocolKey`, `roomUpdateIntervalMs`, `blacklistDurationMs` and `logConnectionRejections`. See [Launch arguments](../README.md#launch-arguments).

Fails with:

- 400 if the body isn't valid JSON, or `TargetPort` is outside 0–65535.
- 409 if `TargetPort` is already in use.

### `DELETE /api/rooms`

Closes every room. Their clients are dropped without notice, and the node exits once the last room has closed.

```json
{
  "status": "success",
  "timestamp": "2026-10-12T16:13:10.228228Z",
  "method": "DELETE",
  "closedRooms": [
    { "name": "Room_63829", "port": 63829 },
    { "name": "Room_55086", "port": 55086 }
  ]
}
```

### `GET /api/rooms/{port}`

Returns one room, with the same fields as an entry in [`GET /api/rooms`](#get-apirooms).

```json
{
  "status": "success",
  "timestamp": "2026-10-12T16:13:07.518057Z",
  "method": "GET",
  "room": {
    "name": "Lobby",
    "port": 53247,
    "clientsCount": 1,
    "clients": [1],
    "bytesSent": 355,
    "bytesReceived": 790
  }
}
```

### `DELETE /api/rooms/{port}`

Closes one room.

```json
{
  "status": "success",
  "timestamp": "2026-10-12T16:13:09.190765Z",
  "method": "DELETE",
  "room": { "name": "Lobby", "port": 53247 }
}
```

Closing a room drops its clients without notice. They get no `OnDisconnect`, and no `ClientLeft` webhooks are sent for them. When the node's last room closes, the node exits.

### `GET /api/rooms/{port}/clients`

Lists the clients in a room, with the token each one joined with.

| Query parameter | Required | Description |
|---|---|---|
| `token` | No | Returns only the client that joined with this token. URL-encode it: tokens contain `+`, `/` and `:`. |

```json
{
  "status": "success",
  "timestamp": "2026-10-12T16:13:07.552422Z",
  "method": "GET",
  "clientsCount": 1,
  "clients": [
    { "uid": 1, "token": "<the token the client joined with>" }
  ]
}
```

When no client matches `token`, the list is empty; the status is still 200.

### `GET /api/rooms/{port}/clients/{uid}`

Returns one client. Returns 404 if no client with that UID is in the room, so it also answers "is this client still here?".

```json
{
  "status": "success",
  "timestamp": "2026-10-12T16:13:07.57259Z",
  "method": "GET",
  "client": { "uid": 1, "token": "<the token the client joined with>" }
}
```

To check that a client joined with a particular token, compare `client.token` with it.

### `DELETE /api/rooms/{port}/clients/{uid}`

Disconnects a client. The response only confirms the request: the room disconnects the client on its next update, then sends `OnDisconnect` to the other clients and calls the `ClientLeft` webhook.

```json
{
  "status": "success",
  "timestamp": "2026-10-12T16:13:07.640555Z",
  "method": "DELETE"
}
```

Returns 404 if no client with that UID is in the room.

## Room settings

`POST /api/rooms` takes these settings as its JSON body. Every field is optional, and field names are case-sensitive.

```json
{
  "TargetPort": 0,
  "Name": "Lobby",
  "ConnectionKey": "6f9b2c1e-4d7a-4e8b-9a3f-1c5d7e9b2a40",
  "UdpObfuscationKey": "",
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
| `UdpObfuscationKey` | empty | If set, all UDP traffic is XOR-scrambled with this key, and clients must do the same (see [Writing a client](../README.md#writing-a-client)). This hides traffic from casual inspection but is not real encryption. |
| `CaptureDataTransferAmount` | `false` | Counts traffic, reported as `bytesSent` and `bytesReceived` by `GET /api/rooms`. |
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
  "roomPort": 53247,
  "clientUid": 1,
  "token": "<the token the client joined with>",
  "endpoint": "203.0.113.5:65067",
  "timestamp": "2026-10-12T16:13:03.802665Z"
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

Sent when a client leaves: it disconnects, its connection times out, or it's dropped through [`DELETE /api/rooms/{port}/clients/{uid}`](#delete-apiroomsportclientsuid). It isn't sent when the room itself is closed.

```json
{
  "roomPort": 53247,
  "clientUid": 1,
  "timestamp": "2026-10-12T16:13:07.672649Z"
}
```

### Delivery

- **Retries.** If a URL can't be reached (a network error, or no answer within 2 seconds), the node retries every 4 seconds until it gets through. Pending retries are kept in memory, so they're lost if the node exits.
- **Responses.** Any HTTP response, including an error status, counts as delivered and isn't retried.
- **Order.** Each webhook is sent on its own, so a quick join and leave can arrive out of order. Use `timestamp` to order them.
- **Invalid URLs.** A URL that isn't an `http` or `https` address ending in `/` is logged as an error and never called.
