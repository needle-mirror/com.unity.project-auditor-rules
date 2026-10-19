#if UNITY_6000_7_OR_NEWER || !UNITY_6000_4_OR_NEWER // 6.7, and old package have OnAnalysisStarted
#define SUPPORTS_CACHE // without the cache, this analyzer is slower, but still works
#endif

using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using Unity.ProjectAuditor.Editor;
using Unity.ProjectAuditor.Editor.Core;
using Unity.ProjectAuditor.Editor.Modules;
using Unity.ProjectAuditorRules.ShadersModuleAnalyzers;
using UnityEditor;
using UnityEditor.PackageManager;
using UnityEngine;

namespace Unity.ProjectAuditorRules.ShaderModuleAnalyzers
{
    internal class ShaderSourceAnalyzer : ShaderModuleAnalyzer
    {
        internal const string PAA2001 = nameof(PAA2001);
        internal const string PAA2002 = nameof(PAA2002);
        internal const string PAA2003 = nameof(PAA2003);
        internal const string PAA2004 = nameof(PAA2004);

        // ShaderUtil.HasSurfaceShaders/HasFixedFunctionShaders and the matching Open*/generated-shader APIs are internal.
        static readonly MethodInfo s_HasSurfaceShadersMethod =
            typeof(ShaderUtil).GetMethod("HasSurfaceShaders", BindingFlags.NonPublic | BindingFlags.Static);
        static readonly MethodInfo s_HasFixedFunctionShadersMethod =
            typeof(ShaderUtil).GetMethod("HasFixedFunctionShaders", BindingFlags.NonPublic | BindingFlags.Static);
        static readonly MethodInfo s_OpenParsedSurfaceShaderMethod =
            typeof(ShaderUtil).GetMethod("OpenParsedSurfaceShader", BindingFlags.NonPublic | BindingFlags.Static);
        static readonly MethodInfo s_OpenGeneratedFixedFunctionShaderMethod =
            typeof(ShaderUtil).GetMethod("OpenGeneratedFixedFunctionShader", BindingFlags.NonPublic | BindingFlags.Static);

        internal static readonly Descriptor k_SurfaceShaderDescriptor = new Descriptor
            (
            PAA2001,
            "Shader is a surface shader that is not supported by URP",
            Areas.MigrationToURP,
            "This shader is a surface shader (it uses the <b>#pragma surface</b> directive). Surface shaders are a Built-in Render Pipeline concept that the Universal Render Pipeline does not compile.",
            "Rewrite the shader for URP, either as a Shader Graph or a hand-written URP shader. Alternatively, use one of the default URP shaders."
            )
        {
            MessageFormat = "Shader '{0}' is a surface shader",
            DefaultSeverity = Severity.Major,
#if UNITY_6000_7_OR_NEWER
            DocumentationUrl = MigrationToURPUtilities.DocumentationUrl,
#endif
#if UNITY_6000_5_OR_NEWER
            FixerLabel = "Convert Source",
#endif
            Fixer = FixSurfaceShader
        };

        internal static readonly Descriptor k_FixedFunctionShaderDescriptor = new Descriptor
            (
            PAA2002,
            "Shader is a fixed function shader that is not supported by URP",
            Areas.MigrationToURP,
            "This shader is written using the deprecated fixed function pipeline (<b>SetTexture</b> / texture combiners) instead of a programmable Cg/HLSL program. Fixed function shaders are a Built-in Render Pipeline concept that the Universal Render Pipeline does not support.",
            "Rewrite the shader for URP, either as a Shader Graph or a hand-written URP shader. Alternatively, use one of the default URP shaders."
            )
        {
            MessageFormat = "Shader '{0}' is a fixed function shader",
            DefaultSeverity = Severity.Major,
#if UNITY_6000_7_OR_NEWER
            DocumentationUrl = MigrationToURPUtilities.DocumentationUrl,
#endif
#if UNITY_6000_5_OR_NEWER
            FixerLabel = "Convert Source",
#endif
            Fixer = FixFixedFunctionShader
        };

        internal static readonly Descriptor k_GrabPassDescriptor = new Descriptor
            (
            PAA2003,
            "Shader uses GrabPass that is not supported by URP",
            Areas.MigrationToURP,
            "This shader uses <b>GrabPass</b>, which captures the screen contents into a texture. GrabPass is a Built-in Render Pipeline feature that the Universal Render Pipeline does not support.",
            "Replace GrabPass with a URP feature such as the opaque texture (camera color) accessed from a URP-compatible shader."
            )
        {
            MessageFormat = "Shader '{0}' uses GrabPass",
            DefaultSeverity = Severity.Major,
#if UNITY_6000_7_OR_NEWER
            DocumentationUrl = MigrationToURPUtilities.DocumentationUrl
#endif
        };

