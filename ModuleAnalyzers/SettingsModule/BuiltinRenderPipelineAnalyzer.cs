using System;
using System.Collections.Generic;
using Unity.ProjectAuditor.Editor;
using Unity.ProjectAuditor.Editor.Core;
using UnityEditor;
using UnityEditor.Rendering;
using UnityEngine;
using UnityEngine.Rendering;

namespace Unity.ProjectAuditorRules.SettingsModuleAnalyzers
{
    internal class BuiltinRenderPipelineAnalyzer : SettingsModuleAnalyzer
    {
        static readonly GraphicsTier[] k_GraphicsTiers = { GraphicsTier.Tier1, GraphicsTier.Tier2, GraphicsTier.Tier3 };

        internal const string PAS0022 = nameof(PAS0022);
        internal const string PAS0023 = nameof(PAS0023);
        internal const string PAS0024 = nameof(PAS0024);

        static readonly Descriptor k_ShaderQualityDescriptor = new Descriptor(
            PAS0022,
            "Graphics: Shader Quality uses a mixture of different values",
            Areas.BuildSize,
            "The current build target Graphics Tier Settings use a mixture of different values (Low/Medium/High) for the <b>Standard Shader Quality</b> setting. This will result in a larger number of shader variants being compiled, which will increase build times and your application's download/install size.",
            "Unless you support devices with a very wide range of capabilities for a particular platform, consider editing the platform in Graphics Settings to use the same shader quality setting across all Graphics Tiers.");

        static readonly Descriptor k_ForwardRenderingDescriptor = new Descriptor(
            PAS0023,
            "Graphics: Rendering Path is set to Forward Rendering",
            Areas.GPU,
            "The current build target uses forward rendering, as set in the <b>Rendering Path</b> settings in <b>Project Settings > Graphics > Tier Settings</b>. This can impact GPU performance in projects with nontrivial numbers of dynamic lights.",
            "This rendering path is suitable for applications with simple rendering and lighting requirements - for instance, 2D applications, or applications which mainly use baked lighting. If the project makes use of more than a few dynamic lights, consider experimenting with changing <b>Rendering Path</b> to Deferred to see whether doing so improves GPU rendering times.");

        static readonly Descriptor k_DeferredRenderingDescriptor = new Descriptor(
            PAS0024,
            "Graphics: Rendering Path is set to Deferred Rendering",
            Areas.GPU,
            "The current build target uses deferred rendering, as set in the <b>Rendering Path</b> settings in <b>Project Settings > Graphics > Tier Settings</b>. This can impact GPU performance in projects with simple rendering requirements.",
            "This rendering path is suitable for applications with more complex rendering requirements - for instance, applications that make use of dynamic lighting or certain types of fullscreen post-processing effects. If the project doesn't make use of such rendering techniques, consider experimenting with changing <b>Rendering Path</b> to Forward to see whether doing so improves GPU rendering times.");

        public override void Initialize(Action<Descriptor> registerDescriptor)
        {
            registerDescriptor(k_ShaderQualityDescriptor);
            registerDescriptor(k_ForwardRenderingDescriptor);
            registerDescriptor(k_DeferredRenderingDescriptor);
        }

        public override IEnumerable<ReportItem> Analyze(SettingsAnalysisContext context)
        {
            // Only check for Built-In Rendering Pipeline
            if (IsUsingBuiltinRenderPipeline())
            {
                if (IsMixedStandardShaderQuality(context.Params.Platform))
                {
                    yield return context.CreateIssue(IssueCategory.ProjectSetting, k_ShaderQualityDescriptor.Id)
                        .WithLocation("Project/Graphics");
                }
                if (IsUsingForwardRendering(context.Params.Platform))
                {
                    yield return context.CreateIssue(IssueCategory.ProjectSetting, k_ForwardRenderingDescriptor.Id)
                        .WithLocation("Project/Graphics");
                }
                if (IsUsingDeferredRendering(context.Params.Platform))
                {
                    yield return context.CreateIssue(IssueCategory.ProjectSetting, k_DeferredRenderingDescriptor.Id)
                        .WithLocation("Project/Graphics");
                }
            }
        }

        static bool IsUsingBuiltinRenderPipeline()
        {
            return GraphicsSettings.defaultRenderPipeline == null;
        }

        internal static bool IsMixedStandardShaderQuality(BuildTarget platform)
        {
            var buildGroup = BuildPipeline.GetBuildTargetGroup(platform);

            ShaderQuality? first = null;
            foreach (var tier in k_GraphicsTiers)
            {
                var quality = EditorGraphicsSettings.GetTierSettings(buildGroup, tier).standardShaderQuality;
                if (first == null)
                    first = quality;
                else if (first != quality)
                    return true;
            }

            return false;
        }

        internal static bool IsUsingForwardRendering(BuildTarget platform)
        {
            var buildGroup = BuildPipeline.GetBuildTargetGroup(platform);

            foreach (var tier in k_GraphicsTiers)
            {
                var path = EditorGraphicsSettings.GetTierSettings(buildGroup, tier).renderingPath;
                if (path == RenderingPath.Forward)
                    return true;
            }

            return false;
        }

        internal static bool IsUsingDeferredRendering(BuildTarget platform)
        {
            var buildGroup = BuildPipeline.GetBuildTargetGroup(platform);

            foreach (var tier in k_GraphicsTiers)
            {
                var path = EditorGraphicsSettings.GetTierSettings(buildGroup, tier).renderingPath;
                if (path == RenderingPath.DeferredShading)
                    return true;
            }

            return false;
        }
    }
}
