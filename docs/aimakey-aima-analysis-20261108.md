# AimaKey：爱玛出行安装包分析

日期：2026-11-08。范围：用户已绑定车辆的手机上实际安装的 App，USB ADB 只读查询与 APK 静态分析。**尚未确认可供 AimaKey 调用的公开启动车辆／关闭车辆接口。当前软件不包含真实控车适配器。**

2026-11-09用户补充真机观察：未启动时“右滑启动”，右上角显示蓝牙连接；启动后同一控件变为完整图标，点按即可关闭；锁屏保持官方连接。这部分UI流程已接受，不再要求重复演示；具体代码分支、第三方调用与车辆状态回执仍未证实。开发与测试转为[手环真实点击、手机离线记录](aimakey-field-architecture-20261109.md)。

## 已由真机只读查询证实

| 项目 | 结果 |
|---|---|
| 手机型号 | PLR110（一加 Ace 6T） |
| Android | 16，API 36 |
| ROM 版本字段 | V16.1.0 |
| 包名 | `com.adb.useroperation` |
| App 版本 | `5.9.5`，versionCode `5090500` |
| App SDK | minSdk 24，targetSdk 31 |
| 安装包 | `pm path` 只返回 `base.apk`，本次没有 split APK |
| APK 大小 | 534,948,775 字节 |
| APK SHA-256 | `ded1f1f699c06dbfc2f2db572f9e9d67a1339a9a2a4182e931de0bc3d83c4b6f` |

ADB 37.0.1 可用。用户插线并授权后，REA 的 `resolve_adb_packages`、`pull_adb_package` 完成确认与提取。本机文件位于 `.research/aimakey/installed/com.adb.useroperation/base.apk`；安装目录包含系统随机路径，未写入公开说明。未读取账号数据库、车辆配对凭据或设备密钥。

REA Android 反编译提供者未配置所需的 JADX MCP JAR，因此没有把失败的 REA 调用当成成功分析。改用本地 JDK 21、JADX 1.5.6、Android aapt2 36.0.0，未将 APK 上传到第三方服务。JADX 源码是反编译视图，部分方法带还原警告；只对实际可读的调用和声明下结论。

## Manifest 和对外入口

**APK 声明分析证实**：aapt2 解码 Manifest，组件清单识别 293 个 Activity／Service／Receiver／Provider 声明，其中 39 个显式 `exported=true`。这是声明计数，未把缺省值推断为已验证的外部可调用性。

- `io.flutter.plugins.MainActivity` 为主入口，显式导出。包含 Launcher 和多种 NFC `NDEF_DISCOVERED`／`TECH_DISCOVERED`／`TAG_DISCOVERED` 过滤器。
- `aimatravel`、`aimaTravel`、`https://amapp.aimaiot.cn` 等线索位于 NFC 过滤器；不能把它们视为有定义的车控 `ACTION_VIEW` API。没有调用这些 URI 做未知动作试验。
- `com.adb.useroperation.NFCBootActivity` 导出，但任务不使用 NFC；未调用。
- 没有发现 `android.app.shortcuts` 元数据、静态 AppWidget 提供者动作 `APPWIDGET_UPDATE` 或明确公开的 START／STOP Intent。这只覆盖本次 APK Manifest，**不排除未分析的运行时动态 shortcuts／PendingIntent**。
- `com.blankj.utilcode.util.MessengerUtils$ServerService` 即使有应用内 messenger action，也明确 `exported=false`。
- `net.easyconn.carman.bluetooth.BleService` 确实导出，action 为 `net.easyconn.carman_action_carman_service_ble_phone`。进一步反编译发现它的 binder 结构围绕 `IBleAction`、`IHudAction`、`IWrcAction`，初始化 HUD／WRC 管理器；没有证据把它等同爱玛动力控制服务。**没有绑定或试调这个服务。**
- 推送、微信／QQ回调、图片裁剪、音频媒体、通知监听等导出组件均不能仅凭导出就作为合法车控入口。系统权限保护的组件也不代表普通 App 有权使用。

蓝牙权限声明包括 `BLUETOOTH`、`BLUETOOTH_ADMIN`、`BLUETOOTH_SCAN`、`BLUETOOTH_CONNECT`、`BLUETOOTH_ADVERTISE`，另有前后台位置、前台服务、connectedDevice 前台服务权限等。声明不能证明锁屏控车流程或车辆连接状态。本次没有修改这些授权。

## 已由反编译视图证实的 Android 链路

以下位置均在本机 `.research/aimakey/`，不提交原始反编译文件。

