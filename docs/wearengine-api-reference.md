# BandKey 的 Wear Engine 接口与恢复入口

本页和链接的 Java 源码是 BandKey 自己的集成说明。当前固定 SDK 为 `com.huawei.hms:wearengine:5.0.3.304`，依赖 `com.huawei.hmf:tasks:1.5.2.206`。

## 接口索引

| 用途 | 官方接口 | BandKey 实现 |
| --- | --- | --- |
| 请求用户授权 | `HiWear.getAuthClient(context).requestPermission`，`AuthCallback` | [MainActivity.java](../android/app/src/main/java/dev/bandkey/bridge/MainActivity.java)，只申请 `DEVICE_MANAGER`、`NOTIFY` |
| 查询可用设备与通知能力 | `HiWear.getDeviceClient(context).getCommonDevice`，`Device.getUuid/isConnected/getNotifyCapability` | [WearableChannel.java](../android/app/src/main/java/dev/bandkey/bridge/WearableChannel.java) |
| 固定手机模式的手环连接状态 | 同上，按已选 UUID 匹配设备 | [WearableConnectionSource.java](../android/app/src/main/java/dev/bandkey/bridge/WearableConnectionSource.java)，查询失败或样本过旧归为未知 |
| 双按钮模板通知 | `Notification.Builder`，`NotificationTemplate.NOTIFICATION_TEMPLATE_TWO_BUTTONS`，`HiWear.getNotifyClient(context).notify` | [WearableChannel.java](../android/app/src/main/java/dev/bandkey/bridge/WearableChannel.java) |
| 按钮反馈与错误 | `Action.onResult/onError` | 同上；当前 SDK 的反馈 2 为第一按钮、3 为第二按钮，仍须通过本次票据和期限检查 |
| 后台串联请求与手环确认 | BandKey 自有接口 | [VmWearConfirmService.java](../android/app/src/main/java/dev/bandkey/bridge/VmWearConfirmService.java)、[VmWearSession.java](../android/app/src/main/java/dev/bandkey/bridge/VmWearSession.java) |

[完整的最小编译示例](../tests/interop/WearEngineArchiveSmoke.java)覆盖授权、设备查询和双按钮通知。它只编译，不访问设备，不执行解锁。

通知发送成功不构成用户批准。只有所选手环对当前有效请求的按钮回调才能进入签名流程；Windows 原生认证仍单独校验。固定手机模式使用连接状态，不把它解释成精确米数。此 SDK 集成未提供让手环额外广播或让 Windows 读取手环 RSSI 的接口。

## 从 GitHub 恢复 SDK

仓库保存了 [四个固定文件的 SHA-256](../android/wearengine-artifacts.json)、官方来源和恢复工具。运行：

```powershell
./scripts/restore-wearengine.ps1
```

脚本从华为官方 Maven 仓下载四个文件至 `.tooling/wearengine-maven`，逐一验证固定 SHA-256。拒绝重定向、未知依赖、校验不符的文件及重解析路径；已存在且校验失败的文件会保留并报错。它不安装应用或操作任何设备。

如果官方源以后不可用，可使用此前的本地离线归档：

```powershell
./scripts/restore-wearengine.ps1 -ArchiveMaven '离线归档解压目录/maven'
cd android
./gradlew.bat --offline '-PbandKeyWearEngineMirror=../.tooling/wearengine-maven' assembleDebug
```

仍需已有 JDK、Android SDK、Gradle 及其他构建依赖，详见 [离线归档说明](wearengine-offline-archive-20261026.md)。SDK 文件和 GitHub 备份不能代替华为服务审批、用户授权、应用签名或运行时服务。

## 仓库与本地归档的范围

GitHub 保存 BandKey 的接口调用源码、笔记、示例、SDK 版本、摘要和恢复工具。本地归档另保留原始 SDK 文件和原签名 BandKey APK；不将整个离线 ZIP 发布到 GitHub。

Wear Engine SDK 的原始 POM 没有标示允许单独再分发的开源许可。华为公开《开发者服务协议》4.3 限制未获许可的华为内容分发，4.5 另行约定开源组件；因此没有把 SDK 原包按开源软件发布。华为公开示例的 Apache-2.0 许可只适用于该示例，不能据此给 SDK 或服务重新授权。

官方来源：

- [Android SDK 集成](https://developer.huawei.com/consumer/cn/doc/connectivity-guides/integrating-phone-sdk-0000001051137958)
- [权限参考](https://developer.huawei.com/consumer/cn/doc/connectivity-References/permission-0000001059433005)
- [官方示例](https://github.com/huaweicodelabs/WearEngine)
- [开发者服务协议](https://developer.huawei.com/consumer/cn/devservice/ServiceAgreement/)
