using Keel.Networking;
using Keel.Utils;
using Keel.Utils.Debug;
using Keel.Utils.Threading;
using System;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;

namespace UdpRelay
{
    internal static class Entry
    {
        private static readonly ConcurrentDictionary<uint, RelayServer> s_servers = new();
        private static readonly ConcurrentBag<PosixSignalRegistration> s_posixSignalRegistry = new();
        private static HttpServer s_httpServer;
        private static LogWorker s_logOutputWorker;
        private static HttpRequestHandler s_httpRequestHandler;
        private static StartupArguments s_startupArguments;
        private static HttpWebhookSender s_httpWebhookSender;

        // TODO: right now the entire application will be shut down as soon as the last relay port is closed.
        // TODO: see HandleInstanceStop - right now its the part of application shutdown logic.
        // TODO: this should be configurable.

        private static void Main(string[] args)
        {
            // First thing to be done - set a fake output to collect logs during the config parsing stage.
            // It's only possible to initialize the actual log worker once the config is parsed.
            var temporaryLogOutput = new Logger.QueueOutput();
            Logger.SetCustomOutput(temporaryLogOutput);

            Logger.LogInfo($"Startup path: '{DomainUtils.DomainDirectory}{string.Join(" ", args)}'.");
            Logger.LogInfo($"Platform: '{DomainUtils.Platform}'.");
            Logger.LogInfo($"PID: '{Environment.ProcessId}'.");
            Logger.LogInfo($"Environment: '{Environment.UserName}'.");

            s_startupArguments = LaunchArguments.CreateInstance<StartupArguments>();
            Logger.LogInfo(s_startupArguments.ToString());

            // Create a new (persistent) log output.
            s_logOutputWorker = LogWorker.BuildAndStart(WorkerScope.NewForegroundThread, s_startupArguments.LogOutputRelativePath);
            Logger.SetCustomOutput(s_logOutputWorker);

            // Copy temporary output to the persistent.
            temporaryLogOutput.FlushTo(s_logOutputWorker);

            // Now update a log level from the config.
            Logger.SetLogLevel(s_startupArguments.LogLevel);

            // Create relay instances.
            // Normally it should only be used for debugging, when its required to have one or a few relay instances (threads) running by default.
            // Such instances are unsafe as they don't use token based connection validation.
            if (s_startupArguments.RelayDefaultPorts.Length > 0)
            {
                Logger.LogInfo($"[Relay] Requested '{s_startupArguments.RelayDefaultPorts.Length}' default port(s) to be opened.");

                foreach (var requestedPort in s_startupArguments.RelayDefaultPorts)
                {
                    Logger.LogInfo($"[Relay] Trying to create relay on port: {requestedPort}.");

                    // The code below has the same logic as the socket api.
                    // 1. Port not specified (0), caller doesn't care about the port, first available port will be used.
                    // 2. Port is specified (!=0), caller DO care about the port - do nothing here, port will be validated upon creation.
                    var targetPort = requestedPort;
                    if (targetPort == 0)
                    {
                        targetPort = Utils.GetAvailableUdpPort();
                        Logger.LogWarning($"    Requested port is set to '0' thus '{targetPort}' will be used instead (as the one available).");
                    }
                    else
                    {
                        if (requestedPort is < 0 or > ushort.MaxValue)
                        {
                            Logger.LogError($"  Requested port '{requestedPort}' is not in a valid port range.");
                            continue;
                        }

                        if (!Utils.IsUdpPortAvailable(requestedPort))
                        {
                            Logger.LogError($"  Requested port '{requestedPort}' is not available.");
                            continue;
                        }
                    }

                    CreateRelayInstance(new RelayServerStartupData
                    {
                        WorkerName = $"RelayThread_{targetPort}",
                        TargetPort = targetPort,
                        ConnectionKey = string.Empty
                    });
                }
            }

            // https://learn.microsoft.com/en-us/dotnet/core/compatibility/core-libraries/10.0/sigterm-signal-handler
            // Intercept SIGINT.
            s_posixSignalRegistry.Add(PosixSignalRegistration.Create(PosixSignal.SIGINT,
                posixSignalContext =>
                {
                    Logger.LogInfo($"ProcessExit: Received {PosixSignal.SIGINT}.");

                    posixSignalContext.Cancel = true;

                    // Stop all instances.
                    if (s_servers.IsEmpty)
                        FinalizeApplication();
                    else
                    {
                        foreach (var relayServer in s_servers.Values)
                            relayServer.Stop();
                    }
                }));

            // Intercept SIGTERM.
            s_posixSignalRegistry.Add(PosixSignalRegistration.Create(PosixSignal.SIGTERM,
                posixSignalContext =>
                {
                    Logger.LogInfo($"ProcessExit: Received {PosixSignal.SIGTERM}.");

                    posixSignalContext.Cancel = true;

                    // Stop all instances.
                    if (s_servers.IsEmpty)
                        FinalizeApplication();
                    else
                    {
                        foreach (var relayServer in s_servers.Values)
                            relayServer.Stop();
                    }
                }));

            // Create and start the HTTP server
            try
            {
                // Create a handle for HTTP requests.
                s_httpRequestHandler = new HttpRequestHandler
                {
                    CreateRelayInstance = CreateRelayInstance
                };

                // If port equals 0, it's a valid case where a 1st available port should be picked.
                if (Utils.IsValidUrl(s_startupArguments.HttpListenerUrl))
                {
                    // Create an http webhook sender.
                    // We only create it if IPC is initialized (HttpServer), otherwise it's just useless.
                    s_httpWebhookSender = new HttpWebhookSender();

                    // Create an http server to maintain IPC.
                    s_httpServer = new HttpServer(url: s_startupArguments.HttpListenerUrl);
                    s_httpServer.Bind(api: "/api/status/", s_httpRequestHandler.HandleApiStatusRequest);
                    s_httpServer.Bind(api: "/api/relay/new/", s_httpRequestHandler.HandleNewRelayInstanceRequest);
                    s_httpServer.Bind(api: "/api/relay/shutdown/", s_httpRequestHandler.HandleShutdownRelayRequest);
                    s_httpServer.Bind(api: "/api/relay/status/", s_httpRequestHandler.HandleStatusInfoRequest);
                    s_httpServer.Bind(api: "/api/relay/has_connection/", s_httpRequestHandler.HandleCheckRelayConnectionRequest);
                    s_httpServer.Bind(api: "/api/relay/get_connection/", s_httpRequestHandler.HandleGetRelayConnectionDataRequest);
                    s_httpServer.Bind(api: "/api/relay/drop_connection/", s_httpRequestHandler.HandleDropRelayConnectionRequest);
                    s_httpServer.Bind(api: "/api/system/info/", s_httpRequestHandler.HandleSystemInfoRequest);
                    s_httpServer.StartBlocking();
                }
                else
                    Logger.LogWarning($"Unable to initialize IPC ({nameof(HttpServer)} and {nameof(HttpWebhookSender)}) as external startup URL parameter is invalid: '{s_startupArguments.HttpListenerUrl}'.");
            }
            catch (Exception e)
            {
                Logger.LogException(e);
            }
        }

