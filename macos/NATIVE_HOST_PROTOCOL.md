# PRO2 macOS Native Host IPC v1

Swift 原生辅助进程提供 CoreBluetooth、IOKit 与系统虚拟输出适配。共享 C# 层负责协议解析、校准、滤波、业务映射和跨平台界面。

## 传输

- UTF-8 JSON Lines，经 stdin 请求、stdout 事件；每行一个对象，单行上限 1 MiB。
- 请求使用 `command` 字段；兼容 `cmd`、`type`。可提供 `id` 或 `requestId`，响应原样回传。
- `slot` 为 0、1、2、3，省略时为 0。各通道具有独立物理连接和虚拟输出。
- 命令按 stdin 顺序进入主线程执行。物理连接结果异步通过 `connection` 事件确认。
- 启动事件为 `ready`；stdin EOF、SIGINT、SIGTERM 或 `shutdown` 负责释放所有资源并输出 `stopped`。
- 逐帧 `submitOutput` 和 `rumble` 在提供请求 id 时返回 `commandResult`；省略 id 可减少流量。
- 常规成功事件：`{"event":"commandResult","command":"ping","ok":true,"id":"r1"}`。
- 错误事件：`{"event":"error","code":"invalid_slot","message":"...","id":"r1","slot":4}`。
- 蓝牙诊断文件位于 `~/Library/Logs/Pro2Bridge/bluetooth.log`；stdout 保持 JSONL。

## 请求

| command | 字段 | 行为 |
| --- | --- | --- |
| `enumerate` | `slot?`, `transport:"auto"/"usb"/"bluetooth"`, `manual:bool?` | 持续发现；随后输出 `devices`。普通 BLE 扫描筛选 Nintendo/手柄名称，manual 展示其他候选并在连接阶段验证精确 GATT 身份 |
| `connect` | `slot`, `deviceId`, `transport:"auto"/"usb"/"bluetooth"` | 发起物理连接。USB 等待当前进程枚举完成；BLE 支持已知标识直连。成功以 `connection.connected=true` 及真实 `rawInput` 为准 |
| `switchToUsb` | `slot`, `deviceId` | 将实时 BLE 通道切到明确指定的已枚举 USB 设备。返回 accepted；USB 首帧后发 transportChanged，保持原虚拟输出实例 |
| `disconnect` | `slot` | 释放该通道物理连接及虚拟输出，取消正在进行的 USB 接管 |
| `stop` | `slot?` | 指定通道或全部通道释放，进程继续接受请求 |
| `shutdown` | — | 释放全部资源并结束进程 |
| `settings` | 顶层或 `settings` 内的 `runtimebattery:bool`, `autooptimize:bool`, `skip:bool`, `autoReconnect:bool`, `ps5GyroScalePitch/Yaw/Roll:number` | BLE 配置在后续连接初始化时读取；skip 仅跳过 Windows 对照项 USB 初始化 03/0D |
| `rumble` | `slot`, `raw02:[byte]` 或 `base64`, `gain:0..3?` | 接收共享 C# 生成的完整 64 字节 Pro 2 02 报告，适配为 USB 64 字节或 BLE 33 字节物理封包 |
| `refreshBattery` | `slot` | USB 请求一次 09 快照后恢复 05；BLE 保留 10 分钟周期、空闲 5 秒及实时输入门槛。调用方应已核对操作空闲 |
| `backendStatus` | — | 返回 `backendStatus` 事件，含真实组件、服务及授权状态 |
| `createOutput` | `slot`, `mode:"pro2"/"pro1"/"xbox"/"dualsense"`, `backend:"broker"/"nativeHID"` | 创建指定身份。Broker 对应 Switch Pro、Xbox 360、DualSense；Pro 2 身份返回 `profile_unsupported` |
| `submitOutput` | `slot`, `state:{...}` | 向已创建输出提交共享 C# 处理后的状态 |
| `readFeedback` | `slot`, `limit:0..64?` | 返回 `feedback.events`，包含震动、触发器、LED 与原始输出报告 |
| `destroyOutput` | `slot` | 释放该通道虚拟输出，保留物理连接 |
| `ping` | — | 返回协议版本 |
| `selfTest` | — | 运行 9 组状态适配纯计算验证，覆盖 21 个逻辑按键、轴、触发器、profile 专属 IMU、倍率、电量及非法状态 |

`createOutput` 另支持显式 `mode:"generic"` 测试通用身份。`nativeHID` 的现有实现提供 generic 身份，并要求 `com.apple.developer.hid.virtual.device` 签名授权。目标 Pro 2 身份按其专用后端实现进度单独验收。

### state 契约

```json
{"command":"submitOutput","slot":0,"state":{"buttons":0,"lx":2048,"ly":2048,"rx":2048,"ry":2048,"l2":0,"r2":0,"accelX":0,"accelY":0,"accelZ":4096,"gyroX":0,"gyroY":0,"gyroZ":0,"accelValid":true,"gyroValid":true,"batteryLevel":9,"batteryCharging":false,"motionTimestampUs":123456}}
```

| 字段 | 单位与默认值 |
| --- | --- |
| `buttons` | 21 位共享 C# 逻辑按键标志，默认 0 |
| `lx/ly/rx/ry` | 0..4095，中点 2048，+Y 向上；默认中点 |
| `l2/r2` | 0..4095；默认 0 |
| `accelX/Y/Z` | Int16，NS2 原始计数，4096 counts/g |
| `gyroX/Y/Z` | Int16，NS2 原始计数，16.384 counts/(°/s) |
| `accelValid/gyroValid` | 各自控制对应传感器写入；默认 false |
| `batteryLevel` | 0..9 原生电量档；255 或省略表示等待有效档位 |
| `batteryCharging` | bool |
| `motionTimestampUs` | 共享 C# 运动时间戳；当前 Broker API 负责其报告发送时间戳 |

