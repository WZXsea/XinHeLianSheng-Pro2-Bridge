using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

namespace Y700Switch2V60Viiper;

public sealed record Pro2UsbCandidate(string Path, Guid ContainerId, ushort InputLength, ushort OutputLength);

// Native Windows HID input plus the existing WinUSB vendor interface. No driver replacement.
public sealed class Pro2UsbDevice : IAsyncDisposable
{
    private readonly SafeFileHandle handle;
    private readonly FileStream input;
    public Pro2UsbCandidate Candidate { get; }
    public Pro2BatteryStatus? InitialBatteryStatus { get; private set; }
    private Pro2UsbDevice(Pro2UsbCandidate candidate, SafeFileHandle handle)
    { Candidate = candidate; this.handle = handle; input = new FileStream(handle, FileAccess.ReadWrite, 64, true); }

    public static IReadOnlyList<Pro2UsbCandidate> Enumerate()
    {
        HidD_GetHidGuid(out var guid);
        var found = new List<Pro2UsbCandidate>();
        foreach (var item in Interfaces(guid))
        {
            if (!item.Path.Contains("vid_057e&pid_2069", StringComparison.OrdinalIgnoreCase) || !item.Physical) continue;
            using var file = CreateFile(item.Path, 0, 3, IntPtr.Zero, 3, 0, IntPtr.Zero);
            if (file.IsInvalid) continue;
            var attributes = new HidAttributes { Size = Marshal.SizeOf<HidAttributes>() };
            if (!HidD_GetAttributes(file, ref attributes) || attributes.VendorId != 0x057e || attributes.ProductId != 0x2069) continue;
            if (!HidD_GetPreparsedData(file, out var data)) continue;
            try
            {
                if (HidP_GetCaps(data, out var caps) < 0 || caps.UsagePage != 1 || caps.Usage is not (4 or 5) || caps.InputLength != 64) continue;
                found.Add(new(item.Path, item.Container, caps.InputLength, caps.OutputLength));
            }
            finally { HidD_FreePreparsedData(data); }
        }
        return found;
    }

    public static Pro2UsbDevice Open(Pro2UsbCandidate candidate)
    {
        var file = CreateFile(candidate.Path, 0xc0000000, 3, IntPtr.Zero, 3, 0x40000000, IntPtr.Zero);
        if (file.IsInvalid) { int error = Marshal.GetLastWin32Error(); file.Dispose(); throw new Win32Exception(error, "无法打开 NS2 Pro USB HID；请关闭占用它的其他手柄软件。"); }
        HidD_SetNumInputBuffers(file, 32);
        return new(candidate, file);
    }

