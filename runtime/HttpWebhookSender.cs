using Keel.Networking;
using Keel.Utils.Debug;
using System;
using System.Collections.Concurrent;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace UdpRelay
{
    internal sealed class HttpWebhookSender : IDisposable
    {
        private const int HTTP_CLIENT_TIMEOUT_SECONDS = 2;
        private const int RETRY_DELAY_SECONDS = HTTP_CLIENT_TIMEOUT_SECONDS + 2;

        private readonly HttpClient _httpClient = new();
        private readonly ConcurrentDictionary<string, bool> _urlValidityCache = new();
        private readonly ConcurrentQueue<(string endpoint, string json)> _retryQueue = new();
        private readonly Timer _retryTimer;

        internal HttpWebhookSender()
        {
            _httpClient.Timeout = TimeSpan.FromSeconds(HTTP_CLIENT_TIMEOUT_SECONDS);

            var retryTimeSpan = TimeSpan.FromSeconds(RETRY_DELAY_SECONDS);
            _retryTimer = new Timer(_ => RetryPending(),
                state: null,
                dueTime: retryTimeSpan,
                period: retryTimeSpan);
        }

        internal void SendAndForget(string endpoint, object payload)
        {
            // Do nothing if string is an empty or null.
            if (string.IsNullOrWhiteSpace(endpoint))
                return;

            // Queue the task.
            _ = Task.Run(async () =>
            {
                try
                {
                    await SendAsync(endpoint, payload);
                }
                catch (Exception ex)
                {
                    Logger.LogException(ex);
                }
            });
        }

        private async Task SendAsync(string endpoint, object payload)
        {
            if (!_urlValidityCache.TryGetValue(endpoint, out var isValid))
                _urlValidityCache.TryAdd(endpoint, isValid = Utils.IsValidUrl(endpoint));

            if (!isValid)
                throw new InvalidOperationException($"[{nameof(HttpWebhookSender)}] Ulr '{endpoint}' is not valid.");

            var json = JsonSerializer.Serialize(payload);

            Logger.LogInfo($"[{nameof(HttpWebhookSender)}] Executing webhook '{endpoint}'. {json}");

            var success = await TrySendAsync(endpoint, json);
            if (!success)
            {
                Logger.LogWarning($"[{nameof(HttpWebhookSender)}] Webhook '{endpoint}' unreachable, queued for retry.");
                _retryQueue.Enqueue((endpoint, json));
            }
        }

        private void RetryPending()
        {
            var count = _retryQueue.Count;
            for (var i = 0; i < count; i++)
            {
                if (!_retryQueue.TryDequeue(out var retryData))
                    break;

                var endpoint = retryData.endpoint;
                Logger.LogWarning($"[{nameof(HttpWebhookSender)}] Retrying webhook '{endpoint}'.");

                _ = Task.Run(async () =>
                {
                    var success = await TrySendAsync(endpoint, retryData.json);
                    if (!success)
                    {
                        Logger.LogWarning($"[{nameof(HttpWebhookSender)}] Webhook '{endpoint}' still unreachable, re-queued.");
                        _retryQueue.Enqueue(retryData);
                    }
                });
            }
        }

        private async Task<bool> TrySendAsync(string endpoint, string json)
        {
            try
            {
                var content = new StringContent(json, Encoding.UTF8, mediaType: "application/json");
                var response = await _httpClient.PostAsync(endpoint, content);
                var body = await response.Content.ReadAsStringAsync();

                // Webhook reached, but the resource was not found, log error.
                if ((int)response.StatusCode >= 400 && (int)response.StatusCode < 500)
                {
                    Logger.LogError($"[{nameof(HttpWebhookSender)}] Webhook '{endpoint}' reached, but resource is unavailable. Status: {(int)response.StatusCode}. {body}");
                    // There is no point to retry here - return true.
                    return true;
                }

                Logger.LogInfo($"[{nameof(HttpWebhookSender)}] Webhook '{endpoint}' reached. Status: {(int)response.StatusCode}. {body}");
                return true;
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or OperationCanceledException)
            {
                return false;
            }
        }

        public void Dispose()
        {
            _retryTimer.Dispose();
            _httpClient.Dispose();
        }
    }
}