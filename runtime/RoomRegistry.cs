using Keel.DependencyInjection;
using Keel.Utils.Debug;
using Keel.Utils.Threading;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;

namespace UdpRelay
{
    /// <summary>
    /// The node's open rooms. Opens them, finds them by port, and sends their webhooks.
    /// </summary>
    internal sealed class RoomRegistry
    {
        [Inject] private readonly StartupArguments _startupArguments = null;
        [Inject] private readonly HttpWebhookSender _httpWebhookSender = null;

        private readonly ConcurrentDictionary<uint, Room> _rooms = new();

        internal bool IsEmpty => _rooms.IsEmpty;

        internal ICollection<Room> All => _rooms.Values;

        /// <summary>
        /// Called once a room has stopped and is no longer registered.
        /// This event will be called from the room's thread, so it has to be thread-safe.
        /// </summary>
        internal event Action<Room> RoomClosed = delegate { };

        /// <summary>
        /// Opens a new room based on the specified <param name="settings"></param>.
        /// </summary>
        internal void Open(RoomSettings settings)
        {
            var room = new Room(settings, _startupArguments.MaxClientsPerRoom, _startupArguments.ProtocolKey, _startupArguments.RoomUpdateIntervalMs);
            room.LogConnectionRejections = _startupArguments.LogConnectionRejections;
            room.BlacklistDurationMs = _startupArguments.BlacklistDurationMs;
            room.AutoCloseOnLastDisconnect = false;
            room.Stopped += () => HandleRoomStopped(room);
            room.ClientJoined += client =>
            {
                _httpWebhookSender.SendAndForget(
                    endpoint: settings.Webhooks.ClientJoined,
                    payload: new
                    {
                        roomPort = room.Port,
                        clientUid = client.Uid,
                        token = client.Token,
                        endpoint = client.EndPoint.ToString(),
                        timestamp = DateTime.UtcNow
                    });
            };

            room.ClientLeft += clientUid =>
            {
                _httpWebhookSender.SendAndForget(
                    endpoint: settings.Webhooks.ClientLeft,
                    payload: new
                    {
                        roomPort = room.Port,
                        clientUid,
                        timestamp = DateTime.UtcNow
                    });
            };

            // Register and configure the room before its thread starts, so its events can't fire before they're handled.
            _rooms.TryAdd(room.Uid, room);
            room.Start(WorkerScope.NewForegroundThread);
            Logger.LogInfo($"[{room.Name}] Listening on port: '{room.Port}'.");
        }

        internal bool TryFind(int port, out Room room)
        {
            foreach (var openRoom in _rooms.Values)
            {
                if (openRoom.Port == port)
                {
                    room = openRoom;
                    return true;
                }
            }

            room = null;
            return false;
        }

        private void HandleRoomStopped(Room room)
        {
            Logger.LogInfo($"Room '{room.Name}' was closed.");

            _rooms.TryRemove(room.Uid, out _);
            RoomClosed(room);
        }
    }
}