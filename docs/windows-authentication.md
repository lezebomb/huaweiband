# Windows 真实登录：当前边界与隔离环境实施方案

本机真实用户账户来源是 MicrosoftAccount。当前没有安装 Credential Provider、认证包或修改登录注册表；没有使用自动键盘输入、粘贴密码、关闭 Hello/PIN、替换或过滤系统凭据提供程序。

## 虚拟机首次账户设置

2026-10-18 用户已完成首次设置，已观察到 BandKey-Win11 内的 Windows 企业评估版桌面。此前工作/学校账户 OOBE 不代表安装失败；已向用户说明“登录选项 → 改为域加入”（Domain join instead）的本地账户入口，参考 [Dell Image Assist 的配置说明](https://www.dell.com/community/en/conversations/image-assist/how-to-create-w11-image-using-a-local-windows-user-account/647fa0bef4ccf8a8de5cf735)。后续用户反馈受本地账户限制的实验安装器及正确/错误密码测试正常，记为本地账户认证的用户反馈，未由工具独立观测。

此前桌面水印显示 `26100.ge_release.240331-1435`；随后用户在来宾实际运行图形诊断，截图确认注册表读取结果为 **build 26300 / 26H2，42/42 参考接口检查通过**。版本以该运行结果为准，不再沿用旧水印推断。

虚拟机可用独立本地账户（例如 BandKeyLab），无需绑定工作或学校邮箱。为后续验证锁屏及凭据正确/错误场景，保留独立测试密码；由用户设置，不使用宿主机密码。个人 MicrosoftAccount 的兼容性应在后续单独验证，本地账户测试不能冒充 MSA 登录测试。

微软还提供正式的安装配置接口 [LocalAccounts](https://learn.microsoft.com/en-us/windows-hardware/customize/desktop/unattend/microsoft-windows-shell-setup-useraccounts-localaccounts) 和 [HideOnlineAccountScreens](https://learn.microsoft.com/en-us/windows-hardware/customize/desktop/unattend/microsoft-windows-shell-setup-oobe-hideonlineaccountscreens)，均可在 oobeSystem 阶段配置。它们为后续可重复部署提供研究依据，不是已在这台运行中虚拟机应用成功的方案。本次没有绕过初始化、修改认证注册表、设置空密码或开启自动登录。

只读调用 VMware `listSnapshots` 返回“此操作需要输入密码”，确认加密虚拟机的 CLI 快照读取还需要认证；没有读取凭据管理器或尝试提取加密密码。用户已手动建立“快照 1”；VMware `.vmsd` 元数据、对应 `.vmsn` 及增量磁盘均已核实，恢复操作尚未测试。VMware Tools 尚未验证。用户确认此前黑色截图源于熄屏，已纠正原因判断。激活窗口会导致缩小及输入捕获问题；此后全部虚拟机操作改由用户执行，开发者只准备文件和教程。

## 为什么授权成功还不能解锁

Windows LogonUI 从 Credential Provider 取得序列化凭据，再交给 LSA/认证包校验。手机的 P-256 公钥没有自动成为这个 Windows 账户的登录凭据。`LockWorkStation` 没有对称的普通桌面 `UnlockWorkStation` API。[微软职责说明](https://learn.microsoft.com/en-us/windows/win32/secauthn/credential-providers-in-windows)

已阅读微软 V2 示例 `CSampleCredential::GetSerialization`：本地账户分支打包 `KERB_INTERACTIVE_UNLOCK_LOGON`；非本地身份分支使用带身份提供方标志的 `CredPackAuthenticationBuffer`，然后选择 Negotiate。两者都仍然需要系统可认可的凭据。这个示例不是外部签名登录适配器。[官方示例源码](https://github.com/microsoft/Windows-classic-samples/blob/main/Samples/CredentialProvider/cpp/CSampleCredential.cpp)

当前正式代理只使用 `UnavailableAuthenticationBackend`。新增的认证调度接口能传递验证后的请求、处理错误/超时并核对系统会话；独立测试模式的模拟后端不能报告 Windows 已解锁。没有可调用的真实解锁路径。

## 已完成的独立接口测试

`scripts/test-credential-provider.ps1` 编译固定提交 `434f6002bdf9cf9829406c3ff2b33387982d6168` 的微软 V2 样例，再由 `tests/CredentialProvider.Lab` 通过 DLL 的类工厂直接实例化。源文件应放在 `.research/Windows-classic-samples`，也可通过 `-UpstreamPath` 指定。脚本检查提交及参考源码是否被修改，需要 Visual Studio C++ 工具和 Windows SDK 10.0.26100.0。

```powershell
./scripts/test-credential-provider.ps1
```

本次控制台和图形入口各有 42/42 检查通过：V2 用户枚举、登录/解锁使用场景、字段描述、合成 SID 和 Negotiate 凭据序列化。图形入口的缺少 DLL、报告无法写入两个反例也通过。合成用户不是 Windows 账户，随机测试密码只存在于内存；序列化缓冲区用后清零。没有执行注册脚本、没有加载进 LogonUI、没有调用 LSA 认证。这是宿主机普通进程中的样例接口验证，不是 BandKey Provider，更不是实际登录成功。报告位于本地 `artifacts/credential-provider-lab/`，包含实际执行系统的 build 和 DisplayVersion。

### 来宾图形诊断包

```powershell
./scripts/package-credential-provider-lab.ps1 -CreateIso
```

该脚本先构建及测试，再从已安装的 VS 目录复制通过 Microsoft Authenticode 校验的 VC++ 运行库，按明确文件列表生成本地测试目录、SHA-256 清单及只读数据 ISO。使用 Windows [IMAPI CreateResultImage](https://learn.microsoft.com/en-us/windows/win32/api/imapi2fs/nf-imapi2fs-ifilesystemimage-createresultimage)，不会自动挂载、启用共享文件夹、运行安装程序或修改虚拟机。ISO 内容不含注册脚本、自动运行项或真实账户凭据。

在 VMware 中将该 ISO 连接到 **BandKey-Win11** 的虚拟光驱，用来宾资源管理器打开光盘，运行 `CredentialProvider.Lab.Gui.exe`。不需要管理员权限或 VMware Tools。程序从旁边加载参考 DLL，报告写入来宾 `%LOCALAPPDATA%\BandKeyLab`，窗口显示检查数、实际 OS build、报告路径和三项明确的“未注册 / 未提交认证 / 未测试解锁”。用户已手动运行，截图确认 42/42 及上述三项 NO。该证据仍不是 Windows 实际登录。

## 独立 BandKey VM Provider

新增 `src/BandKey.CredentialProvider`，独立实验 CLSID `{76B424B6-A72D-4E98-9DC1-485E37A5429C}`。使用 V2 Windows 用户枚举绑定唯一 SID，拒绝非本地身份；不支持远程桌面、CredUI 或改密场景，不写过滤器、不替换系统 Provider。上一阶段的模式 0 只允许手动输入密码、不会自动选择；当前新增模式 2 允许有效的一次性模拟签名触发认证，设计见下一节。**均未接入真实手环或正式 C# 后端**。

模式 0 的密码只由用户在虚拟机安全桌面手动输入，不持久保存。固定长度缓冲区在序列化、取消选择、结束回调、系统结果回调及销毁时清零；读字段接口不会返回密码。两种模式都通过 `CredProtect` 保护后，打包正确的 `KERB_INTERACTIVE_LOGON` / `KERB_WORKSTATION_UNLOCK_LOGON` 消息交给 LogonUI。只有后续真实进入桌面，才能记录 Windows 登录成功。[微软接口语义](https://learn.microsoft.com/en-us/windows/win32/api/credentialprovider/nf-credentialprovider-icredentialprovidercredential-getserialization)

`BandKey.VmSetup.exe` 是由用户运行的安装/移除入口。它只接受 VMware 厂商/型号和 build 26300，拒绝远程会话，核对当前交互本地账户与提升后的 SID 一致，并用 `USER_INFO_24` 拒绝连接 Internet 身份的账户。只有用户选择操作后才请求提升；安装文件置于 Program Files 的独立目录，ACL 仅允许 SYSTEM/管理员写入、普通用户读取执行。安装器内嵌 DLL SHA-256，复制前后核对；最后才启用配置，失败时仅清理本次创建的注册和文件。移除仅针对本实验的三个固定注册子树与专用文件，不调整系统 Provider。[账户类型检查](https://learn.microsoft.com/en-us/windows/win32/api/lmaccess/ns-lmaccess-user_info_24)

```powershell
./scripts/test-bandkey-provider.ps1
./scripts/package-bandkey-provider.ps1
```

上一阶段编译及原生静态分析 0 错误/警告，普通进程 85/85 检查通过，覆盖 SID/身份选择、无配置或无凭据拒绝、密码边界、Windows CredProtect/Unprotect 合成往返、登录/解锁结构类型、清理、COM 生命周期、实际 DLL 加载及图标/标签。宿主机只读检查拒绝安装；二进制 CFG/ASLR/NX、系统依赖、静态运行库及旧 ISO 三个载荷哈希已核对。

用户随后反馈旧版安装、正确密码进入桌面、错误密码被拒绝、系统登录保留及移除正常。它是 [手动密码实验](vm-provider-test.md) 的用户反馈；组件故障和快照恢复仍未测。本轮模式 2 的自动认证必须另测，不能沿用旧版结果。

**最新更正：**用户后续截图显示移除注册后仍有文件残留，重启也不能解决，因此完整卸载尚未通过。已实际复现 CopyFile 保留只读属性使旧 DeleteFile 访问拒绝，修复安装/卸载的属性处理并提供具体路径/错误码，原生回归现为 134/134；来宾清理结果待用户用 cleanup fix 1 版本验证。[复制属性语义](https://learn.microsoft.com/en-us/windows/win32/api/winbase/nf-winbase-copyfilew)、[只读删除语义](https://learn.microsoft.com/en-us/windows/win32/api/fileapi/nf-fileapi-deletefilew)。

后续用户已确认 cleanup fix 1 成功移除。再次安装的旧签名版未自动解锁；截图确认选中 BandKey 后仍提示无当前授权。现增加 lifecycle fix 2：凭据取消选择/取消订阅不再直接销毁同一未过期请求，Provider 取消订阅也保留其当前凭据；身份、场景、锁屏/撤权、过期与销毁仍限制有效性。`CredentialsChanged` 会触发重新枚举，不能把 UI 回调等同于安全上下文已终止。[官方回调语义](https://learn.microsoft.com/en-us/windows/win32/api/credentialprovider/nf-credentialprovider-icredentialproviderevents-credentialschanged)。

新增只读可见的分步日志与 20 项普通进程生命周期/签名路径检查，总原生 154/154。日志明确区分 timer-active、approval-verified、refresh-requested、auto-credential、Vault 失败和 serialization-prepared；Windows 回调另记状态。模拟环境只进入测试 EXE，实际系统认证没有在这些测试中提交。新版真实 LogonUI 的消息循环、Vault 及桌面结果仍待一轮用户诊断；尚不能认定此次来宾失败的唯一原因已被确认。操作见新教程。

## 新增：VM 加密保管与模拟签名

安装器现在登记一次当前 VM 本地密码，通过 LogonUser 核对返回 SID；不接收宿主机密码或 PIN。密码以机器 DPAPI 加密，放在 Program Files 独立 Vault，所有者与 DACL 限制为 SYSTEM/管理员。Provider 仅在 SYSTEM 身份读取，并检查实际文件权限、大小、非重解析点及严格字符串边界。机器 DPAPI 若密文可读，同机其他用户也可能解密，故文件 ACL 是必需权限边界；管理员和系统被控制无法由此防护。[DPAPI 机器范围说明](https://learn.microsoft.com/en-us/windows/win32/seccrypto/example-c-program-using-cryptprotectdata)

Provider 在自己的内存中持有随机挑战，验证固定公钥的 P-256 签名、完整正文、SID、会话、30 秒墙钟/单调时钟有效期和当前锁屏/配置，再原子消费并读取密码；提交前再次检查。消息窗口在原 COM apartment 调用 CredentialsChanged，避免跨线程回调。不得把单纯收到文件、布尔授权或客户端“已交付”作为系统认证成功。

只有有效授权才返回自动提交及默认凭据索引；这会使 LogonUI 请求序列化，仍不能保证 Windows 最终接受。[GetCredentialCount](https://learn.microsoft.com/en-us/windows/win32/api/credentialprovider/nf-credentialprovider-icredentialprovider-getcredentialcount)。签名模式隐藏密码字段，提交按钮关联可见说明字段，避免关联已隐藏输入；[GetSubmitButtonValue](https://learn.microsoft.com/en-us/windows/win32/api/credentialprovider/nf-credentialprovider-icredentialprovidercredential-getsubmitbuttonvalue)规定该接口提供按钮相邻字段。

`BandKey.VmConfirmation.exe` 故意使用当前 VM 用户可解密的模拟私钥。每次用户 Arm 只允许一个新请求，可测试正确或错误签名；默认不签、不锁屏。这证明软件流程，不证明设备来源。请求格式见 [Windows grant](windows-grant-wire.md)，与既有手机 DER 协议独立，未来需要显式设备签署及正式策略桥接。

新版原生 119/119、.NET 互通 15/15、原生反向互通 2/2 通过；四个原生工程开启静态分析并将分析警告作为错误，最终 0 错误/警告。宿主机只读检查无注册/变更，常规核心 82/82 与 Windows 集成 11/11 通过。新模式的来宾加密保管、实际自动认证、回调生命周期和移除仍未测试，由用户按 [新教程](vm-signed-confirmation-test.md) 操作。正式后端保持不可用；并无宿主机部署依据。

成熟项目的参考价值：[privacyIDEA](https://github.com/privacyidea/privacyidea-credential-provider) 和 [multiOTP](https://github.com/multiOTP/multiOTPCredentialProvider) 提供 V2/MFA 的工程经验，但其附加验证仍需与 Windows 认可的账户凭据衔接；不能把它们当作当前 MSA 接受任意手环签名的现成方案。未在本机安装这些组件。

## 方案与适用性

| 方法 | 本项目结论 |
|---|---|
| V2 Credential Provider + 加密托管真实账户密码 | 隔离 VM 本地账户实验已实现，等待新模式来宾测试；不能称为无密码方案。仍需评估 MSA/Hello-only/改密/离线条件。内存中短暂恢复凭据；未收集宿主机密码。 |
| Windows Hello / UserConsentVerifier | 用于系统支持的用户验证；普通应用请求验证不能替代 Winlogon 登录，也没有接受本项目任意 BLE 签名的接口。不能拿应用内验证成功当桌面解锁。[API语义](https://learn.microsoft.com/en-us/uwp/api/windows.security.credentials.ui.userconsentverifier) |
| Companion Device Framework | Windows 10 2004 起废弃，不作为新项目基础。[官方弃用说明](https://learn.microsoft.com/en-us/windows/uwp/security/companion-device-unlock) |
| 内置智能卡登录 | 官方路径针对域账户；本机消费级 MSA 不能直接套用。[技术参考](https://learn.microsoft.com/en-us/windows/security/identity-protection/smart-cards/smart-card-windows-smart-card-technical-reference) |
| FIDO2 安全密钥 | Windows 的 Entra 登录部署有账户/加入方式与兼容认证器要求；本手环未被证明是该认证器，不能把任意 ECDSA 变成 FIDO2 Windows 凭据。[部署要求](https://learn.microsoft.com/en-us/entra/identity/authentication/howto-authentication-passwordless-security-key-windows) |
| 自定义 LSA 认证包 | 理论上可建立新信任路径，但属于关键系统安全组件，并涉及 LSA保护、签名、兼容和完整认证设计。本阶段不修改。 |

没有找到能让现有 MSA 在不提供系统认可凭据、也不扩展认证包的情况下直接接受本项目签名的可靠公开实现。该结论是当前研究范围的限制，不是宣称所有第三方登录方案都不可能。

## 后续顺序

1. 用户在现有隔离 VM 测新版无授权/错误签名拒绝、有效签名回桌面、防重复与移除。现有 Ubuntu 保留，快照恢复仍待验证；不用宿主机密码。
2. 补测密码变更、启动首次登录、睡眠、切换用户和回调失败等边界。当前客户端锁屏后才签，不支持冷启动登录，不将这个限制静默扩展为正式功能。
3. Wear Engine 审批通过后补真实按钮回调及稳定距离证据；不能直接让测试私钥升级为正式信任。正式代理仍拒绝 testOnly 设备调用真实后端。
4. 如继续本路线，设计正式设备的原生正文签署、独立权限配置、撤权、IPC 与状态确认，替换 VM 模拟键；保留系统 Provider。正式 MSA/Hello 兼容另行验证。
5. 保持密码不经明文配置、日志、命令行或通用 IPC 传输。Session 0 不调用交互锁屏；凭据提交与桌面会话实际解锁分别记录。

## 虚拟机通过门槛

必须验证：正确/错误凭据、无授权、重复签名、过期、撤权、篡改、服务不可用、蓝牙离线、重启首次登录、普通锁屏解锁、快速用户切换、RDP、睡眠恢复、账户密码变更、MSA离线登录、Hello-only 策略、组件崩溃、系统升级后回归。旧模式正确/错误密码与移除已有用户反馈，其余尚无新模式真实 Windows 登录证据；原生签名攻击模拟通过不能代替来宾行为测试。

回滚方案应先在虚拟机中验证：通过 VM 专用安装器仅移除 BandKey 实验 CLSID/COM 注册及文件、保留系统 Provider；若登录界面不可用，通过 VMware 控制台回退快照。真实电脑上的注册/安装不在本实验范围，安装器会拒绝当前宿主机。本仓库没有通用注册脚本或自动执行注册入口。
