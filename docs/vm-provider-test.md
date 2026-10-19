# BandKey 虚拟机登录组件测试

这是上一阶段**手动密码**实验的存档教程。用户已反馈安装、正确密码进入桌面、错误密码被拒绝及原有登录保留；其后续截图纠正了卸载正常的笼统反馈：仍有文件残留，重启无效。已修复只读属性清理缺陷，请改用 [新版清理及签名教程](vm-signed-confirmation-test.md)，不要继续按本页旧提示重复重启。

这是仅供 VMware Windows 11 x64 build 26300 的实验包。
本阶段验证手动输入的 Windows 凭据能否通过独立 Provider 交给 Windows。
尚未接入手环按钮、签名授权、密码托管或自动解锁，正式 BandKey 代理仍没有可用的解锁后端。

## 测试前

1. 只在 BandKey-Win11 中操作。保留现有“快照 1”，不操作 Ubuntu。
2. 确认知道虚拟机本地账户的密码，并确认原有密码登录能用。PIN 不等于账户密码。
3. 如果虚拟机账户尚未设置密码，先在虚拟机“设置 → 账户 → 登录选项 → 密码”设置独立测试密码。不要使用主力电脑密码，也不要把密码发给开发者。
4. 本包只支持本地账户。MicrosoftAccount、组织域账户、远程桌面及其他 OS build 会被拒绝；不要为通过检查而修改机器信息或代码。

## 安装与第一轮测试

1. 在 VMware 的 BandKey-Win11 设置中，将 CD/DVD 的 ISO 换成本阶段提供的 `BandKey-VM-Auth-Lab-*.iso`，勾选“已连接”。
2. 在虚拟机“此电脑”打开 `BANDKEY_LAB` 光盘，双击 `BandKey.VmSetup.exe`。
3. 点击 **Install VM password-test tile**，按系统提示由你自己确认管理员权限。
4. 应显示 **VM lab tile installed**。安装程序不会自动锁屏、注销或重启。
5. 在虚拟机开始菜单中选择当前用户 → 锁定。点击锁屏进入登录页，选择“登录选项”中的 **BandKey VM credential test**。尚未做来宾 UI 验证，若看不到该入口，停止并拍下登录选项，不要修改其他 Provider。
6. 手动输入这台虚拟机账户的正确密码并提交，观察是否真的进入桌面。序列化成功、安装成功均不能代替这一步。
7. 再锁定一次，只试一次错误密码，预期 Windows 拒绝；随后用原有系统密码登录选项进入桌面。不要连续尝试错误密码。

请反馈：安装结果、能否看到新选项、正确密码能否进入桌面、错误密码是否被拒绝、原有登录是否仍可用。不要提供密码或 PIN。

## 卸载与回滚

- 能进入桌面：再次从光盘运行 `BandKey.VmSetup.exe`，点击 **Remove VM password-test tile**。它只移除 BandKey 实验 CLSID、COM 注册、实验配置和专用 DLL。
- 若提示 DLL 正在使用：注册已移除；重启虚拟机后再次运行 Remove 清理文件。不要手动删除系统目录或其他登录项。
- 登录页异常：先按 Ctrl+Alt 释放鼠标，在 VMware 中对 **BandKey-Win11** 选择“虚拟机 → 快照 → 快照管理器 → 快照 1 → 转到”。这会丢弃快照之后的虚拟机状态，因此实验机中不要存放需要保留的新文件。不要选择 Ubuntu。

本版本不写 Credential Provider Filter，不替换系统 Provider，不保存或记录密码；只短暂在当前凭据对象的内存中保存手动输入，提交序列化、失败、取消选择或结束回调时清零。使用 Windows CredProtect 保护序列化中的密码。安装文件位于系统 Program Files 的 `BandKeyVmLab`，普通用户只有读取/执行权限。

当前证据：宿主机合成用户接口测试与只读安装限制检查通过；安装及真实密码认证已获用户反馈通过，完整卸载结论已因后续残留截图撤回。快照存在，恢复尚未验证。本页仅记录旧版未持久保存密码的实验；新版清理及加密保管见新教程。实验二进制不进入普通分发 ZIP。