    public ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token) => input.ReadAsync(buffer, token);
    public ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken token) => input.WriteAsync(buffer, token);
    public void FlushInput()
    {
        if (!HidD_FlushQueue(handle)) throw new Win32Exception(Marshal.GetLastWin32Error(), "USB 旧输入队列清理失败");
    }

    public async Task InitializeAsync(IProgress<string> progress, CancellationToken token)
    {
        using var command=OpenCommandChannel();
        foreach(byte[] packet in Pro2UsbProtocol.StartupCommands())
        {
            command.Write(packet,token);
            progress.Report("[PRO2_USB_INIT] command="+packet[0].ToString("X2")+"/"+packet[3].ToString("X2")+" write_success=true input_verified=false");
            await Task.Delay(20,token).ConfigureAwait(false);
        }
        FlushInput();
        InitialBatteryStatus=await Pro2UsbBatteryProbe.CaptureAsync(command.SelectReportAsync,ReadAsync,progress,token).ConfigureAwait(false);
    }

    public async Task<Pro2BatteryStatus?> ReadRuntimeBatteryAsync(
        Func<Memory<byte>,CancellationToken,ValueTask<int>> read,Func<bool> stillIdle,IProgress<string> progress,CancellationToken token)
    {
        // Open/validate the command endpoint before changing report format.
        // The shared HID reader remains the only owner of continuous input.
        using var command=OpenCommandChannel();
        if(!stillIdle())throw new Pro2UsbBatteryDeferredException();
        return await Pro2UsbBatteryProbe.CaptureAsync(command.SelectReportAsync,read,progress,token,
            Pro2UsbBatteryPolicy.CaptureTimeoutMs).ConfigureAwait(false);
    }

    private CommandChannel OpenCommandChannel()
    {
        var paths = new List<(string Path, Guid Container, bool Physical)>();
        using (var root = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Enum\USB\VID_057E&PID_2069&MI_01"))
        {
            if (root != null) foreach (string instance in root.GetSubKeyNames())
            {
                using var key = root.OpenSubKey(instance);
                if (key == null || !string.Equals(key.GetValue("Service") as string, "WinUSB", StringComparison.OrdinalIgnoreCase)) continue;
                using var parameters = key.OpenSubKey("Device Parameters");
                foreach (string name in new[] { "DeviceInterfaceGUIDs", "DeviceInterfaceGUID" })
                {
                    object? value = parameters?.GetValue(name);
                    foreach (string text in value is string[] many ? many : value is string one ? new[] { one } : Array.Empty<string>())
                        if (Guid.TryParse(text, out var guid)) paths.AddRange(Interfaces(guid));
                }
            }
        }
        var matching = paths.Distinct().Where(item => item.Physical && item.Container != Guid.Empty &&
            item.Container == Candidate.ContainerId && item.Path.Contains("vid_057e&pid_2069&mi_01", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (matching.Length != 1) throw new IOException("未找到与此手柄匹配的唯一 WinUSB 命令接口，未向其他设备发送初始化。");
        var commandFile=CreateFile(matching[0].Path,0xc0000000,3,IntPtr.Zero,3,0x40000000,IntPtr.Zero);
        IntPtr usb=IntPtr.Zero;
        try
        {
            if(commandFile.IsInvalid || !WinUsb_Initialize(commandFile,out usb))
                throw new Win32Exception(Marshal.GetLastWin32Error(),"NS2 Pro WinUSB 初始化失败");
            if (!WinUsb_QueryInterfaceSettings(usb, 0, out var descriptor) || descriptor.InterfaceNumber != 1)
                throw new IOException("USB 命令接口编号不匹配。");
            byte endpoint = 0;
            for (byte i = 0; i < descriptor.Endpoints; i++)
                if (WinUsb_QueryPipe(usb, 0, i, out var pipe) && pipe.Type == 2 && (pipe.Id & 0x80) == 0) endpoint = pipe.Id;
            if (endpoint == 0) throw new IOException("USB 命令输出端点不存在。");
            uint timeout = 1500;
            if (!WinUsb_SetPipePolicy(usb, endpoint, 3, 4, ref timeout)) throw new Win32Exception(Marshal.GetLastWin32Error());
            return new CommandChannel(commandFile,usb,endpoint);
        }
        catch
        {
            if(usb!=IntPtr.Zero)WinUsb_Free(usb);
            commandFile.Dispose();
            throw;
        }
    }
    private sealed class CommandChannel(SafeFileHandle file,IntPtr usb,byte endpoint):IDisposable
    {
        public void Write(byte[] packet,CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if(!WinUsb_WritePipe(usb,endpoint,packet,(uint)packet.Length,out var written,IntPtr.Zero) || written!=packet.Length)
                throw new Win32Exception(Marshal.GetLastWin32Error(),"USB 命令发送失败");
        }
        public Task SelectReportAsync(byte id,CancellationToken token)
        { Write(Pro2UsbProtocol.SelectInputReport(id),token); return Task.CompletedTask; }
        public void Dispose() { WinUsb_Free(usb);file.Dispose(); }
    }

    public async ValueTask DisposeAsync() => await input.DisposeAsync().ConfigureAwait(false);

    private static IEnumerable<(string Path, Guid Container, bool Physical)> Interfaces(Guid guid)
    {
        IntPtr set = SetupDiGetClassDevs(ref guid, null, IntPtr.Zero, 0x12);
        if (set == new IntPtr(-1)) yield break;
        try
        {
            for (uint index = 0; ; index++)
            {
                var iface = new DeviceInterface { Size = Marshal.SizeOf<DeviceInterface>() };
                if (!SetupDiEnumDeviceInterfaces(set, IntPtr.Zero, ref guid, index, ref iface)) break;
                var info = new DeviceInfo { Size = Marshal.SizeOf<DeviceInfo>() };
                SetupDiGetDeviceInterfaceDetail(set, ref iface, IntPtr.Zero, 0, out uint required, ref info);
                if (required < 8 || required > 65536) continue;
                IntPtr detail = Marshal.AllocHGlobal((int)required);
                try
                {
                    Marshal.WriteInt32(detail, IntPtr.Size == 8 ? 8 : 6);
                    if (!SetupDiGetDeviceInterfaceDetail(set, ref iface, detail, required, out _, ref info)) continue;
                    string path = Marshal.PtrToStringUni(detail + 4) ?? "";
                    var key = new PropertyKey { Format = new Guid("8c7ed206-3f8a-4827-b3ab-ae9e1faefc6c"), Id = 2 };
                    byte[] container = new byte[16];
                    Guid containerId = SetupDiGetDeviceProperty(set, ref info, ref key, out _, container, 16, out _, 0)
                        ? new Guid(container) : Guid.Empty;
                    bool physical = false, virtualBus = false;
                    uint current = info.Instance;
                    for (int depth = 0; depth < 16; depth++)
                    {
                        var id = new System.Text.StringBuilder(512);
                        if (CM_Get_Device_ID(current, id, 512, 0) != 0) break;
                        string value = id.ToString();
                        if (value.Contains("USBIP", StringComparison.OrdinalIgnoreCase) || value.Contains("SWITCH2EMU", StringComparison.OrdinalIgnoreCase)) virtualBus = true;
                        if (value.StartsWith("USB\\ROOT_HUB", StringComparison.OrdinalIgnoreCase)) physical = true;
                        if (CM_Get_Parent(out uint parent, current, 0) != 0) break;
                        current = parent;
                    }
                    yield return (path, containerId, physical && !virtualBus);
                }
                finally { Marshal.FreeHGlobal(detail); }
            }
        }
        finally { SetupDiDestroyDeviceInfoList(set); }
    }

    [StructLayout(LayoutKind.Sequential)] private struct DeviceInterface { public int Size; public Guid Class; public uint Flags; public UIntPtr Reserved; }
    [StructLayout(LayoutKind.Sequential)] private struct DeviceInfo { public int Size; public Guid Class; public uint Instance; public UIntPtr Reserved; }
    [StructLayout(LayoutKind.Sequential)] private struct PropertyKey { public Guid Format; public uint Id; }
    [StructLayout(LayoutKind.Sequential)] private struct HidAttributes { public int Size; public ushort VendorId, ProductId, Version; }
    [StructLayout(LayoutKind.Sequential)] private struct HidCaps
    {
        public ushort Usage, UsagePage, InputLength, OutputLength, FeatureLength;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 17)] public ushort[] Reserved;
        public ushort LinkNodes, InputButtons, InputValues, InputIndices, OutputButtons, OutputValues, OutputIndices, FeatureButtons, FeatureValues, FeatureIndices;
    }
    [StructLayout(LayoutKind.Sequential, Pack = 1)] private struct UsbInterface { public byte Length, Type, InterfaceNumber, Alternate, Endpoints, Class, Subclass, Protocol, String; }
    [StructLayout(LayoutKind.Sequential)] private struct UsbPipe { public int Type; public byte Id; public ushort MaximumPacketSize; public byte Interval; }
    [DllImport("hid.dll")] private static extern void HidD_GetHidGuid(out Guid guid);
    [DllImport("hid.dll")][return: MarshalAs(UnmanagedType.U1)] private static extern bool HidD_GetAttributes(SafeFileHandle file, ref HidAttributes attributes);
    [DllImport("hid.dll")][return: MarshalAs(UnmanagedType.U1)] private static extern bool HidD_GetPreparsedData(SafeFileHandle file, out IntPtr data);
    [DllImport("hid.dll")][return: MarshalAs(UnmanagedType.U1)] private static extern bool HidD_FreePreparsedData(IntPtr data);
    [DllImport("hid.dll")] private static extern int HidP_GetCaps(IntPtr data, out HidCaps caps);
    [DllImport("hid.dll")][return: MarshalAs(UnmanagedType.U1)] private static extern bool HidD_SetNumInputBuffers(SafeFileHandle file, uint count);
    [DllImport("hid.dll", SetLastError = true)][return: MarshalAs(UnmanagedType.U1)] private static extern bool HidD_FlushQueue(SafeFileHandle file);
    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)] private static extern SafeFileHandle CreateFile(string path, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);
    [DllImport("setupapi.dll", EntryPoint = "SetupDiGetClassDevsW", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr SetupDiGetClassDevs(ref Guid guid, string? enumerator, IntPtr window, uint flags);
    [DllImport("setupapi.dll", SetLastError = true)] private static extern bool SetupDiEnumDeviceInterfaces(IntPtr set, IntPtr info, ref Guid guid, uint index, ref DeviceInterface iface);
    [DllImport("setupapi.dll", EntryPoint = "SetupDiGetDeviceInterfaceDetailW", SetLastError = true)] private static extern bool SetupDiGetDeviceInterfaceDetail(IntPtr set, ref DeviceInterface iface, IntPtr detail, uint size, out uint required, ref DeviceInfo info);
    [DllImport("setupapi.dll", EntryPoint = "SetupDiGetDevicePropertyW", SetLastError = true)] private static extern bool SetupDiGetDeviceProperty(IntPtr set, ref DeviceInfo info, ref PropertyKey key, out uint type, byte[] buffer, uint size, out uint required, uint flags);
    [DllImport("setupapi.dll")] private static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);
    [DllImport("cfgmgr32.dll", EntryPoint = "CM_Get_Device_IDW", CharSet = CharSet.Unicode)] private static extern uint CM_Get_Device_ID(uint instance, System.Text.StringBuilder buffer, uint length, uint flags);
    [DllImport("cfgmgr32.dll")] private static extern uint CM_Get_Parent(out uint parent, uint instance, uint flags);
    [DllImport("winusb.dll", SetLastError = true)] private static extern bool WinUsb_Initialize(SafeFileHandle file, out IntPtr usb);
    [DllImport("winusb.dll")] private static extern bool WinUsb_Free(IntPtr usb);
    [DllImport("winusb.dll", SetLastError = true)] private static extern bool WinUsb_QueryInterfaceSettings(IntPtr usb, byte alternate, out UsbInterface descriptor);
    [DllImport("winusb.dll", SetLastError = true)] private static extern bool WinUsb_QueryPipe(IntPtr usb, byte alternate, byte index, out UsbPipe pipe);
    [DllImport("winusb.dll", SetLastError = true)] private static extern bool WinUsb_SetPipePolicy(IntPtr usb, byte pipe, uint policy, uint length, ref uint value);
    [DllImport("winusb.dll", SetLastError = true)] private static extern bool WinUsb_WritePipe(IntPtr usb, byte pipe, byte[] buffer, uint length, out uint written, IntPtr overlapped);
}

