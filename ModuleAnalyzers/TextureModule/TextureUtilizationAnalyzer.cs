using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using Unity.ProjectAuditor.Editor;
using Unity.ProjectAuditor.Editor.Core;
using Unity.ProjectAuditor.Editor.Utils;
using UnityEditor;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using Object = UnityEngine.Object;
#if UNITY_6000_6_OR_NEWER
using Unity.ProjectAuditor.Editor.AssetAnalysis;
#endif

namespace Unity.ProjectAuditorRules.TextureModuleAnalyzers
{
    class TextureUtilizationAnalyzer : TextureModuleAnalyzer
    {
        internal const string PAA0005 = nameof(PAA0005);
        internal const string PAA0007 = nameof(PAA0007);
        internal const string PAA0010 = nameof(PAA0010);

        internal static readonly Descriptor k_TextureSolidColorDescriptor = new Descriptor(
            PAA0005,
            "Texture: Solid color is not 1x1 size",
            Areas.Memory,
            "The texture is a single, solid color and is bigger than 1x1 pixels in size. Redundant texture data occupies memory unnecessarily.",
            "Consider shrinking the texture to 1x1 size."
        )
        {
            IsEnabledByDefault = false,
            MessageFormat = "Texture '{0}' is a solid color and not 1x1 size",
            Fixer = (issue, analysisParams) => { return ShrinkSolidTexture(issue.RelativePath); }
        };

        internal static readonly Descriptor k_TextureAtlasEmptyDescriptor = new Descriptor(
            PAA0007,
            "Texture Atlas: Too much empty space",
            Areas.Memory,
            "The texture atlas contains a lot of fully transparent pixels. Empty space contributes to texture memory usage.",
            "Consider reorganizing your texture atlas in order to reduce the amount of empty space."
        )
        {
            IsEnabledByDefault = false,
            MessageFormat = "Texture Atlas '{0}' has too much empty space ({1}, {2})"
        };

        internal static readonly Descriptor k_TextureTransparentBorderDescriptor = new Descriptor(
            PAA0010,
            "Texture: Large transparent border",
            Areas.Memory | Areas.GPU,
            "A large part of the texture is a fully transparent border around the visible content. The transparent area wastes texture memory and, when drawn on a quad that covers the whole texture, costs fill rate and overdraw.",
            "Crop the transparent border from the texture and use a smaller quad or sprite mesh (or a tighter sprite mesh type) instead of drawing the transparent pixels."
        )
        {
            IsEnabledByDefault = false,
            MessageFormat = "Texture '{0}' has a transparent border covering {1} of the image ({2})"
        };

#pragma warning disable CS0649
        [DiagnosticParameter("TextureEmptySpaceLimit", "Empty Texture Atlas threshold (percentage)", "Raise an issue if the percentage of fully transparent pixels in a Texture Atlas is greater than this threshold.", 50)]
        int m_EmptySpaceLimit;

        [DiagnosticParameter("TextureTransparentBorderLimit", "Transparent Texture Border threshold (percentage)", "Raise an issue if the percentage of a non-atlas texture that lies outside the bounding box of its non-transparent pixels is greater than this threshold.", 75)]
        int m_TransparentBorderLimit;
#pragma warning restore CS0649

        public override void Initialize(Action<Descriptor> registerDescriptor)
        {
            registerDescriptor(k_TextureSolidColorDescriptor);
            registerDescriptor(k_TextureAtlasEmptyDescriptor);
            registerDescriptor(k_TextureTransparentBorderDescriptor);
        }

