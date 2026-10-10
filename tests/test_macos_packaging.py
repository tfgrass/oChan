"""Fixture-only packaging checks; these do not launch or validate a macOS app."""

import importlib.util
import os
from pathlib import Path
import plistlib
import stat
import struct
import subprocess
import sys
import tarfile
import tempfile
import unittest
from unittest import mock
from types import SimpleNamespace
import zlib


SCRIPT = Path(__file__).resolve().parents[1] / "scripts" / "package-macos.py"
SPEC = importlib.util.spec_from_file_location("ochan_macos_packaging", SCRIPT)
packaging = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(packaging)


def png_chunk(kind, data):
    return struct.pack(">I", len(data)) + kind + data + struct.pack(">I", zlib.crc32(kind + data) & 0xFFFFFFFF)


def make_png(width=256, height=256):
    return (
        packaging.PNG_SIGNATURE
        + png_chunk(b"IHDR", struct.pack(">IIBBBBB", width, height, 8, 6, 0, 0, 0))
        + png_chunk(b"IDAT", zlib.compress((b"\x00" + b"\x44\x55\x66\xff" * width) * height))
        + png_chunk(b"IEND", b"")
    )


def make_ico(png):
    return struct.pack("<HHHBBBBHHII", 0, 1, 1, 0, 0, 0, 0, 1, 32, len(png), 22) + png


class MacosPackagingTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory(prefix="ochan packaging fixtures ")
        self.addCleanup(self.temporary.cleanup)
        self.root = Path(self.temporary.name)
        self.publish = self.root / "publish input"
        self.publish.mkdir()
        (self.publish / "oChan").write_bytes(b"fixture executable; not runnable")
        (self.publish / "oChan").chmod(0o640)
        (self.publish / "oChan.dll").write_bytes(b"managed fixture")
        (self.publish / "libAvaloniaNative.dylib").write_bytes(b"native fixture")
        (self.publish / ".metadata").write_text("publish metadata", encoding="utf-8")
        notices = self.publish / "licenses"
        notices.mkdir()
        (notices / "Avalonia.txt").write_text("upstream license", encoding="utf-8")
        (self.publish / "THIRD-PARTY-NOTICES.md").write_text("notices", encoding="utf-8")
        self.png = make_png()
        self.icon = self.root / "source icon.ico"
        self.icon.write_bytes(make_ico(self.png))
        self.output = self.root / "artifact output"
        self.output.mkdir()
        self.sibling = self.output / "unrelated data.txt"
        self.sibling.write_text("keep", encoding="utf-8")
        self.archive = self.output / "oChan osx fixture.tar.gz"

    def package(self, **kwargs):
        options = dict(publish_directory=self.publish, output_directory=self.output, version="1.0.0", icon_path=self.icon)
        options.update(kwargs)
        return packaging.package_macos(**options)

    def assert_no_bundle(self):
        self.assertFalse((self.output / "oChan.app").exists())
        self.assertEqual("keep", self.sibling.read_text(encoding="utf-8"))
        self.assertEqual([self.sibling], list(self.output.iterdir()))

    def test_complete_bundle_preserves_source_and_siblings(self):
        source_mode = (self.publish / "oChan").stat().st_mode
        bundle, archive = self.package()
        self.assertIsNone(archive)
        macos = bundle / "Contents" / "MacOS"
        for source in self.publish.rglob("*"):
            target = macos / source.relative_to(self.publish)
            if source.is_file():
                self.assertEqual(source.read_bytes(), target.read_bytes())
            else:
                self.assertTrue(target.is_dir())
        self.assertEqual("keep", self.sibling.read_text(encoding="utf-8"))
        self.assertEqual(source_mode, (self.publish / "oChan").stat().st_mode)
        if os.name != "nt":
            self.assertEqual(stat.S_IMODE(source_mode) | stat.S_IXUSR, stat.S_IMODE((macos / "oChan").stat().st_mode))
        metadata = plistlib.loads((bundle / "Contents" / "Info.plist").read_bytes())
        self.assertEqual("org.ochan.desktop", metadata["CFBundleIdentifier"])
        self.assertEqual("1.0.0", metadata["CFBundleShortVersionString"])
        self.assertEqual("1.0.0", metadata["CFBundleVersion"])
        self.assertEqual("15.0", metadata["LSMinimumSystemVersion"])
        self.assertEqual("oChan", metadata["CFBundleExecutable"])
        self.assertEqual("ochan.icns", metadata["CFBundleIconFile"])
        icon = (bundle / "Contents" / "Resources" / "ochan.icns").read_bytes()
        self.assertEqual((b"icns", len(icon), b"ic08", len(icon) - 8), struct.unpack_from(">4sI4sI", icon))
        self.assertEqual(self.png, icon[16:])
        self.assertEqual(self.png, packaging.validate_icns(icon))

    def test_archive_round_trip_includes_only_full_bundle(self):
        bundle, archive_path = self.package(archive_path=self.archive)
        self.assertEqual(self.archive, archive_path)
        with tarfile.open(archive_path, "r:gz") as archive:
            names = set(archive.getnames())
            expected = {"oChan.app"} | {"oChan.app/" + entry.relative_to(bundle).as_posix() for entry in bundle.rglob("*")}
            self.assertEqual(expected, names)
            self.assertTrue(all(name.split("/")[0] == "oChan.app" and ".." not in name.split("/") for name in names))
            for name in names:
                if archive.getmember(name).isfile():
                    self.assertEqual((self.output / name).read_bytes(), archive.extractfile(name).read())
            self.assertTrue(archive.getmember("oChan.app/Contents/MacOS/oChan").mode & stat.S_IXUSR)
            self.assertNotIn("unrelated data.txt", names)

    def test_existing_bundle_is_untouched(self):
        bundle = self.output / "oChan.app"
        bundle.mkdir()
        marker = bundle / "existing.txt"
        marker.write_text("existing", encoding="utf-8")
        with self.assertRaisesRegex(packaging.PackagingError, "overwrite"):
            self.package()
        self.assertEqual("existing", marker.read_text(encoding="utf-8"))

    def test_existing_archive_is_untouched(self):
        self.archive.write_bytes(b"previous archive")
        with self.assertRaisesRegex(packaging.PackagingError, "overwrite"):
            self.package(archive_path=self.archive)
        self.assertEqual(b"previous archive", self.archive.read_bytes())
        self.assertFalse((self.output / "oChan.app").exists())

    def test_missing_input_and_executable_are_rejected(self):
        with self.assertRaises(packaging.PackagingError):
            self.package(publish_directory=self.root / "missing")
        (self.publish / "oChan").unlink()
        with self.assertRaisesRegex(packaging.PackagingError, "executable"):
            self.package()
        self.assert_no_bundle()

    def test_file_input_is_rejected(self):
        with self.assertRaisesRegex(packaging.PackagingError, "directory"):
            self.package(publish_directory=self.publish / "oChan")
        self.assert_no_bundle()

    def test_invalid_version_is_rejected(self):
        for version in ("", "v1.0.0", "1.0", "1.0.0-beta", "01.0.0", "../../other"):
            with self.subTest(version=version), self.assertRaises(packaging.PackagingError):
                self.package(version=version)
        self.assert_no_bundle()

    def test_invalid_icons_are_rejected_before_output(self):
        wrong_dimensions = make_ico(make_png(128, 256))
        bad_offset = bytearray(make_ico(self.png))
        struct.pack_into("<I", bad_offset, 18, len(bad_offset) + 100)
        bad_crc = bytearray(make_ico(self.png))
        bad_crc[-1] ^= 1
        for payload in (b"", b"invalid icon", make_ico(self.png)[:-1], wrong_dimensions, bad_offset, bad_crc):
            with self.subTest(payload_length=len(payload)):
                self.icon.write_bytes(payload)
                with self.assertRaises(packaging.PackagingError):
                    self.package()
        self.assert_no_bundle()

    def test_missing_icon_is_rejected(self):
        self.icon.unlink()
        with self.assertRaises(OSError):
            self.package()
        self.assert_no_bundle()

    def test_output_must_not_overlap_publish_tree(self):
        with self.assertRaisesRegex(packaging.PackagingError, "overlap"):
            self.package(output_directory=self.publish)
        self.assertFalse((self.publish / "oChan.app").exists())
        with self.assertRaisesRegex(packaging.PackagingError, "outside"):
            self.package(archive_path=self.publish / "output.tar.gz")
        with self.assertRaisesRegex(packaging.PackagingError, "outside"):
            self.package(archive_path=self.output / "oChan.app" / "output.tar.gz")
        self.assert_no_bundle()

    def test_archive_extension_is_validated(self):
        with self.assertRaisesRegex(packaging.PackagingError, "tar.gz"):
            self.package(archive_path=self.output / "archive.zip")
        self.assert_no_bundle()

    def symlink(self, target, link):
        try:
            link.symlink_to(target)
        except (OSError, NotImplementedError):
            self.skipTest("Symbolic links are not available for this fixture user.")

    def test_relative_internal_symbolic_link_is_preserved(self):
        self.symlink("libAvaloniaNative.dylib", self.publish / "native-alias.dylib")
        bundle, _ = self.package(archive_path=self.archive)
        link = bundle / "Contents" / "MacOS" / "native-alias.dylib"
        self.assertTrue(link.is_symlink())
        self.assertEqual("libAvaloniaNative.dylib", os.readlink(link))
        with tarfile.open(self.archive, "r:gz") as archive:
            entry = archive.getmember("oChan.app/Contents/MacOS/native-alias.dylib")
            self.assertTrue(entry.issym())
            self.assertEqual("libAvaloniaNative.dylib", entry.linkname)

    def test_executable_must_not_be_a_symbolic_link(self):
        executable = self.publish / "oChan"
        executable.rename(self.publish / "real-apphost")
        self.symlink("real-apphost", executable)
        with self.assertRaisesRegex(packaging.PackagingError, "regular file"):
            self.package()
        self.assert_no_bundle()

    def test_windows_reparse_points_are_rejected(self):
        original_lstat = Path.lstat

        def lstat_with_reparse_point(path):
            result = original_lstat(path)
            if path == self.publish / "licenses":
                return SimpleNamespace(st_mode=result.st_mode, st_file_attributes=0x400)
            return result

        with mock.patch.object(Path, "lstat", new=lstat_with_reparse_point):
            with self.assertRaisesRegex(packaging.PackagingError, "reparse point"):
                self.package()
        self.assert_no_bundle()

    def test_external_and_dangling_symbolic_links_are_rejected(self):
        link = self.publish / "external"
        self.symlink(self.sibling, link)
        with self.assertRaisesRegex(packaging.PackagingError, "within"):
            self.package()
        link.unlink()
        self.symlink("missing", link)
        with self.assertRaisesRegex(packaging.PackagingError, "invalid symbolic"):
            self.package()
        self.assert_no_bundle()

    def test_dangling_destination_links_are_not_overwritten(self):
        destination = self.output / "oChan.app"
        self.symlink("missing", destination)
        with self.assertRaisesRegex(packaging.PackagingError, "overwrite"):
            self.package()
        self.assertTrue(destination.is_symlink())
        destination.unlink()
        self.symlink("missing", self.archive)
        with self.assertRaisesRegex(packaging.PackagingError, "overwrite"):
            self.package(archive_path=self.archive)
        self.assertTrue(self.archive.is_symlink())

    def test_archive_write_failure_cleans_only_owned_output(self):
        with mock.patch.object(packaging.shutil, "copyfileobj", side_effect=OSError("disk full")):
            with self.assertRaisesRegex(OSError, "disk full"):
                self.package(archive_path=self.archive)
        self.assert_no_bundle()

    def test_archive_collision_during_packaging_is_preserved(self):
        original = packaging.write_archive

        def race(bundle, archive):
            original(bundle, archive)
            self.archive.write_bytes(b"concurrent owner")

        with mock.patch.object(packaging, "write_archive", side_effect=race):
            with self.assertRaises(FileExistsError):
                self.package(archive_path=self.archive)
        self.assertEqual(b"concurrent owner", self.archive.read_bytes())
        self.assertFalse((self.output / "oChan.app").exists())

    def test_bundle_collision_during_packaging_is_preserved(self):
        original = packaging.write_archive
        destination = self.output / "oChan.app"

        def race(bundle, archive):
            original(bundle, archive)
            destination.mkdir()
            (destination / "keep.txt").write_bytes(b"concurrent owner")

        with mock.patch.object(packaging, "write_archive", side_effect=race):
            with self.assertRaises(FileExistsError):
                self.package(archive_path=self.archive)
        self.assertEqual(b"concurrent owner", (destination / "keep.txt").read_bytes())
        self.assertFalse(self.archive.exists())
        self.assertEqual("keep", self.sibling.read_text(encoding="utf-8"))

    def test_plutil_failure_leaves_existing_data_untouched(self):
        with (
            mock.patch.object(packaging.sys, "platform", "darwin"),
            mock.patch.object(packaging.shutil, "which", return_value="/usr/bin/plutil"),
            mock.patch.object(packaging.subprocess, "run", return_value=SimpleNamespace(returncode=1, stdout="", stderr="invalid plist")) as run,
        ):
            with self.assertRaisesRegex(packaging.PackagingError, "plutil rejected"):
                self.package()
        self.assertEqual(["plutil", "-lint"], run.call_args.args[0][:2])
        self.assert_no_bundle()

    def test_original_repository_icon_is_embedded_unchanged(self):
        png = packaging.png_from_ico(packaging.DEFAULT_ICON)
        self.assertEqual(png, packaging.validate_icns(packaging.make_icns(png)))

    def test_cli_handles_spaces_and_describes_verification_limit(self):
        result = subprocess.run(
            [sys.executable, str(SCRIPT), "--publish-dir", str(self.publish), "--output-dir", str(self.output), "--version", "1.0.0", "--icon", str(self.icon), "--archive", str(self.archive)],
            capture_output=True, text=True, check=False,
        )
        self.assertEqual(0, result.returncode, result.stderr)
        self.assertIn("unsigned bundle", result.stdout)
        self.assertIn("candidate baseline", result.stdout)
        self.assertIn("native launch verification remains required", result.stdout)
        self.assertTrue(self.archive.is_file())


if __name__ == "__main__":
    unittest.main()
