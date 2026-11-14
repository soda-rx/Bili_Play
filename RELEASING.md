# 版本与发布

Windows 与 Android 使用同一套规则，独立递增版本，不要求两端版本号相同。本文件不授权自动提交、推送、创建 Release 或上传安装包。

## 命名

| 项目 | Tag / 产品版本 | Annotated Tag 注释 / Release 标题 | 唯一发布附件 |
| --- | --- | --- | --- |
| Windows | 1.0.2 | Bili Play v1.0.2 | Bili_Play_1.0.2.zip |
| Android | 0.2.34 | Bili Play Android v0.2.34 | Bili_Play_Android_0.2.34.apk |

上表是格式示例，不代表这些版本已经发布。release-policy.json 保存平台名称和附件前缀；产品版本仍来自各平台原有版本文件，不另设第二个版本号。

- 正式版本为 MAJOR.MINOR.PATCH，不带 v、不补前导零，不含中文或产品名。
- 不兼容变更增加 MAJOR；兼容的新功能增加 MINOR、PATCH 归零；兼容修复和细节优化增加 PATCH。兼容范围包括安装升级、用户配置和保存的数据。
- 预发布统一为 X.Y.Z-alpha.N、X.Y.Z-beta.N、X.Y.Z-rc.N，N 从 1 递增。注释、标题、附件保留完整后缀，GitHub 勾选 Pre-release；正式版不勾选。
- 必须使用 git tag -a 创建附注标签，-m 内容为对应平台的 Release 标题，指向已经提交、验证且用于构建的提交。
- 不更换已发布版本的内容、不移动标签、不强推、不重新分配版本号。软件修正使用新版本；历史命名问题留档，不据此重打旧标签。
- 不为了格式一致改变包身份或签名密钥，避免破坏覆盖升级。提交代码和创建标签不等于发布 GitHub Release。

## 更新日志

使用中文 Markdown 标题“## 新增”“## 修复”“## 改进”，空分类删除。历史加粗分类仍可读取，新版本使用标准标题。参见 [模板](docs/RELEASE_NOTES_TEMPLATE.md)。

只写相对前一发布版本实际发生的变化，用用户能理解的描述，不重复、不夸大、不把计划写成成果。对应条目注明有依据的 @用户名 和 #PR编号，区分问题报告、方案建议与代码贡献。重要迁移提示可作为分类前的普通段落。

工具检查命名、结构、完全重复的条目和占位文字；无法代替人工核实真实性、贡献范围或近义重复。不能直接复制模板占位条目发布。

## 发布检查

需要 Python 3.10+ 和 Git。先获取完整标签历史，再执行只读检查：

~~~powershell
git fetch origin --tags
python -B -m unittest discover -s tools/tests -p test_release.py -v
python -B tools/check_release.py
python -B tools/check_release.py --notes <工程外的更新日志.md>
~~~

应用测试和隐私检查按项目既有构建流程执行，测试结束运行本项目清理脚本。私钥、真实凭据、用户数据始终留在工程与发布附件之外。

用户授权提交和打标签后，确认检出标签提交、工作区干净，再执行 --tag <版本>。不要在此阶段修改版本文件或源码。附件检查须同时指定 --tag、--notes、--asset；发布标题和附件名称使用工具输出的 Required release title / Required asset。通过检查之后，实际发布与上传仍须用户授权。

可准备工程外 JSON 离线核对 Release 表单：字段为 GitHub 的 tag_name、name、prerelease、body、assets（含 name）。执行 python -B tools/check_release.py --release-json <文件>，不会创建或修改远端 Release。

## GitHub Actions

Release policy 工作流对 main 推送、PR 校验当前版本来源；对新 Tag 保留严格的附注、版本及提交检查。Release 事件和手动历史审计均从默认分支取得当前工具，只读读取目标标签中的版本文件，不检出或执行旧版本代码。Release 事件只核对标题、预发布标记和附件名称，不检查或改写已有更新说明、贡献署名。手动运行可指定任一历史标签；留空审计全部标签。