public sealed class Pro2UsbBatteryDeferredException:Exception;

public static class Pro2UsbProtocol
{
    public static byte[] SelectInputReport(byte reportId) =>
        [0x03,0x91,0,0x0a,0,4,0,0,reportId,0,0,0];
    // Wire format: https://github.com/ndeadly/switch2_controller_research/blob/master/commands.md
    public static bool TryEncodeRumble(ReadOnlySpan<byte> raw02, byte id, double gain, out byte[] report, out string error)
    {
        report = new byte[64];
        if (!Pro2BleRumblePacketEncoder.TryEncodeRaw02(raw02, id, report, out _, out error, 1.0)) return false;
        gain=double.IsFinite(gain)?Math.Clamp(gain,0,3):1;
        report[0] = 2; // The physical USB report id replaces the Bluetooth placeholder.
        // USB motor frames use the same packed amplitude fields, with a bounded sum.
        for (int motor = 0; motor < 2; motor++) for (int frame = 0; frame < 3; frame++)
        {
            int offset = 2 + motor * 16 + frame * 5;
            ulong value = 0;
            for (int i = 0; i < 5; i++) value |= (ulong)report[offset + i] << (i * 8);
            // Map the two full-scale components to USB's existing shared budget
            // before applying gain. Previously scaling to 1023 then normalizing
            // erased strength changes well below 100%.
            ulong low=(ulong)Math.Round(((value>>10)&1023)*511.0/2046*gain);
            ulong high=(ulong)Math.Round(((value>>30)&1023)*511.0/2046*gain);
            if(low+high>511)
            {
                ulong sum=low+high;low=low*511/sum;high=high*511/sum;
            }
            value = (value & ~((1023UL << 10) | (1023UL << 30))) | (low << 10) | (high << 30);
            for (int i = 0; i < 5; i++) report[offset + i] = (byte)(value >> (i * 8));
        }
        return true;
    }
    public static byte[][] StartupCommands() =>
    [
        [0x03, 0x91, 0, 0x0d, 0, 8, 0, 0, 1, 0, 0xff, 0xff, 0xff, 0xff, 0xff, 0xff],
        [0x0c, 0x91, 0, 2, 0, 4, 0, 0, 0x27, 0, 0, 0],
        [0x0c, 0x91, 0, 4, 0, 4, 0, 0, 0x27, 0, 0, 0],
        [0x0a, 0x91, 0, 0x08, 0, 0x14, 0, 0, 0x01, 0xff, 0xff, 0xff, 0xff, 0xff, 0xff, 0xff, 0xff, 0x35, 0, 0x46, 0, 0, 0, 0, 0, 0, 0, 0],
        [0x03, 0x91, 0, 0x0a, 0, 4, 0, 0, 5, 0, 0, 0]
    ];
    public static bool IsInputReport(ReadOnlySpan<byte> report) => report.Length == 64 && report[0] == 5;
}
