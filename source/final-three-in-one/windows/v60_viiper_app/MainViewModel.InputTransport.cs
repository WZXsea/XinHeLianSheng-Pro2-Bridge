using System;
using System.Collections.Generic;

namespace Y700Switch2V60Viiper;

public sealed partial class MainViewModel
{
    public IReadOnlyList<string> InputTransportOptions { get; } = ["自动（优先 USB）", "仅 USB 有线", "仅蓝牙无线"];
    public string SelectedInputTransport
    {
        get => InputTransportOptions[NormalizeInputTransport(userSettings.InputTransport) switch
            { Pro2InputTransport.Usb => 1, Pro2InputTransport.Bluetooth => 2, _ => 0 }];
        set
        {
            int index = value == InputTransportOptions[1] ? 2 : value == InputTransportOptions[2] ? 1 : 0;
            userSettings.InputTransport = (Pro2InputTransport)index;
            ConfigureInputTransport();
            SaveUserSettings("[INPUT_TRANSPORT]");
            OnPropertyChanged();
        }
    }
    private static Pro2InputTransport NormalizeInputTransport(Pro2InputTransport value) =>
        Enum.IsDefined(value) ? value : Pro2InputTransport.Auto;
    private void ConfigureInputTransport()
    {
        foreach (var slot in pro2Slots) slot.InputSource.RequestedTransport = NormalizeInputTransport(userSettings.InputTransport);
    }
    public string CompactInputSourceLabel => inputSource.IsUsb ? "真实 USB 输入" : "真实 FD2 输入";
    private Pro2ConnectionPresentation UsbPresentation => inputSource.ConnectionPhase switch
    {
        Pro2ConnectionPhase.Initializing => new("USB · 初始化", "正在初始化 USB 手柄", "正在通过数据线启用真实手柄输入。"),
        Pro2ConnectionPhase.Measuring => new("USB · 正在测速", "USB 已连接，正在验证输入", "仅按真实 USB 输入测量，不使用蓝牙回报率。"),
        Pro2ConnectionPhase.Disconnected => new("USB 已断开", "USB 手柄已断开", "请重新插入数据线，自动连接会继续寻找。"),
        _ => new(inputSource.IsGameplayReady ? "USB · 游戏就绪" : "USB · 等待输入",
            inputSource.IsGameplayReady ? "USB 有线已连接" : "USB 输入暂不可用",
            "按键映射和手柄测试同样适用。自动模式下插入数据线会优先切换至 USB。")
    };
}
