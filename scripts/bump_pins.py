#!/usr/bin/env python3
"""Re-pin a release VideoJaNai packages: scripts/bump_pins.py <source> <tag>.

Sources:
  libaji          the-database/animejanai-inference release: AjiVersion and its assets' SHA-256s
                  (ajiSha256, both platforms) in BuildVideoJaNai/Program.cs
  runtime-packs   the shared runtime packs, a the-database/animejanai-inference runtime-<n> release:
                  RuntimeTag and the platforms' index SHA-256s (RuntimeIndexSha256Win/Linux)
  aji-runtime     the-database/aji-runtime release (the pack store's tool): AjiRuntimeVersion,
                  AjiRuntimeSha256Win/Linux
  rife-models     a the-database/animejanai-inference models-rife-* release (by hand; no upstream
                  workflow sends it): RifeModelsVersion, RifeModelsAsset (its one .7z), RifeModelsSha256

.github/workflows/bump-pins.yml runs this when animejanai-inference sends a repository_dispatch,
then opens a PR; it also runs by hand. Each pinned asset is downloaded to record its SHA-256. --out DIR writes the PR title and body. Standard
library only.
"""
import argparse
import hashlib
import json
import os
import re
import subprocess
import sys
import urllib.error
import urllib.request
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
UA = "janai-bump-pins"
CHECKED = []  # assets confirmed on the release (downloaded or not)


class Fail(Exception):
    pass


def gh(path):
    """GET api.github.com/<path> (GH_TOKEN, when set, only raises the rate limit)."""
    headers = {"Accept": "application/vnd.github+json", "User-Agent": UA}
    token = os.environ.get("GH_TOKEN") or os.environ.get("GITHUB_TOKEN")
    if token:
        headers["Authorization"] = f"Bearer {token}"
    req = urllib.request.Request(f"https://api.github.com/{path}", headers=headers)
    try:
        with urllib.request.urlopen(req, timeout=60) as r:
            return json.load(r)
    except urllib.error.HTTPError as e:
        if e.code == 404:
            return None
        raise


def release(repo, tag):
    rel = gh(f"repos/{repo}/releases/tags/{tag}")
    if rel is None:
        raise Fail(f"{repo} has no published release {tag}")
    if rel.get("draft"):
        raise Fail(f"{repo} {tag} is still a draft; its assets 404 for the builds")
    return rel


def asset(rel, name):
    for a in rel["assets"]:
        if a["name"] == name:
            CHECKED.append(name)
            return a
    have = ", ".join(a["name"] for a in rel["assets"]) or "none"
    raise Fail(f"{name} isn't on {rel['tag_name']} (it has: {have}). Upload it, then re-run.")


def digest(a):
    """Download a release asset; return (sha256, size). Cross-checks GitHub's recorded digest."""
    print(f"  downloading {a['name']} ({a['size']:,} bytes)", flush=True)
    req = urllib.request.Request(a["browser_download_url"], headers={"User-Agent": UA})
    h, n = hashlib.sha256(), 0
    with urllib.request.urlopen(req, timeout=600) as r:
        while chunk := r.read(1 << 20):
            h.update(chunk)
            n += len(chunk)
    sha = h.hexdigest()
    if n != a["size"]:
        raise Fail(f"{a['name']}: downloaded {n} bytes, the release says {a['size']}")
    if a.get("digest") and a["digest"] != f"sha256:{sha}":
        raise Fail(f"{a['name']}: SHA-256 {sha} doesn't match GitHub's {a['digest']}")
    return sha, n


def version_key(tag):
    """Comparable key for v1.2.3 / 1.2.3 / runtime-4 / 2026-10-24-<hash> tags."""
    m = re.match(r"^(\d{4})-(\d{2})-(\d{2})-", tag)
    if m:
        return tuple(int(x) for x in m.groups())
    return tuple(int(x) for x in re.findall(r"\d+", tag))


