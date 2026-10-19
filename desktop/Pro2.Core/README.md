## 共享业务核心
`Pro2.Core.csproj` 以 `net8.0` 编译，`OriginalSharedSources.props` 将原 Windows 程序的 44 个业务文件直接链接到共享库。Windows 与 macOS 的应用层由同一份 C# 代码执行输入解析、标准键映射、背键/C 宏、摇杆与陀螺仪校准、Stability Guard、虚拟报文和触觉转换。

| 模块 | 原始文件 |
|---|---|
| 物理状态和解析 | `GamepadState`、`Pro2HidReportParser`、电池/电量/设备信息 |
| 按键与宏 | `FaceButtonMapping`、`ButtonRemapping`、`RearButtonMapping` |
| 校准和体感 | `Pro2StickCalibration`、`Pro2GyroBiasCalibration`、`ProfessionalImuPipeline` |
| 摇杆处理与时序 | `Pro2InputStabilityFilter`、`Pro2InputStabilityOptions`、顺序输入队列、回报窗口 |
| 游戏输出数据 | `VirtualPadPackets`、`ViiperDeviceProfile`、`ViiperProtocolClient` |
| 震动与触觉 | `Pro2OutputPacketMapper`、`Pro2BleRumblePacketEncoder`、Switch1/DualSense 转换与预览混音 |
| 配置与诊断 | `V60UserSettings`、路径归一化、会话日志与原始证据记录 |

Core 使用原命名空间 `Y700Switch2V60Viiper` 和原公开 API。Windows GATT、HID/WinUSB、注册表、系统驱动、WPF 页面和启动注册由平台进程提供。`PureSoftware133Mode` 的 release 默认值与原纯软件发布方式一致。

## 验证
```sh
dotnet build desktop/Pro2.Core/Pro2.Core.csproj
dotnet run --project desktop/Pro2.Core.Tests/Pro2.Core.Tests.csproj
```

测试项目直接链接原 `FaceButtonMappingRegressionTests` 与 `QualityRegressionTests`，并增加完整 FD2 按键、模式隔离、宏时序和取消、摇杆校准、Stability Guard、原配置 JSON、震动编码及输入边沿队列的回归检查。

## 原 Windows 工程接入
现阶段原 WPF 工程保留平台宿主职责，新的 Avalonia 应用通过独立进程通信使用其 Windows GATT/USB 和 VIIPER/USBIP 实现。共享应用直接引用 `Pro2.Core`，平台宿主直接引用原 WPF 工程；两个进程各自编译同一份业务源文件。

进一步将原 WPF 工程改为引用 `Pro2.Core` 时，以 `OriginalSharedSources.props` 的文件清单逐项 `Compile Remove` 即可消除同进程重复类型。`InternalsVisibleTo("PRO2ControllerAssistant")` 保留原程序集对内部业务辅助类型的访问。此次源码只拆出 `Pro2InputTransport` 与 `SettingsPathNormalization`，枚举持久值和 Windows 路径规则均沿用原实现。
