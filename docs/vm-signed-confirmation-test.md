# BandKey 虚拟机一次性签名确认测试

本包只支持 BandKey-Win11：VMware、Windows 11 x64 build 26300、当前独立本地账户。所有虚拟机操作由用户手动执行，保留 Ubuntu 和“快照 1”。

当前为 **lifecycle fix 2 / diagnostics**。用户已反馈 cleanup fix 1 成功清理残留，重新安装后正确/错误签名都已交付，但有效签名仍需系统密码进入桌面；截图确认已选中 BandKey，显示 No current signed authorization。旧签名版的自动登录测试失败。当前修复登录页刷新提前清理请求的问题并增加诊断，尚不能认定它就是来宾实际失败的唯一原因。本实验未接入真实手环、Wear Engine、BLE 距离或正式代理。

## 1. 安装新实验

1. 若旧版 Remove 在重启后仍提示文件占用，先关闭旧安装器和确认客户端，不要反复重启，也不要删除其他登录组件。
2. 在 VMware 的 **BandKey-Win11** 设置中，将 CD/DVD 的 ISO 换成本次提供的 **BandKey-VM-Signed-Lab-*.iso**，勾选“已连接”。先运行**新光盘**的 `BandKey.VmSetup.exe`，选择 **Remove VM experiment** 并确认 UAC。预期标题含 **cleanup fix 1**，正文为 **Lab registration and files removed**；若仍失败，会列出具体路径和 Windows 错误码，请反馈该提示，先不安装。保留基线快照和 Ubuntu。
3. 在虚拟机“此电脑”打开 `BANDKEY_LAB` 光盘，运行 `BandKey.VmSetup.exe`。主说明应包含 **lifecycle fix 2 with diagnostics**；选择 **Install signed-confirmation VM experiment**，由你确认 UAC。
4. 弹出凭据登记窗口时，只输入**这台虚拟机本地账户的独立密码**。用户名固定，不能使用 PIN；不要输入宿主机密码，也不要把密码发给开发者。此窗口会验证并加密保存该 VM 密码，供这次实验的系统认证使用。
5. 预期显示 **Signed-confirmation VM experiment installed**。登记校验成功只说明密码可用，尚未验证桌面解锁。安装器不会自动锁屏、注销或重启。

本轮优先只复测一次有效确认，使用下面的诊断步骤；确认问题解决后，再完整补测错误签名与防重复。

## 本轮优先：有效确认与分步诊断

1. 运行光盘上的 `BandKey.VmConfirmation.exe`，标题应包含 **lifecycle fix 2 / diagnostics**，下半部分新增 Provider diagnostics 文本框。
2. 点击 **Arm one valid confirmation**，10 秒内从 VM 开始菜单手动锁定，点击锁屏画面进入登录页。在 30 秒有效期内选一次 BandKey 蓝色钥匙。登录页说明也应包含 **lifecycle fix 2**；记录是否需要手动选择，不将手动选择后的成功冒充自动切换成功。
3. 如果仍停留在等待页，可在这轮有效期内点一次右侧箭头。不要等待很久后继续提交旧请求，也不要连续点击。
4. 若未进入桌面，用原有系统密码恢复。回到客户端，将下方 Provider diagnostics 的最后约 20 行复制反馈（点击文本框、Ctrl+A、Ctrl+C）；可以发截图，但不要包含登记窗口、密码、Vault 或私钥文件。

诊断含固定事件名称、启动后的毫秒数和数值结果，不含账号、SID、密码、密钥、nonce 或原始请求。它只是错误定位材料，不参与认证。文件位于 `%ProgramFiles%\BandKeyVmLab\Diagnostics\provider.log`，SYSTEM/管理员可写，目标用户只读，总大小有上限。客户端只显示末尾片段。

| 最后阶段 | 可以确认的范围 |
|---|---|
| challenge-written / waiting-approval | Provider 已发出请求，尚未确认有效响应 |
| approval-rejected | 原生验签或上下文检查拒绝 |
| approval-verified | 原生已核验签名，尚不代表 Windows 认证 |
| refresh-requested / auto-credential | 已请求重新枚举，或已返回可自动提交的凭据索引 |
| timer-active 缺失 | 不能认定窗口定时器已在真实 LogonUI 中运行，需要进一步检查回调环境 |
| vault-*-failed / vault-permission-rejected | 加密凭据读取或权限检查失败 |
| serialization-prepared | 已打包交给 LogonUI，仍不代表实际进入桌面 |
| Windows-result | Windows 回调的数值状态；仍需结合实际桌面结果 |
| challenge-expired / context-cancelled / session-boundary | 请求已过期或会话/策略边界改变 |

登录页重新枚举、UnAdvise 或取消选择本身不会延长请求，也不再直接清掉仍有效的同一请求；密码缓冲区仍立即清零。过期、撤权、会话边界、身份变化及对象销毁继续使请求失效。

