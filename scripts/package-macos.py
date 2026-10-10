#!/usr/bin/env python3
"""Build an unsigned oChan.app from an existing macOS dotnet publish directory.

The 15.0 deployment floor is a candidate baseline, not evidence of a native run.
Only Python's standard library is required. No signing or notarization is done.
"""

from __future__ import annotations

import argparse
import os
from pathlib import Path
import plistlib
import re
import shutil
import stat
import struct
import subprocess
import sys
import tarfile
import tempfile
import zlib


BUNDLE_NAME = "oChan.app"
MINIMUM_MACOS_VERSION = "15.0"
DEFAULT_ICON = Path(__file__).resolve().parents[1] / "Assets" / "ochan.ico"
PNG_SIGNATURE = b"\x89PNG\r\n\x1a\n"


class PackagingError(ValueError):
    """An input or generated artifact does not meet the packaging contract."""


def validate_png(png: bytes) -> None:
    """Check the unchanged PNG's dimensions, chunk boundaries, and checksums."""
    if not png.startswith(PNG_SIGNATURE):
        raise PackagingError("The icon must contain a PNG image.")
    position = len(PNG_SIGNATURE)
    chunks: list[bytes] = []
    while position < len(png):
        if len(png) - position < 12:
            raise PackagingError("The icon PNG has a truncated chunk.")
        length, kind = struct.unpack_from(">I4s", png, position)
        end = position + 12 + length
        if end > len(png):
            raise PackagingError("The icon PNG has an invalid chunk length.")
        data = png[position + 8 : end - 4]
        checksum = struct.unpack_from(">I", png, end - 4)[0]
        if zlib.crc32(kind + data) & 0xFFFFFFFF != checksum:
            raise PackagingError("The icon PNG has an invalid checksum.")
        if not chunks:
            if kind != b"IHDR" or length != 13:
                raise PackagingError("The icon PNG is missing its dimension header.")
            width, height, depth, color, compression, filtering, interlace = struct.unpack(
                ">IIBBBBB", data
            )
            if (width, height) != (256, 256):
                raise PackagingError("The ic08 icon PNG must be exactly 256 x 256 pixels.")
            legal_depths = {0: (1, 2, 4, 8, 16), 2: (8, 16), 3: (1, 2, 4, 8), 4: (8, 16), 6: (8, 16)}
            if depth not in legal_depths.get(color, ()) or compression or filtering or interlace not in (0, 1):
                raise PackagingError("The icon PNG has an invalid image format.")
        elif kind == b"IHDR":
            raise PackagingError("The icon PNG has multiple dimension headers.")
        chunks.append(kind)
        if kind == b"IEND":
            if length or end != len(png) or b"IDAT" not in chunks:
                raise PackagingError("The icon PNG has an invalid end marker or no image data.")
            return
        position = end
    raise PackagingError("The icon PNG is missing its end marker.")


def png_from_ico(icon_path: Path) -> bytes:
    icon = icon_path.read_bytes()
    if len(icon) < 6:
        raise PackagingError("The ICO header is truncated.")
    reserved, image_type, count = struct.unpack_from("<HHH", icon)
    directory_end = 6 + count * 16
    if reserved or image_type != 1 or not count or directory_end > len(icon):
        raise PackagingError("The ICO directory is invalid.")
    selected: bytes | None = None
    for index in range(count):
        width, height, _, entry_reserved, _, _, size, offset = struct.unpack_from(
            "<BBBBHHII", icon, 6 + index * 16
        )
        if entry_reserved or not size or offset < directory_end or offset + size > len(icon):
            raise PackagingError("The ICO contains an invalid image location.")
        candidate = icon[offset : offset + size]
        if width == 0 and height == 0 and candidate.startswith(PNG_SIGNATURE):
            validate_png(candidate)
            if selected is None:
                selected = candidate
    if selected is None:
        raise PackagingError("The ICO must contain an existing 256 x 256 PNG image.")
    return selected


def make_icns(png: bytes) -> bytes:
    validate_png(png)
    return struct.pack(">4sI4sI", b"icns", len(png) + 16, b"ic08", len(png) + 8) + png


def validate_icns(icon: bytes) -> bytes:
    if len(icon) < 16:
        raise PackagingError("The ICNS header is truncated.")
    signature, total_length, kind, element_length = struct.unpack_from(">4sI4sI", icon)
    if signature != b"icns" or total_length != len(icon) or kind != b"ic08" or element_length != len(icon) - 8:
        raise PackagingError("The ICNS container or ic08 element is invalid.")
    png = icon[16:]
    validate_png(png)
    return png


