# 部署与恢复后端：2026-10-26

本阶段接通共享原生部署后端与现有VM安装器。没有操作手机／VM，没有注册主机登录组件、发起密码窗口或Windows认证。正式主机安装器仍未开放，新增代码是后续部署基础，不代表主机解锁已验收。

## 完成内容

安装过程保存24字节deployment-state.bin，只有格式版本、固定阶段及错误码；阶段为目录准备、载荷校验、凭据登记、策略写入、COM登记、等待启用。最后才写Enabled=1。记录不含密码、账号、SID、手机身份、公钥或nonce，也不参与原生授权判断。

进度先写同目录临时文件，刷新缓冲区后整体替换；下次可识别遗留阶段和临时文件。拒绝重解析目录、硬链接、未知格式和非法阶段；只清理固定名称，不递归删除。[MoveFileEx](https://learn.microsoft.com/en-us/windows/win32/api/winbase/nf-winbase-movefileexw)、[FlushFileBuffers](https://learn.microsoft.com/en-us/windows/win32/api/fileapi/nf-fileapi-flushfilebuffers)。

卸载／回滚先尝试Enabled=0，再移除本组件固定的Provider、策略和COM登记。前一步失败仍尝试其余撤销，保留第一个错误；回滚只处理本次确实创建的键。撤销未确认完成时不报告成功；文件占用或未知残留保留可重试记录。系统Provider未改动。

安装／卸载共用命名互斥锁。持锁线程退出后可重新获得锁，继续按实际登记与文件状态处理；安装仍拒绝现存登记或目录，不自动恢复为启用状态。[WaitForSingleObject](https://learn.microsoft.com/en-us/windows/win32/api/synchapi/nf-synchapi-waitforsingleobject)。

只读--check扩展为bandkey.setup-preflight.v2，返回登记存在性、Enabled、进度、DLL哈希核对及recoveryRequired。日记不明、登记缺失或载荷不匹配提示检查。进度记录不能选择路径、身份或注册表目标。真实主机仍被VM保护拒绝，不读取Program Files实验目录。

## 账户后端

此前用户在主机本地输入微软账户密码，Windows接受、返回同一SID，登录／解锁保护格式可生成。这是API层真实证据，实际LogonUI解锁与恢复仍待VM验证。原始报告只在忽略目录，未上传账号信息。

本地检查工具现区分“策略拒绝、未调用Windows”与“已提交Windows后被拒绝”，并拒绝未提交认证却声称成功的返回值；取消／失败不自动重试，秘密缓冲区用后清零。当前11项检查及静态分析通过，本阶段未再次输入真实密码。

VM安装器新增Install Microsoft-account phone test入口，要求已经登录微软账户Windows用户，在安装前拒绝本地账户。当前SID绑定、手机公钥、机器DPAPI、Vault ACL和VM保护保留。用户已报告进入新微软用户桌面，该账户的BandKey解锁／卸载仍待验证。

## 验证与实际边界

原生236/236，新增15项覆盖撤销顺序、多故障清理、已删除项重试、仅处理自建项、进度整体替换、遗留临时文件、格式拒绝、硬链接、并发阻止和废弃锁恢复。文件／互斥锁使用真实Windows API，注册故障由注入函数模拟，未改系统登录注册表。

C#／CNG互操作15项、Java生产授权／刷新编码32项及反向验签、用途隔离、篡改拒绝通过；原生Release与静态分析通过。只读报告确认vmGuardAccepted=false、mutationPerformed=false、providerRegistered=false。认证DLL哈希与阶段前一致，本轮修改安装器及部署辅助代码。

这些不替代VM管理员安装、实际MSA LogonUI认证、卸载后系统登录、快照恢复或主机长期BLE验收。断电／文件系统损坏仍需恢复介质、快照及实机验证，不根据进度记录自动批准认证。

## 交付与后续

scripts/package-account-lab.ps1 -IncludeRelay组合当前完整0.8中继、协议3认证DLL、新安装器及明确核对过的手机公钥。包不含APK或模拟确认客户端，载荷逐项读回校验。最新路径在artifacts/latest-account-backend.json；构建产物、配对资料及原始报告不进Git。

暂时不用操作设备。以后按[微软账户VM教程](host-account-rollout-20261025.md)验证确认、拒绝与卸载恢复；遇到登录问题保留系统登录方式并恢复03-before-MSA。主机没有安装登录组件，无需系统回滚。