| 位置 | 关键点 |
|---|---|
| `MainActivity.java`（classes13.dex，`configureFlutterEngine`） | 注册插件并调用 `GeneratedPluginRegistrant.registerWith`；主 UI 是 Flutter |
| 同文件 `initBaseMsgCh` | `bmc/cn.aimaiot.app` 是 Flutter 进程内 BasicMessageChannel；收到消息仅回复固定字符串，不是跨 App 车控服务 |
| 同文件 `getBootExtras`／`onNewIntent` | 当前可读实现没有把 Intent extras 分发成启动／关闭的逻辑；不能覆盖 Flutter 插件内部其他可能的处理 |
| `GeneratedPluginRegistrant.java` | 注册 `AdbBluetoothPlugin` 和 `FlutterBluePlusPlugin`，说明有多条蓝牙相关实现，不能预设当前车辆只用其中一条 |
| `AdbBluetoothPlugin.java`（classes11.dex） | 注册 `adb_bluetooth_plugin` MethodChannel 与 `adb_bluetooth_upload` EventChannel；`onMethodCall` 分发给 `PluginBridge.dispatch` |
| `PluginBridge.java`（`PluginBridge.kt`） | 先判断 BLE 支持；按方法名在 `IAdbBleBridge` 中反射查找并调用本进程的 `AdbBleBridgeImpl`；不是 Binder／导出 Android Service |
| `AdbBleBridgeImpl.java`（`bluetoothCommand`，本地反编译视图约104行） | 检查系统蓝牙已开与 `AdbBleManager.getIsConnected()`；不满足时调用失败回调；随后调用 `AdbBleManager.command(commandId, commandParameter, success, failed)` |
| 同文件 `initBluetooth` | 初始化参数包含蓝牙身份和认证材料字段，说明复用需保持官方授权上下文；没有提取这些字段的运行值 |
| `EasyconnBleService.java`（classes4.dex，`onBind`） | 返回 EasyConn BLE/HUD/WRC 相关 binder，不能证明能控制电动车动力 |

确认到的子链路为：

```text
Flutter MethodChannel 调用（调用方业务尚未完整定位）
  → AdbBluetoothPlugin.onMethodCall
  → PluginBridge.dispatch
  → IAdbBleBridge / AdbBleBridgeImpl.bluetoothCommand
  → 蓝牙开启及连接状态检查
  → AdbBleManager.command(..., success, failed)

上传事件：AdbBleManager 状态监听 → EventChannel adb_bluetooth_upload → Flutter
```

这条链只能证明插件如何分发，**不能证明首页“右滑启动”一定使用此插件／此 command，也不能证明 success 回调表示电动车已实际启动。**没有编写或发送推测的 commandId、commandParameter、私有认证数据。

## Flutter AOT 证据和未完成部分

业务代码主要位于 `lib/arm64-v8a/libapp.so`，约 28.9 MB；JADX 不会把 Dart AOT 还原成完整原始 Dart 逻辑。静态字符串中存在以下线索，文件偏移仅定位字面量，**不是函数入口、调用边或已执行证据**：

| 本机 libapp.so 文件偏移 | 字面量／线索 | 当前解释 |
|---|---|---|
| `0x0FAF72` | `.../vehicle_info_component/widget/vehicle_control_widget.dart` | 首页控车组件候选位置 |
| `0x101B9F` | `realDoPowerOnTask` | 启动业务候选名称 |
| `0x0A5773` | `CarControl.PowerOn` | PowerOn 与其他动作存在分离命名 |
| `0x2C72F1` | `CarControl.PowerOff`（邻近字节还含额外字符） | PowerOff 候选，不足以推导关闭指令 |
| `0x0EC37F`／`0x2828A7` | `_onVehicleStartBLEReport`／`_onVehicleStopBLEReport` | 可能有启动／停止上报处理，未验证回执语义 |
| `0x0A3517` | `.../vehicle_settings/ble/ble_control_extension.dart` | BLE 业务扩展候选 |
| `0x0B7872` | `AimaBleControlLockResult` | 锁相关结果类型，不与动力启动视为同一操作 |

尚未证实：滑动 UI 的真实事件入口、具体车辆类型所选的 BLE／云端路径、PowerOn 到具体插件指令的绑定、PowerOff 的实际 UI 和条件、当前车型的车速／静止状态来源、实际车辆完成回执。未利用 root、注入、重签 App、私有认证重放或任何修改车辆保护的方式补齐这些空白。

## 集成结论和可继续推进的路径

反编译得到的 Kotlin／Java 类和 Flutter MethodChannel 都处于官方 App 的 UID、进程、FlutterEngine 和授权状态中。AimaKey 不能因为知道方法名就直接调用该实例，也不能复制类或读取其私有凭据来冒充官方 App。

真实 `VehicleController` 接入需要已确认且允许第三方使用的接口及结果语义：例如爱玛提供的 SDK、明确文档化的受保护 Binder／Intent、或由官方配合开放的用户授权控制接口。本次 APK 分析没有找到可直接采用的接口；不等于宣称全球不存在这类合作接口。

下一次实车研究应先由用户确认车辆静止、安全停放且明确同意，由用户手动在官方 App 做一次正常启动和正常关闭，开发侧只观察。记录 UI 状态、时间点与脱敏日志，区分点击、SDK 发送、BLE 接收、实际动作完成。不启动未知导出组件，不抓取／重放车辆认证。没有这一步的明确同意就不操作实车。
