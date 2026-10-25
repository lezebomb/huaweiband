# 微软账户认证后端候选：2026-10-25

## 本阶段完成的代码

将之前只在测试中使用的在线身份封装接入 VM Provider 的真实一次性授权路径。新增 `AccountBinding.h`，区分本地账户与 MicrosoftAccount，拒绝 AzureAD、格式错误和未明确登记的在线身份。现有旧本地账户登记继续可用，不自动升级成微软账户。

新登记同时绑定账户 SID、类型、限定账户名和手机公钥。安装器读取系统账户信息，核对在线身份的本地 SID 与本地账户记录和当前进程令牌一致，再允许用户在 VM 本地输入对应密码；Windows `LogonUser` 必须接受密码并返回同一 SID，才会写入原有 DPAPI／SYSTEM与管理员 ACL 保护的 Vault。密码、PIN 不通过命令行、日志或聊天传递；不接受 Hello PIN 代替账户密码。

原生 Provider 在验签前后检查这组绑定，本地账户使用原有 KERB 路径，明确登记的微软账户使用受保护的在线身份与 Negotiate。LogonUI 改变账户或身份提供方时，取消旧凭据对象；即使外部还持有旧 COM 引用，也不能继续提交旧授权。已消费授权不重复读取 Vault。

所有安装入口仍限定 VMware Windows 11 build26300；物理主机继续被拒绝。正式主机认证后端没有接入，用户的现有 VM 和宿主机都未安装本候选。

## 验证

- 原生 DLL、安装器、确认客户端 Release 构建与静态分析通过，警告作为错误。
- 普通进程 **208/208**，较之前新增22项；覆盖账户格式／类型绑定、变更后的取消、MSA 无授权不读 Vault、有效设备签名后产生受保护在线身份、拒绝重放、旧 COM 引用失效。账户、密码和锁屏上下文为合成输入，使用真实 CNG／SSPI／文件API。
- C#／CNG／Java 签名互操作通过：C#15项、生产Java26项，跨语言篡改拒绝。
- 安装器只读主机检查仍为拒绝，未注册、未修改系统组件；候选安装器内置的 DLL 哈希与实际文件一致。

**尚未用真实微软账户执行登记或 Windows 登录。**普通进程的序列化检查不证明 MSA 网络／缓存认证、Hello-only、账户密码变更、实际 LogonUI 以及主机回滚已经通过。`LogonUser` 如果不支持当前账户或凭据，登记直接失败，不保存，也不退到模拟成功。

## 后续 VM 操作教程（无需现在重装）

当前已通过手环解锁的本地账户 VM 可以继续使用。若要验证主机同类账户，应另外建立 VM 快照，并使用一个专门的微软测试账户；不要把当前主机的个人账户密码输入实验 VM。

1. 保存当前快照，确认 VM 原有系统密码登录可用。保留此前能运行的 ISO 和安装器。
2. 在隔离 VM 中准备微软测试账户并完成其正常系统登录。测试账户必须有自己可用的密码，只有 Hello PIN 的账户不能据此登记。
3. 只有准备好快照和回退方式后，再使用候选 ISO。先用旧安装器移除旧 BandKey 实验登记，保留系统登录方式；如文件被占用按原教程重启清理。
4. 新安装器选择手机确认模式，核对原一加实验公钥。账户由系统解析，用户只在本地对话框输入 VM 测试账户密码。若 Windows 拒绝或 SID 不一致，停止在该处，不改为主机密码或绕过校验。
5. 启动 VM 中继，导入与登记一致的手机公钥；因为换了 Windows 用户，需重新导出该用户的电脑配对资料并在手机核对。先测试手环确认，再测试未确认／拒绝和停止后的旧请求。
6. 只有真实账户登录、失败恢复及卸载验证完成，才评估正式主机部署；不要手动删除 VM 限制或复制 DLL 到主机系统目录。

回滚：使用候选安装器的 Remove，仅移除 BandKey 实验注册与文件，原系统 Provider 保留。无法进入 VM 桌面时，通过 VMware 恢复事前快照。若此次没有安装候选，无需卸载或改动任何系统。

依据：[微软 Credential Provider 样例](https://github.com/microsoft/Windows-classic-samples/blob/main/Samples/CredentialProvider/cpp/CSampleCredential.cpp)、[USER_INFO_24](https://learn.microsoft.com/en-us/windows/win32/api/lmaccess/ns-lmaccess-user_info_24)、[LogonUser](https://learn.microsoft.com/en-us/windows/win32/api/winbase/nf-winbase-logonuserw)。