class File:
    """One file's text, edited in place by labelled one-match substitutions. Keeps the file's
    bytes otherwise (BOM, CRLF)."""

    def __init__(self, rel, changes):
        self.rel = rel
        self.path = ROOT / rel
        self.orig = self.text = self.path.read_bytes().decode("utf-8")
        self.changes = changes

    def _match(self, label, pattern):
        found = list(re.finditer(pattern, self.text, re.M))
        if len(found) != 1:
            raise Fail(f"{label}: expected one match of {pattern!r} in {self.rel}, found {len(found)}")
        return found[0]

    def get(self, label, pattern):
        return self._match(label, pattern).group(1)

    def set(self, label, pattern, value):
        """`pattern`'s group 1 is the pinned value."""
        m = self._match(label, pattern)
        old = m.group(1)
        if old != value:
            self.text = self.text[: m.start(1)] + value + self.text[m.end(1):]
        self.changes.append((self.rel, label, old, value))
        return old

    def save(self):
        if self.text != self.orig:
            self.path.write_bytes(self.text.encode("utf-8"))


def guard_downgrade(what, old, new, allow):
    if version_key(new) < version_key(old) and not allow:
        raise Fail(f"{what}: {new} is older than the pinned {old} (pass --allow-downgrade to pin it anyway)")


def rust_int(n):
    return f"{n:_}"


def run(sources, repo_name):
    p = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    p.add_argument("source", choices=sorted(sources))
    p.add_argument("tag", help="the upstream release tag (yosh-image: any yosh ref)")
    p.add_argument("--out", type=Path, help="write title.txt and body.md here when something changed")
    p.add_argument("--allow-downgrade", action="store_true")
    a = p.parse_args()
    if not re.fullmatch(r"[A-Za-z0-9][A-Za-z0-9._-]{0,99}", a.tag):
        sys.exit(f"error: not a tag: {a.tag!r}")
    changes = []
    try:
        files, title, notes = sources[a.source](a.tag, changes, a.allow_downgrade)
    except Fail as e:
        sys.exit(f"error: {e}")
    for f in files:
        f.save()
    edited = [c for c in changes if c[2] != c[3]]
    for rel, label, old, new in changes:
        print(f"{'changed' if old != new else 'same   '}  {rel}: {label}: {old} -> {new}")
    if not edited:
        print(f"{repo_name} already pins {a.source} {a.tag}; nothing to do")
        return
    if a.out:
        a.out.mkdir(parents=True, exist_ok=True)
        (a.out / "title.txt").write_text(title + "\n", encoding="utf-8")
        rows = "\n".join(f"| `{rel}` | {label} | `{old}` | `{new}` |" for rel, label, old, new in edited)
        checked = ""
        if CHECKED:
            checked = (" Assets confirmed on the release: " + ", ".join(f"`{n}`" for n in CHECKED)
                       + ". Every SHA-256 and size above was computed from the downloaded file.")
        body = (
            f"{notes}\n\n| File | Pin | Old | New |\n|---|---|---|---|\n{rows}\n\n"
            f"Opened by `.github/workflows/bump-pins.yml` (`scripts/bump_pins.py {a.source} {a.tag}`)."
            f"{checked} A newer release of the same source updates this PR instead of opening "
            "another. Review and merge by hand.\n"
        )
        (a.out / "body.md").write_text(body, encoding="utf-8")


AJI_VERSION = r'const string AjiVersion\s*=\s*"([^"]+)";'


def bump_libaji(tag, changes, allow_downgrade):
    if not re.fullmatch(r"v\d+\.\d+\.\d+", tag):
        raise Fail(f"{tag} isn't a libaji release tag (vX.Y.Z)")
    f = File("BuildVideoJaNai/Program.cs", changes)
    guard_downgrade("AjiVersion", f.get("AjiVersion", AJI_VERSION), tag, allow_downgrade)
    rel = release("the-database/animejanai-inference", tag)
    f.set("AjiVersion", AJI_VERSION, tag)
    for name in ("aji-windows-x64.zip", "aji-trtrtx-windows-x64.zip",
                 "aji-linux-x64.tar.zst", "aji-encode-linux-x64.tar.zst"):
        sha, _ = digest(asset(rel, name))
        f.set(f"ajiSha256 {name}", rf'\["{re.escape(name)}"\]\s*=\s*"([0-9a-f]{{64}})"', sha)
    notes = (
        f"Pins [libaji {tag}]({rel['html_url']}). Add a line for it to the release history in the "
        "comment above `AjiVersion` (what VideoJaNai gets from it). A change to `aji_encode`'s "
        "arguments or output needs changes in VideoJaNai.Core first. To test it, build with "
        "`BuildVideoJaNai` and upscale a clip, or dispatch `Deploy` from this branch."
    )
    return [f], f"Bump libaji to {tag}", notes


def const(name, value=r"[^\"]+"):
    return rf'const string {name}\s*=\s*"({value})";'


