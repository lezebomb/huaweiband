# AimaKey 0.1.0：实现、验证与设备操作

这是可运行的**手机模拟实验版**。包名 `dev.bandkey.aimakey`，界面和所有成功反馈明确标注模拟。不含真实电动车 BLE／HTTP 指令，也不操作爱玛出行 App。最终的“11 Pro 固定页面 → 锁屏手机 → 实车启动／关闭”尚未实现。

调查详见 [Wear Engine 与 11 Pro 入口](aimakey-wearengine-research-20261108.md)及[爱玛 APK 分析](aimakey-aima-analysis-20261108.md)。

后续到车旁的具体操作见[实车观察测试步骤](aimakey-real-vehicle-test.md)，包含不带电脑和 USB 本地日志两种方式。

## 模块和真实完成情况

| 文件／模块 | 已完成 | 验证／限制 |
|---|---|---|
| `android/aimakey-core` | 不依赖 Android 的车辆接口、请求／结果协议、协调器、页面状态模型、Mock | 自动化模拟测试；没有真实车型实现 |
| `VehicleController` | `startVehicle`、`stopVehicle`、`getVehicleConnectionState`、分层回调 | 真实适配器必须提供车辆状态证据和静止证据 |
| `AimaCoordinator` | 校验会话、序号、有效期，持久化预占，去重、超时、断连、拒绝动作 | `getLastOperationResult` 返回最后已记录操作结果；不自动重试动作 |
| `AimaPageModel` | 两动作交互、关闭二次确认、待处理防重复、旧结果过滤、重连不重发 | 手机原型实际使用该模型；不是已安装的手环程序 |
| `android/aimakey` | 独立 Android App、手机页面、常驻服务、测试场景、只读能力探测 | 独立 UID／数据／通知；不覆盖 BandKey |
| `WearCapabilityProbe` | Android SDK 设备查询和用户设备管理授权入口，10秒查询超时 | 本独立包没有取得华为服务身份，默认不调用 SDK；本轮未做实时能力查询 |
| `WearPeerTransport` | 与固定 SDK 编译的对端查询、按包名／签名注册收发、代际失效／关闭 | 预留适配代码；没有已验证的穿戴 App 身份，不由模拟服务启用，未做真实 P2P 通信 |
| `AimaInstrumentation` | 真机本地服务、Mock 启停／状态／超时／后台／熄屏测试 | androidTest 专用，不调用爱玛或真实车辆 |

拟定的生产架构仍为：

```text
经证实可安装的手环双按钮页面（目前缺失）
   ↕ 已授权、固定对端身份的 Wear Engine P2P（目前未联通）
AimaKey 手机服务 → AimaCoordinator → VehicleController
   ↕ 公开且允许第三方使用的爱玛接口（目前缺失）
用户已绑定的爱玛车辆
```

当前可运行路径为 `AimaActivity → AimaPageModel → 本地绑定服务 → 请求解码 → AimaCoordinator → MockVehicleController → 结果编码／解码 → 页面反馈`。

## 协议和安全行为

请求为 UTF-8、最多256字节、严格八字段（制表符分隔）：

```text
aimakey/1  session  requestId  sequence  START|STOP  issuedAtMs  expiresAtMs  stopConfirmed
```

`session` 是手机服务进程生成的128位随机值，编码为32位十六进制；`requestId` 为规范 UUID；序号严格递增；TTL 最多15秒，页面目前使用5秒。协议不接收 URL、车辆标识、自由方法名或任意指令参数。

结果使用 `aimakey/result/1`，包含同一 session、requestId、action、stage、受限错误代码和 simulation 标志。页面只处理对应会话和当前操作的结果。**session 本身不是身份认证**；未来跨设备接入必须使用 SDK 固定包名／签名的可信对端，并实现会话／状态同步。当前会话通过本地绑定服务注入页面，没有宣称已实现跨设备握手。

