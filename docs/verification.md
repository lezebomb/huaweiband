# 验证记录

日期：2026-10-17，Asia/Shanghai。

## 已实际运行

- .NET SDK 10.0.402 官方下载、SHA-512校验、项目本地解压。
- Windows Release 全工程构建成功，无编译错误或警告；自包含win-x64目录和ZIP已生成。
- 核心测试44/44，Windows集成测试11/11。报告位于 `artifacts/core-tests.json`、`artifacts/windows-tests.json`，包含生成时间与逐项结果。
- 真实回环网络HTTPS监听、固定证书校验、签名轮询和授权提交、无效签名/重复请求/错误证书/畸形JSON处理；锁屏与距离上下文来自测试构造。
- DPAPI应用签名密钥/HTTPS证书的保存与重新加载。测试发现并修正了Schannel对临时密钥加载模式的兼容问题；没有向受信任证书库安装证书。
- JDK编译Android生产使用的Wire.java与CertificatePinTrust.java，Java验证.NET签名，再由.NET验证Java签名；校验证书固定验证器对正确、错误、过期、空证书链的处理。
- Windows真实Realtek适配器的20秒被动扫描成功，没有配对、GATT连接或写操作。原始附近广播报告仅留在本地artifacts；尚未识别手环身份。
- Android assembleDebug成功，使用官方WearEngine 5.0.3.304 AAR和tasks依赖；已安装并运行在一加 Android 16 实机。
- APK通过apksigner验证；Android Lint为0错误、3警告（低版本忽略扫描权限标记、固定Gradle版本、自定义证书信任）。该信任实现只接受用户导入的精确叶证书指纹，已做正反例测试，没有信任所有证书。
- Android 生产用 PromptGate、RetrySchedule 的 JDK 测试 19/19：迟到设备查询、停止后点击、改选时旧回调、排队授权过期、系统时间回退、32 个并发按钮事件、重复消费及退避恢复。报告 `artifacts/android-runtime-tests.json`；这些是本地生命周期测试，没有调用成功的华为 SDK 或手环。
- 更新后的 APK 已在同一台一加上使用 `adb install -r` 覆盖安装；Activity 冷启动成功，界面读回的 Android Keystore 公钥指纹与更新前一致。记录 `artifacts/phone-20261017/runtime-update-smoke.json`；没有启动桥接或调用手环。
- 监督脚本启动真实的无害测试子进程，第一次异常退出后重启，第二次正常退出后停止，验证通过；没有注册真实启动任务。
- 最终自包含BandKey.exe直接运行3秒被动扫描，退出码0，确认不依赖系统安装的.NET 10运行时。配置窗口离线渲染已检查。

## 尚未验证

手环通知弹出、真实按钮反馈、ColorOS长期后台、手环广播与佩戴者距离对应关系、真实自动锁屏、系统重启/蓝牙断连真机恢复、任务计划的实际注册执行、任何形式的Windows真实解锁。

一加 ADB 已连接；设备环境、HMS Core 安装、正式 App ID 配置、SDK 错误 8 与广播扫描结果见 [实机记录](device-validation-20261017.md)。没有已经准备好的隔离Windows虚拟机供登录组件实验。本阶段没有注册Credential Provider或更改系统登录机制。

## 已纠正的调查/开发问题

首次账户检测读取了沙盒身份，不能代表桌面用户；最终真实用户环境显示MicrosoftAccount。最初HTTPS测试失败，修正Windows证书密钥加载后才获得真实网络测试通过结果。SDK目录配置与Java编译错误已经按实际构建输出修正。未把失败的中间测试计作通过。

交付包校验和位于artifacts/release-manifest.json；不能从“编译成功”推断硬件链路已成功。
