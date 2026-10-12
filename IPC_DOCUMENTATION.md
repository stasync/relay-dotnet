# RT Server IPC Documentation

## Overview

The rt_server implements a bi-directional IPC (Inter-Process Communication) system using HTTP for incoming control commands and webhooks for outgoing event notifications. This enables external processes (primarily the Node.js API server) to manage relay instances and receive real-time notifications about relay lifecycle events.

---

## Architecture

### Components

1. **HttpServer** - HTTP listener for incoming control API requests
2. **HttpRequestHandler** - Request handler implementation for all API endpoints
3. **HttpWebhookClient** - Outgoing webhook client for event notifications
4. **RelayServer** - UDP relay server with lifecycle event emission

### Communication Flow

```
External Process (API Server)
    ↓ HTTP Request (Control Commands)
[HttpServer] → [HttpRequestHandler] → [RelayServer]
    ↑ HTTP Webhooks (Event Notifications)
[HttpWebhookClient] ← [RelayServer Events]
```

---

## HTTP Control API

Base URL: `http://127.0.0.1:{httpListenerPort}`

All responses are JSON with `application/json` content type.

### Startup Configuration

The HTTP listener port is configured via the `httpListenerPort` command-line argument. If set to `0`, the first available TCP port is automatically selected.

**Example:**
```bash
dotnet rt_server.dll --httpListenerPort 8080
```

---

## API Endpoints

### 1. Create New Relay Instance

**Endpoint:** `POST /api/relay/new`

**Description:** Creates a new UDP relay instance with specified configuration.

#### Request Parameters

Query parameter: `config` (optional JSON string)

**DTO: RelayServerStartupData**
```json
{
  "WorkerName": "string",         // Optional - Name for the relay worker thread
  "TargetPort": 0,                // Optional - UDP port (0 = auto-assign first available)
  "UdpEncryptionKey": "string",   // Optional - UDP packet encryption key
  "ConnectionKey": "string"       // Optional - AES-256-CBC encryption key for connection tokens
}
```

**Example Request:**
```http
GET /api/relay/new?config={"TargetPort":9000,"WorkerName":"CustomRelay","UdpEncryptionKey":"udpKey123","ConnectionKey":"mySecretKey123"}
```

Or without config (auto-assigns port):
```http
GET /api/relay/new
```

#### Response

**Success Response (200 OK)**
```json
{
  "status": "success",
  "timestamp": "2026-01-20T12:34:56.789Z",
  "method": "GET",
  "relayPort": 9000,
  "workerName": "CustomRelay"
}
```

**Error Response (400/500)**
```json
{
  "status": 400,
  "error": "Port 9000 is not (or no longer) available at this machine."
}
```

#### Validation Rules

- If `TargetPort` is `0`, first available UDP port is automatically assigned
- If `TargetPort` is specified (non-zero):
  - Must be in valid range (1-65535)
  - Must be available on the system
  - Throws `HttpRequestException` if invalid or unavailable
- If `WorkerName` is empty, defaults to `RelayThread_{TargetPort}`
- `UdpEncryptionKey` is optional - used for UDP packet encryption/decryption
- `ConnectionKey` is optional but **strongly recommended** for production
  - If empty, connection validation is bypassed (security warning logged)
  - Used for AES-256-CBC decryption of connection tokens

---

### 2. Get Connection Information

**Endpoint:** `GET /api/relay/get_connection`

**Description:** Retrieves information about a specific connection on a relay instance.

#### Request Parameters

Query parameters:
- `port` (integer, required) - Relay port number
- `uid` (unsigned integer, optional) - Connection UID to query
- `token` (string, optional) - Connection token to query

**Examples:**
```http
# Get connection by UID
GET /api/relay/get_connection?port=9000&uid=42

# Get connection by token
GET /api/relay/get_connection?port=9000&token=encrypted_token_string
```

#### Response

**Success Response (200 OK)**
```json
{
  "status": "success",
  "timestamp": "2026-03-16T12:34:56.789Z",
  "method": "GET",
  "connectionUid": 42
}
```

