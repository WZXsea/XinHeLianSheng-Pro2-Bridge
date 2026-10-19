# macOS 0.1.0 验证记录

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