SHA = "[0-9a-f]{64}"


def bump_runtime(tag, changes, allow_downgrade):
    if not re.fullmatch(r"runtime-\d+", tag):
        raise Fail(f"{tag} isn't a runtime-<n> tag")
    f = File("BuildVideoJaNai/Program.cs", changes)
    guard_downgrade("RuntimeTag", f.get("RuntimeTag", const("RuntimeTag")), tag, allow_downgrade)
    rel = release("the-database/animejanai-inference", tag)
    f.set("RuntimeTag", const("RuntimeTag"), tag)
    for name, pin in (("packs-windows-x64.json", "RuntimeIndexSha256Win"),
                      ("packs-linux-x64.json", "RuntimeIndexSha256Linux")):
        sha, _ = digest(asset(rel, name))
        f.set(pin, const(pin, SHA), sha)
    notes = (
        f"Pins the shared runtime packs on [{tag}]({rel['html_url']}) (animejanai-inference "
        "`runtime-packs.yml`): TensorRT, the per-GPU builder packs and TensorRT-RTX's runtime, which "
        "the updater installs into the shared pack store. Each `packs-<platform>.json` is checked "
        "against these SHA-256s, and it pins every pack. After the update, `--auto` installs the new "
        "versions; the old ones go once no other app uses them. The component packs this release "
        "still builds (for the AppImage, the offline package and older updaters) come from "
        "`TrtVersion` and friends: bump those to the same TensorRT."
    )
    return [f], f"Bump the runtime packs to {tag}", notes


def bump_aji_runtime(tag, changes, allow_downgrade):
    if not re.fullmatch(r"v\d+\.\d+\.\d+", tag):
        raise Fail(f"{tag} isn't an aji-runtime release tag (vX.Y.Z)")
    f = File("BuildVideoJaNai/Program.cs", changes)
    guard_downgrade("AjiRuntimeVersion", f.get("AjiRuntimeVersion", const("AjiRuntimeVersion")), tag, allow_downgrade)
    rel = release("the-database/aji-runtime", tag)
    f.set("AjiRuntimeVersion", const("AjiRuntimeVersion"), tag)
    for name, pin in (("aji-runtime-windows-x64.zip", "AjiRuntimeSha256Win"),
                      ("aji-runtime-linux-x64.tar.gz", "AjiRuntimeSha256Linux")):
        sha, _ = digest(asset(rel, name))
        f.set(pin, const(pin, SHA), sha)
    notes = (
        f"Pins [aji-runtime {tag}]({rel['html_url']}), the shared pack store's tool that ships beside "
        "the updater. Check its release notes for changes to the commands the updater runs (`ensure`, "
        "`show`, `unref`)."
    )
    return [f], f"Bump aji-runtime to {tag}", notes


def bump_rife(tag, changes, allow_downgrade):
    if not re.fullmatch(r"models-rife-[A-Za-z0-9._-]+", tag):
        raise Fail(f"{tag} isn't a RIFE model release tag (models-rife-<set>)")
    f = File("BuildVideoJaNai/Program.cs", changes)
    guard_downgrade("RifeModelsVersion", f.get("RifeModelsVersion", const("RifeModelsVersion")), tag, allow_downgrade)
    rel = release("the-database/animejanai-inference", tag)
    archives = [a["name"] for a in rel["assets"] if a["name"].endswith(".7z")]
    if len(archives) != 1:
        raise Fail(f"{tag} should carry one .7z model archive, found {len(archives)}")
    f.set("RifeModelsVersion", const("RifeModelsVersion"), tag)
    f.set("RifeModelsAsset", const("RifeModelsAsset"), archives[0])
    sha, _ = digest(asset(rel, archives[0]))
    f.set("RifeModelsSha256", const("RifeModelsSha256", SHA), sha)
    notes = (
        f"Pins the RIFE models [{tag}]({rel['html_url']}). Update the comment above `RifeModelsVersion` "
        "by hand. New file names mean new TensorRT engines on first use. To test it, build with "
        "`BuildVideoJaNai` and upscale a clip with RIFE on."
    )
    return [f], f"Bump the RIFE models to {tag}", notes


SOURCES = {"libaji": bump_libaji, "runtime-packs": bump_runtime, "aji-runtime": bump_aji_runtime,
           "rife-models": bump_rife}

if __name__ == "__main__":
    run(SOURCES, "VideoJaNai")