**Error Responses**
```json
// Missing port parameter
{
  "status": 400,
  "error": "Port is not specified."
}

// Neither uid nor token provided
{
  "status": 400,
  "error": "Either connection UID or token must be specified."
}

// Relay not found
{
  "status": 400,
  "error": "Unable to find relay worker with port 9000."
}

// Connection not found
{
  "status": 400,
  "error": "Unable to find connection with specified criteria."
}
```

---

### 3. Shutdown Relay Instance(s)

**Endpoint:** `GET /api/relay/shutdown`

**Description:** Shuts down one or all active relay instances.

#### Request Parameters

Query parameter: `port` (optional integer)

**Examples:**
```http
# Shutdown specific relay on port 9000
GET /api/relay/shutdown?port=9000

# Shutdown ALL active relays
GET /api/relay/shutdown
```

#### Response

**Success Response (200 OK)**
```json
{
  "status": "success",
  "timestamp": "2026-01-20T12:34:56.789Z",
  "method": "GET",
  "closedRelays": [
    {
      "name": "RelayThread_9000",
      "port": 9000
    },
    {
      "name": "RelayThread_9001",
      "port": 9001
    }
  ]
}
```

#### Behavior

- Without `port` parameter: Stops ALL active `RelayServer` workers
- With `port` parameter: Stops only the relay matching the specified port
- Non-relay workers (e.g., LogWorker, HttpServer) are not affected
- Calls `RelayServer.Stop()` which triggers graceful shutdown

---

### 4. Get Relay Status

**Endpoint:** `GET /api/relay/status`

**Description:** Retrieves status information for relay instances including active connections.

#### Request Parameters

Query parameter: `port` (optional integer)

**Examples:**
```http
# Get status of all relays
GET /api/relay/status

# Get status of specific relay
GET /api/relay/status?port=9000
```

#### Response - All Relays

**Success Response (200 OK)**
```json
{
  "status": "success",
  "timestamp": "2026-01-20T12:34:56.789Z",
  "method": "GET",
  "relayWorkersCount": 2,
  "relayWorkers": [
    {
      "name": "RelayThread_9000",
      "port": 9000,
      "connectionsCount": 3,
      "connections": [1, 2, 5]
    },
    {
      "name": "RelayThread_9001",
      "port": 9001,
      "connectionsCount": 1,
      "connections": [3]
    }
  ]
}
```

#### Response - Specific Relay

**Success Response (200 OK)**
```json
{
  "status": "success",
  "timestamp": "2026-01-20T12:34:56.789Z",
  "method": "GET",
  "relayWorker": {
    "name": "RelayThread_9000",
    "port": 9000,
    "connectionsCount": 3,
    "connections": [1, 2, 5]
  }
}
```

**Error Response (400)**
```json
{
  "status": 400,
  "error": "Unable to find relay worker with port 9000."
}
```

#### Response Fields

- `connections` - Array of connection UIDs (unsigned 32-bit integers)
- `connectionsCount` - Number of active connections
- `name` - Worker thread name
- `port` - UDP port the relay is listening on

---

### 5. Check Connection Existence

**Endpoint:** `GET /api/relay/has_connection`

**Description:** Checks if a specific connection UID exists on a relay instance.

#### Request Parameters

Query parameters (both required):
- `port` (integer) - Relay port number
- `uid` (unsigned integer) - Connection UID to check

**Example:**
```http
GET /api/relay/has_connection?port=9000&uid=42
```

#### Response

**Success Response (200 OK)**
```json
{
  "status": "success",
  "timestamp": "2026-01-20T12:34:56.789Z",
  "method": "GET",
  "hasConnection": true
}
```

**Error Responses**
```json
// Missing port parameter
{
  "status": 400,
  "error": "Port is not specified."
}

// Missing uid parameter
{
  "status": 400,
  "error": "Connection UID is not specified."
}

// Relay not found
{
  "status": 400,
  "error": "Unable to find relay worker with port 9000."
}
```

---

### 6. Drop Connection

**Endpoint:** `GET /api/relay/drop_connection`

**Description:** Forcefully disconnects a specific connection from a relay instance.

#### Request Parameters

Query parameters (both required):
- `port` (integer) - Relay port number
- `uid` (unsigned integer) - Connection UID to drop