## 独立会话与自动锁屏工具（2026-10-19）

此节用于新的 `BandKey-VM-Session-Lab-*.iso`，与上面的 Provider 安装实验分开。**不用卸载、安装或重新登记 Provider，也不用重新安装手机 APK。** 这是独立自包含工具，不改登录配置、不接收密码、不提交账户认证、不自动解锁。默认只记录；只有点击模拟走远才可能真正锁定当前 VM。

1. 仅在 BandKey-Win11 使用，保留“快照 2”和原有系统密码登录。以后有空时再操作；开发工具不会替你接管 VM。
2. 将 CD/DVD 挂到新 Session Lab ISO，把全部光盘内容复制到 VM 的 `C:\BandKeySessionLab`，运行根目录 `Start-VM-Session-Lab.cmd`。不要只复制 EXE，也不要运行旧 Phone Lab 的安装器。主力机、RDP 来宾、Session 0 或不匹配的 VMware/build 26300 环境会被拒绝。
3. 窗口默认只记录本会话。显示 WTS 通知是否注册、模拟场景、锁屏请求次数和观察到的解锁。通知失败时保留数值错误，查询结果单独记录，不能冒充通知。
4. 先点“模拟抖动（不应锁屏）”，等待约 28 秒。预期不锁屏、请求次数仍为 0。样本为模拟 RSSI，不是手环实测；该工具不扫描或连接手环。
5. 再点“模拟走远（会锁定此 VM）”。先模拟稳定近约 8 秒，再模拟持续远离，通常在本轮约 16–22 秒请求一次锁屏。预期看到实际锁屏；调用返回 submitted 只能证明提交，必须结合 WTS／查询事件确认。出现未知会话、停止模拟或恢复睡眠会取消场景，不自动重启。
6. 用原系统密码方式返回桌面。预期通知或连续查询各自记录 Locked → Unlocked，旧模拟不再次锁屏。工具始终不把观察到解锁归因为 BandKey，也不提供自动解锁。
7. 以后采集手机解锁时，保持工具仅记录、不启动模拟；保持原 VM 手机中继和 USB／BLE 通道，再按手机教程测试。工具的系统事件、手机交付以及用户未输入密码的观察需要合在一起判断，不能仅看任意单个成功字段。
8. 关闭窗口后，从 `%LOCALAPPDATA%\BandKeySessionLab` 取本轮 `session-*.json`。报告包含 UTC 起点、单调时间、来源、匿名锁屏状态和固定错误码，不含账号、SID、凭据、公钥、nonce 或健康记录。`recordingStopped=true` 表示采集已停止，`runtimeError` 为空才是正常结束；`truncated=true` 表示部分记录，不能当作完整观察。查询 Unknown 会中断该来源的状态链，查询／通知滞后也不会合并为虚假的解锁。

本阶段已完成代码、模拟／安全门槛测试、宿主机拒绝检查、离线布局和自包含打包。**新工具尚未在来宾实际运行，实际锁屏、WTS 注册／事件、休眠及手机解锁采集均待用户测试。** 旧手机 USB／BLE 桌面结果不回填为本工具实测。

测试预设为近阈值 -65／远阈值 -80 dBm、近持续 2 秒／远持续 5 秒、5 点中值及 0.6 平滑系数；信号消失不锁。它不读取或改写普通代理配置，也不将这些模拟样本作为设备身份或真实距离。

停止与回滚：点击“停止模拟”或关闭窗口。程序注销自己的会话通知订阅，不设置自启或后台服务；不触碰现有 Provider。删除复制的工具文件夹即可移除程序，报告文件可单独保留。若 VM 登录异常，优先用原登录选项；必要时由用户恢复已保存快照。不要操作 Ubuntu。

API 依据：[WM_WTSSESSION_CHANGE](https://learn.microsoft.com/en-us/windows/win32/termserv/wm-wtssession-change)、[WTSRegisterSessionNotification](https://learn.microsoft.com/en-us/windows/win32/api/wtsapi32/nf-wtsapi32-wtsregistersessionnotification)、[LockWorkStation](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-lockworkstation)。
