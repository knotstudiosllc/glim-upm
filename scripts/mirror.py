#!/usr/bin/env python3
"""
Mirror z3y/glim GitHub releases into this repo as a Unity (UPM) package.

For every upstream release that has no matching tag here, it:
  1. downloads the release's package zip (io.github.z3y.glim-<version>.zip),
  2. replaces the contents of the `upm` branch with it (package.json at the root),
  3. commits and tags it with the upstream tag (e.g. v0.13.0).

Options:
  --all       mirror every missing upstream release (oldest first), not just the latest
  --push      push the `upm` branch and new tags to `origin`
  --release   create a GitHub release for each new tag (needs the `gh` CLI; used in CI)

Runs the same locally (Python 3.8+ and git) and in GitHub Actions.
Set GITHUB_TOKEN to avoid API rate limits.
"""
import argparse
import json
import os
import shutil
import subprocess
import sys
import tempfile
import urllib.request
import zipfile
from pathlib import Path

UPSTREAM = "z3y/glim"
PACKAGE_NAME = "io.github.z3y.glim"
BRANCH = "upm"
ROOT = Path(__file__).resolve().parent.parent


def git(*args, cwd=ROOT, capture=False):
    # autocrlf off: store upstream files byte-for-byte on every OS.
    result = subprocess.run(["git", "-c", "core.autocrlf=false", *args], cwd=cwd, check=True, text=True,
                            stdout=subprocess.PIPE if capture else None)
    return result.stdout.strip() if capture else None


def api(url):
    req = urllib.request.Request(url, headers={"Accept": "application/vnd.github+json",
                                               "User-Agent": "glim-upm-mirror"})
    token = os.environ.get("GITHUB_TOKEN")
    if token:
        req.add_header("Authorization", f"Bearer {token}")
    with urllib.request.urlopen(req) as r:
        return json.load(r)


def download(url, dest):
    req = urllib.request.Request(url, headers={"User-Agent": "glim-upm-mirror",
                                               "Accept": "application/octet-stream"})
    with urllib.request.urlopen(req) as r, open(dest, "wb") as f:
        shutil.copyfileobj(r, f)


def upstream_releases():
    releases = api(f"https://api.github.com/repos/{UPSTREAM}/releases?per_page=100")
    releases = [r for r in releases if not r["draft"] and not r["prerelease"]]
    releases.sort(key=lambda r: r["published_at"])  # oldest first
    return releases


def package_asset(release):
    for a in release["assets"]:
        if a["name"].startswith(PACKAGE_NAME) and a["name"].endswith(".zip"):
            return a
    return None


def branch_exists(name):
    local = subprocess.run(["git", "rev-parse", "--verify", "--quiet", f"refs/heads/{name}"],
                           cwd=ROOT, stdout=subprocess.DEVNULL).returncode == 0
    if local:
        return True
    remote = subprocess.run(["git", "rev-parse", "--verify", "--quiet", f"refs/remotes/origin/{name}"],
                            cwd=ROOT, stdout=subprocess.DEVNULL).returncode == 0
    if remote:
        git("branch", name, f"origin/{name}")
    return remote


def extract_package(zip_path, dest):
    with zipfile.ZipFile(zip_path) as z:
        z.extractall(dest)
    # Upstream zips have package.json at the root; handle a single wrapping folder just in case.
    if not (dest / "package.json").exists():
        subdirs = [p for p in dest.iterdir() if p.is_dir() and p.name != ".git"]
        if len(subdirs) == 1 and (subdirs[0] / "package.json").exists():
            inner = subdirs[0]
            for item in inner.iterdir():
                shutil.move(str(item), dest / item.name)
            inner.rmdir()
    if not (dest / "package.json").exists():
        raise RuntimeError("package.json not found in upstream zip")


def mirror(release, worktree):
    tag = release["tag_name"]
    asset = package_asset(release)
    if asset is None:
        print(f"  {tag}: no {PACKAGE_NAME}*.zip asset, skipping")
        return None

    # Clear the branch contents (keep .git file of the worktree).
    for item in worktree.iterdir():
        if item.name == ".git":
            continue
        shutil.rmtree(item) if item.is_dir() else item.unlink()

    with tempfile.TemporaryDirectory() as tmp:
        zip_path = Path(tmp) / asset["name"]
        print(f"  {tag}: downloading {asset['name']}")
        download(asset["browser_download_url"], zip_path)
        extract_package(zip_path, worktree)
        keep_zip = ROOT / ".mirror-cache" / asset["name"]
        keep_zip.parent.mkdir(exist_ok=True)
        shutil.copy(zip_path, keep_zip)

    version = json.loads((worktree / "package.json").read_text(encoding="utf-8"))["version"]
    # --force: upstream ships .gitignore files that exclude the native glim.dll / glim.so,
    # which the package needs. Commit everything that was in the release zip.
    git("add", "--all", "--force", ".", cwd=worktree)
    git("-c", "user.name=glim-upm-mirror", "-c", "user.email=glim-upm-mirror@users.noreply.github.com",
        "commit", "--allow-empty", "-m", f"Glim {version} (mirror of {UPSTREAM} {tag})", cwd=worktree)
    git("-c", "user.name=glim-upm-mirror", "-c", "user.email=glim-upm-mirror@users.noreply.github.com",
        "tag", "-a", tag, "-m", f"Glim {version}", cwd=worktree)
    print(f"  {tag}: committed to '{BRANCH}' and tagged")
    return tag, asset["name"], release


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--all", action="store_true")
    ap.add_argument("--push", action="store_true")
    ap.add_argument("--release", action="store_true")
    args = ap.parse_args()

    existing = set(git("tag", "--list", capture=True).split())
    releases = upstream_releases()
    if not args.all:
        releases = releases[-1:]
    todo = [r for r in releases if r["tag_name"] not in existing]
    if not todo:
        print("Up to date.")
        return 0

    worktree = Path(tempfile.mkdtemp(prefix="glim-upm-"))
    shutil.rmtree(worktree)
    if branch_exists(BRANCH):
        git("worktree", "add", str(worktree), BRANCH)
    else:
        git("worktree", "add", "--detach", str(worktree))
        git("checkout", "--orphan", BRANCH, cwd=worktree)
        git("rm", "-rf", "--quiet", ".", cwd=worktree)

    done = []
    try:
        for r in todo:
            result = mirror(r, worktree)
            if result:
                done.append(result)
    finally:
        git("worktree", "remove", "--force", str(worktree))

    if done and args.push:
        git("push", "origin", BRANCH, *[t for t, _, _ in done])

    if done and args.release:
        for tag, asset_name, r in done:
            notes = (f"Unity package mirror of [{UPSTREAM} {tag}]({r['html_url']}).\n\n"
                     f"---\n\n{r.get('body') or ''}")
            subprocess.run(["gh", "release", "create", tag, str(ROOT / ".mirror-cache" / asset_name),
                            "--title", f"Glim {tag}", "--notes", notes, "--verify-tag"],
                           cwd=ROOT, check=True)

    print("Mirrored:", ", ".join(t for t, _, _ in done) or "nothing")
    return 0


if __name__ == "__main__":
    sys.exit(main())
