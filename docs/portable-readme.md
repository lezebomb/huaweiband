# BandKey Windows 便携开发版

运行本目录 `BandKey.exe`，无需另装 .NET。Windows 11 x64，支持 BLE 的适配器。

当前功能：被动 BLE 扫描、滤波/滞回/延迟判断、离开后调用 Windows 正规锁屏接口、设备授权管理、签名 HTTPS 手机桥接。首次默认关闭自动锁屏和网络监听。

**当前不提供真正的 Windows 解锁。** 手环按钮及 ColorOS 后台尚需真机验证；验证通过的授权消息也不会使 Windows 自动进入桌面。请继续使用原有 PIN/密码。

1. 打开后先核对手环广播地址，关闭自动锁屏观察近/远状态并校准。
2. 确认广播稳定后，手动开启自动锁屏。默认信号消失只显示 Unknown，以避免设备静默导致误锁。
3. 手机桥接需要单独的 BandKey Android APK、华为 Wear Engine 服务授权与应用配置；在同一专用局域网中互相导入并核对公钥指纹。
4. `runtime/` 保存此用户的 DPAPI 应用私钥、配置、公钥与日志，不可随便携包分发；本交付包不含该目录。
5. 关闭窗口缩至托盘；托盘“退出”停止程序。卸载前退出，删除该文件夹。若另外配置了登录后启动，应先取消对应任务。

没有安装或注册 Credential Provider、修改 Windows 登录配置、保存 Windows 密码或 PIN。首次登录前不提供手环登录。

完整技术状态、协议、硬件步骤与登录集成限制见 `docs/`。源工程提供构建、测试和当前用户登录后启动脚本，开发版源码位于原项目目录。

手机实验另有不依赖同一 Wi-Fi 的 USB 开发通道，见 `docs/usb-transport-test.md`。真机 USB / Keystore / HTTPS / CNG 验签已通过合成账户测试，手机到 VM 的实际桌面解锁仍待验证。普通代理的认证后端不因传输通道变化而启用。

新增 `BandKey.exe --ble-bridge` 和托盘“手机 BLE 通信实验”，见 `docs/ble-phone-transport.md`。这是无需同一 Wi-Fi 的前台 BLE 通信实现，已完成生产帧/转发器、实际 TLS 和合成授权的软件测试，尚无 BLE 硬件通过证据。当前新 APK 未安装，手机仍保留上一 USB 实测版本。

另有显式 `--vm-bridge` 手机确认实验入口，仅支持已登记手机公钥的 VMware/build 26300 来宾；在物理机拒绝运行，不自动安装或注册。须使用独立 VM Phone Lab 镜像中的原生安装器，并按 `docs/vm-phone-bridge-test.md` 操作。它不改变普通代理的认证状态；本版本新手机到 VM 解锁仍待实机测试。

只读诊断：`BandKey.exe --probe --seconds 30 --output <绝对路径.json>`；这不会锁屏、配对或写入手环。
