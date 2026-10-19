## 来源与许可
此目录包含 libvirtualhid v2026.1016.55.45 的公开 C++ 头文件与 macOS universal 静态客户端库。文件取自官方发布的 `libvirtualhid-macOS-universal.dmg`，原始下载地址、发布包 SHA-256 和逐文件校验值见 `SOURCE_MANIFEST.json`。

| 内容 | 路径 | 许可 |
|---|---|---|
| 公开头文件 | `include/libvirtualhid/` | MIT |
| universal 静态客户端 | `lib/libvirtualhid.a` | MIT |
| 原始许可证 | `licenses/MIT.md` | MIT 原文 |
| 本项目动态适配层 | 由 `scripts/build-broker-shim.sh` 生成 | Apache-2.0；静态链接的客户端保留 MIT 归属 |

上游的 [License Map](https://github.com/LizardByte/libvirtualhid/blob/v2026.1016.55.45/LICENSES/license-map.md) 将跨平台库、公开头文件和非驱动后端归为 MIT；独立 macOS broker 源码、broker 应用与 DMG 另受 LB-SAL 约束。此仓库仅分发上表中的 MIT 客户端材料。应用内的驱动入口依据 `SOURCE_MANIFEST.json`，从[官方发布源](https://github.com/LizardByte/libvirtualhid/releases/tag/v2026.1016.55.45) 下载原始 DMG，经大小和 SHA-256 校验后缓存，再由系统打开官方安装程序。安装与许可证激活由官方程序处理，仓库和应用安装包包含上述 MIT 客户端材料与下载清单。

## 构建与打包
在 `macos` 目录执行：

```sh
scripts/build-broker-shim.sh
scripts/test-broker.sh
```

构建脚本生成 `lib/libPro2BrokerShim.dylib`，包含 arm64 与 x86_64 两种架构，最低系统版本为 macOS 14.2。应用通过 `dlopen` 加载该适配层；基础应用可保留自身的系统版本要求。打包时复制生成的适配层到 `PRO2 手柄助手.app/Contents/Frameworks/`，并将 `licenses/MIT.md` 放入应用的第三方许可目录。发行签名流程需要给适配层重新签名。

## 输出接口与验收边界
适配层正常调用上游 `Runtime::create(platform_default)`、`Runtime::create_gamepad`、`Gamepad::submit`、`set_output_callback` 和 `get_license_status`。预检读取本机服务和许可证状态，安装、许可证激活与系统隐私授权由用户显式执行。许可证持有人邮箱和密钥均处于上游 broker 内，适配层向 UI 暴露聚合状态。

| 输出 profile | 上游实现 | 当前可验证的范围 |
|---|---|---|
| Generic HID | `generic_gamepad()` | 描述符、状态打包、服务调用 |
| Xbox 360 HID | `xbox_360()` | 描述符、状态打包、服务调用 |
| DualShock 4 USB | `dualshock4_usb()` | 描述符、状态打包、服务调用、反馈回调接口 |
| DualSense USB | `dualsense_usb()` | 描述符、状态打包、服务调用、反馈回调接口 |
| Switch Pro | `switch_pro()` | 描述符、状态打包、服务调用、反馈回调接口 |
| Pro2 原生 `057E:2069` | 上游当前 profile 集合待扩展 | 保留独立后端实现入口 |

`test-broker.sh` 使用真实 MIT 客户端库，验证五种 profile 在内存后端的创建、状态提交和报文长度；并分别测试真实本机 broker 预检、动态模块错误、输入单位与轴向转换。内存验证的证据对应客户端打包路径。游戏识别、震动闭环和实体反馈以安装授权后的本机游戏测试为准。

共享层保留原始 Pro2 计数，其物理标度为加速度 `4096 raw/g`、角速度 `16.384 raw/(°/s)`。macOS 原生输出按固定客户端各 profile 的真实 packer 做适配，以保留与原 Windows 最终 HID 报文相同的计数。Pro 1 使用逆坐标 `(-Y,Z,-X)`，gyro packer 系数为 `14.2842`；DualSense 使用 `(+X,+Z,-Y)`，gyro 和 accel packer 系数分别为 `1145*pi/180` 与 `9.80665*100`。Golden fixture 同时验证客户端实际打包后的陀螺仪、加速度字段。摇杆保持右向和上向为正，按 profile 的协议在上游打包时转换。

## 目标报文运动兼容验证
共享 C# 保留原 NS2 原始计量。原生适配层按目标模式反解官方 packer 的系数，令最终 HID 与原 Windows `VirtualPadPackets` 的坐标和短整型值一致。固定上游源码 [`src/core/report.cpp`](https://github.com/LizardByte/libvirtualhid/blob/v2026.1016.55.45/src/core/report.cpp) 的 DualSense 系数为 `1145*pi<float>/180`（gyro）与 `9.80665F*100`（accel），Switch Pro gyro 系数为 `14.2842F`。这些数值对应当前钉住的 packer 实现。

`Pro2BrokerShim.cpp` 的实际库自测验证完整最终报文：DS5 gyro `(200,900,-800)`、accel `(2000,6000,-4000)`，以及 Switch Pro 三个时间样本的 gyro `(100,200,300)`、accel `(1000,2000,3000)`。Swift NativeHost 同时验证共享源状态到这些原生输入向量的模式适配。
