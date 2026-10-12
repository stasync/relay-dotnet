# Using the HTTP API

This guide walks a backend through a room's whole life: start a node, open a room, let clients in with tokens, follow them through webhooks, and clean up. Each step uses `curl`; [Doing it from C#](#doing-it-from-c) shows the same flow with `HttpClient`.

The [HTTP API reference](http-api.md) lists every endpoint's fields and errors. The [README](../README.md) covers building Relay and the client protocol.

## 1. Start a node

```bash
sh startup.sh
```

This builds Relay and starts a node with its HTTP API on `http://127.0.0.1:8080/`. Check that it answers:

```bash
curl http://127.0.0.1:8080/api/status
```

```json
{"status":"success","pid":40634,"timestamp":"2026-10-12T16:13:03.241445Z","method":"GET"}
```

## 2. Open a room

Give each room its own `ConnectionKey`: a long random value, such as a GUID. Only clients with a token made from that key can join. Add webhook URLs to hear when clients join and leave; they must end with `/`.

```bash
curl -X POST http://127.0.0.1:8080/api/rooms -H "Content-Type: application/json" --data '{
  "Name": "Lobby",
  "ConnectionKey": "6f9b2c1e-4d7a-4e8b-9a3f-1c5d7e9b2a40",
  "Webhooks": {
    "ClientJoined": "http://127.0.0.1:3000/webhooks/client_joined/",
    "ClientLeft": "http://127.0.0.1:3000/webhooks/client_left/"
  }
}'
```

The node replies with 201 Created:

```json
{"status":"success","timestamp":"2026-10-12T16:13:03.333247Z","method":"POST","room":{"name":"Lobby","port":53247}}
```

