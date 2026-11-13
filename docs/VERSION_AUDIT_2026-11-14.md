# 版本管理审计（2026-11-14）

范围：soda-rx/Bili_Play、soda-rx/Bili_Play_Android 的本地源码、远端标签对象和公开 GitHub Release 元数据。本次没有重写历史、修改已发布版本、创建 Release、上传或安装安装包。

## 当前状态

| 项目 | 当前源码 | 公开 Tag | 公开 Release | 版本来源 |
| --- | --- | --- | --- | --- |
| Windows | 1.0.1 | 9 个，全部附注标签 | 8 个 | BiliPlay.csproj Version=1.0.1；Package.appxmanifest Identity.Version=1.0.1.0 |
| Android | 0.2.33 | 1 个，附注标签 | 1 个 | version.properties versionName=0.2.33、versionCode=35；Gradle 读取，界面用 BuildConfig.VERSION_NAME |

检查开始时，两端 main 均与 origin/main 一致、工作区干净。Windows HEAD 为 7b62614，Android HEAD 为 aaf86d1；Android 后者是初始发行后的 README 更新，不代表新应用版本。

逐标签读取源码：Windows 所有标签均对应同名三段产品版本及四段 .0 包版本（最早标签的中文后缀除外）；Android 0.2.33 对应 versionName=0.2.33、versionCode=35。未发现标签所指源码内部版本错配。未检查或运行远端安装包二进制，不能由此推断附件内容、实际签名或升级兼容性。

## Windows 历史标签和 Release

| Tag | Tag 注释 | Release 标题 | 附件 |
| --- | --- | --- | --- |
| 0.8.1初始版本 | 0.8.1 初始版本 | Bili Play v0.8.1 | Bili.Play.zip |
| 0.8.4 | Bili Play v0.8.4 | Bili Play v0.8.4 | Bili.Play.zip |
| 0.9.1 | Bili Play v0.9.1 | 未创建 | 无 |
| 0.9.3 | Bili Play v0.9.3 | Bili Play v0.9.3 | Bili_Play_0.9.3.zip |
| 0.9.5 | Bili Play v0.9.5 | Bili Play v0.9.5 | Bili_Play_0.9.5.zip |
| 0.9.7 | Bili Play v0.9.7 | Bili Play v0.9.7 | Bili_Play_0.9.7.zip |
| 0.9.9 | Bili Play v0.9.9 | Bili Play v0.9.9 | Bili_Play_0.9.9.zip |
| 1.0.0 | Bili Play v1.0.0 | Bili Play v1.0.0 | Bili_Play_1.0.0.zip |
| 1.0.1 | Bili Play v1.0.1 | Bili Play v1.0.1 | Bili_Play_1.0.1.zip |

符合：全部标签为附注类型；最早版本以外的标签名称与注释正确；Release 标题中的数字版本与源码一致；0.9.3 起附件名符合新规范；所有正式 Release 均未勾选 Pre-release。

遗留：
- 0.8.1初始版本 不是纯 SemVer，注释缺少产品名和 v。
- 0.8.1、0.8.4 的 Bili.Play.zip 缺少完整版本号。
- 0.8.1 的初始说明和 0.9.5 的身份迁移说明没有三类分类；其他已使用新增/修复/改进（加粗样式，未来统一 Markdown 标题）。0.9.5 的重装/备份提示有实际价值，应保留。
- 0.9.9 用 PATCH 承载个人空间、私聊等新能力。虽发生在早期 0.x 阶段，未来按本次约定，新功能应增加 MINOR，不延续历史做法。
- 1.0.0 将启动/切歌卡死条目也标为“#1 by @QAQTam”。公开 Issue #1 和回复支持 WebP 兼容性、PowerShell 编码贡献，未支持该音频卡死条目的归因。建议经确认后仅校正署名范围，不删除其已有图片/编码贡献。
- 1.0.1 的系统媒体封面修复和“本地 JPEG 缓存加载”改进描述同一处理链路，建议后续日志合并用户结果与贡献署名，避免同一问题跨分类重复解释。
- 0.9.1 只有 Tag，没有 Release，这本身不构成错误；无需补发，也不因本次审计补造 0.9.10 等历史标签。

