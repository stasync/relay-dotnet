# Data transfer layers and packet simulators

Keel's UDP transport has two hooks that sit right next to the socket:

- **Data transfer layers** see every datagram the transport sends or receives, and can change its bytes. Relay uses them to scramble traffic, enforce the rate limit and count traffic.
- **Incoming packet simulators** drop, delay or duplicate incoming datagrams, to imitate a bad network while you test. Relay doesn't register any. You add them to a client, or to a room in a local build.

Both are part of [Keel](https://github.com/stasync/keel-dotnet). This page describes them as Keel's `latest-build` implements them, which is the version Relay builds against. If you pin a different Keel version, check its source.

These layers aren't the transport, session and relay layers in the [UDP protocol reference](udp-protocol.md#layers). Those describe what's inside a datagram. Data transfer layers sit below all three and handle whole datagrams.

## Where they run

```
Sending:    Send() or a resend ─► transport header ─► layers 1 … n ─► socket

Receiving:  socket ─► layers n … 1 ─► blocked-address check ─► simulators 1 … n ─► transport ─► session ─► relay
```

- **Layers** get every datagram in both directions, transport header included: payloads, acks, heartbeats and resends. On the way in, they run before the blocked-address check, so they also see datagrams from blocked addresses.
- **Simulators** get only incoming datagrams, after the layers have run and after datagrams from blocked addresses are dropped. Nothing passes through a simulator on the way out.
- Both run inside the server's or client's `Update()` and `Send()` calls, on whichever thread makes them. A room makes them all on its own thread.

## Registering

`ReliableUdpServer` and `ReliableUdpClient` have the same six methods:

| Method | What it does |
|---|---|
| `RegisterDataTransferLayer(layer)` | Adds a layer after the ones already registered. |
| `UnregisterDataTransferLayer(layer)` | Removes one layer. |
| `UnregisterAllDataTransferLayers()` | Removes every layer. |
| `RegisterIncomingPacketSimulator(simulator)` | Adds a simulator after the ones already registered. |
| `UnregisterIncomingPacketSimulator(simulator)` | Removes one simulator. |
| `UnregisterAllIncomingPacketSimulators()` | Removes every simulator. |

Register layers and simulators once, at startup, and leave them in place for the whole session:

- **Room:** in the `Room` constructor, right after the `ReliableUdpServer` is created. The room's thread hasn't started yet, so nothing else is using the server. This is where Relay registers its layers.
- **Client:** right after each `Connect()`. The client creates its socket inside `Connect()`, so registering before it throws `InvalidOperationException`. `Disconnect()` throws the socket away along with its layers and simulators, so register them again after every `Connect()`. [`RelayClient`](../examples/relay_client/RelayClient.cs) does this for the obfuscation layer.

Changing them mid-session goes wrong in two ways. The lists aren't thread-safe, so changing them from another thread while `Update()` runs can throw. And both ends must agree on layers that change bytes: if one side scrambles and the other doesn't, neither can read the other.

### The first datagram from a client

`Connect()` sends the Connect message before it returns, so that datagram goes out before any layer is registered. With an obfuscation layer, the room unscrambles a datagram that was never scrambled, reads a wrong protocol key, and drops it silently. Keel resends it 100 ms later, through the layer this time, and the room accepts it.

So connecting to a room with a `UdpObfuscationKey` takes about 100 ms longer. Measured on loopback: 114 ms with the key, 8 ms without.

Simulators don't have this problem. The first incoming datagram is read in `Update()`, after they're registered.

## Data transfer layers

### Order

Layers run in registration order on the way out, and in reverse order on the way in:

```
Registered:  A, then B
Sending:     A, then B, then the socket
Receiving:   the socket, then B, then A
```

The first layer registered is nearest your code, and the last is nearest the socket. If both change bytes, B undoes its change before A sees the datagram. So a layer that changes bytes should usually be registered last, and the layers before it then see plain datagrams in both directions. The other side must register its matching layers in the same order.

### Layers in Keel

Both are in the `Keel.Networking.Udp.LowLevel.DataTransferLayers` namespace.

| Layer | What it does |
|---|---|
| `DataTransferXorObfuscationLayer(key)` | XORs every datagram with the key's UTF-8 bytes, the same way in both directions. An empty key does nothing. The byte layout is under [UdpObfuscationKey](udp-protocol.md#udpobfuscationkey). It hides traffic from casual inspection, but it isn't encryption. |
| `DataTransferAmountCaptureLayer` | Counts `BytesSent`, `BytesReceived`, `PacketsSent` and `PacketsReceived`. It counts whole datagrams, headers included, along with acks, heartbeats and resends. |

### Layers in a room

The `Room` constructor registers up to three layers, in this order, each only when its [room setting](http-api.md#room-settings) asks for it:

| Order | Layer | Registered when | What the room does with it |
|---|---|---|---|
| 1 | `DataTransferXorObfuscationLayer` | `UdpObfuscationKey` is set | Scrambles all of the room's traffic. Clients must register the same key. |
| 2 | [`IncomingDataAmountTracker`](../runtime/IncomingDataAmountTracker.cs) | `ClientRateLimiter.KBitPerSecLimit` is above `0` | Counts each sender's incoming bytes, per IP address and port, in 1-second windows. The first time a sender goes over the limit in a window, the room blocks its IP address. |
| 3 | `DataTransferAmountCaptureLayer` | `CaptureDataTransferAmount` is `true` | Reports its byte counts as the room's `bytesSent` and `bytesReceived` in `GET /api/rooms`. |

The obfuscation layer comes first, the opposite of the advice under [Order](#order), so the other two count scrambled datagrams. That's fine, because XOR doesn't change a datagram's length. If you add a layer that reads the contents, register it before the obfuscation layer so it sees them unscrambled.

The rate limiter runs before the blocked-address check, so it keeps counting a sender after the room has blocked it. Blocking an address that's already blocked does nothing, so the extra counting doesn't make the block longer.

### Writing a layer

Derive from `DataTransferLayer` and override both methods. This one records the biggest datagram in each direction, which helps when checking the [size limits](udp-protocol.md#size-limits):

```csharp
using System;
using System.Net;
using Keel.Networking.Udp.LowLevel;

// Records the biggest datagram seen in each direction, headers included.
sealed class LargestDatagramLayer : DataTransferLayer
{
    public int LargestSent { get; private set; }
    public int LargestReceived { get; private set; }

    public override void ProcessOutgoingData(IPEndPoint targetEndPoint, ref ArraySegment<byte> data) =>
        LargestSent = Math.Max(LargestSent, data.Count);

    public override void ProcessIncomingData(IPEndPoint senderEndPoint, ref ArraySegment<byte> data) =>
        LargestReceived = Math.Max(LargestReceived, data.Count);
}
```

Rules for `data`:

- It's a slice of a buffer that Keel reuses for the next datagram. Copy anything you want to keep.
- To change the bytes, edit them in place, or point `data` at a new array. Setting it to `default` makes Keel throw `InvalidOperationException`.
- A layer can't drop a datagram: Keel throws on an empty one. To get rid of a sender, do what `IncomingDataAmountTracker` does: raise an event, and have the room block the address.
- Keel checks the 1452-byte limit before the layers run. If a layer makes a datagram bigger than that, Keel logs an exception and doesn't send it, and `Send()` doesn't report an error. Each byte a layer adds is one byte less for the payload.
- Every datagram passes through, including a heartbeat from each client every 100 ms and the room's reply to each one, so keep the work small.

## Packet simulators

On a local machine, the network never loses, delays or reorders anything. Simulators make that happen on purpose, so you can see how your app copes before a real network does it to you.

### How they work

In each `Update()`, Keel reads every datagram waiting on the socket and collects them in a list. Each simulator, in registration order, gets that list and can remove, hold back, add or reorder entries. Keel then processes whatever is left. A simulator that holds datagrams back, like the delay simulator, hands them on in a later `Update()`, so delays are only as precise as the gap between `Update()` calls.

Simulators act only on what arrives. On a client, that's the room-to-client hop: packets from other clients, the room's heartbeat replies, and the room's acks for the client's own reliable packets. Some consequences:

- A delay is added once to a round trip, not twice. Measured: with a fixed 300 ms delay on the client, a packet echoed back by the server arrived after 321 ms.
- A lost ack makes the sender resend a datagram that already arrived. Delays above 100 ms do the same, because Keel resends anything not acknowledged within 100 ms. The receiver drops the copies, but they still cost traffic.
- To make the client-to-room hop bad, register the simulators on the room. Relay has no setting for that: add the calls to the `Room` constructor in a local build. Don't ship it, because it affects every client in the room.

Once any simulator is registered, Keel copies every incoming datagram so the simulator can hold on to it. Leave simulators out of production builds.

### Simulators in Keel

All of them are in the `Keel.Networking.Udp.LowLevel.Simulators` namespace. Percentages go from 0 to 100.

| Simulator | Settings and defaults | What it does |
|---|---|---|
| `RandomPacketLossSimulator` | `PacketLossChancePercent` = 10 | Drops each datagram with this chance. |
| `PeriodicPacketLossSimulator` | `PacketLossPercent` = 10 | Drops every *n*th datagram, where *n* is 100 ÷ `PacketLossPercent`, rounded to a whole number: 10 drops every 10th, 30 every 3rd. Unlike random loss, the pattern is fixed, which makes a failing test easier to repeat. |
| `PacketDelaySimulator` | `PacketMinDelayMs` = 100, `PacketMaxDelayMs` = 200, `ReverseDelayedPackets` = `false` | Holds each datagram for a random time between the two delays. Different delays reorder datagrams. With `ReverseDelayedPackets`, datagrams that come due in the same `Update()` are handed on newest first, for more reordering. |
| `RandomPacketDuplicationSimulator` | `PacketDuplicationChancePercent` = 10 | Delivers each datagram twice with this chance. |
| `DropAllPacketsSimulator` | none | Drops everything, as if the network went down. |
| `DummySimulator` | none | Does nothing. A template for writing your own. |

`PacketDelaySimulator` clamps each delay against the other: the minimum can't go above the maximum, and the maximum can't go below the minimum. When raising both, set the maximum first. `{ PacketMinDelayMs = 300, PacketMaxDelayMs = 500 }` gives 200 to 500 ms, because the minimum is clamped to the old maximum of 200. `{ PacketMaxDelayMs = 500, PacketMinDelayMs = 300 }` gives 300 to 500 ms.

### Example

On a `ReliableUdpClient`:

```csharp
using Keel.Networking.Udp.LowLevel.Simulators;

client.Connect(roomEndPoint, connectionData: token);

// A poor mobile connection: 5% loss, then 80 to 250 ms of delay.
client.RegisterIncomingPacketSimulator(new RandomPacketLossSimulator { PacketLossChancePercent = 5 });
client.RegisterIncomingPacketSimulator(new PacketDelaySimulator { PacketMaxDelayMs = 250, PacketMinDelayMs = 80 });
```

`RelayClient` keeps its `ReliableUdpClient` private. To test with it, add the same calls after `_udpClient.Connect()` in `RelayClient.Connect()`.

### What your app sees

Keel's delivery rules, described in the [UDP protocol reference](udp-protocol.md#transport-layer), decide what each simulated problem looks like from your code:

| Simulated problem | What your app sees |
|---|---|
| Loss | Unreliable packets go missing. Reliable and ReliableOrdered packets arrive late: Keel resends at about 100, 300, 700 and 1500 ms, and gives up after 2 s. |
| Delay | Everything arrives later. ReliableOrdered packets that arrive early wait for the ones before them. Unreliable and Reliable packets are handed over in the order they arrive. |
| Duplication | Nothing. Keel drops duplicates on every channel. |
| Everything dropped | The connection ends after 2 s of silence. `RelayClient` reports `ConnectionLost`, or `Unreachable` if it hadn't connected yet. Measured: a client that dropped everything gave up connecting after 2014 ms, even though the server had accepted it. |
| A delay of 2 s or more | The client can't connect, because it gives up if ConnectConfirmed takes longer than 2 s. |

### Writing a simulator

Derive from `IncomingPacketSimulator` and override `Update(in List<(byte[], IPEndPoint)> incomingQueue)`. The list holds the datagrams that arrived since the last `Update()`, each as its own copy with its sender. Whatever is in the list when your method returns goes on to the next simulator. To hold datagrams back, keep them yourself and add them to the list in a later call, as `PacketDelaySimulator` does.
