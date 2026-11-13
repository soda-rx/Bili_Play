#!/usr/bin/env python3
"""Read-only release checks. Never creates tags, releases, packages or uploads."""
import argparse
import io
import json
import os
from pathlib import Path
import re
import subprocess
import sys
import xml.etree.ElementTree as ET
import zipfile

VERSION = re.compile(
    r"(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)"
    r"(?:-(alpha|beta|rc)\.([1-9][0-9]*))?"
)


class InvalidRelease(ValueError):
    pass


def require(condition, message):
    if not condition:
        raise InvalidRelease(message)


def parse_version(value):
    match = VERSION.fullmatch(value)
    require(match is not None, "版本必须为 X.Y.Z 或 X.Y.Z-alpha/beta/rc.N（无 v、前导零或其他文字）")
    return match


def git(root, *args):
    result = subprocess.run(
        ["git", "-C", str(root), "--work-tree=" + str(root), *args],
        capture_output=True, text=True, encoding="utf-8",
    )
    require(result.returncode == 0, "Git 检查失败：" + " ".join(args[:2]))
    return result.stdout.strip()


def read(root, name, ref=None):
    if ref:
        return git(root, "show", ref + ":" + name)
    return (root / name).read_text(encoding="utf-8-sig")


def policy(root):
    data = json.loads(read(root, "release-policy.json"))
    expected = {
        "windows": ("Bili Play", "Bili_Play", ".zip"),
        "android": ("Bili Play Android", "Bili_Play_Android", ".apk"),
    }
    require(data["platform"] in expected, "未知发布平台")
    product, prefix, extension = expected[data["platform"]]
    require(data["product"] == product and data["assetPrefix"] == prefix, "发布配置与平台不一致")
    return dict(data, extension=extension)


def source_version(root, platform, ref=None):
    if platform == "windows":
        project = ET.fromstring(read(root, "BiliPlay.csproj", ref))
        versions = [e.text for e in project.iter() if e.tag == "Version"]
        require(len(versions) == 1, "BiliPlay.csproj 必须只有一个明确的 Version")
        version = versions[0]
        match = parse_version(version)
        core = ".".join(match.group(i) for i in (1, 2, 3))
        require(all(int(match.group(i)) <= 65535 for i in (1, 2, 3)), "MSIX 数字段超过 65535")
        manifest = ET.fromstring(read(root, "Package.appxmanifest", ref))
        identity = manifest.find("{*}Identity")
        require(identity is not None and identity.get("Version") == core + ".0",
                "项目 Version 与四段 MSIX 版本不一致（正式映射 X.Y.Z.0）")
        return version, None
    values = {}
    for line in read(root, "version.properties", ref).splitlines():
        if line.strip() and not line.lstrip().startswith("#"):
            key, value = line.split("=", 1)
            require(key.strip() not in values, "version.properties 存在重复字段")
            values[key.strip()] = value.strip()
    version = values["versionName"]
    parse_version(version)
    code = values["versionCode"]
    require(re.fullmatch(r"[1-9][0-9]*", code) is not None, "versionCode 必须为正整数")
    require(int(code) <= 2100000000, "versionCode 超过 Android 发布上限")
    return version, int(code)


def check_android_code(root, version, code):
    codes = []
    tags = git(root, "tag", "--list").splitlines()
    for tag in tags:
        if not VERSION.fullmatch(tag):
            continue
        old_version, old_code = source_version(root, "android", "refs/tags/" + tag)
        require(old_version == tag, "历史标签的 versionName 与标签不一致：" + tag)
        if tag == version:
            require(old_code == code, "不可改写已标记版本的 versionCode")
        codes.append(old_code)
    if codes and version not in tags:
        require(code > max(codes), "新版本 versionCode 必须大于所有已标记版本（预发布也要递增）")


def check_tag(root, tag, current, product):
    parse_version(tag)
    require(tag == current, "Tag 与程序版本不一致")
    ref = "refs/tags/" + tag
    require(git(root, "cat-file", "-t", ref) == "tag", "必须使用 Annotated Tag，不能是 Lightweight Tag")
    subject = git(root, "for-each-ref", "--format=%(contents:subject)", ref)
    require(subject == product + " v" + tag, "Tag 注释不符合平台命名规范")
    require(git(root, "rev-parse", ref + "^{commit}") == git(root, "rev-parse", "HEAD"),
            "请检出该 Tag 的提交再执行发布校验")
    require(not git(root, "status", "--porcelain", "--untracked-files=all"),
            "发布前工作区必须干净；不能给未提交内容打包发布")


def check_notes(body):
    active = None
    counts = {}
    seen = set()
    for line in body.splitlines():
        heading = re.fullmatch(r"(?:#{2,3}\s+(新增|修复|改进)|\*\*(新增|修复|改进)\*\*)\s*", line)
        if heading:
            active = heading.group(1) or heading.group(2)
            require(active not in counts, "更新日志分类重复：" + active)
            counts[active] = 0
        elif line.startswith("#"):
            active = None
        elif re.match(r"^\s*[-*]\s+", line) and active:
            text = re.sub(r"^\s*[-*]\s+", "", line).strip()
            require(text and not re.match(r"(?i)(?:TODO\b|待填写|在此填写|占位(?:文字|内容|条目))(?:[:：\s]|$)", text),
                    "更新日志仍含占位内容")
            normalized = re.sub(r"[\s*。.!！]+", "", text)
            require(normalized not in seen, "更新日志条目重复")
            seen.add(normalized)
            counts[active] += 1
    require(counts and all(counts.values()), "更新日志应使用非空的二/三级标题：新增、修复、改进；空分类请省略")


def asset_name(data, version):
    return data["assetPrefix"] + "_" + version + data["extension"]


