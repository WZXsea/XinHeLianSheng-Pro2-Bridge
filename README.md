# PRO2 手柄助手 · 增强版

用于在 Windows 10 / 11 x64 上连接 **Nintendo Switch 2 Pro Controller（NS2 Pro）**，支持 USB 有线和 BLE 蓝牙，以及 Xbox、Pro1、Steam / Pro2、PS5 兼容输出。

基于 [LeonChrome/XinHeLianSheng-Pro2-Bridge](https://github.com/LeonChrome/XinHeLianSheng-Pro2-Bridge) 的 v1.3.22 修改，保留原项目历史、许可证和第三方致谢。本仓库是独立维护的增强版本，未经原作者或 Nintendo 官方认证。

当前程序标识：`1.3.22-local-rumble-preview`，首次增强版发行号为 `v1.3.22-enhanced.1`。

**[下载增强版 Windows x64](https://github.com/WZXsea/XinHeLianSheng-Pro2-Bridge/releases/download/v1.3.22-enhanced.1/PRO2ControllerAssistant-1.3.22-enhanced-win-x64.zip)** · [发行说明](https://github.com/WZXsea/XinHeLianSheng-Pro2-Bridge/releases/tag/v1.3.22-enhanced.1)

请与原版 v1.3.22 的安装包区分。本发行版为预发布版本，欢迎在自己的硬件上验证并报告兼容性问题。

## macOS 移植预览版

新增原生 SwiftUI macOS 工程，提供 USB / BLE 连接层、输入监测、自定义键盘映射、短时试振和虚拟 HID 输出接口。源码、构建和验收范围见 [macOS 说明](macos/README.md)。当前预览版已完成通用包构建、13 组协议/输出回归与 USB 约 250 Hz 实测；蓝牙和游戏兼容性继续进行实机验收，系统虚拟手柄需要 Apple 对应签名授权。

## Windows 增强功能

- **USB 有线输入**：自动模式下插线优先使用 USB，界面显示当前实际连接方式。
- **蓝牙快速重连**：已验证的手柄恢复连接偏好，完成短时输入检查后进入游戏；环境变化或验证失败会回退到完整检查。
- **配置中心**：合并手柄测试和按键映射，支持 ABXY 位置交换、各实体键映射，以及背键 GL / GR、C 键的组合、连发和顺序动作。
- **交互式 3D 手柄**：参照 NS2 Pro 外形重建，可视化按键、摇杆、肩键和扳机动作；点击引线框可直接编辑映射。
- **全局深色霓虹主题**：统一首页、配置中心、震动和设置页面，并遵循系统减少动画的偏好。
- **电量读取**：显示手柄原生 0–9 电量档位。有线模式在满足空闲条件时每 5 分钟尝试刷新，蓝牙模式每 10 分钟更新；不把档位伪装成精确百分比。
- **即时试振**：拖动震动强度滑块或点击“试振”触发短振。100% 为软件 1 倍增益，不能理解为电机最大功率。

本机曾测得 USB 约 250Hz、BLE 约 128Hz。该结果仅代表已测试的环境，其他电脑的实际速率取决于系统、驱动和蓝牙适配器。

## 使用

1. 从本仓库 Releases 下载程序，退出旧版后运行。
2. 使用数据线连接手柄，或长按顶部配对键进入蓝牙配对广播。
3. 按提示安装内置 USBIP 驱动；该步骤可能需要管理员授权或重启。程序自带 .NET 和 VIIPER 运行时。
4. 多数 PC 游戏可先选择 **Xbox / XInput** 输出；在“配置中心”设置键位，在“震动”页调整增益。

## 当前限制

- **蓝牙无线耳机音频尚未实现**。USB 连接时，耳机 / 麦克风由 Windows 原生 USB 音频设备提供，需在 Windows 或游戏中选择对应设备。
- PS5 模式为兼容输出与触觉转换，不等于完整复刻 DualSense。
- 游戏震动需要游戏实际发送反馈，并在游戏内开启震动；软件内“试振”成功不代表所有游戏都会发送震动。
- 电量接口提供原生档位，尚无经验证的精确电量百分比。

## 构建与检查

主要程序：`source/final-three-in-one/windows/v60_viiper_app`。在 Windows x64 安装 .NET 8 SDK 后，从仓库根目录运行：

```powershell
dotnet publish source/final-three-in-one/windows/v60_viiper_app/Y700Switch2V60Viiper.csproj -c Release -p:PureSoftwareRelease=true -o artifacts/pure-software

dotnet run --project source/final-three-in-one/tools/tests/pure_software_core_test/PureSoftwareCoreTest.csproj -c Release -p:PureSoftwareRelease=true -- artifacts/qa-results --rumble-preview-only
```

替换最后一个参数为 `--usb-battery-only`、`--ui-only` 或 `--motion-only` 可运行对应检查。完整构建和后端说明见 [源码说明](source/final-three-in-one/README.md)。

## 来源与许可

[Apache 2.0 许可证](LICENSE) · [原项目与第三方致谢](ACKNOWLEDGEMENTS.md) · [增强版修改说明](docs/LOCAL_CHANGES.md) · [模型和标识素材来源](source/final-three-in-one/windows/v60_viiper_app/Assets/Controller/ASSET-SOURCES.md)

Nintendo、Nintendo Switch、Xbox、PlayStation 等名称及标识属于各自权利人。本项目与这些厂商无隶属关系。
