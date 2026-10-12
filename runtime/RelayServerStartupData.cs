using System.Text.Json.Serialization;

namespace UdpRelay
{
    internal sealed class RelayServerStartupData
    {
        [JsonInclude] internal string WorkerName { get; set; }
        [JsonInclude] internal int TargetPort { get; set; }
        [JsonInclude] internal string UdpEncryptionKey { get; set; } = string.Empty;
        [JsonInclude] internal string ConnectionKey { get; set; } = string.Empty;
        [JsonInclude] internal bool CaptureDataTransferAmount { get; set; }
        [JsonInclude] internal WebhooksData Webhooks { get; set; } = new();
        [JsonInclude] internal ClientRateLimiterData ClientRateLimiter { get; set; } = new();

        internal sealed class WebhooksData
        {
            [JsonInclude] internal string UserConnected { get; set; } = string.Empty;
            [JsonInclude] internal string UserDisconnected { get; set; } = string.Empty;
        }

        internal sealed class ClientRateLimiterData
        {
            [JsonInclude] internal int KBitPerSecLimit { get; set; }
        }
    }
}