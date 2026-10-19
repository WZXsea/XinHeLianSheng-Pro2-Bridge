## Windows 原生平台宿主
此控制台进程通过 JSONL 暴露原 Windows 程序的 BLE/GATT、HID/WinUSB 和 VIIPER/USBIP 能力。项目直接引用原 WPF 工程并调用原 `Pro2BleInputSource`、`Pro2UsbDevice`、`ViiperProtocolClient`、`ControllerEnumerationDiagnostics`，与共享 Avalonia 应用通过进程边界隔离程序集。

| 命令 | Windows 行为 |
|---|---|
| `enumerate` | 原 BLE 手动枚举；普通扫描筛选 Pro2 候选，manual 包含其他广播；USB 使用原实体 HID 枚举 |
| `connect` / `disconnect` | 四通道独立所有权，记录枚举地址类型，原初始化与断线重连流程 |
| `switchToUsb` | 自动偏好下按原单手柄、单 USB 候选政策接管；保留同一 VIIPER 虚拟实例 |
| `rawInput` / `primary` 事件 | `ProcessInputPayload` 入口的原始只读观察副本；USB 恢复完整 05 头 |
| `createOutput` | 原 VIIPER API 创建专用 bus 和指定身份，原 USBIP 单实例挂接验收 |
| `submitOutput` | 提交共享 C# 已处理状态，按原 profile 生成 VIIPER 报文 |
| `readFeedback` | 保留原始反馈报文，交共享 C# 完成振动/HD 触觉转换 |
| `rumble` | 原 Raw02 USB/BLE 转换及物理写回；增益范围 0–3 |
| `settings` | 空闲电量、BLE 优化、自动重连、下次连接的初始化对照开关；另支持 `viiperPath`、`usbipPath`、`apiPort` 和原 PS5 三轴灵敏度 |
| `optimizeBluetooth` | 原 Win11 有限预设优化流程；先确认接收，随后发实际结果 |
| `configureBluetooth` / `restoreBluetooth` | 原 Win10 配置计划、备份及 `runas` 管理员子进程；应用需传预设与明确确认 |
| `refreshSteamCache` | 原 Steam 正常退出/重启服务，保留退出超时与结果 |
| `installDriver` | 原版内嵌 USBIP 安装向导及 UAC；返回安装退出码和重启要求 |
| `readActualOutput` | 原 XInputGetState 实际读回 0–3，区分可用性与提交预览 |
| `backendStatus` | 实际 VIIPER 服务、USBIP 安装状态、模式身份和诊断 |
| EOF / `shutdown` | 中止连接、虚拟输入归中、释放 stream、原 USBIP 顺序卸载、移除专用 bus，结束本宿主启动的服务 |

通用字段和状态格式沿用 `macos/NATIVE_HOST_PROTOCOL.md`。Windows 输出后端取 `backend:"viiper"`。六种模式为 `pro2`、`pro1`、`xbox`、`dualsense`、`dualsenseproimu`、`xboxproimu`。反馈事件附加 `wireFormat:"viiper"`、`mode` 与 `rawBase64`；共享层使用原 `Pro2OutputPacketMapper` 或 `DualSenseHapticRumbleScheduler` 处理。

## 构建
```sh
dotnet build desktop/Pro2.Platform.Windows/Pro2.Platform.Windows.csproj -p:EnableWindowsTargeting=true
dotnet publish desktop/Pro2.Platform.Windows/Pro2.Platform.Windows.csproj -c Release -r win-x64 --self-contained true
bash desktop/Pro2.Platform.Windows/Tests/RunPolicy.sh /absolute/path/dotnet
pwsh desktop/Pro2.Platform.Windows/Tests/Smoke.ps1 -HostExecutable /absolute/path/Pro2.Platform.Windows.exe
```

原 WPF 工程需要其原版 `tools/viiper/haptic-v0.8.0/viiper-haptic-pro1.exe`、对应许可证和 USBIP 许可证。构建成功的证据对应源码和 Windows 目标编译；Windows 蓝牙、管理员驱动权限、实体游戏输入和震动闭环以 Windows 机器上的实际验收为准。

