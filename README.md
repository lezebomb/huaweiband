# BandKey

Huawei Band 11 Pro → Android Wear Engine桥接 → BLE → Windows 11。

手动锁定电脑后，携带手机、手环离开再回来，手环收到本次通知，**点一次确认才解锁**。无需同一Wi-Fi。手机与电脑常驻；电脑冷启动第一次登录仍用原密码／Windows Hello。离开自动锁屏已按用户要求关闭。

用户已反馈连续两天实际返回通知及确认解锁正常。本轮最终修改为**新主机请求两分钟有效**，通知显示截止时间；过期及重复确认仍拒绝。投递时间计入期限，未确认不会自动解锁。见[收尾说明](docs/final-maintenance.md)。

2026-10-29常驻修复：发现进程仍在但状态停更近9小时，已将蓝牙操作移到后台、状态读取改为缓存，并增加90秒停更恢复的独立监护。实际进程中断后约6.7秒自动重启并恢复手机通信；不等于长期后台或完整解锁验收。见[证据与边界](docs/resident-recovery-20261029.md)。

2026-10-30手机0.9.5：修复旧的两分钟通知遮住新锁屏请求，以及设备查询无回调后不再恢复的问题；增加系统蓝牙连接事件触发的常驻恢复。系统清理仍可能发生，真实手环结果与软件检查分开记录。见[本轮修复与验证](docs/notification-recovery-20261030.md)。

## 使用与恢复

电脑程序位于`%LOCALAPPDATA%\Programs\BandKeyHostPilot`，开始菜单“BandKey”可查看状态。关闭窗口进入托盘；暂停会停止本次通信与授权。手机保留BandKey桥接、运动健康和HMS，允许BandKey自启动与后台运行。

原Windows密码／Hello保留。开始菜单“Uninstall BandKey”用于完整卸载；独立恢复入口：`C:\ProgramData\BandKeyHostPilotRecovery\BandKey.HostSetup.exe`，选择Remove。

本地`maintenance`保留最终更新工具、一次回滚包及私有签名备份，不上传GitHub。电脑配对及账户密文仍在各自受保护目录，清理工程不会删除它们。详见[更新、回滚、清理范围](docs/final-maintenance.md)。

## 继续开发

```powershell
git clone https://github.com/lezebomb/huaweiband
cd huaweiband
pwsh -File scripts/build.ps1 -Publish
pwsh -File scripts/build-android.ps1 -SdkPath <Android-SDK路径> -WearEngineAppId 119393073 -Lint
pwsh -File scripts/build-host-pilot.ps1
```

依赖：PowerShell7、Node.js、JDK21、Android API36／Build Tools36.0.0；原生组件另需C++ v143及Windows SDK26100。.NET按`global.json`恢复。主机原生包绑定目标电脑和Windows build，不能直接安装到其他电脑。

仓库包含全部项目源码、必要自动测试、Gradle wrapper、固定SDK版本／校验、构建安装回滚脚本和阶段记录。缓存、虚拟机、生成产物、账户密文及私钥不进入Git。覆盖现有手机应用需要原签名备份；GitHub源码不能恢复私钥或华为账号服务权限。

| 目录 | 用途 |
|---|---|
| `src/BandKey.Core` | 签名协议、一次性授权、会话和接近门控 |
| `src/BandKey.Windows` | 托盘、BLE、HTTPS中继、WTS状态 |
| `src/BandKey.CredentialProvider` | 真实Windows账户认证、受保护登记、更新与移除 |
| `android` | Android常驻、BLE、官方Wear Engine通知与按钮 |
| `tests` | 核心、Windows集成、原生及Java/C#/CNG互通检查 |
| `scripts` | 构建、安装、恢复SDK、回滚与安全清理 |
| `docs` | 当前维护说明及分阶段真实结果 |

Wear Engine 5.0.3.304的[接口索引与恢复](docs/wearengine-api-reference.md)及`android/wearengine-artifacts.json`保存在仓库；从官方源恢复时验SHA-256，不绕过华为权限。

## 验证边界

真实主机微软账户已完成手环批准后解锁、等待／拒绝保持锁屏、实际携带返回。原密码／Hello保留；虚拟机曾验证移除后重启登录与快照恢复。两天使用正常是用户实际反馈，不等于所有系统版本或无限期后台均获保证。

两分钟修改：核心185、Windows104、主机原生248项通过；Java141项检查通过，Android Lint零错误、6项原有警告；Java/C#/CNG真实密码学互通通过。软件推进时钟的期限检查不能冒充“两分钟后实际手环解锁”的实机结果。

手环保持与手机原配对。直接手环广播测距未实现，不提供米数。返回判断基于有效通信恢复；始终未离开通信范围时，不会凭空产生新返回事件。每次解锁必须有当前有效的明确确认。
