# Glim Installer

Installs [Glim](https://github.com/z3y/glim) (by z3y, MIT) into **`Assets/Plugins/Glim`** and keeps it updated.

## Install

Package Manager → **+** → **Install package from git URL**:

```
https://github.com/knotstudiosllc/glim-upm.git
```

When Unity opens, the installer checks Glim's latest GitHub release. If Glim is missing or a newer
version is out, it downloads it and puts it in `Assets/Plugins/Glim`.

Glim's scripts reference their own files as `Packages/io.github.z3y.glim/...`; the installer rewrites
those to `Assets/Plugins/Glim/...` so baking works from the Assets folder.

It also makes Glim respect the Lighting window's **Albedo Boost** (Unity's meta pass raises albedo to
`1 / Albedo Boost`; stock Glim always uses 1), so bounce lighting matches Unity's lightmapper.
When **Lighting > Environment > Environment Lighting > Source** is **Gradient** or **Color**, Glim's
sky capture uses those colors (stock Glim always uses the skybox material), matching Unity's lightmapper.

Existing installs are re-patched automatically when the installer updates.

## Menu: Tools → Glim

- **Check for Updates**: check now and install if newer
- **Reinstall Latest**: download and reinstall the latest version
- **Auto Update On Startup**: on by default (per machine). When off, the startup check only logs
  that an update is available. A missing Glim is always installed.

If a bake already ran in this Unity session, Glim's native DLL is locked; the update then waits for
the next Unity start. An older copy at `Assets/Packages/io.github.z3y.glim` is offered to the trash
before installing, since the two would conflict.