        internal static readonly Descriptor k_BirpIncludeDescriptor = new Descriptor
            (
            PAA2004,
            "Shader includes a Built-in Render Pipeline header",
            Areas.MigrationToURP,
            "This shader includes a Built-in Render Pipeline header (for example <b>UnityCG.cginc</b>). Hand-written shaders that include these headers rely on Built-in Render Pipeline HLSL and generally need rewriting for the Universal Render Pipeline.",
            "Rewrite the shader for URP, either as a Shader Graph or a hand-written URP shader. Alternatively, use one of the default URP shaders."
            )
        {
            MessageFormat = "Shader '{0}' includes Built-in Render Pipeline header '{1}'",
            DefaultSeverity = Severity.Major,
#if UNITY_6000_7_OR_NEWER
            DocumentationUrl = MigrationToURPUtilities.DocumentationUrl
#endif
        };

        static readonly HashSet<string> k_BirpIncludeHeaders = new HashSet<string>(new[]
        {
            // Core & Lighting
            "UnityCG.cginc",
            "AutoLight.cginc",
            "Lighting.cginc",

            // UI & 2D
            "UnityUI.cginc",
            "UnitySprites.cginc",

            // Standard / PBR
            "UnityPBSLighting.cginc",
            "UnityStandardBRDF.cginc",
            "UnityStandardCore.cginc",

            // Features & Terrain
            "UnityInstancing.cginc",
            "Tessellation.cginc",
            "TerrainEngine.cginc"
        }, StringComparer.OrdinalIgnoreCase);

        const string k_IncludeDirective = "#include";

        // Both '"path"' and '<path>' delimit an include path.
        static readonly char[] k_IncludePathDelimiters = { '"', '<' };

#if SUPPORTS_CACHE
        // Key: project-relative path of a scanned include file, Value: BiRP header that file includes
        // (directly or transitively), or null if none
        Dictionary<string, string> m_ScannedBirpIncludes = new Dictionary<string, string>();
#endif

        public override void Initialize(Action<Descriptor> registerDescriptor)
        {
            registerDescriptor(k_SurfaceShaderDescriptor);
            registerDescriptor(k_FixedFunctionShaderDescriptor);
            registerDescriptor(k_GrabPassDescriptor);
            registerDescriptor(k_BirpIncludeDescriptor);
        }

#if SUPPORTS_CACHE
        public override void OnAnalysisStarted()
        {
            m_ScannedBirpIncludes.Clear();
        }
#endif

        public override IEnumerable<ReportItem> Analyze(ShaderAnalysisContext context)
        {
            var shaderName = context.Shader.name;
            var location = new Location(context.AssetPath);

            // Fixed function shader
            if (context.IsDescriptorEnabled(k_FixedFunctionShaderDescriptor) && IsFixedFunctionShader(context.Shader))
            {
                yield return context.CreateIssue(IssueCategory.AssetIssue, k_FixedFunctionShaderDescriptor.Id, shaderName)
                    .WithLocation(location);
            }

            // Read the .shader's source.
            if (!TryReadShaderSource(context.AssetPath, out var source))
                yield break;

            // Surface shader
            if (context.IsDescriptorEnabled(k_SurfaceShaderDescriptor) && IsSurfaceShader(context.Shader, source))
            {
                yield return context.CreateIssue(IssueCategory.AssetIssue, k_SurfaceShaderDescriptor.Id, shaderName)
                    .WithLocation(location);
            }

            // GrabPass
            if (context.IsDescriptorEnabled(k_GrabPassDescriptor) && source.Contains("GrabPass"))
            {
                yield return context.CreateIssue(IssueCategory.AssetIssue, k_GrabPassDescriptor.Id, shaderName)
                    .WithLocation(location);
            }

            // Scan for BiRP includes, restricted to the SubShader URP would actually select - an #include
            // that lives only in a later Built-in Render Pipeline fallback SubShader is not a URP problem
            // (URP never reaches it); PAA5004 already flags that SubShader separately as harmless cleanup.
			if (context.IsDescriptorEnabled(k_BirpIncludeDescriptor))
            {
                var scopedSource = ScopeToUrpSelectedSubshader(context.Shader, source);
                var birpInclude = SearchForBirpHeaders(context.AssetPath, scopedSource);
                if (!string.IsNullOrEmpty(birpInclude))
                {
                    yield return context.CreateIssue(IssueCategory.AssetIssue, k_BirpIncludeDescriptor.Id, shaderName, birpInclude)
                        .WithLocation(location);
                }
			}
        }

