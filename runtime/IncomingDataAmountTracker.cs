using Keel.Networking.Udp.LowLevel;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;

namespace UdpRelay
{
    internal sealed class IncomingDataAmountTracker(int limitKBitPerSec) : DataTransferLayer
    {
        internal event Action<IPEndPoint> LimitExceeded = delegate { };

        private sealed class Bucket
        {
            internal long Bytes;
            internal long LastResetTicks = Stopwatch.GetTimestamp();
            internal bool Reported;
        }

        private readonly Dictionary<IPEndPoint, Bucket> _buckets = new();
        private readonly long _limitBytesPerSec = (long)(limitKBitPerSec * 1000.0f / 8.0f);

        public override void ProcessOutgoingData(IPEndPoint targetEndPoint, ref ArraySegment<byte> data)
        {
        }

        public override void ProcessIncomingData(IPEndPoint senderEndPoint, ref ArraySegment<byte> data)
        {
            if (!_buckets.TryGetValue(senderEndPoint, out var bucket))
                _buckets.Add(senderEndPoint, bucket = new Bucket());

            var currentTicks = Stopwatch.GetTimestamp();

            // Tick difference divided by ticks per second.
            var elapsed = (currentTicks - bucket.LastResetTicks) / (double)Stopwatch.Frequency;

            if (elapsed >= 1.0f)
            {
                bucket.Bytes = 0;
                bucket.LastResetTicks = currentTicks;
                bucket.Reported = false;
            }

            bucket.Bytes += data.Count;
            if (bucket.Bytes > _limitBytesPerSec && !bucket.Reported)
            {
                bucket.Reported = true;
                LimitExceeded(senderEndPoint);
            }
        }
    }
}