# VM 手机中继协议与安全边界

此通道在显式 `--vm-bridge` 下启用，普通托盘代理没有 v2 路由。安装器及中继均限制当前 VMware Windows 11 build 26300、本地交互会话与已登记的目标 SID；正式代理继续使用 `UnavailableAuthenticationBackend`。

## 密钥和身份绑定

手机实验 Keystore 别名 `bandkey-vm-lab-identity-v1` 与现有 `bandkey-identity-v1` 分离。公钥采用 P-256：网络身份是 SPKI 的 SHA-256，安装器核对的是 **72 字节 CNG 公钥 blob 的 SHA-256**。这两个指纹用途不同，不应混淆。原生 HKLM 配置固定后者的公钥及 `SignerKind=1`；`0` 是原来的 VM 本地模拟密钥。中途改变类型、SID、公钥或启用状态会使已发请求失效。

手机模式不产生本地模拟私钥。手机密钥不要求系统每次弹出生物识别；本阶段靠前台明确按钮表示测试确认，不能把此密钥当成已证明的真人手环来源。控制应用进程/Android 调试权限或管理员/系统的攻击者不在本测试来源的保护范围内。

电脑身份与 TLS 证书由当前 VM 用户 DPAPI 保存，手机手工导入电脑 ID、公钥、TLS 叶证书固定指纹和 HTTPS 地址。传输没有 CA/域名回退、重定向、网络登记或网络更换密钥入口。TLS 证书还需在有效期内。导出的 JSON 必须含 `protocol=bandkey-vm-phone-v1`、`testOnly=true`。

## 路由和签名

JSON 使用 camelCase，正文上限 8192 字节；当前每来源 IP 120 次/分钟、最多 8 个连接。数字转十进制，正文 UTF-8，各字段以 LF 分隔，不含末尾 LF；字段拒绝空值及 CR/LF。

| 接口 | 请求 | 响应 |
|---|---|---|
| POST `/v2/windows/poll` | pcId, deviceId, nonce, timestamp, signature | 200 电脑签名提示，204 无请求，401 身份/重放拒绝，503 交换不可用 |
| POST `/v2/windows/decision` | pcId, deviceId, challenge, approved, signature | 200 已验证交付/明确拒绝，403 无效/过期/上下文改变，503 文件交付不可用 |

轮询签名正文：`bandkey.windows-grant.poll.v1`、pcId、deviceId、32 字节随机 nonce 的 Base64、Unix 秒 timestamp。P-256/SHA-256 DER 签名，时间容差 ±30 秒，nonce 防重放保留 60 秒、容量 4096；容量满时拒绝，不驱逐仍有效项。旧 v1 签名域不能进入 v2。

提示字段 pcId/deviceId/challenge/signature；challenge 是原生 **436 字节正文**的 Base64。电脑签署 `bandkey.windows-grant.prompt.v1`、pcId、deviceId、challenge，DER 格式。手机核验身份、签名和原生正文时间，再显示用户按钮。

批准 signature 是对原生 436 字节的 **固定 64 字节 P1363 r||s** 签名，Base64 编码；Android Keystore 返回 DER，使用严格转换器生成 P1363。不要对 Base64 字符串签名，不得改写 SID/会话/nonce/时间。中继写原正文 + 签名，共 500 字节。原生协议见 [Windows grant](windows-grant-wire.md)。

拒绝 approved=false：手机 DER 签署 `bandkey.windows-grant.deny.v1`、pcId、deviceId、challenge。无签名的拒绝不能消费请求。拒绝不会写批准文件，本轮中继提示被消费。

返回 `authorized` 代表设备证明通过，`submitted` 代表交付原生公开文件成功，**`windowsUnlocked` 始终 false**。本接口没有 Windows 结果确认语义；必须另外观察 Windows 会话/实际桌面，不能从 HTTP 200 推断解锁。所有结果 `testOnly=true`。

## 数据流和替换点

1. 原生 Provider 在内存生成并持有账户/会话/nonce/30 秒请求，向公开 Exchange 写 `.request`。
2. `IWindowsGrantExchange` 只提供读当前公开请求和交付批准。Windows 文件实现只选当前 SID/会话、有效时间和 canonical 文件名，限制扫描数量/正文长度；写临时文件后再次核对原请求，原子移动，不覆盖现有批准。
3. `WindowsGrantRelay` 验证已登记测试设备的签名轮询，签署提示，记录原始正文与单调时间。手机前台 `PromptGate` 防过期、停止、重复按钮和旧生命周期回调；离开页面停止工作，排队任务再次检查状态。
4. 中继验证手机对原生正文的签名，核对请求仍相同、墙钟/单调时钟及配置，消费本轮提示再交付。写入失败不自动重新提交。撤销及墙钟回退使中继实例永久失效。
5. SYSTEM 原生 Provider 独立验证内存中的完整正文、公钥、账户、会话、双时钟、锁屏与配置，原子消费后才读取受限 Vault，使用 Windows 官方凭据序列化路径交给 LogonUI。中继不读取 Vault。

BLE、Gadgetbridge 或 Wear Engine 的未来适配器可复用网络签名/文件交换层，但必须先验证真实来源、明确用户确认和可靠接近状态，显式引入正式策略；不能把 `testOnly` 标记改掉就升级信任。当前页面只模拟按钮，刻意不把轮询成功或蓝牙连接当作接近证据。

## 失败和限制

无效签名、重放、请求被替换、过期、撤销、配置变化、未知身份、TLS 指纹错误均拒绝。网络/交换不可用时保留系统密码登录；不会注入键盘密码或静默重复认证。中继消费状态驻内存，重启后仍由原生一次性请求/消费保证最终边界。

公开请求目录目标用户可写，拒绝大小/文件名/重解析目录异常并限制数量；恶意同用户可以删除或阻塞请求造成不可用，但没有手机私钥不能凭文件写入绕过原生验签。原生 Provider 是认证边界，公开文件交换不是特权密码 IPC。

停止页面/中继只能取消尚未提交的工作；已经交付或正在发送的签名无法保证撤回。原生过期/一次性限制仍在；彻底撤权使用安装器禁用/移除登记。VM 本地账户密码仍受系统认证，改密需重新登记。该路线尚未覆盖 MSA/Hello-only、冷启动、睡眠/快速切换/RDP、正式部署与硬件按钮。

## 固定手机0.8接近调度

固定手机状态仍按原协议独立签名、限时并单调递增。状态变化立即上报，正常心跳3秒；SDK只有已选唯一设备的公开连接状态可作为证据，缺失／错误为未知。SDK连接确认后的一次后续查询缩至1秒，不改变签名、租期或挑战期限。

仅当先前明确解锁、接近已为Near且旧状态报告仍新鲜时，锁屏可保留物理接近，避免重复稳定等待；会话边界仍清空全部授权轮次。过期旧证据不能因刚到达的新报告而获得继承资格；解锁、未知会话、Far、撤销与租期结束按原规则重置或禁止提交。保留Near不生成批准，新的NEW刷新与WIN挑战仍须分别验签，只有本次有效真实手环点击才可签署批准。默认稳定接近1秒，可配置1～10秒，持续远离锁屏仍15秒。
