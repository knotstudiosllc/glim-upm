// Glim Installer: keeps Glim (https://github.com/z3y/glim) installed and up to date in
// Assets/Plugins/Glim. Downloads the latest upstream release, rewrites Glim's hard-coded
// "Packages/io.github.z3y.glim/" asset paths to "Assets/Plugins/Glim/", and replaces the folder.

using System;
using System.IO;
using System.IO.Compression;
using System.Text;
using UnityEditor;
using UnityEngine;
using UnityEngine.Networking;

namespace KnotStudios.GlimInstaller
{
    [InitializeOnLoad]
    static class GlimInstaller
    {
        const string UpstreamRepo = "z3y/glim";
        const string AssetPrefix = "io.github.z3y.glim";
        const string InstallDir = "Assets/Plugins/Glim";
        const string UpstreamPathPrefix = "Packages/io.github.z3y.glim/";
        const string OldEmbeddedDir = "Assets/Packages/io.github.z3y.glim";

        const string SessionKey = "GlimInstaller.CheckedThisSession";
        const string AutoUpdatePref = "GlimInstaller.AutoUpdate";
        const string Menu = "Tools/Glim/";

        static bool s_Busy;

        static bool AutoUpdate => EditorPrefs.GetBool(AutoUpdatePref, true);

        static GlimInstaller()
        {
            if (Application.isBatchMode || SessionState.GetBool(SessionKey, false))
                return;
            SessionState.SetBool(SessionKey, true);
            EditorApplication.delayCall += () => Check(interactive: false, force: false);
        }

        [MenuItem(Menu + "Check for Updates", priority = 0)]
        static void MenuCheck() => Check(interactive: true, force: false);

        [MenuItem(Menu + "Reinstall Latest", priority = 1)]
        static void MenuReinstall() => Check(interactive: true, force: true);

        [MenuItem(Menu + "Auto Update On Startup", priority = 20)]
        static void MenuToggleAuto() => EditorPrefs.SetBool(AutoUpdatePref, !AutoUpdate);

        [MenuItem(Menu + "Auto Update On Startup", true)]
        static bool MenuToggleAutoValidate()
        {
            UnityEditor.Menu.SetChecked(Menu + "Auto Update On Startup", AutoUpdate);
            return true;
        }

        // ---------------------------------------------------------------------------------

#pragma warning disable 0649 // fields are filled by JsonUtility
        [Serializable] class ReleaseAsset { public string name; public string browser_download_url; }
        [Serializable] class Release { public string tag_name; public ReleaseAsset[] assets; }
        [Serializable] class PackageJson { public string version; }
#pragma warning restore 0649

        static string ProjectRoot => Directory.GetParent(Application.dataPath).FullName;
        static string FullPath(string assetPath) => Path.Combine(ProjectRoot, assetPath.Replace('/', Path.DirectorySeparatorChar));

        static void Log(string msg) => Debug.Log("[Glim Installer] " + msg);

        static void Fail(string msg, bool interactive)
        {
            Debug.LogWarning("[Glim Installer] " + msg);
            if (interactive)
                EditorUtility.DisplayDialog("Glim Installer", msg, "OK");
        }

        static Version ParseVersion(string s)
        {
            if (string.IsNullOrEmpty(s)) return null;
            s = s.Trim().TrimStart('v', 'V');
            int cut = s.IndexOfAny(new[] { '-', '+', ' ' });
            if (cut >= 0) s = s.Substring(0, cut);
            return Version.TryParse(s, out var v) ? v : null;
        }

        static Version InstalledVersion()
        {
            var path = FullPath(InstallDir + "/package.json");
            if (!File.Exists(path)) return null;
            try { return ParseVersion(JsonUtility.FromJson<PackageJson>(File.ReadAllText(path)).version); }
            catch { return null; }
        }

        static bool EditorIsBusy() =>
            EditorApplication.isPlayingOrWillChangePlaymode || EditorApplication.isCompiling || Lightmapping.isRunning;