def bundle_metadata(version: str) -> dict[str, object]:
    if not re.fullmatch(r"(?:0|[1-9][0-9]*)\.(?:0|[1-9][0-9]*)\.(?:0|[1-9][0-9]*)", version):
        raise PackagingError("Version must contain three numeric components, for example 1.0.0.")
    return {
        "CFBundleDevelopmentRegion": "en",
        "CFBundleDisplayName": "oChan",
        "CFBundleExecutable": "oChan",
        "CFBundleIconFile": "ochan.icns",
        "CFBundleIdentifier": "org.ochan.desktop",
        "CFBundleInfoDictionaryVersion": "6.0",
        "CFBundleName": "oChan",
        "CFBundlePackageType": "APPL",
        "CFBundleShortVersionString": version,
        "CFBundleVersion": version,
        "LSMinimumSystemVersion": MINIMUM_MACOS_VERSION,
        "NSHighResolutionCapable": True,
    }


def path_present(path: Path) -> bool:
    return path.exists() or path.is_symlink()


def validate_publish_tree(publish_directory: Path) -> None:
    executable = publish_directory / "oChan"
    if executable.is_symlink() or not executable.is_file():
        raise PackagingError("Publish directory must contain the oChan executable as a regular file.")
    for path in publish_directory.rglob("*"):
        if path.is_symlink():
            target = Path(os.readlink(path))
            try:
                resolved = path.resolve(strict=True)
            except (OSError, RuntimeError) as error:
                raise PackagingError(f"Publish output contains an invalid symbolic link: {path}") from error
            if target.is_absolute() or not resolved.is_relative_to(publish_directory):
                raise PackagingError(f"Publish symbolic links must stay within the bundle: {path}")
        elif getattr(path.lstat(), "st_file_attributes", 0) & 0x400:
            # Windows reparse points (including junctions) are not necessarily symlinks.
            raise PackagingError(f"Publish output contains an unsupported reparse point: {path}")
        elif not path.is_dir() and not path.is_file():
            raise PackagingError(f"Publish output contains an unsupported special file: {path}")


def validate_bundle(bundle: Path, metadata: dict[str, object], original_png: bytes) -> None:
    plist_path = bundle / "Contents" / "Info.plist"
    if plistlib.loads(plist_path.read_bytes()) != metadata:
        raise PackagingError("Info.plist did not survive its structural round trip.")
    if validate_icns((bundle / "Contents" / "Resources" / "ochan.icns").read_bytes()) != original_png:
        raise PackagingError("The ICNS icon does not preserve the source PNG.")
    executable = bundle / "Contents" / "MacOS" / "oChan"
    if not executable.is_file() or (os.name != "nt" and not executable.stat().st_mode & stat.S_IXUSR):
        raise PackagingError("The bundled oChan executable is missing or not executable.")
    if sys.platform == "darwin" and shutil.which("plutil"):
        result = subprocess.run(["plutil", "-lint", str(plist_path)], capture_output=True, text=True, check=False)
        if result.returncode:
            raise PackagingError(f"plutil rejected Info.plist: {result.stdout}{result.stderr}")


def write_archive(bundle: Path, archive_path: Path) -> None:
    def archive_permissions(member: tarfile.TarInfo) -> tarfile.TarInfo:
        # Windows cannot represent POSIX executable bits in its filesystem.
        if member.name == f"{BUNDLE_NAME}/Contents/MacOS/oChan":
            member.mode |= stat.S_IXUSR
        return member

    with tarfile.open(archive_path, "w:gz", dereference=False) as archive:
        archive.add(bundle, arcname=BUNDLE_NAME, filter=archive_permissions)
    with tarfile.open(archive_path, "r:gz") as archive:
        names = {member.name for member in archive.getmembers()}
        expected = {BUNDLE_NAME} | {f"{BUNDLE_NAME}/{path.relative_to(bundle).as_posix()}" for path in bundle.rglob("*")}
        if names != expected:
            raise PackagingError("The archive does not contain the complete application bundle.")
        plist = archive.extractfile(f"{BUNDLE_NAME}/Contents/Info.plist")
        if plist is None or plist.read() != (bundle / "Contents" / "Info.plist").read_bytes():
            raise PackagingError("The archive contains an invalid Info.plist.")
        if not archive.getmember(f"{BUNDLE_NAME}/Contents/MacOS/oChan").mode & stat.S_IXUSR:
            raise PackagingError("The archive lost the executable permission.")


