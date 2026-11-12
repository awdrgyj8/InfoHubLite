using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace InfoHubLite;

/// <summary>
/// AYW Gaming LC 360 ARGB LCD (USB 0B05:1C81, WinUSB). Protocol recovered from ASUS_InfoHub.exe:
///   5A A5 | payload length (4, BE) | Modbus CRC16 of those 4 length bytes (hi, lo) | brightness 0..100
///   payload = width (2, BE) | height (2, BE) | 240x320 RGB565 little-endian pixels
/// The panel is natively 240x320 portrait; landscape frames are sent rotated 90° counter-clockwise
/// while the header still says 320x240. One bulk write per frame.
/// </summary>
sealed class Lcd : IDisposable
{
    public const int NativeW = 240, NativeH = 320, Pixels = NativeW * NativeH;
    public const int HeaderLen = 13, PacketLen = HeaderLen + Pixels * 2;
    static readonly Guid Iface = new("88bae032-5a81-49f0-bc3d-a4ff138216d6");

    SafeFileHandle? _file;
    nint _usb;
    byte _outPipe;

    public bool IsOpen => _usb != 0;

    /// <summary>Fills the 13 header bytes. landscape selects the 320x240 header InfoHub uses for rotated frames.</summary>
    public static void WriteHeader(Span<byte> p, int brightness, bool landscape)
    {
        uint len = 4 + Pixels * 2;
        p[0] = 0x5A; p[1] = 0xA5;
        p[2] = (byte)(len >> 24); p[3] = (byte)(len >> 16); p[4] = (byte)(len >> 8); p[5] = (byte)len;
        ushort crc = Crc16(p.Slice(2, 4));
        p[6] = (byte)(crc >> 8); p[7] = (byte)crc;
        p[8] = (byte)Math.Clamp(brightness, 0, 100);
        int w = landscape ? NativeH : NativeW, h = landscape ? NativeW : NativeH;
        p[9] = (byte)(w >> 8); p[10] = (byte)w; p[11] = (byte)(h >> 8); p[12] = (byte)h;
    }

    public static ushort Crc16(ReadOnlySpan<byte> data) // Modbus: poly 0xA001, init 0xFFFF
    {
        ushort c = 0xFFFF;
        foreach (byte b in data)
        {
            c ^= b;
            for (int i = 0; i < 8; i++) c = (ushort)((c & 1) != 0 ? (c >> 1) ^ 0xA001 : c >> 1);
        }
        return c;
    }

    public bool Open()
    {
        Close();
        string? path = FindPath();
        if (path == null) return false;
        var f = CreateFileW(path, 0xC0000000, 3, 0, 3, 0x40000080, 0); // RW, share RW, OPEN_EXISTING, OVERLAPPED|NORMAL
        if (f.IsInvalid) { f.Dispose(); return false; }                // usually: the official InfoHub still holds the device
        if (!WinUsb_Initialize(f, out nint usb)) { f.Dispose(); return false; }

        byte outPipe = 0;
        if (WinUsb_QueryInterfaceSettings(usb, 0, out var ifd))
            for (byte i = 0; i < ifd.bNumEndpoints; i++)
                if (WinUsb_QueryPipe(usb, 0, i, out var pi) && pi.PipeType == 2 && (pi.PipeId & 0x80) == 0)
                    outPipe = pi.PipeId;
        if (outPipe == 0) { WinUsb_Free(usb); f.Dispose(); return false; }

        uint timeoutMs = 1000; // a wedged device must not hang the engine forever
        WinUsb_SetPipePolicy(usb, outPipe, 3 /*PIPE_TRANSFER_TIMEOUT*/, 4, ref timeoutMs);
        _file = f; _usb = usb; _outPipe = outPipe;
        return true;
    }

    public unsafe bool Write(byte[] packet)
    {
        if (_usb == 0) return false;
        fixed (byte* p = packet)
            if (WinUsb_WritePipe(_usb, _outPipe, p, (uint)packet.Length, out uint n, 0) && n == packet.Length)
                return true;
        Close();
        return false;
    }

    public void Close()
    {
        if (_usb != 0) { WinUsb_Free(_usb); _usb = 0; }
        _file?.Dispose(); _file = null;
    }

    public void Dispose() => Close();

    static unsafe string? FindPath()
    {
        Guid g = Iface;
        if (CM_Get_Device_Interface_List_SizeW(out uint len, ref g, null, 0) != 0 || len < 2) return null;
        char[] buf = new char[len];
        fixed (char* p = buf)
        {
            if (CM_Get_Device_Interface_ListW(ref g, null, p, len, 0) != 0) return null;
            string first = new(p); // multi-sz: take the first present interface
            return first.Length > 0 ? first : null;
        }
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    struct USB_INTERFACE_DESCRIPTOR
    {
        public byte bLength, bDescriptorType, bInterfaceNumber, bAlternateSetting, bNumEndpoints,
                    bInterfaceClass, bInterfaceSubClass, bInterfaceProtocol, iInterface;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct WINUSB_PIPE_INFORMATION { public int PipeType; public byte PipeId; public ushort MaximumPacketSize; public byte Interval; }

    [DllImport("kernel32", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern SafeFileHandle CreateFileW(string name, uint access, uint share, nint sec, uint disposition, uint flags, nint template);
    [DllImport("winusb", SetLastError = true)] static extern bool WinUsb_Initialize(SafeFileHandle file, out nint handle);
    [DllImport("winusb", SetLastError = true)] static extern bool WinUsb_Free(nint handle);
    [DllImport("winusb", SetLastError = true)] static extern bool WinUsb_QueryInterfaceSettings(nint h, byte alt, out USB_INTERFACE_DESCRIPTOR d);
    [DllImport("winusb", SetLastError = true)] static extern bool WinUsb_QueryPipe(nint h, byte alt, byte index, out WINUSB_PIPE_INFORMATION info);
    [DllImport("winusb", SetLastError = true)] static extern bool WinUsb_SetPipePolicy(nint h, byte pipe, uint policy, uint len, ref uint value);
    [DllImport("winusb", SetLastError = true)] static extern unsafe bool WinUsb_WritePipe(nint h, byte pipe, byte* buf, uint len, out uint written, nint overlapped);
    [DllImport("cfgmgr32", CharSet = CharSet.Unicode)] static extern uint CM_Get_Device_Interface_List_SizeW(out uint len, ref Guid g, string? deviceId, uint flags);
    [DllImport("cfgmgr32", CharSet = CharSet.Unicode)] static extern unsafe uint CM_Get_Device_Interface_ListW(ref Guid g, string? deviceId, char* buf, uint len, uint flags);
}
