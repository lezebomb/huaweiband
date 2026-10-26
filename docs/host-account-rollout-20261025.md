# 本机微软账户接入与VM验证：2026-10-25

## 已在真实主机完成

主机build26200、MicrosoftAccount、Hello可用，未注册实验Provider。用户在本机官方CredUI窗口自行输入当前账户密码：LogonUser交互式核验成功，返回SID与桌面账户相同；使用候选后端的同一保护封装，登录／解锁两种格式均可生成，错误码0。程序不保存密码，秘密缓冲区用后清零；原始报告不含账号、邮箱、SID或凭据，只保存在忽略的artifacts/host-account-probe/。

该结果是真实主机账户密码兼容证据，不是LogonUI桌面解锁或安装回滚验收。没有调用锁屏、读取系统保存的密码、注册Provider或修改Hello策略。实际认证由用户本地输入触发一次，不自动尝试错误密码。该次检查配套9项策略检查通过；10月26日后端补充后为11项，未重复要求用户输入密码。

VM使用用户新注册的个人微软测试账号；它需要成为VM实际登录的Windows用户，浏览器中的微软登录不替代此步骤。账号登录和密码输入由用户完成，开发端不接收密码。用户已经授权本机账户验证和后续满足条件后的安装，不重复请求相同授权。

## A：先准备同类账户，暂不安装BandKey

1. 在BandKey-Win11正常桌面停止中继，建立名为03-before-MSA的VM快照。保留当前xsy账户和旧ISO。
2. VM中按Win+I，进入“账户 → 其他用户 → 添加账户”，填写你新注册的个人微软测试账号。选择添加普通个人账户，不是“访问工作或学校”。
3. 添加完成，在该用户下点“更改账户类型”，设为管理员。安装时提升必须还是此用户；用xsy代填管理员凭据会因SID不一致被拒绝。
4. 注销xsy，从Windows登录页选择新增微软账号，完成正常登录。登录过程中可跳过OneDrive文件夹备份和浏览器同步，测试不需要云端文件。
5. 在VM按Win+R输入ms-settings:yourinfo，核对页面显示新注册账号的邮箱；只在浏览器登录不算。确认新增用户能正常进入桌面，且你知道其实际微软账户密码；Hello PIN不替代此密码。到这里先保持VM桌面，不用锁屏或操作BandKey。

仅切换到新用户不能证明手环解锁已通过。若新增账户出现问题，可返回xsy；需要完全回退时恢复事前快照。

## B：更换测试登记，只在VM操作

收到本轮BandKey-MSA-VM目录／ISO后：

1. 在新增微软用户桌面打开光盘BandKey.VmSetup.exe，选Remove VM experiment移除旧BandKey实验登记。原系统登录方式保留。若明确提示DLL占用，重启VM后再运行Remove；其他错误停止并反馈，不反复重启。
2. 运行同一安装器，选Install Microsoft-account phone test（微软账户专用测试），接受VM内的管理员提升。选择光盘Phone-VM-public.bandkeypub，与手机实验页面核对指纹。
3. 安装器展示MicrosoftAccount身份时，只在VM的本地凭据窗口输入该账户实际密码。Windows必须接受并返回同一SID才会保存机器DPAPI密文，Vault仅SYSTEM／管理员可访问。失败时停止，不尝试PIN、空密码或从聊天传密码。
4. 将光盘完整VmBridge目录复制到VM的新文件夹，再运行其中Start-VM-Bridge.cmd。读取登记公钥后无需反复导入；确认监听VM实际IPv4（原为192.168.71.130）、手机地址https://127.0.0.1:19443、固定手机模式、接近等待1秒，首次保持自动锁屏关闭。启动文字应有0.8。
5. 这是新Windows用户，DPAPI电脑身份和证书可能改变。点“导出电脑配对资料给手机”，把公开JSON文字发来，开发端负责手机导入与BLE连接。不发送密码；手机无需重装。
6. 接通后只测一次“锁VM → 手环确认 → 无手动密码回桌面”，再测一次新请求拒绝仍保持锁屏。需实际观察，HTTP交付不等于Windows解锁。

