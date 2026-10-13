# UDP protocol reference

Clients talk to a room over UDP. This page describes every byte that goes over the wire: how the datagrams are framed, how a client connects and stays connected, and the messages that join a room and pass packets between clients.

Most clients don't need all of it. Keel's `ReliableUdpClient` handles the transport and session layers, and [`RelayClient`](../examples/relay_client/RelayClient.cs) handles the relay layer too. Read on if you're writing a client without Keel, debugging a packet capture, or want to know the limits. The [README](../README.md#writing-a-client) has a shorter overview of the relay messages.

## Layers

Every datagram has three layers, each one carried inside the one below:

```
┌─ UDP datagram, at most 1452 bytes ──────────────────────────────────────────────┐
│ transport header │ session message                                              │
│     8 bytes      │ code │ session fields │ relay message                        │
│                  │      │                │ type │ relay fields │ payload        │
└─────────────────────────────────────────────────────────────────────────────────┘
```

| Layer | Implemented by | What it does |
|---|---|---|
| **Transport** | Keel's `UdpTransport` and `UdpProtocol` | Delivery methods: acknowledgements, resends, duplicate filtering and ordering. |
| **Session** | Keel's `ReliableUdpClient` and `ReliableUdpServer` | Connecting with a token, heartbeats, timeouts. |
| **Relay** | This repository's [`Room.cs`](../runtime/Room.cs) | Joining the room, forwarding packets between clients, join and leave notices. |

