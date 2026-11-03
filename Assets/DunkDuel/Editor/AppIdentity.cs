using System;
using System.Collections.Generic;
using System.Security.Cryptography.X509Certificates;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;

namespace DunkDuel.Editor
{
    /// <summary>
    /// The app's identity and Apple signing defaults: bundle identifier and Android package com.astradite.dunkduel,
    /// automatic signing with the Teja Manne developer team. Applied by Project Setup, on Editor load and before every
    /// iOS and Android build, so a fresh checkout or a regenerated Xcode project is ready to sign with no manual steps.
    /// Outside Project Setup only empty or placeholder values are replaced, so a deliberate change in Player Settings
    /// is kept.
    /// </summary>
    [InitializeOnLoad]
    public static class AppIdentity
    {
        /// <summary>iOS bundle identifier and Android package name.</summary>
        public const string BundleId = "com.astradite.dunkduel";

        /// <summary>Apple developer team. An individual membership is named after its owner.</summary>
        public const string AppleTeamName = "Teja Manne";

        /// <summary>
        /// The team's 10-character Team ID, to pin it for every machine. When empty, it is read from this Mac's Apple
        /// signing certificates for <see cref="AppleTeamName"/> and saved in Player Settings. Xcode shows it on the
        /// Unity-iPhone target under Build Settings, Signing, Development Team.
        /// </summary>
        public const string AppleTeamId = "";

        private const string LookupTriedKey = "DunkDuel.AppIdentity.TeamLookupTried";

        static AppIdentity()
        {
            EditorApplication.delayCall += () => Apply(false, false);
        }

        /// <summary>
        /// Sets the app identifiers and the signing team. <paramref name="force"/> (Project Setup) overwrites the
        /// identifiers; otherwise only empty and placeholder ones change. The team is filled in only when none is set,
        /// or, with <paramref name="force"/>, when <see cref="AppleTeamId"/> pins a different one.
        /// </summary>
        public static void Apply(bool force, bool lookUpAgain = true)
        {
            foreach (NamedBuildTarget target in new[] { NamedBuildTarget.iOS, NamedBuildTarget.Android })
            {
                string current = PlayerSettings.GetApplicationIdentifier(target);
                if (current == BundleId || (!force && !IsPlaceholder(current))) continue;
                PlayerSettings.SetApplicationIdentifier(target, BundleId);
                Debug.Log("Dunk Duel: " + target.TargetName + " app identifier set to " + BundleId
                    + (string.IsNullOrEmpty(current) ? "." : " (was " + current + ")."));
            }

            string team = PlayerSettings.iOS.appleDeveloperTeamID;
            string wanted = null;
            if (!string.IsNullOrEmpty(AppleTeamId))
            {
                if (string.IsNullOrEmpty(team) || (force && team != AppleTeamId)) wanted = AppleTeamId;
            }
            else if (string.IsNullOrEmpty(team))
            {
                wanted = FindTeamIdOnThisMac(lookUpAgain);
            }
            if (string.IsNullOrEmpty(wanted)) return;
            PlayerSettings.iOS.appleEnableAutomaticSigning = true;
            PlayerSettings.iOS.appleDeveloperTeamID = wanted;
            Debug.Log("Dunk Duel: iOS signing set to automatic, team " + AppleTeamName + " (" + wanted + ").");
        }

        /// <summary>Empty, Unity's new-project defaults, or this project's first placeholder.</summary>
        public static bool IsPlaceholder(string id)
        {
            return string.IsNullOrEmpty(id)
                || id == "com.dunkduel.game"
                || id == "com.Company.ProductName"
                || id.StartsWith("com.DefaultCompany.", StringComparison.Ordinal);
        }

