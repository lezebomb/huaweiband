# BandKey · 华为手环 11 Pro 智能电脑钥匙

这是可继续研发、可构建的 Windows / Android 工程。**当前尚未实现真正的 Windows 解锁。** 已交付可运行的 BLE 距离监控程序、正规锁屏调用、安全授权协议，以及使用华为官方 Wear Engine SDK 的 Android 桥接程序。

## 直接运行

- Windows：解压 [BandKey-win-x64.zip](artifacts/BandKey-win-x64.zip)，运行其中的 `BandKey.exe`。当前目录已有 [BandKey.exe](artifacts/BandKey-win-x64/BandKey.exe)，内含 .NET 运行时，无需另装运行时。
- Android：[BandKey-bridge-debug.apk](artifacts/BandKey-bridge-debug.apk)。已安装并运行在一加实机，正式 App ID、HMS Core 和账号服务已就绪。用户于 2026-10-17 确认 Wear Engine 申请正在审核；最近实机查询仍为错误 8，尚不能向手环发送按钮通知。
- 配置、应用身份密钥、授权公钥及日志保存在程序旁的 `runtime/`。可用 `--data <目录>` 改变位置。私钥以当前 Windows 用户的 DPAPI 加密保存；正式托盘代理没有 Windows 密码/PIN输入框，也没有保存它们的代码。独立 VM 新实验由用户登记一次 VM 密码，以机器 DPAPI 和 SYSTEM/管理员专属 ACL 保管；它与正式分发包分开。
- 关闭窗口会缩到托盘；使用托盘菜单“退出”彻底关闭。首次启动默认关闭自动锁屏和网络监听。

## 验证状态

| 项目 | 状态 | 证据与限制 |
|---|---|---|
| Windows Release 编译、自包含发布 | 已实际验证 | .NET 10.0.402；无编译错误/警告 |
| Windows BLE / Classic 发现 | 已实际验证 | BLE 25 秒和 Classic inquiry 约 8 秒均完成，未观察到已连接手机的目标手环；不能据此证明永远不可发现 |
| RSSI 滤波、滞回、延迟、重连后的重新就绪 | 模拟测试通过 | 82 项核心测试包含距离轨迹、控制器、协议攻击及认证调度；没有真机走远锁屏测试 |
| 一次性授权、撤权、防重放、认证调度 | 模拟测试通过 | 真实密码学、回环 HTTPS 8/8；锁屏/靠近状态及认证后端为模拟输入，结果明确标记 `simulated` |
| Windows DPAPI、TLS、Java/.NET 签名互通 | 已实际验证 | 11 项 Windows 集成测试；Java 直接编译 Android 的 `Wire.java` 和固定证书验证器，含错误/过期证书反例 |
| `LockWorkStation`、WTS 锁屏事件、托盘/参数界面 | 仅完成代码实现 | 窗口已离线渲染检查；没有在真实会话中触发锁屏测试 |
| Android 官方 SDK、环境诊断 | 部分已实际验证 | 一加 Android 16、运动健康 17.0.8.300；正式 App ID + HMS Core 6.16.4.352 后查询返回服务范围错误 8 |
| Android 前台桥接与按钮处理 | 代码实现及模拟测试通过 | 19 项生命周期/重试测试通过；停止、换设备、过期后取消旧请求，重复点击只消费一次。服务权限、Notify 能力、ColorOS 长期后台尚待验证 |
| 手环按钮点击 → 手机 → 电脑 | 尚需真机验证 | 尚无手环 Notify 能力及真实点击证据；手机指定地址 30 秒扫描为 0 条广播 |
| 电脑同时作为第二个华为协议主机 | 技术上仍存在障碍 | 未拿到已有配对会话的合法认证通道；未重配或重置手环；并发连接能力未知 |
| Windows 真实身份认证/解锁 | VM 本地密码链路获用户反馈通过；正式账户仍有障碍 | VM 正确密码进入桌面、错误密码拒绝及原有登录由用户反馈；当前宿主机 **MicrosoftAccount**、Hello 及手环自动认证均未验证 |
| 微软 V2 Credential Provider 参考接口 | 宿主机接口验证通过，未验证登录 | 控制台和图形入口各 42/42，两个失败场景通过；已生成只读来宾测试 ISO，未注册、未提交系统认证，测试身份为虚构用户 |
| 独立 BandKey VM Provider | 原生 154/154、C# 互通 15/15、原生反向 2/2 | 清理修复获用户反馈通过；旧签名版已交付响应但未自动解锁，登录页显示无当前授权。新增刷新生命周期修复及分步诊断，[新一轮验证](docs/vm-signed-confirmation-test.md)尚待用户执行；正式后端未启用 |
| Windows 隔离测试环境 | 来宾参考接口 42/42，基线快照已核实 | 用户截图确认 build 26300 / 26H2；“快照 1”存在，恢复未测；Ubuntu 配置哈希未变 |
| 登录后启动、崩溃恢复 | 部分已验证 | 监督脚本异常子进程重启、正常退出停止已测；任务计划/真机重启未测，首次登录前不运行用户代理 |

