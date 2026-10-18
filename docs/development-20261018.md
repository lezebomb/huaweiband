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

## 阶段二：来源接口、认证调度与隔离测试

- `IUserConfirmationSource` 和设备来源策略将 Wear Engine、BLE、Gadgetbridge、测试客户端接到同一签名授权流程；后两种真实硬件协议适配仍未实现。
- `AuthenticationCoordinator` 把一次性授权与 Windows 认证、系统会话确认分开，处理撤权、策略替换、会话更换、超时、取消和异常。正式代理仍使用不可用后端；测试来源不得进入真实后端。
- `--test-flow` 使用独立临时身份和真实固定证书回环 HTTPS，模拟靠近、锁屏和按钮，覆盖投递不授权、来源错误、有效签名、重放与远程配置拒绝，8/8 通过。报告明确声明未测试真实手环和 Windows 登录。
- 微软 V2 样例固定提交编译；普通进程直接实例化、合成用户和内存随机凭据序列化 42/42 通过。未注册 Provider、未提交系统认证。
- Android 读取已认证 HTTPS 上的 403 拒绝结果，避免将合法拒绝误报为网络故障；构建及 Lint 完成，生命周期/重试测试 19/19。

本阶段核心测试 82/82、Windows 集成 11/11。新增测试的系统状态及后端由测试替身提供，不是实际解锁证据。首次在受限上下文启动独立测试时遇到 Windows 加密组件异常，正常用户环境运行成功；已补上命令行失败报告与退出码，受限环境重测返回失败而不弹崩溃框。

Windows 11 专用虚拟机已创建；微软官方中文 x64 Enterprise 评估镜像尚在下载，完成后需先核验官方 SHA-256。现有 Ubuntu22.04 的 VMX 哈希在创建前后保持一致。虚拟磁盘、镜像、加密材料和本机测试报告全部排除 Git。真实登录组件只在完成安装、建立基线快照后继续验证。

## 虚拟机准备阶段

官方 Windows 11 Enterprise 26H2 中文 x64 评估 ISO 下载完成，大小 8,548,628,480 字节，整份文件 SHA-256 为 `b37a06d5121eee017826c0b133250bbd21bec694c2d607c60edd29090e667a84`，与[微软公布值](https://support.microsoft.com/en-us/servicing/os/windows/docs/2026/09/verify-the-authenticity-of-a-windows-11-enterprise-evaluation-iso-file)一致。本地 `virtualmachine/iso-verification.json` 保存来源及校验时间，不进入 Git。

镜像只挂载到新建的 BandKey-Win11。该虚拟机的磁盘为自身独立的 80 GiB VMDK，没有物理磁盘或 Ubuntu 磁盘附件；正常用户上下文的 VMware CLI 确认仅该新虚拟机在运行。现有 Ubuntu22.04 配置校验仍保持原值。

用户已手动通过 EFI 光驱引导，Windows 安装程序已打开。当前 VMware 窗口缩成约 344×406，自动化放大操作未生效，已暂停输入，请用户先最大化窗口以便完整核对选项。Windows 安装、首次账户配置、Guest Tools、系统基线快照以及真实登录验证仍未完成；不能把准备好镜像视为已安装 Windows。
