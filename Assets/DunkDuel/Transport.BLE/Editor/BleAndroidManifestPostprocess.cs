#if UNITY_ANDROID
using System.IO;
using System.Xml;
using UnityEditor.Android;

namespace DunkDuel.Transport.BLE.Editor
{
    /// <summary>
    /// Adds the Bluetooth permissions and the BLE feature to the generated unityLibrary manifest:
    /// Android 12+ uses BLUETOOTH_SCAN (neverForLocation), BLUETOOTH_ADVERTISE and BLUETOOTH_CONNECT;
    /// Android 11 and older use legacy BLUETOOTH/BLUETOOTH_ADMIN plus ACCESS_FINE_LOCATION for scanning.
    /// BLE is declared optional so phones without it can still install and play the CPU.
    /// </summary>
    public sealed class BleAndroidManifestPostprocess : IPostGenerateGradleAndroidProject
    {
        private const string AndroidNs = "http://schemas.android.com/apk/res/android";

        public int callbackOrder => 100;

        public void OnPostGenerateGradleAndroidProject(string path)
        {
            string manifestPath = Path.Combine(path, "src", "main", "AndroidManifest.xml");
            if (!File.Exists(manifestPath)) return;
            var doc = new XmlDocument();
            doc.Load(manifestPath);
            XmlElement manifest = doc.DocumentElement;
            if (manifest == null) return;

            AddPermission(doc, manifest, "android.permission.BLUETOOTH_SCAN", null, "neverForLocation");
            AddPermission(doc, manifest, "android.permission.BLUETOOTH_ADVERTISE", null, null);
            AddPermission(doc, manifest, "android.permission.BLUETOOTH_CONNECT", null, null);
            AddPermission(doc, manifest, "android.permission.BLUETOOTH", "30", null);
            AddPermission(doc, manifest, "android.permission.BLUETOOTH_ADMIN", "30", null);
            AddPermission(doc, manifest, "android.permission.ACCESS_FINE_LOCATION", "30", null);
            AddFeature(doc, manifest, "android.hardware.bluetooth_le", false);

            doc.Save(manifestPath);
        }

        private static void AddPermission(XmlDocument doc, XmlElement manifest, string name, string maxSdk, string flags)
        {
            foreach (XmlNode node in manifest.SelectNodes("uses-permission"))
            {
                if (node is XmlElement e && e.GetAttribute("name", AndroidNs) == name) return;
            }
            XmlElement el = doc.CreateElement("uses-permission");
            SetAndroidAttribute(doc, el, "name", name);
            if (maxSdk != null) SetAndroidAttribute(doc, el, "maxSdkVersion", maxSdk);
            if (flags != null) SetAndroidAttribute(doc, el, "usesPermissionFlags", flags);
            manifest.AppendChild(el);
        }

        private static void SetAndroidAttribute(XmlDocument doc, XmlElement el, string localName, string value)
        {
            XmlAttribute a = doc.CreateAttribute("android", localName, AndroidNs);
            a.Value = value;
            el.Attributes.Append(a);
        }

        private static void AddFeature(XmlDocument doc, XmlElement manifest, string name, bool required)
        {
            foreach (XmlNode node in manifest.SelectNodes("uses-feature"))
            {
                if (node is XmlElement e && e.GetAttribute("name", AndroidNs) == name) return;
            }
            XmlElement el = doc.CreateElement("uses-feature");
            SetAndroidAttribute(doc, el, "name", name);
            SetAndroidAttribute(doc, el, "required", required ? "true" : "false");
            manifest.AppendChild(el);
        }
    }
}
#endif
