using Keel.DependencyInjection;
using Keel.Networking;
using Keel.Utils.Debug;
using Keel.Utils.Threading;
using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;

namespace UdpRelay
{
    internal sealed class HttpRequestHandler
    {
        [Inject] private readonly RoomRegistry _rooms = null;

        /// <summary>
        /// Binds every endpoint of the HTTP API to its handler.
        /// </summary>
        internal void Bind(HttpServer server)
        {
            server.Bind(HttpMethod.Get, template: "/api/status", HandleStatusRequest);
            server.Bind(HttpMethod.Get, template: "/api/node", HandleGetNodeRequest);
            server.Bind(HttpMethod.Get, template: "/api/rooms", HandleGetRoomsRequest);
            server.Bind(HttpMethod.Post, template: "/api/rooms", HandleOpenRoomRequestAsync);
            server.Bind(HttpMethod.Delete, template: "/api/rooms", HandleCloseRoomsRequest);
            server.Bind(HttpMethod.Get, template: "/api/rooms/{port}", HandleGetRoomRequest);
            server.Bind(HttpMethod.Delete, template: "/api/rooms/{port}", HandleCloseRoomRequest);
            server.Bind(HttpMethod.Get, template: "/api/rooms/{port}/clients", HandleGetClientsRequest);
            server.Bind(HttpMethod.Get, template: "/api/rooms/{port}/clients/{uid}", HandleGetClientRequest);
            server.Bind(HttpMethod.Delete, template: "/api/rooms/{port}/clients/{uid}", HandleDropClientRequest);
        }

        /// <summary>
        /// GET /api/status - a simple status probe.
        /// Could be used for (relatively) frequent checks if the node is available, with a minimal response payload.
        /// </summary>
        private Task<string> HandleStatusRequest(HttpListenerContext context, IReadOnlyDictionary<string, string> routeValues) =>
            Task.FromResult(ToResponseJson(new
            {
                status = "success",
                pid = Environment.ProcessId,
                timestamp = DateTime.UtcNow,
                method = context.Request.HttpMethod
            }));

        /// <summary>
        /// GET /api/node - the node's memory use and running workers.
        /// </summary>
        private Task<string> HandleGetNodeRequest(HttpListenerContext context, IReadOnlyDictionary<string, string> routeValues)
        {
            var gcMemory = GC.GetTotalMemory(forceFullCollection: false);
            var workingSet = System.Diagnostics.Process.GetCurrentProcess().WorkingSet64;

            Logger.LogInfo($"   GC.GetTotalMemory: {gcMemory / 1024f / 1024f:f2} MB");
            Logger.LogInfo($"   Process WorkingSet: {workingSet / 1024f / 1024f:f2} MB");
            Logger.LogInfo($"   Processor Count: {Environment.ProcessorCount}");
            Logger.LogInfo($"   Workers Count: {Worker.All.Count}");

            var workers = new List<object>(Worker.All.Count);
            foreach (var worker in Worker.All)
            {
                workers.Add(new
                {
                    type = worker.GetType().FullName,
                    name = worker.Name
                });
            }

            return Task.FromResult(ToResponseJson(new
            {
                status = "success",
                timestamp = DateTime.UtcNow,
                method = context.Request.HttpMethod,
                managedMemory = gcMemory,
                totalMemory = workingSet,
                workersCount = workers.Count,
                workers = workers.ToArray()
            }));
        }

        /// <summary>
        /// GET /api/rooms - every room, with its clients and traffic counters.
        /// </summary>
        private Task<string> HandleGetRoomsRequest(HttpListenerContext context, IReadOnlyDictionary<string, string> routeValues)
        {
            var rooms = new List<object>();
            foreach (var room in _rooms.All)
                rooms.Add(DescribeRoom(room));

            Logger.LogInfo($"   Rooms Count: {rooms.Count}/{Worker.All.Count}");

            return Task.FromResult(ToResponseJson(new
            {
                status = "success",
                timestamp = DateTime.UtcNow,
                method = context.Request.HttpMethod,
                roomsCount = rooms.Count,
                rooms = rooms.ToArray()
            }));
        }

