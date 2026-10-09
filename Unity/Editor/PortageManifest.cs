using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace Portage
{
    /// <summary>
    /// In-memory form of a Portage <c>manifest.json</c>. Field names mirror the JSON format
    /// described in Docs/manifest-format.md so the file can be read with <see cref="JsonUtility"/>.
    /// </summary>
    [Serializable]
    public sealed class PortageManifest
    {
        /// <summary>Highest manifest format version this importer understands.</summary>
        public const int SupportedFormatVersion = 1;

        public int formatVersion;
        public Generator generator = new Generator();
        public List<MeshEntry> meshes = new List<MeshEntry>();
        public List<MaterialEntry> materials = new List<MaterialEntry>();
        public List<TextureEntry> textures = new List<TextureEntry>();
        public List<IssueEntry> issues = new List<IssueEntry>();

        [Serializable]
        public sealed class Generator
        {
            public string name = "";
            public string version = "";
            public string engineVersion = "";
            public string exportedAt = "";
        }

        [Serializable]
        public sealed class MeshEntry
        {
            public string id = "";
            public string name = "";
            public string file = "";
            public List<SlotEntry> slots = new List<SlotEntry>();
        }

        [Serializable]
        public sealed class SlotEntry
        {
            public int index;
            public string name = "";
            public string material = "";
        }

        [Serializable]
        public sealed class MaterialEntry
        {
            public string id = "";
            public string name = "";
            public string kind = "";
            public string parent = "";
            public string baseMaterial = "";
            public string blendMode = "";
            public string shadingModel = "";
            public bool twoSided;
            public float opacityMaskClipValue;
            public List<ScalarParameter> scalarParameters = new List<ScalarParameter>();
            public List<VectorParameter> vectorParameters = new List<VectorParameter>();
            public List<TextureParameter> textureParameters = new List<TextureParameter>();
            public bool convertible;
        }

        [Serializable]
        public sealed class ScalarParameter
        {
            public string name = "";
            public float value;
        }

        [Serializable]
        public sealed class VectorParameter
        {
            public string name = "";
            public float[] value = new float[0];

            /// <summary>The value as a linear color. Missing components default to 0, alpha to 1.</summary>
            public Color ToColor()
            {
                float Component(int i, float fallback) => value != null && value.Length > i ? value[i] : fallback;
                return new Color(Component(0, 0f), Component(1, 0f), Component(2, 0f), Component(3, 1f));
            }
        }

        [Serializable]
        public sealed class TextureParameter
        {
            public string name = "";
            public string texture = "";
        }

        [Serializable]
        public sealed class TextureEntry
        {
            public string id = "";
            public string name = "";
            public string file = "";
            public int width;
            public int height;
            public bool srgb;
            public string compression = "";
            public bool flipGreenChannel;

            public bool IsNormalMap => string.Equals(compression, "Normalmap", StringComparison.OrdinalIgnoreCase);
        }

        [Serializable]
        public sealed class IssueEntry
        {
            public string severity = "";
            public string code = "";
            public string asset = "";
            public string message = "";
        }

        /// <summary>Returns the material with the given id, or null.</summary>
        public MaterialEntry FindMaterial(string id)
        {
            return string.IsNullOrEmpty(id) ? null : materials.Find(m => m.id == id);
        }

        /// <summary>Returns the texture with the given id, or null.</summary>
        public TextureEntry FindTexture(string id)
        {
            return string.IsNullOrEmpty(id) ? null : textures.Find(t => t.id == id);
        }

        /// <summary>
        /// Reads and validates a manifest file.
        /// </summary>
        /// <exception cref="PortageManifestException">
        /// The file is missing, is not valid JSON, or has an unsupported format version.
        /// </exception>
        public static PortageManifest Load(string manifestPath)
        {
            if (!File.Exists(manifestPath))
                throw new PortageManifestException($"Manifest not found: {manifestPath}");

            return Parse(File.ReadAllText(manifestPath));
        }

        /// <summary>
        /// Parses and validates manifest JSON.
        /// </summary>
        /// <exception cref="PortageManifestException">
        /// The text is not valid JSON, or has an unsupported format version.
        /// </exception>
        public static PortageManifest Parse(string json)
        {
            PortageManifest manifest;
            try
            {
                manifest = JsonUtility.FromJson<PortageManifest>(json);
            }
            catch (ArgumentException e)
            {
                throw new PortageManifestException($"Manifest is not valid JSON: {e.Message}");
            }

            if (manifest == null)
                throw new PortageManifestException("Manifest is empty.");
            if (manifest.formatVersion < 1)
                throw new PortageManifestException("Manifest has no valid formatVersion.");
            if (manifest.formatVersion > SupportedFormatVersion)
            {
                throw new PortageManifestException(
                    $"Manifest format version {manifest.formatVersion} is newer than the supported version " +
                    $"{SupportedFormatVersion}. Update the Portage package.");
            }

            return manifest;
        }
    }

    /// <summary>Thrown when a manifest cannot be read or is not supported.</summary>
    public sealed class PortageManifestException : Exception
    {
        public PortageManifestException(string message) : base(message)
        {
        }
    }
}