From now on, `room.port` identifies the room. Every setting a room accepts is listed under [Room settings](http-api.md#room-settings).

## 3. Give each client a token

A token is this JSON, encrypted with the room's `ConnectionKey`. Times are Unix milliseconds:

```json
{ "TargetPort": 53247, "IssueUnixTime": 1791824583333, "ExpirationUnixTime": 1791824613333 }
```

In C#, Keel's `EncryptionUtility` makes one:

```csharp
var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
var token = EncryptionUtility.Encrypt(
    JsonSerializer.Serialize(new { TargetPort = port, IssueUnixTime = now, ExpirationUnixTime = now + 30_000 }),
    connectionKey);
```

- **One token per join.** A token stays reserved until it expires, even after its client leaves, so give a reconnecting client a new one.
- **Keep it short-lived.** The client only needs the token to join, so 30 seconds is plenty.

Then send the client three things: the node's address, `room.port`, and the token. The [README](../README.md#connection-tokens) describes the encryption format, if your backend isn't in C#.

## 4. The client joins

The client connects to the room over UDP with its token, then joins. The [chat client example](../examples/chat_client/Program.cs) does exactly this, so you can try it:

```bash
dotnet run --project examples/chat_client -- 127.0.0.1 53247 "<token>"
```

When the client joins, the room calls your `ClientJoined` webhook:

```json
{
  "roomPort": 53247,
  "clientUid": 1,
  "token": "<the token the client joined with>",
  "endpoint": "203.0.113.5:65067",
  "timestamp": "2026-10-12T16:13:03.802665Z"
}
```

Match `token` against the tokens you issued to learn which of your users this is, and keep `clientUid`: the other endpoints use it. When the client leaves, `ClientLeft` arrives with the same `roomPort` and `clientUid`.

Webhooks are plain JSON `POST` requests, so any web framework can receive them. Read `token` with a JSON parser: characters such as `+` arrive escaped. The [reference](http-api.md#webhooks) covers retries and ordering.

## 5. Look up clients

```bash
# Every client in the room
curl http://127.0.0.1:8080/api/rooms/53247/clients

# The client that joined with a token (curl URL-encodes it)
curl -G http://127.0.0.1:8080/api/rooms/53247/clients --data-urlencode "token=<token>"

# One client by UID
curl http://127.0.0.1:8080/api/rooms/53247/clients/1
```

The token lookup returns a list with that client, or an empty list if no client in the room used the token:

```json
{"status":"success","timestamp":"2026-10-12T16:13:07.552422Z","method":"GET","clientsCount":1,"clients":[{"uid":1,"token":"<token>"}]}
```

Looking up a UID returns 404 once that client has left. For the whole room, with its name, client UIDs and traffic, use `curl http://127.0.0.1:8080/api/rooms/53247`.

## 6. Drop a client

```bash
curl -X DELETE http://127.0.0.1:8080/api/rooms/53247/clients/1
```

The room disconnects the client on its next update. The other clients get `OnDisconnect`, and your `ClientLeft` webhook is called.

## 7. Close the room

```bash
curl -X DELETE http://127.0.0.1:8080/api/rooms/53247
```

Closing a room drops its clients without telling them, and no `ClientLeft` webhooks are sent for them, so clean up your own records for the room when you close it.

When a node's last room closes, the node exits. Plan for that: start a node, open rooms on it, and start a new node when you need rooms again.

## Handling errors

Every error except 411 comes back as JSON with the reason in `error`:

```json
{"code":404,"error":"Unable to find room with port 53247.","timestamp":"2026-10-12T16:13:07.603054Z"}
```

| Status | What it means for you |
|---|---|
| 400 | A port, client UID or room setting is invalid. `error` says which. |
| 404 | The room or client is gone. Treat it as closed or left. |
| 405 | The path doesn't take that method. The `Allow` header lists the ones it does. |
| 409 | The port you asked for is taken. Retry with `TargetPort` `0` to get a free one. |
| 411 | Your `POST` had no body length. Send a body, even an empty one, with `Content-Length`. |
| 500 | Something unexpected happened. The node's log has the details. |

## Doing it from C#

The same flow with `HttpClient`. `EncryptionUtility` comes from `Keel.Utils.dll`, which is in every [Keel release](https://github.com/stasync/keel-dotnet/releases).

> **Send room settings with `JsonSerializerOptions.Default`.** Room settings are case-sensitive, and `PostAsJsonAsync` turns property names into camelCase unless you pass options. The node then ignores every setting, including `ConnectionKey`, and opens a room that anyone can join.

```csharp
using System;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using Keel.Utils.Security;

using var http = new HttpClient { BaseAddress = new Uri("http://127.0.0.1:8080/") };

// Room settings are case-sensitive: send them as written, not in camelCase.
var asWritten = JsonSerializerOptions.Default;

// Open a room with its own connection key.
var connectionKey = Guid.NewGuid().ToString();
var response = await http.PostAsJsonAsync("api/rooms", new
{
    Name = "Lobby",
    ConnectionKey = connectionKey,
    Webhooks = new
    {
        ClientJoined = "http://127.0.0.1:3000/webhooks/client_joined/",
        ClientLeft = "http://127.0.0.1:3000/webhooks/client_left/"
    }
}, asWritten);
response.EnsureSuccessStatusCode();
var created = await response.Content.ReadFromJsonAsync<JsonElement>();
var port = created.GetProperty("room").GetProperty("port").GetInt32();

// Create a token for one client: for this room only, valid for 30 seconds.
var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
var token = EncryptionUtility.Encrypt(
    JsonSerializer.Serialize(new { TargetPort = port, IssueUnixTime = now, ExpirationUnixTime = now + 30_000 }),
    connectionKey);

// ...send the node's address, the port and the token to the client...

// Later: find the client that joined with the token, and drop it.
var found = await http.GetFromJsonAsync<JsonElement>($"api/rooms/{port}/clients?token={Uri.EscapeDataString(token)}");
foreach (var client in found.GetProperty("clients").EnumerateArray())
{
    var uid = client.GetProperty("uid").GetUInt32();
    (await http.DeleteAsync($"api/rooms/{port}/clients/{uid}")).EnsureSuccessStatusCode();
}

// Close the room.
(await http.DeleteAsync($"api/rooms/{port}")).EnsureSuccessStatusCode();
```
