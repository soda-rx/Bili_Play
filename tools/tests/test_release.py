"""Synthetic regression fixtures: no personal data, remote writes or app execution."""
import importlib.util
import io
import json
import os
import shutil
import sys
import subprocess
from pathlib import Path
import tempfile
import unittest
import zipfile

SPEC = importlib.util.spec_from_file_location("check_release", Path(__file__).parents[1] / "check_release.py")
r = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(r)


class ReleaseChecks(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        self.windows = dict(platform="windows", product="Bili Play", assetPrefix="Bili_Play", extension=".zip")
        self.android = dict(platform="android", product="Bili Play Android", assetPrefix="Bili_Play_Android", extension=".apk")
        self.notes = "## 修复\n\n- 修复封面加载失败。（@Example，#3）\n"

    def rejects(self, call, *args):
        with self.assertRaises(r.InvalidRelease):
            call(*args)

    def git(self, *args):
        return r.git(self.root, *args)

    def repo(self):
        subprocess.run(["git", "-C", str(self.root), "init", "-q"], check=True, capture_output=True)
        self.git("config", "core.hooksPath", str(self.root / ".git/no-hooks"))
        self.git("config", "user.name", "Release Test")
        self.git("config", "user.email", "release-test@example.invalid")
        self.git("config", "commit.gpgSign", "false")
        self.git("config", "tag.gpgSign", "false")
        (self.root / "version.properties").write_text("versionName=1.0.0\nversionCode=36\n")
        self.git("add", ".")
        self.git("commit", "-qm", "Synthetic version")

    def test_version_formats(self):
        for value in ("1.0.0", "0.2.34", "1.1.0-alpha.1", "1.1.0-beta.2", "1.1.0-rc.1"):
            r.parse_version(value)
        for value in ("v1.0.0", "1.00.0", "1.0.0初始版本", "Bili Play 1.0.0", "1.0.0.0",
                      "1.0.0-beta.01", "1.0.0-beta.0", "1.0.0+local", "1.0"):
            self.rejects(r.parse_version, value)

    def test_annotated_tag_and_clean_commit(self):
        self.repo()
        self.git("tag", "-a", "1.0.0", "-m", "Bili Play Android v1.0.0")
        r.check_tag(self.root, "1.0.0", "1.0.0", "Bili Play Android")
        self.rejects(r.check_tag, self.root, "1.0.0", "1.0.0", "Bili Play")
        self.rejects(r.check_tag, self.root, "1.0.0", "1.0.1", "Bili Play Android")
        (self.root / "uncommitted.txt").write_text("Synthetic file")
        self.rejects(r.check_tag, self.root, "1.0.0", "1.0.0", "Bili Play Android")
        (self.root / "uncommitted.txt").unlink()
        (self.root / "next.txt").write_text("Synthetic next commit")
        self.git("add", ".")
        self.git("commit", "-qm", "Another commit")
        self.rejects(r.check_tag, self.root, "1.0.0", "1.0.0", "Bili Play Android")

    def test_lightweight_tag_rejected(self):
        self.repo()
        self.git("tag", "1.0.0")
        self.rejects(r.check_tag, self.root, "1.0.0", "1.0.0", "Bili Play Android")

    def test_android_code_monotonic_and_immutable(self):
        self.repo()
        self.git("tag", "-a", "1.0.0", "-m", "Bili Play Android v1.0.0")
        r.check_android_code(self.root, "1.0.0", 36)
        r.check_android_code(self.root, "1.0.1-beta.1", 37)
        self.rejects(r.check_android_code, self.root, "1.0.1", 36)
        self.rejects(r.check_android_code, self.root, "1.0.0", 37)

    def test_windows_numeric_manifest_must_match(self):
        (self.root / "BiliPlay.csproj").write_text("<Project><Version>1.2.3</Version></Project>")
        path = self.root / "Package.appxmanifest"
        path.write_text('<Package xmlns="urn:test"><Identity Version="1.2.3.0"/></Package>')
        self.assertEqual(("1.2.3", None), r.source_version(self.root, "windows"))
        for wrong in ("1.2.2.0", "1.2.3.1"):
            path.write_text(f'<Package xmlns="urn:test"><Identity Version="{wrong}"/></Package>')
            self.rejects(r.source_version, self.root, "windows")

    def test_notes_sections_empty_duplicates_and_placeholders(self):
        r.check_notes(self.notes)
        r.check_notes("## 修复\n- 修复封面停留在占位符的问题。\n")
        for body in ("初始版本", "## 修复\n", "## 修复\n- TODO\n",
                     self.notes + "\n## 改进\n- 修复封面加载失败。（@Example，#3）\n",
                     self.notes + "\n## 新增\n", self.notes + "\n## 修复\n- 第二项\n"):
            self.rejects(r.check_notes, body)

    def test_title_assets_and_prerelease_flag(self):
        for data in (self.windows, self.android):
            release = dict(tag_name="1.0.0", name=data["product"] + " v1.0.0",
                           body=self.notes, prerelease=False,
                           assets=[dict(name=r.asset_name(data, "1.0.0"))])
            r.check_release(data, release, "1.0.0")
            for field, bad in (("name", data["product"] + " v1.0.1"), ("prerelease", True),
                               ("tag_name", "v1.0.0"), ("assets", [])):
                self.rejects(r.check_release, data, dict(release, **{field: bad}), "1.0.0")
        version = "1.1.0-beta.1"
        preview = dict(tag_name=version, name="Bili Play Android v" + version, body=self.notes,
                       prerelease=True, assets=[dict(name=r.asset_name(self.android, version))])
        r.check_release(self.android, preview, version)
        self.rejects(r.check_release, self.android, dict(preview, prerelease=False), version)
        release = dict(tag_name="1.0.0", name="Bili Play Android v1.0.0", body=self.notes,
                       prerelease=False, assets=[dict(name="Bili_Play_Android_1.0.0.APK")])
        self.rejects(r.check_release, self.android, release, "1.0.0")

    def test_android_internal_identity_and_version(self):
        valid = "package: name='io.github.sodarx.biliplay' versionCode='36' versionName='1.0.0' platformBuildVersionName='16'"
        r.check_android_badging(valid, "1.0.0", 36)
        for bad in (valid.replace("biliplay'", "biliplay.development'"),
                    valid.replace("versionCode='36'", "versionCode='35'"),
                    valid.replace("versionName='1.0.0'", "versionName='0.9.9'")):
            self.rejects(r.check_android_badging, bad, "1.0.0", 36)

    def make_zip(self, version="1.0.0", extra=None, package_version="1.0.0.0"):
        package = io.BytesIO()
        with zipfile.ZipFile(package, "w") as z:
            z.writestr("AppxManifest.xml",
                       '<Package xmlns="urn:test"><Identity Name="sodarx.BiliPlay" '
                       'Publisher="CN=43D94D0E-79B6-4582-B52D-D4AE86E09E83" '
                       f'ProcessorArchitecture="x64" Version="{package_version}"/></Package>')
            z.writestr("AppxSignature.p7x", b"Synthetic non-cryptographic fixture")
        path = self.root / ("Bili_Play_" + version + ".zip")
        with zipfile.ZipFile(path, "w") as z:
            z.writestr("BiliPlay_" + version + "_x64.msix", package.getvalue())
            z.writestr("BiliPlay.cer", b"Synthetic public certificate fixture")
            if extra:
                z.writestr(extra, b"Synthetic forbidden extra file")
        return path

    def test_windows_archive_contents_and_internal_version(self):
        r.check_asset(self.make_zip(), self.windows, "1.0.0", None)
        self.rejects(r.check_asset, self.make_zip(extra="signing.pfx"), self.windows, "1.0.0", None)
        self.rejects(r.check_asset, self.make_zip(package_version="0.9.9.0"), self.windows, "1.0.0", None)
        self.rejects(r.check_asset, self.make_zip("1.1.0-beta.1"), self.windows, "1.1.0-beta.1", None)

    def test_ci_release_event_end_to_end(self):
        self.repo()
        tools = self.root / "tools"
        tools.mkdir()
        shutil.copyfile(r.__file__, tools / "check_release.py")
        (self.root / "release-policy.json").write_text(
            json.dumps({key: value for key, value in self.android.items() if key != "extension"}),
            encoding="utf-8")
        self.git("add", ".")
        self.git("commit", "-qm", "Synthetic release tooling")
        self.git("tag", "-a", "1.0.0", "-m", "Bili Play Android v1.0.0")
        release = dict(tag_name="1.0.0", name="Bili Play Android v1.0.0",
                       body=self.notes, prerelease=False,
                       assets=[dict(name="Bili_Play_Android_1.0.0.apk")])
        event = self.root / ".git" / "synthetic-release-event.json"
        environment = dict(os.environ, GITHUB_EVENT_NAME="release",
                           GITHUB_EVENT_PATH=str(event), GITHUB_REF_TYPE="tag",
                           GITHUB_REF_NAME="1.0.0", RELEASE_TAG="", PYTHONUTF8="1")
        for flag, expected_code in ((False, 0), (True, 1)):
            release["prerelease"] = flag
            event.write_text(json.dumps(dict(release=release)), encoding="utf-8")
            result = subprocess.run(
                [sys.executable, "-B", str(tools / "check_release.py"), "--ci"],
                env=environment, capture_output=True, text=True, encoding="utf-8")
            self.assertEqual(expected_code, result.returncode, result.stdout + result.stderr)

    def historical_repo(self):
        self.repo()
        self.git("tag", "-a", "1.0.0", "-m", "Bili Play Android v1.0.0")
        tools = self.root / "tools"
        tools.mkdir()
        shutil.copyfile(r.__file__, tools / "check_release.py")
        (self.root / "release-policy.json").write_text(
            json.dumps({key: value for key, value in self.android.items() if key != "extension"}),
            encoding="utf-8")
        (self.root / "version.properties").write_text("versionName=1.1.0\nversionCode=37\n")
        self.git("add", ".")
        self.git("commit", "-qm", "Current tool and newer product version")

    def test_history_uses_old_snapshot_without_old_checker(self):
        self.historical_repo()
        missing = subprocess.run(
            ["git", "-C", str(self.root), "cat-file", "-e", "refs/tags/1.0.0:tools/check_release.py"],
            capture_output=True)
        self.assertNotEqual(0, missing.returncode)
        (self.root / "uncommitted.txt").write_text("Synthetic current work")
        head_before = self.git("rev-parse", "HEAD")
        changes_before = self.git("status", "--porcelain")
        record = r.audit_history(self.root, self.android, "1.0.0")[0]
        self.assertEqual(("1.0.0", 36), (record["version"], record["code"]))
        self.assertEqual(head_before, self.git("rev-parse", "HEAD"))
        self.assertEqual(changes_before, self.git("status", "--porcelain"))
        self.rejects(r.check_tag, self.root, "1.0.0", "1.0.0", "Bili Play Android")

    def test_history_rejects_lightweight_and_version_mismatch(self):
        self.repo()
        self.git("tag", "1.0.0")
        self.rejects(r.audit_history, self.root, self.android, "1.0.0")
        self.git("tag", "-a", "1.0.1", "-m", "Bili Play Android v1.0.1")
        self.rejects(r.audit_history, self.root, self.android, "1.0.1")
        self.rejects(r.audit_history, self.root, self.android, "HEAD")

    def test_history_metadata_does_not_validate_or_modify_user_notes(self):
        self.historical_repo()
        body = "作者自定义正文与署名，原样保留。"
        release = dict(tag_name="1.0.0", name="Bili Play Android v1.0.0", body=body,
                       prerelease=False, assets=[dict(name="Bili_Play_Android_1.0.0.apk")])
        original = json.dumps(release, ensure_ascii=False)
        r.audit_history(self.root, self.android, "1.0.0", release)
        self.assertEqual(original, json.dumps(release, ensure_ascii=False))
        self.rejects(r.audit_history, self.root, self.android, "1.0.0",
                     dict(release, name="Bili Play Android v1.0.1"))

    def test_current_tool_cli_reads_old_release_and_manual_history(self):
        self.historical_repo()
        event = self.root / ".git" / "historical-event.json"
        release = dict(tag_name="1.0.0", name="Bili Play Android v1.0.0", body="原始说明",
                       prerelease=False, assets=[dict(name="Bili_Play_Android_1.0.0.apk")])
        event.write_text(json.dumps(dict(release=release)), encoding="utf-8")
        for event_name, selected in (("release", ""), ("workflow_dispatch", "1.0.0"),
                                     ("workflow_dispatch", "")):
            env = dict(os.environ, GITHUB_EVENT_NAME=event_name, GITHUB_EVENT_PATH=str(event),
                       GITHUB_REF_TYPE="branch", GITHUB_REF_NAME="main",
                       RELEASE_TAG=selected, PYTHONUTF8="1")
            process = subprocess.run(
                [sys.executable, "-B", str(self.root / "tools/check_release.py"), "--ci"],
                env=env, capture_output=True, text=True, encoding="utf-8")
            self.assertEqual(0, process.returncode, process.stdout + process.stderr)
            self.assertIn("PASS historical tag: 1.0.0", process.stdout)
        process = subprocess.run(
            [sys.executable, "-B", str(self.root / "tools/check_release.py"),
             "--audit-history", "--tag", "1.0.0", "--asset", "unused.zip"],
            capture_output=True, text=True, encoding="utf-8", env=dict(os.environ, PYTHONUTF8="1"))
        self.assertEqual(1, process.returncode)

    def test_history_never_executes_script_stored_in_old_tag(self):
        self.repo()
        tools = self.root / "tools"
        tools.mkdir()
        (tools / "check_release.py").write_text("raise AssertionError('must not run old code')\n")
        self.git("add", ".")
        self.git("commit", "-qm", "Synthetic old tool that must never execute")
        self.git("tag", "-a", "1.0.0", "-m", "Bili Play Android v1.0.0")
        records = r.audit_history(self.root, self.android)
        self.assertEqual(["1.0.0"], [record["tag"] for record in records])


if __name__ == "__main__":
    unittest.main()