测试原始结果：[核心](artifacts/core-tests.json)、[Windows 集成](artifacts/windows-tests.json)、[Android 生命周期](artifacts/android-runtime-tests.json)。最新硬件与接入结果见 [一加实机记录](docs/device-validation-20261017.md)，申请身份和用途见 [Wear Engine 申请资料](docs/wearengine-application.md)。

## 技术选择

Windows 使用 C#/.NET 10、WinForms 托盘、Windows BLE API、Kestrel HTTPS；Android 使用 Java、系统 Activity、Android Keystore 和官方 Wear Engine 5.0.3.304。没有服务器订阅或手机日常确认步骤。

距离优先由电脑被动观察手环广播。MAC 地址只选择观测对象，不授予解锁权限；地址轮换、连接后停止广播都会影响距离判断。不会把手机在同一 Wi-Fi 当作手环靠近。

按钮通道优先采用手机透明桥接。华为[模板通知文档](https://developer.huawei.com/consumer/fr/doc/connectivity-Guides/wearable-device-notification-0000001057412109)说明可在没有手环 App 时显示按钮并返回点击反馈；Band 11 Pro 的固件支持情况仍未知。`notify()` 发送成功不会签署授权，只有选定设备的按钮回调才会进入签名流程。

Gadgetbridge 已有 [Band 11 Pro 条目](https://gadgetbridge.org/gadgets/wearables/huawei-honor/#huawei-band-11-pro)，但标为实验性。其 Huawei 协议实现需要认证会话；本工程没有照搬认证/配对代码，也没有改写运动健康的配对状态。详细研究见 [技术依据与路线比较](docs/research.md)。

## 第一次使用：距离监测

1. 在 Windows 上打开 BandKey，保持自动锁屏关闭。
2. 在运动健康中核对手环蓝牙地址和型号，选择对应的广播。无法匹配时停止在这一步，不能只选择信号最强的设备。
3. 佩戴手环在工位静坐、走远、回来，观察原始 RSSI 和滤波状态。近状态稳定后才会进入就绪状态。
4. 默认近阈值 -65 dBm、远阈值 -80 dBm、持续远离 15 秒。RSSI 不是米数，请按实际环境校准。
5. 默认广播消失变为 `Unknown`，不因此锁屏。确认手环在佩戴、手机连接、熄屏等情况下持续广播后，才考虑启用“信号消失后也锁屏”，默认超时 45 秒。
6. 保存并开启自动锁屏后，持续远离才调用 `LockWorkStation`。手动 PIN/密码解锁后，必须再次确认稳定靠近，才能触发下一次离开锁屏。

适配器关闭、扫描异常或恢复睡眠后会取消就绪状态。BLE 自动重试采用最多 60 秒退避。系统锁屏状态通过 WTS 查询与通知确认；锁屏 API 返回成功仅表示请求已提交。

## 第一次使用：手机桥接

需要一次性配置，日常操作才可以只点手环：

1. 账号服务已开通，Wear Engine 正在审核。待两项权限通过后，按 [申请资料](docs/wearengine-application.md)核对 App ID、包名和签名指纹。正式 App ID 已写入 `android/gradle.properties`。不要将 Windows 密码/PIN发给开发工具。
2. 让一加上的运动健康正常连接手环，安装 APK，授予蓝牙和通知权限；在 App 内申请 `DEVICE_MANAGER`、`NOTIFY` 权限并选定手环 UUID。查看返回的 Notify 能力。
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

下一步由用户按 [VM 一次性签名确认教程](docs/vm-signed-confirmation-test.md)验证“模拟设备确认 → Windows 实际进入桌面”、拒绝无效确认、防重复及清理。正式登录方案仍受 [Windows 认证实施门槛](docs/windows-authentication.md)约束。Wear Engine 审核通过后，继续验证 **一加 + Band 11 Pro 的 Notify 点击反馈与电脑侧可持续观测性**；VM 模拟私钥不能作为真实手环凭据。
