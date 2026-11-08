# AimaKey：Band 11 Pro 常驻入口能力调查

调查日期：2026-11-08。当前结论：**没有证据支持在这只 Band 11 Pro 上安装自定义 AimaKey App／卡片。最终的手环双按钮页面尚未实现。** 保留独立页面模型与通信接口，继续开发手机模块，不用模板通知替代该目标。

## 证据等级

- **官方文档证实**：对应平台、设备和文档明确描述的能力。
- **SDK／源码分析证实**：固定版本 SDK 的公开签名、字节码或开源项目实现；不等于目标设备验证。
- **模拟测试通过**：本项目 Mock 行为，不代表车辆或手环行为。
- **真机验证**：注明设备、时间、操作及限制；用户观察与自动采集分别记录。
- **尚未证实**：未取得适用接口、运行时结果或端到端验证。

## 原工程和授权边界

BandKey 使用 `com.huawei.hms:wearengine:5.0.3.304` 和 `com.huawei.hmf:tasks:1.5.2.206`。本次从华为官方 Maven 恢复四个文件，按 `android/wearengine-artifacts.json` 校验 SHA-256，再使用 JDK `javap` 和 JADX 1.5.6 检查公开 API。

`MainActivity.java` 当前只请求 `Permission.DEVICE_MANAGER`、`Permission.NOTIFY`；`WearableChannel.java` 使用 `getCommonDevice`、`getNotifyCapability` 和 `NOTIFICATION_TEMPLATE_TWO_BUTTONS`。它能复用通知授权与错误处理思路，不能产生应用列表入口。没有在本次替换已安装的 BandKey 或修改 Windows 登录组件。

