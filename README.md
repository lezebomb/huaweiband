# BandKey · 华为手环 11 Pro 智能电脑钥匙

2026-10-27 主机试用：已安装独立、本机与 Windows build 绑定的 Provider，保留原密码／Hello；真实手环确认后已返回当前物理主机桌面，原生日志包含 `Windows-result=0`；新一轮等待及拒绝后仍锁屏。电脑当前用户常驻任务与手机0.9.0前台常驻已配置，主机流结束修复已观察到保留同一 GATT；仍有偶发 CCCD 丢失并自动恢复。主机走远／返回、冷启动、长期常驻与主机卸载尚待验收。见[主机常驻试用与恢复](docs/host-resident-pilot.md)。

VM实测：固定手机模式已完成“手环走远自动锁VM → 返回后新通知确认解锁”，返回通知约10秒；另完成VM卸载、重启后原系统登录、快照恢复及恢复后手环再次解锁。两轮BLE各为单次连接、故障0，长期稳定性仍待验证。见[完整返回与恢复记录](docs/vm-return-and-recovery-20261027.md)。实验VM保护不变，主机采用独立试用配置。

携带手机模式：[信号绑定与确认门控后端](docs/carried-phone-backend-20261026.md)已实现，尚缺真实认证会话到 BLE 连接代次的接入及硬件验证，暂未开放实机启用入口。

Windows BLE 助手补丁已将“整个运行期间共4次尝试”改为连续失败上限，经过足够成功传输后才恢复重试额度；错误帧／权限失败和原运行期限不放宽。Windows97项检查通过；本轮真实手机服务重启后已自动恢复并再次确认解锁，但额外TCP中止仍存在，硬件预算重置未触发。见[实测记录](docs/hardware-validation-20261027.md)、[使用与回退](docs/ble-recovery-budget-20261027.md)。

Wear Engine 接口入口：[调用索引、源码和 SDK 恢复](docs/wearengine-api-reference.md)。仓库保存集成接口、固定版本与校验信息；原始 SDK 保留在本地归档，恢复脚本从官方源获取并逐一验哈希。

实机回退基线为 **0.8.0**，曾单轮约 **3秒通知、确认后1秒回桌面**；不是长期性能保证。**0.9中继候选**已在真实手环完成批准／拒绝、断连恢复后批准及完整离开／返回；原地通知和解锁各约3秒，返回通知约10秒，长期稳定性尚待验收。核心176项、Windows97项、Android Java110项通过。手机保留原0.8.0，0.8.2仅为未安装候选。见[实测记录](docs/vm-return-and-recovery-20261027.md)、[修复与回滚](docs/fixed-phone-native-recovery-0.9.md)、[原0.8实测](docs/fixed-phone-fast-0.8.0.md)。

**真实 Band 11 Pro 已通过一加，经 USB／BLE 使隔离 VM 本地账户返回桌面；0.8 BLE下新增微软账户也已获用户确认，无需输入密码，等待／拒绝时保持锁屏。** 0.4已验证手机返回主屏幕并熄屏后的手环确认；固定手机模式0.5.2已获用户反馈走远自动锁屏、返回通知及确认。0.8尚未重测完整走远／返回，不继承为新版验收。工具记录签名交付，桌面结果来自用户观察。BLE无需同一 Wi-Fi，间歇性10053及流超时仍存在，不能宣称长期稳定。详见[阶段记录](docs/validation-20261025.md)、[微软账户结果](docs/host-account-rollout-20261025.md)。

两种接近模式：手机固定在电脑旁时，签名上报手环与手机的连接状态；手机随身时，可用手机与电脑的BLE信号，当前仅完成后端接口。电脑扫描尚未找到可归属的手环广播，Wear Engine公开接口没有提供额外广播或RSSI控制。两种模式都要求真实手环明确确认，不自动批准。见[场景与边界](docs/proximity-modes.md)。

