using System;
using System.Collections.Generic;
using Unity.ProjectAuditor.Editor;
using Unity.ProjectAuditor.Editor.Core;
using UnityEditor;
using UnityEditor.Build;

namespace Unity.ProjectAuditorRules.SettingsModuleAnalyzers
{
    class IosTargetVersionAnalyzer : SettingsModuleAnalyzer
    {
        internal const string PAS0042 = nameof(PAS0042);

        static readonly Descriptor k_Descriptor_6000_7 = new Descriptor(
            PAS0042,
            "iOS: Target minimum iOS Version is below 16.0",
            Areas.Upgrade,
            "The <b>Target minimum iOS Version</b> in Player Settings is below 16.0. Unity 6.7 raises the minimum supported iOS version to 16.0, so a project targeting a lower version will not build.",
            "Set <b>Target minimum iOS Version</b> to 16.0 or higher in <b>Project Settings > Player > iOS > Other Settings</b>.")
        {
#if UNITY_6000_4_OR_NEWER
            Platforms = new SerializableEnum<BuildTarget>[] { BuildTarget.iOS },
#else
            Platforms = new[] { BuildTarget.iOS },
#endif
            MessageFormat = "iOS Target minimum iOS Version ({0}) is below 16.0"
        };

        static readonly Version k_MinimumSupportedVersion_6000_7 = new Version(16, 0);

        public override void Initialize(Action<Descriptor> registerDescriptor)
        {
            registerDescriptor(k_Descriptor_6000_7);
        }

        public override IEnumerable<ReportItem> Analyze(SettingsAnalysisContext context)
        {
            if (context.IsDescriptorEnabled(k_Descriptor_6000_7))
            {
                var targetVersion = PlayerSettings.iOS.targetOSVersionString;
                if (IsBelowMinimum(targetVersion, k_MinimumSupportedVersion_6000_7))
                {
                    yield return context.CreateIssue(IssueCategory.ProjectSetting, k_Descriptor_6000_7.Id, targetVersion)
                        .WithLocation("Project/Player")
                        .WithUpgradeProperties("6000.7", null, null);
                }
            }
        }

        internal static bool IsBelowMinimum(string versionString, Version minimum)
        {
            if (string.IsNullOrWhiteSpace(versionString))
                return false;

            versionString = versionString.Trim();

            // System.Version requires at least two components, but the setting may hold just "15".
            if (!versionString.Contains("."))
                versionString += ".0";

            return Version.TryParse(versionString, out var version) && version < minimum;
        }
    }
}