        // Restricts the shader source to just the SubShader URP would select, so an #include that lives
        // only in a later SubShader isn't mistaken for a live URP problem (see the PAA2004 call site).
        // SubShader blocks are declared top-level and in order, so the boundary is simply the start of the
        // next "SubShader" keyword occurrence - this avoids needing a full brace-matching parser. Falls
        // back to the whole source if the selected SubShader, or its end boundary, can't be found this way.
        static string ScopeToUrpSelectedSubshader(Shader shader, string source)
        {
            var selectedIndex = ShaderUrpRenderabilityAnalyzer.FindUrpSelectedSubshaderIndex(shader);
            if (selectedIndex < 0)
                return source;

            // Search a masked copy (same length, so indices still apply to the original source) with string
            // literals and comments blanked out, so a shader name like "Custom/MySubShader" or a comment
            // mentioning "SubShader" isn't mistaken for an actual block declaration.
            var maskedSource = MaskStringsAndComments(source);

            var searchIndex = -1;
            for (var i = 0; i <= selectedIndex; i++)
            {
                searchIndex = FindSubShaderKeyword(maskedSource, searchIndex + 1);
                if (searchIndex < 0)
                    return source;
            }

            var nextSubshaderIndex = FindSubShaderKeyword(maskedSource, searchIndex + 1);
            return nextSubshaderIndex < 0 ? source : source.Substring(0, nextSubshaderIndex);
        }

        // Finds the next "SubShader" keyword occurrence at or after startIndex in text that has already had
        // string literals and comments masked out (see MaskStringsAndComments), requiring a word boundary on
        // both sides so it only matches the keyword itself, not as part of a longer identifier.
        static int FindSubShaderKeyword(string maskedSource, int startIndex)
        {
            const string k_SubShaderKeyword = "SubShader";

            var index = startIndex;
            while (true)
            {
                index = maskedSource.IndexOf(k_SubShaderKeyword, index, StringComparison.OrdinalIgnoreCase);
                if (index < 0)
                    return -1;

                var before = index - 1;
                var after = index + k_SubShaderKeyword.Length;
                if ((before < 0 || !IsIdentifierChar(maskedSource[before])) &&
                    (after >= maskedSource.Length || !IsIdentifierChar(maskedSource[after])))
                    return index;

                index++;
            }
        }

        static bool IsIdentifierChar(char c) => char.IsLetterOrDigit(c) || c == '_';

        // Blanks out the contents of ShaderLab string literals and // and /* */ comments (replacing each
        // character with a space, except newlines which are preserved), so searching for a ShaderLab keyword
        // in the result can't match text that only appears inside a quoted name or a comment. The output has
        // the same length as source, so an index found in it is still valid against the original source.
        static string MaskStringsAndComments(string source)
        {
            var result = new char[source.Length];
            var i = 0;

            while (i < source.Length)
            {
                var c = source[i];

                if (c == '"')
                {
                    result[i++] = ' ';
                    while (i < source.Length && source[i] != '"')
                        result[i] = source[i++] == '\n' ? '\n' : ' ';
                    if (i < source.Length)
                        result[i++] = ' '; // closing quote
                    continue;
                }

                if (c == '/' && i + 1 < source.Length && source[i + 1] == '/')
                {
                    while (i < source.Length && source[i] != '\n')
                        result[i++] = ' ';
                    continue;
                }

                if (c == '/' && i + 1 < source.Length && source[i + 1] == '*')
                {
                    result[i++] = ' ';
                    result[i++] = ' ';
                    while (i < source.Length && !(source[i] == '*' && i + 1 < source.Length && source[i + 1] == '/'))
                        result[i] = source[i++] == '\n' ? '\n' : ' ';
                    if (i < source.Length)
                        result[i++] = ' ';
                    if (i < source.Length)
                        result[i++] = ' ';
                    continue;
                }

                result[i] = c;
                i++;
            }

            return new string(result);
        }

        static bool IsSurfaceShader(Shader shader, string source)
        {
            if (shader != null && s_HasSurfaceShadersMethod != null)
                return (bool)s_HasSurfaceShadersMethod.Invoke(null, new object[] { shader });

            return source.Contains("#pragma surface");
        }

