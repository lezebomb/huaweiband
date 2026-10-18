# Windows 真实登录：当前边界与隔离环境实施方案

本机真实用户账户来源是 MicrosoftAccount。当前没有安装 Credential Provider、认证包或修改登录注册表；没有使用自动键盘输入、粘贴密码、关闭 Hello/PIN、替换或过滤系统凭据提供程序。

## 为什么授权成功还不能解锁

Windows LogonUI 从 Credential Provider 取得序列化凭据，再交给 LSA/认证包校验。手机的 P-256 公钥没有自动成为这个 Windows 账户的登录凭据。`LockWorkStation` 没有对称的普通桌面 `UnlockWorkStation` API。[微软职责说明](https://learn.microsoft.com/en-us/windows/win32/secauthn/credential-providers-in-windows)

已阅读微软 V2 示例 `CSampleCredential::GetSerialization`：本地账户分支打包 `KERB_INTERACTIVE_UNLOCK_LOGON`；非本地身份分支使用带身份提供方标志的 `CredPackAuthenticationBuffer`，然后选择 Negotiate。两者都仍然需要系统可认可的凭据。这个示例不是外部签名登录适配器。[官方示例源码](https://github.com/microsoft/Windows-classic-samples/blob/main/Samples/CredentialProvider/cpp/CSampleCredential.cpp)

当前 `UnavailableAuthenticationBackend` 始终返回未认证；`AuthorizationBroker` 从不将 `WindowsUnlocked` 设为 true。没有可调用的伪解锁路径。

## 方案与适用性

| 方法 | 本项目结论 |
|---|---|
| V2 Credential Provider + 加密托管真实账户密码 | 可能实现系统认可的认证；不能称为无密码方案。仍需评估 MSA/Hello-only/密码变更/离线缓存等条件。使用时也需在内存中短暂恢复凭据。未实现、未收集真实密码。 |
| Windows Hello / UserConsentVerifier | 用于系统支持的用户验证；普通应用请求验证不能替代 Winlogon 登录，也没有接受本项目任意 BLE 签名的接口。不能拿应用内验证成功当桌面解锁。[API语义](https://learn.microsoft.com/en-us/uwp/api/windows.security.credentials.ui.userconsentverifier) |
| Companion Device Framework | Windows 10 2004 起废弃，不作为新项目基础。[官方弃用说明](https://learn.microsoft.com/en-us/windows/uwp/security/companion-device-unlock) |
| 内置智能卡登录 | 官方路径针对域账户；本机消费级 MSA 不能直接套用。[技术参考](https://learn.microsoft.com/en-us/windows/security/identity-protection/smart-cards/smart-card-windows-smart-card-technical-reference) |
| FIDO2 安全密钥 | Windows 的 Entra 登录部署有账户/加入方式与兼容认证器要求；本手环未被证明是该认证器，不能把任意 ECDSA 变成 FIDO2 Windows 凭据。[部署要求](https://learn.microsoft.com/en-us/entra/identity/authentication/howto-authentication-passwordless-security-key-windows) |
| 自定义 LSA 认证包 | 理论上可建立新信任路径，但属于关键系统安全组件，并涉及 LSA保护、签名、兼容和完整认证设计。本阶段不修改。 |

没有找到能让现有 MSA 在不提供系统认可凭据、也不扩展认证包的情况下直接接受本项目签名的可靠公开实现。该结论是当前研究范围的限制，不是宣称所有第三方登录方案都不可能。

## 下阶段优先顺序

1. 先取得手环真实按钮回调与稳定距离观测证据，避免为不可用的硬件通道部署登录扩展。
2. 准备独立 Windows 11 25H2 虚拟机，创建快照、测试账户、可从宿主机访问的恢复控制台；使用虚拟机测试凭据，绝不使用真实电脑密码。
3. 从固定提交的微软 V2 示例建立独立附加 Provider。保持系统 PIN/密码提供程序，不写 Credential Provider Filter，不包装内置 Provider。
4. 若选择密码托管路线，先实现受 ACL 保护的独立凭据保管/代理，评估 DPAPI、Windows Credential Manager、机器/用户范围的真实权限区别。禁止以明文配置、日志、命令行或通用网络/管道消息运输密码。
5. 登录代理与交互用户进程区分权限；IPC 限制 SYSTEM 与明确SID，拒绝远端管道，绑定用户SID、目标会话、挑战和一次性消费。Provider不应相信任意本机进程发来的“已授权”布尔值。
6. Provider 中仅在验证的目标会话授权后提交系统凭据，等待真实认证结果。后台服务不可从 Session 0 直接调用交互锁屏；仍由会话代理调用。

## 虚拟机通过门槛

必须验证：正确/错误凭据、无授权、重复签名、过期、撤权、篡改、服务不可用、蓝牙离线、重启首次登录、普通锁屏解锁、快速用户切换、RDP、睡眠恢复、账户密码变更、MSA离线登录、Hello-only 策略、组件崩溃、系统升级后回归。以上目前均没有 Windows真实登录测试证据。

回滚方案应先在虚拟机中验证：只移除 BandKey 自有 CLSID/COM 注册及文件、保留系统 Provider；若登录界面不可用，使用虚拟机控制台和脱机恢复移除该自有注册并回退快照。真实电脑上的注册/安装另需用户明确确认。本仓库没有可被误执行的注册脚本。
