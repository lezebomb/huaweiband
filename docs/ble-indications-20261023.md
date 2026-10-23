# BLE 确认交付准备版（2026-10-23）

Android 0.3.3 与本阶段 Windows 助手新增 GATT indication。它请求对端 ATT 确认；仍须固定证书 TLS、电脑签名及一次性授权，GATT 确认不构成身份认证。Android 官方 [`notifyCharacteristicChanged`](https://developer.android.com/reference/android/bluetooth/BluetoothGattServer#notifyCharacteristicChanged(android.bluetooth.BluetoothDevice,%20android.bluetooth.BluetoothGattCharacteristic,%20boolean,%20byte%5B%5D)) 的 `confirm=true` 用于 indication；Windows 官方 [CCCD 枚举](https://learn.microsoft.com/en-us/uwp/api/windows.devices.bluetooth.genericattributeprofile.gattclientcharacteristicconfigurationdescriptorvalue)定义 Notify=1、Indicate=2。

新手机同时提供 Notify 和 Indicate。新 Windows 助手优先订阅 Indicate，只提供 Notify 的旧手机继续使用原方式；旧助手也可继续连接新手机。健康检查核对实际协商的 CCCD，不能把 indication 误判为订阅丢失。传输期间切换订阅会结束当前流，后续必须重新建立受 TLS 保护的连接。CLI 报告新增 `deliveryMode`，用于辨别实际使用的方式。

修正了 VM 轮询等待／退避期间未及时刷新过期按钮状态的问题。真实提交仍经过原有票据及有效期检查。此外修正 `scripts/build.ps1` 的参数数组括号：PowerShell 原先把两个 MSBuild 属性拼为一个字符串，现分别传参。

## 验证状态

- Android 0.3.3 构建、APK 签名及 Lint 通过，0 错误、4 警告；Java 检查 83 项通过，新增 12 项 CCCD 编码与非法输入检查。
- Windows 构建 0 警告、0 错误；核心 115/115、Windows 集成 81/81，通过通知兼容、indication 优先及协商值健康检查等用例。Socket、TLS、密码学和文件交换实际执行，GATT 是模拟输入，未注册或提交 Windows 登录。
- 手机已带走，**0.3.3 尚未安装，indication 未实机验证**；一加仍为已实测的 0.3.2。此前 TCP 10053、GATT 写入超时不标记为已解决。

## 准备文件与短复测

本地 APK：`artifacts/BandKey-bridge-0.3.3-debug.apk`。新助手目录：`artifacts/BandKey-win-x64-indicate-20261023/`，内有 `启动BLE助手.cmd`。这些文件均忽略，不进入 Git。

手机再次连接后保留数据更新一次 APK，先只启动 BLE 与轮询，使用新电脑助手连接原 VM 中继；不必重装 VM 或重新登记密钥。确认断开／重连能恢复，并用 CLI 报告核对 `deliveryMode=indicate`，再单独验证手环确认与拒绝。新版协议不以旧版结果代替新实机验收。

回退时关闭新助手，运行原助手即可使用 Notify；手机同时兼容两种方式。停止手机轮询及全部 BLE 后结束本轮测试；系统密码登录与原手环配对保留。
