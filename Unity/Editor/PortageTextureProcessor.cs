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

        readonly string _exportFolder;
        readonly string _textureFolder;
        readonly PortageMappingTable _table;
        readonly PortageImportReport _report;

        readonly Dictionary<string, string> _importedTextures = new Dictionary<string, string>();
        readonly Dictionary<string, string> _masksByKey = new Dictionary<string, string>();
        readonly Dictionary<string, string> _keyByMaskPath = new Dictionary<string, string>();

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

            var texture = new Texture2D(width, height, TextureFormat.RGBA32, false, true);
            try
            {
                texture.SetPixels32(packed);
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
            {
                _report.Error("texture.importFailed", baseName, $"Unity could not import {assetPath} as a texture.");
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

            string extension = System.IO.Path.GetExtension(source.Path).ToLowerInvariant();
            if (extension != ".png" && extension != ".jpg" && extension != ".jpeg")
            {
                _report.Error("mask.unsupportedFormat", owner,
                    $"Cannot pack '{source.Path}': only .png and .jpg sources are supported. The mask was skipped.");
                return null;
            }

            if (!File.Exists(source.Path))
            {
                _report.Error("texture.missingFile", owner, $"Texture file not found: {source.Path}");
                return null;
            }

            var texture = new Texture2D(2, 2, TextureFormat.RGBA32, false, true);
            try
            {
                if (!texture.LoadImage(File.ReadAllBytes(source.Path)))
                {
                    _report.Error("mask.decodeFailed", owner, $"Could not decode '{source.Path}'.");
                    return null;
                }

                return new PixelSource
                {
                    Pixels = texture.GetPixels32(),
                    Width = texture.width,
                    Height = texture.height,
                    Channel = source.Channel,
                    Invert = source.Invert,
                };
            }
            finally
            {
                Object.DestroyImmediate(texture);
            }
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
