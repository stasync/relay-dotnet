using Keel.Networking;
using Keel.Networking.Udp;
using Keel.Networking.Udp.LowLevel;
using Keel.Networking.Udp.LowLevel.DataTransferLayers;
using Keel.Utils.Debug;
using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace UdpRelay
{
    /// <summary>
    /// A room: one UDP port and thread that forwards messages between the clients that joined it.
    /// </summary>
    internal sealed class Room : Keel.Utils.Threading.Worker
    {
        private sealed class ConnectionTokenData
        {
            [JsonInclude] internal int TargetPort { get; init; }
            [JsonInclude] internal long IssueUnixTime { get; init; }
            [JsonInclude] internal long ExpirationUnixTime { get; init; }
        }

        internal readonly struct ClientData
        {
            internal readonly uint Uid;
            internal readonly string Token;
            internal readonly IPEndPoint EndPoint;

            internal ClientData(uint uid, ReliableUdpServer.ConnectionRequest request)
            {
                Uid = uid;
                Token = request.ConnectionData;
                EndPoint = request.EndPoint;
            }
        }

        internal readonly struct ReadOnlyClientCollection : IReadOnlyCollection<uint>
        {
            public int Count
            {
                [MethodImpl(MethodImplOptions.AggressiveInlining)]
                get => _clients.Count;
            }

            private readonly ConcurrentDictionary<uint, string> _clients;

            internal ReadOnlyClientCollection(ConcurrentDictionary<uint, string> clients) =>
                _clients = clients;

            internal bool HasClient(uint clientUid) =>
                _clients.ContainsKey(clientUid);

            internal string GetClientToken(uint clientUid) =>
                _clients.TryGetValue(clientUid, out var token) ? token : throw new KeyNotFoundException($"Client with id '{clientUid}' does not exist.");

            internal uint[] ToArray()
            {
                var array = new uint[_clients.Count];
                _clients.Keys.CopyTo(array, arrayIndex: 0);
                return array;
            }

            public IEnumerator<uint> GetEnumerator() => _clients.Keys.GetEnumerator();
            IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
        }

        internal int Port
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => _udpServer.Port;
        }

        internal ReadOnlyClientCollection Clients
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => new(_clients);
        }

        internal int BytesSent
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => _bytesSent;
        }

        internal int BytesReceived
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => _bytesReceived;
        }

        internal bool LogConnectionRejections { get; set; }

        internal bool AutoCloseOnLastDisconnect { get; set; }

        internal int BlacklistDurationMs { get; set; }

        internal event Action<ClientData> ClientJoined = delegate { };

        internal event Action<uint> ClientLeft = delegate { };

        internal event Action Stopped = delegate { };

        private readonly ReliableUdpServer _udpServer;
        private readonly NetReader _dataReader = new();
        private readonly NetWriter _dataWriter = new();
        private readonly HashSet<uint> _routingConnectionsCache = new();
        private readonly Queue<uint> _disconnectQueue = new();
        private readonly Dictionary<string, long> _activeTokens = new();
        private readonly ConcurrentDictionary<uint, ClientData> _pendingConnections = new();
        private readonly ConcurrentDictionary<uint, string> _clients = new();
        private readonly ConcurrentQueue<uint> _externalDisconnectQueue = new();

        private readonly string _connectionKey;

        private volatile int _bytesSent;
        private volatile int _bytesReceived;

        private readonly DataTransferAmountCaptureLayer _dataTransferAmountCaptureLayer;

        internal Room(RoomSettings settings, int maxClients, ushort protocolKey, int updateIntervalMs) : base(updateIntervalMs, settings.Name.Trim(), monitor: true)
        {
            _udpServer = new ReliableUdpServer(maxClients, settings.TargetPort, protocolKey);
            if (!string.IsNullOrWhiteSpace(settings.UdpEncryptionKey))
                _udpServer.RegisterDataTransferLayer(new DataTransferXorObfuscationLayer(settings.UdpEncryptionKey));

            if (settings.ClientRateLimiter?.KBitPerSecLimit > 0)
            {
                var layer = new IncomingDataAmountTracker(settings.ClientRateLimiter.KBitPerSecLimit);
                layer.LimitExceeded += endpoint =>
                {
                    TryAddToBlacklist(endpoint.Address, reason: $"Client data transfer limit ({settings.ClientRateLimiter.KBitPerSecLimit}KBit/S) exceeded");
                };
                _udpServer.RegisterDataTransferLayer(layer);
            }

            if (settings.CaptureDataTransferAmount)
            {
                _dataTransferAmountCaptureLayer = new DataTransferAmountCaptureLayer();
                _udpServer.RegisterDataTransferLayer(_dataTransferAmountCaptureLayer);
            }

            _udpServer.ValidateConnection += OnValidateConnection;
            _udpServer.UnexpectedClientAction += OnUnexpectedClientAction;
            _udpServer.Connected += OnConnected;
            _udpServer.ConnectionRejected += OnConnectionRejected;
            _udpServer.DataReceived += OnDataReceived;
            _udpServer.Disconnected += OnDisconnected;

            _connectionKey = settings.ConnectionKey;
        }

        private bool OnValidateConnection(in ReliableUdpServer.ConnectionRequest connectionRequest)
        {
            if (string.IsNullOrWhiteSpace(_connectionKey))
            {
                // If the primary key is not setup, we always allow the connection. This could be used from debugging/testing. This is a serious security issue and key should be assigned in production.
                Logger.LogWarning($"[{nameof(OnValidateConnection)}] [SECURITY CONCERN] Bypassing validation for new connection ({connectionRequest.EndPoint}), as the primary connection key is not set.");
                return true;
            }

            if (string.IsNullOrWhiteSpace(connectionRequest.ConnectionData))
            {
                Logger.LogError($"[{nameof(OnValidateConnection)}] New connection ({connectionRequest.EndPoint}) data was not provided.");
                return false;
            }

            // Trying to decrypt connection data (token).
            var connectionTokenData = default(ConnectionTokenData);
            try
            {
                var connectionTokenString = Keel.Utils.Security.EncryptionUtility.Decrypt(encryptedData: connectionRequest.ConnectionData, secret: _connectionKey);
                connectionTokenData = JsonSerializer.Deserialize<ConnectionTokenData>(connectionTokenString);
            }
            catch (Exception ex)
            {
                Logger.LogException(ex);
            }

            if (connectionTokenData == null)
                return false;

            // Port sanity check.
            if (connectionTokenData.TargetPort != _udpServer.Port)
            {
                Logger.LogError($"[{nameof(OnValidateConnection)}] New connection ({connectionRequest.EndPoint}) target port '{connectionTokenData.TargetPort}' is invalid. Actual port is '{Port}'.");
                return false;
            }

            // Time sanity check.
            if (connectionTokenData.IssueUnixTime > connectionTokenData.ExpirationUnixTime)
            {
                Logger.LogError($"[{nameof(OnValidateConnection)}] New connection ({connectionRequest.EndPoint}) token is invalid. Expiration time must be greater than the issue time.");
                return false;
            }

            // Check if the token is too old.
            var localUnixTimeMilliseconds = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            if (localUnixTimeMilliseconds > connectionTokenData.ExpirationUnixTime)
            {
                Logger.LogError($"[{nameof(OnValidateConnection)}] New connection ({connectionRequest.EndPoint}) token invalid: expired.");
                return false;
            }

            // Check if the token is already in use by some other connection (preventing multiple connections).
            if (!_activeTokens.TryAdd(connectionRequest.ConnectionData, connectionTokenData.ExpirationUnixTime))
            {
                Logger.LogError($"[{nameof(OnValidateConnection)}] New connection ({connectionRequest.EndPoint}) token is already in use.");
                return false;
            }

            // Connection is valid and could be accepted.
            return true;
        }

        private void OnConnected(uint connectionUid, ReliableUdpServer.ConnectionRequest connectionRequest)
        {
            Logger.LogInfo($"[{nameof(OnConnected)}] New connection '{connectionUid}' from '{connectionRequest.EndPoint}' accepted, waiting for auth request...");
            _pendingConnections.TryAdd(connectionUid, new ClientData(connectionUid, connectionRequest));
        }

        private void OnConnectionRejected(IPEndPoint ipEndPoint, ReliableUdpClient.RejectionReason reason)
        {
            if (LogConnectionRejections)
                Logger.LogError($"[{nameof(OnConnected)}] New connection from '{ipEndPoint}' rejected with reason '{reason}'.");

            if (reason == ReliableUdpClient.RejectionReason.ValidationFailure)
                TryAddToBlacklist(ipEndPoint.Address, reason: "Client connection validation failed");
        }

        internal void DropClient(uint uid)
        {
            // NOTE: Internal udp server is not thread safe, while this function (and class) has to be.
            // Implementing a concurrent queue instead of locks for simplicity.
            _externalDisconnectQueue.Enqueue(uid);
        }

        private void OnDataReceived(uint senderConnectionUid, byte[] rawData, UdpProtocol.DeliveryMethod deliveryMethod)
        {
            try
            {
                _dataReader.Replace(rawData);
                var type = (ClientMessageType)_dataReader.ReadByte();

                switch (type)
                {
                    case ClientMessageType.OnAuth:
                        {
                            // Check pending connections.
                            if (!_pendingConnections.TryRemove(senderConnectionUid, out var clientData))
                            {
                                Logger.LogError($"Connection '{senderConnectionUid}' is not an active pending connection. Sometimes it may be normal considering different network issues.");
                                break;
                            }

                            // If connection already exists, we shouldn't handle 'OnAuth' message type.
                            if (!_clients.TryAdd(senderConnectionUid, clientData.Token))
                            {
                                TryAddToBlacklist(senderConnectionUid, reason: $"Connection '{senderConnectionUid}' doesn't exist");
                                break;
                            }

                            // Call the client joined event.
                            ClientJoined(clientData);

                            // Prepare the message for other clients.
                            _dataWriter.SeekZero();
                            _dataWriter.WriteByte((byte)RoomMessageType.OnJoined);
                            _dataWriter.WritePackedUInt32(senderConnectionUid);
                            _dataWriter.WritePackedUInt32((uint)_clients.Count);
                            foreach (var existedConnection in _clients.Keys)
                                _dataWriter.WritePackedUInt32(existedConnection);

                            // Data to send back.
                            var dataToSend = _dataWriter.AsArraySegment();

                            // Send back to all (including sender).
                            foreach (var existedConnection in _clients.Keys)
                                _udpServer.SendTo(existedConnection, dataToSend, UdpProtocol.DeliveryMethod.ReliableOrdered);

                            Logger.LogInfo($"[{nameof(ClientMessageType.OnAuth)}] Client '{senderConnectionUid}' joined - '{_clients.Count}' clients now.");
                        }
                        break;

                    case ClientMessageType.OnPacket:
                        {
                            if (!_clients.ContainsKey(senderConnectionUid))
                            {
                                TryAddToBlacklist(senderConnectionUid, reason: $"Connection '{senderConnectionUid}' doesn't exist");
                                break;
                            }

                            var userDataOpCode = _dataReader.ReadPackedUInt32();
                            var userDataReceiversCount = _dataReader.ReadPackedUInt32();

                            // userDataReceiversCount == 0 means that message will be sent to everyone, except the sender.
                            _routingConnectionsCache.Clear();
                            if (userDataReceiversCount > 0)
                            {
                                for (var i = 0; i < userDataReceiversCount; i++)
                                {
                                    var receiver = _dataReader.ReadPackedUInt32();
                                    _routingConnectionsCache.Add(receiver);
                                }
                            }

                            var userDataPayload = _dataReader.ReadBytesAndSizeNonAlloc();
                            _dataWriter.SeekZero();
                            _dataWriter.WriteByte((byte)RoomMessageType.OnPacket);
                            _dataWriter.WritePackedUInt32(senderConnectionUid);
                            _dataWriter.WritePackedUInt32(userDataOpCode);
                            _dataWriter.WriteBytesAndSize(userDataPayload);

                            // Data to send back.
                            var dataToSend = _dataWriter.AsArraySegment();

                            // Send to others.
                            foreach (var existedConnection in _clients.Keys)
                            {
                                // Should be sent:
                                // 1. userDataReceiversCount == 0 - send to everyone, except sender.
                                // 2. userDataReceiversCount > 0 - collection explicitly defined, send to all valid connections that are present in _routingConnectionsCache collection.
                                var shouldBeSent =
                                    userDataReceiversCount == 0 && existedConnection != senderConnectionUid ||
                                    userDataReceiversCount > 0 && _routingConnectionsCache.Contains(existedConnection);

                                if (shouldBeSent)
                                    _udpServer.SendTo(existedConnection, dataToSend, deliveryMethod);
                            }
                        }
                        break;

                    default:
                        TryAddToBlacklist(senderConnectionUid, reason: $"Unknown message type '{type}'");
                        break;
                }
            }
            catch (Exception e)
            {
                Logger.LogException(e);
                TryAddToBlacklist(senderConnectionUid, reason: e.Message);
            }
        }

        private void TryAddToBlacklist(uint connectionUid, string reason = null)
        {
            if (BlacklistDurationMs < 1)
                return;

            if (_udpServer.TryGetConnectionEndPoint(connectionUid, out var endPoint))
                _udpServer.AddAddressToBlacklistIfAbsent(endPoint.Address, millisecondsToAdd: BlacklistDurationMs, reason);
        }

        private void TryAddToBlacklist(IPAddress address, string reason = null)
        {
            if (BlacklistDurationMs >= 1)
                _udpServer.AddAddressToBlacklistIfAbsent(address, millisecondsToAdd: BlacklistDurationMs, reason);
        }

        private void OnDisconnected(uint disconnectedConnectionUid)
        {
            _pendingConnections.Remove(disconnectedConnectionUid, out _);

            if (_clients.Remove(disconnectedConnectionUid, out _))
                _disconnectQueue.Enqueue(disconnectedConnectionUid);
        }

        private void OnUnexpectedClientAction(IPEndPoint ipEndPoint, string description) =>
            TryAddToBlacklist(ipEndPoint.Address, reason: description);

        protected override void OnUpdate()
        {
            try
            {
                base.OnUpdate();

                while (_externalDisconnectQueue.TryDequeue(out var connectionToDropUid))
                    _udpServer.Disconnect(connectionToDropUid);

                _udpServer.Update();
                UpdateDisconnectQueue();
                CleanupExpiredTokens();

                if (_dataTransferAmountCaptureLayer != null)
                {
                    _bytesSent = _dataTransferAmountCaptureLayer.BytesSent;
                    _bytesReceived = _dataTransferAmountCaptureLayer.BytesReceived;
                }
            }
            catch (Exception e)
            {
                Logger.LogException(e);
            }
        }

        private void CleanupExpiredTokens()
        {
            if (_activeTokens.Count == 0)
                return;

            var localUnixTimeMilliseconds = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            foreach (var token in new List<string>(_activeTokens.Keys))
            {
                var tokenExpirationTime = _activeTokens[token];
                if (localUnixTimeMilliseconds >= tokenExpirationTime)
                    _activeTokens.Remove(token);
            }
        }

        private void UpdateDisconnectQueue()
        {
            if (_disconnectQueue.Count == 0)
                return;

            while (_disconnectQueue.TryDequeue(out var disconnectedConnectionUid))
            {
                // Call the client left event.
                ClientLeft(disconnectedConnectionUid);

                // Preparing data to send to existed connections.
                _dataWriter.SeekZero();
                _dataWriter.WriteByte((byte)RoomMessageType.OnDisconnect);
                _dataWriter.WritePackedUInt32(disconnectedConnectionUid);

                // Data to send to existed connections.
                var dataToSend = _dataWriter.AsArraySegment();

                // Send to all existed connections.
                foreach (var existedConnection in _clients.Keys)
                    _udpServer.SendTo(existedConnection, dataToSend, UdpProtocol.DeliveryMethod.ReliableOrdered);

                Logger.LogInfo($"[{nameof(OnDisconnected)}] Client '{disconnectedConnectionUid}' left - '{_clients.Count}' clients now.");
            }

            if (AutoCloseOnLastDisconnect && _clients.IsEmpty)
                Stop();
        }

        protected override void OnStop()
        {
            base.OnStop();

            _udpServer.Dispose();
            Stopped();
        }
    }
}