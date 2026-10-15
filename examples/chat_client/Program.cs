using Keel.Networking;
using Keel.Networking.Udp;
using Keel.Networking.Udp.LowLevel;
using System;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;

// A minimal Relay client: joins a room, sends every line you type to the other clients in the room,
// and prints what they send.
//
// Usage: dotnet run --project examples/chat_client -- <host> <port> [token]
//   host   address of the relay node, e.g. 127.0.0.1
//   port   UDP port of the room
//   token  connection token, needed when the room has a ConnectionKey

// Message types: the first byte of every message. See "Writing a client" in the README.
const byte OnAuth = 0;       // client -> room: join the room
const byte OnPacket = 1;     // both directions: a message for other clients
const byte OnJoined = 0;     // room -> client: a client joined
const byte OnDisconnect = 2; // room -> client: a client left

// Our own op code for chat lines. The room never reads op codes, so their meaning is up to the app.
const uint ChatOpCode = 1;

if (args.Length < 2 || !int.TryParse(args[1], out var port))
{
    Console.WriteLine("Usage: chat_client <host> <port> [token]");
    return 1;
}

var host = IPAddress.TryParse(args[0], out var address)
    ? address
    : Array.Find(Dns.GetHostAddresses(args[0]), a => a.AddressFamily == AddressFamily.InterNetwork);
var token = args.Length > 2 ? args[2] : null;

var client = new ReliableUdpClient();
var running = true;
var joined = false;

// Ends the main loop. Keel can raise more than one of these events for the same disconnect, so only the first is reported.
void Stop(string message)
{
    if (!running)
        return;

    Console.WriteLine(message);
    running = false;
}

// The UDP connection is up: join the room.
client.Connected += () =>
    client.Send(new[] { OnAuth }, UdpProtocol.DeliveryMethod.ReliableOrdered);

// ValidationFailure means the token was missing, wrong, expired or already used.
client.ConnectionRejected += reason => Stop($"The room rejected the connection: {reason}.");
client.ConnectionFailed += () => Stop("Couldn't reach the room.");
client.Disconnected += () => Stop("Disconnected from the room.");

client.DataReceived += (data, _) =>
{
    var reader = new NetReader(data);
    switch (reader.ReadByte())
    {
        case OnJoined:
        {
            var joinedUid = reader.ReadPackedUInt32();
            var clientCount = reader.ReadPackedUInt32();
            var clientUids = new uint[clientCount];
            for (var i = 0; i < clientCount; i++)
                clientUids[i] = reader.ReadPackedUInt32();

            // Everyone gets OnJoined, including the client that just joined.
            if (joinedUid == client.ConnectionUid)
            {
                joined = true;
                Console.WriteLine($"Joined the room as client {joinedUid}. Clients here: {string.Join(", ", clientUids)}.");
                Console.WriteLine("Type a message and press Enter to send it, or /quit to leave.");
            }
            else
                Console.WriteLine($"Client {joinedUid} joined.");

            break;
        }

        case OnPacket:
        {
            var senderUid = reader.ReadPackedUInt32();
            var opCode = reader.ReadPackedUInt32();
            var payload = reader.ReadBytesAndSize();
            if (opCode == ChatOpCode)
                Console.WriteLine($"[client {senderUid}] {Encoding.UTF8.GetString(payload)}");

            break;
        }

        case OnDisconnect:
            Console.WriteLine($"Client {reader.ReadPackedUInt32()} left.");
            break;
    }
};

// Console.ReadLine blocks, so read input on a background thread and hand the lines to the main loop.
// The client isn't thread-safe: only the main loop touches it.
var lines = new ConcurrentQueue<string>();
var inputThread = new Thread(() =>
{
    while (Console.ReadLine() is { } line)
        lines.Enqueue(line);

    // Input was closed, e.g. with Ctrl+D.
    lines.Enqueue("/quit");
}) { IsBackground = true };
inputThread.Start();

Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    lines.Enqueue("/quit");
};

Console.WriteLine($"Connecting to {host}:{port}...");
client.Connect(new IPEndPoint(host, port), connectionData: token);

// If the room has a UdpObfuscationKey, register the same key here, right after Connect():
// client.RegisterDataTransferLayer(new Keel.Networking.Udp.LowLevel.DataTransferLayers.DataTransferXorObfuscationLayer(udpObfuscationKey));

var writer = new NetWriter();
while (running)
{
    // Nothing runs in the background: the client sends, receives and raises its events in Update().
    client.Update();

    while (running && lines.TryDequeue(out var line))
    {
        if (line == "/quit")
        {
            Stop("Leaving the room.");

            // Leaving doesn't notify the room; the others see OnDisconnect once the room notices the silence.
            client.Disconnect();
            break;
        }

        if (!joined)
        {
            Console.WriteLine("Not in the room yet, message not sent.");
            continue;
        }

        if (line.Length == 0)
            continue;

        // OnPacket: op code, receiver count (0 means everyone except me), then the payload.
        writer.SeekZero();
        writer.WriteByte(OnPacket);
        writer.WritePackedUInt32(ChatOpCode);
        writer.WritePackedUInt32(0);
        writer.WriteBytesAndSize(Encoding.UTF8.GetBytes(line));
        client.Send(writer.AsArraySegment(), UdpProtocol.DeliveryMethod.ReliableOrdered);
    }

    Thread.Sleep(10);
}

return 0;
