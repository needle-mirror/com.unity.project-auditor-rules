using System;
using System.Collections.Generic;
using Unity.ProjectAuditor.Editor;
using Unity.ProjectAuditor.Editor.Core;
using UnityEditor.PackageManager;
using UnityEditorInternal;

namespace Unity.ProjectAuditorRules.PackagesModuleAnalyzers
{
    internal class PackagesAnalyzer : PackagesModuleAnalyzer
    {
        internal const string PAP0001 = nameof(PAP0001);
        internal const string PAP0002 = nameof(PAP0002);
        internal const string PAP0003 = nameof(PAP0003);
        internal const string PAP0004 = nameof(PAP0004);

        static readonly Descriptor k_RecommendPackageUpgrade = new Descriptor(
            PAP0001,
            "Newer recommended package version",
            Areas.Upgrade,
            "A newer recommended version of this package is available.",
            "Update the package via Package Manager."
        )
        {
            MessageFormat = "Package '{0}' could be updated from version '{1}' to '{2}'",
            DefaultSeverity = Severity.Minor
        };

        static readonly Descriptor k_RecommendPackagePreview = new Descriptor(
            PAP0002,
            "Experimental/Preview packages",
            Areas.Quality,
            "Experimental or Preview packages are in the early stages of development and not yet ready for production.",
            "Experimental packages should only be used for testing purposes and to give feedback to Unity."
        )
        {
            MessageFormat = "Package '{0}' version '{1}' is a preview/experimental version"
        };

        static readonly Descriptor k_RecommendPackageDowngrade = new Descriptor(
            PAP0003,
            "Older recommended package version",
            Areas.Upgrade,
            "An older package is the default for this version of Unity.",
            "Downgrade the package via Package Manager."
        )
        {
            MessageFormat = "Package '{0}' could be downgraded from version '{1}' to '{2}'",
            DefaultSeverity = Severity.Minor
        };

        internal static readonly Descriptor k_ModifiedPackageDescriptor = new Descriptor(
            PAP0004,
            "Modified Package",
            Areas.Quality | Areas.Upgrade,
            "Using modified versions of Unity packages prevents easy updates to newer versions. Unity expects to be able to update these packages in lockstep with Editor versions. The modified version may not be compatible with a newer version of Unity.",
            "Consider whether the package really needs to be customized."
            )
        {
            MessageFormat = "Using modified package '{0}'",
            DefaultSeverity = Severity.Major
        };

        public override void Initialize(Action<Descriptor> registerDescriptor)
        {
            registerDescriptor(k_RecommendPackageUpgrade);
            registerDescriptor(k_RecommendPackagePreview);
            registerDescriptor(k_RecommendPackageDowngrade);
            registerDescriptor(k_ModifiedPackageDescriptor);
        }

        public override IEnumerable<ReportItem> Analyze(PackageAnalysisContext context)
        {
            var package = context.PackageInfo;

            // first check if any package is preview or experimental
            if (package.version.Contains("pre") || package.version.Contains("exp"))
            {
                yield return context.CreateIssue(IssueCategory.ProjectSetting, k_RecommendPackagePreview.Id, package.name, package.version)
                    .WithLocation(package.assetPath);
            }

            // if not preview or experimental, check anyway if there is a recommended version available
            var recommendedVersionString = package.versions.recommended;
            if (!string.IsNullOrEmpty(package.version) && !string.IsNullOrEmpty(recommendedVersionString))
            {
                if (!recommendedVersionString.Equals(package.version))
                {
                    var version = InternalEditorUtility.GetUnityVersion();
                    var versionString = $"{version.Major}.{version.Minor}";

                    int comparison = CompareVersions(package.version, recommendedVersionString);
                    if (comparison < 0)
                    {
                        yield return context.CreateIssue(IssueCategory.ProjectSetting, k_RecommendPackageUpgrade.Id, package.name, package.version, recommendedVersionString)
                            .WithLocation(package.assetPath)
                            .WithUpgradeProperties(versionString, null, null);
                    }
                    else if (comparison > 0)
                    {
                        yield return context.CreateIssue(IssueCategory.ProjectSetting, k_RecommendPackageDowngrade.Id, package.name, package.version, recommendedVersionString)
                            .WithLocation(package.assetPath)
                            .WithUpgradeProperties(versionString, null, null);
                    }
                }
            }

            // custom/modified packages are high risk for upgrades because Unity expects to update them in lockstep with Editor versions
            if (package.source == PackageSource.Embedded || package.source == PackageSource.Local || package.source == PackageSource.LocalTarball)
            {
                // Modified package (Local but exists on Registry)
                if (package.versions != null && !string.IsNullOrEmpty(package.versions.latest) && ProjectAuditor.Editor.ProjectAuditor.KnownUnityVersions.Count > 1)
                {
                    yield return context.CreateIssue(IssueCategory.ProjectSetting, k_ModifiedPackageDescriptor.Id, package.name)
                        .WithLocation(package.assetPath)
                        .WithUpgradeProperties(ProjectAuditor.Editor.ProjectAuditor.KnownUnityVersions[1], null, null); // KnownUnityVersions[1] is the first future version (assuming the forked package is ok in the current version, otherwise user simply has compile errors to fix and doesn't need this issue reporting on top of those)
                }
                // Custom package (Local and unknown to Registry, no higher risk than normal project code)
            }
        }

