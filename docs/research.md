# 技术依据与路线选择

调研日期：2026-10-17（Asia/Shanghai）。下列设备级结论只覆盖已有证据，没有替代真机验证。

## 本机证据

最终真实用户环境检查：Windows 11 25H2，内核版本 10.0.26200；账户来源 `MicrosoftAccount`，Realtek Bluetooth Adapter 状态 OK。首次沙盒检查得到的 Local 身份不代表真实桌面账户，已纠正。系统原有 .NET 运行时但无 SDK；项目内下载并校验 .NET SDK 10.0.402。已有 VS 2022 C++ 14.44、JDK 21.0.12.1、Android API 35/36、build-tools 36.0.0。

BLE 只读探测在适配器开启时成功接收广播。当前记录没有能可靠归属 Band 11 Pro 的名称或服务标识。不能由此认定“手环没有蓝牙”或“直连一定失败”；也不能把附近随机地址当成这块手环。初次调查时没有 ADB 手机；后续已连接一加，最新的 Classic 连接、指定地址扫描与 SDK 接入证据见 [实机记录](device-validation-20261017.md)。

## 官方与上游来源

| 来源 | 支持的结论 | 没有证明的事情 |
|---|---|---|
| [Gadgetbridge Huawei 支持列表](https://gadgetbridge.org/gadgets/wearables/huawei-honor/) | 列出 Band 11 Pro，标记实验性 | 特定固件与一加/Windows 并发会话可用性 |
| [Huawei 共通功能与问题](https://gadgetbridge.org/basics/topics/huawei-honor/) | 有通知、部分设备回复等，认证密钥存在兼容问题 | 任意设备均支持交互按钮或双主机 |
| [Gadgetbridge 源码](https://codeberg.org/Freeyourgadget/Gadgetbridge) | Band11Pro 使用 HuaweiLECoordinator；Huawei 服务 UUID FE86、写 FE01、读 FE02；实现有认证/绑定阶段 | 可以绕过配对认证读取运动健康会话密钥 |
| [Wear Engine](https://developer.huawei.com/consumer/en/hms/huawei-wearengine/) 与 [服务接入流程](https://developer.huawei.com/consumer/cn/hms/huawei-wearengine/) | 华为提供正式手机/可穿戴开放能力；需要服务申请 | 本项目尚未取得应用服务授权 |
| [官方示例](https://github.com/huaweicodelabs/WearEngine) | Android 使用 HiWear 授权和绑定设备查询 | 旧示例不包含本项目需要的完整通知实现 |
| [模板通知](https://developer.huawei.com/consumer/fr/doc/connectivity-Guides/wearable-device-notification-0000001057412109) | 无手环 App 时仍可显示模板消息；要求运动健康连接与 NOTIFY 权限；反馈值 2/3 对应前两个按钮 | Band 11 Pro/ColorOS 16 对该功能的实际兼容性 |
| [Notification.Builder](https://developer.huawei.com/consumer/fr/doc/connectivity-References/notification_builder-0000001060099091) | 提供按钮内容、模板与 Action 回调配置 | 发送成功等同于点击 |
| [Windows Credential Providers](https://learn.microsoft.com/en-us/windows/win32/secauthn/credential-providers-in-windows) | Provider 收集和序列化凭据，认证由 Windows 安全组件处理；应保留系统提供程序 | 仅返回“外设授权成功”即可登录 |
| [微软 V2 示例](https://github.com/microsoft/Windows-classic-samples/tree/main/Samples/CredentialProvider) | 有真实密码凭据打包的参考实现 | 示例具备外设签名认证、生产级 IPC 或本机免密能力 |
| [Companion Device Framework](https://learn.microsoft.com/en-us/windows/uwp/security/companion-device-unlock) | API 自 Windows 10 2004 起废弃 | 作为新 Windows 11 项目的可维护基础 |
| [LockWorkStation](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-lockworkstation) | 应从交互用户进程调用；异步请求，WTS 通知确认状态 | Session 0 服务能直接锁定交互桌面 |

上游研究副本的提交：

- Gadgetbridge：`0be3c1f02934d9a242c5a44daa64f0cfde9a46f3`。
- WearEngine 示例：`81a98055a479d9f9740374c0a26f27d56be19706`。
- Windows-classic-samples：`434f6002bdf9cf9829406c3ff2b33387982d6168`。

只使用这些副本阅读协议/公开 API。本项目未复制 Gadgetbridge 的 AGPL 代码。官方 SDK 通过华为 Maven 获取，由其条款约束。

## 可复用项目比较

| 项目 | 有用部分 | 采用决定 |
|---|---|---|
| [ProximityD](https://github.com/samueltauil/proximityd) | BLE RSSI、滤波、滞回及界面思路 | 参考行为边界；该项目也区分唤醒登录界面与真实登录，不把靠近当认证 |
| [NearLock](https://github.com/naveenjujaray/NearLock) | 无管理员权限的离开锁屏 | 行为参考，未复制源码 |
| [WinUnlock](https://github.com/imabd645/WinUnlock) | 手机挑战、Credential Provider、服务/交互会话分工 | 文档显示仍需要 DPAPI 加密的 Windows 密码并经管道传给 Provider；不能当作无密码签名登录的证明 |
| [hodor](https://github.com/PsyChip/hodor) | Provider 与管道概念 | 文档接口携带用户密码，未做安全审计，不用于真实电脑 |
| 微软 V2 示例 | 官方的 V2 用户枚举、凭据序列化基线 | 后续隔离环境的优先基线；本机未注册、安装或加载到 LogonUI |

以上 GitHub 项目的功能来自作者公开资料，未声称已在本机安装运行，也未将其作为安全认证保证。

## 路线比较与决定

| 路线 | 当前实用性 | 对现有设备影响 | 维护代价 |
|---|---|---|---|
| Windows 被动 BLE 观测 | 扫描 API 已验证；手环身份和持续广播待确认 | 不主动连接、不写配对 | 低，但随机地址/广播静默需要实测 |
| Windows 作为 Huawei 协议主机 | 有开源协议线索，尚无第二主机会话证据 | 更换认证密钥或重绑定可能干扰运动健康，故未尝试 | 高，受固件认证版本影响 |
| 一加透明桥接 + Wear Engine Notify | SDK/按钮接口有依据，APK可构建；服务授权、硬件能力待确认 | 复用运动健康连接，不重绑 | 中，受厂商服务与 ColorOS 后台策略影响 |
| 手机桥接 + Gadgetbridge | 有协议实现，但可能要换连接管理者 | 可能影响原运动健康，未获得此操作确认 | 高，并有 AGPL 义务 |
| 手环第三方 App / 开发者模式 | 未找到该型号足以据此实施的证据 | 未尝试安装或刷写 | 当前不依赖此路线 |

选择：电脑侧先交付正式被动观测模块，按钮优先做 Wear Engine 桥接实测。直连保留显式 GATT 元数据诊断，不预设直连成功，也不预设绝对不可能。

Wear Engine 模板能否在该型号上实现目标，是现阶段最有价值的验证。如果能力不支持或服务不向该组合开放，将明确记录失败条件，再评估用户授权下的其他协议方案。