### 安装前提示账户类型不符（旧包可能显示错误13）

若提示“Sign into the Microsoft-connected Windows user…This option refuses local accounts”，本次安装在创建目录或注册前已停止。这里旧包显示的13是BandKey前置条件失败的通用错误码，不是密码验证失败。实现依据当前WTS会话用户的NetUserGetInfo(24)身份标记，并交叉核对用户SID和当前进程Token；不会用设置首页的邮箱卡片代替Windows登录身份。

设置中出现微软邮箱、Microsoft 365卡片或“停止自动登录到所有Microsoft应用”，不能单独证明Windows已经用该微软身份登录。保留xsy，从VM“设置 → 账户 → 其他用户”添加新注册的微软账户，设为管理员，再注销当前用户并从Windows登录页进入新增用户；如果该用户已存在，直接切换，不重复创建。再次安装仍不符时停止，保留完整提示，不改注册表或跳过账户校验。

依据：[添加Windows用户与应用账户是不同入口](https://support.microsoft.com/en-us/windows/security/identity-signin/manage-user-accounts-in-windows)、[Windows本地／微软登录切换](https://support.microsoft.com/en-us/accounts-billing/manage/change-from-a-local-account-to-a-microsoft-account-in-windows)。

## C：恢复／卸载

在微软用户中运行同一安装器选Remove，只移除BandKey固定实验注册及专属文件。确认移除完成，锁VM后用原系统登录方式返回桌面；必要时按明确的占用提示重启再清理。系统Provider没有被过滤或替换。

若不能正常进入VM，通过VMware快照管理器恢复03-before-MSA；不要在宿主机运行安装器、编辑登录注册表或删除系统Provider。回退快照会还原VM电脑身份；手机如已导入新用户资料，需恢复此前公开配对资料，无需清除手机数据或重新配对手环。卸载和快照恢复必须按实际结果记录。

原生认证DLL仍是协议3且本阶段未改变，原生组件合计236项及互操作检查通过；本轮安装器增加明确的微软账户测试入口，安装前拒绝本地账户，并更新提示文字；经静态分析／只读主机拒绝检查，没有取消VM限制。完整包不含APK、密码、私钥、Vault、Exchange或运行数据。安装／卸载已有进度记录、先禁用后清理和并发保护，见[部署后端](deployment-backend-20261026.md)。

## 已取得的微软账户 VM 结果

用户已进入新增微软账户的 Windows 桌面，并完成专用安装和公开配对更新。原 0.8 组合下，用户确认真实手环批准后 VM 自动回桌面、未输入密码；另一次等待 8 秒及手环拒绝后始终保持锁屏。初次失败发生在原生请求过期、没有批准交付的阶段，后续重接 BLE 后成功；不能据此将所有连接问题视为已解决。

以上是用户实际观察的 VM 微软账户结果；没有独立采集该轮 Windows ReportResult／WTS 归因，且尚未完成 C 项卸载恢复，也不是主机登录验收。0.8.1 提速试验失败后已恢复手机 0.8.0。后续候选只换用户态中继，见[0.9 修复及回滚](fixed-phone-native-recovery-0.9.md)。

## 本地检查工具

构建：运行scripts/build-host-account-probe.ps1。交互入口为artifacts/host-account-probe/BandKey.HostCredentialProbe.exe --output <报告路径>；--check仅核对当前桌面账户与进程SID，不弹密码窗口。退出即可卸载此检查工具，它没有安装系统组件。

本轮用户通过检查的EXE SHA-256为3f4a50ed3c4cf61224e9e0d38d4213e262dea99b53e823312c1d5e2b43839e9a。不要将通过的JSON当成安装授权令牌，安装器仍须自行核验系统状态。

依据：[LogonUser](https://learn.microsoft.com/en-us/windows/win32/api/winbase/nf-winbase-logonuserw)、[CredUI与凭据清理](https://learn.microsoft.com/en-us/windows/win32/api/wincred/nf-wincred-creduipromptforcredentialsw)、[受保护身份封装](https://learn.microsoft.com/en-us/windows/win32/api/wincred/nf-wincred-credpackauthenticationbufferw)。