def check_release(data, release, version):
    require(release["tag_name"] == version, "Release Tag 与源码版本不一致")
    require(release["name"] == data["product"] + " v" + version, "Release 标题不符合规范")
    expected = parse_version(version).group(4) is not None
    require(type(release["prerelease"]) is bool and release["prerelease"] == expected,
            "Release 的 Pre-release 状态与版本后缀不一致")
    check_notes(release.get("body") or "")
    names = [item["name"] for item in release.get("assets", [])]
    require(names == [asset_name(data, version)], "Release 应仅包含一个名称正确且包含完整版本号的发布附件")


def check_android_badging(badging, version, code):
    match = re.search(r"^package: name='([^']+)' versionCode='([0-9]+)' versionName='([^']+)'",
                      badging, re.MULTILINE)
    require(match is not None, "aapt 未返回有效 APK 元数据")
    require(match.group(1) == "io.github.sodarx.biliplay", "不能把开发验收包当成正式 APK")
    require(match.group(2) == str(code) and match.group(3) == version,
            "APK 内部 versionName/versionCode 与源码、Tag 不一致")


def check_asset(path, data, version, code, aapt=None):
    require(path.is_file() and not path.is_symlink(), "发布附件不存在或是符号链接")
    require(path.name == asset_name(data, version), "发布附件名不符合规范（含扩展名大小写）")
    if data["platform"] == "android":
        require(aapt is not None, "检查 APK 内容必须传入 Android SDK 的 --aapt 路径")
        result = subprocess.run([str(aapt), "dump", "badging", str(path)],
                                capture_output=True, text=True, encoding="utf-8")
        require(result.returncode == 0, "aapt 检查失败")
        check_android_badging(result.stdout, version, code)
        return
    require(parse_version(version).group(4) is None,
            "Windows 预发布安装链路尚未配置：需先解决预览身份、版本显示与升级顺序")
    with zipfile.ZipFile(path) as archive:
        expected = {"BiliPlay_" + version + "_x64.msix", "BiliPlay.cer"}
        require(len(archive.infolist()) == 2 and set(archive.namelist()) == expected,
                "ZIP 根目录必须仅含对应版本 MSIX 与 BiliPlay.cer（公开证书）")
        for item in archive.infolist():
            require(not item.is_dir() and 0 < item.file_size <= 1024**3, "ZIP 存在目录、空文件或异常大文件")
        package_name = next(name for name in expected if name.endswith(".msix"))
        with zipfile.ZipFile(io.BytesIO(archive.read(package_name))) as package:
            manifest = ET.fromstring(package.read("AppxManifest.xml"))
            identity = manifest.find("{*}Identity")
            require(identity is not None and identity.get("Version") == version + ".0",
                    "ZIP 内 MSIX 的版本与 Tag 不一致")
            require(identity.get("Name") == "sodarx.BiliPlay"
                    and identity.get("Publisher") == "CN=43D94D0E-79B6-4582-B52D-D4AE86E09E83"
                    and identity.get("ProcessorArchitecture") == "x64",
                    "MSIX 身份不是正式 x64 包")
            require("AppxSignature.p7x" in package.namelist(), "MSIX 未签名")
    # Signature presence is NOT cryptographic verification. Matching certificate,
    # hashes, signature and privacy checks remain in Build-Installer.ps1.


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--tag", help="检查现有标签；工作区必须检出该提交且干净")
    parser.add_argument("--notes", type=Path, help="发布前检查中文 Markdown 更新日志")
    parser.add_argument("--asset", type=Path, help="检查本地发布附件（必须同时提供 --tag、--notes）")
    parser.add_argument("--aapt", type=Path, help="Android SDK aapt 的可信可执行文件路径")
    parser.add_argument("--release-json", type=Path, help="检查 GitHub Release JSON 或完整事件")
    parser.add_argument("--ci", action="store_true", help="自动读取 GitHub Actions 事件，不修改远端")
    args = parser.parse_args(argv)
    root = Path(__file__).resolve().parent.parent
    try:
        data = policy(root)
        version, code = source_version(root, data["platform"])
        if code is not None:
            check_android_code(root, version, code)
        payload = None
        event = args.release_json
        if args.ci and os.environ.get("GITHUB_EVENT_NAME") == "release":
            event = Path(os.environ["GITHUB_EVENT_PATH"])
        if event:
            payload = json.loads(event.read_text(encoding="utf-8-sig"))
            payload = payload.get("release", payload)
            check_release(data, payload, version)
        tag = args.tag or (payload["tag_name"] if payload else None)
        if args.ci:
            tag = tag or os.environ.get("RELEASE_TAG") or None
            if os.environ.get("GITHUB_REF_TYPE") == "tag":
                tag = tag or os.environ["GITHUB_REF_NAME"]
        if tag:
            check_tag(root, tag, version, data["product"])
        if args.notes:
            check_notes(args.notes.read_text(encoding="utf-8-sig"))
        if args.asset:
            require(tag and args.notes, "附件检查必须同时指定 --tag 与 --notes")
            check_asset(args.asset, data, version, code, args.aapt)
        print("PASS source version: " + version + (f" (versionCode={code})" if code else ""))
        print("Required release title: " + data["product"] + " v" + version)
        print("Required asset: " + asset_name(data, version))
        if payload:
            print("PASS release metadata / asset names; remote binaries were NOT downloaded or verified")
        if args.asset:
            print("PASS local package version / identity; retain separate signing and privacy verification")
        return 0
    except (InvalidRelease, OSError, KeyError, ValueError, ET.ParseError, zipfile.BadZipFile) as error:
        print("FAIL: " + str(error), file=sys.stderr)
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
