using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

namespace Mouser.Phase0;

internal static class Wire
{
    /// <summary>"M6S0" — one telemetry frame.</summary>
    public const uint MagicData = 0x4D365330;

    /// <summary>"PING" — round-trip probe.</summary>
    public const uint MagicPing = 0x50494E47;

    /// <summary>"PONG" — reply to PING, payload echoed verbatim.</summary>
    public const uint MagicPong = 0x504F4E47;

    public const int DataSize = 32;
    public const int PingSize = 16;
}

/// <summary>
/// Frame-oriented UDP sender. The capture side accumulates stroke deltas and
/// flushes them at ~100 Hz, which is what a 6DOF consumer actually wants; sending
/// one datagram per 1 kHz stroke would be pointless.
/// </summary>
internal sealed class UdpSender : IDisposable
{
    private readonly Socket _socket;
    private readonly IPEndPoint _target;
    private readonly byte[] _buffer = new byte[Wire.DataSize];
    private uint _sequence;

    public UdpSender(IPEndPoint target)
    {
        _target = target;
        _socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
    }

    public void Send(long dx, long dy, long wheel, bool gate)
    {
        Span<byte> span = _buffer;
        BinaryPrimitives.WriteUInt32LittleEndian(span[0..4], Wire.MagicData);
        BinaryPrimitives.WriteUInt32LittleEndian(span[4..8], _sequence++);
        BinaryPrimitives.WriteInt64LittleEndian(span[8..16], Stopwatch.GetTimestamp());
        BinaryPrimitives.WriteSingleLittleEndian(span[16..20], dx);
        BinaryPrimitives.WriteSingleLittleEndian(span[20..24], dy);
        BinaryPrimitives.WriteSingleLittleEndian(span[24..28], wheel);
        BinaryPrimitives.WriteUInt16LittleEndian(span[28..30], gate ? (ushort)1 : (ushort)0);
        span[30] = 0;
        span[31] = 0;

        try
        {
            _socket.SendTo(_buffer, _target);
        }
        catch (SocketException)
        {
            // Link-local trouble must never stall the input path.
        }
    }

    public void Dispose() => _socket.Dispose();
}
