using Keel.Networking;
using Keel.Networking.Udp;
using Keel.Networking.Udp.LowLevel;
using Keel.Networking.Udp.LowLevel.DataTransferLayers;
using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;

namespace UdpRelay.Client
{
    /// <summary>
    /// Why a <see cref="RelayClient"/> left its room.
    /// </summary>
    public enum RelayDisconnectReason
    {
        /// <summary><see cref="RelayClient.Disconnect"/> was called.</summary>
        Left,

        /// <summary>The room didn't answer: wrong address or port, wrong <c>UdpEncryptionKey</c>, the room is closed, or this IP address is blocked.</summary>
        Unreachable,

        /// <summary>The room already has as many clients as the node's <c>maxClientsPerRoom</c> allows.</summary>
        RoomFull,

        /// <summary>The token was missing, wrong, expired or already used. The room also blocks this IP address for a while.</summary>
        InvalidToken,

        /// <summary>The connection timed out, the client was dropped through the HTTP API, or the room was closed.</summary>
        ConnectionLost
    }

    /// <summary>
    /// A Relay room client that any app can reuse: it joins a room, keeps track of who is in it, and sends and
    /// receives packets. It speaks the protocol described under "Writing a client" in the README, so the app
    /// only deals with op codes and payloads.
    /// </summary>
    /// <remarks>
    /// Like Keel's <see cref="ReliableUdpClient"/>, nothing runs in the background: call <see cref="Update"/>
    /// regularly, such as once per frame or every 10 ms, and every event is raised from inside it.
    /// The class isn't thread-safe, so use it from one thread.
    /// </remarks>
    public sealed class RelayClient
    {
        public enum ConnectionState
        {
            /// <summary>Not connected. <see cref="Connect(IPEndPoint, string, string)"/> starts a new connection.</summary>
            Disconnected,

            /// <summary>Connecting to the room, or connected and waiting for the room to confirm the join.</summary>
            Connecting,

            /// <summary>In the room: packets can be sent.</summary>
            Joined
        }

        public delegate void PacketHandler(uint senderUid, uint opCode, byte[] payload, UdpProtocol.DeliveryMethod deliveryMethod);

        // Message types: the first byte of every message.
        private const byte OnAuth = 0;       // client -> room: join the room
        private const byte OnPacket = 1;     // both directions: a packet for other clients
        private const byte OnJoined = 0;     // room -> client: a client joined
        private const byte OnDisconnect = 2; // room -> client: a client left

        public ConnectionState State { get; private set; }

        public bool IsJoined => State == ConnectionState.Joined;

        /// <summary>This client's UID in the room, or 0 until it has joined.</summary>
        public uint Uid { get; private set; }

        /// <summary>The UIDs of every client in the room, including this one. Empty until this client has joined.</summary>
        public IReadOnlyCollection<uint> Clients => _clients;

        /// <summary>This client joined the room. <see cref="Clients"/> lists everyone already there.</summary>
        public event Action Joined = delegate { };

        /// <summary>Another client joined the room.</summary>
        public event Action<uint> ClientJoined = delegate { };

        /// <summary>Another client left the room.</summary>
        public event Action<uint> ClientLeft = delegate { };

        /// <summary>Another client sent this client a packet. The payload array belongs to the handler.</summary>
        public event PacketHandler PacketReceived = delegate { };

        /// <summary>
        /// The connection ended. It's raised exactly once for every <see cref="Connect(IPEndPoint, string, string)"/>,
        /// whatever ended it, and <see cref="State"/> is already <see cref="ConnectionState.Disconnected"/> when it's raised.
        /// </summary>
        public event Action<RelayDisconnectReason> Disconnected = delegate { };

        private readonly ReliableUdpClient _udpClient = new ReliableUdpClient();
        private readonly NetWriter _writer = new NetWriter();
        private readonly HashSet<uint> _clients = new HashSet<uint>();

        // Keel raises its events from inside its own Update(), where it catches and logs every exception as a
        // malformed datagram. Messages and the end of the connection are queued there and handled afterward,
        // so exceptions from the app's handlers reach the app, and handlers can safely call Disconnect().
        private readonly Queue<(byte[] Data, UdpProtocol.DeliveryMethod DeliveryMethod)> _incoming = new Queue<(byte[], UdpProtocol.DeliveryMethod)>();
        private RelayDisconnectReason? _pendingDisconnectReason;

