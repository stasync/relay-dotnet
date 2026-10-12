using System;
using System.Net;

namespace UdpRelay
{
    /// <summary>
    /// Thrown by a request handler to reply with a specific status code (e.g. 404) instead of a generic 500.
    /// </summary>
    internal sealed class HttpStatusException : Exception
    {
        internal HttpStatusCode StatusCode { get; }

        internal HttpStatusException(HttpStatusCode statusCode, string message) : base(message) =>
            StatusCode = statusCode;
    }
}