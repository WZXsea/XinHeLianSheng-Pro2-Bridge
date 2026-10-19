# PRO2 手柄助手 · macOS 移植预览版

原生 SwiftUI 应用，面向 macOS 13 及以上，支持 Apple Silicon，并提供 Intel 交叉编译。协议来自本仓库 Windows 增强版，保留 Apache-2.0 许可证和原项目归属。

## 功能与验收范围

| 层次 | 实现 | 验证范围 |
|---|---|---|
| 有线输入 | IOKit HID report 05，匹配同一物理 USB location 的 interface 1 bulk OUT 初始化 | M4 MacBook Air / macOS 27.0.1 实测 22,470 个有效报文，约 250 Hz |
| 无线输入 | CoreBluetooth，GATT 特征核验、ACK 序列初始化、FD2 通知、断线重连 | 编译、初始化字节核验；实机连接待验收 |
| 输入监测 | ABXY、肩键、扳机、摇杆、方向键、C/GL/GR、原始 IMU | 编码/解析回归测试；实机已读取摇杆与 L 键，其余实体键逐项验收中 |
| 键盘输出 | 自定义实体键映射、左摇杆 WASD、按键边沿发送、断线/静默/退出释放 | 编译、映射编码测试；辅助功能授权与游戏实测待验收 |
| 系统虚拟手柄 | 通用 HID gamepad，21 个按钮和 4 根轴，ABXY 位置交换 | 报文测试；本机创建返回失败，需 Apple 虚拟 HID 签名授权 |
| 电量 | FD2 原生电压与充电标记；解析器支持 primary 0–9 档位 | 当前持续输入为 FD2，界面按实际报文显示 |
| 震动 | BLE 33 字节与 USB 64 字节编码、幅度预算、250 ms 界面试振 | 黄金向量与边界测试；实体试振、游戏反馈待验收 |

当前交付为 0.1.0 预览版。Windows 的 WPF、VIIPER、XInput 与 USBIP 输出路径需要对应的 macOS 原生实现；本目录提供独立 macOS 程序，Windows 工程保留现有构建方式。

## 构建与运行

安装 Apple Command Line Tools 或完整 Xcode 后，从仓库根目录执行：

```bash
bash macos/scripts/test-core.sh
bash macos/scripts/build-app.sh
open 'macos/dist/PRO2 手柄助手.app'
```

构建 Intel/Apple Silicon 通用包：

```bash
bash macos/scripts/build-app.sh --universal
```

产物位于 `macos/dist/`。脚本使用本地 ad-hoc 签名；公开分发可使用开发者证书签名与 Apple 公证。系统的首次运行审核按“系统设置 → 隐私与安全性”提示处理。

## 使用

1. USB：用数据线连接 NS2 Pro，点击“搜索”，选择 USB 设备并连接。初始化仅访问 VID `057e`、PID `2069`、同一物理 location 的 interface 1。输入依赖 HID 权限，命令接口访问错误会显示系统返回码。
2. 蓝牙：长按手柄顶部配对键，点击“搜索”，选择对应设备并连接。应用使用 CoreBluetooth 完成 GATT 发现和订阅，系统蓝牙授权按提示处理。
3. 在“手柄测试”确认按键与摇杆变化。“有效报文”与 Hz 统计来自实际接收，Windows 历史速率仅用于原 Windows 版。
4. 在“输出与映射”选择“键盘映射”，授予应用辅助功能权限，配置各实体键与左摇杆 WASD。按键发送至当前前台应用；切回“输入监测”暂停输出。
5. 点击“断开”、关闭应用窗口、系统睡眠或输入静默超过 500 ms 时，应用释放已发送按键。已选 BLE 设备断线后重新扫描；USB 在相同物理端口重新插接时重连。

连接模式由用户选择。自动 USB/蓝牙切换、Windows 3D 界面、组合/连发/顺序动作、完整 PS5 兼容输出与耳机音频列入后续移植范围。

## 虚拟手柄签名

Apple 的 `IOHIDUserDeviceCreateWithProperties` 要求 `com.apple.developer.hid.virtual.device` entitlement；SDK 头文件与 [Apple 文档](https://developer.apple.com/documentation/bundleresources/entitlements/com.apple.developer.hid.virtual.device) 均明确该要求。本机 ad-hoc 构建的实际检测结果为 `virtual_hid_created=false`。需要取得对应 Apple 授权并使用有效 provisioning profile 和签名后，才能验收系统虚拟手柄输出。

界面提供显式的创建结果。通用 HID 游戏兼容性由具体游戏或 SDL/Steam 的识别情况决定。游戏震动回传需要补充输出描述符、反馈回调与实际游戏验收。

## 诊断

```bash
macos/dist/pro2-diagnostics --scan-usb
macos/dist/pro2-diagnostics --probe-usb --duration 60
macos/dist/pro2-diagnostics --native-controllers
macos/dist/pro2-diagnostics --virtual-hid
macos/dist/pro2-diagnostics --decode fd2 HEX_BYTES
```

`--virtual-hid` 创建成功返回 0，需要授权时返回 2。`--decode` 格式支持 `fd2`、`usb`、`primary`、`standard`、`legacy`；输入为十六进制字节。输入解析使用显式格式，避免不同布局交叉解释。

`--probe-usb` 会初始化首个匹配的手柄，汇总有效报文、实际 Hz、按键并集与四轴原始范围。`--native-controllers` 查询 Apple GameController；本机有线测试期间该接口返回 0 个控制器，IOKit 原始输入已成功读取。

摇杆使用源代码默认中心 2048、死区 64、量程 1600；当前版本保留原始摇杆与 IMU 数据，设备专属校准及陀螺仪偏置估计属于后续范围。电量界面使用实际电压、充电标记与原生档位；精确百分比需额外标定。

## 测试

`test-core.sh` 在完整 Xcode 环境使用 XCTest；仅有 CLT 时，将同一 XCTest 方法体转换为独立 Swift 断言程序，直接编译真实 Core、Output 和 C HID bridge。覆盖输入长度、保留位、两套按钮布局、USB/FD2 对照、电池、初始化序列、震动黄金向量与 USB 幅度预算、21 键映射和四轴方向。

硬件验收应分别记录 USB 与 BLE 的按键/摇杆、拔插/断线、休眠唤醒、实际 Hz、前台游戏键盘输入、权限恢复。系统虚拟手柄与游戏震动在具备对应授权的构建上另行验收。
