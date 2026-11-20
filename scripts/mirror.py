#!/usr/bin/env python3
"""
Mirror z3y/glim GitHub releases into this repo as a Unity (UPM) package.

For every upstream release that has no matching tag here, it:
  1. downloads the release's package zip (io.github.z3y.glim-<version>.zip),
  2. replaces the contents of the `upm` branch with it (package.json at the root),
  3. commits and tags it with the upstream tag (e.g. v0.13.0).

Each mirrored version is patched so Glim works from a read-only git (UPM) package: its temp
bake scene is moved to Assets/Plugins/Glim (see PATCHES below).

Options:
  --repatch   apply the patch to the current upm tip (versions mirrored before the patch existed)
  --all      mirror every missing upstream release (oldest first), not just the latest
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


# ---------------------------------------------------------------------------------------------
# Read-only package patch.
# Glim bakes by opening and saving a temp scene (and its LightingData.asset) inside its own
# package folder. Packages installed from a git URL are read-only, so Unity refuses
# ("It is not allowed to open a scene in a read-only package"). This moves those two files to
# a writable folder in the project: Assets/Plugins/Glim. The temp scene is copied there from
# the package the first time a bake runs.
# Every replacement must match exactly; if upstream changes this code the mirror fails loudly
# instead of publishing a broken package.
PATCH_MARKER = "GLIM-UPM PATCH"
WRITABLE_DIR = "Assets/Plugins/Glim"

PATCHES = {
    "Editor/LightingData.cs": [
        (
            '        public const string TempScenePath = "Packages/io.github.z3y.glim/Editor/Scene/Temp.unity";\n'
            '        public const string TempLightingDataPath = "Packages/io.github.z3y.glim/Editor/Scene/Temp/LightingData.asset";\n',
            f'        // {PATCH_MARKER}: temp bake scene lives in the project, because git (UPM) packages are read-only.\n'
            f'        public const string TempScenePath = "{WRITABLE_DIR}/Temp.unity";\n'
            f'        public const string TempLightingDataPath = "{WRITABLE_DIR}/Temp/LightingData.asset";\n'
            '        const string PackageTempScenePath = "Packages/io.github.z3y.glim/Editor/Scene/Temp.unity";\n'
            '\n'
            f'        // {PATCH_MARKER}: copy the package\'s empty temp scene into the project on first use.\n'
            '        static void EnsureWritableTempScene()\n'
            '        {\n'
            '            if (AssetDatabase.LoadAssetAtPath<SceneAsset>(TempScenePath) != null)\n'
            '            {\n'
            '                return;\n'
            '            }\n'
            '\n'
            '            string folder = "Assets";\n'
            f'            foreach (var part in "{WRITABLE_DIR}".Substring("Assets/".Length).Split(\'/\'))\n'
            '            {\n'
            '                string next = folder + "/" + part;\n'
            '                if (!AssetDatabase.IsValidFolder(next))\n'
            '                {\n'
            '                    AssetDatabase.CreateFolder(folder, part);\n'
            '                }\n'
            '                folder = next;\n'
            '            }\n'
            '\n'
            '            if (!AssetDatabase.CopyAsset(PackageTempScenePath, TempScenePath))\n'
            '            {\n'
            '                throw new System.Exception($"Glim: could not copy {PackageTempScenePath} to {TempScenePath}");\n'
            '            }\n'
            '        }\n',
        ),
        (
            '            Scene tempScene = EditorSceneManager.OpenScene(TempScenePath, OpenSceneMode.Additive);\n',
            f'            EnsureWritableTempScene(); // {PATCH_MARKER}\n'
            '            Scene tempScene = EditorSceneManager.OpenScene(TempScenePath, OpenSceneMode.Additive);\n',
        ),
    ],
}


def apply_patches(package_dir):
    for rel, replacements in PATCHES.items():
        path = package_dir / rel
        raw = path.read_bytes()
        crlf = b"\r\n" in raw
        text = raw.decode("utf-8").replace("\r\n", "\n")
        if PATCH_MARKER in text:
            continue  # already patched
        for old, new in replacements:
            if text.count(old) != 1:
                raise RuntimeError(f"Patch for {rel} no longer matches upstream code:\n{old}")
            text = text.replace(old, new)
        if crlf:
            text = text.replace("\n", "\r\n")
        path.write_bytes(text.encode("utf-8"))

    pkg_path = package_dir / "package.json"
    pkg = json.loads(pkg_path.read_text(encoding="utf-8"))
    note = f" (UPM mirror: temp bake scene moved to {WRITABLE_DIR} so it works from a git URL)"
    if note not in pkg.get("description", ""):
        pkg["description"] = pkg.get("description", "") + note
        pkg_path.write_text(json.dumps(pkg, indent=2) + "\n", encoding="utf-8")


def make_zip(ref, name):
    out = ROOT / ".mirror-cache" / name
    out.parent.mkdir(exist_ok=True)
    git("archive", "--format=zip", "-o", str(out), ref)
    return out.name


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

    apply_patches(worktree)
    version = json.loads((worktree / "package.json").read_text(encoding="utf-8"))["version"]
    # --force: upstream ships .gitignore files that exclude the native glim.dll / glim.so,
    # which the package needs. Commit everything that was in the release zip.
    git("add", "--all", "--force", ".", cwd=worktree)
    git("-c", "user.name=glim-upm-mirror", "-c", "user.email=glim-upm-mirror@users.noreply.github.com",
        "commit", "--allow-empty", "-m", f"Glim {version} (mirror of {UPSTREAM} {tag})", cwd=worktree)
    git("-c", "user.name=glim-upm-mirror", "-c", "user.email=glim-upm-mirror@users.noreply.github.com",
        "tag", "-a", tag, "-m", f"Glim {version}", cwd=worktree)
    print(f"  {tag}: committed to '{BRANCH}' and tagged")
    return tag, make_zip(tag, asset["name"]), release


def repatch(worktree):
    """Apply PATCHES to the current tip of the upm branch (for versions mirrored before a
    patch existed). Commits on top and moves that version's tag to the new commit."""
    apply_patches(worktree)
    git("add", "--all", "--force", ".", cwd=worktree)
    if not git("status", "--porcelain", cwd=worktree, capture=True):
        print("upm branch already patched.")
        return None
    version = json.loads((worktree / "package.json").read_text(encoding="utf-8"))["version"]
    tag = f"v{version}"
    ident = ["-c", "user.name=glim-upm-mirror", "-c", "user.email=glim-upm-mirror@users.noreply.github.com"]
    git(*ident, "commit", "-m", f"Glim {version}: read-only package patch ({WRITABLE_DIR})", cwd=worktree)
    git(*ident, "tag", "-a", "-f", tag, "-m", f"Glim {version} (patched)", cwd=worktree)
    print(f"Patched upm and moved tag {tag}")
    return tag


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--all", action="store_true")
    ap.add_argument("--push", action="store_true")
    ap.add_argument("--release", action="store_true")
    ap.add_argument("--repatch", action="store_true",
                    help="apply the read-only package patch to the existing upm tip and move its tag")
    args = ap.parse_args()

    if args.repatch:
        worktree = Path(tempfile.mkdtemp(prefix="glim-upm-"))
        shutil.rmtree(worktree)
        git("worktree", "add", str(worktree), BRANCH)
        try:
            tag = repatch(worktree)
        finally:
            git("worktree", "remove", "--force", str(worktree))
        if tag and args.push:
            git("push", "origin", BRANCH)
            git("push", "--force", "origin", f"refs/tags/{tag}")
        return 0

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