        public override IEnumerable<ReportItem> Analyze(TextureAnalysisContext context)
        {
            if (context.IsDescriptorEnabled(k_TextureSolidColorDescriptor) &&
                IsTextureSolidColorTooBig(context.Importer, context.Texture))
            {
                var location = new Location(context.Importer.assetPath);
#if UNITY_6000_6_OR_NEWER
                var dependencyNode = new TextureDependencyNode { Location = location };
#endif

                yield return context.CreateIssue(IssueCategory.AssetIssue, k_TextureSolidColorDescriptor.Id, context.Name)
#if UNITY_6000_6_OR_NEWER
                    .WithDependencies(dependencyNode)
#endif
                    .WithLocation(location);
            }

            var texture2D = context.Texture as Texture2D;
            if (texture2D == null)
                yield break;

            // Atlases (multi-sprite textures) are expected to be packed densely; everything else is
            // checked for a transparent border that should have been cropped.
            var isAtlas = context.Importer.spriteImportMode == SpriteImportMode.Multiple;
            var descriptor = isAtlas ? k_TextureAtlasEmptyDescriptor : k_TextureTransparentBorderDescriptor;
            if (!context.IsDescriptorEnabled(descriptor))
                yield break;

            if (!TryGetAlphaStats(texture2D, out var stats))
                yield break;

            var percent = isAtlas ? stats.TransparentPercent : stats.BorderPercent;
            var pixels = isAtlas ? stats.TransparentPixels : stats.BorderPixels;
            var limit = isAtlas ? m_EmptySpaceLimit : m_TransparentBorderLimit;
            if (percent <= limit)
                yield break;

            var bytes = (ulong)(pixels * GetBytesPerPixel(texture2D));
            var assetLocation = new Location(context.Importer.assetPath);
#if UNITY_6000_6_OR_NEWER
            var assetDependencyNode = new TextureDependencyNode { Location = assetLocation };
#endif

            yield return context.CreateIssue(IssueCategory.AssetIssue, descriptor.Id, context.Name, Formatting.FormatPercentage(percent / 100.0f), Formatting.FormatSize(bytes))
#if UNITY_6000_6_OR_NEWER
                .WithDependencies(assetDependencyNode)
#endif
                .WithLocation(assetLocation);
        }

        static bool ShrinkSolidTexture(string path)
        {
            var textureImporter = AssetImporter.GetAtPath(path) as TextureImporter;
            if (textureImporter == null)
                return false;

            // Only plain 2D textures can be fixed; arrays, 3D textures and cubemaps load as null here.
            var texture = AssetDatabase.LoadAssetAtPath<Texture2D>(path);
            if (texture == null)
                return false;

            Color color;
            if (textureImporter.isReadable)
            {
                color = texture.GetPixel(0, 0);
            }
            else
            {
                var copy = CopyTexture(texture);
                color = copy.GetPixel(0, 0);
                UnityEngine.Object.DestroyImmediate(copy);
            }

            if (!TryEncodeForExtension(path, color, out var encoded))
                return false;

            File.WriteAllBytes(path, encoded);
            textureImporter.SaveAndReimport();
            return true;
        }

        static bool TryEncodeForExtension(string path, Color color, out byte[] encoded)
        {
            encoded = null;

            var extension = Path.GetExtension(path).ToLowerInvariant();

            var newTexture = new Texture2D(1, 1, TextureFormat.RGBA32, false);
            newTexture.SetPixel(0, 0, color);
            newTexture.Apply();

            try
            {
                switch (extension)
                {
                    case ".png":
                        encoded = newTexture.EncodeToPNG();
                        break;
                    case ".jpg":
                    case ".jpeg":
                        encoded = newTexture.EncodeToJPG();
                        break;
                    case ".tga":
                        encoded = newTexture.EncodeToTGA();
                        break;
                    case ".exr":
                        encoded = newTexture.EncodeToEXR();
                        break;
                    default:
                        // Unsupported source format (e.g. .psd, .gif): don't overwrite the original asset.
                        return false;
                }
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(newTexture);
            }

            return encoded != null;
        }

