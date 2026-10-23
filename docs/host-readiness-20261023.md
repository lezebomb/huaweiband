# 真实主机账户兼容性：2026-10-23

当前主机只读检查确认 Windows 11 build 26200、MicrosoftAccount、Hello 可用，未注册 BandKey VM Provider。检查没有请求 Hello 验证、读取密码或提交 Windows 认证。原始报告仅保存在忽略的 `artifacts/host-readiness-20261023.json`，不上传账户标识。

## 本阶段交付

- `BandKey.exe --host-audit --output <报告路径>`：读取账户类型、Windows build、交互会话、Hello 可用性和实验 Provider 注册状态；不安装任何组件。报告不包含账户名、邮箱、SID、设备公钥或凭据。读取失败返回未知，不能据此准入安装。
- `OnlineCredentialPacking.h`：按微软官方样例，使用 `CRED_PACK_PROTECTED_CREDENTIALS | CRED_PACK_ID_PROVIDER_CREDENTIALS` 为 `MicrosoftAccount\用户` 创建受保护的在线身份序列化；仅接受登录／解锁场景。输出绑定 Provider CLSID 和认证包，调用者须清零释放。
- 此封装尚未接入 VM Provider。现有 VM 组件仍只接受本地账户；真实主机后端仍未启用。

## 已执行的验证

原生 Release 构建、静态分析（警告作为错误）通过，普通进程测试 **186/186**。其中新增28项覆盖两种场景的在线凭据封装、原始输出不含随机假密码、SSPI 标记加密、同一登录会话解密并恢复该随机内容、清零释放及不支持的身份／参数拒绝。临时解密内容仅在测试进程内存中比较和清零，不写报告；没有使用真实 Windows 密码或 PIN。

原生↔C#↔Java 签名互操作继续通过：C# 15项、生产 Java 编码26项，CNG 和 C# 拒绝篡改。VM 安装器只读检查仍拒绝真实主机，未注册或修改登录组件。

在线身份的 `CredUnPackAuthenticationBuffer` 字符串输出可能是编码凭据，因此不把该字符串当明文密码比较。测试通过官方 SSPI 解密与序列化检查真实随机内容。**封装成功不等于账户通过 Windows 认证，更不等于桌面已解锁。**

## 安装前仍需完成

| 项目 | 当前证据 | 缺口 |
|---|---|---|
| 手环→一加→USB／BLE→VM 本地账户 | 用户已确认真实自动返回桌面；等待和拒绝保持锁屏 | 新 indication 传输和后台服务仍待手机回连实测 |
| 微软账户 | 真实主机类型已确认；受保护格式已在普通进程验证 | 正式账户登记、认证衔接、失败处理尚未实现和验证 |
| Windows Hello | 仅检查可用性 | 不代表手环签名可直接用作 Hello 或 Windows 登录凭据 |
| 回滚 | 现有 VM 安装／卸载工具和快照 | 正式主机独立组件的恢复与卸载仍需隔离验证 |

用户已授权条件满足后安装到当前主机，无需再次申请相同授权。现阶段不满足安装条件，不能通过取消 VM 限制来部署实验 DLL。后续认证登记如需账户密码，应由用户在安全本地界面输入；不从聊天、VM 凭据或浏览器中复用。

本阶段回滚只需恢复源代码／删除本地构建产物，因为没有安装主机组件。VM 内没有变更，不需要用户重装当前已验证的版本。

## 官方依据

- [微软 Credential Provider V2 样例](https://github.com/microsoft/Windows-classic-samples/blob/main/Samples/CredentialProvider/cpp/CSampleCredential.cpp)：在线身份的受保护封装与 Negotiate 提交格式。
- [CredPackAuthenticationBuffer](https://learn.microsoft.com/en-us/windows/win32/api/wincred/nf-wincred-credpackauthenticationbufferw)：在线身份格式与同一登录会话保护。
- [CredUnPackAuthenticationBuffer](https://learn.microsoft.com/en-us/windows/win32/api/wincred/nf-wincred-credunpackauthenticationbufferw)：在线身份的字符串可能为编码表示。
- [SspiDecryptAuthIdentityEx](https://learn.microsoft.com/en-us/windows/win32/api/sspi/nf-sspi-sspidecryptauthidentityex)：同一登录会话解密选项。
- [USER_INFO_24](https://learn.microsoft.com/en-us/windows/win32/api/lmaccess/ns-lmaccess-user_info_24)：账户身份提供方查询。
- [CheckAvailabilityAsync](https://learn.microsoft.com/en-us/uwp/api/windows.security.credentials.ui.userconsentverifier.checkavailabilityasync)：Hello 可用性检查。
