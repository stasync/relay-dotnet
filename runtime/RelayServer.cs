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
    /// Relay server responsible for routing the udp packages accordingly, to the corresponded clients.
    /// </summary>
    internal sealed class RelayServer : Keel.Utils.Threading.Worker
    {
        private sealed class ConnectionTokenData
        {
            [JsonInclude] internal int TargetPort { get; init; }
            [JsonInclude] internal long IssueUnixTime { get; init; }
            [JsonInclude] internal long ExpirationUnixTime { get; init; }
        }

        internal readonly struct NewConnectionData
        {
            internal readonly uint Uid;
            internal readonly string Token;
            internal readonly IPEndPoint EndPoint;

            internal NewConnectionData(uint uid, ReliableUdpServer.ConnectionRequest request)
            {
                Uid = uid;
                Token = request.ConnectionData;
                EndPoint = request.EndPoint;
            }
        }

        internal readonly struct ReadOnlyConcurrentConnectionCollection : IReadOnlyCollection<uint>
        {
            public int Count
            {
                [MethodImpl(MethodImplOptions.AggressiveInlining)]
                get => _connections.Count;
            }

            private readonly ConcurrentDictionary<uint, string> _connections;

            internal ReadOnlyConcurrentConnectionCollection(ConcurrentDictionary<uint, string> connections) =>
                _connections = connections;

            internal bool HasConnection(uint connectionUid) =>
                _connections.ContainsKey(connectionUid);

            internal string GetConnectionToken(uint connectionUid) =>
                _connections.TryGetValue(connectionUid, out var token) ? token : throw new KeyNotFoundException($"Connection with id '{connectionUid}' does not exist.");

            internal uint[] ToArray()
            {
                var array = new uint[_connections.Count];
                _connections.Keys.CopyTo(array, arrayIndex: 0);
                return array;
            }

            public IEnumerator<uint> GetEnumerator() => _connections.Keys.GetEnumerator();
            IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
        }

        internal int Port
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => _udpListener.Port;
        }

        internal ReadOnlyConcurrentConnectionCollection Connections
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => new(_connections);
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

        internal event Action<NewConnectionData> Connected = delegate { };

        internal event Action<uint> Disconnected = delegate { };

        internal event Action Stopped = delegate { };

        private readonly ReliableUdpServer _udpListener;
        private readonly NetReader _dataReader = new();
        private readonly NetWriter _dataWriter = new();
        private readonly HashSet<uint> _routingConnectionsCache = new();
        private readonly Queue<uint> _disconnectQueue = new();
        private readonly Dictionary<string, long> _activeTokens = new();
        private readonly ConcurrentDictionary<uint, NewConnectionData> _pendingConnections = new();
        private readonly ConcurrentDictionary<uint, string> _connections = new();
        private readonly ConcurrentQueue<uint> _externalDisconnectQueue = new();

        private readonly string _connectionKey;

        private volatile int _bytesSent;
        private volatile int _bytesReceived;

        private readonly DataTransferAmountCaptureLayer _dataTransferAmountCaptureLayer;

        internal RelayServer(RelayServerStartupData startupData, int maxConnections, ushort protocolKey, int millisecondsTimeStep) : base(millisecondsTimeStep, startupData.WorkerName.Trim(), monitor: true)
        {
            _udpListener = new ReliableUdpServer(maxConnections, startupData.TargetPort, protocolKey);
            if (!string.IsNullOrWhiteSpace(startupData.UdpEncryptionKey))
                _udpListener.RegisterDataTransferLayer(new DataTransferXorObfuscationLayer(startupData.UdpEncryptionKey));

            if (startupData.ClientRateLimiter?.KBitPerSecLimit > 0)
            {
                var layer = new IncomingDataAmountTracker(startupData.ClientRateLimiter.KBitPerSecLimit);
                layer.LimitExceeded += endpoint =>
                {
                    TryAddToBlacklist(endpoint.Address, reason: $"Client data transfer limit ({startupData.ClientRateLimiter.KBitPerSecLimit}KBit/S) exceeded");
                };
                _udpListener.RegisterDataTransferLayer(layer);
            }

            if (startupData.CaptureDataTransferAmount)
            {
                _dataTransferAmountCaptureLayer = new DataTransferAmountCaptureLayer();
                _udpListener.RegisterDataTransferLayer(_dataTransferAmountCaptureLayer);
            }

            _udpListener.ValidateConnection += OnValidateConnection;
            _udpListener.UnexpectedClientAction += OnUnexpectedClientAction;
            _udpListener.Connected += OnConnected;
            _udpListener.ConnectionRejected += OnConnectionRejected;
            _udpListener.DataReceived += OnDataReceived;
            _udpListener.Disconnected += OnDisconnected;

            _connectionKey = startupData.ConnectionKey;
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
            if (connectionTokenData.TargetPort != _udpListener.Port)
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
            _pendingConnections.TryAdd(connectionUid, new NewConnectionData(connectionUid, connectionRequest));
        }

        private void OnConnectionRejected(IPEndPoint ipEndPoint, ReliableUdpClient.RejectionReason reason)
        {
            if (LogConnectionRejections)
                Logger.LogError($"[{nameof(OnConnected)}] New connection from '{ipEndPoint}' rejected with reason '{reason}'.");

            if (reason == ReliableUdpClient.RejectionReason.ValidationFailure)
                TryAddToBlacklist(ipEndPoint.Address, reason: "Client connection validation failed");
        }

        internal void DropConnection(uint uid)
        {
            // NOTE: Internal udp listener is not thread safe, while this function (and class) has to be.
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
                            if (!_pendingConnections.TryRemove(senderConnectionUid, out var newConnectionData))
                            {
                                Logger.LogError($"Connection '{senderConnectionUid}' is not an active pending connection. Sometimes it may be normal considering different network issues.");
                                break;
                            }

                            // If connection already exists, we shouldn't handle 'OnAuth' message type.
                            if (!_connections.TryAdd(senderConnectionUid, newConnectionData.Token))
                            {
                                TryAddToBlacklist(senderConnectionUid, reason: $"Connection '{senderConnectionUid}' doesn't exist");
                                break;
                            }

                            // Call a connection event.
                            Connected(newConnectionData);

                            // Prepare the message for other clients.
                            _dataWriter.SeekZero();
                            _dataWriter.WriteByte((byte)ServerMessageType.OnJoined);
                            _dataWriter.WritePackedUInt32(senderConnectionUid);
                            _dataWriter.WritePackedUInt32((uint)_connections.Count);
                            foreach (var existedConnection in _connections.Keys)
                                _dataWriter.WritePackedUInt32(existedConnection);

                            // Data to send back.
                            var dataToSend = _dataWriter.AsArraySegment();

                            // Send back to all (including sender).
                            foreach (var existedConnection in _connections.Keys)
                                _udpListener.SendTo(existedConnection, dataToSend, UdpProtocol.DeliveryMethod.ReliableOrdered);

                            Logger.LogInfo($"[{nameof(ClientMessageType.OnAuth)}] New connection registered with UID: '{senderConnectionUid}' - '{_connections.Count}' connections now.");
                        }
                        break;

                    case ClientMessageType.OnPacket:
                        {
                            if (!_connections.ContainsKey(senderConnectionUid))
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
                            _dataWriter.WriteByte((byte)ServerMessageType.OnPacket);
                            _dataWriter.WritePackedUInt32(senderConnectionUid);
                            _dataWriter.WritePackedUInt32(userDataOpCode);
                            _dataWriter.WriteBytesAndSize(userDataPayload);

                            // Data to send back.
                            var dataToSend = _dataWriter.AsArraySegment();

                            // Send to others.
                            foreach (var existedConnection in _connections.Keys)
                            {
                                // Should be sent:
                                // 1. userDataReceiversCount == 0 - send to everyone, except sender.
                                // 2. userDataReceiversCount > 0 - collection explicitly defined, send to all valid connections that are present in _routingConnectionsCache collection.
                                var shouldBeSent =
                                    userDataReceiversCount == 0 && existedConnection != senderConnectionUid ||
                                    userDataReceiversCount > 0 && _routingConnectionsCache.Contains(existedConnection);

                                if (shouldBeSent)
                                    _udpListener.SendTo(existedConnection, dataToSend, deliveryMethod);
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

            if (_udpListener.TryGetConnectionEndPoint(connectionUid, out var endPoint))
                _udpListener.AddAddressToBlacklistIfAbsent(endPoint.Address, millisecondsToAdd: BlacklistDurationMs, reason);
        }

        private void TryAddToBlacklist(IPAddress address, string reason = null)
        {
            if (BlacklistDurationMs >= 1)
                _udpListener.AddAddressToBlacklistIfAbsent(address, millisecondsToAdd: BlacklistDurationMs, reason);
        }

        private void OnDisconnected(uint disconnectedConnectionUid)
        {
            _pendingConnections.Remove(disconnectedConnectionUid, out _);

            if (_connections.Remove(disconnectedConnectionUid, out _))
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
                    _udpListener.Disconnect(connectionToDropUid);

                _udpListener.Update();
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
                // Call the event.
                Disconnected(disconnectedConnectionUid);

                // Preparing data to send to existed connections.
                _dataWriter.SeekZero();
                _dataWriter.WriteByte((byte)ServerMessageType.OnDisconnect);
                _dataWriter.WritePackedUInt32(disconnectedConnectionUid);

                // Data to send to existed connections.
                var dataToSend = _dataWriter.AsArraySegment();

                // Send to all existed connections.
                foreach (var existedConnection in _connections.Keys)
                    _udpListener.SendTo(existedConnection, dataToSend, UdpProtocol.DeliveryMethod.ReliableOrdered);

                Logger.LogInfo($"[{nameof(OnDisconnected)}] '{disconnectedConnectionUid}' connection disconnected - '{_connections.Count}' connections now.");
            }

            if (AutoCloseOnLastDisconnect && _connections.IsEmpty)
                Stop();
        }

        protected override void OnStop()
        {
            base.OnStop();

            _udpListener.Dispose();
            Stopped();
        }
    }
}