        public RelayClient()
        {
            // The UDP connection is up: join the room.
            _udpClient.Connected += () =>
                _udpClient.Send(new[] { OnAuth }, UdpProtocol.DeliveryMethod.ReliableOrdered);

            _udpClient.DataReceived += (data, deliveryMethod) =>
                _incoming.Enqueue((data, deliveryMethod));

            // Keel can raise more than one of these for the same disconnect, and always ends with Disconnected,
            // so the first one tells why.
            _udpClient.ConnectionRejected += reason =>
                _pendingDisconnectReason ??= reason == ReliableUdpClient.RejectionReason.ServerIsFull
                    ? RelayDisconnectReason.RoomFull
                    : RelayDisconnectReason.InvalidToken;
            _udpClient.ConnectionFailed += () => _pendingDisconnectReason ??= RelayDisconnectReason.Unreachable;
            _udpClient.Disconnected += () => _pendingDisconnectReason ??= RelayDisconnectReason.ConnectionLost;
        }

        /// <summary>
        /// Starts connecting to a room. The client joins as soon as the connection is up, and raises
        /// <see cref="Joined"/> when the room confirms it.
        /// </summary>
        /// <param name="roomEndPoint">The node's address and the room's UDP port.</param>
        /// <param name="token">The connection token. Needed when the room has a <c>ConnectionKey</c>. A token works
        /// only once, so a reconnecting client needs a new one.</param>
        /// <param name="udpEncryptionKey">The room's <c>UdpEncryptionKey</c>, if it has one.</param>
        public void Connect(IPEndPoint roomEndPoint, string token = null, string udpEncryptionKey = null)
        {
            if (State != ConnectionState.Disconnected)
                throw new InvalidOperationException("Already connected. Call Disconnect() first.");

            _udpClient.Connect(roomEndPoint, connectionData: token);
            State = ConnectionState.Connecting;

            // The client creates its socket in Connect(), so the layer has to be registered after it, every time.
            if (!string.IsNullOrEmpty(udpEncryptionKey))
                _udpClient.RegisterDataTransferLayer(new DataTransferXorObfuscationLayer(udpEncryptionKey));
        }

        /// <summary>
        /// Starts connecting to a room. <paramref name="host"/> is an IP address or a host name. Resolving a name
        /// blocks until DNS answers.
        /// </summary>
        public void Connect(string host, int port, string token = null, string udpEncryptionKey = null)
        {
            var address = IPAddress.TryParse(host, out var parsed)
                ? parsed
                : Array.Find(Dns.GetHostAddresses(host), a => a.AddressFamily == AddressFamily.InterNetwork);

            if (address == null)
                throw new ArgumentException($"'{host}' has no IPv4 address.", nameof(host));

            Connect(new IPEndPoint(address, port), token, udpEncryptionKey);
        }

        /// <summary>
        /// Sends, receives, and raises events. Call it regularly while connected.
        /// </summary>
        public void Update()
        {
            if (State == ConnectionState.Disconnected)
                return;

            _udpClient.Update();

            // A handler may disconnect, which drops the rest of the queue.
            while (State != ConnectionState.Disconnected && _incoming.Count > 0)
            {
                var (data, deliveryMethod) = _incoming.Dequeue();
                HandleMessage(data, deliveryMethod);
            }

            if (State != ConnectionState.Disconnected && _pendingDisconnectReason is { } reason)
                End(reason);
        }

        /// <summary>
        /// Sends a packet to every other client in the room.
        /// </summary>
        /// <param name="opCode">Tells the receivers what the payload is. The room never reads it, so its meaning is up to the app.</param>
        /// <param name="payload">Use Keel's <see cref="NetWriter"/> to pack values into it. The packet must fit in one UDP datagram
        /// with its headers, which usually leaves 1429 bytes (see docs/udp-protocol.md). Keel throws
        /// <see cref="InvalidOperationException"/> for a bigger one, and nothing is sent.</param>
        /// <param name="deliveryMethod">The room forwards the packet with the same delivery method.</param>
        public void Send(uint opCode, ArraySegment<byte> payload, UdpProtocol.DeliveryMethod deliveryMethod = UdpProtocol.DeliveryMethod.ReliableOrdered) =>
            SendPacket(opCode, payload, deliveryMethod, receiverUids: null);

        /// <summary>
        /// Sends a packet to one client in the room.
        /// </summary>
        public void SendTo(uint receiverUid, uint opCode, ArraySegment<byte> payload, UdpProtocol.DeliveryMethod deliveryMethod = UdpProtocol.DeliveryMethod.ReliableOrdered) =>
            SendPacket(opCode, payload, deliveryMethod, new[] { receiverUid });