        /// <summary>
        /// POST /api/rooms - opens a room. The request body holds the room settings as JSON; an empty body uses the defaults.
        /// </summary>
        private async Task<string> HandleOpenRoomRequestAsync(HttpListenerContext context, IReadOnlyDictionary<string, string> routeValues)
        {
            string body;
            using (var reader = new StreamReader(context.Request.InputStream, context.Request.ContentEncoding))
                body = await reader.ReadToEndAsync();

            RoomSettings settings;
            try
            {
                settings = string.IsNullOrWhiteSpace(body) ? null : JsonSerializer.Deserialize<RoomSettings>(body);
            }
            catch (JsonException e)
            {
                throw new HttpStatusException(HttpStatusCode.BadRequest, $"Room settings are not valid JSON: {e.Message}");
            }

            // No body (or a JSON null) - use the default settings.
            settings ??= new RoomSettings();

            // The code below has the same logic as the socket api.
            // 1. Port not specified (0), caller doesn't care about the port, first available port will be used.
            // 2. Port is specified (!=0), caller do care about the port - making sure its valid and throw otherwise.
            if (settings.TargetPort == 0)
                settings.TargetPort = Utils.GetAvailableUdpPort();
            else if (settings.TargetPort is < 0 or > ushort.MaxValue)
                throw new HttpStatusException(HttpStatusCode.BadRequest, $"Port {settings.TargetPort} is invalid.");
            // Port might be unavailable by the time we got here.
            else if (!Utils.IsUdpPortAvailable(settings.TargetPort))
                throw new HttpStatusException(HttpStatusCode.Conflict, $"Port {settings.TargetPort} is not (or no longer) available at this machine.");

            if (string.IsNullOrWhiteSpace(settings.Name))
                settings.Name = $"Room_{settings.TargetPort}";

            Logger.LogInfo($"   Opening a room with settings: {JsonSerializer.Serialize(settings)}");

            // Open the room.
            _rooms.Open(settings);

            // Reply with 201 Created, pointing at the new room.
            context.Response.StatusCode = (int)HttpStatusCode.Created;
            context.Response.AddHeader("Location", $"{context.Request.Url!.GetLeftPart(UriPartial.Path).TrimEnd('/')}/{settings.TargetPort}/");

            return ToResponseJson(new
            {
                status = "success",
                timestamp = DateTime.UtcNow,
                method = context.Request.HttpMethod,
                room = new
                {
                    name = settings.Name,
                    port = settings.TargetPort
                }
            });
        }

        /// <summary>
        /// DELETE /api/rooms - closes every room. Once the last room is closed, the node exits.
        /// </summary>
        private Task<string> HandleCloseRoomsRequest(HttpListenerContext context, IReadOnlyDictionary<string, string> routeValues)
        {
            var closedRooms = new List<object>();
            foreach (var room in _rooms.All)
            {
                closedRooms.Add(new
                {
                    name = room.Name,
                    port = room.Port
                });
                room.Stop();
            }

            return Task.FromResult(ToResponseJson(new
            {
                status = "success",
                timestamp = DateTime.UtcNow,
                method = context.Request.HttpMethod,
                closedRooms = closedRooms.ToArray()
            }));
        }

        /// <summary>
        /// GET /api/rooms/{port} - one room, with its clients and traffic counters.
        /// </summary>
        private Task<string> HandleGetRoomRequest(HttpListenerContext context, IReadOnlyDictionary<string, string> routeValues)
        {
            var room = FindRoom(routeValues);

            return Task.FromResult(ToResponseJson(new
            {
                status = "success",
                timestamp = DateTime.UtcNow,
                method = context.Request.HttpMethod,
                room = DescribeRoom(room)
            }));
        }

        /// <summary>
        /// DELETE /api/rooms/{port} - closes one room. Once the last room is closed, the node exits.
        /// </summary>
        private Task<string> HandleCloseRoomRequest(HttpListenerContext context, IReadOnlyDictionary<string, string> routeValues)
        {
            var room = FindRoom(routeValues);
            room.Stop();

            return Task.FromResult(ToResponseJson(new
            {
                status = "success",
                timestamp = DateTime.UtcNow,
                method = context.Request.HttpMethod,
                room = new
                {
                    name = room.Name,
                    port = room.Port
                }
            }));
        }