## 原始输入观察接口
`Pro2BleInputSource.RawInputReceived` 接收 `(ownedPayload,isFd2,isPrimary)`。空订阅保持一次委托读取与分支；存在订阅时复制原始字节，订阅者异常被输入所有者隔离。原 parser、计数、队列、校准和通知顺序继续由原流程执行。宿主将原始数据交共享应用再次按同一份业务源文件处理。

## 维护与读回补充契约
长操作先返回 `commandResult`，包含 `accepted:true,pending:true`；完成后发送 `operationResult` 和可直接展示的 `status.message`。这使蓝牙优化期间的逐帧输入输出继续使用原通信通道。

Win10 配置请求使用 `preset:"7p5"|"flexible"|"15"`、`confirmed:true`，实验环境另传 `experimentalConsent:true`，再进入原政策检查与 UAC。`restoreBluetooth` 沿用原始备份恢复入口。所有系统操作均对应显式命令。

`readActualOutput` 请求包含 `slot` 与 `index:0..3`；返回 `event:"actualOutput"`、`source:"XInputGetState"`、`available` 及共享逻辑按键与 12 位轴/扳机，`native` 保留 XInput 原值。此读回只证明系统 XInput 当前观察值。

`battery` 事件来自原 Source 的 `GetBatterySnapshot()`，保留 `level`、`percent`、`charging`、`externalPower`、`powerKnown`、`voltageMillivolts`、`source` 和 `levelAgeSeconds`。该事件补充初始化及 USB 周期采样已经解码的原缓存；原始 `primary` 事件仍只转发真正观测到的通知字节。

运动设置使用 `ps5GyroScalePitch/Yaw/Roll` 和 `professionalInvertGyroPitch/Yaw/Roll`。输入 `state` 保留 NS2 原始单位；Windows 在原 `VirtualPadPackets` PS5 目标构建中应用一次灵敏度。Professional 反转只用于对应的测试模式。

`settings.skipInitialization` 映射原 `SkipUsbInitializationOnNextConnect`。原 Source 在开始连接时冻结本轮策略，当前连接保持既定初始化顺序；诊断包含真实协议审计、原自动优化摘要和初始化读取的固件版本。

`factoryCalibration` 转发初始化实际读取并验证的 0x13040/16 字节和 0x13100/24 字节块，字段为 `slot/address/base64`。原 Windows 读取流程将这些块用于诊断；共享 Parser 沿原算法独立执行静置 bias 校准，原厂块作为有来源的原始证据保留。

原生连接循环每秒调用原 `RecheckLiveRate()`，以五秒真实窗口更新速率与稳定性。自动优化还复用原 `BleLowRateDiagnoser` 和 `CanOptimizeConnectionCompatibility`：持续低速、API 能力、本轮尝试次数和当前测量状态均由原政策控制；优化任务继续保留原始输入与虚拟输出通道。

## 运行中的 USB 接管
`settings.inputTransport` 取 `auto/usb/bluetooth`，映射原 `RequestedTransport`。请求 `switchToUsb` 包含 `slot` 和枚举得到的 USB 路径 `deviceId`。宿主立即回复 `accepted:true,pending:true`，后台复核原 `Pro2TransportSwitchPolicy`：自动偏好、一个连接参与者和一个可用 USB 候选。随后将原虚拟输入归中、释放物理震动，再调用原 `Source.SwitchToUsbAsync`，由原流程先核实 USB 访问能力再替换实体输入。

切换期间，该槽原始输入所有者保持存活；输出提交和震动写入在切换期间暂停，完成后接收新帧。成功事件为 `transportChanged`，包含 `slot/transport:"usb"/deviceId/connected:true/outputRetained:true`。该流程复用现有 VIIPER bus/device/stream。错误统一返回 `code:"usb_switch_failed"` 和关联槽位；原 BLE 仍有效时继续保留，实体输入已断开时另发真实 `connection.connected:false`，由原重连循环处理。`disconnect/stop/EOF` 取消并等待接管任务，成功事件发出前再次核对取消状态。

Host `selfTest` 包含 `transportSwitchPolicyTested:true`。跨平台 `Tests/RunPolicy.sh` 直接提取原政策类并链接宿主实际适配文件，验证 216 种连接/候选/偏好组合、实际连接状态分类和协议名称往返。
