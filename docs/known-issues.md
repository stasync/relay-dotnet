# Known issues

Security gaps and limitations that Relay has today, each with a workaround until it's fixed.

## Security

### The HTTP API is plain HTTP

The node serves its API over plain HTTP. The API key, room `ConnectionKey`s and client tokens all cross the network as plain text, so anyone who can watch the traffic between the backend and the node can take the key and control the node.

**Workaround:** keep the API on `127.0.0.1`, or on a private network shared only by the backend and the node. If the backend runs on another machine, put a TLS reverse proxy such as nginx or Caddy in front of the node, and point the backend at the proxy.

**Possible fix:** serve HTTPS from the node, or document a supported proxy setup.

### Webhooks aren't signed

`ClientJoined` and `ClientLeft` webhooks are plain JSON `POST`s with nothing that proves they came from the node. Anyone who can reach your webhook URLs can send fake ones; a fake `ClientLeft`, for example, makes your backend think a player left.

**Workaround:** accept webhooks only on `127.0.0.1` or a private network. In `ClientJoined`, check that `token` is one you issued. Webhook URLs can use `https://` if your backend serves HTTPS.

**Possible fix:** send the API key, or an HMAC signature of the body, with each webhook.

### Logs contain room keys and tokens

The HTTP API key is never logged, but these are, at the `Info` level:

- Each room's `ConnectionKey` and `UdpObfuscationKey`, when the room opens ([HttpRequestHandler.cs](../runtime/HttpRequestHandler.cs)).
- Client tokens: in every HTTP response body ([HttpRequestHandler.cs](../runtime/HttpRequestHandler.cs)), in the `?token=` query of each request ([HttpServer.cs](../runtime/HttpServer.cs)), and in each `ClientJoined` webhook ([HttpWebhookSender.cs](../runtime/HttpWebhookSender.cs)).

The `ConnectionKey` is the one that matters: anyone who can read the logs can make valid tokens for that room for as long as it's open. A logged token is worth little, since each one joins only once and expires soon.

**Workaround:** restrict who can read the log folder, and don't ship the logs to shared systems. Or start the node with `-logLevel Warning`, which leaves out all `Info` lines, including the useful ones.

**Possible fix:** stop logging room settings and response bodies, or mask keys and tokens in them.

### Game traffic isn't encrypted

UDP traffic between clients and rooms, including the token each client connects with, travels as plain bytes. `UdpObfuscationKey` only XORs it, which hides it from casual inspection but not from anyone who tries. Don't send secrets through a room.

**Possible fix:** an encrypting [data transfer layer](layers-and-simulators.md), registered on both the room and the client.

### Rooms without a `ConnectionKey` let anyone in

`POST /api/rooms` without a `ConnectionKey`, or with an empty body, opens a room that accepts any client, with or without a token. So does every room opened with `-roomPort`. The node only logs a `[SECURITY CONCERN]` warning for each connection.

**Workaround:** always send a `ConnectionKey` when opening a room.

**Possible fix:** reject `POST /api/rooms` without a `ConnectionKey`, and keep open rooms for `-roomPort` only.

### The API key check is basic

- The key is compared with a plain string comparison, not in constant time. Measuring the difference over a network is impractical, so this is an accepted trade-off.
- Wrong guesses aren't limited. Each one is logged as an error, so a scanner can fill the log.
- There's one key for everything, with no read-only or per-backend keys. Changing it means restarting the node.
- Passed as `-httpApiKey <key>`, the key shows up in the process list for every user on the machine. `startup.sh` passes it this way.

**Workaround:** use a long random key, such as the output of `openssl rand -hex 32`. On shared machines, put it in `httpApiKey.cmdparam` next to `relay.dll` instead of on the command line, readable only by the user that runs the node.

## Behavior

### A node exits when its last room closes

Closing the last room, through the API or with Ctrl+C, stops the node. To open rooms again, the backend has to start a new node.

**Possible fix:** make this configurable (see the TODO in [Entry.cs](../runtime/Entry.cs)).

### A node without a working API keeps running

If `httpListenerUrl` is invalid or `httpApiKey` is empty, the node logs an error and keeps running with no API, and no rooms unless `-roomPort` opened some, until it's stopped.

**Workaround:** after starting a node, poll `GET /api/status`; if it never answers, read the log and stop the process.

**Possible fix:** exit with a non-zero code instead.

### Webhook retries never stop

An unreachable webhook URL is retried every 4 seconds with no limit, and pending webhooks are kept in memory. If the backend is down for a long time, the queue keeps growing, and everything still pending is lost when the node exits. Webhooks can also arrive out of order; use their `timestamp`.

**Possible fix:** cap the number of retries, back off between them, and drop the oldest when the queue is full.
