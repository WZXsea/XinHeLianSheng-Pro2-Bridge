using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using Y700Switch2V60Viiper;

internal static class UsbBatteryGetReportProbe
{
    internal static async Task RunAsync()
    {
        var devices=Pro2UsbDevice.Enumerate();
        if(devices.Count!=1)throw new InvalidOperationException("Expected one physical NS2 Pro");
        using var handle=CreateFile(devices[0].Path,0xc0000000,3,IntPtr.Zero,3,0,IntPtr.Zero);
        if(handle.IsInvalid)throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        byte[] report=new byte[64];report[0]=9;
        var watch=Stopwatch.StartNew();
        var result=await Task.Run(()=>
        {
            bool ok=HidD_GetInputReport(handle,report,report.Length);
            return (ok,error:ok?0:Marshal.GetLastWin32Error());
        }).WaitAsync(TimeSpan.FromSeconds(2));
        Console.WriteLine("GET_REPORT success="+result.ok+" error="+result.error+" elapsed_ms="+watch.Elapsed.TotalMilliseconds.ToString("F2")+
            " prefix="+Convert.ToHexString(report.AsSpan(0,20)));
        if(result.ok && Pro2UsbBatteryProbe.TryDecode(report,out var battery))Console.WriteLine(battery.Telemetry);
    }
    [DllImport("kernel32.dll",EntryPoint="CreateFileW",CharSet=CharSet.Unicode,SetLastError=true)]
    private static extern SafeFileHandle CreateFile(string path,uint access,uint share,IntPtr security,uint creation,uint flags,IntPtr template);
    [DllImport("hid.dll",SetLastError=true)][return:MarshalAs(UnmanagedType.U1)]
    private static extern bool HidD_GetInputReport(SafeFileHandle handle,byte[] buffer,int length);
}
