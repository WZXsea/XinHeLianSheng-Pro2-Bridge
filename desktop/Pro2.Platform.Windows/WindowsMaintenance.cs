using System.Diagnostics;
using System.Text.Json;
using Y700Switch2V60Viiper;

namespace Pro2.Platform.Windows;

internal sealed record WindowsConfigurationPlan(bool Restore,Win10BlePresetDefinition Preset,
    IReadOnlyList<string> Arguments,string Confirmation);

/// Native system operations reuse the original Windows implementations and UAC entry points.
internal static class WindowsMaintenance
{
    public static WindowsConfigurationPlan PrepareConfiguration(JsonElement request,bool restore)
    {
        string? id=request.TryGetProperty("preset",out var preset)?preset.GetString():null;
        if(!restore && string.IsNullOrWhiteSpace(id))throw new HostFailure("preset_required","请选择 Win10 蓝牙预设：7p5、flexible 或 15。");
        var selected=restore?Win10BlePresetPolicy.Default:Win10BlePresetPolicy.Get(id!);
        var plan=BleCompatibilityInspector.Refresh().Plan;
        bool confirmed=request.TryGetProperty("confirmed",out var confirm)&&confirm.ValueKind==JsonValueKind.True;
        if(!restore && !confirmed)throw new HostFailure("confirmation_required",BleTurboUiPresentation.Confirmation(plan,selected));
        bool experimental=request.TryGetProperty("experimentalConsent",out var consent)&&consent.ValueKind==JsonValueKind.True;
        var arguments=BleTurboUiPresentation.Arguments(restore,plan,selected,experimental);
        return new(restore,selected,arguments,BleTurboUiPresentation.Confirmation(plan,selected));
    }
    public static async Task<Dictionary<string,object?>> ConfigureAsync(WindowsConfigurationPlan plan,CancellationToken token)
    {
        string executable=Path.Combine(AppContext.BaseDirectory,"PRO2ControllerAssistant.exe");
        if(!File.Exists(executable))throw new HostFailure("legacy_helper_missing","原 Windows 管理员配置组件待补全。");
        var start=new ProcessStartInfo(executable){UseShellExecute=true,Verb="runas",WindowStyle=ProcessWindowStyle.Hidden,WorkingDirectory=AppContext.BaseDirectory};
        foreach(string argument in plan.Arguments)start.ArgumentList.Add(argument);
        var requested=DateTimeOffset.UtcNow;
        using var process=Process.Start(start)??throw new HostFailure("elevation_failed","管理员配置进程等待授权。");
        await process.WaitForExitAsync(token);
        if(process.ExitCode!=0)throw new HostFailure("configuration_failed","原蓝牙配置进程返回退出码 "+process.ExitCode+"；请查看原配置日志。");
        BleCompatibilityInspector.Refresh();
        var completion=BleConfigurationOperationEvidence.Read(plan.Restore?"restore":"apply",plan.Restore?"original":plan.Preset.Id,requested);
        return new(){["success"]=true,["preset"]=plan.Restore?"original":plan.Preset.Id,["known"]=completion.Known,["restartSucceeded"]=completion.RestartSucceeded,["restartRequired"]=completion.RestartRequired,["code"]=completion.Code,
            ["message"]=plan.Restore?"原蓝牙参数恢复流程已完成；请依据系统提示重新连接或重启 Windows。":completion.RestartSucceeded?"蓝牙预设已保存且适配器重启已核实；请重新连接并实测输入。":"蓝牙预设已保存；请依据系统提示重启 Windows 后重新连接实测。"};
    }
    public static async Task<Dictionary<string,object?>> InstallDriverAsync(CancellationToken token)
    {
        var installer=UsbipRuntimeLocator.FindBundledInstaller()??throw new HostFailure("installer_missing","随包 USBIP 原版安装器待补全。");
        using var process=Process.Start(new ProcessStartInfo(installer.InstallerPath){UseShellExecute=true,Verb="runas",WorkingDirectory=Path.GetDirectoryName(installer.InstallerPath)??AppContext.BaseDirectory})??throw new HostFailure("installer_start_failed","USBIP 安装器等待启动与 UAC 授权。");
        await process.WaitForExitAsync(token);
        bool reboot=process.ExitCode is 1641 or 3010;
        if(process.ExitCode!=0 && !reboot)throw new HostFailure("installer_failed","USBIP 安装器返回退出码 "+process.ExitCode+"。");
        var runtime=UsbipRuntimeLocator.Find();
        return new(){["success"]=true,["exitCode"]=process.ExitCode,["rebootRequired"]=reboot,["runtimeLocated"]=runtime!=null,["message"]=reboot?"USBIP 安装流程完成，Windows 要求重启。":"USBIP 安装流程完成；驱动状态可在重新检测后核对。"};
    }
    public static async Task<Dictionary<string,object?>> RefreshSteamCacheAsync(CancellationToken token)
    {
        var result=await SteamControllerCacheService.RestartSteamAsync(token);
        if(!result.Success)throw new HostFailure("steam_refresh_failed",result.Detail);
        return new(){["success"]=result.Success,["steamWasRunning"]=result.SteamWasRunning,["detail"]=result.Detail,["message"]=result.SteamWasRunning?"Steam 已正常退出并重新启动，控制器进程缓存已刷新。":"当前 Steam 处于关闭状态，控制器进程缓存已释放。"};
    }
    public static Dictionary<string,object?> ReadActualOutput(int index)
    {
        if(index is <0 or >3)throw new HostFailure("invalid_index","XInput 读取索引取值为 0、1、2 或 3。");
        try
        {
            bool available=XInputTestReader.TryRead(index,out var state);
            static int Axis(short raw)=>Math.Clamp((int)Math.Round(raw<0?2048+raw*2048d/32768:2048+raw*2047d/32767),0,4095);
            return new(){["event"]="actualOutput",["source"]="XInputGetState",["index"]=index,["available"]=available,["buttons"]=available?(ulong)state.Buttons:0UL,
                ["lx"]=available?Axis(state.LX):2048,["ly"]=available?Axis(state.LY):2048,["rx"]=available?Axis(state.RX):2048,["ry"]=available?Axis(state.RY):2048,["l2"]=available?(int)Math.Round(state.LT*4095d/255):0,["r2"]=available?(int)Math.Round(state.RT*4095d/255):0,
                ["native"]=new{lx=state.LX,ly=state.LY,rx=state.RX,ry=state.RY,lt=state.LT,rt=state.RT},["message"]=available?"系统 XInput 实际读回。":"此 XInput 索引等待系统设备。"};
        }
        catch(DllNotFoundException){return new(){["event"]="actualOutput",["source"]="XInputGetState",["index"]=index,["available"]=false,["code"]="xinput_unavailable",["message"]="系统 XInput 运行时待安装。"};}
    }
}