        static void Check(bool interactive, bool force)
        {
            if (s_Busy) return;
            if (EditorIsBusy())
            {
                Fail("Unity is busy (play mode, compiling or baking). Try again from Tools > Glim.", interactive);
                return;
            }

            s_Busy = true;
            var req = UnityWebRequest.Get($"https://api.github.com/repos/{UpstreamRepo}/releases/latest");
            req.SetRequestHeader("User-Agent", "glim-installer");
            req.SetRequestHeader("Accept", "application/vnd.github+json");
            req.SendWebRequest().completed += _ =>
            {
                try { OnRelease(req, interactive, force); }
                catch (Exception e) { s_Busy = false; Fail("Update check failed: " + e.Message, interactive); }
                finally { req.Dispose(); }
            };
        }

        static void OnRelease(UnityWebRequest req, bool interactive, bool force)
        {
            if (req.result != UnityWebRequest.Result.Success)
            {
                s_Busy = false;
                Fail($"Could not reach GitHub ({req.error}).", interactive);
                return;
            }

            var release = JsonUtility.FromJson<Release>(req.downloadHandler.text);
            var latest = ParseVersion(release?.tag_name);
            ReleaseAsset zip = null;
            if (release?.assets != null)
                foreach (var a in release.assets)
                    if (a.name.StartsWith(AssetPrefix) && a.name.EndsWith(".zip")) { zip = a; break; }

            if (latest == null || zip == null)
            {
                s_Busy = false;
                Fail($"Latest Glim release ({release?.tag_name}) has no package zip.", interactive);
                return;
            }

            var installed = InstalledVersion();
            bool needed = force || installed == null || latest > installed;
            if (!needed)
            {
                s_Busy = false;
                if (interactive)
                    EditorUtility.DisplayDialog("Glim Installer", $"Glim {installed} is up to date.", "OK");
                return;
            }

            if (installed != null && !force && !interactive && !AutoUpdate)
            {
                s_Busy = false;
                Log($"Glim {latest} is available (installed: {installed}). Use Tools > Glim > Check for Updates.");
                return;
            }

            if (!RemoveOldEmbeddedCopy())
            {
                s_Busy = false;
                return;
            }

            Log($"Downloading Glim {latest}...");
            var workDir = Path.Combine(ProjectRoot, "Temp", "GlimInstaller");
            if (Directory.Exists(workDir)) Directory.Delete(workDir, true);
            Directory.CreateDirectory(workDir);
            var zipPath = Path.Combine(workDir, zip.name);

            var dl = new UnityWebRequest(zip.browser_download_url, UnityWebRequest.kHttpVerbGET,
                new DownloadHandlerFile(zipPath), null);
            dl.SetRequestHeader("User-Agent", "glim-installer");
            dl.SendWebRequest().completed += _ =>
            {
                try
                {
                    if (dl.result != UnityWebRequest.Result.Success)
                        Fail($"Download failed ({dl.error}).", interactive);
                    else
                        Install(zipPath, workDir, latest, installed, interactive);
                }
                catch (Exception e) { Fail("Install failed: " + e.Message, interactive); }
                finally { dl.Dispose(); s_Busy = false; }
            };
        }

        // An older copy in Assets/Packages would clash with Assets/Plugins/Glim (same scripts/GUIDs).
        static bool RemoveOldEmbeddedCopy()
        {
            if (!AssetDatabase.IsValidFolder(OldEmbeddedDir))
                return true;

            if (!EditorUtility.DisplayDialog("Glim Installer",
                    $"An older Glim copy exists at {OldEmbeddedDir}. It conflicts with {InstallDir}.\n\n" +
                    "Move it to the trash and install Glim to " + InstallDir + "?", "Move to Trash", "Cancel"))
            {
                Log("Install cancelled: old copy at " + OldEmbeddedDir + " was kept.");
                return false;
            }

            if (!AssetDatabase.MoveAssetToTrash(OldEmbeddedDir))
            {
                Fail("Could not move " + OldEmbeddedDir + " to the trash. Delete it manually.", true);
                return false;
            }
            return true;
        }

