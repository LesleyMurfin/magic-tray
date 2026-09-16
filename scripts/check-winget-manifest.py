#!/usr/bin/env python3
"""Checks the winget manifest set Magic Tray ships in packaging/winget.

The three YAML files per version folder have to parse, agree with the folder
name, and carry the keys microsoft/winget-pkgs will be asked to accept. This is
not `winget validate` - that is Windows only, and the submission workflow
(.github/workflows/winget-submit.yml) still runs it on windows-latest. This
script is the cheap PR gate: it reads files, touches no network, and every
failure is a GitHub annotation (::error file=<path>,line=<n>::<message>) so it
lands on the offending key in the diff.

Every Installers entry is checked, not just the first: the schema allows a
per-architecture entry to override a root-level value, so a second entry with a
bad digest or nested path must not be able to hide behind a valid first one.

Usage:
    python3 scripts/check-winget-manifest.py
    python3 scripts/check-winget-manifest.py --repo-root /path/to/magic-tray

Requires PyYAML. Exits non-zero if anything is wrong.
"""

from __future__ import annotations

import argparse
import os
import pathlib
import re
import sys

try:
    import yaml
except ModuleNotFoundError:
    sys.exit("check-winget-manifest.py needs PyYAML. Install it with:\n"
             "    python3 -m pip install 'PyYAML==6.0.3'\n"
             "(the Winget manifest job installs that same pinned version into a throwaway venv).")

IDENTIFIER = "LesleyMurfin.MagicTray"
MANIFEST_SCHEMA = "1.12.0"
MANIFEST_ROOT = "packaging/winget/manifests/l/LesleyMurfin/MagicTray"
NESTED_EXE = "MagicMouseTray.exe"
# The manifests ship a 64-zero digest on purpose: it satisfies the schema's
# ^[A-Fa-f0-9]{64}$ while being obviously not a real hash. winget-submit.yml
# patches in the published ZIP's digest.
PLACEHOLDER_SHA = "0" * 64
SHA256 = re.compile(r"[0-9A-Fa-f]{64}")

# What the 1.12.0 schema marks required for each manifest in the set. Checking
# only the keys this script has an opinion about would let a manifest missing
# DefaultLocale or Publisher through a gate whose whole claim is "carries the
# keys winget-pkgs will be asked to accept".
SCHEMA_REQUIRED = {
    "version": ("PackageIdentifier", "PackageVersion", "DefaultLocale",
                "ManifestType", "ManifestVersion"),
    "installer": ("PackageIdentifier", "PackageVersion", "Installers",
                  "ManifestType", "ManifestVersion"),
    "locale": ("PackageIdentifier", "PackageVersion", "PackageLocale", "Publisher",
               "PackageName", "License", "ShortDescription", "ManifestType",
               "ManifestVersion"),
}
# Required by every installer entry, wherever the entry sits.
SCHEMA_REQUIRED_INSTALLER_ENTRY = ("Architecture", "InstallerUrl")
# NOT in the schema's required set. Magic Tray's listing carries it deliberately,
# so the gate holds us to it - but the message must not claim winget demands it.
HOUSE_REQUIRED_LOCALE = ("PublisherUrl",)


def line_of(path: pathlib.Path, key: str | None) -> int:
    """First line declaring `key`, so the annotation lands on it."""
    if not key:
        return 1
    pattern = re.compile(r"^\s*-?\s*" + re.escape(key) + r"\s*:")
    for number, text in enumerate(path.read_text(encoding="utf-8").splitlines(), 1):
        if pattern.match(text):
            return number
    return 1


def fail(problems: list[str], path: pathlib.Path, key: str | None, message: str) -> None:
    """Records a failure and prints it as an annotation on the offending key."""
    problems.append(message)
    print("::error file=%s,line=%d::%s" % (path.as_posix(), line_of(path, key), message))


def check_required(problems: list[str], path: pathlib.Path, document: dict,
                   keys: tuple[str, ...], why: str, where: str = "") -> None:
    """Each key present and non-empty. Absent, null, blank and [] all fail."""
    for key in keys:
        value = document.get(key)
        if value is None or (isinstance(value, (str, list, dict)) and not value) or \
                (isinstance(value, str) and not value.strip()):
            fail(problems, path, key, "%s%s is missing or empty; %s." % (where, key, why))


def check_common(problems: list[str], path: pathlib.Path, document: dict, version: str) -> None:
    """The keys every manifest in the set has to agree on."""
    if document.get("PackageIdentifier") != IDENTIFIER:
        fail(problems, path, "PackageIdentifier",
                      "PackageIdentifier is %r; expected %r."
                      % (document.get("PackageIdentifier"), IDENTIFIER))
    if str(document.get("PackageVersion")) != version:
        fail(problems, path, "PackageVersion",
                      "PackageVersion is %r but the manifest folder is %r."
                      % (document.get("PackageVersion"), version))
    if str(document.get("ManifestVersion")) != MANIFEST_SCHEMA:
        fail(problems, path, "ManifestVersion",
                      "ManifestVersion is %r; the community repository expects %r."
                      % (document.get("ManifestVersion"), MANIFEST_SCHEMA))


