using Keel.Networking;
using Keel.Utils.Debug;
using System;
using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace UdpRelay
{
    internal sealed class HttpServer
    {
        internal delegate Task<string> HandlerDelegate(HttpListenerContext context);

        private readonly HttpListener _listener = new();
        private readonly string _url;
        private readonly ConcurrentDictionary<string, HandlerDelegate> _apiHandlers = new();

        private bool _isRunning;

        internal HttpServer(string url)
        {
            if (!Utils.IsValidUrl(url))
                throw new ArgumentException("Invalid url", nameof(url));

            _url = url;
            _listener.Prefixes.Add(_url);
            _isRunning = false;
        }

        internal void Bind(string api, HandlerDelegate handler)
        {
            if (_isRunning)
                throw new InvalidOperationException($"Listener is already running. Cannot bind API after calling '{nameof(StartBlocking)}'.");

            if (_apiHandlers.TryAdd(api, handler))
                return;

            var currentHandler = _apiHandlers[api];
            throw new InvalidOperationException($"Handler for '{api}' is already registered '{currentHandler.Method.DeclaringType!.FullName}.{currentHandler.Method.Name}'.");
        }

        internal void StartBlocking()
        {
            if (_isRunning)
            {
                Logger.LogError($"[{GetType().FullName}] Already listening.");
                return;
            }

            try
            {
                _listener.Start();
                _isRunning = true;

                Logger.LogInfo($"[{GetType().FullName}] Listening on '{_url}'.");
                foreach (var apiUrl in _apiHandlers.Keys)
                {
                    try
                    {
                        var apiFullPath = Utils.CombineUrl(_url, apiUrl);
                        Logger.LogInfo($"   [{apiUrl}] --- {apiFullPath}");
                    }
                    catch (Exception ex)
                    {
                        Logger.LogException(ex);
                    }
                }

                // Continuous blocking loop to handle requests.
                while (_isRunning)
                {
                    try
                    {
                        // This blocks until a request comes in.
                        var context = _listener.GetContext();

                        // Handle request asynchronously (non-blocking for individual requests).
                        _ = Task.Run(() => HandleRequestAsync(context));
                    }
                    catch (HttpListenerException)
                    {
                        // Exception in this context usually means that the listener was shut down.
                        break;
                    }
                    catch (Exception ex)
                    {
                        Logger.LogException(ex);
                    }
                }
            }
            catch (HttpListenerException ex)
            {
                Logger.LogException(ex);
            }
            catch (Exception ex)
            {
                Logger.LogException(ex);
            }
            finally
            {
                Stop();
            }
        }

        internal void Stop()
        {
            if (!_isRunning)
                return;

            _isRunning = false;
            _listener.Stop();
            _listener.Close();

            Logger.LogInfo($"[{GetType().FullName}] Server stopped.");
        }

        private async Task HandleRequestAsync(HttpListenerContext context)
        {
            try
            {
                if (_apiHandlers.TryGetValue(context.Request.Url!.AbsolutePath, out var requestHandler))
                {
                    try
                    {
                        Logger.LogInfo($"HTTP request: {context.Request.HttpMethod} {context.Request.Url!.PathAndQuery}.");

                        var responseString = await requestHandler(context);
                        await SendSuccessResponseAsync(context.Response, responseString);
                    }
                    catch (Exception e)
                    {
                        Logger.LogException(e);
                        await SendErrorResponseAsync(context.Response, statusCode: 500, errorMessage: $"{context.Request.Url!.AbsolutePath}: Internal server error: {e.Message.TrimEnd('.')}.");
                    }
                }
                else
                {
                    Logger.LogError($"[{GetType().FullName}] Unable to find handler for '{context.Request.Url!.AbsolutePath}'.");
                    await SendErrorResponseAsync(context.Response, statusCode: 404, errorMessage: $"{context.Request.Url!.AbsolutePath} not found.");
                }
            }
            catch (Exception e)
            {
                Logger.LogException(e);
                await SendErrorResponseAsync(context.Response, statusCode: 500, errorMessage: $"{context.Request.Url!.AbsolutePath}: Internal server error: {e.Message.TrimEnd('.')}.");
            }
        }

        private static async Task SendSuccessResponseAsync(HttpListenerResponse response, string responseString)
        {
            var buffer = Encoding.UTF8.GetBytes(responseString);
            response.StatusCode = 200;
            response.ContentType = "application/json";
            response.ContentLength64 = buffer.Length;

            await response.OutputStream.WriteAsync(buffer);
            response.OutputStream.Close();
        }

        private static async Task SendErrorResponseAsync(HttpListenerResponse response, int statusCode, string errorMessage)
        {
            var errorData = new
            {
                code = statusCode,
                error = errorMessage,
                timestamp = DateTime.UtcNow
            };

            var json = JsonSerializer.Serialize(errorData);
            var buffer = Encoding.UTF8.GetBytes(json);
            response.StatusCode = statusCode;
            response.ContentType = "application/json";
            response.ContentLength64 = buffer.Length;

            await response.OutputStream.WriteAsync(buffer);
            response.OutputStream.Close();
        }
    }
}