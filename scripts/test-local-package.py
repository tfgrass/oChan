#!/usr/bin/env python3
"""Consume a local libochan nupkg through PackageReference and a fresh package cache."""
from __future__ import annotations

import argparse
import hashlib
import json
import os
from pathlib import Path
import subprocess
import tempfile
import xml.etree.ElementTree as ET
import zipfile


def child_text(element: ET.Element, name: str) -> str:
    for child in element:
        if child.tag.rsplit("}", 1)[-1] == name:
            return child.text or ""
    raise ValueError(f"NuGet metadata is missing {name}.")


def inspect_package(package: Path) -> tuple[str, str]:
    with zipfile.ZipFile(package) as archive:
        nuspecs = [name for name in archive.namelist() if name.endswith(".nuspec")]
        if len(nuspecs) != 1:
            raise ValueError("Expected exactly one NuSpec in the candidate.")
        root = ET.fromstring(archive.read(nuspecs[0]))
        metadata = next(item for item in root if item.tag.rsplit("}", 1)[-1] == "metadata")
        package_id, version = child_text(metadata, "id"), child_text(metadata, "version")
        if package_id.lower() != "libochan":
            raise ValueError(f"Unexpected candidate package ID: {package_id}")
        for item in metadata.iter():
            dependency = item.attrib.get("id", "").lower()
            if dependency.startswith(("avalonia", "actipro")) or dependency == "ochan":
                raise ValueError(f"Desktop dependency leaked into library package: {dependency}")
        assemblies = [name for name in archive.namelist() if name.endswith(".dll")]
        if assemblies != ["lib/net10.0/libochan.dll"]:
            raise ValueError(f"Unexpected package assemblies: {assemblies}")
        if not any(name.lower().endswith(("license", "license.txt", "license.md")) for name in archive.namelist()):
            raise ValueError("Package must include its project license.")
        readme = child_text(metadata, "readme")
        if readme not in archive.namelist():
            raise ValueError("Package metadata must identify its included README.")
        return package_id, version


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--feed", required=True)
    parser.add_argument("--consumer", required=True)
    args = parser.parse_args()
    feed = Path(args.feed).resolve(strict=True)
    consumer = Path(args.consumer).resolve(strict=True)
    candidates = sorted(feed.glob("libochan.*.nupkg"))
    if len(candidates) != 1:
        raise ValueError(f"Expected one unambiguous libochan candidate, found {len(candidates)}.")
    package = candidates[0]
    package_id, version = inspect_package(package)
    candidate_hash = hashlib.sha256(package.read_bytes()).hexdigest()
    with tempfile.TemporaryDirectory(prefix="ochan-package-consumer-") as directory:
        temporary = Path(directory)
        cache = temporary / "packages"
        config = ET.Element("configuration")
        sources = ET.SubElement(config, "packageSources")
        ET.SubElement(sources, "clear")
        ET.SubElement(sources, "add", key="candidate", value=str(feed))
        ET.SubElement(sources, "add", key="nuget.org", value="https://api.nuget.org/v3/index.json")
        mapping = ET.SubElement(config, "packageSourceMapping")
        ET.SubElement(mapping, "clear")
        ET.SubElement(ET.SubElement(mapping, "packageSource", key="candidate"), "package", pattern=package_id)
        ET.SubElement(ET.SubElement(mapping, "packageSource", key="nuget.org"), "package", pattern="*")
        config_path = temporary / "NuGet.Config"
        ET.ElementTree(config).write(config_path, encoding="utf-8", xml_declaration=True)
        environment = dict(os.environ)
        environment["NUGET_PACKAGES"] = str(cache)
        environment["NUGET_HTTP_CACHE_PATH"] = str(temporary / "http-cache")
        version_property = "-p:LibOChanVersion=" + version
        def run(arguments: list[str]) -> None:
            subprocess.run(arguments, cwd=consumer.parent, env=environment, check=True, timeout=240)
        run(["dotnet", "restore", str(consumer), "--configfile", str(config_path),
             "--packages", str(cache), "-warnaserror:NU1900", version_property])
        assets = json.loads((consumer.parent / "obj" / "project.assets.json").read_text())
        for name, item in assets["libraries"].items():
            dependency_id = name.rsplit("/", 1)[0].lower()
            if item["type"] == "project":
                raise ValueError(f"Consumer has a forbidden ProjectReference: {name}")
            if dependency_id.startswith(("avalonia", "actipro")) or dependency_id == "ochan":
                raise ValueError(f"Desktop dependency leaked into consumer: {name}")
        restored_entry = next((item for name, item in assets["libraries"].items()
                               if name.rsplit("/", 1)[0].lower() == package_id.lower()), None)
        if restored_entry is None:
            raise ValueError("Consumer did not resolve libochan.")
        restored_directory = (cache / restored_entry["path"]).resolve(strict=True)
        if not restored_directory.is_relative_to(cache.resolve()):
            raise ValueError("Package was restored outside the isolated cache.")
        restored_packages = list(restored_directory.glob("*.nupkg"))
        if len(restored_packages) != 1 or hashlib.sha256(restored_packages[0].read_bytes()).hexdigest() != candidate_hash:
            raise ValueError("The restored package does not exactly match the local candidate.")
        run(["dotnet", "build", str(consumer), "-c", "Release", "--no-restore", "-t:Rebuild", version_property])
        run(["dotnet", "run", "--project", str(consumer), "-c", "Release", "--no-restore", "--no-build", version_property])
        evidence = {"package_id": package_id, "version": version, "sha256": candidate_hash,
                    "fresh_cache": True, "exact_package_match": True,
                    "desktop_dependencies": False, "consumer_exit_code": 0}
        (feed / "consumer-evidence.json").write_text(json.dumps(evidence, indent=2) + "\n")
        print(json.dumps(evidence, indent=2))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