        /// <summary>
        /// The Team ID (certificate OU) of this Mac's Apple code-signing certificates for <see cref="AppleTeamName"/>,
        /// read with `security find-certificate`. Only public certificates are read, so macOS asks for no keychain
        /// access. Tried once per Editor session unless <paramref name="evenIfTried"/>; null without a single match.
        /// </summary>
        private static string FindTeamIdOnThisMac(bool evenIfTried)
        {
            if (Application.platform != RuntimePlatform.OSXEditor) return null;
            if (!evenIfTried && SessionState.GetBool(LookupTriedKey, false)) return null;
            SessionState.SetBool(LookupTriedKey, true);

            string pem = RunTool("/usr/bin/security", "find-certificate -a -c \"" + AppleTeamName + "\" -p");
            var all = new SortedSet<string>(StringComparer.Ordinal);
            var named = new SortedSet<string>(StringComparer.Ordinal);
            foreach (Match m in Regex.Matches(pem ?? "", "-----BEGIN CERTIFICATE-----(.+?)-----END CERTIFICATE-----", RegexOptions.Singleline))
            {
                X509Certificate2 cert;
                try
                {
                    cert = new X509Certificate2(Convert.FromBase64String(Regex.Replace(m.Groups[1].Value, @"\s+", "")));
                }
                catch (Exception)
                {
                    continue;
                }
                if (cert.NotAfter < DateTime.Now) continue;
                string ou = SigningTeamOf(cert.Subject, out bool ownTeam);
                if (ou == null) continue;
                all.Add(ou);
                if (ownTeam) named.Add(ou);
            }

            // Prefer certificates of the team named after its owner (a member of other teams has those too).
            SortedSet<string> pick = named.Count > 0 ? named : all;
            if (pick.Count == 1)
            {
                foreach (string id in pick) return id;
            }
            Debug.LogWarning(pick.Count == 0
                ? "Dunk Duel: no Apple signing certificate for " + AppleTeamName + " on this Mac, so the iOS signing team "
                    + "is not set. Sign in to Xcode with that account and build once, or set AppIdentity.AppleTeamId."
                : "Dunk Duel: certificates for several teams (" + string.Join(", ", pick) + "); set AppIdentity.AppleTeamId "
                    + "to choose the signing team.");
            return null;
        }

        /// <summary>
        /// The team ID of an Apple code-signing certificate subject ("CN=Apple Development: Name (ID), OU=TEAMID,
        /// O=Team, C=..."), or null for any other certificate. <paramref name="ownTeam"/>: O is <see cref="AppleTeamName"/>.
        /// </summary>
        public static string SigningTeamOf(string subject, out bool ownTeam)
        {
            string cn = NamePart(subject, "CN"), ou = NamePart(subject, "OU"), o = NamePart(subject, "O");
            ownTeam = o == AppleTeamName;
            if (cn == null || ou == null || !Regex.IsMatch(ou, "^[A-Z0-9]{10}$")) return null;
            bool signing = cn.StartsWith("Apple Development:", StringComparison.Ordinal)
                || cn.StartsWith("Apple Distribution:", StringComparison.Ordinal)
                || cn.StartsWith("iPhone Developer:", StringComparison.Ordinal)
                || cn.StartsWith("iPhone Distribution:", StringComparison.Ordinal);
            return signing ? ou : null;
        }

        /// <summary>One attribute of an X.500 name as .NET prints it: CN=a, OU=b, O="c, d".</summary>
        private static string NamePart(string name, string key)
        {
            Match m = Regex.Match(name ?? "", @"(?:^|,\s*)" + key + @"=(""(?:[^""]|"""")*""|[^,]*)");
            if (!m.Success) return null;
            string v = m.Groups[1].Value.Trim();
            if (v.Length >= 2 && v[0] == '"' && v[v.Length - 1] == '"') v = v.Substring(1, v.Length - 2).Replace("\"\"", "\"");
            return v;
        }

        private static string RunTool(string file, string arguments)
        {
            try
            {
                var info = new System.Diagnostics.ProcessStartInfo(file, arguments)
                {
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    CreateNoWindow = true,
                };
                using (System.Diagnostics.Process p = System.Diagnostics.Process.Start(info))
                {
                    if (p == null) return null;
                    string output = p.StandardOutput.ReadToEnd();
                    p.WaitForExit(10000);
                    return output;
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning("Dunk Duel: could not run " + file + ": " + e.Message);
                return null;
            }
        }
    }

    /// <summary>Before every iOS and Android build: identity and signing defaults (see <see cref="AppIdentity"/>).</summary>
    public sealed class AppIdentityBuildStep : IPreprocessBuildWithReport
    {
        public int callbackOrder => -200;

        public void OnPreprocessBuild(BuildReport report)
        {
            BuildTarget target = report.summary.platform;
            if (target == BuildTarget.iOS || target == BuildTarget.Android) AppIdentity.Apply(false);
        }
    }
}
