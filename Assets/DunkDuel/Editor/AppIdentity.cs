using System;
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

        /// <summary>Apple developer team, for messages. An individual membership is named after its owner.</summary>
        public const string AppleTeamName = "Teja Manne";

        /// <summary>
        /// The team's Team ID (Xcode: Unity-iPhone target, Build Settings, Signing, Development Team). Not the code in
        /// the certificate name, "Apple Development: Teja Manne (BX42AX95QW)", which identifies the person.
        /// </summary>
        public const string AppleTeamId = "JYC5MKYDS7";

        static AppIdentity()
        {
            EditorApplication.delayCall += () => Apply(false);
        }

        /// <summary>
        /// Sets the app identifiers and the signing team. <paramref name="force"/> (Project Setup) overwrites both;
        /// otherwise only empty and placeholder identifiers change and the team is filled in only when none is set.
        /// </summary>
        public static void Apply(bool force)
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
            if (team == AppleTeamId && PlayerSettings.iOS.appleEnableAutomaticSigning) return;
            if (!force && !string.IsNullOrEmpty(team)) return;
            PlayerSettings.iOS.appleEnableAutomaticSigning = true;
            PlayerSettings.iOS.appleDeveloperTeamID = AppleTeamId;
            Debug.Log("Dunk Duel: iOS signing set to automatic, team " + AppleTeamName + " (" + AppleTeamId + ")"
                + (string.IsNullOrEmpty(team) || team == AppleTeamId ? "." : ", was " + team + "."));
        }

        /// <summary>Empty, Unity's new-project defaults, or this project's first placeholder.</summary>
        public static bool IsPlaceholder(string id)
        {
            return string.IsNullOrEmpty(id)
                || id == "com.dunkduel.game"
                || id == "com.Company.ProductName"
                || id.StartsWith("com.DefaultCompany.", StringComparison.Ordinal);
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
