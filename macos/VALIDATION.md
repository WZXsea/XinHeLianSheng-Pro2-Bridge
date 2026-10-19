# Windows / macOS 统一桌面验证记录

## 1.4.0-dev.1 · 2026-10-19

本机为 Apple M4 MacBook Air / macOS 27.0.1。Windows 与 Intel Mac 产物在本机完成交叉构建；各目标的运行验证继续由 GitHub Actions 原生环境执行。

| 检查 | 当前证据 |
|---|---|
| 同一业务实现 | Pro2.Core 直接链接原 C# 的 44 个源文件，12 组 / 693 个断言通过 |
| 共同 UI | 同一 Avalonia 四页及原资源，156 个根绑定；四页、键位编辑、进阶动作源码渲染通过 |
| 字体与控件 | 同一 Noto Sans SC 静态字体，Normal/Bold 字重检查；21 键、42 项配置草稿/边界/布局回归和震动拖动试听 |
| 原生协议 | Swift Core 13 组，传输策略 8 组，真实 MIT broker 客户端 5 组通过 |
| 持续交互进程 | JSONL 分片、保持 stdin 打开、EOF、请求关联；9 组原生状态适配自检通过 |
| 共享程序→Mac 原生进程 | 11 组 / 122 个断言通过，包含并发单进程、8 轮重启、句柄回收、4 槽位、USB 接管与停止竞态 |
| 驱动下载 | 官方来源固定版本与 SHA-256；7 组 / 38 个断言，缓存、损坏文件、上限、取消与跨进程幂等通过 |
| Windows 平台 | 原 BLE/USB/VIIPER/USBIP，真实 XInput 读回、原提权维护、216 组合接管策略；Release 编译及发布 0 警告 / 0 错误 |
| 三目标打包 | osx-arm64、osx-x64、win-x64 完整包与 SHA-256 已生成；许可、图标、版本和独立 native 目录核对通过 |
| Mac 本地安装 | 最新共同桌面已装至 /Applications/PRO2 手柄助手.app；主程序与随包通信进程启动成功；旧版已备份 |
| 体感报文 | 按官方真实 packer 的系数转换；DualSense / Pro1 最终 HID 黄金样本与原 Windows 计数对照通过 |

蓝牙配对后的持续实体输入、全部实体按键、蓝牙→USB 热插实体闭环、系统虚拟设备与游戏反馈继续进行实机验收。Mac 的 Xbox / Pro1 / DualSense 输出使用独立 Virtual HID Broker 及机器许可证；Pro2 原生虚拟身份保留专用后端入口。Professional IMU 实验流水线另列功能补全项。

GUI 记录当前包括同源码 Headless 渲染与实际进程启动。桌面截图接口本轮返回 ScreenCaptureKit -3811，完整桌面捕捉与人工交互验收按独立证据记录。新架构中的 USB 历史测量见下表，USB 约 250 Hz 对应当时已接入的实体设备。

## 历史 0.1.0 预览记录

日期：2026-10-19。主机：Apple M4 MacBook Air，macOS 27.0.1，Apple Swift 6.4，Command Line Tools。手柄通过 USB 直连。

| 检查 | 结果 |
|---|---|
| Universal Release | arm64 与 x86_64 编译、lipo 合并、ad-hoc 签名检查通过 |
| 最低系统版本 | Mach-O `LC_BUILD_VERSION` 为 13.0；较旧系统运行兼容性待对应设备验收 |
| 协议/输出测试 | `bash macos/scripts/test-core.sh`，13/13 组通过；当前 CLT 使用相同 XCTest 方法体的独立运行器 |
| USB 枚举 | 找到 `Switch 2 Pro Controller`，VID/PID 为 057e/2069 |
| USB 初始化 | 同物理 location 的 interface 1 bulk OUT 五条命令发送成功 |
| USB 持续输入 | 90 秒采集包含 22,470 个有效 64-byte report 05，250.0016 Hz |
| 摇杆/按键 | 捕获两摇杆变化和 L 键按下/释放；其余实体键继续逐项验收 |
| Apple GameController | 本轮 USB 测试返回 `native_controller_count=0` |
| 虚拟 HID 创建 | 本机返回 `virtual_hid_created=false`，需要 Apple `com.apple.developer.hid.virtual.device` 签名授权 |
| GUI 启动 | 应用启动成功，AX 树确认搜索/连接/测试/映射/日志控件；完整视觉与点击流程待验收 |
| BLE、键盘游戏输入、实体试振 | 实机验收待完成 |

原始 USB 采集输出存于本地 `artifacts/usb-hardware-test.txt`，该目录由仓库忽略规则保护。硬件采集阶段使用输入监测，键盘输出保持由用户在界面中显式启用。

## 源码复核

连接与协议迁移分别经过独立审查，已修复重复连接取消竞态、初始化期间断开后的迟到回调、USB 来源设备隔离、拔插后过期设备选择，以及静默/断线/窗口关闭后的按键释放。蓝牙初始化命令排除原项目的 pairing commit，命令、ACK 和 FD2 特征完整匹配后进入初始化。
