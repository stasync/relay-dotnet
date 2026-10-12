using Keel.Networking;
using Keel.Utils.Debug;
using Keel.Utils.Threading;
using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;

namespace UdpRelay
{
    internal sealed class HttpRequestHandler
    {
        internal delegate void OpenRoomDelegate(RoomSettings settings);

        internal OpenRoomDelegate OpenRoom { get; init; }

        /// <summary>
        /// Remote services requested a simple api status probe.
        /// Could be used for (relatively) frequent checks if services is available with a minimal response payload.
        /// Unlike the other methods that could be technically used to check the status, this one returns a small payload holding only basic thus information consuming less data.
        /// </summary>
        internal Task<string> HandleApiStatusRequest(HttpListenerContext context)
        {
            // Make a response.
            var successResponseData = new
            {
                status = "success",
                pid = Environment.ProcessId,
                timestamp = DateTime.UtcNow,
                method = context.Request.HttpMethod,
            };

            var successResponseJson = JsonSerializer.Serialize(successResponseData);
            Logger.LogInfo($"   Response: '{successResponseJson}'.");

            return Task.FromResult(successResponseJson);
        }

        /// <summary>
        /// Remote service requested a new room to be opened.
        /// </summary>
        internal Task<string> HandleNewRoomRequest(HttpListenerContext context)
        {
            // Handling request such as: GET http://127.0.0.1:8080/api/room/new/?config={}
            var queryParameters = System.Web.HttpUtility.ParseQueryString(context.Request.Url!.Query);

            var configJson = queryParameters["config"];
            RoomSettings settings;
            if (string.IsNullOrWhiteSpace(configJson))
            {
                // Config is not specified within the request - create a default one.
                settings = new RoomSettings
                {
                    TargetPort = Utils.GetAvailableUdpPort()
                };
            }
            else
            {
                // Config is specified, trying to parse it.
                settings = JsonSerializer.Deserialize<RoomSettings>(configJson);

                // The code below has the same logic as the socket api.
                // 1. Port not specified (0), caller doesn't care about the port, first available port will be used.
                // 2. Port is specified (!=0), caller do care about the port - making sure its valid and throw otherwise.

                // If target port is not specified, take first available.
                if (settings.TargetPort == 0)
                    settings.TargetPort = Utils.GetAvailableUdpPort();
                else
                {
                    // Port is specified - making sure its valid.
                    // Check if port ranges.
                    if (settings.TargetPort is < 0 or > ushort.MaxValue)
                        throw new HttpRequestException($"Port {settings.TargetPort} is invalid.");

                    // Check if port is available. It's not exactly clear how this app will be started. Port might be unavailable by the time we got here.
                    // NOTE: this scenario is not highly possible.
                    if (!Utils.IsUdpPortAvailable(settings.TargetPort))
                        throw new HttpRequestException($"Port {settings.TargetPort} is not (or no longer) available at this machine.");
                }
            }

            if (string.IsNullOrWhiteSpace(settings.Name))
                settings.Name = $"Room_{settings.TargetPort}";

            Logger.LogInfo($"   Opening a room with settings: {JsonSerializer.Serialize(settings)}");

            // Open the room.
            OpenRoom(settings);

            // Make a response.
            var successResponseData = new
            {
                status = "success",
                timestamp = DateTime.UtcNow,
                method = context.Request.HttpMethod,
                room = new
                {
                    name = settings.Name,
                    port = settings.TargetPort
                }
            };

            var successResponseJson = JsonSerializer.Serialize(successResponseData);
            Logger.LogInfo($"   Response: '{successResponseJson}'.");

            return Task.FromResult(successResponseJson);
        }