## Android 历史标签和 Release

| Tag | Tag 注释 | Release 标题 | 附件 |
| --- | --- | --- | --- |
| 0.2.33 | Bili Play v0.2.33 | Bili Play Android v0.2.33 | Bili_Play_Android_0.2.33.APK |

符合：纯 SemVer 附注标签；Release 标题正确；versionName 与标签一致；正式版未勾选 Pre-release。

遗留：
- 注释缺少 Android。
- 附件扩展名为大写 .APK，本次新规则要求小写 .apk。
- 日志只有初始版本一句话，没有按实际功能分类。无需为分类编造修复项。
- 现有 Build-Android.ps1 只生成 development 验收包；没有完整的正式签名发布流水线。不能仅重命名就认定为正式包。未核验既有远端 APK 的包身份或签名，下一次正式发布前须先核对，以免改身份或密钥破坏用户覆盖升级。

## 本次落地的未来规范

两个仓库新增相同的只读 check_release.py、合成回归测试、Release policy Actions、平台配置、RELEASING.md 和日志模板。

- source 检查：SemVer 格式、Windows 双版本一致性、Android versionCode 对已有标签递增且不改写旧值。
- tag 检查：附注类型、平台注释、源码版本、当前提交、工作区干净。
- release 检查：标题、预发布勾选、非空分类、占位/完全重复条目、唯一附件名称。
- 本地附件检查：Windows ZIP 内仅 MSIX/公开证书，读取清单版本和正式身份；Android 经可信 SDK aapt 读取真实包身份、versionName、versionCode，拒绝开发验收身份。
- 现有构建入口增加版本预检；Android 未来交付扩展名改小写。没有改变版本、应用身份、签名和业务代码。
- Actions 仅 contents: read，无上传和发布步骤。审计完成后经用户授权提交、推送这些维护改动；远端启用及运行结果以 GitHub Actions 为准。
- published/edited 是发布后的检查，不能阻止网页直接发布。发布前应先用本地附件与日志做预检；是否配置分支保护仍由仓库管理员决定。

限制：元数据校验不能证明日志真实、全部贡献正确、附件签名可信或软件没有 Bug。Windows 密码学验证仍在原 Build-Installer.ps1，Android 仍需可信 SDK 签名/隐私验证。Windows 预发布安装链路尚未设计：现有 UI 只显示前三段，MSIX 要求数字四段；工具阻止直接把预发布当正式包打出，后续需单独规划渠道与升级，不在本任务擅改应用。

## 历史处理与后续递增

旧 Tag、对象、Release、附件与已发布版本全部保留。在本审计中记录例外，从下一次发行按规范执行；如要编辑旧 Release 正文或署名，先取得用户确认，不覆盖旧附件或移动标签。

Windows 下一次仅修复可用 1.0.2，新增兼容功能用 1.1.0，真正不兼容变更用 2.0.0。Android 仅修复可用 0.2.34，新增兼容功能用 0.3.0，稳定接口和升级策略确定后再考虑 1.0.0；首次稳定版本不代表两端必须同号。Android versionCode 每次发行继续单调递增，包括预发布和正式版。

依据：[Windows Releases](https://github.com/soda-rx/Bili_Play/releases)、[Android Releases](https://github.com/soda-rx/Bili_Play_Android/releases)、[Issue #1](https://github.com/soda-rx/Bili_Play/issues/1)、[SemVer](https://semver.org/lang/zh-CN/)、[Android 版本管理](https://developer.android.com/studio/publish/versioning)、[Microsoft 包版本要求](https://learn.microsoft.com/en-us/windows/apps/publish/publish-your-app/msix/app-package-requirements)。
