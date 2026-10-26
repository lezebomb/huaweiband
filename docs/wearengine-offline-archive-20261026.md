# Wear Engine离线归档：2026-10-26

当前基线：com.huawei.hms:wearengine:5.0.3.304，依赖com.huawei.hmf:tasks:1.5.2.206。归档用于保存现有开发材料和恢复原版本，不产生额外服务权限或永久授权。

## 保存内容

- 原始AAR／JAR及POM，按Maven目录保存，并记录各文件SHA-256。
- 本阶段BandKey已暂存源码树快照、当前0.8.0签名APK。
- 华为公开WearEngine示例源码与其原始Apache-2.0许可，固定提交81a98055a479d9f9740374c0a26f27d56be19706。该许可属于示例项目，不据此给SDK或华为服务重新授权。
- 本接口笔记、来源索引、离线构建说明、独立完整性校验脚本。

没有保存手机／健康记录、密码、授权令牌、私钥或签名keystore；也没有打包运动健康、HMS Core或手环固件。原始APK研究副本不属于归档。

## BandKey当前使用的接口

1. HiWear.getAuthClient(context).requestPermission：请求DEVICE_MANAGER、NOTIFY；取消或失败时停止依赖该授权的流程。
2. HiWear.getDeviceClient(context).getCommonDevice：取得运动健康提供的设备对象。BandKey再按已选UUID筛选，核对isConnected和getNotifyCapability；获取不到设备或查询失败归为未知。
3. HiWear.getNotifyClient(context).notify：发送双按钮模板，包含当前请求显示编号、说明和期限。
4. Notification.Builder、NotificationTemplate、NotificationConstants：构造模板及按钮内容。
5. Action.onResult／onError：处理本次按钮反馈或错误。当前SDK中2、3对应第一／第二按钮；BandKey还核对票据、期限、所选设备和生命周期，通知发送成功不签署解锁批准。

这些是本工程已有集成的说明，不是华为SDK的替代实现。模板通知不需要自行开发手环App；P2P等其他能力可能需要穿戴端应用配合。未申请人体／运动传感器能力。

## 离线构建

android/settings.gradle新增可选Gradle属性bandKeyWearEngineMirror。指定归档maven目录后，先核对四个文件的固定SHA-256，再将wearengine和tasks两个模块独占解析到该目录。未指定时仍用原官方仓库。

在源码副本的android目录运行：

    ./gradlew.bat --offline '-PbandKeyWearEngineMirror=归档完整路径/maven' assembleDebug

还需JDK21、Android SDK API36／build-tools36.0.0，以及Gradle9.3.1、AGP9.1.0和其余构建缓存。归档不包含整套JDK／Android SDK／Gradle缓存，不能保证全新电脑零安装构建。本机验证若成功，只证明现有工具链在禁用Gradle网络解析时能使用归档SDK完成构建。

源码快照由暂存Git树生成，包含归档工具自身；base commit和source tree见manifest。普通归档没有签名私钥，未来修改后继续使用原应用身份仍需保管原签名keystore，或按华为流程更新指纹。已保存的原签名APK可原样留作恢复使用。

## 完整性与备份

解压归档后用PowerShell执行：

    ./tools/verify-wearengine-archive.ps1 -Path 解压目录

校验文件数量、相对路径、大小及SHA-256，拒绝重解析点和目录越界。ZIP旁另有.sha256文件；请与ZIP一起复制到你控制的另一块硬盘或其他备份位置。校验和用于发现变化，不是华为的数字签名或永久使用许可。

## 持续可用性的边界

Wear Engine SDK调用的是运动健康及手环系统内的服务。服务权限、华为账号／用户授权、包名／APP ID／签名匹配、运动健康和固件兼容性仍然需要有效。把SDK保存下来不能绕过授权刷新，也不能将闭源服务或固件一并冻结。BandKey的30分钟实验运行限制是本程序自身限制，和SDK文件保存年限无关。

此归档没有修改华为鉴权、提取令牌、复制设备配对密钥或解除配对。

官方来源：
- https://developer.huawei.com/consumer/cn/doc/connectivity-guides/service-introduction-0000000000018585
- https://developer.huawei.com/consumer/cn/doc/connectivity-guides/integrating-phone-sdk-0000001051137958
- https://developer.huawei.com/consumer/cn/doc/connectivity-References/permission-0000001059433005
- https://developer.huawei.com/consumer/cn/doc/connectivity-guides/faq-0000001050818031
- https://developer.huawei.com/consumer/cn/devservice/ServiceAgreement/
- https://github.com/huaweicodelabs/WearEngine
- https://docs.gradle.org/9.3.1/userguide/filtering_repository_content.html

SDK原始下载来源为项目配置的https://developer.huawei.com/repo/。本地备份保留原始文件及现有协议适用性；不把归档上传公共仓库。