        /// <summary>
        /// Remote service requested to close the room on a specific port, or every room (if port is not specified).
        /// </summary>
        internal Task<string> HandleCloseRoomRequest(HttpListenerContext context)
        {
            // Handling request such as: GET http://127.0.0.1:8080/api/room/close/?port=<number>
            var queryParameters = System.Web.HttpUtility.ParseQueryString(context.Request.Url!.Query);
            var portParameter = queryParameters["port"];

            var closedRoomsInfo = new List<object>();
            if (string.IsNullOrWhiteSpace(portParameter))
            {
                foreach (var worker in Worker.All)
                {
                    if (worker is not Room room)
                        continue;

                    closedRoomsInfo.Add(new
                    {
                        name = room.Name,
                        port = room.Port
                    });
                    room.Stop();
                }
            }
            else
            {
                var portToClose = int.Parse(portParameter);
                foreach (var worker in Worker.All)
                {
                    if (worker is not Room room || portToClose > 0 && room.Port != portToClose)
                        continue;

                    closedRoomsInfo.Add(new
                    {
                        name = room.Name,
                        port = room.Port
                    });
                    room.Stop();
                }
            }

            // Make a response.
            var successResponseData = new
            {
                status = "success",
                timestamp = DateTime.UtcNow,
                method = context.Request.HttpMethod,
                closedRooms = closedRoomsInfo.ToArray()
            };

            var successResponseJson = JsonSerializer.Serialize(successResponseData);
            Logger.LogInfo($"   Response: '{successResponseJson}'.");

            return Task.FromResult(successResponseJson);
        }

        /// <summary>
        /// Remote services requested node information: memory use and running workers.
        /// </summary>
        internal Task<string> HandleNodeInfoRequest(HttpListenerContext context)
        {
            var gcMemory = GC.GetTotalMemory(forceFullCollection: false);
            var workingSet = System.Diagnostics.Process.GetCurrentProcess().WorkingSet64;

            Logger.LogInfo($"   GC.GetTotalMemory: {gcMemory / 1024f / 1024f:f2} MB");
            Logger.LogInfo($"   Process WorkingSet: {workingSet / 1024f / 1024f:f2} MB");
            Logger.LogInfo($"   Processor Count: {Environment.ProcessorCount}");
            Logger.LogInfo($"   Workers Count: {Worker.All.Count}");

            // Collection worker information.
            var allWorkersInformation = new List<object>(Worker.All.Count);
            foreach (var worker in Worker.All)
            {
                var workerTypeFullName = worker.GetType().FullName;
                var workerName = worker.Name;
                allWorkersInformation.Add(new
                {
                    type = workerTypeFullName,
                    name = workerName
                });
            }

            // Make a response.
            var successResponseData = new
            {
                status = "success",
                timestamp = DateTime.UtcNow,
                method = context.Request.HttpMethod,
                managedMemory = gcMemory,
                totalMemory = workingSet,
                workersCount = Worker.All.Count,
                workers = allWorkersInformation.ToArray()
            };

            var successResponseJson = JsonSerializer.Serialize(successResponseData);
            Logger.LogInfo($"   Response: '{successResponseJson}'.");

            return Task.FromResult(successResponseJson);
        }

        /// <summary>
        /// Remote services requested room status information.
        /// </summary>
        internal Task<string> HandleRoomStatusRequest(HttpListenerContext context)
        {
            // Handling request such as: GET http://127.0.0.1:8080/api/room/status/?port=<number>
            var queryParameters = System.Web.HttpUtility.ParseQueryString(context.Request.Url!.Query);
            var portParameter = queryParameters["port"];

            // Specific port was not requested.
            if (string.IsNullOrWhiteSpace(portParameter))
            {
                var roomsInformation = new List<object>(Worker.All.Count);
                foreach (var worker in Worker.All)
                {
                    if (worker is Room room)
                        roomsInformation.Add(new
                        {
                            name = room.Name,
                            port = room.Port,
                            clientsCount = room.Clients.Count,
                            clients = room.Clients.ToArray(),
                            bytesSent = room.BytesSent,
                            bytesReceived = room.BytesReceived
                        });
                }

                Logger.LogInfo($"   Rooms Count: {roomsInformation.Count}/{Worker.All.Count}");

                // Make a response.
                var successResponseData = new
                {
                    status = "success",
                    timestamp = DateTime.UtcNow,
                    method = context.Request.HttpMethod,
                    roomsCount = roomsInformation.Count,
                    rooms = roomsInformation.ToArray()
                };

                var successResponseJson = JsonSerializer.Serialize(successResponseData);
                Logger.LogInfo($"   Response: '{successResponseJson}'.");

                return Task.FromResult(successResponseJson);
            }

            // Specific port was requested - looking for the target room.
            var targetPort = int.Parse(portParameter);

            Logger.LogInfo($"   Looking for room with port '{targetPort}'.");

            // Looking for the target room.
            var targetRoom = default(Room);
            foreach (var worker in Worker.All)
            {
                if (worker is not Room room || room.Port != targetPort)
                    continue;

                targetRoom = room;
                break;
            }

            // Throw if the room was not found.
            if (targetRoom == null)
                throw new HttpRequestException($"Unable to find room with port {targetPort}.");

            // Success response.
            {
                var successResponseData = new
                {
                    status = "success",
                    timestamp = DateTime.UtcNow,
                    method = context.Request.HttpMethod,
                    room = new
                    {
                        name = targetRoom.Name,
                        port = targetRoom.Port,
                        clientsCount = targetRoom.Clients.Count,
                        clients = targetRoom.Clients.ToArray(),
                        bytesSent = targetRoom.BytesSent,
                        bytesReceived = targetRoom.BytesReceived
                    }
                };

                var successResponseJson = JsonSerializer.Serialize(successResponseData);
                Logger.LogInfo($"   Response: '{successResponseJson}'.");

                return Task.FromResult(successResponseJson);
            }
        }

