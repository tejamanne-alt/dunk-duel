#if UNITY_IOS
using System.IO;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEditor.iOS.Xcode;

namespace DunkDuel.Transport.BLE.Editor
{
    /// <summary>
    /// iOS build step for the CoreBluetooth bridge: Bluetooth usage strings in Info.plist, CoreBluetooth linked
    /// into UnityFramework (where IL2CPP and the Swift plugin live), and the Swift build settings the plugin needs.
    /// </summary>
    public sealed class BleIosBuildPostprocess : IPostprocessBuildWithReport
    {
        public const string UsageDescription =
            "Dunk Duel uses Bluetooth to find and play against a nearby phone. No account or internet connection is needed.";

        public int callbackOrder => 100;

        public void OnPostprocessBuild(BuildReport report)
        {
            if (report.summary.platform != BuildTarget.iOS) return;
            string path = report.summary.outputPath;

            string plistPath = Path.Combine(path, "Info.plist");
            var plist = new PlistDocument();
            plist.ReadFromFile(plistPath);
            plist.root.SetString("NSBluetoothAlwaysUsageDescription", UsageDescription);
            plist.root.SetString("NSBluetoothPeripheralUsageDescription", UsageDescription); // iOS 12 and older
            plist.WriteToFile(plistPath);

            string projectPath = PBXProject.GetPBXProjectPath(path);
            var project = new PBXProject();
            project.ReadFromFile(projectPath);
            string framework = project.GetUnityFrameworkTargetGuid();
            string app = project.GetUnityMainTargetGuid();
            project.AddFrameworkToProject(framework, "CoreBluetooth.framework", false);
            project.SetBuildProperty(framework, "SWIFT_VERSION", "5.0");
            project.SetBuildProperty(framework, "CLANG_ENABLE_MODULES", "YES");
            project.SetBuildProperty(framework, "ALWAYS_EMBED_SWIFT_STANDARD_LIBRARIES", "NO");
            project.SetBuildProperty(app, "ALWAYS_EMBED_SWIFT_STANDARD_LIBRARIES", "YES");
            project.WriteToFile(projectPath);
        }
    }
}
#endif
