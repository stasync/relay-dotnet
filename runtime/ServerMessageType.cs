namespace UdpRelay
{
    internal enum ServerMessageType : byte
    {
        OnJoined,
        OnPacket,
        OnDisconnect
    }
}