        /// <summary>
        /// Remote services requested to check whether a client with a specific id is in a room.
        /// </summary>
        internal Task<string> HandleHasClientRequest(HttpListenerContext context)
        {
            // Handling request such as: GET http://127.0.0.1:8080/api/room/has_client/?port=<number>&uid=<number>
            var queryParameters = System.Web.HttpUtility.ParseQueryString(context.Request.Url!.Query);
            var portParameter = queryParameters["port"];
            var clientUidParameter = queryParameters["uid"];

            if (string.IsNullOrWhiteSpace(portParameter))
                throw new HttpRequestException("Port is not specified.");

            if (string.IsNullOrWhiteSpace(clientUidParameter))
                throw new HttpRequestException("Client UID is not specified.");

            var targetPort = int.Parse(portParameter);
            if (targetPort == 0)
                throw new HttpRequestException("Target port cannot be 0.");

            var clientToCheck = uint.Parse(clientUidParameter);

            // Looking for the target room.
            var targetRoom = default(Room);
            foreach (var worker in Worker.All)
            {
                if (worker is not Room room || room.Port != targetPort)
                    continue;

                targetRoom = room;
                break;
            }

            // Throw if the room was not found.
            if (targetRoom == null)
                throw new HttpRequestException($"Unable to find room with port {targetPort}.");

            var successResponseData = new
            {
                status = "success",
                timestamp = DateTime.UtcNow,
                method = context.Request.HttpMethod,
                hasClient = targetRoom.Clients.HasClient(clientToCheck)
            };

            var successResponseJson = JsonSerializer.Serialize(successResponseData);
            Logger.LogInfo($"   Response: '{successResponseJson}'.");

            return Task.FromResult(successResponseJson);
        }

