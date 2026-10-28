# 两分钟确认与项目收尾

用户已反馈连续两天实际携带手机、手环返回后能及时收到通知并确认解锁。此前失败的试验仍保存在 Git 历史，不能把所有旧候选均计为实测通过。

## 最终行为

- 主机每次新建Windows授权挑战有效 **120秒**，手环通知显示本次截止时间。从请求创建开始计时，蓝牙投递消耗的时间也计入期限。
- 手机0.9.4、电脑中继、原生登录组件共同接受两分钟挑战。兼容旧30秒挑战，不能任意修改为其他时长。独立旧VM的默认挑战仍为30秒。
- 必须明确点击本次有效通知；拒绝、过期、账户／会话变更、原授权重放均不能解锁。单调时钟和墙上时钟均检查。
- 自动锁屏继续关闭，常驻、自启动、原密码／Hello、现有手机密钥及手环配对保持原设置。
- 最终版沿用已部署的0.9.3 BLE传输，不加入先前未部署的GATT会话候选；该候选可在`c6f9e7a`查看。
- 只戴手环返回：SDK报告未知状态时，由3秒查询改为1秒只读查询，可消除该阶段最多约2秒的额外等待；明确断连时本来已每秒查询。未知仍不能当作走远或批准，不改变手机／手环配对、不控制运动健康重连速度，不承诺总返回时间。

## 更新与恢复

部署：一加已保留数据更新0.9.4/code20，原签名与设备公钥一致；主机原生DLL及用户态中继已更新，自启动常驻、`autoLock:false`保持。已观察到更新后一次真实原生`Windows-result=0`，以及另一轮挑战准确在120秒到期；不能由此声称“等待60秒后的手环确认”已验收。首次该项尝试用户反馈无新通知，SDK接受发送与实际手环显示分开记录，后续结果另行补充。

原生安装器新增管理员命令`--update`，只更新同一主机、同一用户已启用的BandKey组件，不读取密码密文、不重新登记账户、不修改系统登录方式。新DLL经内嵌SHA-256核对后替换，保留`C:\Program Files\BandKeyHostPilot\provider-previous.dll`。

文件占用会拒绝更新，不强制退出LogonUI、不自动重启。正常重启后重试即可。失败时处理Windows可能已将原文件移至备份的情况；实现参考微软[ReplaceFileW错误与文件状态](https://learn.microsoft.com/en-us/windows/win32/api/winbase/nf-winbase-replacefilew)。

回滚新原生组件：以管理员身份运行`maintenance\Current\Native\BandKey.HostSetup.exe --rollback-update`。完整卸载入口为开始菜单“Uninstall BandKey”，备用入口为`C:\ProgramData\BandKeyHostPilotRecovery\BandKey.HostSetup.exe`。

## 本地保留

- 已安装电脑程序：`%LOCALAPPDATA%\Programs\BandKeyHostPilot`。
- 配对、TLS身份与必要状态：`%LOCALAPPDATA%\BandKeyHostBridge`。不删除或上传密钥、DPAPI密文。
- 受保护原生组件与账户密文：`C:\Program Files\BandKeyHostPilot`；独立恢复工具位于`C:\ProgramData\BandKeyHostPilotRecovery`。
- `D:\huawei\maintenance`：最终手机APK、原生更新工具、一次回滚备份、清理收据、私有Android签名密钥。该目录被Git忽略，签名子目录限制为当前用户、SYSTEM和管理员。
- 小体积源码、必要测试、构建脚本和技术记录保留在GitHub及当前检出。生成测试产物、旧APK、截图、下载碎片、Windows实验VM和快照无需长期占用磁盘。

手机清理仅处理BandKey旧测试应用／本项目临时文件，不处理运动健康、HMS、系统安装器或其他个人文件。不能卸载当前一加的`dev.bandkey.bridge`，否则会丢失Android Keystore身份。

## 从GitHub恢复开发

1. `git clone https://github.com/lezebomb/huaweiband`。保留所有源码、测试、脚本及文档。
2. 安装PowerShell7、Node.js、JDK21、Android API36／Build Tools36.0.0。原生开发另需Visual Studio Build Tools的C++ v143与Windows SDK26100。
3. Windows：`pwsh -File scripts/build.ps1 -Publish`。缺失项目.NET时，bootstrap按`global.json`固定版本及官方哈希恢复。
4. SDK：`pwsh -File scripts/restore-wearengine.ps1`恢复固定版本并逐件验SHA-256；Gradle也可从官方仓库解析。服务权限仍由华为管理。
5. 更新原手机前，将私有备份`maintenance\Private\debug.keystore`恢复到`.tooling\android-prefs\.android\debug.keystore`。缺少原签名可以开发新构建，但不能覆盖现有App或冒充已批准签名；不要上传私钥，不要卸载现有App绕过签名差异。
6. 手机：`pwsh -File scripts/build-android.ps1 -SdkPath <SDK路径> -WearEngineAppId 119393073 -Lint`。原生：`pwsh -File scripts/build-host-pilot.ps1`，为目标电脑/build重新绑定。

工具链可重新下载，不属于运行依赖。删除前停止本工程构建服务；项目内已登记的Visual Studio Build Tools使用正式卸载器移除，不直接删除安装目录。Ubuntu与其他项目不处理。

清理脚本默认仅预览；显式`-Apply -RemoveVm -RemoveToolchain`才删除。它核对绝对路径、重解析点、Git跟踪文件、GitHub已同步和签名备份，拒绝删除运行中的BandKey虚拟机。移除VM／快照后需要重新建立实验环境；源码与历史结果仍在仓库。