**Example:**
```http
GET /api/relay/drop_connection?port=9000&uid=42
```

#### Response

**Success Response (200 OK)**
```json
{
  "status": "success",
  "timestamp": "2026-01-20T12:34:56.789Z",
  "method": "GET"
}
```

**Error Responses**
```json
// Missing port parameter
{
  "status": 400,
  "error": "Port is not specified."
}

// Missing uid parameter
{
  "status": 400,
  "error": "Connection UID is not specified."
}

// Connection not found
{
  "status": 400,
  "error": "Unable to find connection with uid 42 on port 9000."
}

// Relay not found
{
  "status": 400,
  "error": "Unable to find relay worker with port 9000."
}
```

#### Behavior

- Calls `RelayServer.DropConnection(uid)` internally
- Uses thread-safe concurrent queue for disconnection requests
- Triggers `Disconnected` event which sends webhook notification

---

### 7. Get System Information

**Endpoint:** `GET /api/system/info`

**Description:** Retrieves process-level system information and worker statistics.

#### Request Parameters

None

**Example:**
```http
GET /api/system/info
```

#### Response

**Success Response (200 OK)**
```json
{
  "status": "success",
  "timestamp": "2026-01-20T12:34:56.789Z",
  "method": "GET",
  "managedMemory": 52428800,          // bytes (GC.GetTotalMemory)
  "totalMemory": 104857600,           // bytes (Process.WorkingSet64)
  "workersCount": 5,                  // Total Worker instances
  "workers": [
    {
      "type": "UdpRelay.RelayServer",
      "name": "RelayThread_9000"
    },
    {
      "type": "UdpRelay.RelayServer",
      "name": "RelayThread_9001"
    },
    {
      "type": "Core.Utils.Debug.LogWorker",
      "name": "LogWorker"
    }
  ]
}
```

#### Response Fields

- `managedMemory` - Managed heap memory in bytes (from `GC.GetTotalMemory(false)`)
- `totalMemory` - Total process memory in bytes (from `Process.WorkingSet64`)
- `workersCount` - Count of all active `Worker` instances
- `workers` - Array of worker information
  - `type` - Full type name of the worker
  - `name` - Worker instance name

#### Use Cases

- Health monitoring
- Memory leak detection
- Worker lifecycle debugging
- Performance profiling

---

## Webhook Notifications (Outgoing)

The rt_server sends HTTP POST webhooks to a configured endpoint for relay lifecycle events.

### Configuration

**Command-line argument:** `--webhooksEndpoint`

**Example:**
```bash
dotnet rt_server.dll --webhooksEndpoint "http://localhost:8080/api/webhooks"
```

**Validation:**
- URL must be valid HTTP/HTTPS
- If empty or invalid, webhook client is not initialized
- Validation uses `Core.Networking.Utils.IsValidUrl()`

### Webhook Delivery

- **Method:** HTTP POST
- **Content-Type:** `application/json`
- **Delivery:** Fire-and-forget (async, non-blocking)
- **Error Handling:** Exceptions logged, does not affect relay operation

---

## Webhook Events

### 1. User Connected

**Endpoint:** `POST {webhooksEndpoint}/relay_user_connected`

**Triggered when:** New UDP client successfully connects to a relay instance

**Payload:**
```json
{
  "relayPort": 9000,
  "connUid": 42,
  "connRequestData": "encrypted_connection_token_string",
  "endpoint": "192.168.1.100:54321"
}
```

**Fields:**
- `relayPort` - UDP port of the relay instance
- `connUid` - Unique connection identifier (uint32)
- `connRequestData` - Raw connection data sent by client (usually encrypted token)
- `endpoint` - Client's IP address and port (IPEndPoint.ToString())

**Event Source:** `RelayServer.Connected` event

---

### 2. User Disconnected

**Endpoint:** `POST {webhooksEndpoint}/relay_user_disconnected`

**Triggered when:** UDP client disconnects (graceful or forced)

**Payload:**
```json
{
  "relayPort": 9000,
  "connUid": 42
}
```

**Fields:**
- `relayPort` - UDP port of the relay instance
- `connUid` - Unique connection identifier that disconnected