        /// <summary>
        /// Remote services requested to find a client by its id, its token, or both.
        /// </summary>
        internal Task<string> HandleGetClientRequest(HttpListenerContext context)
        {
            // Handling request such as: GET http://127.0.0.1:8080/api/room/get_client/?port=<number>&uid=<number>&token=<string>
            var queryParameters = System.Web.HttpUtility.ParseQueryString(context.Request.Url!.Query);
            var portParameter = queryParameters["port"];
            var clientUidParameter = queryParameters["uid"];
            var tokenParameter = queryParameters["token"];

            if (string.IsNullOrWhiteSpace(portParameter))
                throw new HttpRequestException("Port is not specified.");

            var targetPort = int.Parse(portParameter);
            if (targetPort == 0)
                throw new HttpRequestException("Target port cannot be 0.");

            if (string.IsNullOrWhiteSpace(clientUidParameter) && string.IsNullOrWhiteSpace(tokenParameter))
                throw new HttpRequestException("Either client uid or token has to be specified.");

            // Looking for the target room.
            var targetRoom = default(Room);
            foreach (var worker in Worker.All)
            {
                if (worker is not Room room || room.Port != targetPort)
                    continue;

                targetRoom = room;
                break;
            }

            // Throw if the room was not found.
            if (targetRoom == null)
                throw new HttpRequestException($"Unable to find room with port {targetPort}.");

            // Final filtered connection id.
            var resultClientUid = 0u;

            // 1. Uid is a primary parameter - check if it's defined.
            if (!string.IsNullOrWhiteSpace(clientUidParameter))
            {
                // Parse connection uid.
                var clientUidToCheck = uint.Parse(clientUidParameter);

                // Check if the connection actually registered.
                if (!targetRoom.Clients.HasClient(clientUidToCheck))
                    throw new HttpRequestException($"Client with id {clientUidToCheck} doesn't exist on port {targetPort}.");

                // Check if additional query parameter (requestData) is defined.
                if (!string.IsNullOrWhiteSpace(tokenParameter))
                {
                    if (targetRoom.Clients.GetClientToken(clientUidToCheck) != tokenParameter)
                        throw new InvalidOperationException($"Client {clientUidToCheck} joined with a different token.");
                }

                resultClientUid = clientUidToCheck;
            }
            // 2. Uid is not defined.
            else
            {
                // Check if the secondary parameter (request_data) is.
                if (!string.IsNullOrWhiteSpace(tokenParameter))
                {
                    // Iterate over the all connections and chek if the request data matches the one that was provided in query.
                    foreach (var clientUid in targetRoom.Clients)
                    {
                        var clientToken = targetRoom.Clients.GetClientToken(clientUid);
                        if (string.IsNullOrWhiteSpace(clientToken))
                            continue;

                        if (clientToken != tokenParameter)
                            continue;

                        resultClientUid = clientUid;
                        break;
                    }
                }
            }

            // Check if the connection actually registered.
            if (resultClientUid == 0u)
                throw new HttpRequestException($"Unable to find a matching client on port {targetPort} using query {context.Request.Url!.Query}.");

            var successResponseData = new
            {
                status = "success",
                timestamp = DateTime.UtcNow,
                method = context.Request.HttpMethod,
                clientUid = resultClientUid
            };

            var successResponseJson = JsonSerializer.Serialize(successResponseData);
            Logger.LogInfo($"   Response: '{successResponseJson}'.");

            return Task.FromResult(successResponseJson);
        }

        /// <summary>
        /// Remote services requested to drop a client from the room on a specific port.
        /// </summary>
        internal Task<string> HandleDropClientRequest(HttpListenerContext context)
        {
            // Handling request such as: GET http://127.0.0.1:8080/api/room/drop_client/?port=<number>&uid=<number>
            var queryParameters = System.Web.HttpUtility.ParseQueryString(context.Request.Url!.Query);
            var portParameter = queryParameters["port"];
            var clientUidParameter = queryParameters["uid"];

            if (string.IsNullOrWhiteSpace(portParameter))
                throw new HttpRequestException("Port is not specified.");

            if (string.IsNullOrWhiteSpace(clientUidParameter))
                throw new HttpRequestException("Client UID is not specified.");

            var targetPort = int.Parse(portParameter);
            if (targetPort == 0)
                throw new HttpRequestException("Target port cannot be 0.");

            var clientToDrop = uint.Parse(clientUidParameter);

            // Looking for the target room.
            var targetRoom = default(Room);
            foreach (var worker in Worker.All)
            {
                if (worker is not Room room || room.Port != targetPort)
                    continue;

                targetRoom = room;
                break;
            }

            // Throw if the room was not found.
            if (targetRoom == null)
                throw new HttpRequestException($"Unable to find room with port {targetPort}.");

            foreach (var clientUid in targetRoom.Clients)
            {
                if (clientUid != clientToDrop)
                    continue;

                targetRoom.DropClient(clientToDrop);

                var successResponseData = new
                {
                    status = "success",
                    timestamp = DateTime.UtcNow,
                    method = context.Request.HttpMethod
                };

                var successResponseJson = JsonSerializer.Serialize(successResponseData);
                Logger.LogInfo($"   Response: '{successResponseJson}'.");

                return Task.FromResult(successResponseJson);
            }

            // Throw if the client wasn't found.
            throw new HttpRequestException($"Unable to find client with uid {clientToDrop} on port {targetPort}.");
        }
    }
}