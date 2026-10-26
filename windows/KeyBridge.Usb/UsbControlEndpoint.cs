using System.Buffers.Binary;

namespace KeyBridge.Usb;

internal sealed class UsbControlEndpoint(CtapHidDevice hid)
{
    private byte _configuration;
    private byte _idle;
    public bool Configured => _configuration == 1;

    // Standard control transfers use little-endian setup fields even inside network-order USB/IP.
    public async Task<(int Status, byte[] Data)> TransferAsync(byte[] setup, byte[] data, uint direction, CancellationToken cancellationToken)
    {
        if (setup.Length != 8 || ((setup[0] >> 7) & 1) != direction) return (-22, []);
        var value = BinaryPrimitives.ReadUInt16LittleEndian(setup.AsSpan(2));
        var index = BinaryPrimitives.ReadUInt16LittleEndian(setup.AsSpan(4));
        var length = BinaryPrimitives.ReadUInt16LittleEndian(setup.AsSpan(6));
        byte[]? response = null;
        switch (setup[0], setup[1])
        {
            case (0x80, 6): // GET_DESCRIPTOR (device)
                if ((value >> 8) is 1 or 2 or 3) response = UsbDescriptors.GetDescriptor((byte)(value >> 8), (byte)value);
                break;
            case (0x81, 6) when index == 0: // HID and report descriptors are interface descriptors
                if ((value >> 8) is 0x21 or 0x22) response = UsbDescriptors.GetDescriptor((byte)(value >> 8), (byte)value);
                break;
            case (0x80, 0) when value == 0 && index == 0: // GET_STATUS, bus-powered, no remote wake
            case (0x81, 0) when value == 0 && index == 0:
            case (0x82, 0) when value == 0 && index is 0 or 1 or 0x81:
                response = [0, 0]; break;
            case (0x00, 5) when value <= 127 && index == 0 && length == 0: // SET_ADDRESS
                response = []; break;
            case (0x00, 9) when value <= 1 && index == 0 && length == 0: // SET_CONFIGURATION
                _configuration = (byte)value; response = []; break;
            case (0x80, 8) when value == 0 && index == 0: // GET_CONFIGURATION
                response = [_configuration]; break;
            case (0x81, 10) when Configured && value == 0 && index == 0: // GET_INTERFACE
                response = [0]; break;
            case (0x01, 11) when Configured && value == 0 && index == 0 && length == 0: // SET_INTERFACE
                response = []; break;
            case (0x02, 1) when value == 0 && index is 1 or 0x81 && length == 0: // CLEAR_FEATURE(ENDPOINT_HALT)
                response = []; break;
            case (0x21, 10) when index == 0 && (value & 0xff) == 0 && length == 0: // SET_IDLE
                _idle = (byte)(value >> 8); response = []; break;
            case (0xa1, 2) when index == 0 && value == 0: // GET_IDLE
                response = [_idle]; break;
            case (0x21, 9) when Configured && index == 0 && value == 0x0200 && length == 64 && data.Length == 64:
                await hid.SendReportAsync(data, cancellationToken).ConfigureAwait(false);
                response = []; break;
        }
        // Unsupported device qualifier/BOS, boot protocol and legacy HID requests stall explicitly.
        if (response is null) return (-32, []); // -EPIPE: control endpoint STALL
        return (0, response.AsSpan(0, Math.Min(response.Length, length)).ToArray());
    }
}
