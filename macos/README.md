# PRO2 macOS 原生通信层

产品界面和业务逻辑位于 [统一桌面工程](../desktop/README.md)。本目录提供 Swift 原生通信进程 `pro2-native-host` 与命令行诊断，基础系统要求 macOS 13，当前可选 broker 客户端要求 macOS 14.2。

## 设备与进程边界

IOKit 读取同一物理 USB location 的 report 05，通过 interface 1 bulk OUT 执行原始初始化；电量快照临时读取 report 09，完成或超时后恢复 report 05。CoreBluetooth 负责已配对设备恢复、候选发现、GATT 核验、ACK 初始化、校准与电量读取、FD2 订阅、重连和震动写入。

host 向共享 C# 层发送原始报文。按键映射、校准、背键动作和触觉转换由共同业务层处理；JSONL 协议见 [NATIVE_HOST_PROTOCOL.md](NATIVE_HOST_PROTOCOL.md)。进程退出、输入中断与切换输出均释放相应状态。

## 构建与验证

```bash
bash scripts/build-desktop.sh osx-arm64
bash scripts/build-desktop.sh osx-x64
bash macos/scripts/test-core.sh
bash macos/scripts/test-transport.sh
bash macos/scripts/test-broker.sh
```

以上命令从仓库根目录执行。Mac 包含共同 UI、自带 .NET 运行时、原生 host 和虚拟输出客户端；构建产物位于 `artifacts/desktop/`。历史 `macos/scripts/build-app.sh` 转发至相同构建入口，`--universal` 生成 Apple Silicon 和 Intel 两个独立安装包。

## 系统虚拟输出

包内保留 [libvirtualhid MIT 客户端](Vendor/libvirtualhid/README.md) 的来源、校验和及许可证。Xbox、Pro1、DualSense 使用官方签名 broker，安装过程和机器许可证激活见 [上游安装说明](https://docs.lizardbyte.dev/projects/libvirtualhid/master/md_docs_2macos-gamepad.html)。原生 Pro2 虚拟身份保留专用实现入口，诊断明确返回 `profile_unsupported`。

broker bundle/DMG 的再分发按 [LB-SAL](https://github.com/LizardByte/libvirtualhid/blob/v2026.1016.55.45/LICENSES/LicenseRef-LizardByte-SAL-1.0.md) 取得作者授权。软件内安装入口使用官方源和固定 SHA-256，打开原始 DMG，由用户完成官方安装与系统授权。客户端预检仅读取服务、许可状态和设备数量。

## 硬件证据

本机历史 USB 90 秒采集为 22,470 个有效报文，约 250 Hz，并观察到两个摇杆和 L 键。其他实体键、BLE 配对后持续输入、休眠恢复、游戏识别及震动闭环继续记录于 [VALIDATION.md](VALIDATION.md)。蓝牙日志位于 `~/Library/Logs/Pro2Bridge/bluetooth.log`，采用有界日志并隐藏完整设备标识。
