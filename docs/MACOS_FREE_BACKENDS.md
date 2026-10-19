# macOS 免费开源输出方案核验

日期：2026-10-19。设备：Apple M4 / macOS 27.0.1。

## 当前结论

存在免费、开源、完成 Apple 授权签名的 macOS 虚拟手柄软件。为本软件选择输出后端时，优先评估这些实现；当前可选 libvirtualhid broker 的逐台机器许可证属于该厂商的商业方案。

| 项目 | 已核实能力 | 与共同桌面的整合范围 |
|---|---|---|
| [WaveBird](https://github.com/murphyjt/wavebird) | MIT；NS2 Pro BLE；Xbox Wireless、Switch Pro、DS4、DS5；震动；已签名公证发布包 | 最贴合实体手柄，可复用原生虚拟输出实现；USB 和共同核心状态输入需接入 |
| [OpenJoystickDriver](https://github.com/xsyetopz/OpenJoystickDriver) | MIT；现代 CoreHID/IOKit 虚拟手柄；多种兼容身份；已签名公证发布包 | 可复用后端代码；现有 RPC 以同团队客户端的设备管理/配置/自测为主，外部 create/submit 状态接口需扩展 |
| [procon2-mac](https://github.com/caqlayan/procon2-mac) | MIT；USB 使能、BLE、DriverKit 虚拟设备、Feature Report 0x7F 双向通道 | 可研究自有 DriverKit 后端，发行者负责有效签名和对应能力 |
| [Switch2Bridge](https://github.com/SwonDev/Switch2Bridge/blob/main/README.en.md) | MIT；NS2 Pro USB/BLE → Wine SDL shim → winebus XInput | Wine/CrossOver 兼容路径；原生 Mac 游戏的 Steam 注入实验已由作者撤回 |

## 官方发布包的静态实证

以下核验均为下载、只读挂载、签名和元数据检查，应用与驱动保持未运行状态。

| 产物 | 核验结果 |
|---|---|
| WaveBird v0.9.0 | arm64；包内最低系统 15.0；严格 codesign 通过；spctl accepted / Notarized Developer ID；分发 profile 含 hid.virtual.device、ProvisionsAllDevices=true |
| OpenJoystickDriver 0.5.0-beta.4 | arm64+x86_64；严格 codesign 通过；spctl accepted / Notarized Developer ID；分发 profile 含 hid.virtual.device、ProvisionsAllDevices=true |

固定文件 SHA-256：

```
WaveBird-0.9.0.dmg
164b72bf44670371e91dd3b949267dfa440be1c6ccdaeed0fbed6c525229f3d4

OpenJoystickDriver 0.5.0-beta.4 DMG
7a612da4370523650b562f1b09c99f835c67f7bb744388bd5d6b88f3c7680ea7
```

[WaveBird 发行记录](https://github.com/murphyjt/wavebird/releases/tag/v0.9.0)及 [OJD 发行记录](https://github.com/xsyetopz/OpenJoystickDriver/releases/tag/0.5.0-beta.4)保留原始构建与下载来源。WaveBird 主分支 README 的系统要求与实际 v0.9.0 包存在差异，本表采用已检查包的 Info.plist。

## 整合与签名边界

共同 Avalonia 界面、C# 核心、四通道、USB/BLE 接入及配置保持同一维护入口。免费输出后端接入相同的 create/submit/feedback 生命周期，继续使用共同核心已经映射的状态，避免建立第二份产品业务逻辑。

MIT 允许保留版权与许可的源码复用及原样分发。原发布包的 Apple 授权对应原签名团队、App ID 和代码内容；我方修改并编译的程序需使用匹配自身身份的授权与签名。终端用户的免费使用与发行者的 Apple 开发者签名准备分别处理。

OJD 固定版本的 [RPC 鉴权](https://github.com/xsyetopz/OpenJoystickDriver/blob/d43199315ce713cc8c5f543077cf5da616b61efb/Sources/OpenJoystickDriver/Runtime/RPC/Server.swift#L161-L198)检查 signing identifier 和 team；[现有公开命令](https://github.com/xsyetopz/OpenJoystickDriver/blob/d43199315ce713cc8c5f543077cf5da616b61efb/Sources/OpenJoystickDriver/Runtime/RPC/LocalRPCBridge.swift#L138-L271)覆盖设备、配置、兼容身份及自测。把签名发布包用于独立输入代理，需要上游提供受控外部状态接口；复用源码构建我方服务，需要我方签名能力。

## 后续验收

1. 选择受控外部输入接口或自有签名后端，实现共同状态到虚拟设备的提交和退出归中。
2. 对照原 Windows 的按钮、摇杆、传感器和震动黄金报文，保留源协议、单位及轴向。
3. 在 NS2 Pro 实体 USB/BLE 下验证 Steam、GameController 和游戏输入，再验收震动闭环。
4. 将已验证的服务及其许可放入共同安装流程，记录系统授权入口和更新后的签名验证。
