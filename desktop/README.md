# PRO2 统一桌面工程

## 维护方式

Windows 和 macOS 使用同一套产品界面、版本号、配置格式和 C# 业务实现。功能先在共享层实现；只有系统设备 API 和虚拟设备后端进入对应平台进程。

| 工程 | 职责 | 两端关系 |
|---|---|---|
| Pro2.Desktop | Avalonia 四个页面、3D 手柄、键位编辑、设置与诊断 | 同一份 XAML、C#、主题、模型、图标和字体 |
| Pro2.Application | 四通道、连接状态、输入处理、输出调度、设置与 JSONL 进程通信 | 同一程序集 |
| Pro2.Core | 原始解析、映射、背键、校准、稳定滤波、触觉转换、虚拟报文 | 直接链接原 Windows 工程的 44 个源文件 |
| Pro2.Platform.Windows | 原 BLE/USB、VIIPER、USBIP、系统 XInput 回读 | 独立 Windows 进程，复用原实现 |
| macos/Pro2Native | CoreBluetooth、IOKit、虚拟设备 API 适配 | 独立 Swift 进程；传输原始输入，接收共享层状态 |

共享业务文件清单见 `Pro2.Core/OriginalSharedSources.props`。原始 WPF 工程作为 Windows 平台服务和迁移基准保留。旧 SwiftUI 预览界面已从生产目标退出。

## 开发入口

版本只修改 `Directory.Build.props` 中的 `Version`。常规界面修改进入 `Pro2.Desktop`，业务修改进入链接的共享原文件或 `Pro2.Application`；平台 API 修改进入各自原生 host。Windows helper 与共同主程序放在不同进程和目录，分别加载原版平台程序集与共享程序集。

.NET SDK 版本由根目录 `global.json` 指定，Avalonia 依赖固定版本。使用 `PRO2_DOTNET` 可以指定 SDK 可执行文件路径。

```bash
dotnet run --project desktop/Pro2.Core.Tests -c Release
bash scripts/build-desktop.sh osx-arm64
bash scripts/build-desktop.sh osx-x64
bash scripts/build-desktop.sh win-x64
```

产物：`artifacts/desktop/PRO2-版本-运行目标.zip`。Mac 应用位于同目录对应运行目标的 `PRO2 手柄助手.app`；Windows 主程序位于 `win-x64/publish/PRO2ControllerAssistant.exe`。包内包含 .NET 运行时。Mac 本地构建使用 ad-hoc 签名，公开发行的 Developer ID 签名与公证需发行者证书。

## 同步更新与发布

`.github/workflows/desktop.yml` 每次变更同时构建 Windows x64、Apple Silicon 和 Intel Mac，并运行共享回归、各平台离线协议与宿主进程测试。三个目标全部成功后，`v1.*` 版本标签生成同一发行草稿并附三个安装包。草稿审阅完成后统一发布，用户使用同一版本发行页获取对应平台包。

```bash
dotnet run --project desktop/Pro2.Desktop.Tests -c Release
bash macos/scripts/test-core.sh
bash macos/scripts/test-transport.sh
bash macos/scripts/test-broker.sh
python3 macos/Tests/Pro2NativeHostTests/test_ipc.py 'artifacts/desktop/osx-arm64/PRO2 手柄助手.app/Contents/Helpers/pro2-native-host'
dotnet run --project desktop/Pro2.Application.Tests -c Release -- 'artifacts/desktop/osx-arm64/PRO2 手柄助手.app/Contents/Helpers/pro2-native-host'
```

Headless 测试从相同控件渲染四页与键位编辑器，检查绑定并输出到 `artifacts/ui/`。渲染结果证明布局与资源加载；实体连接、系统枚举和游戏反馈分别记录硬件验收。

## 驱动与平台能力

Windows 包内含原 USBIP 安装器和 VIIPER 运行时；首次系统安装使用原管理员授权流程。Mac 包内含 MIT 虚拟设备客户端及动态适配层。独立 Virtual HID Broker 使用 LB-SAL 许可，随包再分发需作者授权；软件安装入口可从官方源下载、校验并打开原始安装包。机器许可证按上游流程激活。下载器保留原始安装包，核对固定大小及 SHA-256 后原子缓存；并发点击复用同一次获取。

| 功能 | Windows 平台 | macOS 平台 |
|---|---|---|
| USB / BLE 输入 | 原 Windows 实现，单手柄 USB 自动接管 | IOKit / CoreBluetooth，同一接管策略 |
| 界面、配置、映射、校准、背键动作 | 共同实现 | 共同实现 |
| Xbox / Pro1 / DualSense 输出 | 原 VIIPER / USBIP | Virtual HID Broker，需要系统服务及机器许可证 |
| Pro2 原生输出 | 原 VIIPER 身份 | 专用虚拟身份后端待接入 |
| 实际输出回读 | XInputGetState，槽位 0–3 | 当前界面展示已提交状态 |
| Win10 蓝牙栈维护 | 原 Windows 提权工具与确认流程 | macOS 使用系统蓝牙设置 |
| 耳机音频 | 原系统 USB 音频路径；无线音频仍按原项目范围 | 依赖系统 USB 音频枚举；单独验收 |

高级 Professional IMU 实验模式的原始代码仍在共享 Core 内；端到端校准/积分流水线、双端实体运动方向及游戏兼容性按独立验收项推进。跨平台共同版本与后端能力在界面状态、诊断和发行说明中分别表达。
