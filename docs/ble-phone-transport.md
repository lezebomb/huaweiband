# 手机与 Windows 的独立 BLE 传输

已实现 Android 外围服务、Windows 中央设备连接、双向字节流、Windows 设置窗口和模拟集成测试。2026-10-19 当前 APK 在 Mate60 完成合成账户 BLE 11 项及 CNG 2/2，MTU 517、两端字节计数一致。随后实际 VM 手机确认获用户确认自动回桌面，未确认／拒绝保持锁屏；助手在最后拒绝后出现一次流错误，持续连接稳定性尚未通过。详见 [分轮实测记录](mate60-validation-20261019.md)。

它与已有 USB 通道分别保留，避免要求手机和电脑连接同一 Wi-Fi。最终目标仍为真实手环按钮；此模块不访问华为手环 GATT、不解除配对，也不把手机信号当作手环距离。

## 通信与信任

手机实验页 → 本机 TCP 回环 19443 → Android BandKey BLE 外围服务 → Windows BandKey 中央设备 → VM 私网端口 19443。

传递的是原有 TLS 字节流；TLS 在手机与 VM 之间终止，Windows BLE 助手不解密它。精确证书指纹、电脑签名、独立手机实验密钥、原生挑战有效期、账户/会话和一次消费仍分别验证。广播 UUID、蓝牙地址、连接成功及传输 ACK 均不授予解锁权限。没有调用蓝牙配对/解除配对 API。

手机只广播自定义服务 UUID，不广播设备名或账户标识。Windows 只查询该服务和两个特征，不探测手环。发现相同服务的冒充者可能干扰可用性，TLS 和签名仍用于阻止其授权；此实现没有将广播识别当作可信设备登记。

## 手机到 VM 使用步骤

1. 按 [VM 手机实验教程](vm-phone-bridge-test.md)登记手机实验公钥、VM 密码保管和中继。保持系统原有登录选项及可恢复快照。宿主机不安装 Provider。
2. VM 中继监听来宾的私网 IPv4、端口 19443；宿主机必须能访问它。手机配对 JSON 的 endpoint 使用 `https://127.0.0.1:19443`，保留实际 VM 的电脑 ID、公钥和证书指纹。无需 VMware NAT 入站映射。
3. 手机已有相同 APK 时直接复用，不重复安装。在 VM 实验页导入并核对配对资料，授予附近设备中的 CONNECT / ADVERTISE 权限。点击“启动前台 BLE 传输”。授权弹窗关闭后若未启动，应再次点击该按钮。
4. Windows 托盘菜单选择“手机 BLE 通信实验”，或运行 `BandKey.exe --ble-bridge`。输入 VM 私网 IP，点击连接。确认订阅完成；窗口显示阶段、MTU、流数量和字节计数。默认最长 30 分钟，可设 1–120 分钟；关闭会取消并释放连接。
5. 手机点击启动轮询，保持本页前台。手动锁定 VM；收到经过核验的请求后点击本次确认，观察 VM 是否实际返回桌面。客户端“已交付”不等于 Windows 已解锁。
6. 补测不确认、错误证书/签名、拒绝、超过 30 秒、断开蓝牙、撤权与重新锁定。每次确认只应消费一次。最后恢复普通登录、移除实验组件或按快照回滚。

手机离开页面即关闭 BLE 服务、广播和当前 TCP 连接；返回后需手动重新启动。该版不声称 ColorOS 长期后台可用。异常流会要求重新连接，避免将残余数据解释为新会话。手机关闭蓝牙、撤回权限及 Windows 适配器移除仍须硬件补测。

命令行助手仅连接 RFC1918 私网或回环目标，端口固定为 19443：

```powershell
BandKey.exe --ble-tunnel --target <VM私网IPv4> --seconds 1800 --output <绝对路径.json>
```

可提供 `--ready <文件>` 和 `--stop-file <文件>` 做开发编排；停止文件应使用本轮独立路径。日志仅记录固定阶段、异常类型和数量，不记录 MAC、载荷、nonce、密钥或 Windows 凭据。普通代理认证后端保持不可用。

## v1 帧格式与限制

服务 UUID：`ab7c1101-5d18-4e0b-9aba-30ad640be001`；电脑写 RX `...1102...`，手机通过 TX `...1103...` 通知。TX 使用标准 CCCD。单个 GATT 中央设备和单个 TCP 流，ATT 写入须有响应；通知通过完成回调串行发送。

