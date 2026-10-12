using System.Text.Json.Serialization;

namespace UdpRelay
{
    internal sealed class RoomSettings
    {
        [JsonInclude] internal string Name { get; set; }
        [JsonInclude] internal int TargetPort { get; set; }
        [JsonInclude] internal string UdpEncryptionKey { get; set; } = string.Empty;
        [JsonInclude] internal string ConnectionKey { get; set; } = string.Empty;
        [JsonInclude] internal bool CaptureDataTransferAmount { get; set; }
        [JsonInclude] internal WebhooksData Webhooks { get; set; } = new();
        [JsonInclude] internal ClientRateLimiterData ClientRateLimiter { get; set; } = new();

        internal sealed class WebhooksData
        {
            [JsonInclude] internal string ClientJoined { get; set; } = string.Empty;
            [JsonInclude] internal string ClientLeft { get; set; } = string.Empty;
        }

        internal sealed class ClientRateLimiterData
        {
            [JsonInclude] internal int KBitPerSecLimit { get; set; }
        }
    }
}