        /// <summary>
        /// Sends a packet to the listed clients. Nothing is sent if the list is empty. The
        /// room skips UIDs that aren't in the room.
        /// </summary>
        public void SendTo(IReadOnlyCollection<uint> receiverUids, uint opCode, ArraySegment<byte> payload, UdpProtocol.DeliveryMethod deliveryMethod = UdpProtocol.DeliveryMethod.ReliableOrdered)
        {
            if (receiverUids == null)
                throw new ArgumentNullException(nameof(receiverUids));

            // An empty list would mean "everyone except me" to the room.
            if (receiverUids.Count > 0)
                SendPacket(opCode, payload, deliveryMethod, receiverUids);
        }

        /// <summary>
        /// Leaves the room and raises <see cref="Disconnected"/> with <see cref="RelayDisconnectReason.Left"/>.
        /// The room isn't told: the other clients get <see cref="ClientLeft"/> once the room notices the silence,
        /// within a few seconds.
        /// </summary>
        public void Disconnect()
        {
            if (State != ConnectionState.Disconnected)
                End(RelayDisconnectReason.Left);
        }

        private void SendPacket(uint opCode, ArraySegment<byte> payload, UdpProtocol.DeliveryMethod deliveryMethod, IReadOnlyCollection<uint> receiverUids)
        {
            // The room blocks the IP address of a client that sends a packet before joining.
            if (State != ConnectionState.Joined)
                throw new InvalidOperationException("Not in a room yet. Wait for the Joined event.");

            // Keel's size check doesn't catch this one: NetWriter can't write a size this big, so it would leave the
            // payload out, and the room would block this IP address for the malformed message.
            if (payload.Count > ushort.MaxValue)
                throw new ArgumentException($"The payload is {payload.Count} bytes, too big for one datagram.", nameof(payload));

            // OnPacket: op code, receiver count (0 means everyone except me), each receiver's UID, then the payload.
            _writer.SeekZero();
            _writer.WriteByte(OnPacket);
            _writer.WritePackedUInt32(opCode);
            _writer.WritePackedUInt32((uint)(receiverUids?.Count ?? 0));
            if (receiverUids != null)
            {
                foreach (var uid in receiverUids)
                    _writer.WritePackedUInt32(uid);
            }

            _writer.WriteBytesAndSize(payload);
            _udpClient.Send(_writer.AsArraySegment(), deliveryMethod);
        }

        private void HandleMessage(byte[] data, UdpProtocol.DeliveryMethod deliveryMethod)
        {
            var reader = new NetReader(data);
            switch (reader.ReadByte())
            {
                case OnJoined:
                    {
                        var joinedUid = reader.ReadPackedUInt32();
                        var clientCount = reader.ReadPackedUInt32();

                        // The room sends OnJoined to everyone, including the client that just joined. The first one
                        // this client gets is its own, and it lists everyone already in the room.
                        if (State == ConnectionState.Connecting && joinedUid == _udpClient.ConnectionUid)
                        {
                            for (var i = 0; i < clientCount; i++)
                                _clients.Add(reader.ReadPackedUInt32());

                            Uid = joinedUid;
                            State = ConnectionState.Joined;
                            Joined();
                        }
                        else if (State == ConnectionState.Joined && _clients.Add(joinedUid))
                            ClientJoined(joinedUid);

                        break;
                    }

                case OnPacket:
                    {
                        var senderUid = reader.ReadPackedUInt32();
                        var opCode = reader.ReadPackedUInt32();
                        var payload = reader.ReadBytesAndSize();
                        PacketReceived(senderUid, opCode, payload, deliveryMethod);
                        break;
                    }

                case OnDisconnect:
                    {
                        var leftUid = reader.ReadPackedUInt32();
                        if (_clients.Remove(leftUid))
                            ClientLeft(leftUid);

                        break;
                    }

                    // Ignore message types from newer relays.
            }
        }

        private void End(RelayDisconnectReason reason)
        {
            // Closes the socket if Keel hasn't already. It raises Keel's Disconnected, which the reset below discards.
            _udpClient.Disconnect();

            _incoming.Clear();
            _pendingDisconnectReason = null;
            _clients.Clear();
            Uid = 0;
            State = ConnectionState.Disconnected;

            // Last, so a handler can call Connect() again right away.
            Disconnected(reason);
        }
    }
}