        static bool IsTextureSolidColorTooBig(TextureImporter textureImporter, Texture texture)
        {
            if (texture == null)
            {
                Debug.LogWarning($"Could not load texture at {textureImporter.assetPath}");
                return false;
            }

            // Skip textures of unsupported dimensions
            if (!(
                texture.dimension == UnityEngine.Rendering.TextureDimension.Tex2D
                || texture.dimension == UnityEngine.Rendering.TextureDimension.Tex2DArray
                || texture.dimension == UnityEngine.Rendering.TextureDimension.Tex3D
                || texture.dimension == UnityEngine.Rendering.TextureDimension.Cube
                ))
                return false;

            // Skip textures which are child assets (fonts, embedded textures, etc.)
            if (!AssetDatabase.IsMainAsset(texture))
                return false;

            if (texture.width == 1 && texture.height == 1)
            {
                return false;
            }

            return IsSolidColorWithDimensionHandling(textureImporter, texture);
        }

        static bool IsSolidColorWithDimensionHandling(TextureImporter textureImporter, Texture texture)
        {
            bool isTooBig = false;

            // For non-readable textures, make it readable to use some functions (GetPixels())
            // For crunched textures, we need to convert them since a copy requires a size match, or skip the test
            switch (texture.dimension)
            {
                case UnityEngine.Rendering.TextureDimension.Tex2D:
                    {
                        Texture2D texture2D = texture as Texture2D;

                        if (textureImporter.crunchedCompression)
                        {
                            Texture2D convertTexture = new Texture2D(texture2D.width, texture2D.height, GetUncrunchedFormat(texture2D.format), false);
                            convertTexture.name = texture2D.name + " (temp)";
                            if (Graphics.ConvertTexture(texture2D, convertTexture))
                            {
                                isTooBig = IsSolidColor(convertTexture);
                            }
                            Object.DestroyImmediate(convertTexture);
                        }
                        else if (textureImporter.isReadable)
                        {
                            isTooBig = IsSolidColor(texture2D);
                        }
                        else
                        {
                            Texture2D copyTexture = CopyTexture(texture2D);
                            isTooBig = IsSolidColor(copyTexture);
                            Object.DestroyImmediate(copyTexture);
                        }

                        break;
                    }

                case UnityEngine.Rendering.TextureDimension.Tex2DArray:
                    {
                        Texture2DArray texture2DArray = texture as Texture2DArray;

                        if (textureImporter.crunchedCompression)
                        {
                            // Can't call Graphics.ConvertTexture with a src of Texture2DArray, so skip until/if we write a custom convert function
                        }
                        else if (textureImporter.isReadable)
                        {
                            isTooBig = IsSolidColor(texture2DArray);
                        }
                        else
                        {
                            Texture2DArray copyTexture = CopyTexture(texture2DArray);
                            isTooBig = IsSolidColor(copyTexture);
                            Object.DestroyImmediate(copyTexture);
                        }

                        break;
                    }

                case UnityEngine.Rendering.TextureDimension.Tex3D:
                    {
                        Texture3D texture3D = texture as Texture3D;

                        if (textureImporter.crunchedCompression)
                        {
                            // Can't call Graphics.ConvertTexture with a src of Texture3D, so skip until/if we write a custom convert function
                        }
                        else if (textureImporter.isReadable)
                        {
                            isTooBig = IsSolidColor(texture3D);
                        }
                        else
                        {
                            Texture3D copyTexture = CopyTexture(texture3D);
                            isTooBig = IsSolidColor(copyTexture);
                            Object.DestroyImmediate(copyTexture);
                        }

                        break;
                    }

                case UnityEngine.Rendering.TextureDimension.Cube:
                    {
                        Cubemap textureCube = texture as Cubemap;

                        if (textureImporter.crunchedCompression)
                        {
                            Cubemap convertTexture = new Cubemap(textureCube.width, GetUncrunchedFormat(textureCube.format), false);
                            convertTexture.name = textureCube.name + " (temp)";
                            if (Graphics.ConvertTexture(textureCube, convertTexture))
                            {
                                isTooBig = IsSolidColor(convertTexture);
                            }
                            Object.DestroyImmediate(convertTexture);
                        }
                        else if (textureImporter.isReadable)
                        {
                            isTooBig = IsSolidColor(textureCube);
                        }
                        else
                        {
                            Cubemap copyTexture = CopyTexture(textureCube);
                            isTooBig = IsSolidColor(copyTexture);
                            Object.DestroyImmediate(copyTexture);
                        }

                        break;
                    }
            }

            return isTooBig;
        }