        static bool IsFixedFunctionShader(Shader shader)
        {
            if (shader == null || s_HasFixedFunctionShadersMethod == null)
                return false;

            return (bool)s_HasFixedFunctionShadersMethod.Invoke(null, new object[] { shader });
        }

        static bool FixSurfaceShader(ReportItem issue, AnalysisParams analysisParams)
        {
            return TryReplaceWithGeneratedShader(issue.RelativePath, s_HasSurfaceShadersMethod,
                s_OpenParsedSurfaceShaderMethod, "GeneratedFromSurfaceShader");
        }

        static bool FixFixedFunctionShader(ReportItem issue, AnalysisParams analysisParams)
        {
            return TryReplaceWithGeneratedShader(issue.RelativePath, s_HasFixedFunctionShadersMethod,
                s_OpenGeneratedFixedFunctionShaderMethod, "GeneratedFixedFunction");
        }

        // Asks the Editor to generate the programmable equivalent of the shader (the same code the Shader
        // Inspector's "Show generated code" button reveals), then copies it over the original source. The Editor
        // writes that generated code to a fixed, well-known path under Temp/.
        static bool TryReplaceWithGeneratedShader(string assetPath, MethodInfo hasMethod, MethodInfo openMethod, string tempFilePrefix)
        {
            if (hasMethod == null || openMethod == null)
                return false;

            var shader = AssetDatabase.LoadAssetAtPath<Shader>(assetPath);
            if (shader == null || !(bool)hasMethod.Invoke(null, new object[] { shader }))
                return false;

            openMethod.Invoke(null, new object[] { shader });

            var tempPath = $"Temp/{tempFilePrefix}-{shader.name}.shader";
            if (!File.Exists(tempPath))
                return false;

            try
            {
                File.Copy(tempPath, GetFilePath(assetPath), true);
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
            {
                return false;
            }

            AssetDatabase.ImportAsset(assetPath);
            return true;
        }

        // Returns the name of the first BiRP header reached from shaderPath, following its include chain, or null.
        string SearchForBirpHeaders(string shaderPath, string source)
        {
            var cycleCut = false;
            return SearchForBirpHeadersRecursive(shaderPath, source, new HashSet<string>(), ref cycleCut);
        }

        // visiting holds the files currently on the recursion stack, so that cyclic includes terminate.
        string SearchForBirpHeadersRecursive(string filePath, string source, HashSet<string> visiting, ref bool cycleCut)
        {
            var includes = FindIncludes(source);
            foreach (var include in includes)
            {
                if (IsBirpHeaderName(include, out var birpHeader))
                    return birpHeader;

                // An include path is relative to the file that includes it (or to the project root), so it
                // can only be resolved against the file we are currently scanning.
                if (!TryResolveIncludePath(filePath, include, out var includePath))
                    continue;

#if SUPPORTS_CACHE
                if (m_ScannedBirpIncludes.TryGetValue(includePath, out var scannedInclude))
                {
                    if (!string.IsNullOrEmpty(scannedInclude))
                        return scannedInclude;
                    continue;
                }
#endif

                // Already being scanned further up the chain: the file includes itself, directly or indirectly.
                if (!visiting.Add(includePath))
                {
                    cycleCut = true;
                    continue;
                }

                string nestedInclude = null;
                var nestedCycleCut = false;
                if (TryReadShaderSource(includePath, out var nestedSource))
                    nestedInclude = SearchForBirpHeadersRecursive(includePath, nestedSource, visiting, ref nestedCycleCut);

                visiting.Remove(includePath);
                cycleCut |= nestedCycleCut;

                if (!string.IsNullOrEmpty(nestedInclude))
                {
#if SUPPORTS_CACHE
                    m_ScannedBirpIncludes[includePath] = nestedInclude;
#endif
                    return nestedInclude;
                }

#if SUPPORTS_CACHE
                if (!nestedCycleCut)
                    m_ScannedBirpIncludes[includePath] = null;
#endif
            }

            return null;
        }

        // Resolves an include path the way the shader compiler does for project files: relative to the
        // directory of the file doing the including, then relative to the project root. Absolute paths and
        // files outside the project are not resolved, since only project sources can be read back.
        static bool TryResolveIncludePath(string includingFilePath, string includePath, out string resolvedPath)
        {
            resolvedPath = null;

            if (string.IsNullOrEmpty(includePath) || Path.IsPathRooted(includePath))
                return false;

            includePath = includePath.Replace('\\', '/');

            var includingFolder = string.IsNullOrEmpty(includingFilePath)
                ? null
                : Path.GetDirectoryName(includingFilePath).Replace('\\', '/');

            if (!string.IsNullOrEmpty(includingFolder))
            {
                var relativeToIncludingFile = NormalizePath(includingFolder + "/" + includePath);
                if (IsReadableShaderSource(relativeToIncludingFile, out _))
                {
                    resolvedPath = relativeToIncludingFile;
                    return true;
                }
            }

            // Includes are sometimes authored as project-relative paths ("Assets/Shaders/Common.hlsl").
            var relativeToProject = NormalizePath(includePath);
            if (IsReadableShaderSource(relativeToProject, out _))
            {
                resolvedPath = relativeToProject;
                return true;
            }

            return false;
        }

        // Collapses the '.' and '..' segments of a forward-slashed relative path, so that paths reached by
        // different include chains compare equal. A path that climbs above the project root keeps its leading
        // '..' segments and is therefore rejected by IsReadableShaderSource.
        static string NormalizePath(string path)
        {
            var segments = new List<string>();

            foreach (var segment in path.Split('/'))
            {
                if (segment.Length == 0 || segment == ".")
                    continue;

                if (segment == ".." && segments.Count > 0 && segments[segments.Count - 1] != "..")
                {
                    segments.RemoveAt(segments.Count - 1);
                    continue;
                }

                segments.Add(segment);
            }

            return string.Join("/", segments);
        }

        // Whether an asset path is a project source file we can read, and where it is on disk. Built-in
        // shaders surfaced by ShadersModule are not on disk; only project/package shaders are.
        static bool IsReadableShaderSource(string assetPath, out string filePath)
        {
            filePath = null;

            if (string.IsNullOrEmpty(assetPath))
                return false;

            if (!assetPath.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase) &&
                !assetPath.StartsWith("Packages/", StringComparison.OrdinalIgnoreCase))
                return false;

            filePath = GetFilePath(assetPath);
            return File.Exists(filePath);
        }

