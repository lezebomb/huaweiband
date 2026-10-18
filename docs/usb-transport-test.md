# 不依赖同一 Wi-Fi 的 USB 通信

当前提供 USB 调试传输，适合开发和隔离 VM 联调。手机仍可通过蓝牙连接运动健康与手环，无需修改已有配对。USB 是已验证的开发通道，最终无线使用仍需单独验证。

## 数据路径

手机 `https://127.0.0.1:19443` → ADB USB reverse → 宿主机 IPv4 回环端口 19443 → 有界 TCP 转发 → VM 私网地址端口 19443。

TLS 仍终止于 VM，宿主机只转发字节。手机继续核对 VM 的精确证书指纹、电脑公钥及签名；连接或转发成功不会授予解锁权限。无需手机与电脑处于同一 Wi-Fi，无需 VMware NAT 入站端口映射。宿主机到 VM 的虚拟私网必须可达，来宾监听和防火墙仍需按 [手机实验教程](vm-phone-bridge-test.md)配置。

宿主机源工程运行：

```powershell
./scripts/connect-phone-usb.ps1 -GuestAddress '<VM私网IPv4>' -AdbPath '<platform-tools目录>\adb.exe'
```

脚本先检查来宾监听是否可达；若端口已有 ADB 映射则拒绝覆盖。默认运行 30 分钟，最多两小时，结束或 USB 断开后清理本次映射和自己的子进程。手机实验配置仅将 endpoint 改为 `https://127.0.0.1:19443`，保留实际 VM 的电脑 ID、公钥和 TLS 指纹。不要将测试夹具身份导入为 VM 身份。

转发器仅监听回环地址，仅接受回环或 RFC1918 IPv4 目标；最多四条并发连接，每连接最长两分钟，建连最多三秒。支持双向流和半关闭，拒绝超额连接，不记录载荷。USB 授权、连通性与手机前台确认分别验证，不能将手机 RSSI 当成手环距离。

## 验证边界

2026-10-18，一加 Android 16 通过实际 USB、ADB reverse、宿主机 TCP 转发和固定证书 HTTPS，完成七轮共 36 项检查：独立实验密钥、公钥导出、电脑签名请求、有效签名及重放拒绝、错误证书拒绝、拒绝确认、过期拒绝、USB 映射移除及撤权拒绝。Windows CNG 另行验证捕获的 Android Keystore 签名，并拒绝篡改。

全部使用固定虚构 SID / 会话和自动测试确认，不含 Windows 密码，不调用 Provider、Vault、锁屏或账户认证。有效授权只写入普通进程的合成交换目录。Android KeyInfo 报告密钥位于安全硬件；这不是独立硬件证明或远程 attestation。普通桥接身份未改变。

```powershell
./scripts/test-phone-usb.ps1 -AdbPath '<platform-tools目录>\adb.exe'
```

需先构建 Windows、原生测试和 `assembleDebugAndroidTest`。脚本比较已安装 APK 哈希，按需安装本工程 APK 和临时测试 APK；ColorOS 安装确认选择继续安装，不更新安装器。每轮有唯一运行 ID，拒绝陈旧结果；运行器使用前台实验页和有时限的测试唤醒锁，单轮最多 90 秒，不修改系统电池策略。结束移除临时测试 APK 和本次反向映射，保留正式应用及 Keystore。原始报告仅在被忽略的 `artifacts/`。

最新脚本初次复验发现系统 .NET 选择错误，已改用项目运行时；后台长等待在实验页置前后才完成，因此补上前台启动和超时限制，失败中间态不计为通过。

尚未完成手机按钮经 USB 到 VM 的实际桌面解锁、来宾手机模式安装/回滚、长期后台、真实手环按钮及距离测试。当前 VM 未启动新手机中继；预检查已拒绝不可达的来宾端口且无残留映射。旧模拟签名自动解锁的用户反馈不能沿用为 USB 解锁结果。

官方依据：[ADB reverse 命令](https://android.googlesource.com/platform/packages/modules/adb/+/refs/heads/main/docs/user/adb.1.md)、[Android Instrumentation](https://developer.android.com/reference/android/app/Instrumentation)、[KeyInfo](https://developer.android.com/reference/android/security/keystore/KeyInfo)。
