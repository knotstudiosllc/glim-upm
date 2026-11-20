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

        // Bump when the installer changes what it does to Glim's files, so existing installs are
        // re-patched. 1 = package path rewrite, 2 = Albedo Boost (Lighting window),
        // 3 = Environment Lighting Gradient / Color sky, 4 = Albedo Boost slider on the Glim Lightmapper.
        const int PatchLevel = 4;
        const string AmbientHelperTemplate = "Packages/com.knotstudiosllc.glim-installer/Editor/Patches/GlimInstallerAmbient.cs.txt";
        // Dot-files are ignored by Unity's asset database.
        const string MarkerFile = InstallDir + "/.glim-installer-patch";

        const string SessionKey = "GlimInstaller.CheckedThisSession";
        const string AutoUpdatePref = "GlimInstaller.AutoUpdate";
        const string Menu = "Tools/Glim/";

        static bool s_Busy;

        static bool AutoUpdate => EditorPrefs.GetBool(AutoUpdatePref, true);

        static GlimInstaller()
        {
            if (Application.isBatchMode)
                return;

            bool checkedThisSession = SessionState.GetBool(SessionKey, false);
            // After the installer itself updates (same session), still re-patch an outdated install.
            bool outdatedPatch = InstalledVersion() != null && InstalledPatchLevel() < PatchLevel;
            if (checkedThisSession && !outdatedPatch)
                return;

            SessionState.SetBool(SessionKey, true);
            EditorApplication.delayCall += () => Check(interactive: false, force: false);
        }

        static int InstalledPatchLevel()
        {
            var path = FullPath(MarkerFile);
            if (!File.Exists(path)) return InstalledVersion() == null ? 0 : 1;
            return int.TryParse(File.ReadAllText(path).Trim(), out var level) ? level : 1;
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
            bool repatch = installed != null && InstalledPatchLevel() < PatchLevel;
            bool needed = force || repatch || installed == null || latest > installed;
            if (!needed)
            {
                s_Busy = false;
                if (interactive)
                    EditorUtility.DisplayDialog("Glim Installer", $"Glim {installed} is up to date.", "OK");
                return;
            }

            if (installed != null && !force && !repatch && !interactive && !AutoUpdate)
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

            // 2b. Albedo Boost: Glim renders albedo through each material's Meta pass but always
            //     passes boost 1. Unity's lightmapper uses the Lighting window's Albedo Boost
            //     (meta pass does albedo^(1/boost)); feed Glim the same value.
            bool albedoBoost = PatchAlbedoBoost(root);

            // 2c. Environment Lighting: Glim always captures the skybox material; Unity's
            //     lightmapper uses the Gradient / Color ambient source when one is selected.
            bool ambient = PatchAmbientSource(root);

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
            File.WriteAllText(FullPath(MarkerFile), PatchLevel.ToString());
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
            Log($"Installed Glim {latest} to {InstallDir}{from}. Rewrote package paths in {rewritten} script(s). " +
                (albedoBoost ? "Albedo Boost support added. " : "WARNING: Albedo Boost patch did not apply (Glim's GlimLightmapper.cs or MetaTexture.cs changed). ") +
                (ambient ? "Gradient/Color environment lighting support added." : "WARNING: environment lighting patch did not apply (Glim's SkyboxCapture.cs changed)."));
            if (interactive)
                EditorUtility.DisplayDialog("Glim Installer", $"Glim {latest} installed to {InstallDir}.", "OK");
        }

        // Albedo Boost: an "Albedo Boost" slider on the Glim Lightmapper component, used when Glim
        // renders surface colours through each material's Meta pass (albedo^(1/boost), the same as
        // Unity's lightmapper). Stock Glim always uses 1.
        const string AlbedoFieldAnchor = "public float emissiveMultiplier = 1.0f;";
        const string AlbedoField =
            "public float emissiveMultiplier = 1.0f;\n\n" +
            "        [Tooltip(\"Brightens surface colors for bounced light, like Unity's Albedo Boost (albedo^(1/boost)). " +
            "1 = physically correct, 10 = surfaces bounce almost like white. Added by Glim Installer.\")]\n" +
            "        [Range(1.0f, 10.0f)] public float albedoBoost = 1.0f; // Glim Installer: Albedo Boost";
        const string AlbedoBoostOriginal = "cmd.SetGlobalFloat(\"unity_OneOverOutputBoost\", 1.0f);";
        const string AlbedoBoostPatched =
            "cmd.SetGlobalFloat(\"unity_OneOverOutputBoost\", 1.0f / Mathf.Clamp(" +
            "UnityEngine.Object.FindFirstObjectByType<GlimLightmapper>() is GlimLightmapper glimInstallerLightmapper " +
            "? glimInstallerLightmapper.albedoBoost : 1.0f, 1.0f, 10.0f)); // Glim Installer: Albedo Boost";

        static bool PatchAlbedoBoost(string packageRoot)
        {
            // Both edits or neither: MetaTexture must never reference a field that wasn't added.
            var runtimeFile = Path.Combine(packageRoot, "Runtime", "GlimLightmapper.cs");
            var metaFile = Path.Combine(packageRoot, "Editor", "MetaTexture.cs");
            if (!TryReplaceOnce(runtimeFile, AlbedoFieldAnchor, AlbedoField, out var runtimeText, out var runtimeBom)) return false;
            if (!TryReplaceOnce(metaFile, AlbedoBoostOriginal, AlbedoBoostPatched, out var metaText, out var metaBom)) return false;
            WriteText(runtimeFile, runtimeText, runtimeBom);
            WriteText(metaFile, metaText, metaBom);
            return true;
        }

        // Replaces a string that must occur exactly once. Keeps the file's line endings.
        static bool TryReplaceOnce(string file, string original, string patched, out string text, out bool bom)
        {
            text = null;
            bom = false;
            if (!File.Exists(file)) return false;

            var bytes = File.ReadAllBytes(file);
            bom = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;
            text = Encoding.UTF8.GetString(bytes, bom ? 3 : 0, bytes.Length - (bom ? 3 : 0));
            if (text.Contains("Glim Installer: Albedo Boost")) return true; // already patched

            int first = text.IndexOf(original, StringComparison.Ordinal);
            if (first < 0 || text.IndexOf(original, first + 1, StringComparison.Ordinal) >= 0)
                return false; // missing or ambiguous: leave Glim untouched

            string newline = text.Contains("\r\n") ? "\r\n" : "\n";
            text = text.Replace(original, patched.Replace("\n", newline));
            return true;
        }

        static void WriteText(string file, string text, bool bom)
        {
            var enc = new UTF8Encoding(bom);
            File.WriteAllBytes(file, Combine(enc.GetPreamble(), enc.GetBytes(text)));
        }

        const string AmbientOriginal = "camera.RenderToCubemap(rt);";
        const string AmbientPatched = "GlimInstallerAmbient.RenderToCubemap(camera, rt); // Glim Installer: ambient source";

        static bool PatchAmbientSource(string packageRoot)
        {
            var file = Path.Combine(packageRoot, "Editor", "SkyboxCapture.cs");
            var template = Path.GetFullPath(AmbientHelperTemplate);
            if (!File.Exists(file) || !File.Exists(template)) return false;

            var bytes = File.ReadAllBytes(file);
            bool bom = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;
            var text = Encoding.UTF8.GetString(bytes, bom ? 3 : 0, bytes.Length - (bom ? 3 : 0));
            if (!text.Contains("Glim Installer: ambient source"))
            {
                int first = text.IndexOf(AmbientOriginal, StringComparison.Ordinal);
                if (first < 0 || text.IndexOf(AmbientOriginal, first + 1, StringComparison.Ordinal) >= 0)
                    return false; // missing or ambiguous: leave Glim untouched
                text = text.Replace(AmbientOriginal, AmbientPatched);
                var enc = new UTF8Encoding(bom);
                File.WriteAllBytes(file, Combine(enc.GetPreamble(), enc.GetBytes(text)));
            }

            File.Copy(template, Path.Combine(packageRoot, "Editor", "GlimInstallerAmbient.cs"), true);
            return true;
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