| 字段 | 长度 | 定义 |
|---|---:|---|
| kind | 1 | OPEN=1、READY=2、DATA=3、EOF=4、ABORT=5 |
| session | 8 | 非零随机 64 位值，小端；连接各自生成 |
| sequence | 4 | 小端，从 0 递增；小于 65536 |
| payload | 0–160 | DATA 必须非空，控制帧必须为空 |

OPEN / READY 为各方向序号 0，其后只接受 DATA / EOF / ABORT。拒绝跨会话、重复、乱序、错误长度、未知类型及结束后的任何帧。每方向最多 131072 字节，每条流最长 45 秒，电脑 TCP 建连最多 3 秒；队列最多 128 帧。最小 MTU23 可传 7 字节载荷，更大 MTU 按实际值扩展至最多 160 字节。低 MTU 下性能是否满足 30 秒授权窗口尚待实测。

Android 另用独立计时器关闭超时 Socket，避免只设置读超时却让写入永久阻塞。Windows 复用同一个 `BleStreamForwarder` 执行真实 GATT 与模拟测试，取消会中止两个方向。断线/乱序/超额失败不会延长原生挑战有效期；蓝牙 ACK 不等于应用消息已验签或系统已登录。

## 已完成的软件验证

- C# 核心 115/115，新增 9 项 BLE 格式/状态/上限测试，含 1000 次随机往返。
- Windows 47/47，新增 12 项：最小 MTU 模拟通道中双向各 60KB 与半关闭；乱序、错会话、ABORT、超额、空闲超时、取消；真实回环 TLS 下的有效签名/重放、拒绝、错误证书、错误签名与撤权。只模拟 GATT，生产转发器、Socket、TLS、密码学与文件交换实际执行，账户/会话为合成输入。
- Android 生产 Java BLE 编码 22 项（含 1000 次往返）；独立跨语言测试验证 C# → Java 1000 帧、Java → C# 1000 帧、完整重新编码一致性。Java 互通报告 1022 项，C# 互通报告 1001 项，避免将这些向量计为独立硬件测试。
- Windows Release / 自包含发布成功，零编译错误/警告；Android APK 构建、签名检查、Lint 零错误。窗口已离线渲染检查。没有在本阶段连接手机或操作 VM。

独立跨语言命令：`./scripts/test-ble-interop.ps1 -JavaHome '<JDK目录>'`。先运行常规构建；测试产物仅在 `artifacts/`，不进入 Git。

`scripts/test-phone-ble.ps1 -AdbPath '<platform-tools目录>\adb.exe'` 与独立 Android BLE instrumentation 已完成 Mate60 实测。脚本先校验 APK 哈希，相同版本跳过安装；连续测试默认保留测试 APK，显式 `-RemoveTestPackage` 才卸载测试组件。可添加 `-GrantNearbyPermissions` 授予本应用 CONNECT / ADVERTISE，否则先在页面授予。不要更新系统安装器。USB 只传公钥、测试配置和控制标记；拒绝已有相关 ADB 数据映射，不创建任何反向映射。GATT 就绪后才生成新挑战，测试限制为固定虚构 SID / 会话，检查双向 BLE 字节、签名交付/重放、明确的错误证书原因，并由 CNG 独立核验签名。退出清理自己的进程；这仍不是 VM 或真实手环解锁测试。

一加上一 USB 阶段的报告继续作为历史证据保留。当前 APK 另有 Mate60 的 USB 36 项及 BLE 11 项报告；发布清单按 APK 哈希和两端/TLS/CNG 交叉结果登记。14 项报告变异检查与 5 项 TLS 错误分类检查属于软件测试。

随后实际 VM 轮次中，工具观察真实 GATT 就绪及手机签名交付，用户确认自动回桌面；再锁后等待 8 秒再拒绝，用户确认始终锁屏。BLE 数据不经 USB 映射，USB 仍用于控制，未做拔线验收。该轮主机记录 MTU 517、完成 56 条流、发送 33742／接收 70293 有效载荷字节，但最后报 `stream-failed / IOException`（先于人工停止），不能作为正常结束或两端精确计数一致的证据；根因尚未确定。保留失败并要求重新连接，不放宽跨会话或一次性授权检查。后续应先排查流中断，再测一加 BLE、长期后台／恢复、真实手环点击和距离。

官方 API 依据：[Android GATT server](https://developer.android.com/reference/android/bluetooth/BluetoothGattServer)、[BLE 广播](https://developer.android.com/reference/android/bluetooth/le/BluetoothLeAdvertiser)、[Windows GATT MTU](https://learn.microsoft.com/en-us/uwp/api/windows.devices.bluetooth.genericattributeprofile.gattsession.maxpdusize?view=winrt-26100)。