        static bool IsSolidColor(Texture2D texture)
        {
            // Skip "degenerate" textures like font atlases
            if (texture.width == 0 || texture.height == 0)
                return false;
            if (texture.width == 1 && texture.height == 1)
                return false;

            //Optimization lines
            //As GetPixels function can be costly, run a first test to check if texture is not solid color
            //Pick a second in-range pixel: use (1,0) only when there is more than one column, otherwise (0,1).
            var pixel1 = texture.GetPixel(0, 0);
            var pixel2 = texture.width > 1 ? texture.GetPixel(1, 0) : texture.GetPixel(0, 1);

            if (pixel1 != pixel2)
                return false;

            Color32[] pixels = null;
            try
            {
                pixels = texture.GetPixels32();
            }
            catch (ArgumentException)
            {
                // in some cases, GetPixels32 fails with a "Texture X has no data." error and throws an exception
                return false;
            }

            // It is unlikely to get a null pixels array, but we should check just in case
            if (pixels == null)
            {
                Debug.LogWarning($"Could not read {texture.name}");
                return false;
            }

            // It is unlikely, but possible that we got this far and there are no pixels.
            var pixelCount = pixels.Length;
            if (pixelCount == 0)
            {
                Debug.LogWarning($"No pixels in {texture.name}");
                return false;
            }

            // Convert to int for faster comparison
            var colorValue = Color32ToInt.Convert(pixels[0]);
            for (var i = 1; i < pixelCount; i++)
            {
                var pixel = Color32ToInt.Convert(pixels[i]);
                if (pixel != colorValue)
                    return false;
            }

            return true;
        }

        static bool IsSolidColor(Texture2DArray texture)
        {
            // Skip "degenerate" textures like font atlases
            if (texture.width == 0 || texture.height == 0)
            {
                return false;
            }

            // It doesn't matter if all slices are the same solid color, just that they are all solid colors.
            for (int j = 0; j < texture.depth; ++j)
            {
                var pixels = texture.GetPixels32(j);

                // It is unlikely to get a null pixels array, but we should check just in case
                if (pixels == null)
                {
                    Debug.LogWarning($"Could not read {texture.name}");
                    return false;
                }

                // It is unlikely, but possible that we got this far and there are no pixels.
                var pixelCount = pixels.Length;
                if (pixelCount == 0)
                {
                    Debug.LogWarning($"No pixels in {texture.name}");
                    return false;
                }

                // Convert to int for faster comparison
                var colorValue = Color32ToInt.Convert(pixels[0]);
                for (var i = 1; i < pixelCount; i++)
                {
                    var pixel = Color32ToInt.Convert(pixels[i]);
                    if (pixel != colorValue)
                        return false;
                }
            }

            return true;
        }

        static bool IsSolidColor(Texture3D texture)
        {
            // Skip "degenerate" textures like font atlases
            if (texture.width == 0 || texture.height == 0)
            {
                return false;
            }

            // It doesn't matter if all slices are the same solid color, just that they are all solid colors.
            for (int j = 0; j < texture.depth; ++j)
            {
                var pixels = texture.GetPixels32(j);

                // It is unlikely to get a null pixels array, but we should check just in case
                if (pixels == null)
                {
                    Debug.LogWarning($"Could not read {texture.name}");
                    return false;
                }

                // It is unlikely, but possible that we got this far and there are no pixels.
                var pixelCount = pixels.Length;
                if (pixelCount == 0)
                {
                    Debug.LogWarning($"No pixels in {texture.name}");
                    return false;
                }

                // Convert to int for faster comparison
                var colorValue = Color32ToInt.Convert(pixels[0]);
                for (var i = 1; i < pixelCount; i++)
                {
                    var pixel = Color32ToInt.Convert(pixels[i]);
                    if (pixel != colorValue)
                        return false;
                }
            }

            return true;
        }

