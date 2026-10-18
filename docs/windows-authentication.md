# Windows 真实登录：当前边界与隔离环境实施方案

本机真实用户账户来源是 MicrosoftAccount。当前没有安装 Credential Provider、认证包或修改登录注册表；没有使用自动键盘输入、粘贴密码、关闭 Hello/PIN、替换或过滤系统凭据提供程序。

## 虚拟机首次账户设置

2026-10-18 用户已完成 Windows 文件安装，当前实见页面为企业评估版的工作/学校账户 OOBE，尚未进入桌面。该页面不是安装失败的证据。先查看“登录选项”是否提供“改为域加入”（Domain join instead）；该入口可创建本地 Windows 测试账户，参考 [Dell Image Assist 的配置说明](https://www.dell.com/community/en/conversations/image-assist/how-to-create-w11-image-using-a-local-windows-user-account/647fa0bef4ccf8a8de5cf735)。该资料针对较早 Windows 11；本机 26H2 菜单分支尚未点击验证，不能保证该版本一定保留相同文案。

虚拟机可用独立本地账户（例如 BandKeyLab），无需绑定工作或学校邮箱。为后续验证锁屏及凭据正确/错误场景，保留独立测试密码；由用户设置，不使用宿主机密码。个人 MicrosoftAccount 的兼容性应在后续单独验证，本地账户测试不能冒充 MSA 登录测试。

微软还提供正式的安装配置接口 [LocalAccounts](https://learn.microsoft.com/en-us/windows-hardware/customize/desktop/unattend/microsoft-windows-shell-setup-useraccounts-localaccounts) 和 [HideOnlineAccountScreens](https://learn.microsoft.com/en-us/windows-hardware/customize/desktop/unattend/microsoft-windows-shell-setup-oobe-hideonlineaccountscreens)，均可在 oobeSystem 阶段配置。它们为后续可重复部署提供研究依据，不是已在这台运行中虚拟机应用成功的方案。本次没有绕过初始化、修改认证注册表、设置空密码或开启自动登录。

只读调用 VMware `listSnapshots` 返回“此操作需要输入密码”，确认加密虚拟机的 CLI 快照读取还需要认证；没有读取凭据管理器或尝试提取加密密码。Windows 基线快照尚未创建。已找到现有 VMware 安装内的 Windows Tools ISO，需在首次设置完成后继续安装及验证。观察到调用 Computer Use 激活窗口前后，VMware 从最大化恢复到小窗口；原因未确诊，暂避开该调用，账户页面不自动操作。

## 为什么授权成功还不能解锁

Windows LogonUI 从 Credential Provider 取得序列化凭据，再交给 LSA/认证包校验。手机的 P-256 公钥没有自动成为这个 Windows 账户的登录凭据。`LockWorkStation` 没有对称的普通桌面 `UnlockWorkStation` API。[微软职责说明](https://learn.microsoft.com/en-us/windows/win32/secauthn/credential-providers-in-windows)

已阅读微软 V2 示例 `CSampleCredential::GetSerialization`：本地账户分支打包 `KERB_INTERACTIVE_UNLOCK_LOGON`；非本地身份分支使用带身份提供方标志的 `CredPackAuthenticationBuffer`，然后选择 Negotiate。两者都仍然需要系统可认可的凭据。这个示例不是外部签名登录适配器。[官方示例源码](https://github.com/microsoft/Windows-classic-samples/blob/main/Samples/CredentialProvider/cpp/CSampleCredential.cpp)

当前正式代理只使用 `UnavailableAuthenticationBackend`。新增的认证调度接口能传递验证后的请求、处理错误/超时并核对系统会话；独立测试模式的模拟后端不能报告 Windows 已解锁。没有可调用的真实解锁路径。

## 已完成的独立接口测试

`scripts/test-credential-provider.ps1` 编译固定提交 `434f6002bdf9cf9829406c3ff2b33387982d6168` 的微软 V2 样例，再由 `tests/CredentialProvider.Lab` 通过 DLL 的类工厂直接实例化。源文件应放在 `.research/Windows-classic-samples`，也可通过 `-UpstreamPath` 指定。脚本检查提交及参考源码是否被修改，需要 Visual Studio C++ 工具和 Windows SDK 10.0.26100.0。

```powershell
./scripts/test-credential-provider.ps1
```

本次 42/42 检查通过：V2 用户枚举、登录/解锁使用场景、字段描述、合成 SID 和 Negotiate 凭据序列化。合成用户不是 Windows 账户，随机测试密码只存在于内存；序列化缓冲区用后清零。没有执行注册脚本、没有加载进 LogonUI、没有调用 LSA 认证。这是样例接口验证，不是 BandKey Provider，更不是实际登录成功。报告位于本地 `artifacts/credential-provider-lab/tests.json`。

成熟项目的参考价值：[privacyIDEA](https://github.com/privacyidea/privacyidea-credential-provider) 和 [multiOTP](https://github.com/multiOTP/multiOTPCredentialProvider) 提供 V2/MFA 的工程经验，但其附加验证仍需与 Windows 认可的账户凭据衔接；不能把它们当作当前 MSA 接受任意手环签名的现成方案。未在本机安装这些组件。

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
2. 准备独立 Windows 11 虚拟机，创建快照、测试账户、可从宿主机访问的恢复控制台；使用虚拟机测试凭据，绝不使用真实电脑密码。已新建 BandKey-Win11（8 GiB、4 vCPU、80 GiB 稀疏虚拟磁盘、NAT、UEFI/vTPM），官方 Enterprise 26H2 评估镜像已下载并匹配微软 SHA-256，已挂载并进入 Windows 安装程序，尚未完成安装。现有 Ubuntu22.04 保留。
3. 从固定提交的微软 V2 示例建立独立附加 Provider。保持系统 PIN/密码提供程序，不写 Credential Provider Filter，不包装内置 Provider。
4. 若选择密码托管路线，先实现受 ACL 保护的独立凭据保管/代理，评估 DPAPI、Windows Credential Manager、机器/用户范围的真实权限区别。禁止以明文配置、日志、命令行或通用网络/管道消息运输密码。
5. 登录代理与交互用户进程区分权限；IPC 限制 SYSTEM 与明确SID，拒绝远端管道，绑定用户SID、目标会话、挑战和一次性消费。Provider不应相信任意本机进程发来的“已授权”布尔值。
6. Provider 中仅在验证的目标会话授权后提交系统凭据，等待真实认证结果。后台服务不可从 Session 0 直接调用交互锁屏；仍由会话代理调用。

## 虚拟机通过门槛

必须验证：正确/错误凭据、无授权、重复签名、过期、撤权、篡改、服务不可用、蓝牙离线、重启首次登录、普通锁屏解锁、快速用户切换、RDP、睡眠恢复、账户密码变更、MSA离线登录、Hello-only 策略、组件崩溃、系统升级后回归。以上目前均没有 Windows真实登录测试证据。

回滚方案应先在虚拟机中验证：只移除 BandKey 自有 CLSID/COM 注册及文件、保留系统 Provider；若登录界面不可用，使用虚拟机控制台和脱机恢复移除该自有注册并回退快照。真实电脑上的注册/安装另需用户明确确认。本仓库没有可被误执行的注册脚本。