| 阶段 | 含义 |
|---|---|
| `ACCEPTED` | 已预占，尚无发送证据 |
| `REQUEST_SENT` | 适配器报告请求已发送，不能显示启动车辆成功 |
| `VEHICLE_RECEIVED` | 适配器报告车辆已接收，仍不代表动作完成 |
| `CONFIRMED` | 收到新鲜、已连接、与动作目标电源状态相符的车辆状态证据；Mock 必须标为模拟 |
| `REJECTED`／`FAILED` | 拒绝或失败，错误代码明确说明原因 |
| `TIMED_OUT` | 结果未知，不自动补发或反向操作 |

关键策略：

- 启动和关闭只由界面明确触发；连接、靠近、服务启动、重连和任务恢复不发动作。
- 关闭需要二次确认，同时要求适配器提供不超过2秒的静止状态；未知／移动／陈旧状态都拒绝。用户勾选确认不能替代真实静止证据。
- 进程内动作超时使用单调时钟，不因手机墙钟回拨延长；迟到回执不能把超时改成成功。
- 同一 requestId 重复返回原结果；改动作使用同一 ID 被拒绝；序号回放被拒绝。
- **已拒绝的请求也被消耗**。重连或忙状态消失不能让网络重传的旧点击变成新动作。512个记录达到上限时拒绝新操作，不淘汰记录制造重放窗口。
- SharedPreferences 同步 `commit` 预占失败时不调适配器；进程重建生成新会话，不恢复执行队列。当前只保存最后预占信息，旧会话请求失效，而非恢复旧会话。
- 前台服务返回 `START_NOT_STICKY`；系统杀死后需要用户重新开启。没有靠开机／连接广播自动执行或恢复动作。
- 调试日志只写受控状态和错误码，不记录请求包、nonce、车辆／账号标识或凭据。备份／迁移规则排除本地数据。

真实适配器还需在动作提交处重新保证当前授权与车辆安全条件，明确每种回调的含义；本次没有使用 Mock 来替代这项验收。

## 构建与自动化

环境：JDK21、Android API36／Build Tools36.0.0、Gradle9.3.1、AGP9.1.0、Wear Engine5.0.3.304。本机原开发缓存已清理，本次在 `.tooling/` 恢复工具，不改系统 Java 或已安装的 BandKey。

```powershell
./scripts/test-aimakey.ps1 -JavaHome '<JDK21目录>'
./scripts/build-aimakey.ps1 -JavaHome '<JDK21目录>' -SdkPath '<Android SDK目录>' -Lint -DeviceTests
```

源码不包含 AimaKey 华为服务身份。正式取得独立包名／签名对应的服务权限后，才可使用构建参数 `-WearEngineAppId '<AimaKey自己的ID>'`；原 BandKey ID 不应直接填入。现有 [SDK 恢复工具](../scripts/restore-wearengine.ps1)和固定哈希继续可用。

产物为 `artifacts/aimakey/AimaKey-0.1.0-mock.apk`，签名校验通过；生成物、签名私钥和测试结果都留在忽略目录。本轮没有更新 Windows 组件。原 `build-android.ps1` 明确限定 `:app` 任务，新增模块不会悄悄扩大原 BandKey 构建脚本的产出范围。

## 已运行的验证

截至本轮交付前实际运行：

| 验证 | 结果 | 说明 |
|---|---|---|
| AimaKey 核心 | 55项通过 | 请求与结果编解码、明确阶段、重复／变造ID、序号回放、过期／未来请求、停止确认、移动／未知状态、断连、忙、存储失败、超时、迟到回执、记录上限、页面重连和回执过滤 |
| Android 构建 | AimaKey App、androidTest、原 BandKey App 构建通过 | 使用固定 Wear Engine SDK，未把编译成功当成手环支持 |
| Android Lint | AimaKey 0错误、0警告 | 原 BandKey 0错误、6项原有警告；本轮另修复 AimaKey 备份配置和系统栏布局，并在最终手机截图确认标题未被系统栏遮挡 |
| 原 BandKey Java 回归 | 151项通过 | 10个现有测试套件，含1000次帧编解码随机往返 |
| 原 BandKey Core | 190项通过 | 未修改原核心实现 |
| 原 BandKey Windows | 108项通过 | 包含 Java／.NET P256 互通；先恢复工具链环境再通过，不涉及真实主机解锁动作 |
| 一加真机 Mock instrumentation | 12项通过 | `screenOffTest=true`、`keyguardLocked=true`；真实手机、模拟车辆 |