主机MicrosoftAccount密码已由用户本地输入，经Windows核验、SID一致后加密登记；实际LogonUI确认解锁和拒绝已通过。本阶段主机原生240项、VM原生237项、Windows100项及静态分析通过；长期BLE、冷启动和主机实际卸载仍待验收。此前后端记录见[部署后端](docs/deployment-backend-20261026.md)、[微软账户VM阶段](docs/host-account-rollout-20261025.md)。

已增加Wear Engine 5.0.3.304及依赖的本地离线归档、源码快照、原签名APK和完整性校验；可选本地Maven镜像按固定SHA-256核对。本机现有工具链离线构建已通过；归档不冻结华为服务授权，也不包含整套开发工具链或签名私钥。见[归档与恢复说明](docs/wearengine-offline-archive-20261026.md)。

此前修复记录：[0.5.1刷新循环](docs/fixed-phone-recovery-0.5.1.md)、[0.5.2通知轮次](docs/wear-notification-rounds-0.5.2.md)、[0.6连接复用](docs/fixed-phone-latency-0.6.0.md)、[0.7附带挑战](docs/fixed-phone-inline-0.7.0.md)。0.8仅替换中继5个文件并保留数据更新手机，未更改原生登录组件或手环配对。

## 直接运行

- Windows：解压 [BandKey-win-x64.zip](artifacts/BandKey-win-x64.zip)，运行其中的 `BandKey.exe`。当前目录已有 [BandKey.exe](artifacts/BandKey-win-x64/BandKey.exe)，内含 .NET 运行时，无需另装运行时。
- 手机 BLE 修复版：[启动入口](artifacts/启动BLE修复版.cmd)、[独立 ZIP](artifacts/BandKey-BLE-recovery.zip)。先停止并关闭旧 BLE 助手，再运行新入口；它只打开电脑传输助手，不安装登录组件。ZIP 需完整解压后运行 `Start-BLE.cmd`；不与运行中的旧程序混合覆盖。
- BLE助手使用当前自包含发布目录，支持实际 Indication／Notify 状态及会话记录。VM小补丁路径记录在 `artifacts/latest-relay-patch.json`，仅含用户态中继5个文件，须复制到已有完整0.7目录；不含登录安装器。
- Android：一加当前保留原0.8.0；本机 [0.8.0回退APK](artifacts/BandKey-bridge-0.8.0-debug.apk) 与 [0.8.2候选APK](artifacts/BandKey-bridge-0.8.2-debug.apk) 分开保存，通用构建输出已是候选。候选尚未安装，不重复更新手机。手环保持与一加的现有配对，Mate60旧结果不替代当前一加验收。
- VM 手机实验：[一步步教程](docs/vm-phone-bridge-test.md)、[中继协议](docs/vm-phone-relay-protocol.md)。`BandKey-VM-Phone-Lab-*.iso` 与旧模拟镜像独立保存。用户已在 VMware/build 26300 来宾登记手机模式并完成联调；原有系统登录选项和快照保留，宿主机未注册 Provider。
- VM 会话／自动锁屏工具：[使用说明](docs/vm-provider-test.md)。独立 `BandKey-VM-Session-Lab-*.iso` 默认只记录 WTS／查询，模拟走远按钮可在指定来宾调用锁屏；不安装登录组件。当前已构建及离线验证，尚未在 VM 实跑。可用 `./scripts/package-session-lab.ps1` 构建，最新本机路径在 `artifacts/latest-session-lab-package.json`。
- 配置、应用身份密钥、授权公钥及日志保存在程序旁的 `runtime/`。可用 `--data <目录>` 改变位置。私钥以当前 Windows 用户的 DPAPI 加密保存；正式托盘代理没有 Windows 密码/PIN输入框，也没有保存它们的代码。独立 VM 新实验由用户登记一次 VM 密码，以机器 DPAPI 和 SYSTEM/管理员专属 ACL 保管；它与正式分发包分开。
- 关闭窗口会缩到托盘；使用托盘菜单“退出”彻底关闭。首次启动默认关闭自动锁屏和网络监听。

