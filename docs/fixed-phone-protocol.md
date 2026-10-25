# 固定手机状态与原生新请求协议

复用手机P-256 Keystore、电脑签名、证书固定的HTTPS及BLE字节流；只在VM中继显式开启固定模式时开放。手机报告所选设备状态，不是手环硬件签名或精确测距证明。

- POST /v2/windows/fixed/context：手机签名PollRequest，用途 bandkey.fixed-phone.context-poll.v1。PC签名用途 bandkey.fixed-phone.context.v1，返回PcId、DeviceId、32字节ContextNonce、ExpiresAt、NextSequence。每次运行独立上下文，最多30分钟；请求nonce不可重放。
- POST /v2/windows/fixed/state：PcId、DeviceId、ContextNonce、Sequence、ObservedAtMilliseconds、BandHash、Connected、Signature。用途 bandkey.fixed-phone.state.v1。布尔规范 true／false／unknown，JSON未知为null；UUID的UTF-8 SHA-256固定本轮目标，改选撤销。递增序号≤2^53-1；拒绝超过10秒／超前3秒的样本、错身份与无效签名。字符串沿用ProtocolText的UTF-8字节长度分隔。
- Unknown、来源不健康、过期会清除接近状态，不能当成走远。自动锁屏默认关闭且限VM交互会话。

## 刷新与批准分离

Near且VM锁定时建立436字节小端刷新请求：16字节 BANDKEY-NEW-V1\0\0、32位会话ID、64位签发／过期FILETIME毫秒、32字节nonce、184个UTF-16字符SID槽。有效期严格10秒，校验SID、会话、填充和时钟。

PC用 bandkey.fixed-phone.renewal.v1 签名PcId、DeviceId、ContextNonce、Body，附状态响应。手机验电脑签名与上下文后自动签NEW用途，POST /v2/windows/fixed/renew回传64字节P1363签名。该路径不调用原生WIN用途批准函数。

中继原子创建 Exchange/request-renewal.bin（500字节，不覆盖）。SYSTEM Provider计时器独立验手机公钥、SID、锁定会话、期限、单调时间和一次性nonce，再取消旧凭据并请求LogonUI重枚举。它不读Vault、不序列化Windows凭据，新枚举生成新的随机WIN挑战。

中继观察到同SID／会话、签发不早于刷新、不同于旧body的新WIN挑战后，才开放普通poll／decision。解锁仍需真实手环当前点击和原有30秒一次性授权。旧批准、旧通知或NEW用途签名不能批准新挑战。

单次返回不无限刷新；拒绝／过期后需离开返回或显式重试。模式、登记、账户、会话改变或睡眠恢复取消旧上下文。没有声称完成硬件密钥证明、精确测距或正式主机认证。