def package_macos(
    publish_directory: Path,
    output_directory: Path,
    version: str,
    icon_path: Path = DEFAULT_ICON,
    archive_path: Path | None = None,
) -> tuple[Path, Path | None]:
    metadata = bundle_metadata(version)
    try:
        source = publish_directory.resolve(strict=True)
    except OSError as error:
        raise PackagingError(f"Publish directory does not exist: {publish_directory}") from error
    if not source.is_dir():
        raise PackagingError("Publish input must be a directory.")
    destination = output_directory.resolve() / BUNDLE_NAME
    if path_present(destination):
        raise PackagingError(f"Refusing to overwrite existing bundle: {destination}")
    if destination.is_relative_to(source) or source.is_relative_to(destination):
        raise PackagingError("Publish input and application output must not overlap.")
    archive_destination: Path | None = None
    if archive_path is not None:
        if path_present(archive_path):
            raise PackagingError(f"Refusing to overwrite existing archive: {archive_path}")
        archive_destination = archive_path.resolve()
        if not archive_destination.name.endswith((".tar.gz", ".tgz")):
            raise PackagingError("Archive destination must end in .tar.gz or .tgz.")
        if (
            archive_destination.is_relative_to(source)
            or archive_destination.is_relative_to(destination)
            or destination.is_relative_to(archive_destination)
        ):
            raise PackagingError("Archive output must be outside the publish input and application bundle.")
    validate_publish_tree(source)
    original_png = png_from_ico(icon_path)
    destination.parent.mkdir(parents=True, exist_ok=True)
    if archive_destination is not None:
        archive_destination.parent.mkdir(parents=True, exist_ok=True)
    created_bundle = False
    created_archive = False
    try:
        with tempfile.TemporaryDirectory(prefix=".ochan-macos-", dir=destination.parent) as staging:
            staged_bundle = Path(staging) / BUNDLE_NAME
            contents = staged_bundle / "Contents"
            contents.mkdir(parents=True)
            shutil.copytree(source, contents / "MacOS", symlinks=True)
            resources = contents / "Resources"
            resources.mkdir()
            (resources / "ochan.icns").write_bytes(make_icns(original_png))
            (contents / "Info.plist").write_bytes(plistlib.dumps(metadata, fmt=plistlib.FMT_XML, sort_keys=True))
            executable = contents / "MacOS" / "oChan"
            executable.chmod(executable.stat().st_mode | stat.S_IXUSR)
            validate_bundle(staged_bundle, metadata, original_png)
            staged_archive = Path(staging) / "application.tar.gz"
            if archive_destination is not None:
                write_archive(staged_bundle, staged_archive)
            # Exclusive mkdir reserves the final name without replacing existing data.
            destination.mkdir()
            created_bundle = True
            (staged_bundle / "Contents").rename(destination / "Contents")
            if archive_destination is not None:
                # Exclusive creation works even when archive and bundle are on different volumes.
                with archive_destination.open("xb") as archive_output:
                    created_archive = True
                    with staged_archive.open("rb") as archive_input:
                        shutil.copyfileobj(archive_input, archive_output)
            return destination, archive_destination
    except BaseException:
        if created_archive and archive_destination is not None:
            archive_destination.unlink()
        if created_bundle:
            shutil.rmtree(destination)
        raise


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--publish-dir", required=True, type=Path)
    parser.add_argument("--output-dir", required=True, type=Path)
    parser.add_argument("--version", required=True)
    parser.add_argument("--icon", default=DEFAULT_ICON, type=Path)
    parser.add_argument("--archive", type=Path)
    arguments = parser.parse_args()
    try:
        bundle, archive = package_macos(arguments.publish_dir, arguments.output_dir, arguments.version, arguments.icon, arguments.archive)
    except (OSError, ValueError, tarfile.TarError) as error:
        parser.exit(1, f"Packaging failed: {error}\n")
    print(f"Created unsigned bundle: {bundle}")
    if archive is not None:
        print(f"Created archive: {archive}")
    print(f"Minimum macOS {MINIMUM_MACOS_VERSION} is a candidate baseline; native launch verification remains required.")
    return 0


if __name__ == "__main__":
    sys.exit(main())