**Event Source:** `RelayServer.Disconnected` event

**Disconnect Triggers:**
- Client timeout (no heartbeat)
- Explicit disconnect request from client
- Forced disconnection via `/api/relay/drop_connection`
- Relay instance shutdown

---

## Connection Token System

### Token Structure

**DTO: ConnectionTokenData** (internal to RelayServer)
```json
{
  "TargetPort": 9000,
  "IssueUnixTime": 1737374096789,
  "ExpirationUnixTime": 1737374126789
}
```

### Token Lifecycle

1. **Generation** (by external system, e.g., API server):
   ```csharp
   var tokenData = new {
       TargetPort = 9000,
       IssueUnixTime = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
       ExpirationUnixTime = DateTimeOffset.UtcNow.AddSeconds(30).ToUnixTimeMilliseconds()
   };
   var tokenJson = JsonSerializer.Serialize(tokenData);
   var encryptedToken = EncryptionUtility.Encrypt(tokenJson, secretKey);
   ```

2. **Client Connection:**
   - Client includes encrypted token in connection request
   - Token sent as `connectionData` in UDP connection handshake

3. **Validation** (in `RelayServer.OnValidateConnection`):
   - Decrypt token using shared `ConnectionKey`
   - Verify `TargetPort` matches relay's listening port
   - Verify `ExpirationUnixTime > IssueUnixTime`
   - Verify current time < `ExpirationUnixTime`
   - Verify token not already in use (prevents duplicate connections)

4. **Token Tracking:**
   - Accepted tokens stored in `_activeTokens` dictionary
   - Entry removed on client disconnect
   - Prevents token reuse across multiple connections

### Validation Bypass (Debug Only)

If `ConnectionKey` is empty or whitespace:
- Validation is **completely bypassed**
- All connections accepted without token verification
- Security warning logged on each connection
- **NEVER use in production**

### Encryption Details

- **Algorithm:** AES-256-CBC
- **Implementation:** `Core.Utils.Security.EncryptionUtility`
- **Key Source:** `RelayServerStartupData.ConnectionKey`
- **Shared Secret:** Must match between token generator and relay instance

---

## Error Handling

### HTTP Response Codes

- **200 OK** - Successful operation
- **400 Bad Request** - Invalid parameters, validation failure
- **404 Not Found** - Unknown API endpoint
- **500 Internal Server Error** - Unhandled exception

### Error Response Format

All errors return JSON:
```json
{
  "status": 400,
  "error": "Descriptive error message"
}
```

### Exception Handling

- Exceptions in request handlers logged via `Logger.LogException(e)`
- Returns 500 status with exception details in error message
- Webhook failures logged but do not affect API operation
- Worker thread exceptions logged in `Worker.UpdateLoop()` finally block

---

## Thread Safety

### Concurrent Access Patterns

1. **HttpServer:**
   - Requests handled asynchronously via `Task.Run()`
   - Each request processed in separate task
   - No blocking of listener thread

2. **RelayServer:**
   - `DropConnection()` uses `ConcurrentQueue<uint>` for thread-safe external disconnection
   - `_connections` uses `ConcurrentDictionary<uint, byte>` for connection tracking
   - Worker update loop processes queue in `OnUpdate()`

3. **HttpWebhookClient:**
   - `SendWebhookAndForget()` wraps async call in `Task.Run()`
   - URL cache uses `ConcurrentDictionary<string, string>`
   - No synchronization locks required

---

## Configuration Reference

### Command-Line Arguments

All arguments configurable via `--argName value` syntax or `.cmdparam` files.

**IPC-Related Arguments:**

| Argument | Type | Default | Description |
|----------|------|---------|-------------|
| `httpListenerPort` | int | 0 | TCP port for HTTP control API (0 = auto-assign) |
| `webhooksEndpoint` | string | "" | Base URL for outgoing webhook notifications |

**Relay-Related Arguments:**