## 验证状态

| 项目 | 状态 | 证据与限制 |
|---|---|---|
| Windows Release 编译、自包含发布 | 已实际验证 | .NET 10.0.402；无编译错误/警告 |
| Windows BLE / Classic 发现 | 已实际验证 | BLE 25 秒和 Classic inquiry 约 8 秒均完成，未观察到已连接手机的目标手环；不能据此证明永远不可发现 |
| RSSI 滤波、滞回、延迟、重连后的重新就绪 | 模拟测试通过；固定手机有硬件结果 | 核心176项；0.9固定手机完整走远自动锁屏／返回确认解锁获用户确认，手机随身RSSI模式未接入实机 |
| 一次性授权、撤权、防重放、认证调度 | 模拟测试通过 | 真实密码学、回环 HTTPS 8/8；锁屏/靠近状态及认证后端为模拟输入，结果明确标记 `simulated` |
| Windows DPAPI、TLS、原生中继与文件交换 | 已实际验证，部分GATT／会话输入为模拟 | Windows97项，含实际TCP、密码学、文件API、新锁屏直达、原生替换恢复和连续故障预算；不能替代物理BLE长期稳定性验收 |
| 手机到 Windows 的独立 BLE 传输 | 合成账户 11 项通过；实际 VM 解锁获用户确认 | 合成轮次 MTU 517、两端计数一致；实际 VM 轮次无 USB 数据映射，确认解锁／拒绝保持锁屏，随后出现流错误；长期稳定性待修复验证 |
| `LockWorkStation`、WTS 锁屏事件、托盘/参数界面 | VM固定手机自动锁屏获用户反馈 | 0.5.2用户走远后确认VM自动锁定；本轮0.8仅测手动锁屏与确认，未独立采集WTS解锁结果，宿主机未启用 |
| Android 官方 SDK、环境诊断 | 设备与通知已实际验证 | 一加 Android 16、运动健康 17.0.8.300、HMS Core 6.16.4.352，服务 API 14；已获设备信息及通知调用，不再返回旧服务范围错误 8 |
| Android 前台桥接与按钮处理 | 生命周期测试及真实手环按钮通过 | 22 项生命周期/重试检查通过，含 VM 等待与恢复；真实手环独立按钮测试未签名或调用电脑认证；ColorOS 长期后台尚待验证 |
| 手环按钮点击 → 手机 → 电脑 | 真实手环经 USB／BLE 解锁隔离 VM 获用户确认 | 当前0.8单轮约3秒通知／1秒回桌面；确认／拒绝、后台熄屏与固定手机走远的结果分版本记录，不等于精确测距或长期验收 |
| 电脑同时作为第二个华为协议主机 | 技术上仍存在障碍 | 未拿到已有配对会话的合法认证通道；未重配或重置手环；并发连接能力未知 |
| Windows 真实身份认证/解锁 | VM 本地／微软账户及物理主机微软账户已实测 | 主机真实手环确认后自动回桌面，原生Windows-result=0；等待／拒绝保持锁屏。原系统登录保留，长期稳定与主机移除未验收 |
| 微软 V2 Credential Provider 参考接口 | 宿主机接口验证通过，未验证登录 | 控制台和图形入口各 42/42，两个失败场景通过；已生成只读来宾测试 ISO，未注册、未提交系统认证，测试身份为虚构用户 |
| 独立 BandKey VM Provider | 原生236项及静态分析通过；VM本地／微软账户实测获用户确认 | 原生DLL不变；已取得VM卸载后原登录、快照恢复及恢复后再次解锁的用户确认；正式主机部署尚未验收 |
| Android/Java/C#/CNG 原生授权互通 | 软件及实机 USB / BLE 验签通过 | .NET 验原生 15/15、原生验 .NET 2/2、Java 编码 26/26、.NET 与 CNG 各验 Java 2/2；一加 USB、Mate60 USB / BLE Keystore 真实签名及 CNG 篡改拒绝，非账户认证 |
| 手机手动确认 → VM 原生认证 | Mate60 USB / BLE 实机联调获用户确认 | 工具代点前台测试按钮，真实 Keystore 签名；实际返回桌面由用户确认；非手环回调，不验证距离、原生过期或长期后台 |
| Windows 隔离测试环境 | 来宾参考接口 42/42；新快照恢复获用户确认 | build 26300 / 26H2；04-BandKey-0.9-working已创建和还原，恢复后实际确认解锁；本轮未操作Ubuntu |
| 登录后启动、崩溃恢复 | 主机任务已安装并启动；手机常驻已启用 | 当前用户交互会话、非管理员任务已核验；手机Home后前台服务仍运行。冷启动和长期OEM后台仍待实测；电脑首次登录前不运行用户代理 |