        /// <summary>
        /// GET /api/rooms/{port}/clients?token=... - the clients in a room.
        /// With a token, only the client that joined with that token (if any) is returned.
        /// </summary>
        private Task<string> HandleGetClientsRequest(HttpListenerContext context, IReadOnlyDictionary<string, string> routeValues)
        {
            var room = FindRoom(routeValues);
            var tokenParameter = System.Web.HttpUtility.ParseQueryString(context.Request.Url!.Query)["token"];

            var clients = new List<object>();
            foreach (var clientUid in room.Clients)
            {
                // The client might have left in the meantime.
                if (!room.Clients.TryGetClientToken(clientUid, out var clientToken))
                    continue;

                if (tokenParameter != null && clientToken != tokenParameter)
                    continue;

                clients.Add(DescribeClient(clientUid, clientToken));
            }

            return Task.FromResult(ToResponseJson(new
            {
                status = "success",
                timestamp = DateTime.UtcNow,
                method = context.Request.HttpMethod,
                clientsCount = clients.Count,
                clients = clients.ToArray()
            }));
        }

        /// <summary>
        /// GET /api/rooms/{port}/clients/{uid} - one client in a room.
        /// </summary>
        private Task<string> HandleGetClientRequest(HttpListenerContext context, IReadOnlyDictionary<string, string> routeValues)
        {
            var room = FindRoom(routeValues);
            var clientUid = ParseClientUid(routeValues["uid"]);
            if (!room.Clients.TryGetClientToken(clientUid, out var clientToken))
                throw new HttpStatusException(HttpStatusCode.NotFound, $"Client {clientUid} is not in the room with port {room.Port}.");

            return Task.FromResult(ToResponseJson(new
            {
                status = "success",
                timestamp = DateTime.UtcNow,
                method = context.Request.HttpMethod,
                client = DescribeClient(clientUid, clientToken)
            }));
        }

        /// <summary>
        /// DELETE /api/rooms/{port}/clients/{uid} - drops a client from a room.
        /// The room disconnects the client on its next update.
        /// </summary>
        private Task<string> HandleDropClientRequest(HttpListenerContext context, IReadOnlyDictionary<string, string> routeValues)
        {
            var room = FindRoom(routeValues);
            var clientUid = ParseClientUid(routeValues["uid"]);
            if (!room.Clients.TryGetClientToken(clientUid, out _))
                throw new HttpStatusException(HttpStatusCode.NotFound, $"Client {clientUid} is not in the room with port {room.Port}.");

            room.DropClient(clientUid);

            return Task.FromResult(ToResponseJson(new
            {
                status = "success",
                timestamp = DateTime.UtcNow,
                method = context.Request.HttpMethod
            }));
        }

        /// <summary>
        /// Finds the room addressed by the "port" route value, or replies with 400 (invalid port) or 404 (no such room).
        /// </summary>
        private Room FindRoom(IReadOnlyDictionary<string, string> routeValues)
        {
            var portParameter = routeValues["port"];
            if (!int.TryParse(portParameter, out var port) || port is < 1 or > ushort.MaxValue)
                throw new HttpStatusException(HttpStatusCode.BadRequest, $"'{portParameter}' is not a valid port.");

            return _rooms.TryFind(port, out var room)
                ? room
                : throw new HttpStatusException(HttpStatusCode.NotFound, $"Unable to find room with port {port}.");
        }

        private static uint ParseClientUid(string clientUidParameter) =>
            uint.TryParse(clientUidParameter, out var clientUid)
                ? clientUid
                : throw new HttpStatusException(HttpStatusCode.BadRequest, $"'{clientUidParameter}' is not a valid client UID.");

        private static object DescribeRoom(Room room) => new
        {
            name = room.Name,
            port = room.Port,
            clientsCount = room.Clients.Count,
            clients = room.Clients.ToArray(),
            bytesSent = room.BytesSent,
            bytesReceived = room.BytesReceived
        };

        private static object DescribeClient(uint clientUid, string token) => new
        {
            uid = clientUid,
            token
        };

        private static string ToResponseJson(object responseData)
        {
            var responseJson = JsonSerializer.Serialize(responseData);
            Logger.LogInfo($"   Response: '{responseJson}'.");
            return responseJson;
        }
    }
}