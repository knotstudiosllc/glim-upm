# Glim UPM mirror

Unofficial Unity Package Manager mirror of [z3y/glim](https://github.com/z3y/glim) (MIT).
A GitHub Action checks upstream every 6 hours and republishes each new release as a UPM package:

- **`upm` branch**: the package (`package.json` at the root), one commit per Glim release
- **tags** (`v0.13.0`, ...): one per release, pointing at the `upm` commit
- **GitHub Releases**: the upstream package zip + upstream release notes

All code and binaries are Glim's; see the license files inside the package.

## Install in Unity

Add to `Packages/manifest.json` (replace `<user>`):

```json
"io.github.z3y.glim": "https://github.com/<user>/glim-upm.git#upm"
```

or Package Manager → **+** → **Install package from git URL** with the same URL.

- **Latest version:** `#upm`. Unity pins the commit in `packages-lock.json`; to pick up a new
  release, select Glim in Package Manager and click **Update**.
- **Pinned version:** `#v0.13.0` (any tag).

Before the repo is on GitHub, a local clone works too:

```json
"io.github.z3y.glim": "git+file:///C:/Users/mineb/Unity/glim-upm#upm"
```

## Publishing this repo

1. Create an empty repo named `glim-upm` on GitHub (no README/license).
2. Push everything:
   ```
   git remote add origin https://github.com/<user>/glim-upm.git
   git push -u origin main upm --tags
   ```
3. In the repo's **Settings → Actions → General → Workflow permissions**, select **Read and write**.
4. **Actions → Mirror Glim releases → Run workflow** once to check it works
   (tick *all* to also create GitHub Releases for versions already mirrored locally — tags that
   already exist are skipped, so releases for those need `gh release create <tag>` by hand).

## Manual / local run

```
python scripts/mirror.py          # mirror the latest upstream release into the upm branch
python scripts/mirror.py --all    # every missing release
python scripts/mirror.py --push   # ...and push upm + tags
```