        static void Install(string zipPath, string workDir, Version latest, Version installed, bool interactive)
        {
            // 1. Extract.
            var staging = Path.Combine(workDir, "extract");
            Directory.CreateDirectory(staging);
            var stagingFull = Path.GetFullPath(staging) + Path.DirectorySeparatorChar;
            using (var fs = File.OpenRead(zipPath))
            using (var zip = new ZipArchive(fs, ZipArchiveMode.Read))
            {
                foreach (var entry in zip.Entries)
                {
                    var dest = Path.GetFullPath(Path.Combine(staging, entry.FullName));
                    if (!dest.StartsWith(stagingFull, StringComparison.OrdinalIgnoreCase))
                        continue; // ignore entries escaping the folder
                    if (string.IsNullOrEmpty(entry.Name))
                    {
                        Directory.CreateDirectory(dest);
                        continue;
                    }
                    Directory.CreateDirectory(Path.GetDirectoryName(dest));
                    using (var src = entry.Open())
                    using (var dst = File.Create(dest))
                        src.CopyTo(dst);
                }
            }

            var root = staging;
            if (!File.Exists(Path.Combine(root, "package.json")))
            {
                var dirs = Directory.GetDirectories(root);
                if (dirs.Length == 1 && File.Exists(Path.Combine(dirs[0], "package.json")))
                    root = dirs[0];
                else
                    throw new Exception("package.json not found in the release zip.");
            }

            // 2. Point Glim's hard-coded package paths at Assets/Plugins/Glim.
            int rewritten = 0;
            foreach (var file in Directory.GetFiles(root, "*.cs", SearchOption.AllDirectories))
            {
                var bytes = File.ReadAllBytes(file);
                bool bom = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;
                var text = Encoding.UTF8.GetString(bytes, bom ? 3 : 0, bytes.Length - (bom ? 3 : 0));
                if (!text.Contains(UpstreamPathPrefix)) continue;
                text = text.Replace(UpstreamPathPrefix, InstallDir + "/");
                var enc = new UTF8Encoding(bom);
                File.WriteAllBytes(file, Combine(enc.GetPreamble(), enc.GetBytes(text)));
                rewritten++;
            }

            // 3. Swap folders. Renaming fails if Glim's native DLL is loaded (after a bake), in
            //    which case nothing is changed and the update runs on the next Unity start.
            var target = FullPath(InstallDir);
            var backup = target + "~old";
            Directory.CreateDirectory(Path.GetDirectoryName(target));
            if (Directory.Exists(backup)) Directory.Delete(backup, true);
            if (Directory.Exists(target))
            {
                try { Directory.Move(target, backup); }
                catch (IOException)
                {
                    Fail("Glim's native library is in use (a bake ran this session). " +
                         $"Restart Unity and Glim {latest} will be installed on startup.", interactive);
                    return;
                }
            }
            Directory.Move(root, target);
            if (Directory.Exists(backup))
            {
                // Keep the folder's own .meta; drop the old files.
                try { Directory.Delete(backup, true); } catch (IOException) { }
            }
            if (Directory.Exists(workDir))
            {
                try { Directory.Delete(workDir, true); } catch (IOException) { }
            }

            AssetDatabase.Refresh();
            var from = installed == null ? "" : $" (was {installed})";
            Log($"Installed Glim {latest} to {InstallDir}{from}. Rewrote package paths in {rewritten} script(s).");
            if (interactive)
                EditorUtility.DisplayDialog("Glim Installer", $"Glim {latest} installed to {InstallDir}.", "OK");
        }

        static byte[] Combine(byte[] a, byte[] b)
        {
            var r = new byte[a.Length + b.Length];
            Buffer.BlockCopy(a, 0, r, 0, a.Length);
            Buffer.BlockCopy(b, 0, r, a.Length, b.Length);
            return r;
        }
    }
}
