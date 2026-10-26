using System.Buffers.Binary;

namespace KeyBridge.Usb;

public enum CtapHidCommand : byte
{
    Ping = 0x01, Msg = 0x03, Init = 0x06, Cbor = 0x10, Cancel = 0x11,
    KeepAlive = 0x3b, Error = 0x3f
}

public enum CtapHidError : byte
{
    InvalidCommand = 0x01, InvalidParameter = 0x02, InvalidLength = 0x03,
    InvalidSequence = 0x04, MessageTimeout = 0x05, ChannelBusy = 0x06,
    InvalidChannel = 0x0b, Other = 0x7f
}

public static class HidFraming
{
    public const int ReportSize = 64;
    public const int MaximumMessageSize = 57 + 128 * 59;
    public const uint BroadcastChannel = uint.MaxValue;

    public static IReadOnlyList<byte[]> Fragment(uint channel, CtapHidCommand command, ReadOnlySpan<byte> payload)
    {
        if (payload.Length > MaximumMessageSize) throw new ArgumentOutOfRangeException(nameof(payload));
        var packets = new List<byte[]>();
        var first = new byte[ReportSize];
        BinaryPrimitives.WriteUInt32BigEndian(first, channel);
        first[4] = (byte)((byte)command | 0x80);
        BinaryPrimitives.WriteUInt16BigEndian(first.AsSpan(5), checked((ushort)payload.Length));
        var offset = Math.Min(57, payload.Length);
        payload[..offset].CopyTo(first.AsSpan(7));
        packets.Add(first);
        byte sequence = 0;
        while (offset < payload.Length)
        {
            var packet = new byte[ReportSize];
            BinaryPrimitives.WriteUInt32BigEndian(packet, channel);
            packet[4] = sequence++;
            var count = Math.Min(59, payload.Length - offset);
            payload.Slice(offset, count).CopyTo(packet.AsSpan(5));
            offset += count;
            packets.Add(packet);
        }
        return packets;
    }
}
