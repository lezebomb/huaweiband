# 2026-10-18 审核等待期开发记录

Wear Engine 申请状态为用户反馈的“审核中”。本阶段不重复申请、不发送技术支持工单、不更改手环配对。

## 阶段一：广播观测与自动锁屏控制

已实现并测试：

- Core 中的 `IDeviceObservationSource`、`DeviceObservation` 统一描述观测结果，包含传输来源、地址及类型、RSSI、时间戳、广播类型、连接/扫描标志和有界数据段。
- Windows 官方被动扫描器实现该接口；`--probe --jsonl <路径>` 按收到的每条广播保存 JSONL，最多 50,000 条 / 16 MiB，原始记录仅留本地。
- `ObservationTarget` 同时匹配地址与已选地址类型；不按名称、服务 UUID 或相似负载猜测随机地址对应关系，匿名广播不作身份目标。
- `AutoLockController` 复用原有滤波状态机，使用可替换的交互会话接口。Session 0、未知会话、已锁会话和关闭功能时不调用锁屏；API 请求成功后仍等待 WTS 确认。
- 默认信号失效后取消就绪，弱信号重连不沿用旧的 Near；睡眠、扫描停止和手动解锁后需要新的稳定 Near。显式启用“信号消失后也锁屏”仍保留原有超时策略。

本阶段构建 0 错误、0 警告，核心 59/59、Windows 集成 11/11 通过。自动锁屏相关测试使用假的会话接口，不会锁定主力电脑，不能作为手环走远锁屏证据。

实际 25 秒 Windows 被动扫描正常结束，记录 17 个地址、71 条广播，包含连接型、非连接型和可扫描型广播；该窗口未识别到目标手环。原始数据在本地 `artifacts/hardware-20261018/`，不进入 Git。短时未观察到不能证明手机连接期间永远无法发现。

## Windows 身份认证环境

已有 Visual Studio C++ 生成工具、Windows SDK 和 VMware Workstation。已登记的虚拟机只有 Ubuntu；没有发现可直接用于登录组件验证的 Windows 虚拟机。真实账户来源包含 MicrosoftAccount，密码免输/Hello-only 设备策略值为 2；不能假定现有账户密码可直接作为自定义 Provider 的认证材料。

没有安装或注册 Credential Provider，没有修改登录注册表、禁用系统 Provider 或收集账户密码/PIN。
