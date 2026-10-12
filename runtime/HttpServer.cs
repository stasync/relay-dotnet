using Keel.Networking;
using Keel.Utils.Debug;
using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace UdpRelay
{
    internal sealed class HttpServer
    {
        /// <param name="routeValues">Values of the route template parameters, e.g. "port" and "uid" for "/api/rooms/{port}/clients/{uid}".</param>
        /// <returns>JSON body of the response, sent with status 200 unless the handler set another 2xx code (e.g. 201) on the response. Throw <see cref="HttpStatusException"/> to reply with an error.</returns>
        internal delegate Task<string> HandlerDelegate(HttpListenerContext context, IReadOnlyDictionary<string, string> routeValues);

        private sealed record Route(HttpMethod Method, RouteTemplate Template, HandlerDelegate Handler);

        private readonly HttpListener _listener = new();
        private readonly string _url;
        private readonly int _urlPathSegmentCount;
        private readonly List<Route> _routes = new();

        private bool _isRunning;

        internal HttpServer(string url)
        {
            if (!Utils.IsValidUrl(url))
                throw new ArgumentException("Invalid url", nameof(url));

            _url = url;
            _listener.Prefixes.Add(_url);
            _isRunning = false;

            // Routes are relative to the listener url: with "http://localhost:8080/relay/", "/api/rooms" is served at "/relay/api/rooms".
            _urlPathSegmentCount = RouteTemplate.SplitPath(new Uri(_url).AbsolutePath).Length;
        }

        /// <summary>
        /// Binds a handler to a method and a route template such as "/api/rooms/{port}/clients/{uid}".
        /// When several templates match a path, the one bound first wins.
        /// </summary>
        internal void Bind(HttpMethod method, string template, HandlerDelegate handler)
        {
            if (_isRunning)
                throw new InvalidOperationException($"Listener is already running. Cannot bind API after calling '{nameof(StartBlocking)}'.");

            var routeTemplate = new RouteTemplate(template);
            var currentRoute = _routes.Find(route => route.Method == method && route.Template.HasSameShapeAs(routeTemplate));
            if (currentRoute != null)
            {
                var currentHandler = currentRoute.Handler;
                throw new InvalidOperationException($"Handler for '{method} {template}' is already registered as '{currentRoute.Template}' '{currentHandler.Method.DeclaringType!.FullName}.{currentHandler.Method.Name}'.");
            }

            _routes.Add(new Route(method, routeTemplate, handler));
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
                foreach (var route in _routes)
                {
                    try
                    {
                        var apiFullPath = Utils.CombineUrl(_url, route.Template.Template);
                        Logger.LogInfo($"   [{route.Method} {route.Template}] --- {apiFullPath}");
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
                // HttpListener answers a POST or PUT that has no body length with 411 Length Required itself, but still hands the request over.
                // It must not be acted on, or e.g. a room would be opened for a request the client saw fail.
                if (context.Request.HttpMethod is "POST" or "PUT" &&
                    context.Request.Headers["Content-Length"] == null &&
                    !string.Equals(context.Request.Headers["Transfer-Encoding"], "chunked", StringComparison.OrdinalIgnoreCase))
                {
                    Logger.LogError($"[{GetType().FullName}] Ignoring {context.Request.HttpMethod} {context.Request.Url!.AbsolutePath}: no Content-Length, already answered with 411.");
                    return;
                }

                var pathSegments = RouteTemplate.SplitPath(context.Request.Url!.AbsolutePath)[_urlPathSegmentCount..];
                List<string> allowedMethods = null;
                foreach (var route in _routes)
                {
                    if (!route.Template.TryMatch(pathSegments, out var routeValues))
                        continue;

                    if (route.Method.Method != context.Request.HttpMethod)
                    {
                        (allowedMethods ??= new List<string>()).Add(route.Method.Method);
                        continue;
                    }

                    try
                    {
                        Logger.LogInfo($"HTTP request: {context.Request.HttpMethod} {context.Request.Url!.PathAndQuery}.");

                        var responseString = await route.Handler(context, routeValues);
                        await SendSuccessResponseAsync(context.Response, responseString);
                    }
                    catch (HttpStatusException e)
                    {
                        Logger.LogError($"[{GetType().FullName}] {context.Request.HttpMethod} {context.Request.Url!.AbsolutePath}: {(int)e.StatusCode} {e.Message}");
                        await SendErrorResponseAsync(context.Response, statusCode: (int)e.StatusCode, errorMessage: e.Message);
                    }
                    catch (Exception e)
                    {
                        Logger.LogException(e);
                        await SendErrorResponseAsync(context.Response, statusCode: 500, errorMessage: $"{context.Request.Url!.AbsolutePath}: Internal server error: {e.Message.TrimEnd('.')}.");
                    }

                    return;
                }

                if (allowedMethods != null)
                {
                    Logger.LogError($"[{GetType().FullName}] Method '{context.Request.HttpMethod}' is not allowed for '{context.Request.Url!.AbsolutePath}'.");
                    context.Response.AddHeader("Allow", string.Join(", ", allowedMethods));
                    await SendErrorResponseAsync(context.Response, statusCode: 405, errorMessage: $"{context.Request.HttpMethod} {context.Request.Url!.AbsolutePath} not allowed, use {string.Join(" or ", allowedMethods)}.");
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
            // The status code stays 200 unless the handler set another one, such as 201 Created.
            var buffer = Encoding.UTF8.GetBytes(responseString);
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