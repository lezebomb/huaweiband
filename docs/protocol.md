# BandKey v1 授权协议

传输为局域网 HTTPS，Android 固定电脑叶证书 SHA-256，禁止跳转和明文回退。电脑持 ECDSA P-256 私钥（DPAPI CurrentUser）；手机持 Android Keystore P-256 不可导出私钥。算法为 SHA256withECDSA，ASN.1 DER 签名；公钥格式为 DER SubjectPublicKeyInfo。

`pcId` / `deviceId` 等于各自公钥 DER 的 SHA-256 小写十六进制。双方配对通过用户核对完整指纹后本地导入；不存在网络注册、网络配置、TOFU 自动信任接口。

## 线上消息

所有请求/响应是 UTF-8 JSON，字段名 lowerCamelCase，Unix 秒时间。签名不依赖 JSON 字段顺序，使用以下 UTF-8 文本，无最后换行。变量不得含 CR/LF。

`POST /v1/poll`

```text
bandkey.poll.v1
<pcId>
<deviceId>
<nonce>
<timestamp>
```

nonce 为 32 个随机字节的标准 Base64（含 padding）。签名放在 JSON 的 `signature`；时间允许±30秒。服务器验证已登记手机公钥并拒绝重复轮询 nonce。缓存 60 秒、最多4096项，满时拒绝而不驱逐仍有效项。

未锁屏或距离非 Near：204。否则返回电脑签名的 challenge：

```text
bandkey.challenge.v1
<pcId>
<deviceId>
<sessionId>
<nonce>
<issuedAt>
<expiresAt>
```

锁屏会话 ID 每次重新锁屏时更换，挑战有效期30秒。同一未过期挑战可重复读取；全局最多每60秒发出一个新挑战，以限制重复弹窗。手机核对电脑签名、双方 ID、nonce 长度、有效期；只向已选择的手环 UUID 发模板通知。

手机在开始设备查询时建立本地请求票据。查询返回、通知提交和按钮回调均检查该票据；只有第一次有效确认/拒绝能够消费它。停止服务、改选手环或重新导入电脑会取消旧任务；提交队列中的点击也须再次检查。手机同时使用 Unix 秒时间和单调时钟限制已收到请求的有效期，避免系统时间回退延长等待时间。取消不能保证撤回已发送到手环的通知，其迟到点击仍会被拒绝。电脑端签名、上下文与过期检查继续独立执行。

取消不撤回已经开始发送的网络消息。需要立即撤销手机身份时，使用 Windows 的“撤销手机”，使电脑端的授权记录与未完成挑战失效。

`POST /v1/approval` 的签名文本：

```text
bandkey.approval.v1
<Base64(UTF8(challenge签名文本))>
<challenge.signature>
<1表示确认，0表示拒绝>
wearengine-button
```

JSON 带完整 `challenge`、`approved`、`source`、`signature`。Windows 必须仍锁屏、距离仍 Near、设备仍授权、挑战与内存中完整记录完全一致且未过期，才验签并原子消费。合法拒绝同样消费挑战。离开 Near、会话解锁、撤权或重启均使旧挑战无效。

有效响应示例语义：

```json
{
  "authorized": true,
  "windowsUnlocked": false,
  "code": "authorized-authentication-backend-unavailable"
}
```

这不是 Windows 登录令牌。当前未部署认证后端，无论验签是否成功都不解锁 Windows。

## 信任与限制

- 手环按钮由华为连接回调报告，再由手机签名。签名证明“受信任手机报告了点击”，不证明手环持有本项目私钥。Android 系统、桥接 APK、运动健康和华为服务属于信任边界。手机被控制可伪造其报告。
- BLE 地址、RSSI、同一 Wi-Fi 都不是身份认证，也不能抵御蓝牙信号转发。攻击者可仿冒广播干扰自动锁屏；本版本不宣称具备距离证明或抗中继能力。
- 桥接监听默认关闭、地址默认loopback。请求体最多8192字节、最多8个并发连接、每远端IP每分钟120次；没有远端管理接口。
- 应用日志只写事件/错误代码，不写请求体、nonce、私钥、Windows密码或PIN。原始硬件扫描报告可能含附近蓝牙地址，应只在本地保存。
- DPAPI 防止其他账户直接解密应用私钥；不能抵御已控制同一用户的恶意程序。未来认证后端必须采用独立权限边界，不能直接把当前便携存储当作 SYSTEM 服务的信任配置。
- 撤销手机后旧签名立即失效。电脑或手机密钥/证书丢失需要重新核对配对；证书过期时不自动忽略证书有效期。