| Argument | Type | Default | Description |
|----------|------|---------|-------------|
| `relayPort` | int[] | [] | Default UDP ports to open on startup (debug only) |
| `maxConnectionsPerPort` | int | 16 | Maximum concurrent connections per relay |
| `relayProtocolKey` | ushort | 0 | UDP protocol key for client handshake |
| `relayMillisecondsTimeStep` | int | 50 | Worker update loop interval (ms) |
| `blacklistDurationMs` | int | 10000 | IP blacklist duration after validation failure |
| `logConnectionRejections` | bool | false | Log rejected connection attempts |

**Logging Arguments:**

| Argument | Type | Default | Description |
|----------|------|---------|-------------|
| `logLevel` | LogLevel | All | Minimum log level (All, Info, Warning, Error) |
| `logOutputRelativePath` | string | "logs" | Relative path for log file output |

### Argument Loading

Arguments loaded from:
1. Command line: `dotnet rt_server.dll --httpListenerPort 8080`
2. Config files: Create `{argName}.cmdparam` file with value as content

Example `.cmdparam` files:
```
# httpListenerPort.cmdparam
8080

# webhooksEndpoint.cmdparam
http://localhost:8080/api/webhooks
```

---

## Usage Examples

### Example 1: Create Relay with Token Validation

```bash
# Start rt_server with HTTP listener
dotnet rt_server.dll --httpListenerPort 8080 --webhooksEndpoint "http://localhost:3000/webhooks"

# Create new relay via API (from external process)
curl "http://127.0.0.1:8080/api/relay/new?config=%7B%22TargetPort%22:9000,%22ConnectionKey%22:%22mySecret123%22%7D"

# Response:
# {
#   "status": "success",
#   "timestamp": "2026-01-20T12:34:56.789Z",
#   "method": "GET",
#   "relayPort": 9000,
#   "workerName": "RelayThread_9000"
# }
```

### Example 2: Monitor Relay Status

```bash
# Check all relays
curl "http://127.0.0.1:8080/api/relay/status"

# Check specific relay
curl "http://127.0.0.1:8080/api/relay/status?port=9000"

# Check if connection exists
curl "http://127.0.0.1:8080/api/relay/has_connection?port=9000&uid=42"
```

### Example 3: System Monitoring

```bash
# Get process info
curl "http://127.0.0.1:8080/api/system/info"

# Response includes memory usage and worker list
```

### Example 4: Graceful Shutdown

```bash
# Shutdown specific relay
curl "http://127.0.0.1:8080/api/relay/shutdown?port=9000"

# Shutdown all relays (triggers application exit)
curl "http://127.0.0.1:8080/api/relay/shutdown"
```

### Example 5: Force Disconnect Client

```bash
# Drop specific connection
curl "http://127.0.0.1:8080/api/relay/drop_connection?port=9000&uid=42"

# Triggers webhook: POST {webhooksEndpoint}/relay_user_disconnected
```

---

## Integration with Node.js API Server

### Process Spawning

The Node.js API server spawns rt_server instances via `process_manager.js`:

```javascript
const args = [
  '--httpListenerPort', httpPort,
  '--webhooksEndpoint', 'http://localhost:8080/api/webhooks',
  '--relayPort', relayPort,
  '--connectionKey', encryptionKey
];

const proc = spawn('dotnet', ['rt_server.dll', ...args], {
  detached: true,
  stdio: ['ignore', 'ignore', 'ignore']
});
```

### Webhook Handler

API server implements webhook endpoints to receive relay events:

```javascript
// Express routes
app.post('/api/webhooks/relay_user_connected', (req, res) => {
  const { relayPort, connUid, connRequestData, endpoint } = req.body;
  // Handle connection event
  res.json({ status: 'received' });
});

app.post('/api/webhooks/relay_user_disconnected', (req, res) => {
  const { relayPort, connUid } = req.body;
  // Handle disconnection event
  res.json({ status: 'received' });
});
```

### Control Flow

1. API server receives cluster creation request
2. Generates encryption key and connection tokens
3. Spawns rt_server process with arguments
4. rt_server exposes HTTP control API
5. Clients connect to relay with encrypted tokens
6. rt_server sends webhooks on connection events
7. API server receives webhooks and updates MongoDB
8. API server can query relay status via HTTP API

---

## Security Considerations

### Connection Token Security

