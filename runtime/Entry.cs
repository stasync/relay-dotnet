using Keel.DependencyInjection;
using Keel.Networking;
using Keel.Utils;
using Keel.Utils.Debug;
using Keel.Utils.Threading;
using System;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Threading;

namespace UdpRelay
{
    internal static class Entry
    {
        private static readonly ConcurrentBag<PosixSignalRegistration> s_posixSignalRegistry = new();
        private static RootScope s_scope;
        private static RoomRegistry s_roomRegistry;
        private static HttpServer s_httpServer;
        private static LogWorker s_logOutputWorker;
        private static int s_isFinalizing;

        // TODO: right now the node shuts down as soon as its last room is closed.
        // TODO: see HandleRoomClosed - right now its the part of application shutdown logic.
        // TODO: this should be configurable.

        private static void Main(string[] args)
        {
            // First thing to be done - set a fake output to collect logs during the config parsing stage.
            // It's only possible to initialize the actual log worker once the config is parsed.
            var temporaryLogOutput = new Logger.QueueOutput();
            Logger.SetCustomOutput(temporaryLogOutput);

            // The arguments themselves aren't logged, as they may hold the HTTP API key. Their parsed values are logged below, with the key hidden.
            Logger.LogInfo($"Startup path: '{DomainUtils.DomainDirectory}'.");
            Logger.LogInfo($"Platform: '{DomainUtils.Platform}'.");
            Logger.LogInfo($"PID: '{Environment.ProcessId}'.");
            Logger.LogInfo($"Environment: '{Environment.UserName}'.");

            var startupArguments = LaunchArguments.CreateInstance<StartupArguments>();
            Logger.LogInfo(startupArguments.ToString());

            // Create a new (persistent) log output.
            s_logOutputWorker = LogWorker.BuildAndStart(WorkerScope.NewForegroundThread, startupArguments.LogOutputRelativePath);
            Logger.SetCustomOutput(s_logOutputWorker);

            // Copy temporary output to the persistent.
            temporaryLogOutput.FlushTo(s_logOutputWorker);

            // Now update a log level from the config.
            Logger.SetLogLevel(startupArguments.LogLevel);

            // Bind the node's services. Each one is created when it's first provided.
            s_scope = BuildScope(startupArguments);
            s_roomRegistry = s_scope.Provide<RoomRegistry>();
            s_roomRegistry.RoomClosed += HandleRoomClosed;

            // Open rooms requested on the command line.
            // Normally it should only be used for debugging when it's required to have one or a few rooms running by default.
            // Such rooms are unsafe as they don't use token-based connection validation.
            if (startupArguments.DefaultRoomPorts.Length > 0)
            {
                Logger.LogInfo($"[Node] Requested '{startupArguments.DefaultRoomPorts.Length}' default room(s) to be opened.");

                foreach (var requestedPort in startupArguments.DefaultRoomPorts)
                {
                    Logger.LogInfo($"[Node] Trying to open a room on port: {requestedPort}.");

                    // The code below has the same logic as the socket api.
                    // 1. Port isn't specified (0), caller doesn't care about the port, first available port will be used.
                    // 2. Port is specified (!=0), caller DOES care about the port - do nothing here, port will be validated upon creation.
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

                    s_roomRegistry.Open(new RoomSettings
                    {
                        Name = $"Room_{targetPort}",
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

                    // Close all rooms.
                    if (s_roomRegistry.IsEmpty)
                        FinalizeApplication();
                    else
                    {
                        foreach (var room in s_roomRegistry.All)
                            room.Stop();
                    }
                }));

            // Intercept SIGTERM.
            s_posixSignalRegistry.Add(PosixSignalRegistration.Create(PosixSignal.SIGTERM,
                posixSignalContext =>
                {
                    Logger.LogInfo($"ProcessExit: Received {PosixSignal.SIGTERM}.");

                    posixSignalContext.Cancel = true;

                    // Close all rooms.
                    if (s_roomRegistry.IsEmpty)
                        FinalizeApplication();
                    else
                    {
                        foreach (var room in s_roomRegistry.All)
                            room.Stop();
                    }
                }));

            // Create and start the HTTP server
            try
            {
                // If port equals 0, it's a valid case where a 1st available port should be picked.
                if (!Utils.IsValidUrl(startupArguments.HttpListenerUrl))
                    Logger.LogWarning($"Unable to initialize IPC ({nameof(HttpServer)} and {nameof(HttpWebhookSender)}) as external startup URL parameter is invalid: '{startupArguments.HttpListenerUrl}'.");
                // The API exposes every client's token and can close any room, so it never runs without a key.
                else if (string.IsNullOrWhiteSpace(startupArguments.HttpApiKey))
                    Logger.LogError($"Unable to initialize IPC ({nameof(HttpServer)} and {nameof(HttpWebhookSender)}) as the 'httpApiKey' startup parameter is empty.");
                else
                {
                    // Create an http server to maintain IPC.
                    s_httpServer = s_scope.Provide<HttpServer>();
                    s_scope.Provide<HttpRequestHandler>().Bind(s_httpServer);
                    s_httpServer.StartBlocking();
                }
            }
            catch (Exception e)
            {
                Logger.LogException(e);
            }
        }

        /// <summary>
        /// Handle a room's closed event. Once the last room is closed, the node shuts down.
        /// This event will be called from the room's thread, so it has to be thread-safe.
        /// </summary>
        private static void HandleRoomClosed(Room room)
        {
            // The room is already unregistered - check if any other rooms are left.
            if (s_roomRegistry.IsEmpty)
            {
                Logger.LogInfo("[SHUTDOWN] Last room was closed. The node will exit.");
                FinalizeApplication();
            }
        }

        /// <summary>
        /// If there are no rooms open, this function will exit the application (with code 0) as soon as the logging thread has been shut down.
        /// Function will throw <see cref="InvalidOperationException"/> if any room is still open.
        /// </summary>
        private static void FinalizeApplication()
        {
            if (!s_roomRegistry.IsEmpty)
                throw new InvalidOperationException("Application can only be finalized when all rooms are closed.");

            // Rooms can close at the same time on different threads, and each one calls this. Only the first call shuts the node down.
            if (Interlocked.Exchange(ref s_isFinalizing, 1) == 1)
                return;

            // Stop http sever.
            s_httpServer?.Stop();
            s_httpServer = null;

            // Release the node's services (such as the webhook sender) while errors can still be logged.
            try
            {
                DisposeScope(s_scope);
            }
            catch (Exception e)
            {
                Logger.LogException(e);
            }

            // Now we will be able to close the process once the logging thread is shut down.
            s_logOutputWorker.Stopped += () =>
            {
                // No rooms are left - release interceptors.
                foreach (var exitSignalInterceptor in s_posixSignalRegistry)
                    exitSignalInterceptor.Dispose();
                s_posixSignalRegistry.Clear();

                // Everything is cleaned up - exit the process.
                Environment.Exit(0);
            };

            // Stop the logger worker thread finally.
            s_logOutputWorker?.Stop();
            s_logOutputWorker = null;
        }

        /// <summary>
        /// Binds the node's services. They live as long as the node, and each one is created when it's first provided.
        /// </summary>
        internal static RootScope BuildScope(StartupArguments startupArguments)
        {
            var binder = new ScopeBinder();
            binder.Bind<StartupArguments, StartupArguments>(() => startupArguments);
            binder.BindToSelf<HttpWebhookSender>();
            binder.BindToSelf<RoomRegistry>();
            binder.BindToSelf<HttpRequestHandler>();
            // The constructor rejects an invalid URL or an empty key, so it's only provided once Main has checked both.
            binder.Bind<HttpServer, HttpServer>(() => new HttpServer(url: startupArguments.HttpListenerUrl, apiKey: startupArguments.HttpApiKey));
            return new RootScope(binder.ToDependencyMap());
        }

        /// <summary>
        /// Disposes every service the scope created, then the scope. Keel's <see cref="Scope.Dispose"/> doesn't dispose the services itself.
        /// </summary>
        internal static void DisposeScope(Scope scope)
        {
            foreach (var instance in scope.EnumerateActiveInstances())
                (instance as IDisposable)?.Dispose();

            scope.Dispose();
        }
    }
}