华为开发者服务审批、App ID／包名／签名配置、手机用户授权、穿戴端 App 安装与身份匹配是不同条件。BandKey 现有通知可用，不证明 AimaKey 独立包具有相同审批或 P2P 授权。本次独立包默认 `UNCONFIGURED`，不冒用 BandKey 的 App ID。华为官方[接入流程](https://developer.huawei.com/consumer/en/doc/connectivity-guides/dev-process-0000001051058039/)要求注册应用信息和取得服务权限。

## APP_INSTALLATION：ArkTS 和 Android 不相同

**官方文档证实**：[Wear Engine ArkTS API](https://developer.huawei.com/consumer/cn/doc/doccenter-references/api/wearengine_api/)的 `wearEngine.DeviceCapability.APP_INSTALLATION = 14` 可由 `device.isDeviceCapabilitySupported(...)` 查询。它属于 `@kit.WearEngine`、HarmonyOS Stage 模型；不是 Android Java API。该文档也包含 `startRemoteApp`、`isRemoteAppInstalled` 等方法，不能据此推断一加 Android 可以直接调用。

**SDK／源码分析证实**：当前 Android AAR 中 `DeviceClient` 确实存在 `Task<Integer> queryDeviceCapability(Device,int)`。但是[Android 设备查询指南](https://developer.huawei.com/consumer/cn/doc/connectivity-guides/device-connection-management-0000001050779152/)目前只文档化 CBTI 数字疗法能力，ID 为 128。返回 0／1 表示支持／不支持；`Device` 另定义未知值 2。**没有把 ArkTS 的 14 当成受官方支持的 Android 安装能力查询。** Java 中存在通用 int 参数，不等于每个值都具有跨平台相同语义。

Android AAR 可见的相关 API：

| 类 | 已核对的 API | 能说明什么 |
|---|---|---|
| `Device` | `isConnected`、`getP2pCapability`、`getNotifyCapability` | 连接与通信能力，不是第三方 UI／安装许可 |
| `DeviceClient` | `getCommonDevice`、`getBondedDevices`、`queryDeviceCapability` | 获取设备及文档规定的能力查询 |
| `P2pClient` | `isAppInstalled`、`getAppVersion`、`registerReceiver`、`send`、`ping` | 与已存在的对端 App 通信；没有创建／安装页面 API |
| `Peer.Builder` | `setDevice`、`setPkgName`、`setFingerPrint` | 对端设备和应用身份匹配 |
| `NotifyClient` | `notify` | 模板通知，不是常驻 App |

完整公开方法依据同时参见官方 [DeviceClient](https://developer.huawei.com/consumer/cn/doc/connectivity-References/deviceclient-0000001060779120/)。本地提取和反编译内容位于忽略目录 `.research/aimakey/`，没有把 SDK 或反编译副本提交到仓库。

## 这只设备的实际证据

1. **真机验证（用户 2026-11-08 手动查看）**：运动健康的这只手环详情页没有应用市场；手环卡片管理只能选内置功能。未改配对、未安装 Gadgetbridge、未修改固件。
2. **历史真机诊断文件，本次只读读取**：原 BandKey 的 `wear-diagnostics.json` 记录时间为 2026-10-30 18:17:59 +08:00，型号字段 `Candy-B59FB`，软件 `6.0.0.130(SP3C00M03)`，运动健康 `17.0.8.300`、HMS `6.16.4.352`，服务 API level 14，P2P=0、Notify=0（0 为支持）。这不是本次重新查询，也不是独立 App 安装验证。未公开设备名称后缀、UUID 或序列号。
3. **官方文档证实**：[11 系列按键和屏幕控制](https://consumer.huawei.com/cn/support/content/zh-cn16085694/)描述应用列表、左滑卡片等系统入口。[桌面卡片设置](https://consumer.huawei.com/cn/support/content/zh-cn16016534/)说明管理已有卡片。两者不能证明允许开发者注册新卡片。

华为[应用管理说明](https://consumer.huawei.com/nz/support/content/en-gb16066729/)说明需在运动健康的设备详情中使用 AppGallery 安装应用。该说明主要适用所列手表，不能把手表安装能力移植为 Band 11 Pro 支持声明。

## 方案比较

| 方案 | API／依据 | 11 Pro 当前证据 | 安装／权限条件 | 固定双按钮页 | 与 BandKey 复用关系 |
|---|---|---|---|---|---|
| A 原生穿戴 App | 已安装对端 App + Wear Engine P2P；ArkTS 有安装能力查询 | 无应用市场；尚无可安装路径 | 需真实穿戴运行包、签名、设备支持、应用服务配置 | 支持安装的设备上原则可实现；本机未证实 | 可复用 SDK 版本、设备查询思路；不能复用通知作为页面 |
| B 自定义桌面卡片 | 官方只确认管理已有卡片；当前 Android SDK 无注册自定义卡片 API | 用户只见内置卡片 | 未找到面向该型号的第三方卡片接入途径 | 尚未证实 | 没有可直接调用的现成模块 |
| C 可交互表盘／快捷入口 | Theme Studio 数据容器点击可跳转对应应用 | 没有任意自定义回调／新 App 入口的证据 | 表盘设计师权限和适配资源不等于安装 App 权限 | 未证明能发送 START／STOP 消息 | 常规表盘资源不能直接调用 BandKey Android 代码 |
| D 其他 Wear Engine 交互 | 模板通知、状态与传感器、P2P | 已有通知按钮历史真机结果 | 对应服务及用户权限 | 通知不是独立页面；不满足本任务固定入口 | 只复用诊断、状态、错误处理方式 |
| E Gadgetbridge 扩展 | Band11Pro coordinator、HuaweiState 和运行时能力判断 | 项目识别该设备；未证明能安装第三方 App | 自有连接／配对机制，且设备实际能力决定支持范围 | 尚无该设备可行证据 | 不替换运动健康连接，不安装到手机做试验 |

表盘判断依据为华为[表盘 FAQ](https://developer.huawei.com/consumer/cn/doc/content/watchface-faq-0000001174035539/)与[表盘测试流程](https://developer.huawei.com/consumer/en/doc/content/watch-face-test-pro-0000001633927113/)。这些资料说明数据容器跳转、资源包和设计师测试；没有证实 Band 11 Pro 上任意车控代码或 Wear Engine 回调能力。

Gadgetbridge 原始仓库通过 Codeberg API／raw 只读检查：

- [HuaweiBand11ProCoordinator.java](https://codeberg.org/Freeyourgadget/Gadgetbridge/src/branch/master/app/src/main/java/nodomain/freeyourgadget/gadgetbridge/devices/huawei/huaweiband11pro/HuaweiBand11ProCoordinator.java)：匹配设备名称并归类 FITNESS_BAND，没有为该型号声明 App 安装支持。
- [HuaweiCoordinator.java](https://codeberg.org/Freeyourgadget/Gadgetbridge/src/branch/master/app/src/main/java/nodomain/freeyourgadget/gadgetbridge/devices/huawei/HuaweiCoordinator.java)：应用管理交给设备状态；其中表盘管理也使用 apps management 能力，不能把这个布尔值解释为可安装通用 App。
- [HuaweiState.java](https://codeberg.org/Freeyourgadget/Gadgetbridge/src/branch/master/app/src/main/java/nodomain/freeyourgadget/gadgetbridge/devices/huawei/HuaweiState.java)：`getSupportsInstalledAppManagement()` 依赖 `supportsAppParams()`，源码还提示判断可能不准确。
- 官方项目[设备与功能说明](https://gadgetbridge.org/basics/topics/huawei-honor/)也明确各型号能力并不完全相同。本次未连接、未侧载、未获取配对密钥。

## 软件决定与下一步

新增独立 `:aimakey-core` 与 `:aimakey`，保持原 BandKey 的运行包、数据、通知和 Windows 授权协议隔离。手机原型和 `AimaPageModel` 都只保留启动／关闭主动作，提供连接状态和分阶段反馈；`WearPeerTransport` 编译对接真实 Android SDK，但未接入运行中的模拟服务，也未配置未知穿戴包名／指纹。

继续获得真正入口需要华为对 **Band 11 Pro、当前固件、第三方安装／卡片 SDK 和具体权限**给出明确适用资料或新增支持。若有这样的资料，再建立对应穿戴工程并做真机安装验证。现阶段不能靠 DevEco 模拟器、其他 WATCH 型号、历史 P2P=0 或不断发送通知宣称目标已实现。