- **Encryption Required:** Always set `ConnectionKey` in production
- **Token Expiration:** Implement reasonable expiration times (30s recommended)
- **Single-Use Tokens:** Tokens cannot be reused after successful connection
- **Port Binding:** Tokens bound to specific relay port
- **Time Validation:** Protects against replay attacks with expired tokens

### Network Security

- **HTTP Listener:** Binds to `127.0.0.1` (localhost only)
- **Webhook Endpoint:** Should use HTTPS in production
- **IP Blacklisting:** Failed validations trigger temporary IP blocks
- **Blacklist Duration:** Configurable via `blacklistDurationMs` argument

### Recommendations

1. **Always use HTTPS** for webhook endpoints in production
2. **Rotate encryption keys** periodically
3. **Monitor failed connection attempts** via logs
4. **Set appropriate expiration times** for tokens
5. **Use firewall rules** to restrict HTTP API access
6. **Enable connection rejection logging** for security auditing

---

## Troubleshooting

### Common Issues

**Issue:** HTTP listener fails to start
- **Cause:** Port already in use
- **Solution:** Change `httpListenerPort` or set to 0 for auto-assignment

**Issue:** Webhooks not received
- **Cause:** Invalid webhook endpoint URL
- **Solution:** Verify URL with `Utils.IsValidUrl()`, check API server logs

**Issue:** Connections rejected with validation failure
- **Cause:** Token encryption/decryption mismatch
- **Solution:** Ensure `ConnectionKey` matches between token generator and relay

**Issue:** "Port not available" error on relay creation
- **Cause:** Port already bound by another process
- **Solution:** Use different port or set to 0 for auto-assignment

**Issue:** Application doesn't exit after shutdown
- **Cause:** Worker threads still active
- **Solution:** Ensure all relay instances stopped before finalization

### Debugging

**Enable verbose logging:**
```bash
dotnet rt_server.dll --logLevel All --logConnectionRejections true
```

**Check worker status:**
```bash
curl "http://127.0.0.1:8080/api/system/info"
```

**Monitor relay connections:**
```bash
curl "http://127.0.0.1:8080/api/relay/status"
```

**Test webhook delivery:**
- Check API server webhook endpoint logs
- Verify `webhooksEndpoint` URL is reachable
- Look for webhook errors in rt_server logs

---

## Performance Characteristics

### HTTP Server

- **Concurrency:** Async request handling (no blocking)
- **Overhead:** Minimal - delegates to handlers immediately
- **Throughput:** Limited by .NET HttpListener implementation

### Webhooks

- **Fire-and-Forget:** Non-blocking, async delivery
- **Failure Handling:** Logged but does not retry
- **Latency:** Does not impact relay operation

### Worker Threading

- **Update Interval:** Configurable via `relayMillisecondsTimeStep`
- **Default:** 50ms update cycle
- **Monitoring:** Warns if update exceeds time step
- **CPU Usage:** Proportional to connection count and message rate

---

## Version Compatibility

- **.NET Runtime:** 9.0+ (multi-targets netstandard2.1 in core libs)
- **OS Support:** Linux, macOS, Windows
- **HTTP Protocol:** HTTP/1.1 (via HttpListener)
- **JSON Serialization:** System.Text.Json
- **Signal Handling:** POSIX signals (SIGINT, SIGTERM) on Unix-like systems

---

## References

### Source Files

- [HttpServer.cs](runtime/HttpServer.cs) - HTTP listener implementation
- [HttpRequestHandler.cs](runtime/HttpRequestHandler.cs) - API endpoint handlers
- [HttpWebhookClient.cs](runtime/HttpWebhookClient.cs) - Webhook delivery client
- [RelayServer.cs](runtime/RelayServer.cs) - UDP relay with event emission
- [RelayServerStartupData.cs](runtime/RelayServerStartupData.cs) - Configuration DTO
- [Entry.cs](runtime/Entry.cs) - Application entry point and lifecycle management
- [StartupArguments.cs](runtime/StartupArguments.cs) - Command-line argument definitions

### Related Documentation

- [LRO Server Architecture](../.github/copilot-instructions.md)
- [Build & Deployment Guide](../build.sh)
- [API Server Integration](../st_server/server/)