        // An "Assets/..." asset path doubles as a path relative to the project folder, but a
        // "Packages/<name>/..." one is virtual: only an embedded package lives under the project folder, so
        // the package has to say where it resolved to (Library/PackageCache for a registry package).
        static string GetFilePath(string assetPath)
        {
            if (!assetPath.StartsWith("Packages/", StringComparison.OrdinalIgnoreCase))
                return assetPath;

            var packageInfo = UnityEditor.PackageManager.PackageInfo.FindForAssetPath(assetPath);
            if (packageInfo == null || string.IsNullOrEmpty(packageInfo.resolvedPath))
                return assetPath;

            // packageInfo.assetPath is the "Packages/<name>" prefix of assetPath, resolvedPath its folder.
            return packageInfo.resolvedPath + assetPath.Substring(packageInfo.assetPath.Length);
        }

        static bool TryReadShaderSource(string assetPath, out string source)
        {
            source = null;

            if (!IsReadableShaderSource(assetPath, out var filePath))
                return false;

            try
            {
                source = File.ReadAllText(filePath);
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
            {
                return false;
            }

            return true;
        }

        // Whether an include names a BiRP header, and the header name as written in the include directive.
        static bool IsBirpHeaderName(string includePath, out string headerName)
        {
            var fileName = Path.GetFileName(includePath.Replace('\\', '/'));
            headerName = k_BirpIncludeHeaders.Contains(fileName) ? fileName : null;
            return headerName != null;
        }

        // Include paths as they are written in the source, in the order they appear.
        static List<string> FindIncludes(string source)
        {
            var result = new List<string>();

            foreach (var line in source.Split('\n'))
            {
                // Matches '#include' and '#include_with_pragmas'.
                var includeIndex = line.IndexOf(k_IncludeDirective, StringComparison.Ordinal);
                if (includeIndex < 0)
                    continue;

                // Ignore commented-out includes.
                var commentIndex = line.IndexOf("//", StringComparison.Ordinal);
                if (commentIndex >= 0 && commentIndex < includeIndex)
                    continue;

                var pathStart = line.IndexOfAny(k_IncludePathDelimiters, includeIndex + k_IncludeDirective.Length);
                if (pathStart < 0)
                    continue;

                var pathEnd = line.IndexOf(line[pathStart] == '"' ? '"' : '>', pathStart + 1);
                if (pathEnd <= pathStart + 1)
                    continue;

                result.Add(line.Substring(pathStart + 1, pathEnd - pathStart - 1).Trim());
            }

            return result;
        }
    }
}