        static bool IsSolidColor(Cubemap texture)
        {
            // Skip "degenerate" textures like font atlases
            if (texture.width == 0 || texture.height == 0)
            {
                return false;
            }

            // It doesn't matter if all faces are the same solid color, just that they are all solid colors.
            for (int j = 0; j < 6; ++j)
            {
                var pixels = texture.GetPixels((CubemapFace)j);

                // It is unlikely to get a null pixels array, but we should check just in case
                if (pixels == null)
                {
                    Debug.LogWarning($"Could not read {texture.name}");
                    return false;
                }

                // It is unlikely, but possible that we got this far and there are no pixels.
                var pixelCount = pixels.Length;
                if (pixelCount == 0)
                {
                    Debug.LogWarning($"No pixels in {texture.name}");
                    return false;
                }

                var colorValue = pixels[0];
                for (var i = 1; i < pixelCount; i++)
                {
                    if (pixels[i] != colorValue)
                        return false;
                }
            }

            return true;
        }

        struct AlphaStats
        {
            public int TotalPixels;

            // Pixels that are fully transparent (alpha == 0), wherever they are in the texture.
            public int TransparentPixels;

            // Pixels outside the bounding box of the non-transparent pixels, i.e. the fully transparent
            // border that could be cropped away. All pixels if the texture is entirely transparent.
            public int BorderPixels;

            public float TransparentPercent => 100.0f * TransparentPixels / TotalPixels;
            public float BorderPercent => 100.0f * BorderPixels / TotalPixels;
        }

        static bool TryGetAlphaStats(Texture2D texture2D, out AlphaStats stats)
        {
            stats = default;

            if (texture2D == null || texture2D.width == 0 || texture2D.height == 0)
                return false;

            Color32[] pixels;

            if (texture2D.isReadable)
            {
                pixels = texture2D.GetPixels32();
            }
            else
            {
                var copyTexture = CopyTexture(texture2D);
                if (copyTexture == null)
                {
                    Debug.LogWarning($"Could not copy {texture2D.name}");
                    return false;
                }

                try
                {
                    pixels = copyTexture.GetPixels32();
                }
                catch (ArgumentException)
                {
                    // in some cases, GetPixels32 fails with a "Texture X has no data." error and throws an exception
                    return false;
                }
                finally
                {
                    //Release texture from Memory
                    Object.DestroyImmediate(copyTexture);
                }
            }

            // It is unlikely to get a null pixels array, but we should check just in case
            if (pixels == null)
            {
                Debug.LogWarning($"Could not read {texture2D.name}");
                return false;
            }

            // It is unlikely, but possible that we got this far and there are no pixels.
            if (pixels.Length == 0)
            {
                Debug.LogWarning($"No pixels in {texture2D.name}");
                return false;
            }

            var width = texture2D.width;
            var height = texture2D.height;
            var transparentCount = 0;
            int minX = width, minY = height, maxX = -1, maxY = -1;
            for (var y = 0; y < height; y++)
            {
                var row = y * width;
                for (var x = 0; x < width; x++)
                {
                    if (pixels[row + x].a == 0)
                    {
                        transparentCount++;
                        continue;
                    }

                    if (x < minX) minX = x;
                    if (x > maxX) maxX = x;
                    if (y < minY) minY = y;
                    if (y > maxY) maxY = y;
                }
            }

            var contentPixels = maxX < 0 ? 0 : (maxX - minX + 1) * (maxY - minY + 1);
            stats.TotalPixels = pixels.Length;
            stats.TransparentPixels = transparentCount;
            stats.BorderPixels = pixels.Length - contentPixels;
            return true;
        }

