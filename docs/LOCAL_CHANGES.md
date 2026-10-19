# 增强版修改说明

本版本基于 LeonChrome 的 PRO2 手柄助手 v1.3.22（上游提交 `ce66b79`）修改。

上游：https://github.com/LeonChrome/XinHeLianSheng-Pro2-Bridge

增强版标识：`1.3.22-local-rumble-preview`，用于区分原版发行包，不代表原作者发布的新版本。

## 主要修改

- 增加 USB HID 输入、USB / BLE 自动切换和连接方式显示。
- 增加蓝牙历史连接偏好、快速重连检查与失败回退。
- 合并手柄测试与键位映射，增加实体键、背键、C 键编辑及稳定的映射弹出菜单。
- 增加 NS2 Pro 外形重建模型、独立按键位移及肩键 / 扳机动作。
- 重做首页布局和全局深色霓虹主题，减少普通页面的调试信息。
- 增加 USB 原生电量读取及空闲时定期刷新，读取失败时恢复普通输入报告。
- 增加滑块即时试振与短振混合器，修正 USB 震动增益过早饱和的问题。
- 补充相关回归检查和模型生成脚本。

实现集中在 `source/final-three-in-one/windows/v60_viiper_app`，检查位于 `source/final-three-in-one/tools/tests/pure_software_core_test`。保留仓库中的历史硬件实验供参考，不把它们描述为本增强版的必要硬件。

## 测试范围

首次公开增强版 `v1.3.22-enhanced.1`：完整离线回归 56,895 项通过，即时试振专项 47 项通过。离线检查不代表所有硬件和游戏都已验证。

USB 约 250Hz、BLE 约 128Hz 为开发环境的实测结果，不是跨设备保证。手柄按键、摇杆和 USB 短振由使用者确认过；蓝牙耳机音频未完成，不能宣传为支持。

原项目与依赖的许可证、署名和素材来源保持保留。运行时日志、用户设置、蓝牙缓存、设备地址及本机诊断记录不应纳入公开发布包。


## 1.4.0 统一桌面开发版本

新增 `desktop/` 作为 Windows 与 macOS 的共同产品入口。Avalonia 页面对照原 Windows XAML 及资源，业务核心直接链接原 C# 文件；平台 host 负责系统设备与虚拟设备 I/O。独立 SwiftUI 预览界面退出生产目标。

版本号与发布矩阵统一；四页、配置中心、原 3D 模型、背键动作、校准、触觉、四通道、USB 自动接管、托盘/菜单栏和原 Windows 驱动维护进入共同工程。Windows 保留内置原驱动安装器；macOS 包含 MIT 客户端，并在软件内从官方源获取经过校验的独立 broker 安装包。来源与授权要求分别见 `desktop/README.md`、`macos/Vendor/libvirtualhid/README.md`。

测试及硬件证据记录于 `macos/VALIDATION.md`。共享源码、独立宿主进程、安装包构建通过与实体游戏验收分别记录，原生 Pro2 输出及 Professional IMU 实验流水线按明确的后端能力推进。
