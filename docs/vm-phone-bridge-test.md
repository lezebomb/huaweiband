# VM 手机确认联调教程

本阶段交付的是「手机明确点击 → 固定证书 HTTPS → 原生一次性签名 → VM 本地账户认证」的软件链路。手机测试按钮代替手环；不验证距离，不调用 Wear Engine。旧版 **lifecycle fix 2 本地模拟确认**已获用户有效解锁及三项反例反馈；本次手机链路是新版本，必须重新在 VM 验证，不能沿用旧结果。

所有 VM 操作由用户执行。保留 BandKey-Win11 的基线快照和原有系统密码登录；不要选择 Ubuntu。不要在主力 Windows 安装实验 Provider，也不要输入宿主机密码或 PIN。

## 1. 准备文件和手机公钥

1. 本次镜像名为 `BandKey-VM-Phone-Lab-*.iso`，挂到 **BandKey-Win11** 的 CD/DVD，保持已连接。镜像不自动运行，不引导安装系统。
2. 光盘根目录有 `BandKey.VmSetup.exe`、Provider DLL、旧本地模拟客户端、`BandKey-bridge-debug.apk`、`VmBridge` 文件夹和本教程。把 **整个 VmBridge 文件夹**复制到 VM 的 `C:\BandKeyPhoneLab\VmBridge`。不单独复制 EXE；同目录包含自带的 .NET 运行时。
3. 将 APK 复制到手机，覆盖安装现有 BandKey 桥接应用。使用同一开发签名，保留应用数据；不要卸载旧 App。首次覆盖与新页实机兼容性仍待验证。APK 是调试版本。
4. 手机打开 BandKey → **VM 手机确认实验**。页面显示手机实验公钥的完整 SHA-256。点击 **1. 导出手机实验公钥**，保存到下载目录，文件后缀 `.bandkeypub`，大小应为 **72 字节**。
5. 将这个公开文件复制进 VM，例如 `C:\BandKeyPhoneLab\bandkey-vm-phone.bandkeypub`。可以用已经配置的共享文件传输；没有 VMware Tools 时，可在 VMware 的「虚拟机 → 可移动设备」把手机 USB 连接给 BandKey-Win11，在手机选文件传输，通过资源管理器复制下载目录文件。文件复制完后将 USB 断开回宿主机也可以，后续用局域网通信。

此文件只有公钥。不要复制任何 `.dpapi`、Vault 或私钥。手机实验密钥使用独立 Android Keystore 别名，与原有 Wear Engine 桥接身份分开；卸载/清除应用数据会丢失密钥，需要重新登记。

## 2. 在 VM 登记手机确认模式

1. 关闭旧 `BandKey.VmConfirmation.exe`。运行**本次光盘**上的 `BandKey.VmSetup.exe`，先选择 **Remove VM experiment**，确认显示注册及文件已移除。若提示共享冲突，关闭占用程序或重启 VM 后再移除；其他错误按路径/错误码处理，不叠加安装。
2. 再运行安装器，选 **Install phone-confirmation VM experiment**。不要选 `Install local simulation experiment`；后者继续使用旧 Arm 客户端。
3. 确认 UAC，在文件选择框选择刚复制的 `.bandkeypub`。**逐字核对安装器与手机显示的完整 SHA-256 相同**后继续。
4. 在 Windows 凭据窗口由你输入**这台 VM 当前本地账户的独立密码**。它仍以机器 DPAPI + SYSTEM/管理员专用 ACL 保存；手机、中继、配置 JSON 不接收此密码。实验内部最终使用的是 Windows 可接受的本地账户凭据，不能称为系统无密码登录。
5. 预期提示 **Phone-confirmation VM experiment installed**。此模式不生成 `Exchange\signer.dpapi`，旧本地 Arm 程序也会拒绝手机模式。

## 3. 让手机能连到 VM

先保持 VM 当前 NAT 网络。用 VM 中 `ipconfig` 查它的 IPv4，例如 **192.168.157.128**；宿主机中查与手机同一 Wi-Fi 的 IPv4，例如 **192.168.1.20**。示例地址必须替换成实际值。