        // Compares two package version strings using Semantic Versioning precedence rules
        // (https://semver.org, §11). Crucially, a pre-release version (e.g. "1.0.3-pre.1") has LOWER
        // precedence than its associated release ("1.0.3").
        // Returns -1 if lhs has lower precedence than rhs, 1 if higher, 0 if they are equal.
        internal static int CompareVersions(string lhs, string rhs)
        {
            SplitVersion(lhs, out var leftCore, out var leftPreRelease);
            SplitVersion(rhs, out var rightCore, out var rightPreRelease);

            var coreComparison = CompareCoreVersions(leftCore, rightCore);
            if (coreComparison != 0)
                return Math.Sign(coreComparison);

            // Equal core versions: a release outranks a pre-release of the same version.
            var leftHasPreRelease = leftPreRelease.Length > 0;
            var rightHasPreRelease = rightPreRelease.Length > 0;
            if (!leftHasPreRelease && !rightHasPreRelease)
                return 0;
            if (!leftHasPreRelease)
                return 1;
            if (!rightHasPreRelease)
                return -1;

            return Math.Sign(ComparePreRelease(leftPreRelease, rightPreRelease));
        }

        static void SplitVersion(string version, out string core, out string preRelease)
        {
            version = (version ?? string.Empty).Trim();

            // Build metadata ("+...") does not affect precedence, so discard it.
            var plusIndex = version.IndexOf('+');
            if (plusIndex >= 0)
                version = version.Substring(0, plusIndex);

            var dashIndex = version.IndexOf('-');
            if (dashIndex >= 0)
            {
                core = version.Substring(0, dashIndex);
                preRelease = version.Substring(dashIndex + 1);
            }
            else
            {
                core = version;
                preRelease = string.Empty;
            }
        }

        static int CompareCoreVersions(string leftCore, string rightCore)
        {
            var left = leftCore.Split('.');
            var right = rightCore.Split('.');
            var count = Math.Max(left.Length, right.Length);
            for (var i = 0; i < count; i++)
            {
                var leftValue = i < left.Length ? ParseNumericIdentifier(left[i]) : 0;
                var rightValue = i < right.Length ? ParseNumericIdentifier(right[i]) : 0;
                if (leftValue != rightValue)
                    return leftValue < rightValue ? -1 : 1;
            }
            return 0;
        }

        static int ParseNumericIdentifier(string identifier)
        {
            return int.TryParse(identifier, out var value) ? value : 0;
        }

        // Compares dot-separated pre-release identifiers per SemVer §11: numeric identifiers compare
        // numerically and rank below alphanumeric ones, which compare by ASCII order. When all shared
        // identifiers are equal, the version with more identifiers has higher precedence.
        static int ComparePreRelease(string leftPreRelease, string rightPreRelease)
        {
            var left = leftPreRelease.Split('.');
            var right = rightPreRelease.Split('.');
            var count = Math.Min(left.Length, right.Length);
            for (var i = 0; i < count; i++)
            {
                var comparison = ComparePreReleaseIdentifier(left[i], right[i]);
                if (comparison != 0)
                    return comparison;
            }
            return left.Length.CompareTo(right.Length);
        }

        static int ComparePreReleaseIdentifier(string left, string right)
        {
            var leftIsNumeric = int.TryParse(left, out var leftValue);
            var rightIsNumeric = int.TryParse(right, out var rightValue);

            if (leftIsNumeric && rightIsNumeric)
                return leftValue.CompareTo(rightValue);
            if (leftIsNumeric)
                return -1; // numeric identifiers have lower precedence than alphanumeric ones
            if (rightIsNumeric)
                return 1;
            return string.CompareOrdinal(left, right);
        }
    }
}