真机测试在短暂熄屏并锁定时验证一次 Mock 回执及后台服务，熄屏段约数秒。**不是长期锁屏、深度 Doze、厂商清理、自启动、真实 BLE 或 Wear Engine 双向链路验收。**现有真实 Windows 手环解锁仅做软件回归，没有在这轮重跑完整真机解锁场景。

最终交付 APK SHA-256：`f68daa94a54590632ee2cece972fc98ef407cc2b2f52792098eb7bc7adc5b535`。调试构建换签名身份或重新构建后，哈希可能变化。

## 手机手动使用与预期

1. 手机已安装“AimaKey 模拟实验室”；也可通过 USB 使用 `adb -s <序列号> install -r artifacts/aimakey/AimaKey-0.1.0-mock.apk`。ColorOS 若弹出安装确认，需手动允许。首次安装曾返回 -99；用户解锁并允许后重试成功，没有关闭系统安全保护。
2. 打开 App，点击“启动”：预期依次出现模拟发送、接收、启动态确认；点击“关闭”，确认弹窗后出现模拟关闭态确认。所有成功结果必须带 `[模拟]`。
3. 切换车辆未连接／移动中／静止未知：前者拒绝启动，后两者拒绝关闭。切换无回执／仅接收：约5秒后显示结果未知，不重试。
4. 点击“开启锁屏后台模拟”，通过正常系统弹窗允许附近设备与通知，再点击一次以开启前台服务。ColorOS 禁止 ADB `pm grant`，测试脚本不再尝试代授或要求扩大 USB 调试权限。
5. 自动测试（只操作 Mock）：`./scripts/test-aimakey-device.ps1 -Serial '<序列号>' -ScreenOff -SkipInstall`。该命令短暂熄屏，结束唤亮；不会输入锁屏密码。手机仍锁定时由用户正常解锁。
6. 结束实验点击“停止后台模拟”。切勿把模拟的“启动态确认”理解成实车已操作。

## 恢复、卸载和下一阶段

回滚本阶段手机实验：先停止后台模拟，再通过系统卸载“AimaKey 模拟实验室”；或只对独立包执行：

```powershell
adb -s <序列号> uninstall dev.bandkey.aimakey.test
adb -s <序列号> uninstall dev.bandkey.aimakey
```

不卸载 `dev.bandkey.bridge`、爱玛出行、运动健康或 HMS，不解除手环／车辆绑定。无需撤销或重新登记 Windows 登录组件；本阶段没有安装它们。

下一阶段依赖两个独立障碍：华为确认这只11 Pro的可安装／可常驻第三方入口；爱玛提供或确认有权限使用的控制接口及车辆完成状态。任何实车动态观察都须先由用户确认安全停稳并明确同意，优先由用户亲自操作官方 App。确认正常关闭控件、条件与回执之前，不生成停止车辆指令。当前已准备好手机侧模型和测试，可在接口明确后替换 Mock，不影响既有 BandKey。

已准备被动观察脚本 `scripts/observe-aima-app.ps1`：只观察已运行的官方 App 进程日志，不启动／点击 App，不清日志，不发送控车包。**须先取得本次实车观察同意，再让用户手动操作**。原始输出位于仅当前用户与 SYSTEM 可访问的 `.research/aimakey/observations/`，不提交 Git；日志缺少事件不能证明动作未发生。

用户于本轮明确选择“现在不做实车测试，先交付这一阶段”。因此没有运行该观察脚本；只完成 PowerShell 语法解析检查，没有宣称真实启动或关闭通过验证。