1. VMware「编辑 → 虚拟网络编辑器 → 更改设置 → VMnet8 → NAT 设置 → 添加」：宿主机端口 **19443**，类型 **TCP**，虚拟机 IP 填实际 VM IPv4，虚拟机端口 **19443**。只添加这一条映射，不重置网络，不修改 Ubuntu 设置。此流程依据 [Broadcom 的 NAT 端口转发说明](https://knowledge.broadcom.com/external/article?legacyId=2006955)。
2. 手机和宿主机接同一可信家庭 Wi-Fi。访客网络/客户端隔离可能阻断访问。不要在路由器做公网端口转发。
3. 在 VM 打开 `C:\BandKeyPhoneLab\VmBridge\Start-VM-Bridge.cmd`（或同目录 `BandKey.exe --vm-bridge`），导入与安装器相同的手机公钥。
4. **本机监听 IPv4**填 VM 实际 IPv4，**手机访问地址**填 `https://宿主机WiFi地址:19443`。默认 `127.0.0.1` 只供本机使用，手机不能通过它访问 VM。
5. 需要防火墙允许时，只开放这个端口和所需来源，保留防火墙启用。下面命令由用户在对应系统的管理员 PowerShell 中运行；只使用实际地址，不直接照抄示例。

VM 规则示例（NAT 转发后的来源由 VMware 决定，先限定 VM NAT 所在子网；若网络被识别为公用，此规则仍只匹配指定程序、端口和 NAT 子网）：

```powershell
New-NetFirewallRule -Name BandKeyVmPhoneLab -DisplayName 'BandKey VM phone lab' -Direction Inbound -Action Allow -Protocol TCP -LocalPort 19443 -Program 'C:\BandKeyPhoneLab\VmBridge\BandKey.exe' -RemoteAddress '192.168.157.0/24' -Profile Any
```

宿主机规则示例，只允许实际手机 IP 和家庭专用网络：

```powershell
New-NetFirewallRule -Name BandKeyVmPhoneForward -DisplayName 'BandKey VM phone forward' -Direction Inbound -Action Allow -Protocol TCP -LocalPort 19443 -RemoteAddress '192.168.1.30' -Profile Private
```

规则参数见 [Microsoft New-NetFirewallRule](https://learn.microsoft.com/en-us/powershell/module/netsecurity/new-netfirewallrule)。如果 VM 日后换 IP，需要同步修改 NAT 映射和监听地址。项目不会自动添加这些规则或调整网络。

也可以由你仅把 **BandKey-Win11** 网络切为桥接，让手机直接访问 VM 的家庭局域网地址，省去 NAT 映射；对应防火墙只允许手机 IP。无线网络是否支持这种桥接要现场验证。两种方式任选其一。

## 4. 绑定电脑身份并验证

1. 在 VM 中点击 **启动 VM 手机中继**，应显示电脑 ID、TLS SHA-256、手机访问地址。
2. 点击 **2. 导出电脑配对资料给手机**，得到 `bandkey-vm-pc.json`。将此公开 JSON 传到手机，在实验页点 **2. 导入 VM 电脑配对资料**；核对电脑 ID、TLS 指纹、地址与 VM 窗口完全相同，选择一致并保存。
3. 保持手机实验页前台打开；不需要启动原来的 Wear Engine 后台桥接。页面应显示轮询启动。手机网络错误不会签名，也不会尝试密码。
4. **仅锁定 VM**：在 VM 开始菜单点击账户 → 锁定。不要在宿主机按 Win+L。应先保持锁屏，手机出现一次性确认，点 **确认本次 VM 解锁（测试按钮）**。
5. 观察 VM 是否实际返回桌面，记录是否需要先点击锁屏画面/选择 BandKey 选项。手机显示“签名已交给原生组件”只是交付结果，不能代替实际桌面结果。
6. 再锁 VM，这次不点手机确认，应保持锁屏；使用系统密码回来。再测试手机明确拒绝、超过 30 秒后点击、停止手机轮询、停止 VM 中继，都应无法凭旧请求进入桌面。
7. 收到请求后退出手机实验页，再回来，旧页面按钮不能直接提交旧请求；有效期内再次出现的提示也需要新的明确点击。已经进入网络提交/已经交付的签名无法靠关闭页面撤回，仍受原生有效期与一次性限制。

若请求过期后一直没有新请求，先用系统密码回桌面再锁定一次，创建新的登录周期。不要反复点击过期按钮。

建议反馈这几项：**手机确实收到了提示 / 未点击保持锁屏 / 点击后实际回桌面 / 再锁不重复解锁 / 拒绝与过期保持锁屏**。如果失败，提供客户端状态和 Provider diagnostics 的固定事件尾部，不提供 Vault、密码、请求正文或密钥。

## 5. 停止、撤销和回滚

- 手机点停止，VM 中继点停止，然后关闭应用；不会自动启用开机启动。尚未交付的本轮中继授权失效；已交付签名不能保证撤回。需要彻底撤销时，使用安装器 **Remove VM experiment**，它禁用/移除原生登记并删除已知实验文件，保留系统登录选项。
- 更换手机、清除 App 数据、重新生成实验密钥时，必须先 Remove，再以新公钥登记；不可自动信任网络上传的新公钥。VM 密码变更后也要重新登记。
- VM 中继自己的电脑身份和 TLS 密钥保存在当前 VM 用户 `%LOCALAPPDATA%\BandKeyVmBridge`，用用户 DPAPI 加密，不在光盘/公用请求目录里。停止中继后可删除该专用目录以撤销配对资料；重启后需重新向手机导入新的电脑身份。
- 在两个系统各删除本阶段创建的对应规则：`Remove-NetFirewallRule -Name BandKeyVmPhoneLab`（VM）、`Remove-NetFirewallRule -Name BandKeyVmPhoneForward`（宿主机）。在 NAT 设置中只删除刚添加的 19443 映射。
- 登录组件异常时由你把 **BandKey-Win11** 回退“快照 1”；快照恢复操作仍未实测。不要操作 Ubuntu。

## 当前完成边界

已完成并实跑：C# 核心授权/攻击测试、真实回环 HTTPS 和公开文件交换、原生签名验证/合成凭据序列化、Java/C#/CNG 三方签名互通、Android 构建/Lint、宿主机只读拒绝检查。普通进程测试没有向 Windows 提交账户认证。

已实现待实机：Android Keystore 实际签名与新页生命周期、手机到 VM 的网络、手机模式安装/卸载及实际桌面解锁。本轮 ADB 无已连接设备，未安装新 APK，也未操作 VM。

仍待 Wear Engine：真实通知/按钮回调。BLE 距离、ColorOS 后台、MSA/Hello、正式代理认证后端仍有独立验证门槛，测试手机密钥不会自动变为正式手环身份。协议与替换接口见 [中继协议](vm-phone-relay-protocol.md)。
