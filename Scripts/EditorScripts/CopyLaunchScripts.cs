#if UNITY_EDITOR
using System.IO;
using System.Runtime.CompilerServices;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;

namespace UniCAVE
{
    /// <summary>
    /// After every Windows build, copies the package's CAVEScripts folder next to the exe, so each
    /// synced build folder carries its own launch scripts. Files in a CAVEScripts folder at the
    /// project root (beside Assets) are copied afterwards and replace the package's ones.
    /// </summary>
    public class CopyLaunchScripts : IPostprocessBuildWithReport
    {
        public int callbackOrder => 100;

        public void OnPostprocessBuild(BuildReport report)
        {
            if(report.summary.platform != BuildTarget.StandaloneWindows64) return;

            string buildDir = Path.GetDirectoryName(report.summary.outputPath);
            string srcDir = ResolveCaveScriptsDir();

            if(!Directory.Exists(srcDir))
            {
                Debug.LogWarning($"[CopyLaunchScripts] source folder missing: {srcDir} - skipped");
                return;
            }

            int copied = CopyFiles(srcDir, buildDir);
            Debug.Log($"[CopyLaunchScripts] copied {copied} file(s) from {srcDir} to {buildDir}");

            string projectDir = Path.GetFullPath(Path.Combine(Application.dataPath, "..", "CAVEScripts"));
            if(Directory.Exists(projectDir))
            {
                int replaced = CopyFiles(projectDir, buildDir);
                Debug.Log($"[CopyLaunchScripts] copied {replaced} project file(s) from {projectDir} to {buildDir}");
            }
        }

        private static int CopyFiles(string srcDir, string buildDir)
        {
            int copied = 0;
            foreach(string src in Directory.GetFiles(srcDir))
            {
                string name = Path.GetFileName(src);
                if(name.Equals("README.md", System.StringComparison.OrdinalIgnoreCase)) continue;
                if(name.EndsWith(".meta", System.StringComparison.OrdinalIgnoreCase)) continue;
                File.Copy(src, Path.Combine(buildDir, name), overwrite: true);
                copied++;
            }
            return copied;
        }

        // CallerFilePath is fixed at compile time, so CAVEScripts is found two folders above this
        // file wherever the package was imported (Assets/ or Packages/).
        private static string ResolveCaveScriptsDir([CallerFilePath] string thisFile = null)
        {
            string editorDir = Path.GetDirectoryName(thisFile);                  // .../Scripts/EditorScripts
            string scriptsDir = Path.GetDirectoryName(editorDir);                // .../Scripts
            string packageRoot = Path.GetDirectoryName(scriptsDir);              // .../<PackageRoot>
            return Path.GetFullPath(Path.Combine(packageRoot, "CAVEScripts"));
        }
    }
}
#endif