权限仅 contents: read，无签名密钥，不安装软件、不上传附件、不创建/删除/覆盖 Release。CI 不下载或执行远端二进制，不能据此声称远端附件签名、隐私或内部版本已验证。

在 GitHub 网页直接发布时，published/edited 检查在操作之后发生，并非事前阻断，务必先做本地检查。可由管理员把 Release policy 设为分支必需检查；本次未修改保护设置。历史审计不依赖旧标签中是否存在新工具，不为补工具重写旧提交。历史审计不充当正式发布验收，不替代本地严格的 --tag / --asset 检查。

## Windows 历史审计入口

以下入口在当前 Windows 仓库可用；本次未改动 Android 工程。

~~~powershell
# 使用当前工具读取所有标签的版本文件，不修改工作区或标签。
python -B tools/check_release.py --audit-history

# 只检查一个历史版本，工作区无需切回旧提交。
python -B tools/check_release.py --audit-history --tag 0.9.5

# 可选：核对导出的 Release 元数据，不检查或更改原有日志和署名。
python -B tools/check_release.py --audit-history --release-json <工程外的Release元数据.json>
~~~

历史模式不会运行旧标签中的脚本，不接受本地安装包验收参数；新版本正式验收仍使用原有严格命令，要求检出对应标签提交且工作区干净。未关联 Release 的标签也可以审计，不因此自动补发 Release。

## Windows 版本来源与打包

- 产品版本：BiliPlay.csproj 的 Version；程序集版本由 .NET SDK 生成。
- 升级版本：Package.appxmanifest 的 Identity.Version。正式版 X.Y.Z 对应 X.Y.Z.0，两处必须同时更新。
- 关于页读取程序集版本前三段，当前不能显示 alpha/beta/rc 后缀。
- Build-Installer.ps1 原先只读清单决定交付名称；现在先检查项目和清单一致，再进入原有构建、签名、资源、文件块哈希、公开证书匹配及隐私验证流程，不安装或信任证书。
- 桌面交付目录仍为 Bili_Play_<版本>，仅含 BiliPlay_<版本>_x64.msix 与 BiliPlay.cer。GitHub 附件是这两个文件组成的 ZIP；不额外附私钥、说明、安装脚本或协议副本。源码和应用内许可/协议保留。

~~~powershell
# 示例，确认目录正确、目标 ZIP 不存在后执行；不覆盖旧附件。
$releaseVersion = '1.0.2'
$releaseDesktop = [Environment]::GetFolderPath('Desktop')
$releaseFolder = Join-Path $releaseDesktop ("Bili_Play_" + $releaseVersion)
$releaseZip = Join-Path $releaseDesktop ("Bili_Play_" + $releaseVersion + ".zip")
if (Test-Path -LiteralPath $releaseZip) { throw '目标已存在，请勿覆盖历史附件。' }
Compress-Archive -LiteralPath (Join-Path $releaseFolder ("BiliPlay_" + $releaseVersion + "_x64.msix")), (Join-Path $releaseFolder 'BiliPlay.cer') -DestinationPath $releaseZip
python -B tools/check_release.py --tag $releaseVersion --notes <工程外的更新日志.md> --asset $releaseZip
~~~

ZIP 校验读取包内清单核对版本、正式身份和 x64 架构，拒绝额外文件；不代替 Build-Installer.ps1 的密码学签名和证书匹配验证。

### Windows 预发布边界

元数据工具支持规范预发布标签、标题和 Pre-release 状态，但现有 MSIX/应用版本显示尚未形成可用的预发布发行链路；打包入口与附件检查阻止误发。

不能把 -beta.1 塞进 MSIX 数字版本，也不能擅自以更高第四段表示 beta，再以同一身份发布更低的正式 .0。规划预览渠道、独立身份或合法递增映射并完成升级测试后再启用。这需要单独的应用改动，本任务不修改业务代码或现有身份。

参考：[SemVer](https://semver.org/lang/zh-CN/)、[Microsoft 包版本要求](https://learn.microsoft.com/en-us/windows/apps/publish/publish-your-app/msix/app-package-requirements)。
