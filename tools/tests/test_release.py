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


if __name__ == "__main__":
    unittest.main()
