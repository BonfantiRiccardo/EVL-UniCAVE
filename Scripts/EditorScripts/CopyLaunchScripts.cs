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
    /// After every Windows standalone build, copy the contents of the package's
    /// CAVEScripts folder next to the built .exe. This keeps launch.ps1,
    /// kill.ps1, and nodes.json bundled with every build so CTS can invoke
    /// them directly on each node without a separate deploy step.
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

            int copied = 0;
            foreach(string src in Directory.GetFiles(srcDir))
            {
                string name = Path.GetFileName(src);
                if(name.Equals("README.md", System.StringComparison.OrdinalIgnoreCase)) continue;
                string dst = Path.Combine(buildDir, name);
                File.Copy(src, dst, overwrite: true);
                copied++;
            }
            Debug.Log($"[CopyLaunchScripts] copied {copied} file(s) from {srcDir} to {buildDir}");
        }

        // Locates the CAVEScripts folder relative to this .cs file. CallerFilePath
        // is resolved at compile time, so it points to wherever Unity actually
        // compiled the package from (Assets/ or Packages/). The folder layout the
        // walk-up assumes:
        //   <PackageRoot>/
        //     Scripts/EditorScripts/CopyLaunchScripts.cs  <- this file
        //     CAVEScripts/                                <- target
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
