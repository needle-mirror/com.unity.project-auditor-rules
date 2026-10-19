using System;
using System.Collections.Generic;
using Unity.ProjectAuditor.Editor;
using Unity.ProjectAuditor.Editor.Core;
using Unity.ProjectAuditor.Editor.Modules;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace Unity.ProjectAuditorRules.ShadersModuleAnalyzers
{
    internal class ShaderUrpRenderabilityAnalyzer : ShaderModuleAnalyzer
    {
        internal const string PAA5003 = nameof(PAA5003);
        internal const string PAA5004 = nameof(PAA5004);

        // LightMode pass tag values that URP's default (built-in) renderer draws. Includes the LWRP-era
        // "LightweightForward", which URP still draws for backward compatibility. This does NOT cover passes
        // drawn by custom ScriptableRenderPass / Renderer Features, which can draw any LightMode - a shader
        // rendered only that way may still be flagged. Kept as plain strings (not ShaderTagId) so no
        // Shader.TagToID call runs during static initialization, which is not allowed in the context where
        // analyzers are constructed; ShaderTagId is built at analysis time.
        static readonly string[] k_UrpLightModeTagNames =
        {
            "SRPDefaultUnlit",
            "UniversalForward",
            "UniversalForwardOnly",
            "UniversalGBuffer",
            "Universal2D",
            "LightweightForward",
        };

        // Legacy BiRP LightMode pass tags that have no direct URP equivalent.
        static readonly HashSet<string> k_LegacyLightModeTags = new HashSet<string>(new[]
        {
            "ForwardBase",
            "ForwardAdd",
            "Deferred",
            "Vertex",
            "VertexLM",
            "VertexLMRGBM",
            "Always"
        }, StringComparer.OrdinalIgnoreCase);

        // Returns the canonical (conventionally-cased) spelling from k_LegacyLightModeTags matching
        // lightModeName case-insensitively, or null if it isn't one of them.
        static string FindCanonicalLegacyLightModeTag(string lightModeName)
        {
            return k_LegacyLightModeTags.TryGetValue(lightModeName, out var canonical) ? canonical : null;
        }

        internal static readonly Descriptor k_UrpRenderabilityDescriptor = new Descriptor
            (
            PAA5003,
            "Shader has no pass that URP can render",
            Areas.MigrationToURP,
            "This shader has no pass that the Universal Render Pipeline's default renderer draws (no UniversalForward or SRPDefaultUnlit LightMode pass in a URP-eligible subshader), so under URP a material that uses it would be drawn with the error (magenta) shader. Passes drawn only by a custom ScriptableRenderPass or Renderer Feature are not considered.",
            "If using URP, convert or rewrite the shader for URP, for example as a Shader Graph or a URP-compatible shader. If the shader is not used with URP, no change is required."
            )
        {
            MessageFormat = "Shader '{0}' has no URP-compatible pass and would render with the URP error shader{1}",
#if UNITY_6000_7_OR_NEWER
            DocumentationUrl = MigrationToURPUtilities.DocumentationUrl
#endif
        };

        internal static readonly Descriptor k_UnreachableBirpFallbackDescriptor = new Descriptor
            (
            PAA5004,
            "Shader has a Built-in Render Pipeline pass that URP never draws",
            Areas.MigrationToURP,
            "This shader has a pass tagged with a legacy Built-in Render Pipeline LightMode (for example <b>ForwardBase</b>) that the Universal Render Pipeline " +
            "never draws - either because it sits in a SubShader positioned after the one URP selects (URP never evaluates one past the one it selects), or " +
            "because it is a stray leftover pass alongside a working one in the SubShader URP does select. This is not a URP compatibility problem, but the " +
            "pass is dead code that can be removed once the project no longer needs to support the Built-in Render Pipeline.",
            "If the pass is the SubShader's only content, remove the whole SubShader; if the SubShader also contains a working URP pass, remove only the " +
            "legacy-tagged pass instead - removing the whole SubShader in that case would also delete the working pass."
            )
        {
            MessageFormat = "Shader '{0}' has a Built-in Render Pipeline pass tagged '{1}' that URP never draws{2}",
            DefaultSeverity = Severity.Minor,
#if UNITY_6000_7_OR_NEWER
            DocumentationUrl = MigrationToURPUtilities.DocumentationUrl
#endif
        };

        const string k_LightModeTagName = "LightMode";
        const string k_RenderPipelineTagName = "RenderPipeline";
        const string k_UniversalPipelineTagValue = "UniversalPipeline";

        public override void Initialize(Action<Descriptor> registerDescriptor)
        {
            registerDescriptor(k_UrpRenderabilityDescriptor);
            registerDescriptor(k_UnreachableBirpFallbackDescriptor);
        }

        public override IEnumerable<ReportItem> Analyze(ShaderAnalysisContext context)
        {
            var shader = context.Shader;
            if (shader == null)
                yield break;

            // Package shaders are upstream / read-only and not the user's migration concern (for example URP's
            // own internal Hidden/* shaders). ShadersModule collects with "t:shader" (no assets-only scope), so
            // Packages/ shaders reach us and must be filtered here.
            if (context.AssetPath.StartsWith("Packages/", StringComparison.Ordinal))
                yield break;

            // A shader that already fails to compile is reported separately as a Shader error, and its pass
            // tags can be unreliable. Skip it here to avoid misleading, duplicate noise.
            if (ShaderUtil.ShaderHasError(shader))
                yield break;

            // Computed once and shared by both rules below - see the class comment for what this means.
            var selectedSubshaderIndex = FindUrpSelectedSubshaderIndex(shader);
            var isRenderable = IsRenderableByUrp(shader, selectedSubshaderIndex, out var legacyLightModeTag);

            if (context.IsDescriptorEnabled(k_UrpRenderabilityDescriptor) && !isRenderable)
            {
                var legacyTagSuffix = string.IsNullOrEmpty(legacyLightModeTag)
                    ? string.Empty
                    : $" (found legacy pass tag '{legacyLightModeTag}')";

                yield return context.CreateIssue(IssueCategory.AssetIssue,
                    k_UrpRenderabilityDescriptor.Id, shader.name, legacyTagSuffix)
                    .WithLocation(context.AssetPath);
            }

            if (context.IsDescriptorEnabled(k_UnreachableBirpFallbackDescriptor))
            {
                // A legacy pass tag in the selected SubShader itself is only worth a separate cleanup note
                // when that SubShader is otherwise renderable - if it isn't, PAA5003 already names the tag
                // above. A stray legacy pass sitting alongside a working one is dead code too, just like one
                // in a later SubShader: URP never draws it either way.
                var unreachableLegacyTag = FindUnreachableLegacyFallbackTag(shader, selectedSubshaderIndex,
                    includeSelectedSubshader: isRenderable, out var foundInSelectedSubshader);
                if (!string.IsNullOrEmpty(unreachableLegacyTag))
                {
                    // Removing the whole SubShader is only safe advice when the tagged pass is the only thing
                    // in it - if it's sharing the selected SubShader with a working pass, that pass would be
                    // deleted too.
                    var cleanupAdvice = foundInSelectedSubshader
                        ? " (alongside a working pass in the same SubShader - remove only this pass)"
                        : " (in a SubShader URP never reaches - the whole SubShader can be removed)";

                    yield return context.CreateIssue(IssueCategory.AssetIssue,
                        k_UnreachableBirpFallbackDescriptor.Id, shader.name, unreachableLegacyTag, cleanupAdvice)
                        .WithLocation(context.AssetPath);
                }
            }
        }

        // Returns the index of the SubShader URP would select (the first one, in declaration order, whose
        // RenderPipeline tag is absent or UniversalPipeline), or -1 if none is eligible.
        internal static int FindUrpSelectedSubshaderIndex(Shader shader)
        {
            // Built here (analysis time), not in a static initializer - see k_UrpLightModeTagNames.
            var renderPipelineTag = new ShaderTagId(k_RenderPipelineTagName);

            for (int subshaderIndex = 0; subshaderIndex < shader.subshaderCount; subshaderIndex++)
            {
                var renderPipeline = shader.FindSubshaderTagValue(subshaderIndex, renderPipelineTag);
                if (renderPipeline == ShaderTagId.none ||
                    string.Equals(renderPipeline.name, k_UniversalPipelineTagValue, StringComparison.OrdinalIgnoreCase))
                    return subshaderIndex;
            }

            return -1;
        }

        // True if the subshader URP would select (selectedSubshaderIndex, or none if < 0) has a pass URP
        // draws. Within that subshader, an untagged pass (LightMode == none) is treated by URP as
        // SRPDefaultUnlit for backward compatibility, so it counts as renderable. This does not model GPU or
        // Shader LOD support, which can also influence which subshader URP finally selects.
        static bool IsRenderableByUrp(Shader shader, int selectedSubshaderIndex, out string legacyLightModeTag)
        {
            legacyLightModeTag = null;

            if (selectedSubshaderIndex < 0)
                return false;

            var lightModeTag = new ShaderTagId(k_LightModeTagName);

            int passCount = shader.GetPassCountInSubshader(selectedSubshaderIndex);
            for (int passIndex = 0; passIndex < passCount; passIndex++)
            {
                var lightMode = shader.FindPassTagValue(selectedSubshaderIndex, passIndex, lightModeTag);

                // Untagged pass -> SRPDefaultUnlit (legacy) -> URP renders it.
                if (lightMode == ShaderTagId.none)
                    return true;

                var lightModeName = lightMode.name;
                for (int t = 0; t < k_UrpLightModeTagNames.Length; t++)
                {
                    if (string.Equals(lightModeName, k_UrpLightModeTagNames[t], StringComparison.OrdinalIgnoreCase))
                        return true;
                }

                if (legacyLightModeTag == null)
                    legacyLightModeTag = FindCanonicalLegacyLightModeTag(lightModeName);
            }

            // The selected subshader has no pass URP draws -> error shader.
            return false;
        }

        // Returns the first legacy LightMode tag value found in a SubShader URP never draws a pass from:
        // either a later SubShader (URP never evaluates one past the one it selects), or - when
        // includeSelectedSubshader is true - a stray pass in the selected SubShader itself, alongside the
        // pass(es) that make it otherwise renderable. Either way the tagged pass is dead code, not a URP
        // compatibility problem. foundInSelectedSubshader distinguishes the two cases, since only the first
        // (a fully unreachable later SubShader) is safe to remove in its entirety - the second shares its
        // SubShader with a pass that is still in use.
        static string FindUnreachableLegacyFallbackTag(Shader shader, int selectedSubshaderIndex, bool includeSelectedSubshader,
            out bool foundInSelectedSubshader)
        {
            foundInSelectedSubshader = false;

            if (selectedSubshaderIndex < 0)
                return null;

            var lightModeTag = new ShaderTagId(k_LightModeTagName);
            var startSubshaderIndex = includeSelectedSubshader ? selectedSubshaderIndex : selectedSubshaderIndex + 1;

            for (int subshaderIndex = startSubshaderIndex; subshaderIndex < shader.subshaderCount; subshaderIndex++)
            {
                int passCount = shader.GetPassCountInSubshader(subshaderIndex);
                for (int passIndex = 0; passIndex < passCount; passIndex++)
                {
                    var lightMode = shader.FindPassTagValue(subshaderIndex, passIndex, lightModeTag);
                    if (lightMode == ShaderTagId.none)
                        continue;

                    var lightModeName = lightMode.name;
                    var canonical = FindCanonicalLegacyLightModeTag(lightModeName);
                    if (canonical != null)
                    {
                        foundInSelectedSubshader = subshaderIndex == selectedSubshaderIndex;
                        return canonical;
                    }
                }
            }

            return null;
        }
    }
}
