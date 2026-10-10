using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace Portage.Tests
{
    /// <summary>
    /// Imports small exports that are built in code and checks how static switches gate the inputs of the
    /// generated URP Lit materials.
    /// </summary>
    public sealed class PortageSwitchImportTests
    {
        const string Root = "Assets/__PortageSwitchTests";
        const string Out = Root + "/Out";
        const string Textures = Out + "/Textures/";
        const string MasterId = "/Game/Test/M_Master.M_Master";
        const string PackedLayout = "ORMA(AO,Rough,Mat,Emis)";

        readonly List<string> _tempFolders = new List<string>();

        [SetUp]
        public void SetUp() => Cleanup();

        [TearDown]
        public void TearDown() => Cleanup();

        void Cleanup()
        {
            if (AssetDatabase.IsValidFolder(Root))
                AssetDatabase.DeleteAsset(Root);
            foreach (string folder in _tempFolders.Where(Directory.Exists))
                Directory.Delete(folder, true);
            _tempFolders.Clear();
        }

        // ---- Export builder ----

        sealed class Export
        {
            public string Folder;
            public readonly PortageManifest Manifest = new PortageManifest { formatVersion = 1 };

            public void AddTexture(string name, int width, int height, Color32[] pixels, bool srgb = false,
                string compression = "Default")
            {
                string directory = Path.Combine(Folder, "Textures");
                Directory.CreateDirectory(directory);
                var texture = new Texture2D(width, height, TextureFormat.RGBA32, false, true);
                try
                {
                    texture.SetPixels32(pixels);
                    texture.Apply(false);
                    File.WriteAllBytes(Path.Combine(directory, name + ".png"), texture.EncodeToPNG());
                }
                finally
                {
                    Object.DestroyImmediate(texture);
                }

                Manifest.textures.Add(new PortageManifest.TextureEntry
                {
                    id = TextureId(name), name = name, file = "Textures/" + name + ".png", width = width,
                    height = height, srgb = srgb, compression = compression,
                });
            }

            public static string TextureId(string name) => "/Game/Test/" + name + "." + name;

            public PortageManifest.MaterialEntry AddMaterial(string name, string baseMaterial = MasterId)
            {
                var entry = new PortageManifest.MaterialEntry
                {
                    id = "/Game/Test/" + name + "." + name, name = name, kind = "instance", baseMaterial = baseMaterial,
                    blendMode = "opaque", shadingModel = "defaultLit", convertible = true,
                };
                Manifest.materials.Add(entry);
                return entry;
            }

            public void Save()
            {
                File.WriteAllText(Path.Combine(Folder, "manifest.json"), JsonUtility.ToJson(Manifest, true));
            }
        }

        Export NewExport()
        {
            string folder = Path.Combine(Path.GetTempPath(), "portage-switch-" + System.Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(folder);
            _tempFolders.Add(folder);
            return new Export { Folder = folder };
        }

        static Color32[] Fill(int count, Color32 color)
        {
            return Enumerable.Repeat(color, count).ToArray();
        }

        // The textures most tests share.
        static Export WithCommonTextures(Export export)
        {
            export.AddTexture("T_Base", 2, 2, Fill(4, new Color32(255, 255, 255, 255)), true);
            export.AddTexture("T_Normal", 2, 2, Fill(4, new Color32(128, 128, 255, 255)), false, "Normalmap");
            export.AddTexture("T_ORM", 2, 2, Fill(4, new Color32(40, 200, 90, 255)));
            export.AddTexture("T_Metal", 2, 2, Fill(4, new Color32(90, 90, 90, 255)));
            return export;
        }

        static void Texture(PortageManifest.MaterialEntry entry, string parameter, string texture)
        {
            entry.textureParameters.Add(new PortageManifest.TextureParameter
                { name = parameter, texture = Export.TextureId(texture) });
        }

        static void Scalar(PortageManifest.MaterialEntry entry, string name, float value)
        {
            entry.scalarParameters.Add(new PortageManifest.ScalarParameter { name = name, value = value });
        }

        static void Vector(PortageManifest.MaterialEntry entry, string name, float r, float g, float b, float a = 1f)
        {
            entry.vectorParameters.Add(new PortageManifest.VectorParameter { name = name, value = new[] { r, g, b, a } });
        }

        static void Switch(PortageManifest.MaterialEntry entry, string name, bool value)
        {
            entry.staticSwitchParameters.Add(new PortageManifest.StaticSwitchParameter { name = name, value = value });
        }

        PortageImportReport Import(Export export)
        {
            export.Save();
            PortageImportReport report = PortageImporter.Import(export.Folder, Out, null);
            Assert.AreEqual(0, report.ErrorCount, string.Join("\n", report.Entries
                .Where(e => e.Severity == PortageSeverity.Error).Select(e => e.Code + ": " + e.Message)));
            return report;
        }

        static Material LoadMaterial(string name)
        {
            var material = AssetDatabase.LoadAssetAtPath<Material>(Out + "/Materials/" + name + ".mat");
            Assert.IsNotNull(material, name);
            return material;
        }

        static IEnumerable<PortageImportReport.Entry> EntriesOf(PortageImportReport report, string materialName)
        {
            return report.Entries.Where(e => e.Asset == "/Game/Test/" + materialName + "." + materialName);
        }

        static bool Has(PortageImportReport report, string code) => report.Entries.Any(e => e.Code == code);

        static bool Has(PortageImportReport report, string materialName, string code)
        {
            return EntriesOf(report, materialName).Any(e => e.Code == code);
        }

        static bool Exists(string assetPath) => File.Exists(Path.GetFullPath(assetPath));

        static Color32[] ReadPng(string assetPath)
        {
            var texture = new Texture2D(2, 2, TextureFormat.RGBA32, false, true);
            try
            {
                Assert.IsTrue(texture.LoadImage(File.ReadAllBytes(Path.GetFullPath(assetPath))), assetPath);
                return texture.GetPixels32();
            }
            finally
            {
                Object.DestroyImmediate(texture);
            }
        }

        static void AssertColor(Color expectedLinear, Color actual, float tolerance = 0.01f)
        {
            Color linear = actual.linear;
            Assert.AreEqual(expectedLinear.r, linear.r, tolerance, "r");
            Assert.AreEqual(expectedLinear.g, linear.g, tolerance, "g");
            Assert.AreEqual(expectedLinear.b, linear.b, tolerance, "b");
        }

        // The emissive property is HDR and stored linear.
        static void AssertHdr(Color expected, Color actual, float tolerance = 0.01f)
        {
            Assert.AreEqual(expected.r, actual.r, tolerance, "r");
            Assert.AreEqual(expected.g, actual.g, tolerance, "g");
            Assert.AreEqual(expected.b, actual.b, tolerance, "b");
        }

        // ---- Base color ----

        static Export BaseColorExport(Export export, string name, params (string name, bool value)[] switches)
        {
            WithCommonTextures(export);
            PortageManifest.MaterialEntry entry = export.AddMaterial(name);
            Texture(entry, "BaseColor", "T_Base");
            Vector(entry, "Tint", 1f, 0.9f, 0.8f);
            Vector(entry, "Base Color Constant", 0.2f, 0.4f, 0.6f, 0.3f);
            foreach ((string switchName, bool value) in switches)
                Switch(entry, switchName, value);
            return export;
        }

        [Test]
        public void BaseColorTextureOff_UsesTheConstantAsTheBaseColor()
        {
            Export export = BaseColorExport(NewExport(), "MI_Flat", ("Base Color Texture Input", false));

            PortageImportReport report = Import(export);

            Material material = LoadMaterial("MI_Flat");
            Assert.IsNull(material.GetTexture("_BaseMap"));
            AssertColor(new Color(0.2f, 0.4f, 0.6f), material.GetColor("_BaseColor"));
            Assert.AreEqual(1f, material.GetColor("_BaseColor").a, 0.001f, "the alpha of a constant is not an opacity");
            Assert.IsTrue(Has(report, "MI_Flat", "baseColor.constant"));
            Assert.IsTrue(Has(report, "MI_Flat", "parameter.switchedOff"));
            Assert.IsFalse(Exists(Textures + "T_Base.png"));
        }

        [Test]
        public void BaseColorTextureOn_IgnoresTheConstantAndAppliesTheTint()
        {
            Export export = BaseColorExport(NewExport(), "MI_Textured", ("Base Color Texture Input", true));

            PortageImportReport report = Import(export);

            Material material = LoadMaterial("MI_Textured");
            Assert.IsNotNull(material.GetTexture("_BaseMap"));
            AssertColor(new Color(1f, 0.9f, 0.8f), material.GetColor("_BaseColor"));
            Assert.IsFalse(Has(report, "baseColor.constant"));
            Assert.IsFalse(Has(report, "parameter.switchedOff"));
        }

        [Test]
        public void BaseColorSwitchAbsent_BehavesAsBefore()
        {
            Export export = BaseColorExport(NewExport(), "MI_NoSwitch");

            PortageImportReport report = Import(export);

            Material material = LoadMaterial("MI_NoSwitch");
            Assert.IsNotNull(material.GetTexture("_BaseMap"));
            AssertColor(new Color(1f, 0.9f, 0.8f), material.GetColor("_BaseColor"));
            Assert.IsFalse(Has(report, "baseColor.constant"));
            Assert.IsFalse(Has(report, "parameter.switchedOff"));
        }

        [Test]
        public void BaseColorTextureOff_WithoutAConstantLeavesTheTint()
        {
            Export export = WithCommonTextures(NewExport());
            PortageManifest.MaterialEntry entry = export.AddMaterial("MI_TintOnly");
            Texture(entry, "BaseColor", "T_Base");
            Vector(entry, "Tint", 1f, 0.9f, 0.8f);
            Switch(entry, "Base Color Texture Input", false);

            PortageImportReport report = Import(export);

            Material material = LoadMaterial("MI_TintOnly");
            Assert.IsNull(material.GetTexture("_BaseMap"));
            AssertColor(new Color(1f, 0.9f, 0.8f), material.GetColor("_BaseColor"));
            Assert.IsFalse(Has(report, "baseColor.constant"));
        }

        // ---- Normal ----

        [TestCase(true)]
        [TestCase(false)]
        public void NormalSwitch_GatesTheNormalMap(bool on)
        {
            Export export = WithCommonTextures(NewExport());
            PortageManifest.MaterialEntry entry = export.AddMaterial("MI_Normal");
            Texture(entry, "Normal", "T_Normal");
            Switch(entry, "Use Normal", on);

            PortageImportReport report = Import(export);

            Material material = LoadMaterial("MI_Normal");
            Assert.AreEqual(on, material.GetTexture("_BumpMap") != null);
            Assert.AreEqual(on, material.IsKeywordEnabled("_NORMALMAP"));
            Assert.AreEqual(!on, Has(report, "MI_Normal", "parameter.switchedOff"));
            Assert.AreEqual(on, Exists(Textures + "T_Normal.png"));
        }

        // ---- Color mask ----

        static Export ColorMaskExport(Export export, string name, bool colorMask, bool baseColor)
        {
            WithCommonTextures(export);
            export.AddTexture("T_TintMask", 2, 1, new[] { new Color32(0, 0, 0, 255), new Color32(255, 255, 255, 255) },
                false, "Masks");
            PortageManifest.MaterialEntry entry = export.AddMaterial(name);
            Texture(entry, "BaseColor", "T_Base");
            Texture(entry, "Color_Mask", "T_TintMask");
            Vector(entry, "Tint", 1f, 0.5f, 0.25f);
            Switch(entry, "Use Color Mask", colorMask);
            Switch(entry, "Base Color Texture Input", baseColor);
            return export;
        }

        [Test]
        public void ColorMaskOn_BakesTheMaskedTint()
        {
            Export export = ColorMaskExport(NewExport(), "MI_Masked", true, true);

            PortageImportReport report = Import(export);

            Material material = LoadMaterial("MI_Masked");
            Assert.IsTrue(Has(report, "MI_Masked", "colorMask.baked"));
            Assert.IsTrue(Exists(Textures + "MI_Masked_BaseColor.png"));
            Assert.IsNotNull(material.GetTexture("_BaseMap"));
        }

        [Test]
        public void ColorMaskOff_AppliesTheTintToTheWholeSurface()
        {
            Export export = ColorMaskExport(NewExport(), "MI_Unmasked", false, true);

            PortageImportReport report = Import(export);

            Material material = LoadMaterial("MI_Unmasked");
            Assert.IsFalse(Has(report, "colorMask.baked"));
            Assert.IsFalse(Exists(Textures + "MI_Unmasked_BaseColor.png"));
            Assert.IsNotNull(material.GetTexture("_BaseMap"));
            Assert.IsTrue(Exists(Textures + "T_Base.png"));
            AssertColor(new Color(1f, 0.5f, 0.25f), material.GetColor("_BaseColor"));
            Assert.IsTrue(Has(report, "MI_Unmasked", "parameter.switchedOff"));
        }

        [Test]
        public void BaseColorTextureOff_DoesNotUseTheColorMask()
        {
            Export export = ColorMaskExport(NewExport(), "MI_NoBase", true, false);

            PortageImportReport report = Import(export);

            Material material = LoadMaterial("MI_NoBase");
            Assert.IsFalse(Has(report, "colorMask.baked"));
            Assert.IsFalse(Exists(Textures + "MI_NoBase_BaseColor.png"));
            Assert.IsNull(material.GetTexture("_BaseMap"));
            Assert.IsTrue(Has(report, "MI_NoBase", "parameter.switchedOff"));
        }

        // ---- Metallic ----

        [TestCase(true)]
        [TestCase(false)]
        public void MetallicSwitch_GatesAMetallicOnlyTexture(bool on)
        {
            Export export = WithCommonTextures(NewExport());
            PortageManifest.MaterialEntry entry = export.AddMaterial("MI_Metal");
            Texture(entry, "Metallic", "T_Metal");
            Scalar(entry, "Metallic Constant", 0.6f);
            Switch(entry, "Metallic Texture Input", on);

            PortageImportReport report = Import(export);

            Material material = LoadMaterial("MI_Metal");
            Assert.AreEqual(on, material.GetTexture("_MetallicGlossMap") != null);
            Assert.AreEqual(on, material.IsKeywordEnabled("_METALLICSPECGLOSSMAP"));
            Assert.AreEqual(!on, Has(report, "MI_Metal", "parameter.switchedOff"));
            if (!on)
                Assert.AreEqual(0.6f, material.GetFloat("_Metallic"), 0.001f);
        }

        [Test]
        public void MetallicOn_PacksTheMetallicChannel()
        {
            Export export = WithCommonTextures(NewExport());
            PortageManifest.MaterialEntry entry = export.AddMaterial("MI_Packed");
            Texture(entry, "ORM", "T_ORM");
            Scalar(entry, "Metallic Constant", 0.6f);
            Switch(entry, "Metallic Texture Input", true);

            Import(export);

            Material material = LoadMaterial("MI_Packed");
            Assert.IsNotNull(material.GetTexture("_MetallicGlossMap"));
            Assert.AreEqual(90, ReadPng(Textures + "T_ORM_Mask.png")[0].r);
        }

        [Test]
        public void MetallicOff_DropsTheMetallicChannelFromThePackedMask()
        {
            Export export = WithCommonTextures(NewExport());
            PortageManifest.MaterialEntry entry = export.AddMaterial("MI_Packed");
            Texture(entry, "ORM", "T_ORM");
            Scalar(entry, "Metallic Constant", 0.6f);
            Switch(entry, "Metallic Texture Input", false);

            PortageImportReport report = Import(export);

            Material material = LoadMaterial("MI_Packed");
            Assert.IsNotNull(material.GetTexture("_MetallicGlossMap"), "smoothness is still packed");
            Assert.AreEqual(0.6f, material.GetFloat("_Metallic"), 0.001f);
            Color32 pixel = ReadPng(Textures + "T_ORM_Mask.png")[0];
            Assert.AreEqual(Mathf.RoundToInt(0.6f * 255f), pixel.r, "the red channel carries the metallic scalar");
            Assert.AreNotEqual(90, pixel.r);
            Assert.IsTrue(Has(report, "MI_Packed", "parameter.switchedOff"));
        }

        // ---- Roughness ----

        [Test]
        public void RoughnessOn_PacksTheSmoothnessChannel()
        {
            Export export = WithCommonTextures(NewExport());
            PortageManifest.MaterialEntry entry = export.AddMaterial("MI_Rough");
            Texture(entry, "ORM", "T_ORM");
            Scalar(entry, "Roughness Constant", 0.3f);
            Switch(entry, "Roughness Texture Input", true);

            PortageImportReport report = Import(export);

            Material material = LoadMaterial("MI_Rough");
            Assert.AreEqual(1f, material.GetFloat("_Smoothness"), 0.001f);
            Color32 pixel = ReadPng(Textures + "T_ORM_Mask.png")[0];
            Assert.AreEqual(255 - 200, pixel.a, 1);
            Assert.AreEqual(90, pixel.r);
            Assert.IsFalse(Has(report, "parameter.switchedOff"));
        }

        [Test]
        public void RoughnessOff_DropsTheSmoothnessChannelAndUsesTheScalar()
        {
            Export export = WithCommonTextures(NewExport());
            PortageManifest.MaterialEntry entry = export.AddMaterial("MI_Rough");
            Texture(entry, "ORM", "T_ORM");
            Scalar(entry, "Roughness Constant", 0.3f);
            Switch(entry, "Roughness Texture Input", false);

            PortageImportReport report = Import(export);

            Material material = LoadMaterial("MI_Rough");
            Assert.AreEqual(0.7f, material.GetFloat("_Smoothness"), 0.001f);
            Color32 pixel = ReadPng(Textures + "T_ORM_Mask.png")[0];
            Assert.AreEqual(255, pixel.a);
            Assert.AreEqual(90, pixel.r, "the metallic channel is unaffected");
            Assert.IsTrue(Has(report, "MI_Rough", "parameter.switchedOff"));
        }

        [Test]
        public void RoughnessOff_WithOnlyARoughnessTextureLeavesNoMask()
        {
            Export export = WithCommonTextures(NewExport());
            export.AddTexture("T_Rough", 2, 2, Fill(4, new Color32(200, 200, 200, 255)));
            PortageManifest.MaterialEntry entry = export.AddMaterial("MI_RoughOnly");
            Texture(entry, "Roughness", "T_Rough");
            Scalar(entry, "Roughness Constant", 0.25f);
            Switch(entry, "Roughness Texture Input", false);

            Import(export);

            Material material = LoadMaterial("MI_RoughOnly");
            Assert.IsNull(material.GetTexture("_MetallicGlossMap"));
            Assert.AreEqual(0.75f, material.GetFloat("_Smoothness"), 0.001f);
        }

        // ---- Occlusion ----

        [TestCase(true)]
        [TestCase(false)]
        public void OcclusionSwitch_GatesTheOcclusionMapEvenWhenApplyOcclusionIsOn(bool on)
        {
            Export export = WithCommonTextures(NewExport());
            PortageManifest.MaterialEntry entry = export.AddMaterial("MI_Ao");
            Texture(entry, "ORM", "T_ORM");
            Switch(entry, "Use AO", on);
            new PortageMappingFile { applyOcclusion = true }.Save(export.Folder);

            PortageImportReport report = Import(export);

            Material material = LoadMaterial("MI_Ao");
            Assert.IsNotNull(material.GetTexture("_MetallicGlossMap"));
            Assert.AreEqual(on, material.GetTexture("_OcclusionMap") != null);
            Assert.AreEqual(on, material.IsKeywordEnabled("_OCCLUSIONMAP"));
            Assert.AreEqual(!on, Has(report, "MI_Ao", "parameter.switchedOff"));
        }

        [Test]
        public void OcclusionOn_StillNeedsApplyOcclusion()
        {
            Export export = WithCommonTextures(NewExport());
            PortageManifest.MaterialEntry entry = export.AddMaterial("MI_Ao");
            Texture(entry, "ORM", "T_ORM");
            Switch(entry, "Use AO", true);

            Import(export);

            Assert.IsNull(LoadMaterial("MI_Ao").GetTexture("_OcclusionMap"));
        }

        // ---- Emission ----

        // 2x2 packed texture whose alpha holds the emissive mask.
        static readonly byte[] EmissiveAlpha = { 0, 85, 170, 255 };

        static Export EmissiveExport(Export export)
        {
            export.AddTexture("T_Packed", 2, 2, EmissiveAlpha.Select(a => new Color32(40, 200, 90, a)).ToArray());
            export.AddTexture("T_Glow", 2, 2, Fill(4, new Color32(255, 128, 0, 255)), true);
            return export;
        }

        // A material with an emission mask channel, a color and an intensity, and the given switches.
        static PortageManifest.MaterialEntry EmissiveMaterial(Export export, string name, bool emission,
            bool? emissionTexture = true, bool? separate = null, bool withSeparateTexture = false)
        {
            PortageManifest.MaterialEntry entry = export.AddMaterial(name);
            Texture(entry, PackedLayout, "T_Packed");
            if (withSeparateTexture)
                Texture(entry, "Unique_Emissive_Texture", "T_Glow");
            Vector(entry, "Emissive Constant", 1f, 0.5f, 0.25f);
            Scalar(entry, "Emissive_Intencity", 2f);
            Switch(entry, "Use Emissive", emission);
            if (emissionTexture.HasValue)
                Switch(entry, "Emissive Texture", emissionTexture.Value);
            if (separate.HasValue)
                Switch(entry, "Seperate_Emissive_Texture", separate.Value);
            return entry;
        }

        // The emissive texture parameter is mapped explicitly through the sidecar.
        static void MapSeparateEmissionTexture(Export export, PortageManifest.MaterialEntry entry)
        {
            var file = new PortageMappingFile();
            file.SetTexture(PortageMappingFile.KeyOf(entry), "Unique_Emissive_Texture", TextureUsage.Emission);
            file.Save(export.Folder);
        }

        [Test]
        public void EmissionOn_ExtractsTheMaskChannelIntoAnEmissionMap()
        {
            Export export = EmissiveExport(NewExport());
            EmissiveMaterial(export, "MI_Glow", true);

            PortageImportReport report = Import(export);

            Material material = LoadMaterial("MI_Glow");
            string path = Textures + "T_Packed_Emission.png";
            var map = AssetDatabase.LoadAssetAtPath<Texture2D>(path);
            Assert.IsNotNull(map);
            Assert.AreEqual(map, material.GetTexture("_EmissionMap"));
            Assert.IsFalse(((TextureImporter)AssetImporter.GetAtPath(path)).sRGBTexture);
            Assert.IsTrue(report.CreatedTextures.Contains(path));

            Color32[] pixels = ReadPng(path);
            Assert.AreEqual(4, pixels.Length);
            for (int i = 0; i < pixels.Length; i++)
            {
                Assert.AreEqual(EmissiveAlpha[i], pixels[i].r, "pixel " + i);
                Assert.AreEqual(EmissiveAlpha[i], pixels[i].g, "pixel " + i);
                Assert.AreEqual(EmissiveAlpha[i], pixels[i].b, "pixel " + i);
                Assert.AreEqual(255, pixels[i].a, "pixel " + i);
            }

            // Emission color times intensity, linear and HDR.
            AssertHdr(new Color(2f, 1f, 0.5f), material.GetColor("_EmissionColor"));
            Assert.IsTrue(material.IsKeywordEnabled("_EMISSION"));
            Assert.IsFalse(Has(report, "emission.textureMissing"));
        }

        [Test]
        public void EmissionOn_KeepsTheOtherChannelsOfThePackedMask()
        {
            Export export = EmissiveExport(NewExport());
            EmissiveMaterial(export, "MI_Glow", true);

            Import(export);

            Material material = LoadMaterial("MI_Glow");
            Assert.IsNotNull(material.GetTexture("_MetallicGlossMap"));
            Color32 pixel = ReadPng(Textures + "T_Packed_Mask.png")[0];
            Assert.AreEqual(90, pixel.r);
            Assert.AreEqual(255 - 200, pixel.a, 1);
        }

        [Test]
        public void EmissionTextureOff_UsesTheEmissionColorAlone()
        {
            Export export = EmissiveExport(NewExport());
            EmissiveMaterial(export, "MI_Glow", true, emissionTexture: false);

            PortageImportReport report = Import(export);

            Material material = LoadMaterial("MI_Glow");
            Assert.IsNull(material.GetTexture("_EmissionMap"));
            Assert.IsFalse(Exists(Textures + "T_Packed_Emission.png"));
            AssertHdr(new Color(2f, 1f, 0.5f), material.GetColor("_EmissionColor"));
            Assert.IsTrue(material.IsKeywordEnabled("_EMISSION"));
            Assert.IsTrue(Has(report, "MI_Glow", "parameter.switchedOff"));
            Assert.IsFalse(Has(report, "emission.textureMissing"));
        }

        [Test]
        public void SeparateEmissionTextureOn_UsesTheSeparateTextureInsteadOfTheMaskChannel()
        {
            Export export = EmissiveExport(NewExport());
            MapSeparateEmissionTexture(export,
                EmissiveMaterial(export, "MI_Glow", true, separate: true, withSeparateTexture: true));

            PortageImportReport report = Import(export);

            Material material = LoadMaterial("MI_Glow");
            Assert.AreEqual(AssetDatabase.LoadAssetAtPath<Texture2D>(Textures + "T_Glow.png"),
                material.GetTexture("_EmissionMap"));
            Assert.IsNotNull(material.GetTexture("_EmissionMap"));
            Assert.IsFalse(Exists(Textures + "T_Packed_Emission.png"));
            Assert.IsTrue(material.IsKeywordEnabled("_EMISSION"));
            Assert.IsTrue(Has(report, "MI_Glow", "parameter.switchedOff"));
        }

        [Test]
        public void SeparateEmissionTextureOff_UsesTheMaskChannelInsteadOfTheSeparateTexture()
        {
            Export export = EmissiveExport(NewExport());
            MapSeparateEmissionTexture(export,
                EmissiveMaterial(export, "MI_Glow", true, separate: false, withSeparateTexture: true));

            PortageImportReport report = Import(export);

            Material material = LoadMaterial("MI_Glow");
            Assert.AreEqual(AssetDatabase.LoadAssetAtPath<Texture2D>(Textures + "T_Packed_Emission.png"),
                material.GetTexture("_EmissionMap"));
            Assert.IsNotNull(material.GetTexture("_EmissionMap"));
            Assert.IsFalse(Exists(Textures + "T_Glow.png"));
            Assert.IsTrue(Has(report, "MI_Glow", "parameter.switchedOff"));
        }

        [Test]
        public void MappedEmissiveTexture_IsUsedWhenNoSeparateSwitchExists()
        {
            Export export = EmissiveExport(NewExport());
            MapSeparateEmissionTexture(export, EmissiveMaterial(export, "MI_Glow", true, withSeparateTexture: true));

            Import(export);

            Assert.AreEqual(AssetDatabase.LoadAssetAtPath<Texture2D>(Textures + "T_Glow.png"),
                LoadMaterial("MI_Glow").GetTexture("_EmissionMap"));
        }

        [Test]
        public void GuessedEmissiveTexture_IsUsedWhenNoSeparateSwitchExists()
        {
            Export export = EmissiveExport(NewExport());
            EmissiveMaterial(export, "MI_Glow", true, withSeparateTexture: true);

            Import(export);

            Assert.AreEqual(AssetDatabase.LoadAssetAtPath<Texture2D>(Textures + "T_Glow.png"),
                LoadMaterial("MI_Glow").GetTexture("_EmissionMap"));
            Assert.IsFalse(Exists(Textures + "T_Packed_Emission.png"));
        }

        [Test]
        public void GuessedEmissiveTexture_FollowsTheSeparateTextureSwitch([Values] bool separate)
        {
            Export export = EmissiveExport(NewExport());
            EmissiveMaterial(export, "MI_Glow", true, separate: separate, withSeparateTexture: true);

            PortageImportReport report = Import(export);

            string used = Textures + (separate ? "T_Glow.png" : "T_Packed_Emission.png");
            string unused = Textures + (separate ? "T_Packed_Emission.png" : "T_Glow.png");
            Assert.IsNotNull(AssetDatabase.LoadAssetAtPath<Texture2D>(used));
            Assert.AreEqual(AssetDatabase.LoadAssetAtPath<Texture2D>(used), LoadMaterial("MI_Glow").GetTexture("_EmissionMap"));
            Assert.IsFalse(Exists(unused));
            Assert.IsTrue(Has(report, "MI_Glow", "parameter.switchedOff"));
        }

        [Test]
        public void EmissionOff_DoesNotImportAGuessedEmissiveTexture()
        {
            Export export = EmissiveExport(NewExport());
            EmissiveMaterial(export, "MI_Dark", false, withSeparateTexture: true);

            PortageImportReport report = Import(export);

            Assert.IsNull(LoadMaterial("MI_Dark").GetTexture("_EmissionMap"));
            Assert.IsFalse(Exists(Textures + "T_Glow.png"));
            Assert.IsFalse(Has(report, "MI_Dark", "parameter.switchedOff"));
        }

        [Test]
        public void EmissionOnWithTextureSwitchButNoMap_WarnsAndLeavesTheColorBlack()
        {
            Export export = NewExport();
            PortageManifest.MaterialEntry entry = export.AddMaterial("MI_Dim");
            Vector(entry, "Emissive Constant", 1f, 0.5f, 0.25f);
            Scalar(entry, "Emissive_Intencity", 2f);
            Switch(entry, "Use Emissive", true);
            Switch(entry, "Emissive Texture", true);

            PortageImportReport report = Import(export);

            Material material = LoadMaterial("MI_Dim");
            Assert.IsNull(material.GetTexture("_EmissionMap"));
            AssertHdr(Color.black, material.GetColor("_EmissionColor"));
            Assert.IsFalse(material.IsKeywordEnabled("_EMISSION"));
            Assert.IsTrue(report.Entries.Any(e => e.Code == "emission.textureMissing" &&
                                                  e.Severity == PortageSeverity.Warning && e.Asset == entry.id));
        }

        [Test]
        public void EmissionOnWithoutTextureSwitchAndNoMap_UsesTheColor()
        {
            Export export = NewExport();
            PortageManifest.MaterialEntry entry = export.AddMaterial("MI_Lamp");
            Vector(entry, "Emissive Constant", 1f, 0.5f, 0.25f);
            Scalar(entry, "Emissive_Intencity", 3f);
            Switch(entry, "Use Emissive", true);

            PortageImportReport report = Import(export);

            Material material = LoadMaterial("MI_Lamp");
            AssertHdr(new Color(3f, 1.5f, 0.75f), material.GetColor("_EmissionColor"));
            Assert.IsTrue(material.IsKeywordEnabled("_EMISSION"));
            Assert.IsFalse(Has(report, "emission.textureMissing"));
        }

        [Test]
        public void EmissionOff_KeepsEverythingEmissiveOutSilently()
        {
            Export export = EmissiveExport(NewExport());
            EmissiveMaterial(export, "MI_Dark", false, withSeparateTexture: true);

            PortageImportReport report = Import(export);

            Material material = LoadMaterial("MI_Dark");
            Assert.IsNull(material.GetTexture("_EmissionMap"));
            AssertHdr(Color.black, material.GetColor("_EmissionColor"));
            Assert.IsFalse(material.IsKeywordEnabled("_EMISSION"));
            Assert.IsFalse(Exists(Textures + "T_Packed_Emission.png"));
            Assert.IsFalse(Exists(Textures + "T_Glow.png"));
            Assert.IsFalse(Has(report, "parameter.switchedOff"), "guessed emission parameters are silent");
            Assert.IsFalse(Has(report, "emission.textureMissing"));
        }

        [Test]
        public void EmissionOff_ReportsAnEmissionParameterThatWasMappedOnPurpose()
        {
            Export export = NewExport();
            PortageManifest.MaterialEntry entry = export.AddMaterial("MI_Dark");
            Vector(entry, "Glow Color", 1f, 0.5f, 0.25f);
            Switch(entry, "Use Emissive", false);
            var file = new PortageMappingFile();
            file.SetVector(PortageMappingFile.KeyOf(entry), "Glow Color", "_EmissionColor");
            file.Save(export.Folder);

            PortageImportReport report = Import(export);

            AssertHdr(Color.black, LoadMaterial("MI_Dark").GetColor("_EmissionColor"));
            Assert.IsTrue(report.Entries.Any(e => e.Code == "parameter.switchedOff" && e.Message.Contains("Glow Color")));
        }

        [Test]
        public void EmissionIntensityAlone_DoesNotMakeAMaterialGlow()
        {
            Export export = NewExport();
            PortageManifest.MaterialEntry entry = export.AddMaterial("MI_Dark");
            Scalar(entry, "Emissive_Intencity", 5f);
            Switch(entry, "Use Emissive", true);

            Import(export);

            Material material = LoadMaterial("MI_Dark");
            AssertHdr(Color.black, material.GetColor("_EmissionColor"));
            Assert.IsFalse(material.IsKeywordEnabled("_EMISSION"));
        }

        // ---- Materials of one base material ----

        [Test]
        public void MaterialsOfOneBaseMaterial_FollowTheirOwnSwitchValues()
        {
            Export export = EmissiveExport(WithCommonTextures(NewExport()));
            foreach ((string name, bool on) in new[] { ("MI_On", true), ("MI_Off", false) })
            {
                PortageManifest.MaterialEntry entry = EmissiveMaterial(export, name, on);
                Texture(entry, "Normal", "T_Normal");
                Texture(entry, "BaseColor", "T_Base");
                Switch(entry, "Use Normal", on);
                Vector(entry, "Base Color Constant", 0.2f, 0.4f, 0.6f);
                Switch(entry, "Base Color Texture Input", on);
            }

            PortageImportReport report = Import(export);

            Material on1 = LoadMaterial("MI_On");
            Material off = LoadMaterial("MI_Off");
            Assert.IsNotNull(on1.GetTexture("_BumpMap"));
            Assert.IsNull(off.GetTexture("_BumpMap"));
            Assert.IsTrue(on1.IsKeywordEnabled("_NORMALMAP"));
            Assert.IsFalse(off.IsKeywordEnabled("_NORMALMAP"));

            Assert.IsNotNull(on1.GetTexture("_BaseMap"));
            Assert.IsNull(off.GetTexture("_BaseMap"));
            AssertColor(new Color(0.2f, 0.4f, 0.6f), off.GetColor("_BaseColor"));
            AssertColor(Color.white, on1.GetColor("_BaseColor"));

            Assert.IsNotNull(on1.GetTexture("_EmissionMap"));
            Assert.IsNull(off.GetTexture("_EmissionMap"));
            Assert.Greater(on1.GetColor("_EmissionColor").maxColorComponent, 0f);
            Assert.AreEqual(0f, off.GetColor("_EmissionColor").maxColorComponent);

            Assert.IsFalse(Has(report, "MI_On", "parameter.switchedOff"));
            Assert.IsTrue(Has(report, "MI_Off", "parameter.switchedOff"));
        }

        // ---- Regression: no switches ----

        [Test]
        public void MaterialWithoutSwitches_ImportsAsBefore()
        {
            Export export = EmissiveExport(WithCommonTextures(NewExport()));
            PortageManifest.MaterialEntry entry = export.AddMaterial("MI_Plain");
            Texture(entry, "BaseColor", "T_Base");
            Texture(entry, "Normal", "T_Normal");
            Texture(entry, PackedLayout, "T_Packed");
            Vector(entry, "Tint", 1f, 0.9f, 0.8f);
            Vector(entry, "Base Color Constant", 0.2f, 0.4f, 0.6f);
            Vector(entry, "Emissive Constant", 1f, 0.5f, 0.25f);
            Scalar(entry, "Metallic Constant", 0.6f);
            Scalar(entry, "Roughness Constant", 0.3f);
            Scalar(entry, "Emissive_Intencity", 2f);
            Texture(entry, "Unique_Emissive_Texture", "T_Glow");

            PortageImportReport report = Import(export);

            Material material = LoadMaterial("MI_Plain");
            Assert.IsNotNull(material.GetTexture("_BaseMap"));
            Assert.IsNotNull(material.GetTexture("_BumpMap"));
            Assert.IsTrue(material.IsKeywordEnabled("_NORMALMAP"));
            Assert.IsNotNull(material.GetTexture("_MetallicGlossMap"));
            Assert.IsTrue(material.IsKeywordEnabled("_METALLICSPECGLOSSMAP"));
            AssertColor(new Color(1f, 0.9f, 0.8f), material.GetColor("_BaseColor"));

            // Constants are not guessed and nothing emissive is.
            Assert.AreEqual(0f, material.GetFloat("_Metallic"), 0.001f);
            Assert.AreEqual(1f, material.GetFloat("_Smoothness"), 0.001f);
            Assert.IsNull(material.GetTexture("_EmissionMap"));
            AssertHdr(Color.black, material.GetColor("_EmissionColor"));
            Assert.IsFalse(material.IsKeywordEnabled("_EMISSION"));
            Assert.IsFalse(Exists(Textures + "T_Packed_Emission.png"));
            Assert.IsFalse(Exists(Textures + "T_Glow.png"));

            // The packed alpha is the smoothness, not an emission mask.
            Assert.AreEqual(255 - 200, ReadPng(Textures + "T_Packed_Mask.png")[0].a, 1);

            foreach (string code in new[]
                     {
                         "parameter.switchedOff", "baseColor.constant", "emission.textureMissing",
                     })
                Assert.IsFalse(Has(report, code), code);
        }

        [Test]
        public void FixtureBlock_ImportsWithItsSwitchesOn()
        {
            string export = Path.Combine(Path.GetDirectoryName(PortageManifestTests.FixturePath), "Block");

            PortageImportReport report = PortageImporter.Import(export, Out, null);

            Assert.AreEqual(0, report.ErrorCount);
            Material material = LoadMaterial("MI_Block");
            Assert.IsNotNull(material.GetTexture("_BaseMap"));
            Assert.IsNotNull(material.GetTexture("_BumpMap"));
            Assert.IsFalse(Has(report, "parameter.switchedOff"));
        }
    }
}