def check_installer(problems: list[str], path: pathlib.Path, document: dict) -> None:
    """Every Installers entry, each field read from the entry then the root.

    The schema lets InstallerType, NestedInstallerType, NestedInstallerFiles
    and InstallerSha256 sit at the root or on the entry, with the entry
    winning, so resolve in that order and check each entry on its own.
    """
    entries = document.get("Installers")
    if not isinstance(entries, list) or not entries:
        fail(problems, path, "Installers", "No Installers entry.")
        return

    for index, entry in enumerate(entries):
        if not isinstance(entry, dict):
            fail(problems, path, "Installers", "Installers entry %d is not a mapping." % index)
            continue

        def setting(name: str, entry: dict = entry):
            return entry[name] if name in entry else document.get(name)

        # Names the entry in the message: the annotation can only point at the
        # first line declaring a key, so say which entry actually failed.
        where = "Installers entry %d (%s)" % (index, entry.get("Architecture", "no Architecture"))

        check_required(problems, path, entry, SCHEMA_REQUIRED_INSTALLER_ENTRY,
                       "the 1.12.0 installer schema requires it on every entry",
                       where="%s: " % where)

        if setting("InstallerType") != "zip":
            fail(problems, path, "InstallerType",
                          "%s: InstallerType is %r; Magic Tray ships a portable ZIP, so it must be 'zip'."
                          % (where, setting("InstallerType")))
        if setting("NestedInstallerType") != "portable":
            fail(problems, path, "NestedInstallerType",
                          "%s: NestedInstallerType is %r; expected 'portable'."
                          % (where, setting("NestedInstallerType")))

        nested = setting("NestedInstallerFiles") or []
        relative = [n.get("RelativeFilePath") for n in nested if isinstance(n, dict)]
        if relative != [NESTED_EXE]:
            fail(problems, path, "RelativeFilePath",
                          "%s: NestedInstallerFiles RelativeFilePath list is %r; expected exactly %r, the exe at the root of the ZIP."
                          % (where, relative, [NESTED_EXE]))

        digest = str(setting("InstallerSha256") or "")
        if digest != PLACEHOLDER_SHA and not SHA256.fullmatch(digest):
            fail(problems, path, "InstallerSha256",
                          "%s: InstallerSha256 %r is neither 64 hex characters nor the 64-zero placeholder patched in at submission time."
                          % (where, digest))


def check_locale(problems: list[str], path: pathlib.Path, document: dict) -> None:
    """The keys the public winget listing renders."""
    check_required(problems, path, document, HOUSE_REQUIRED_LOCALE,
                   "Magic Tray's winget listing carries it (the schema treats it as optional)")


def check_set(problems: list[str], folder: pathlib.Path) -> None:
    """One version folder: all three files, then the per-file rules."""
    version = folder.name
    files = {
        "version": folder / ("%s.yaml" % IDENTIFIER),
        "installer": folder / ("%s.installer.yaml" % IDENTIFIER),
        "locale": folder / ("%s.locale.en-US.yaml" % IDENTIFIER),
    }
    parsed = {}

    for kind, path in files.items():
        if not path.is_file():
            fail(problems, folder, None, "Missing %s manifest %s." % (kind, path.name))
            continue
        try:
            document = yaml.safe_load(path.read_text(encoding="utf-8"))
        except yaml.YAMLError as error:
            fail(problems, path, None, "Not valid YAML: %s" % str(error).replace("\n", " "))
            continue
        if not isinstance(document, dict):
            fail(problems, path, None, "Manifest does not parse to a mapping.")
            continue
        parsed[kind] = document
        check_required(problems, path, document, SCHEMA_REQUIRED[kind],
                       "the 1.12.0 %s schema requires it" % kind)
        check_common(problems, path, document, version)

    if "installer" in parsed:
        check_installer(problems, files["installer"], parsed["installer"])
    if "locale" in parsed:
        check_locale(problems, files["locale"], parsed["locale"])

    print("checked manifest set %s" % folder.as_posix())


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument(
        "--repo-root",
        type=pathlib.Path,
        default=pathlib.Path(__file__).resolve().parent.parent,
        help="repository root to check; defaults to the parent of this script's folder",
    )
    # Every path printed from here on is relative to the repository root, which
    # is what a GitHub annotation needs to attach itself to the diff.
    os.chdir(parser.parse_args().repo_root)
    root = pathlib.Path(MANIFEST_ROOT)

    folders = sorted(p for p in root.iterdir() if p.is_dir()) if root.is_dir() else []
    if not folders:
        print("::error file=%s,line=1::No winget manifest version folder found." % root.as_posix())
        return 1

    problems: list[str] = []
    for folder in folders:
        check_set(problems, folder)

    if problems:
        print("winget manifest: %d problem(s) found." % len(problems))
        return 1
    print("winget manifest: OK (%d set(s) checked)." % len(folders))
    return 0


if __name__ == "__main__":
    sys.exit(main())