        static Texture2D CopyTexture(Texture2D texture)
        {
            Texture2D newTexture;

            // CopyTexture seems to not want to work with Crunch textures, so take the long route via RT.
            if (texture.format != GetUncrunchedFormat(texture.format))
            {
                RenderTexture tmp = RenderTexture.GetTemporary(
                    texture.width,
                    texture.height,
                    0,
                    RenderTextureFormat.Default,
                    RenderTextureReadWrite.Linear);

                Graphics.Blit(texture, tmp);
                RenderTexture previous = RenderTexture.active;
                RenderTexture.active = tmp;
                newTexture = new Texture2D(texture.width, texture.height);
                newTexture.ReadPixels(new Rect(0, 0, tmp.width, tmp.height), 0, 0);
                newTexture.Apply();
                RenderTexture.active = previous;
                RenderTexture.ReleaseTemporary(tmp);
            }
            else
            {
                newTexture = new Texture2D(texture.width, texture.height, texture.format, texture.mipmapCount != 1);
                Graphics.CopyTexture(texture, newTexture);
            }

            newTexture.name = texture.name + " (temp)";

            return newTexture;
        }

        static Texture2DArray CopyTexture(Texture2DArray texture)
        {
            Texture2DArray newTexture = new Texture2DArray(texture.width, texture.height, texture.depth, texture.format, texture.mipmapCount != 1);
            newTexture.name = texture.name + " (temp)";
            Graphics.CopyTexture(texture, newTexture);

            return newTexture;
        }

        static Texture3D CopyTexture(Texture3D texture)
        {
            Texture3D newTexture = new Texture3D(texture.width, texture.height, texture.depth, texture.format, texture.mipmapCount != 1);
            newTexture.name = texture.name + " (temp)";
            Graphics.CopyTexture(texture, newTexture);

            return newTexture;
        }

        static Cubemap CopyTexture(Cubemap texture)
        {
            Cubemap newTexture = new Cubemap(texture.width, texture.format, texture.mipmapCount != 1);
            newTexture.name = texture.name + " (temp)";
            Graphics.CopyTexture(texture, newTexture);

            return newTexture;
        }

        static TextureFormat GetUncrunchedFormat(TextureFormat format)
        {
            TextureFormat localFormat = format;

            switch (localFormat)
            {
                case TextureFormat.DXT1Crunched:
                    {
                        localFormat = TextureFormat.DXT1;

                        break;
                    }

                case TextureFormat.DXT5Crunched:
                    {
                        localFormat = TextureFormat.DXT5;

                        break;
                    }

                case TextureFormat.ETC2_RGBA8Crunched:
                    {
                        localFormat = TextureFormat.ETC2_RGBA8;

                        break;
                    }

                case TextureFormat.ETC_RGB4Crunched:
                    {
                        localFormat = TextureFormat.ETC_RGB4;

                        break;
                    }
            }

            return localFormat;
        }

        static float GetBytesPerPixel(Texture2D tex)
        {
            // Calculate the total size of the texture in bytes, including mipmaps
            long totalBytes = GraphicsFormatUtility.ComputeMipChainSize(
                tex.width,
                tex.height,
                tex.graphicsFormat,
                tex.mipmapCount
            );

            // Divide by total pixels to get bytes per pixel
            return (float)totalBytes / (tex.width * tex.height);
        }

        [StructLayout(LayoutKind.Explicit)]
        struct Color32ToInt
        {
            [FieldOffset(0)] int m_Int;
            [FieldOffset(0)] Color32 m_Color;

            public int Int => m_Int;
            public Color32 Color => m_Color;

            Color32ToInt(Color32 color)
            {
                m_Int = 0;
                m_Color = color;
            }

            Color32ToInt(int value)
            {
                m_Color = default;
                m_Int = value;
            }

            public static int Convert(Color32 color)
            {
                var convert = new Color32ToInt(color);
                return convert.m_Int;
            }

            public static Color32 Convert(int value)
            {
                var convert = new Color32ToInt(value);
                return convert.m_Color;
            }
        }
    }
}