        /// <summary>
        /// Handle stopped event to dispose exit signal interceptors.
        /// This event will be called from the worker thread, so it has to be thread-safe.
        /// </summary>
        private static void HandleInstanceStop(RelayServer server)
        {
            Logger.LogInfo($"Relay server thread '{server.Name}' was terminated.");

            // Remove registered instances and check if any other instances left.
            s_servers.TryRemove(server.Uid, out _);
            if (s_servers.IsEmpty)
            {
                Logger.LogInfo("[SHUTDOWN] Last relay instance was stopped. Application will be terminated.");
                FinalizeApplication();
            }
        }

        /// <summary>
        /// If there are no instances actively running, this function will exit the application (with code 0) as soon as the logging thread has been shut down.
        /// Function will throw <see cref="InvalidOperationException"/> if at least 1 relay worker in active.
        /// </summary>
        private static void FinalizeApplication()
        {
            if (!s_servers.IsEmpty)
                throw new InvalidOperationException("Application can only be finalized when all relay threads are stopped.");

            // Stop http sever.
            s_httpServer?.Stop();
            s_httpServer = null;

            // Now we will be able to close the process once the logging thread is shut down.
            s_logOutputWorker.Stopped += () =>
            {
                // Relay thread instances count is 0 now - release interceptors.
                foreach (var exitSignalInterceptor in s_posixSignalRegistry)
                    exitSignalInterceptor.Dispose();
                s_posixSignalRegistry.Clear();

                // Everything is cleaned up - exit the process.
                Environment.Exit(0);
            };

            // Stop the logger worker thread finally.
            s_logOutputWorker?.Stop();
            s_logOutputWorker = null;

            // TODO: Properly finalize s_httpWebhookClient (?).
        }

        /// <summary>
        /// Creates a new relay instance based on the specified <param name="startupData"></param> configuration.
        /// </summary>
        private static void CreateRelayInstance(RelayServerStartupData startupData)
        {
            var instance = new RelayServer(startupData, s_startupArguments.RelayMaxConnectionsPerPort, s_startupArguments.RelayProtocolKey, s_startupArguments.RelayMillisecondsTimeStep);
            instance.Start(WorkerScope.NewForegroundThread);
            instance.LogConnectionRejections = s_startupArguments.RelayLogConnectionRejections;
            instance.BlacklistDurationMs = s_startupArguments.RelayBlacklistDurationMs;
            instance.AutoCloseOnLastDisconnect = false;
            instance.Stopped += () => HandleInstanceStop(instance);
            instance.Connected += connectionData =>
            {
                s_httpWebhookSender?.SendAndForget(
                    endpoint: startupData.Webhooks.UserConnected,
                    payload: new
                    {
                        relayPort = instance.Port,
                        connUid = connectionData.Uid,
                        token = connectionData.Token,
                        endpoint = connectionData.EndPoint.ToString(),
                        timestamp = DateTime.UtcNow
                    });
            };

            instance.Disconnected += connectionUid =>
            {
                s_httpWebhookSender?.SendAndForget(
                    endpoint: startupData.Webhooks.UserDisconnected,
                    payload: new
                    {
                        relayPort = instance.Port,
                        connUid = connectionUid,
                        timestamp = DateTime.UtcNow
                    });
            };

            // Register instance.
            s_servers.TryAdd(instance.Uid, instance);
            Logger.LogInfo($"[{instance.Name}] Listening on port: '{instance.Port}'.");
        }
    }
}