using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Y700Switch2V60Viiper;

public static class Pro2TransportSwitchPolicy
{
    public static bool CanAutoSwitch(Pro2InputTransport preference,bool alreadyUsb,bool connectedOrAwaiting,
        int participatingControllers,int freeUsbControllers,bool busy) => preference==Pro2InputTransport.Auto && !alreadyUsb &&
        connectedOrAwaiting && participatingControllers==1 && freeUsbControllers==1 && !busy;
    public static string ActiveLabel(bool switching,bool usb,bool connected) => switching ? "正在切换到 USB 有线" :
        !connected ? "当前输入：未连接" : usb ? "当前输入：USB 有线" : "当前输入：蓝牙无线";
}

public sealed partial class MainViewModel
{
    private Task? transportMonitorTask;
    private bool switchingInputTransport;
    private string transportSwitchNote="";
    public string ActiveInputTransportText => Pro2TransportSwitchPolicy.ActiveLabel(switchingInputTransport,inputSource.IsUsb,inputSource.IsTransportConnected);
    public string ActiveInputTransportHint => (transportSwitchNote.Length>0 ? transportSwitchNote+"\n" : "")+
        "连接偏好："+SelectedInputTransport+"。此处显示程序实际读取的实体连接；插线充电不代表已切换 USB。";

    private async Task MonitorInputTransportAsync(CancellationToken token)
    {
        string failedPath="";
        try
        {
            while (!token.IsCancellationRequested)
            {
                await Task.Delay(1000,token);
                if (shuttingDown || Busy || NormalizeInputTransport(userSettings.InputTransport)!=Pro2InputTransport.Auto || inputSource.IsUsb) continue;
                var participants=pro2Slots.Where(s=>s.InputSource.IsRunning || s.AutoReconnectEnabled || s.VirtualDeviceRunning ||
                    s.Index==1 && AutoReconnectEnabled).ToArray();
                if (participants.Length!=1) continue; // Never guess USB/BLE identity among several controllers.
                var slot=participants[0];
                if (slot.InputSource.IsUsb) continue;
                var usb=await Task.Run(()=>Pro2UsbDevice.Enumerate().Where(c=>!Pro2BleInputSource.UsbPathIsClaimed(c.Path)).ToArray(),token);
                if (usb.Length==0) { failedPath=""; continue; }
                if (!Pro2TransportSwitchPolicy.CanAutoSwitch(slot.InputSource.RequestedTransport,slot.InputSource.IsUsb,true,participants.Length,usb.Length,Busy) ||
                    usb[0].Path==failedPath) continue;
                if (!await operationGate.WaitAsync(0,token)) continue;
                bool resumePrimary=AutoReconnectEnabled;
                var resumeSlots=pro2Slots.Where(s=>s.AutoReconnectEnabled).ToArray();
                Busy=true; switchingInputTransport=true;
                try
                {
                    RefreshCompactPresentation();
                    await StopAutoReconnectAsync(); // Join loops before replacing their physical source.
                    await bleOperationGate.WaitAsync(token);
                    try
                    {
                        token.ThrowIfCancellationRequested();
                        session?.CancelRearButtons(); slot.Session?.CancelRearButtons();
                        Status="检测到 NS2 Pro 数据线，正在从蓝牙切换到 USB 有线…";
                        bool success=await slot.InputSource.SwitchToUsbAsync(usb[0],new Progress<string>(AppendLog),token);
                        failedPath=success ? "" : usb[0].Path;
                        transportSwitchNote=success ? "已从蓝牙切换到 USB 有线，虚拟手柄保持不变。" : "USB 切换未完成，将按连接偏好继续重连。";
                        Status=transportSwitchNote; InputStatus=slot.InputSource.Status;
                        AppendLog("[INPUT_TRANSPORT_SWITCH] usb_verified="+success+" virtual_device_recreated=false slot="+slot.Index);
                        slot.RefreshFromSource();
                    }
                    finally { bleOperationGate.Release(); }
                }
                catch (OperationCanceledException) when(token.IsCancellationRequested) { throw; }
                catch (Exception ex)
                {
                    failedPath=usb[0].Path; transportSwitchNote="USB 切换失败："+FirstLine(ex.Message);
                    Status=transportSwitchNote; AppendLog("[INPUT_TRANSPORT_SWITCH] error="+ex.GetType().Name);
                }
                finally
                {
                    switchingInputTransport=false;
                    try
                    {
                        if (!shuttingDown && !token.IsCancellationRequested)
                        {
                            if (resumePrimary) StartAutoReconnect();
                            foreach (var previous in resumeSlots)
                                await StartPro2SlotAutoReconnectAsync(previous,token);
                        }
                    }
                    finally { Busy=false; operationGate.Release(); RefreshCompactTelemetry(); }
                }
            }
        }
        catch (OperationCanceledException) when(token.IsCancellationRequested) { }
        catch (Exception ex) { AppendLog("[INPUT_TRANSPORT_MONITOR] stopped="+ex.GetType().Name); }
    }
}