逻辑按键位序为 `South, East, West, North, L1, R1, L2, R2, Back, Start, LS, RS, DpadDown, DpadRight, DpadLeft, DpadUp, Home, Capture, GR, GL, C`。辅助进程将其适配为 Broker 的 FD2 位序，保持业务层逻辑位序统一。

共享业务层传入完成校准和业务映射、仍采用原 NS2 坐标与计数单位的状态。辅助进程按输出 profile 适配固定版本客户端的最终报告打包系数，以保留原 Windows HID 报文的轴向和灵敏度。Pro 1 使用逆坐标 `(-Y,Z,-X)`，陀螺仪输入除以官方 Switch packer 的 `14.2842`。DualSense 使用 `(+X,+Z,-Y)`；其目标陀螺仪计数除以 `1145*pi/180`，目标加速度计数除以 `9.80665*100` 后提交客户端。

DualSense 的 `ps5GyroScalePitch/Yaw/Roll` 按原设置规则限制为 0.1..4.0、保留 2 位，默认 1。目标 gyro_x/y/z 分别乘对应参数一次；这些参数从下一次输出提交开始生效。Pro 1、Xbox 使用各自原生适配，不应用这组三轴倍率。settings 回执包含 `gyroScales:{pitch,yaw,roll}` 和 `gyroAppliesTo:"nextSubmit"`。

## 事件

| event | 字段 |
| --- | --- |
| `ready` | `protocolVersion:1`, `platform:"macOS"`, `slots:4`, `inputFormats:["usb","fd2"]`, `stateContract:"GamepadState-v1"` |
| `devices` | `slot`, `transport`, `devices:[{id,name,transport}]`；每个 transport 的全量列表 |
| `status` | `slot`, `transport`, `message` |
| `connection` | `slot`, `transport`, `connected`, `deviceId` |
| `transportChanged` | `slot`, `transport:"usb"`, `deviceId`；已验证 USB 首帧后仅提交一次，替代 connection=true，虚拟输出实例保持 |
| `transportSwitchFailed` | `slot`, `deviceId`（目标 USB ID）, `message`, `connected`（恢复后的真实 BLE 输入状态） |
| `rawInput` | `slot`, `format:"usb"/"fd2"`, `base64`, `hostUptimeSeconds`；USB 为完整 64 字节 05 报告，FD2 为 GATT 原始值 |
| `primary` | `slot`, `transport`, `format:"primary"`, `base64`；两种传输均为去除报告 ID 后的 primary payload |
| `factoryCalibration` | `slot`, `address`, `base64`；BLE 原厂 IMU 块 0x13040 或 0x13100 |
| `diagnostics` | `slot`, `transport:"bluetooth"`, `diagnostics:{phase,phaseLabel,bluetoothState,scannedAdvertisements,candidateCount,serviceCount,characteristicSummary,initializationStep,initializationTotal,acknowledgedCommands,ackTimeouts,receivedFrames,malformedFrames,measuredHz,maximumGapMilliseconds,requestedReportRate,reportRateStatus,reconnectAttempt,lastError,controllerFirmware,bluetoothFirmware,dspFirmware,runtimeBatteryStatus,logPath}` |
| `backendStatus` | `ready`, `message`, `broker:{shimAvailable,installed,serviceAvailable,licenseState,code,message,activeDevices,activationLimit,activationUsage,supportedModes,purchaseURL}`, `nativeHID:{supportedModes,requiredEntitlement}`, `pro2:{available,code,message}` |
| `feedback` | `slot`, `events:[{kind,lowRumble,highRumble,leftTriggerRumble,rightTriggerRumble,red,green,blue,playerLEDs,flashingPlayerLEDs,rawBase64}]` |
| `commandResult` | `command`, `ok:true`, 请求相关 id 与结果字段 |
| `error` | `code`, `message`, 可选 slot、请求相关 id；接收端延迟超过 1024 个排队原始帧时包含 `droppedFrames` |
| `stopped` | `reason:"hostShutdown"` |

### USB 热插接管顺序

共享应用仅在 Auto 模式、单个活跃 BLE 源和单个可用 USB 候选时发起接管，并先提交中性输入与中性震动。NativeHost 只处理传入的目标设备 ID。

接管过程中保留原 BLE 对象、虚拟输出对象和逻辑连接；USB 初始化产生的临时连接回调在辅助进程内处理。收到首个真实 USB 05 报告后，辅助进程切换输入、释放 BLE，发出一次 `transportChanged`，随后转发 USB 连接电量和原始输入。原虚拟输出实例保持可用。

USB 尝试失败时，辅助进程等待新的 BLE FD2 报告确认恢复，再发 `transportSwitchFailed.connected=true`。BLE 恢复超时则发 connected=false，并同步物理连接释放事件。停止、断开或显式重新连接会取消接管；每个异步回调都核对所属接管与槽位对象。

## 构建与验证

```bash
swift build --package-path macos --product pro2-native-host
python3 macos/Tests/Pro2NativeHostTests/test_ipc.py /absolute/path/pro2-native-host
```

辅助进程需要作为带蓝牙使用说明的 `.app` 或主应用组成部分打包。Broker shim 可放在辅助进程 bundle 的 `Contents/Frameworks/libPro2BrokerShim.dylib`；显式绝对路径可通过 `PRO2_BROKER_SHIM` 环境变量配置。