The transport and session layers belong to [Keel](https://github.com/stasync/keel-dotnet). This page describes them as Keel's `latest-build` implements them, which is the version Relay builds against. If you pin a different Keel version, check its source.

The **payload** is your app's data. Neither Keel nor the room ever looks inside it.

## Encoding

| Type | Encoding |
|---|---|
| byte | One byte. |
| u16, u32 | Fixed size, little-endian. |
| packed | A `uint` in 1 to 5 bytes, written by `NetWriter.WritePackedUInt32`. See the next table. |
| string | Length in bytes (u16), then that many bytes of UTF-8. Null and empty strings are both just `00 00`. |
| bytes | Length (u16), then that many bytes. Written by `NetWriter.WriteBytesAndSize`. |

A packed value's first byte tells how many bytes follow:

| Value | Bytes | Layout |
|---|---|---|
| 0 – 240 | 1 | The value itself. |
| 241 – 2287 | 2 | `241 + (v − 240) / 256`, then `(v − 240) % 256`. |
| 2288 – 67823 | 3 | `249`, then `(v − 2288) / 256`, then `(v − 2288) % 256`. |
| 67824 – 16777215 | 4 | `250`, then `v` as 3 bytes, little-endian. |
| larger | 5 | `251`, then `v` as u32. |

For example, 7 is `07`, 300 is `f1 3c`, 1000 is `f3 f8`, and 674358722 is `fb c2 e5 31 28`.

## Transport layer

### Header

Every datagram starts with 8 bytes:

| Offset | Size | Field | Values |
|---|---|---|---|
| 0 | u16 | Protocol key | Must match the receiver's key. Datagrams with a different key are dropped silently. Rooms use the node's `protocolKey` launch argument, `0` by default, and Keel's `ReliableUdpClient` always sends `0`. |
| 2 | byte | Datagram type | `0` payload, `1` ack. |
| 3 | byte | Channel | The delivery method: `0` Unreliable, `1` Reliable, `2` ReliableOrdered. |
| 4 | u32 | Datagram UID | Sequence number. Each side counts separately for each peer and each channel, starting at 1. |

In a payload datagram, the session message follows the header and fills the rest of the datagram. An ack has one more byte after the header, as described under [Acks](#acks).

A datagram can be at most **1452 bytes**, header included, and there's no fragmentation. Keel throws `InvalidOperationException` rather than send a bigger one. See [Size limits](#size-limits).

### Delivery methods

| Channel | Delivery method | Acked and resent | Duplicates dropped | Delivered in order |
|---|---|---|---|---|
| `0` | Unreliable | No | Yes | No |
| `1` | Reliable | Yes | Yes | No |
| `2` | ReliableOrdered | Yes | Yes | Yes |

The receiver remembers the last 2048 datagram UIDs on each channel and drops any it has seen before. On channel `2`, it delivers datagrams strictly in UID order, and holds back any that arrive early until the gap is filled.

### Acks

The receiver of a payload datagram on channel `1` or `2` answers right away with an ack:

| Offset | Size | Field | Value |
|---|---|---|---|
| 0 | u16 | Protocol key | As above. |
| 2 | byte | Datagram type | `1` |
| 3 | byte | Channel | `0`: acks are never acked themselves. |
| 4 | u32 | Datagram UID | The UID of the datagram being acknowledged. |
| 8 | byte | Original channel | The channel that datagram came on. |

For example, `00 00 01 00 01 00 00 00 01` acknowledges datagram 1 on channel `1`.

The sender keeps each reliable datagram until it's acknowledged. It resends it after 100 ms, then waits twice as long before each new try, up to 1 s between tries. That's resends at about 100, 300, 700 and 1500 ms. After 2 s it gives up on that datagram. Giving up doesn't end the connection by itself, but by then the session layer's heartbeat timeout has usually ended it anyway.

### UdpEncryptionKey

When a room has a `UdpEncryptionKey`, every datagram is XOR-scrambled in both directions, header and acks included. Byte `i` of the datagram is XORed with byte `i % n` of the key's UTF-8 bytes, where `n` is the key's length in bytes. The sender scrambles the datagram as the very last step, and the receiver unscrambles it before reading anything else.

A client with the wrong key, or no key, sends a scrambled protocol key, so the room drops its datagrams silently. To the client, the room looks unreachable. This hides traffic from casual inspection, but it isn't real encryption.

## Session layer

A session message fills the rest of a payload datagram. Its first byte is a message code, and the codes differ by direction.

### Messages from the client

| Code | Message | Fields | Sent on |
|---|---|---|---|
| `0` | Connect | Connection data (string): the [connection token](../README.md#connection-tokens), or empty when the room has no `ConnectionKey`. | Reliable |
| `1` | Heartbeat | Connection UID (packed), validation UID (packed), slot (byte). | Unreliable, every 100 ms |
| `2` | Data | Connection UID (packed), validation UID (packed), slot (byte), then the relay message (bytes). | The relay message's delivery method |

### Messages from the room

| Code | Message | Fields | Sent on |
|---|---|---|---|
| `0` | ConnectConfirmed | Connection UID (packed), validation UID (packed), slot (byte). | Reliable |
| `1` | ConnectionRejected | Reason (byte): `0` the room is full, `1` the token failed validation. | Unreliable |
| `2` | Heartbeat | Nothing. | Unreliable, in reply to each client heartbeat |
| `3` | Data | The relay message (bytes). | The relay message's delivery method |
| `4` | Disconnect | Nothing. | Unreliable |

ConnectConfirmed hands the client three values, which it repeats in every Heartbeat and Data message:

- **Connection UID:** the client's UID in the room. The room numbers clients 1, 2, 3 and so on in the order they connect, and never reuses a number. The relay layer and the HTTP API use the same UID.
- **Validation UID:** a random 32-bit number. It proves that the messages come from the client the room confirmed. It's usually 5 bytes once packed.
- **Slot:** the connection's index in the room, from 0 up to `maxClientsPerRoom` − 1. Because it's one byte, a room holds at most 255 clients, whatever `maxClientsPerRoom` says.

### Connecting

1. The client sends Connect with its token.
2. The room checks the token if it has a `ConnectionKey`, then looks for a free slot.
3. If both checks pass, the room answers with ConnectConfirmed. Otherwise it answers with ConnectionRejected. A rejected token also blocks the client's IP address.
4. If the client gets no ConnectConfirmed within 2 s, it gives up. ConnectionRejected is sent unreliably, so a lost rejection looks like this too.

Once connected, the room accepts the client's messages only from the IP address and port it connected from. A client whose address changes mid-session, for example when a NAT rebinds its port, gets blocked.

### Staying connected

The client sends a Heartbeat every 100 ms, and the room answers each one. If either side hears nothing for **2 s**, it considers the connection gone:

- The room frees the slot. If the client had joined, the room also sends OnDisconnect to the other clients and calls the `ClientLeft` webhook.
- The client raises `Disconnected` in Keel, which `RelayClient` reports as `ConnectionLost`.

### Leaving

| What happens | What goes over the wire |
|---|---|
| The client disconnects | Nothing. The room notices the silence within 2 s. |
| The client is dropped through `DELETE /api/rooms/{port}/clients/{uid}` | The room sends Disconnect. It's unreliable, so if it's lost, the client times out after 2 s instead. |
| The room is closed | Nothing. Clients time out after 2 s. |

For at least 2 s after a client leaves, the room quietly ignores late Heartbeat and Data messages for its UID, instead of blocking the address.

## Relay layer

A relay message travels inside a session Data message. Its first byte is the message type, and as in the session layer, the types differ by direction.

### Messages from the client

| Type | Message | Fields |
|---|---|---|
| `0` | OnAuth | Nothing. Sent once, right after ConnectConfirmed. |
| `1` | OnPacket | Op code (packed), receiver count (packed), each receiver's UID (packed), then the payload (bytes). |

### Messages from the room

| Type | Message | Fields | Sent on |
|---|---|---|---|
| `0` | OnJoined | The new client's UID (packed), the client count (packed), then every client's UID (packed), in no particular order. The list includes the new client. | ReliableOrdered |
| `1` | OnPacket | The sender's UID (packed), op code (packed), then the payload (bytes). | Whatever the sender used |
| `2` | OnDisconnect | The UID of the client that left (packed). | ReliableOrdered |

### Joining

A connected client isn't in the room until it sends OnAuth. Until then, the room doesn't forward packets to it, doesn't list it in the HTTP API, and blocks its IP address if it sends an OnPacket.

When OnAuth arrives, the room adds the client, calls the `ClientJoined` webhook, and sends OnJoined to every client, the new one included. A client recognizes its own OnJoined by comparing the UID with its connection UID. Its own OnJoined is also the first OnJoined it gets.

A client should send OnPacket only after its own OnJoined has arrived. An unreliable OnPacket sent right after OnAuth can overtake it, and the room would block the client for sending a packet before joining. A repeated OnAuth is ignored.

### Routing

The room never reads the op code or the payload. It forwards each OnPacket with the delivery method the sender used:

- With a receiver count of `0`, it goes to every other client in the room.
- Otherwise, it goes only to the listed UIDs that are in the room. UIDs that aren't in the room are skipped, a UID listed twice gets the packet once, and senders that list themselves get their own packet back.

## Example: joining and sending

These are real datagrams from a client that joined a room with one other client in it, UID 1. The room has no `ConnectionKey`, so it accepts any token; `tok` keeps the example short. Spaces separate the transport header, the session message and the relay message. Acks and heartbeats are left out.

| Direction | Datagram | Meaning |
|---|---|---|
| client → room | `00 00 00 01 01 00 00 00`&nbsp;&nbsp;`00 03 00 74 6f 6b` | Reliable datagram 1. Connect, token `tok`. |
| room → client | `00 00 00 01 01 00 00 00`&nbsp;&nbsp;`00 02 fb c2 e5 31 28 01` | Reliable datagram 1. ConnectConfirmed: UID 2, validation UID 674358722, slot 1. |
| client → room | `00 00 00 02 01 00 00 00`&nbsp;&nbsp;`02 02 fb c2 e5 31 28 01 01 00`&nbsp;&nbsp;`00` | ReliableOrdered datagram 1. Data from UID 2, carrying 1 byte: OnAuth. |
| room → client | `00 00 00 02 01 00 00 00`&nbsp;&nbsp;`03 05 00`&nbsp;&nbsp;`00 02 02 01 02` | ReliableOrdered datagram 1. Data carrying 5 bytes: OnJoined, UID 2 joined, 2 clients: 1 and 2. |

Then the client sends `hi` with op code 7 to everyone else, which is 25 bytes:

```
00 00 00 02 02 00 00 00 02 02 fb c2 e5 31 28 01 07 00 01 07 00 02 00 68 69
```

| Bytes | Layer | Field | Value |
|---|---|---|---|
| `00 00` | transport | Protocol key | 0 |
| `00` | transport | Datagram type | Payload |
| `02` | transport | Channel | ReliableOrdered |
| `02 00 00 00` | transport | Datagram UID | 2 |
| `02` | session | Code | Data |
| `02` | session | Connection UID | 2 |
| `fb c2 e5 31 28` | session | Validation UID | 674358722 |
| `01` | session | Slot | 1 |
| `07 00` | session | Relay message size | 7 |
| `01` | relay | Type | OnPacket |
| `07` | relay | Op code | 7 |
| `00` | relay | Receiver count | 0: everyone except the sender |
| `02 00` | relay | Payload size | 2 |
| `68 69` | payload | | `hi` |

When client 1 sends `yo` with op code 7 back, unreliably, this client receives 18 bytes:

```
00 00 00 00 02 00 00 00 03 07 00 01 01 07 02 00 79 6f
```

| Bytes | Layer | Field | Value |
|---|---|---|---|
| `00 00` | transport | Protocol key | 0 |
| `00` | transport | Datagram type | Payload |
| `00` | transport | Channel | Unreliable |
| `02 00 00 00` | transport | Datagram UID | 2: heartbeats use this channel too |
| `03` | session | Code | Data |
| `07 00` | session | Relay message size | 7 |
| `01` | relay | Type | OnPacket |
| `01` | relay | Sender UID | 1 |
| `07` | relay | Op code | 7 |
| `02 00` | relay | Payload size | 2 |
| `79 6f` | payload | | `yo` |

## Size limits

Each packet travels in a single datagram of at most 1452 bytes, so the headers and the payload together must fit. Sending a packet takes these headers:

| Layer | Header | Bytes |
|---|---|---|
| Transport | Fixed header | 8 |
| Session | Data: code, connection UID, validation UID, slot, size | 1 + (1–5) + (1–5) + 1 + 2: usually 10 |
| Relay | OnPacket: type, op code, receiver count, receivers, size | 1 + (1–5) + (1–5) + (1–5 per receiver) + 2: 5 for a broadcast with an op code up to 240 |

So the largest payload is `1452 − 8 − session header − relay header`:

- **Usually 1429 bytes**: a broadcast with an op code up to 240, from a client whose UID is up to 240. Measured: 1429 bytes went through, and 1430 threw.
- **At least 1421 bytes** for a broadcast, whatever the op code and UIDs.
- Each listed receiver takes 1 more byte, or up to 5 for UIDs above 240.

A packet that's too big throws `InvalidOperationException` in Keel, and nothing is sent. Split bigger data across several packets. The room's forwarded OnPacket is always smaller than the one the client sent, so anything the room accepts, it can forward.

The other messages fit easily. The biggest OnJoined, for a full room of 255 clients, is under 1300 bytes.

## What gets an address blocked

The room blocks a whole IP address, every port, for the node's `blacklistDurationMs` (10 s by default; `0` turns blocking off). While an address is blocked, the room silently drops everything it sends. To the client, the room looks like it went quiet, so it times out. Other clients behind the same address, such as on the same home or office network, are blocked too.

| Layer | Cause |
|---|---|
| Transport | A datagram shorter than 8 bytes, or with an unknown datagram type. A wrong protocol key isn't blocked: the datagram is just dropped. |
| Session | A rejected token. A full room doesn't block. |
| Session | An unknown message code, or a message that can't be read. |
| Session | A Heartbeat or Data message for an empty slot, unless its UID left in the last 2 s. |
| Session | A connection UID or validation UID that doesn't match the slot. |
| Session | A Heartbeat or Data message from a different IP address or port than the client connected from. |
| Relay | An unknown message type, or a message that can't be read. |
| Relay | An OnPacket before OnAuth. |
| Rate limit | More incoming traffic than the room's `ClientRateLimiter.KBitPerSecLimit`. It counts whole datagrams, headers included, for each IP address and port, in 1-second windows. |
