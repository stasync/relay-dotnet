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
        internal delegate void CreateRelayInstanceDelegate(RelayServerStartupData startupData);

        internal CreateRelayInstanceDelegate CreateRelayInstance { get; init; }

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
        /// Remote service requested a new relay to be allocated.
        /// </summary>
        internal Task<string> HandleNewRelayInstanceRequest(HttpListenerContext context)
        {
            // Handling request such as: GET http://127.0.0.1:8080/api/relay/new/?config={}
            var queryParameters = System.Web.HttpUtility.ParseQueryString(context.Request.Url!.Query);

            var configJson = queryParameters["config"];
            RelayServerStartupData startupData;
            if (string.IsNullOrWhiteSpace(configJson))
            {
                // Config is not specified within the request - create a default one.
                startupData = new RelayServerStartupData
                {
                    TargetPort = Utils.GetAvailableUdpPort()
                };
            }
            else
            {
                // Config is specified, trying to parse it.
                startupData = JsonSerializer.Deserialize<RelayServerStartupData>(configJson);

                // The code below has the same logic as the socket api.
                // 1. Port not specified (0), caller doesn't care about the port, first available port will be used.
                // 2. Port is specified (!=0), caller do care about the port - making sure its valid and throw otherwise.

                // If target port is not specified, take first available.
                if (startupData.TargetPort == 0)
                    startupData.TargetPort = Utils.GetAvailableUdpPort();
                else
                {
                    // Port is specified - making sure its valid.
                    // Check if port ranges.
                    if (startupData.TargetPort is < 0 or > ushort.MaxValue)
                        throw new HttpRequestException($"Port {startupData.TargetPort} is invalid.");

                    // Check if port is available. It's not exactly clear how this app will be started. Port might be unavailable by the time we got here.
                    // NOTE: this scenario is not highly possible.
                    if (!Utils.IsUdpPortAvailable(startupData.TargetPort))
                        throw new HttpRequestException($"Port {startupData.TargetPort} is not (or no longer) available at this machine.");
                }
            }

            if (string.IsNullOrWhiteSpace(startupData.WorkerName))
                startupData.WorkerName = $"RelayThread_{startupData.TargetPort}";

            Logger.LogInfo($"   Creating new relay instance with config: {JsonSerializer.Serialize(startupData)}");

            // Create an actual relay instance.
            CreateRelayInstance(startupData);

            // Make a response.
            var successResponseData = new
            {
                status = "success",
                timestamp = DateTime.UtcNow,
                method = context.Request.HttpMethod,
                relayPort = startupData.TargetPort,
                workerName = startupData.WorkerName
            };

            var successResponseJson = JsonSerializer.Serialize(successResponseData);
            Logger.LogInfo($"   Response: '{successResponseJson}'.");

            return Task.FromResult(successResponseJson);
        }

        /// <summary>
        /// Remote service requested to shut down a relay with the specific port, or all currently active relays (if port is not specified).
        /// </summary>
        internal Task<string> HandleShutdownRelayRequest(HttpListenerContext context)
        {
            // Handling request such as: GET http://127.0.0.1:8080/api/relay/shutdown/?port=<number>
            var queryParameters = System.Web.HttpUtility.ParseQueryString(context.Request.Url!.Query);
            var portParameter = queryParameters["port"];

            var closedRelaysInfo = new List<object>();
            if (string.IsNullOrWhiteSpace(portParameter))
            {
                foreach (var worker in Worker.All)
                {
                    if (worker is not RelayServer relay)
                        continue;

                    closedRelaysInfo.Add(new
                    {
                        name = relay.Name,
                        port = relay.Port
                    });
                    relay.Stop();
                }
            }
            else
            {
                var portToShutDown = int.Parse(portParameter);
                foreach (var worker in Worker.All)
                {
                    if (worker is not RelayServer relay || portToShutDown > 0 && relay.Port != portToShutDown)
                        continue;

                    closedRelaysInfo.Add(new
                    {
                        name = relay.Name,
                        port = relay.Port
                    });
                    relay.Stop();
                }
            }

            // Make a response.
            var successResponseData = new
            {
                status = "success",
                timestamp = DateTime.UtcNow,
                method = context.Request.HttpMethod,
                closedRelays = closedRelaysInfo.ToArray()
            };

            var successResponseJson = JsonSerializer.Serialize(successResponseData);
            Logger.LogInfo($"   Response: '{successResponseJson}'.");

            return Task.FromResult(successResponseJson);
        }

        /// <summary>
        /// Remote services requested process system information.
        /// </summary>
        internal Task<string> HandleSystemInfoRequest(HttpListenerContext context)
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
        /// Remote services requested relay status information.
        /// </summary>
        internal Task<string> HandleStatusInfoRequest(HttpListenerContext context)
        {
            // Handling request such as: GET http://127.0.0.1:8080/api/relay/status/?port=<number>
            var queryParameters = System.Web.HttpUtility.ParseQueryString(context.Request.Url!.Query);
            var portParameter = queryParameters["port"];

            // Specific port was not requested.
            if (string.IsNullOrWhiteSpace(portParameter))
            {
                var relayWorkersInformation = new List<object>(Worker.All.Count);
                foreach (var worker in Worker.All)
                {
                    if (worker is RelayServer relay)
                        relayWorkersInformation.Add(new
                        {
                            name = relay.Name,
                            port = relay.Port,
                            connectionsCount = relay.Connections.Count,
                            connections = relay.Connections.ToArray(),
                            bytesSent = relay.BytesSent,
                            bytesReceived = relay.BytesReceived
                        });
                }

                Logger.LogInfo($"   Relay Workers Count: {relayWorkersInformation.Count}/{Worker.All.Count}");

                // Make a response.
                var successResponseData = new
                {
                    status = "success",
                    timestamp = DateTime.UtcNow,
                    method = context.Request.HttpMethod,
                    relayWorkersCount = relayWorkersInformation.Count,
                    relayWorkers = relayWorkersInformation.ToArray()
                };

                var successResponseJson = JsonSerializer.Serialize(successResponseData);
                Logger.LogInfo($"   Response: '{successResponseJson}'.");

                return Task.FromResult(successResponseJson);
            }

            // Specific port was requested - looking for target relay.
            var targetPort = int.Parse(portParameter);

            Logger.LogInfo($"   Looking for relay with port '{targetPort}'.");

            // Looking for a target relay server.
            var targetRelayWorker = default(RelayServer);
            foreach (var worker in Worker.All)
            {
                if (worker is not RelayServer relay || relay.Port != targetPort)
                    continue;

                targetRelayWorker = relay;
                break;
            }

            // Throw if worker was not found.
            if (targetRelayWorker == null)
                throw new HttpRequestException($"Unable to find relay worker with port {targetPort}.");

            // Success response.
            {
                var successResponseData = new
                {
                    status = "success",
                    timestamp = DateTime.UtcNow,
                    method = context.Request.HttpMethod,
                    relayWorker = new
                    {
                        name = targetRelayWorker.Name,
                        port = targetRelayWorker.Port,
                        connectionsCount = targetRelayWorker.Connections.Count,
                        connections = targetRelayWorker.Connections.ToArray(),
                        bytesSent = targetRelayWorker.BytesSent,
                        bytesReceived = targetRelayWorker.BytesReceived
                    }
                };

                var successResponseJson = JsonSerializer.Serialize(successResponseData);
                Logger.LogInfo($"   Response: '{successResponseJson}'.");

                return Task.FromResult(successResponseJson);
            }
        }

        /// <summary>
        /// Remote services requested to check if the connection with a specific id is connected.
        /// </summary>
        internal Task<string> HandleCheckRelayConnectionRequest(HttpListenerContext context)
        {
            // Handling request such as: GET http://127.0.0.1:8080/api/relay/has_connection/?port=<number>&uid=<number>
            var queryParameters = System.Web.HttpUtility.ParseQueryString(context.Request.Url!.Query);
            var portParameter = queryParameters["port"];
            var connectionUidParameter = queryParameters["uid"];

            if (string.IsNullOrWhiteSpace(portParameter))
                throw new HttpRequestException("Port is not specified.");

            if (string.IsNullOrWhiteSpace(connectionUidParameter))
                throw new HttpRequestException("Connection UID is not specified.");

            var targetPort = int.Parse(portParameter);
            if (targetPort == 0)
                throw new HttpRequestException("Target port cannot be 0.");

            var connectionToCheck = uint.Parse(connectionUidParameter);

            // Looking for a target relay server.
            var targetRelayWorker = default(RelayServer);
            foreach (var worker in Worker.All)
            {
                if (worker is not RelayServer relay || relay.Port != targetPort)
                    continue;

                targetRelayWorker = relay;
                break;
            }

            // Throw if worker was not found.
            if (targetRelayWorker == null)
                throw new HttpRequestException($"Unable to find relay worker with port {targetPort}.");

            var successResponseData = new
            {
                status = "success",
                timestamp = DateTime.UtcNow,
                method = context.Request.HttpMethod,
                hasConnection = targetRelayWorker.Connections.HasConnection(connectionToCheck)
            };

            var successResponseJson = JsonSerializer.Serialize(successResponseData);
            Logger.LogInfo($"   Response: '{successResponseJson}'.");

            return Task.FromResult(successResponseJson);
        }

        /// <summary>
        /// Remote services requested to get a connection data with a specific id.
        /// </summary>
        internal Task<string> HandleGetRelayConnectionDataRequest(HttpListenerContext context)
        {
            // Handling request such as: GET http://127.0.0.1:8080/api/relay/get_connection_data/?port=<number>&uid=<number>&token=<string>
            var queryParameters = System.Web.HttpUtility.ParseQueryString(context.Request.Url!.Query);
            var portParameter = queryParameters["port"];
            var connectionUidParameter = queryParameters["uid"];
            var connectionTokenParameter = queryParameters["token"];

            if (string.IsNullOrWhiteSpace(portParameter))
                throw new HttpRequestException("Port is not specified.");

            var targetPort = int.Parse(portParameter);
            if (targetPort == 0)
                throw new HttpRequestException("Target port cannot be 0.");

            if (string.IsNullOrWhiteSpace(connectionUidParameter) && string.IsNullOrWhiteSpace(connectionTokenParameter))
                throw new HttpRequestException("Either connection uid of token has to be specified.");

            // Looking for a target relay server.
            var targetRelayWorker = default(RelayServer);
            foreach (var worker in Worker.All)
            {
                if (worker is not RelayServer relay || relay.Port != targetPort)
                    continue;

                targetRelayWorker = relay;
                break;
            }

            // Throw if worker was not found.
            if (targetRelayWorker == null)
                throw new HttpRequestException($"Unable to find relay worker with port {targetPort}.");

            // Final filtered connection id.
            var resultConnectionUid = 0u;

            // 1. Uid is a primary parameter - check if it's defined.
            if (!string.IsNullOrWhiteSpace(connectionUidParameter))
            {
                // Parse connection uid.
                var connectionUidToCheck = uint.Parse(connectionUidParameter);

                // Check if the connection actually registered.
                if (!targetRelayWorker.Connections.HasConnection(connectionUidToCheck))
                    throw new HttpRequestException($"Connection with id {connectionUidToCheck} doesn't exist on port {targetPort}.");

                // Check if additional query parameter (requestData) is defined.
                if (!string.IsNullOrWhiteSpace(connectionTokenParameter))
                {
                    if (targetRelayWorker.Connections.GetConnectionToken(connectionUidToCheck) != connectionTokenParameter)
                        throw new InvalidOperationException($"Connection {connectionUidToCheck} data doesn't match the requested parameter {connectionTokenParameter}.");
                }

                resultConnectionUid = connectionUidToCheck;
            }
            // 2. Uid is not defined.
            else
            {
                // Check if the secondary parameter (request_data) is.
                if (!string.IsNullOrWhiteSpace(connectionTokenParameter))
                {
                    // Iterate over the all connections and chek if the request data matches the one that was provided in query.
                    foreach (var connectionUid in targetRelayWorker.Connections)
                    {
                        var connectionToken = targetRelayWorker.Connections.GetConnectionToken(connectionUid);
                        if (string.IsNullOrWhiteSpace(connectionToken))
                            continue;

                        if (connectionToken != connectionTokenParameter)
                            continue;

                        resultConnectionUid = connectionUid;
                        break;
                    }
                }
            }

            // Check if the connection actually registered.
            if (resultConnectionUid == 0u)
                throw new HttpRequestException($"Unable to find applicable connection on port {targetPort} using query {context.Request.Url!.Query}.");

            var successResponseData = new
            {
                status = "success",
                timestamp = DateTime.UtcNow,
                method = context.Request.HttpMethod,
                connectionUid = resultConnectionUid
            };

            var successResponseJson = JsonSerializer.Serialize(successResponseData);
            Logger.LogInfo($"   Response: '{successResponseJson}'.");

            return Task.FromResult(successResponseJson);
        }

        /// <summary>
        /// Remote services requested to drop a specific connection on a specific port.
        /// </summary>
        internal Task<string> HandleDropRelayConnectionRequest(HttpListenerContext context)
        {
            // Handling request such as: GET http://127.0.0.1:8080/api/relay/drop_connection/?port=<number>&uid=<number>
            var queryParameters = System.Web.HttpUtility.ParseQueryString(context.Request.Url!.Query);
            var portParameter = queryParameters["port"];
            var connectionUidParameter = queryParameters["uid"];

            if (string.IsNullOrWhiteSpace(portParameter))
                throw new HttpRequestException("Port is not specified.");

            if (string.IsNullOrWhiteSpace(connectionUidParameter))
                throw new HttpRequestException("Connection UID is not specified.");

            var targetPort = int.Parse(portParameter);
            if (targetPort == 0)
                throw new HttpRequestException("Target port cannot be 0.");

            var connectionToDrop = uint.Parse(connectionUidParameter);

            // Looking for a target relay server.
            var targetRelayWorker = default(RelayServer);
            foreach (var worker in Worker.All)
            {
                if (worker is not RelayServer relay || relay.Port != targetPort)
                    continue;

                targetRelayWorker = relay;
                break;
            }

            // Throw if worker was not found.
            if (targetRelayWorker == null)
                throw new HttpRequestException($"Unable to find relay worker with port {targetPort}.");

            foreach (var connUid in targetRelayWorker.Connections)
            {
                if (connUid != connectionToDrop)
                    continue;

                targetRelayWorker.DropConnection(connectionToDrop);

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

            // Throw if connection wasn't found.
            throw new HttpRequestException($"Unable to find connection with uid {connectionToDrop} on port {targetPort}.");
        }
    }
}