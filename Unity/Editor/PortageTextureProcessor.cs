using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Portage
{
    /// <summary>
    /// Copies exported textures into the project, configures their importers and builds the packed
    /// mask texture URP Lit needs. One instance serves one import run (it caches by source).
    /// </summary>
    public sealed class PortageTextureProcessor
    {
        /// <summary>Source channel of a packing input.</summary>
        public enum SourceChannel
        {
            R = 0,
            G = 1,
            B = 2,
            A = 3,
        }

        /// <summary>A file-based mask input: one channel of one image on disk.</summary>
        public sealed class MaskSource
        {
            public string Path = "";
            public SourceChannel Channel;
            public bool Invert;

            public string Key => $"{Path}|{Channel}|{(Invert ? 1 : 0)}";
        }

        /// <summary>The up to three inputs of a packed mask. Null means "not available".</summary>
        public sealed class MaskRecipe
        {
            public MaskSource Metallic;
            public MaskSource Smoothness;
            public MaskSource Occlusion;

            public bool IsEmpty => Metallic == null && Smoothness == null && Occlusion == null;
        }

        /// <summary>A decoded mask input for <see cref="PackMask"/>. Pixels are in Unity row order.</summary>
        public sealed class PixelSource
        {
            public Color32[] Pixels;
            public int Width;
            public int Height;
            public SourceChannel Channel;
            public bool Invert;
        }

        /// <summary>A decoded color mask image, see <see cref="LoadColorMask"/>.</summary>
        public sealed class ColorMaskImage
        {
            public Color32[] Pixels;
            public int Width;
            public int Height;

            /// <summary>Whether the mask texture is sRGB-encoded.</summary>
            public bool Srgb;

            public bool IsUniform;

            /// <summary>Mask value in 0..1 (linear) when <see cref="IsUniform"/>.</summary>
            public float UniformValue;
        }

        readonly string _exportFolder;
        readonly string _textureFolder;
        readonly PortageMappingTable _table;
        readonly PortageImportReport _report;

        readonly Dictionary<string, string> _importedTextures = new Dictionary<string, string>();
        readonly Dictionary<string, string> _masksByKey = new Dictionary<string, string>();
        readonly Dictionary<string, string> _keyByMaskPath = new Dictionary<string, string>();
        readonly Dictionary<string, string> _bakedByKey = new Dictionary<string, string>();
        readonly Dictionary<string, string> _keyByBakedPath = new Dictionary<string, string>();

        // The most recently decoded color mask: instances of one master material usually share theirs.
        // Only one is kept so a run never holds more than one decoded mask in memory.
        string _lastColorMaskPath;
        ColorMaskImage _lastColorMask;

        public PortageTextureProcessor(string exportFolder, string textureFolder, PortageMappingTable table,
            PortageImportReport report)
        {
            _exportFolder = exportFolder;
            _textureFolder = textureFolder;
            _table = table;
            _report = report;
        }

        /// <summary>Absolute path of an exported file given its manifest-relative path.</summary>
        public string ResolveExportPath(string relativeFile)
        {
            return System.IO.Path.GetFullPath(System.IO.Path.Combine(_exportFolder, relativeFile ?? ""));
        }

        /// <summary>
        /// Copies a texture into the project and configures its importer. Returns the asset path,
        /// or null (with an error in the report) when the source file is missing.
        /// </summary>
        public string ImportTexture(PortageManifest.TextureEntry entry)
        {
            if (_importedTextures.TryGetValue(entry.id, out string cached))
                return cached;

            string source = ResolveExportPath(entry.file);
            if (!File.Exists(source))
            {
                _report.Error("texture.missingFile", entry.id, $"Texture file not found: {source}");
                _importedTextures[entry.id] = null;
                return null;
            }

            string assetPath = _textureFolder + "/" + System.IO.Path.GetFileName(source);
            PortageAssetUtil.CopyIfChanged(source, PortageAssetUtil.ToAbsolutePath(assetPath));
            AssetDatabase.ImportAsset(assetPath, ImportAssetOptions.ForceSynchronousImport);

            var importer = AssetImporter.GetAtPath(assetPath) as TextureImporter;
            if (importer == null)
            {
                _report.Error("texture.importFailed", entry.id, $"Unity could not import {assetPath} as a texture.");
                _importedTextures[entry.id] = null;
                return null;
            }

            bool dirty = false;
            if (entry.IsNormalMap)
            {
                if (importer.textureType != TextureImporterType.NormalMap)
                {
                    importer.textureType = TextureImporterType.NormalMap;
                    dirty = true;
                }

                bool flip = _table.flipNormalGreen ^ entry.flipGreenChannel;
                if (importer.flipGreenChannel != flip)
                {
                    importer.flipGreenChannel = flip;
                    dirty = true;
                }
            }
            else if (importer.textureType != TextureImporterType.Default)
            {
                importer.textureType = TextureImporterType.Default;
                dirty = true;
            }

            if (importer.sRGBTexture != entry.srgb)
            {
                importer.sRGBTexture = entry.srgb;
                dirty = true;
            }

            if (dirty)
                importer.SaveAndReimport();

            _importedTextures[entry.id] = assetPath;
            _report.CreatedTextures.Add(assetPath);
            return assetPath;
        }

        /// <summary>
        /// Packs the sources into a mask PNG in the texture folder and imports it. Returns the asset
        /// path, or null (with a report entry) on failure. The same recipe always yields the same asset
        /// within one run.
        /// </summary>
        /// <param name="baseName">Name used for the file, <c>&lt;baseName&gt;_Mask.png</c>.</param>
        /// <param name="metallicFill">Value for the red channel when the recipe has no metallic source.</param>
        public string BuildMask(MaskRecipe recipe, string baseName, byte metallicFill, bool warnAboutBlackOcclusion = false)
        {
            if (recipe == null || recipe.IsEmpty)
                return null;

            string key = $"M:{recipe.Metallic?.Key}#S:{recipe.Smoothness?.Key}#O:{recipe.Occlusion?.Key}" +
                         (recipe.Metallic == null ? $"#F:{metallicFill}" : "");
            if (_masksByKey.TryGetValue(key, out string existing))
                return existing;

            PixelSource metallic = Load(recipe.Metallic, baseName);
            PixelSource smoothness = Load(recipe.Smoothness, baseName);
            PixelSource occlusion = Load(recipe.Occlusion, baseName);
            if ((recipe.Metallic != null && metallic == null)
                || (recipe.Smoothness != null && smoothness == null)
                || (recipe.Occlusion != null && occlusion == null))
            {
                _masksByKey[key] = null;
                return null;
            }

            Color32[] packed = PackMask(metallic, smoothness, occlusion, metallicFill, out int width, out int height);

            // Unreal mostly ignores material ambient occlusion under dynamic lighting, so packs ship
            // occlusion channels with large black areas that nobody noticed. URP applies occlusion to
            // all ambient light, which turns those areas black in shadow.
            if (occlusion != null && warnAboutBlackOcclusion)
            {
                int black = 0;
                foreach (Color32 pixel in packed)
                {
                    if (pixel.g < 13)
                        black++;
                }

                float fraction = black / (float)packed.Length;
                if (fraction > 0.1f)
                {
                    _report.Warning("mask.occlusionMostlyBlack", baseName,
                        $"{baseName}: {fraction:P0} of the occlusion channel is black, so those areas render black in " +
                        "shadow. If that looks wrong, turn off 'Apply ambient occlusion' in the import window.");
                }
            }

            string safeName = PortageAssetUtil.SanitizeFileName(baseName);
            string assetPath = _textureFolder + "/" + safeName + "_Mask.png";
            int suffix = 2;
            while (_keyByMaskPath.TryGetValue(assetPath, out string owner) && owner != key)
                assetPath = _textureFolder + "/" + safeName + "_Mask_" + suffix++ + ".png";

            TextureImporter importer = WriteAndImport(assetPath, packed, width, height, true, baseName);
            if (importer == null)
            {
                _masksByKey[key] = null;
                return null;
            }

            bool dirty = false;
            if (importer.textureType != TextureImporterType.Default)
            {
                importer.textureType = TextureImporterType.Default;
                dirty = true;
            }

            if (importer.sRGBTexture)
            {
                importer.sRGBTexture = false;
                dirty = true;
            }

            if (importer.alphaSource != TextureImporterAlphaSource.FromInput)
            {
                importer.alphaSource = TextureImporterAlphaSource.FromInput;
                dirty = true;
            }

            if (importer.alphaIsTransparency)
            {
                importer.alphaIsTransparency = false;
                dirty = true;
            }

            if (dirty)
                importer.SaveAndReimport();

            _masksByKey[key] = assetPath;
            _keyByMaskPath[assetPath] = key;
            _report.CreatedTextures.Add(assetPath);
            return assetPath;
        }

        /// <summary>
        /// Reads a color mask texture from the export folder (PNG or JPG only). Returns null, with an
        /// error in the report, when it cannot be read.
        /// </summary>
        public ColorMaskImage LoadColorMask(PortageManifest.TextureEntry mask, string owner)
        {
            string path = ResolveExportPath(mask.file);
            if (path == _lastColorMaskPath)
                return _lastColorMask;

            _lastColorMaskPath = path;
            _lastColorMask = null;
            Color32[] pixels = LoadPixels(path, owner, "color mask", out int width, out int height);
            if (pixels == null)
                return null;

            var image = new ColorMaskImage { Pixels = pixels, Width = width, Height = height, Srgb = mask.srgb };
            image.IsUniform = IsUniformMask(pixels, out byte value);
            float encoded = value / 255f;
            image.UniformValue = mask.srgb ? SrgbToLinear(encoded) : encoded;
            _lastColorMask = image;
            return image;
        }

        /// <summary>
        /// Bakes <c>base * lerp(1, tint, mask)</c> into <c>&lt;materialName&gt;_BaseColor.png</c> in the texture
        /// folder and imports it like a normal base color texture, with the sRGB setting of the base. Returns
        /// the asset path, or null (with a report entry) on failure. The same combination of base, mask and
        /// tint yields the same asset within one run; re-importing updates the file in place and keeps its GUID.
        /// </summary>
        /// <param name="tintLinear">Tint in linear space. Its alpha is ignored; components above 1 are clamped.</param>
        public string BakeTintedBaseColor(string materialName, PortageManifest.TextureEntry baseTexture,
            PortageManifest.TextureEntry maskTexture, ColorMaskImage mask, Color tintLinear)
        {
            string basePath = ResolveExportPath(baseTexture.file);
            // Invariant culture: a decimal comma could not be told apart from the separator.
            string key = FormattableString.Invariant(
                $"B:{basePath}|M:{ResolveExportPath(maskTexture.file)}|T:{tintLinear.r:0.#####},{tintLinear.g:0.#####},{tintLinear.b:0.#####}");
            if (_bakedByKey.TryGetValue(key, out string existing))
                return existing;

            Color32[] basePixels = LoadPixels(basePath, materialName, "base color texture", out int width, out int height);
            if (basePixels == null)
            {
                _bakedByKey[key] = null;
                return null;
            }

            Color32[] baked = TintBaseColor(basePixels, width, height, mask.Pixels, mask.Width, mask.Height, mask.Srgb,
                tintLinear, baseTexture.srgb);

            // ImportTexture copies exported textures to the same folder under their own file name, so a name
            // that one of them has is taken as well.
            string sourceFolder = System.IO.Path.GetDirectoryName(basePath);
            string safeName = PortageAssetUtil.SanitizeFileName(materialName);
            string assetPath = _textureFolder + "/" + safeName + "_BaseColor.png";
            int suffix = 2;
            while (File.Exists(System.IO.Path.Combine(sourceFolder, System.IO.Path.GetFileName(assetPath)))
                   || (_keyByBakedPath.TryGetValue(assetPath, out string owner) && owner != key))
                assetPath = _textureFolder + "/" + safeName + "_BaseColor_" + suffix++ + ".png";

            TextureImporter importer = WriteAndImport(assetPath, baked, width, height, false, materialName);
            if (importer == null)
            {
                _bakedByKey[key] = null;
                return null;
            }

            bool dirty = false;
            if (importer.textureType != TextureImporterType.Default)
            {
                importer.textureType = TextureImporterType.Default;
                dirty = true;
            }

            if (importer.sRGBTexture != baseTexture.srgb)
            {
                importer.sRGBTexture = baseTexture.srgb;
                dirty = true;
            }

            if (dirty)
                importer.SaveAndReimport();

            _bakedByKey[key] = assetPath;
            _keyByBakedPath[assetPath] = key;
            _report.CreatedTextures.Add(assetPath);
            return assetPath;
        }

        // Writes the pixels to a PNG at the asset path and imports it. Returns the importer, or null with an
        // error in the report.
        TextureImporter WriteAndImport(string assetPath, Color32[] pixels, int width, int height, bool linear,
            string owner)
        {
            var texture = new Texture2D(width, height, TextureFormat.RGBA32, false, linear);
            try
            {
                texture.SetPixels32(pixels);
                texture.Apply(false);
                PortageAssetUtil.WriteIfChanged(PortageAssetUtil.ToAbsolutePath(assetPath), texture.EncodeToPNG());
            }
            finally
            {
                Object.DestroyImmediate(texture);
            }

            AssetDatabase.ImportAsset(assetPath, ImportAssetOptions.ForceSynchronousImport);
            var importer = AssetImporter.GetAtPath(assetPath) as TextureImporter;
            if (importer == null)
                _report.Error("texture.importFailed", owner, $"Unity could not import {assetPath} as a texture.");
            return importer;
        }

        /// <summary>
        /// Pure pixel function: <c>out.rgb = encode(decode(base.rgb) * lerp(1, tint.rgb, m))</c>, alpha taken
        /// from the base. <c>m</c> is the red channel of the mask (converted to linear when
        /// <paramref name="maskIsSrgb"/>), sampled bilinearly by UV when the sizes differ. The result has the
        /// size of the base. The tint is linear; its alpha is ignored. The base is decoded and encoded as sRGB
        /// when <paramref name="baseIsSrgb"/>, and used as it is otherwise.
        /// </summary>
        public static Color32[] TintBaseColor(Color32[] basePixels, int baseWidth, int baseHeight,
            Color32[] maskPixels, int maskWidth, int maskHeight, bool maskIsSrgb, Color tintLinear,
            bool baseIsSrgb = true)
        {
            if (baseWidth <= 0 || baseHeight <= 0 || basePixels == null || basePixels.Length != baseWidth * baseHeight)
                throw new ArgumentException("The base pixels do not match the given size.");
            if (maskWidth <= 0 || maskHeight <= 0 || maskPixels == null || maskPixels.Length != maskWidth * maskHeight)
                throw new ArgumentException("The mask pixels do not match the given size.");

            // Mask red channel as linear 0..1, converted once per texel.
            var mask = new float[maskPixels.Length];
            for (int i = 0; i < mask.Length; i++)
            {
                float v = maskPixels[i].r / 255f;
                mask[i] = maskIsSrgb ? SrgbToLinear(v) : v;
            }

            var result = new Color32[basePixels.Length];
            bool sameSize = maskWidth == baseWidth && maskHeight == baseHeight;
            for (int y = 0; y < baseHeight; y++)
            {
                for (int x = 0; x < baseWidth; x++)
                {
                    int index = y * baseWidth + x;
                    float m = sameSize
                        ? mask[index]
                        : SampleBilinear(mask, maskWidth, maskHeight, (x + 0.5f) / baseWidth, (y + 0.5f) / baseHeight);

                    Color32 p = basePixels[index];
                    result[index] = new Color32(
                        TintChannel(p.r, Mathf.Lerp(1f, tintLinear.r, m), baseIsSrgb),
                        TintChannel(p.g, Mathf.Lerp(1f, tintLinear.g, m), baseIsSrgb),
                        TintChannel(p.b, Mathf.Lerp(1f, tintLinear.b, m), baseIsSrgb),
                        p.a);
                }
            }

            return result;
        }

        /// <summary>
        /// True when the red channel of the mask varies by at most 2/255. <paramref name="value"/> is the
        /// midpoint of the range.
        /// </summary>
        public static bool IsUniformMask(Color32[] pixels, out byte value)
        {
            value = 0;
            if (pixels == null || pixels.Length == 0)
                return false;

            byte min = 255, max = 0;
            foreach (Color32 pixel in pixels)
            {
                if (pixel.r < min)
                    min = pixel.r;
                if (pixel.r > max)
                    max = pixel.r;
            }

            value = (byte)((min + max + 1) / 2);
            return max - min <= 2;
        }

        /// <summary>Exact sRGB electro-optical transfer function, 0..1 in and out.</summary>
        public static float SrgbToLinear(float c)
        {
            return c <= 0.04045f ? c / 12.92f : Mathf.Pow((c + 0.055f) / 1.055f, 2.4f);
        }

        /// <summary>Exact inverse sRGB transfer function, 0..1 in and out.</summary>
        public static float LinearToSrgb(float c)
        {
            return c <= 0.0031308f ? c * 12.92f : 1.055f * Mathf.Pow(c, 1f / 2.4f) - 0.055f;
        }

        // sRGB bytes as linear values, computed once per value instead of once per texel.
        static readonly float[] SrgbDecodeTable = BuildSrgbDecodeTable();

        static float[] BuildSrgbDecodeTable()
        {
            var table = new float[256];
            for (int i = 0; i < table.Length; i++)
                table[i] = SrgbToLinear(i / 255f);
            return table;
        }

        static byte TintChannel(byte encoded, float factor, bool srgb)
        {
            // An untinted channel keeps its byte, which also skips the transfer functions.
            if (factor == 1f)
                return encoded;

            float linear = Mathf.Clamp01((srgb ? SrgbDecodeTable[encoded] : encoded / 255f) * factor);
            return (byte)Mathf.RoundToInt(Mathf.Clamp01(srgb ? LinearToSrgb(linear) : linear) * 255f);
        }

        static float SampleBilinear(float[] values, int width, int height, float u, float v)
        {
            float fx = Mathf.Clamp(u * width - 0.5f, 0f, width - 1f);
            float fy = Mathf.Clamp(v * height - 0.5f, 0f, height - 1f);
            int x0 = (int)fx, y0 = (int)fy;
            int x1 = Math.Min(x0 + 1, width - 1), y1 = Math.Min(y0 + 1, height - 1);
            float tx = fx - x0, ty = fy - y0;

            float top = Mathf.Lerp(values[y0 * width + x0], values[y0 * width + x1], tx);
            float bottom = Mathf.Lerp(values[y1 * width + x0], values[y1 * width + x1], tx);
            return Mathf.Lerp(top, bottom, ty);
        }

        // Decodes a PNG or JPG file from disk. Pixels are in Unity row order.
        Color32[] LoadPixels(string path, string owner, string what, out int width, out int height)
        {
            width = 0;
            height = 0;

            string extension = System.IO.Path.GetExtension(path).ToLowerInvariant();
            if (extension != ".png" && extension != ".jpg" && extension != ".jpeg")
            {
                _report.Error("mask.unsupportedFormat", owner,
                    $"Cannot read {what} '{path}': only .png and .jpg sources are supported. It was skipped.");
                return null;
            }

            if (!File.Exists(path))
            {
                _report.Error("texture.missingFile", owner, $"Texture file not found: {path}");
                return null;
            }

            var texture = new Texture2D(2, 2, TextureFormat.RGBA32, false, true);
            try
            {
                if (!texture.LoadImage(File.ReadAllBytes(path)))
                {
                    _report.Error("mask.decodeFailed", owner, $"Could not decode '{path}'.");
                    return null;
                }

                width = texture.width;
                height = texture.height;
                return texture.GetPixels32();
            }
            finally
            {
                Object.DestroyImmediate(texture);
            }
        }

        /// <summary>
        /// Pure pixel packing: output R = metallic, G = occlusion, B = 0, A = smoothness. Inputs may be
        /// null; a missing metallic becomes <paramref name="metallicFill"/>, missing occlusion and
        /// smoothness become 255. The output has the size of the largest input; smaller inputs are
        /// sampled by UV with nearest filtering.
        /// </summary>
        public static Color32[] PackMask(PixelSource metallic, PixelSource smoothness, PixelSource occlusion,
            byte metallicFill, out int width, out int height)
        {
            width = 0;
            height = 0;
            foreach (PixelSource source in new[] { metallic, smoothness, occlusion })
            {
                if (source == null)
                    continue;
                if (source.Width * source.Height > width * height)
                {
                    width = source.Width;
                    height = source.Height;
                }
            }

            if (width <= 0 || height <= 0)
                throw new ArgumentException("At least one non-empty source is required.");

            var result = new Color32[width * height];
            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    result[y * width + x] = new Color32(
                        Sample(metallic, x, y, width, height, metallicFill),
                        Sample(occlusion, x, y, width, height, 255),
                        0,
                        Sample(smoothness, x, y, width, height, 255));
                }
            }

            return result;
        }

        static byte Sample(PixelSource source, int x, int y, int width, int height, byte fallback)
        {
            if (source == null)
                return fallback;

            int sx = source.Width == width ? x : Math.Min(source.Width - 1, (int)((x + 0.5f) / width * source.Width));
            int sy = source.Height == height ? y : Math.Min(source.Height - 1, (int)((y + 0.5f) / height * source.Height));
            Color32 pixel = source.Pixels[sy * source.Width + sx];

            byte value;
            switch (source.Channel)
            {
                case SourceChannel.R: value = pixel.r; break;
                case SourceChannel.G: value = pixel.g; break;
                case SourceChannel.B: value = pixel.b; break;
                default: value = pixel.a; break;
            }

            return source.Invert ? (byte)(255 - value) : value;
        }

        PixelSource Load(MaskSource source, string owner)
        {
            if (source == null)
                return null;

            Color32[] pixels = LoadPixels(source.Path, owner, "mask source", out int width, out int height);
            if (pixels == null)
                return null;

            return new PixelSource
            {
                Pixels = pixels,
                Width = width,
                Height = height,
                Channel = source.Channel,
                Invert = source.Invert,
            };
        }
    }

    /// <summary>Small file and folder helpers shared by the importer classes.</summary>
    internal static class PortageAssetUtil
    {
        public static string ProjectRoot => Directory.GetParent(Application.dataPath).FullName;

        public static string ToAbsolutePath(string assetPath)
        {
            return Path.GetFullPath(Path.Combine(ProjectRoot, assetPath));
        }

        public static string SanitizeFileName(string name)
        {
            foreach (char c in Path.GetInvalidFileNameChars())
                name = name.Replace(c, '_');
            return string.IsNullOrEmpty(name) ? "Unnamed" : name;
        }

        /// <summary>Creates every missing folder of a project-relative path such as Assets/A/B.</summary>
        public static void EnsureFolder(string assetFolder)
        {
            string[] parts = assetFolder.Split('/');
            string current = parts[0];
            for (int i = 1; i < parts.Length; i++)
            {
                string next = current + "/" + parts[i];
                if (!AssetDatabase.IsValidFolder(next))
                    AssetDatabase.CreateFolder(current, parts[i]);
                current = next;
            }
        }

        /// <summary>Copies a file unless the destination already has identical content (avoids reimports).</summary>
        public static void CopyIfChanged(string source, string destination)
        {
            if (File.Exists(destination) && ContentEquals(File.ReadAllBytes(source), File.ReadAllBytes(destination)))
                return;

            Directory.CreateDirectory(Path.GetDirectoryName(destination));
            File.Copy(source, destination, true);
        }

        public static void WriteIfChanged(string destination, byte[] bytes)
        {
            if (File.Exists(destination) && ContentEquals(bytes, File.ReadAllBytes(destination)))
                return;

            Directory.CreateDirectory(Path.GetDirectoryName(destination));
            File.WriteAllBytes(destination, bytes);
        }

        static bool ContentEquals(byte[] a, byte[] b)
        {
            if (a.Length != b.Length)
                return false;
            for (int i = 0; i < a.Length; i++)
            {
                if (a[i] != b[i])
                    return false;
            }

            return true;
        }
    }
}
