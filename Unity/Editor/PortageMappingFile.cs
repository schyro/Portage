using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEngine;

namespace Portage
{
    /// <summary>
    /// The <c>portage-mapping.json</c> sidecar in an export folder: the parameter choices a user made in the
    /// import window. Field names mirror Docs/mapping-format.md so <see cref="JsonUtility"/> can read it.
    /// </summary>
    [Serializable]
    public sealed class PortageMappingFile
    {
        public const string FileName = "portage-mapping.json";

        /// <summary>Highest mapping format version this importer understands.</summary>
        public const int SupportedFormatVersion = 1;

        public int version = SupportedFormatVersion;
        public List<MaterialChoices> materials = new List<MaterialChoices>();

        /// <summary>Why the file on disk was ignored by <see cref="Load"/>, or empty when it was fine.</summary>
        [NonSerialized] public string LoadError = "";

        [Serializable]
        public sealed class MaterialChoices
        {
            public string baseMaterial = "";
            public List<TextureChoice> textures = new List<TextureChoice>();
            public List<ScalarChoice> scalars = new List<ScalarChoice>();
            public List<VectorChoice> vectors = new List<VectorChoice>();
        }

        [Serializable]
        public sealed class TextureChoice
        {
            public string parameter = "";
            public string usage = "";
            public string red = "None";
            public string green = "None";
            public string blue = "None";
            public string alpha = "None";
        }

        [Serializable]
        public sealed class ScalarChoice
        {
            public string parameter = "";
            public string property = "";
            public string conversion = "None";
        }

        [Serializable]
        public sealed class VectorChoice
        {
            public string parameter = "";
            public string property = "";
        }

        /// <summary>The key choices are stored under: the base material id, or the material id when it has none.</summary>
        public static string KeyOf(PortageManifest.MaterialEntry entry)
        {
            return string.IsNullOrEmpty(entry.baseMaterial) ? entry.id : entry.baseMaterial;
        }

        public static string PathIn(string exportFolder)
        {
            return Path.Combine(exportFolder ?? "", FileName);
        }

        /// <summary>
        /// Reads the sidecar from an export folder. A missing file gives an empty object. An unreadable file or
        /// one with a newer version also gives an empty object, with the reason in <see cref="LoadError"/>.
        /// </summary>
        public static PortageMappingFile Load(string exportFolder)
        {
            string path = PathIn(exportFolder);
            if (!File.Exists(path))
                return new PortageMappingFile();

            PortageMappingFile file;
            try
            {
                file = JsonUtility.FromJson<PortageMappingFile>(File.ReadAllText(path));
            }
            catch (ArgumentException e)
            {
                return Invalid($"{FileName} is not valid JSON and was ignored: {e.Message}");
            }
            catch (IOException e)
            {
                return Invalid($"{FileName} could not be read and was ignored: {e.Message}");
            }

            if (file == null)
                return Invalid($"{FileName} is empty and was ignored.");
            if (file.version > SupportedFormatVersion)
            {
                return Invalid($"{FileName} has format version {file.version}, newer than the supported version " +
                               $"{SupportedFormatVersion}, and was ignored. Update the Portage package.");
            }

            file.version = SupportedFormatVersion;
            file.materials = file.materials ?? new List<MaterialChoices>();
            return file;
        }

        static PortageMappingFile Invalid(string error)
        {
            return new PortageMappingFile { LoadError = error };
        }

        /// <summary>Writes the sidecar as pretty UTF-8 JSON without a byte order mark.</summary>
        public void Save(string exportFolder)
        {
            version = SupportedFormatVersion;
            string json = JsonUtility.ToJson(this, true).Replace("\r\n", "\n") + "\n";
            File.WriteAllText(PathIn(exportFolder), json, new UTF8Encoding(false));
        }

        /// <summary>Returns the choices stored for a base material key, or null.</summary>
        public MaterialChoices FindMaterial(string key)
        {
            return materials.Find(m => m.baseMaterial == key);
        }

        /// <summary>Returns the stored texture choice. Names are compared exactly.</summary>
        public TextureChoice FindTexture(string key, string parameter)
        {
            return FindMaterial(key)?.textures.Find(c => c.parameter == parameter);
        }

        public ScalarChoice FindScalar(string key, string parameter)
        {
            return FindMaterial(key)?.scalars.Find(c => c.parameter == parameter);
        }

        public VectorChoice FindVector(string key, string parameter)
        {
            return FindMaterial(key)?.vectors.Find(c => c.parameter == parameter);
        }

        public void SetTexture(string key, string parameter, TextureUsage usage, MaskChannel red = MaskChannel.None,
            MaskChannel green = MaskChannel.None, MaskChannel blue = MaskChannel.None, MaskChannel alpha = MaskChannel.None)
        {
            MaterialChoices material = GetOrAdd(key);
            TextureChoice choice = material.textures.Find(c => c.parameter == parameter);
            if (choice == null)
            {
                choice = new TextureChoice { parameter = parameter };
                material.textures.Add(choice);
            }

            bool mask = usage == TextureUsage.Mask;
            choice.usage = usage.ToString();
            choice.red = (mask ? red : MaskChannel.None).ToString();
            choice.green = (mask ? green : MaskChannel.None).ToString();
            choice.blue = (mask ? blue : MaskChannel.None).ToString();
            choice.alpha = (mask ? alpha : MaskChannel.None).ToString();
        }

        /// <summary>Stores a scalar choice. An empty property means the parameter is ignored.</summary>
        public void SetScalar(string key, string parameter, string property, ScalarConversion conversion)
        {
            MaterialChoices material = GetOrAdd(key);
            ScalarChoice choice = material.scalars.Find(c => c.parameter == parameter);
            if (choice == null)
            {
                choice = new ScalarChoice { parameter = parameter };
                material.scalars.Add(choice);
            }

            choice.property = property ?? "";
            choice.conversion = conversion.ToString();
        }

        /// <summary>Stores a vector choice. An empty property means the parameter is ignored.</summary>
        public void SetVector(string key, string parameter, string property)
        {
            MaterialChoices material = GetOrAdd(key);
            VectorChoice choice = material.vectors.Find(c => c.parameter == parameter);
            if (choice == null)
            {
                choice = new VectorChoice { parameter = parameter };
                material.vectors.Add(choice);
            }

            choice.property = property ?? "";
        }

        public void RemoveTexture(string key, string parameter)
        {
            FindMaterial(key)?.textures.RemoveAll(c => c.parameter == parameter);
        }

        /// <summary>Removes every stored choice of a base material.</summary>
        public void ClearMaterial(string key)
        {
            materials.RemoveAll(m => m.baseMaterial == key);
        }

        MaterialChoices GetOrAdd(string key)
        {
            MaterialChoices material = FindMaterial(key);
            if (material == null)
            {
                material = new MaterialChoices { baseMaterial = key };
                materials.Add(material);
            }

            return material;
        }

        /// <summary>Parses an enum name; unknown names are not accepted, so they count as "no choice".</summary>
        public static bool TryParseEnum<T>(string text, out T value) where T : struct
        {
            return Enum.TryParse(text, true, out value) && Enum.IsDefined(typeof(T), value);
        }

        /// <summary>Parses a mask channel name; anything unknown is <see cref="MaskChannel.None"/>.</summary>
        public static MaskChannel ParseChannel(string text)
        {
            return TryParseEnum(text, out MaskChannel channel) ? channel : MaskChannel.None;
        }

        public static ScalarConversion ParseConversion(string text)
        {
            return TryParseEnum(text, out ScalarConversion conversion) ? conversion : ScalarConversion.None;
        }
    }
}