测试原始结果：[核心](artifacts/core-tests.json)、[Windows 集成](artifacts/windows-tests.json)、[Android 生命周期](artifacts/android-runtime-tests.json)。最新硬件与接入结果见 [一加实机记录](docs/device-validation-20261017.md)，申请身份和用途见 [Wear Engine 申请资料](docs/wearengine-application.md)。

## 技术选择

Windows 使用 C#/.NET 10、WinForms 托盘、Windows BLE API、Kestrel HTTPS；Android 使用 Java、系统 Activity、Android Keystore 和官方 Wear Engine 5.0.3.304。正式目标通过手环按钮确认；新增手机按钮仅用于审批等待期的独立 VM 联调。

距离优先由电脑被动观察手环广播。MAC 地址只选择观测对象，不授予解锁权限；地址轮换、连接后停止广播都会影响距离判断。不会把手机在同一 Wi-Fi 当作手环靠近。

按钮通道采用手机桥接。Band 11 Pro当前固件已实际验证模板通知及确认／拒绝回调；无需手环App。`notify()` 发送成功不会签署授权，只有选定设备的本次有效按钮回调才会进入签名流程。SDK接受通知的耗时不等于手环物理显示时间。

Gadgetbridge 已有 [Band 11 Pro 条目](https://gadgetbridge.org/gadgets/wearables/huawei-honor/#huawei-band-11-pro)，但标为实验性。其 Huawei 协议实现需要认证会话；本工程没有照搬认证/配对代码，也没有改写运动健康的配对状态。详细研究见 [技术依据与路线比较](docs/research.md)。

## 通用托盘代理：距离监测（目标手环广播仍不可用）

1. 在 Windows 上打开 BandKey，保持自动锁屏关闭。
2. 在运动健康中核对手环蓝牙地址和型号，选择对应的广播。无法匹配时停止在这一步，不能只选择信号最强的设备。
3. 佩戴手环在工位静坐、走远、回来，观察原始 RSSI 和滤波状态。近状态稳定后才会进入就绪状态。
4. 默认近阈值 -65 dBm、远阈值 -80 dBm、持续远离 15 秒。RSSI 不是米数，请按实际环境校准。
5. 默认广播消失变为 `Unknown`，不因此锁屏。确认手环在佩戴、手机连接、熄屏等情况下持续广播后，才考虑启用“信号消失后也锁屏”，默认超时 45 秒。
6. 保存并开启自动锁屏后，持续远离才调用 `LockWorkStation`。手动 PIN/密码解锁后，必须再次确认稳定靠近，才能触发下一次离开锁屏。

适配器关闭、扫描异常或恢复睡眠后会取消就绪状态。BLE 自动重试采用最多 60 秒退避。系统锁屏状态通过 WTS 查询与通知确认；锁屏 API 返回成功仅表示请求已提交。

## 通用托盘代理：手机桥接（不包含VM登录后端）

需要一次性配置，日常操作才可以只点手环：

1. 账号服务与 Wear Engine 两项基础权限已开通，并在一加实际调用。App ID、包名、签名指纹仍须与申请资料一致。正式 App ID 已写入 `android/gradle.properties`。不要将 Windows 密码/PIN发给开发工具。
2. 让一加上的运动健康正常连接手环，安装 APK，授予蓝牙和通知权限；在 App 内申请 `DEVICE_MANAGER`、`NOTIFY` 权限并选定手环 UUID。设备选择与发送通知使用 `getCommonDevice()` 获取能力完整的设备对象。可先打开“手环按钮测试（不会解锁电脑）”，检查通知和确认／拒绝回调。
3. Windows 配置“本机监听 IP”为电脑的局域网 IP、启用 HTTPS 桥接、保存后退出重开程序。默认 `127.0.0.1` 只用于本机，不可被手机访问。防火墙如阻止访问，仅为专用网络/本地子网放行该程序或 TCP 18443；程序不自动修改防火墙。
4. Windows 导出电脑配对资料，在手机导入并核对完整公钥和 TLS 指纹。手机导出公钥，在 Windows 导入并核对手机指纹。配对文件没有私钥。
5. ColorOS 中为 BandKey 与运动健康开启允许后台活动、自启动，按需关闭电池优化。启动自动桥接后有持续前台服务通知。
6. 电脑已经锁屏、距离状态为 Near、且手机已授权时才生成挑战。手环第一按钮确认，第二按钮拒绝。挑战 30 秒过期，重复消息无效。
7. 本版本收到有效按钮授权只报告“手环授权已验证；Windows 登录后端尚未可用”。仍需要原有 PIN/密码进入桌面。

Windows 中“撤销手机”立即使该公钥及未完成挑战失效。Android 重装或数据清除会丢失 Keystore 密钥，需要重新授权。

Android 停止桥接、重新导入电脑或改选手环会取消当前任务，改选后需重新启动桥接。迟到的设备查询不能再触发通知；已发送到手环的通知可能仍然显示，但旧按钮回调会被拒绝。有效期同时检查系统时间和单调时钟，调整手机时间不会延长已显示请求的本地有效期。

## 构建

在项目根目录使用 PowerShell 7、Node.js（SDK 下载）、JDK 21（跨语言测试）。Windows 项目无需 Visual Studio。

```powershell
node scripts/bootstrap.mjs
./scripts/build.ps1 -Publish
```

SDK 保存在 `.tooling/dotnet/`，下载校验官方 SHA-512；NuGet 包也在项目内。`-Publish` 生成自包含 Windows x64 程序和 ZIP，测试失败会停止发布。

Android 需要已接受相应许可证的 Android SDK API 36、build-tools 36.0.0、JDK 21。脚本不代替你接受许可证。

```powershell
./scripts/build-android.ps1 -SdkPath 'D:\Collect\.tooling\android-sdk' -Lint
# 如需覆盖项目内已经配置的 App ID；此参数不改变开发签名密钥：
./scripts/build-android.ps1 -SdkPath 'D:\Collect\.tooling\android-sdk' -WearEngineAppId '你的AppID'
```

Gradle Wrapper 9.3.1 校验发行包 SHA-256；AGP 9.1.0、依赖版本固定。APK 及签名证书指纹输出到 `artifacts/`。真实发布仍需独立管理发布签名证书；不要把调试签名当作正式发布签名。

Android 构建默认先使用 JDK 执行生产代码的生命周期/重试测试；也可单独运行 `./scripts/test-android-runtime.ps1`。这些测试不模拟华为 SDK 成功，也不构成手环按钮或后台实机测试。

## 诊断命令

```powershell
# 只读被动扫描，不进行配对或 GATT 通信
Start-Process -Wait ./artifacts/BandKey-win-x64/BandKey.exe -ArgumentList '--probe --seconds 30 --output D:\huawei\artifacts\probe.json'
# 保存每条广播的时间戳、RSSI、地址/广播类型及有界数据段，便于分析连续性
Start-Process -Wait ./artifacts/BandKey-win-x64/BandKey.exe -ArgumentList '--probe --seconds 60 --output D:\huawei\artifacts\probe.json --jsonl D:\huawei\artifacts\observations.jsonl'
# Classic inquiry：只做发现，不配对、不打开 RFCOMM；可用 --cache-only 只读缓存
Start-Process -Wait ./artifacts/BandKey-win-x64/BandKey.exe -ArgumentList '--probe-classic --seconds 8 --output D:\huawei\artifacts\classic-probe.json'
# 独立模拟输入，真实回环 HTTPS；不读取正式代理配置，不调用锁屏或 Windows 登录
Start-Process -Wait ./artifacts/BandKey-win-x64/BandKey.exe -ArgumentList '--test-flow --output D:\huawei\artifacts\flow-lab.json'
```

Android 提供环境/设备能力诊断、指定 MAC 的 30 秒广播诊断和诊断 JSON 导出。诊断不会连接、配对或重置手环，缺少服务授权会显示真实 SDK 错误码。

当前阶段记录见 [审核等待期开发记录](docs/development-20261018.md)。构建产物、原始硬件记录和本地服务申请资料均不上传 Git；本文的 `artifacts/` 链接指向本地构建结果。

另有 `--inspect-gatt --address 12位十六进制地址 --output 路径`，用于显式发起一次服务/特征元数据查询。它不进行特征值写入、通知订阅或配对，但会尝试连接设备。**没有在本次研发中对手环执行该命令**；可能影响单连接设备的现有连接，先确认设备和操作时机。查询到 FE86/FE01/FE02 不代表 Huawei 协议认证成功。

## 安装、启动恢复与卸载

便携版无需管理员安装。需要登录后自动启动时，由用户手动运行：

```powershell
./scripts/startup.ps1
# 取消自动启动
./scripts/startup.ps1 -Remove
```

脚本仅创建当前用户的登录后任务，不保存账户密码；监督进程在异常退出后退避重启，正常退出则停止。本版本不会在冷启动、任何用户首次登录前提供手环认证。

卸载时先取消自动启动，在托盘退出，删除便携程序目录即可。删除 `runtime/` 会删除此电脑的应用身份和授权公钥；未来重新启用需重新配对。Android 在 App 中停止桥接，再使用系统卸载。没有安装 Credential Provider、认证包或登录注册表项目，无需回滚这些组件。

## 工程结构

```text
src/BandKey.Core/            距离状态机、签名协议、认证接口
src/BandKey.Windows/         托盘 UI、BLE、WTS、DPAPI、HTTPS、GATT 诊断
src/BandKey.SessionLab/      独立 VM 会话记录与模拟 RSSI 锁屏验收工具
android/                    正式 Android 桥接工程与 Gradle Wrapper
tests/BandKey.Tests/         信号与协议攻击测试
tests/BandKey.Windows.Tests/ DPAPI、实际 HTTPS、跨语言密码学测试
tests/CredentialProvider.Lab/ 不注册的微软 V2 参考接口测试宿主
src/BandKey.CredentialProvider/ VM 专用 Provider、加密保管安装器及模拟签名客户端
tests/BandKey.CredentialProvider.Tests/ 普通进程中的独立 Provider 测试
tests/BandKey.Grant.Interop/ 原生 CNG 与 .NET Windows 授权互通测试
tests/interop/              编译 Android Wire.java 的 JDK 验证程序
scripts/                    可重复构建、登录后启动、异常恢复
docs/                       技术证据、协议、验证记录、真机步骤、认证路线
artifacts/                  EXE/APK/测试报告（不纳入版本控制）
.research/                  上游只读研究副本（不纳入项目源码）
.tooling/                   项目本地 SDK/依赖缓存
```

真实手环至VM本地／微软账户联调已有用户确认；0.9固定手机完整走远／返回和VM卸载、原登录、快照恢复已完成，见[最新阶段记录](docs/vm-return-and-recovery-20261027.md)。长期后台、BLE稳定性及正式主机部署／恢复仍待验收。正式登录受[认证实施门槛](docs/windows-authentication.md)约束，手机密钥不能作为手环自身持有的凭据。
