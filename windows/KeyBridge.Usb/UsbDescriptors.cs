using System.Buffers.Binary;
using System.Text;

namespace KeyBridge.Usb;

/// <summary>USB 2.0 full-speed, non-boot HID. VID/PID are development-only pid.codes test identifiers.</summary>
public static class UsbDescriptors
{
    public const ushort VendorId = 0x1209;
    public const ushort ProductId = 0x0001;
    public const string BusId = "1-1";
    public const uint DeviceId = 0x00010001;

    public static ReadOnlySpan<byte> Device =>
    [18, 1, 0x00, 0x02, 0, 0, 0, 64, 0x09, 0x12, 0x01, 0x00, 0x00, 0x01, 1, 2, 3, 1];

    public static ReadOnlySpan<byte> Report =>
    [0x06, 0xd0, 0xf1, 0x09, 0x01, 0xa1, 0x01, 0x09, 0x20, 0x15, 0x00,
     0x26, 0xff, 0x00, 0x75, 0x08, 0x95, 0x40, 0x81, 0x02, 0x09, 0x21,
     0x15, 0x00, 0x26, 0xff, 0x00, 0x75, 0x08, 0x95, 0x40, 0x91, 0x02, 0xc0];

    public static ReadOnlySpan<byte> Hid => [9, 0x21, 0x11, 0x01, 0, 1, 0x22, 34, 0];

    public static ReadOnlySpan<byte> Configuration =>
    [9, 2, 41, 0, 1, 1, 0, 0x80, 50,
     9, 4, 0, 0, 2, 3, 0, 0, 0,
     9, 0x21, 0x11, 0x01, 0, 1, 0x22, 34, 0,
     7, 5, 0x81, 3, 64, 0, 5,
     7, 5, 0x01, 3, 64, 0, 5];

    public static byte[]? GetDescriptor(byte type, byte index)
    {
        return type switch
        {
            1 when index == 0 => Device.ToArray(),
            2 when index == 0 => Configuration.ToArray(),
            0x21 when index == 0 => Hid.ToArray(),
            0x22 when index == 0 => Report.ToArray(),
            3 => StringDescriptor(index),
            _ => null
        };
    }

    public static byte[]? StringDescriptor(byte index)
    {
        if (index == 0) return [4, 3, 0x09, 0x04];
        var text = index switch
        {
            1 => "KeyBridge",
            2 => "KeyBridge Android Authenticator",
            3 => "KEYBRIDGE-DEVELOPMENT-1",
            _ => null
        };
        if (text is null) return null;
        var encoded = Encoding.Unicode.GetBytes(text);
        var result = new byte[encoded.Length + 2];
        result[0] = checked((byte)result.Length);
        result[1] = 3;
        encoded.CopyTo(result, 2);
        return result;
    }

    public static byte[] UsbIpDeviceRecord()
    {
        var record = new byte[312];
        Encoding.ASCII.GetBytes("/keybridge/usb/1-1").CopyTo(record, 0);
        Encoding.ASCII.GetBytes(BusId).CopyTo(record, 256);
        BinaryPrimitives.WriteUInt32BigEndian(record.AsSpan(288), 1); // busnum
        BinaryPrimitives.WriteUInt32BigEndian(record.AsSpan(292), 1); // devnum
        BinaryPrimitives.WriteUInt32BigEndian(record.AsSpan(296), 2); // full speed
        BinaryPrimitives.WriteUInt16BigEndian(record.AsSpan(300), VendorId);
        BinaryPrimitives.WriteUInt16BigEndian(record.AsSpan(302), ProductId);
        BinaryPrimitives.WriteUInt16BigEndian(record.AsSpan(304), 0x0100);
        record[309] = 1; // configuration value
        record[310] = 1; // configurations
        record[311] = 1; // interfaces
        return record;
    }
}