如果报告环境不支持、旧目录存在或登记失败，停止本轮安装，记录提示。不要修改机器信息、注册表限制或其他登录组件来绕过检查。

## 2. 无确认与错误签名必须保持锁屏

1. 在虚拟机光盘运行 **BandKey.VmConfirmation.exe**，初始状态应为 **Disarmed**。
2. 在**虚拟机内部**开始菜单，点击用户头像 → 锁定。不要在宿主机按 Win+L。点击锁屏画面进入登录页，等约 10 秒，预期不会自动回到桌面；再用原有系统密码登录。
3. 在确认客户端点击 **Arm one INVALID signature**，10 秒内按同样方式锁定虚拟机并进入登录页。等待约 10 秒，仍应保持锁屏。必要时只选择 BandKey 登录选项观察一次，不要反复提交。
4. 使用原有系统密码进入桌面。客户端应显示 **One INVALID signature delivered**；这说明反例已送达，不表示发生过 Windows 认证。若显示 Expired，说明没有在有效期内完成，应重新做这一轮。

任意一轮在没有有效确认时自动进入桌面，都属于失败：立即停止，Remove 实验组件并反馈。

## 3. 有效签名与防重复

1. 点击 **Arm one valid confirmation**，10 秒内手动锁定虚拟机，点击锁屏画面进入登录页。
2. 预期 Provider 验证签名后向 Windows 提交凭据，随后**实际进入桌面**。若只出现 BandKey 登录选项，可先选择该选项一次；记录是否需要这次选择。不要手动填密码来代替本轮自动认证。
3. 客户端的 **One signed authorization delivered** 只代表签名已交付。必须亲眼确认是否真的进入桌面，才能判断 Windows 登录结果。
4. 回到桌面后**不再点击 Arm**，再次锁定并进入登录页，等待约 10 秒，预期保持锁屏。用原有系统密码进入桌面，证明上次确认没有重复使用。

客户端每次 Arm 仅允许 30 秒内的一个新请求，签名后立即回到 Disarmed。Cancel 或关闭客户端会停止后续签名；已发出的有效签名不能靠关闭客户端撤回，但仍只有一次且受原请求 30 秒有效期限制。测试程序不会自行锁屏。冷启动首次登录、快速用户切换、睡眠恢复等不属于这轮已验证能力。

若 30 秒后没有进入桌面，用原有系统密码恢复；反馈登录页提示和客户端状态即可，不要发送密码、PIN 或 Vault 文件。

## 4. 卸载与回滚

1. 关闭确认客户端，运行**本次光盘**的 `BandKey.VmSetup.exe`，选择 **Remove VM experiment**，确认 UAC。
2. 它清除已知实验文件的只读属性，再删除注册项、DLL、加密 VM 密码和模拟私钥，保留系统登录方式。失败时逐项显示路径/错误码：只有 32/33 表示共享/锁冲突，才需要关闭占用程序或重启；5 是访问拒绝，145 是目录非空，不要仅凭旧提示一直重启。随后锁定一次，用原有系统方式登录，确认 BandKey 实验选项已消失。
3. 若登录界面异常，按 Ctrl+Alt 释放键鼠，在 VMware 对 **BandKey-Win11** 进入“虚拟机 → 快照 → 快照管理器 → 快照 1 → 转到”。这会丢弃该快照之后的来宾状态；快照存在已核实，恢复操作本身尚未测试。不要选择 Ubuntu。

VM 密码变更后应 Remove 并重新登记；本版不会自动同步密码，也不会反复尝试过期密码。不要更改 VM 密码后继续本实验。

## 安全与证据边界

- 密码使用机器范围 DPAPI 加密，存于 `%ProgramFiles%\BandKeyVmLab\Vault\credential.dpapi`，文件所有者与 ACL 仅允许 SYSTEM/管理员；Provider 还会检查实际文件权限。**机器 DPAPI 本身不隔离同机用户**，文件 ACL 是必要边界。管理员或被控制的系统不在保护范围内，删除文件也不等于擦除虚拟磁盘/快照中的历史数据。
- 模拟签名私钥由当前 VM 用户 DPAPI 保护，故意允许测试客户端使用。它不能证明真人点击手环，不能用于正式电脑认证。请求绑定账户 SID、会话、随机 nonce、完整正文与有效期，由 Provider 在内存独立核对，使用后原子消费。
- 本阶段宿主机仅做普通进程测试与只读环境拒绝检查，没有安装 Provider、保存宿主机密码或提交宿主机认证。正式 BandKey 代理仍使用不可用认证后端。

请反馈五项：**无确认保持锁屏 / 错误签名保持锁屏且已送达 / 有效签名实际进入桌面（是否需选择选项）/ 再锁无确认不解锁 / 卸载和原有登录正常**。这些结果会分别记为用户反馈，不伪装成开发工具独立观测。
