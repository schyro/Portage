using System.IO;
using System.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace Portage.Tests
{
    public sealed class PortageColorMaskTests
    {
        const string Root = "Assets/__PortageColorMaskTests";

        string _tempExport;

        [SetUp]
        public void SetUp() => Cleanup();

        [TearDown]
        public void TearDown() => Cleanup();

        void Cleanup()
        {
            if (AssetDatabase.IsValidFolder(Root))
                AssetDatabase.DeleteAsset(Root);
            if (_tempExport != null && Directory.Exists(_tempExport))
                Directory.Delete(_tempExport, true);
            _tempExport = null;
        }

        static Color32[] Grey(int count, byte value = 128)
        {
            var pixels = new Color32[count];
            for (int i = 0; i < count; i++)
                pixels[i] = new Color32(value, value, value, 255);
            return pixels;
        }

        static Color32[] MaskOf(params byte[] red)
        {
            return red.Select(r => new Color32(r, r, r, 255)).ToArray();
        }

        static byte Expected(byte encoded, float factor)
        {
            float linear = PortageTextureProcessor.SrgbToLinear(encoded / 255f) * factor;
            return (byte)Mathf.RoundToInt(PortageTextureProcessor.LinearToSrgb(linear) * 255f);
        }

        [Test]
        public void TintBaseColor_TintsOnlyTheWhiteAreas()
        {
            Color32[] basePixels = Grey(4);
            basePixels[3].a = 77;

            Color32[] result = PortageTextureProcessor.TintBaseColor(basePixels, 2, 2, MaskOf(0, 255, 255, 0), 2, 2,
                false, new Color(1f, 0.25f, 0f, 0.5f));

            Assert.AreEqual(new Color32(128, 128, 128, 255), result[0]);
            Assert.AreEqual(new Color32(128, 128, 128, 77), result[3]);
            foreach (int i in new[] { 1, 2 })
            {
                Assert.AreEqual(128, result[i].r);
                Assert.AreEqual(Expected(128, 0.25f), result[i].g);
                Assert.AreEqual(0, result[i].b);
                Assert.AreEqual(255, result[i].a);
            }
        }

        [Test]
        public void TintBaseColor_ASmallerMaskIsSampledByUv()
        {
            Color32[] result = PortageTextureProcessor.TintBaseColor(Grey(4), 2, 2, MaskOf(255), 1, 1, false,
                new Color(1f, 0.25f, 0f));

            Assert.AreEqual(4, result.Length);
            foreach (Color32 pixel in result)
                Assert.AreEqual(Expected(128, 0.25f), pixel.g);
        }

        [Test]
        public void TintBaseColor_SrgbMaskIsLinearisedBeforeBlending()
        {
            Assert.AreEqual(0.2158f, PortageTextureProcessor.SrgbToLinear(128 / 255f), 0.0005f);

            Color32[] result = PortageTextureProcessor.TintBaseColor(Grey(1), 1, 1, MaskOf(128), 1, 1, true,
                new Color(1f, 0f, 1f));

            float m = PortageTextureProcessor.SrgbToLinear(128 / 255f);
            Assert.AreEqual(Expected(128, 1f - m), result[0].g);
            Assert.AreEqual(128, result[0].r);
        }

        [Test]
        public void IsUniformMask_AllowsTwoLevelsOfNoise()
        {
            Assert.IsTrue(PortageTextureProcessor.IsUniformMask(MaskOf(100, 102, 101), out byte value));
            Assert.AreEqual(101, value);
            Assert.IsTrue(PortageTextureProcessor.IsUniformMask(MaskOf(255), out value));
            Assert.AreEqual(255, value);
            Assert.IsFalse(PortageTextureProcessor.IsUniformMask(MaskOf(100, 103), out _));
            Assert.IsFalse(PortageTextureProcessor.IsUniformMask(MaskOf(0, 255), out _));
        }

        // Adds a black/white mask texture and a Color_Mask parameter to the Block export's manifest.
        string CopyBlockWithMask(byte left, byte right)
        {
            string source = Path.Combine(Path.GetDirectoryName(PortageManifestTests.FixturePath), "Block");
            _tempExport = Path.Combine(Path.GetTempPath(), "portage-colormask-" + System.Guid.NewGuid().ToString("N"));
            foreach (string file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
            {
                string target = Path.Combine(_tempExport, Path.GetRelativePath(source, file));
                Directory.CreateDirectory(Path.GetDirectoryName(target));
                File.Copy(file, target);
            }

            var mask = new Texture2D(2, 1, TextureFormat.RGBA32, false, true);
            try
            {
                mask.SetPixels32(MaskOf(left, right));
                mask.Apply(false);
                File.WriteAllBytes(Path.Combine(_tempExport, "Textures", "T_Block_Mask.png"), mask.EncodeToPNG());
            }
            finally
            {
                Object.DestroyImmediate(mask);
            }

            string manifestPath = Path.Combine(_tempExport, "manifest.json");
            string json = File.ReadAllText(manifestPath);
            const string textureAnchor = "\"textures\": [";
            const string parameterAnchor = "\"textureParameters\": [";
            Assert.IsTrue(json.Contains(textureAnchor) && json.Contains(parameterAnchor));
            json = json.Replace(textureAnchor, textureAnchor +
                "{\"id\":\"/Game/PortageTest/T_Block_Mask.T_Block_Mask\",\"name\":\"T_Block_Mask\"," +
                "\"file\":\"Textures/T_Block_Mask.png\",\"width\":2,\"height\":1,\"srgb\":false," +
                "\"compression\":\"Masks\",\"flipGreenChannel\":false},");
            // Only the first occurrence: it belongs to MI_Block; the second material has an empty array.
            int at = json.IndexOf(parameterAnchor, System.StringComparison.Ordinal);
            json = json.Insert(at + parameterAnchor.Length,
                "{\"name\":\"Color_Mask\",\"texture\":\"/Game/PortageTest/T_Block_Mask.T_Block_Mask\"},");
            File.WriteAllText(manifestPath, json);
            return _tempExport;
        }

        [Test]
        public void ColorMask_BakesATintedCopyAndSkipsTheOriginalBaseTexture()
        {
            string export = CopyBlockWithMask(0, 255);

            PortageImportReport report = PortageImporter.Import(export, Root + "/Block", null);

            Assert.AreEqual(0, report.ErrorCount);
            string bakedPath = Root + "/Block/Textures/MI_Block_BaseColor.png";
            var baked = AssetDatabase.LoadAssetAtPath<Texture2D>(bakedPath);
            Assert.IsNotNull(baked);
            var material = AssetDatabase.LoadAssetAtPath<Material>(Root + "/Block/Materials/MI_Block.mat");
            Assert.AreEqual(baked, material.GetTexture("_BaseMap"));
            Assert.AreEqual(Color.white, material.GetColor("_BaseColor"));
            Assert.IsNull(AssetDatabase.LoadAssetAtPath<Texture2D>(Root + "/Block/Textures/T_Block_D.png"));
            Assert.IsTrue(report.Entries.Any(e => e.Code == "colorMask.baked" && e.Message.Contains("MI_Block_BaseColor.png")));
            Assert.IsTrue(report.Entries.Any(e => e.Code == "parameter.guessed" && e.Message.Contains("ColorMask")));
        }

        [Test]
        public void ColorMask_UniformMaskOnlyScalesTheTint()
        {
            string export = CopyBlockWithMask(255, 255);

            PortageImportReport report = PortageImporter.Import(export, Root + "/Block", null);

            Assert.AreEqual(0, report.ErrorCount);
            var material = AssetDatabase.LoadAssetAtPath<Material>(Root + "/Block/Materials/MI_Block.mat");
            Assert.IsNotNull(material.GetTexture("_BaseMap"));
            Assert.IsNotNull(AssetDatabase.LoadAssetAtPath<Texture2D>(Root + "/Block/Textures/T_Block_D.png"));
            Assert.IsNull(AssetDatabase.LoadAssetAtPath<Texture2D>(Root + "/Block/Textures/MI_Block_BaseColor.png"));
            Assert.IsFalse(report.Entries.Any(e => e.Code == "colorMask.baked"));
            Assert.AreEqual(0.9f, material.GetColor("_BaseColor").linear.g, 0.01f);
        }

        [Test]
        public void ColorMask_WithoutATintIsReportedAsUnused()
        {
            string export = CopyBlockWithMask(0, 255);
            PortageManifest manifest = PortageManifest.Load(Path.Combine(export, "manifest.json"));
            PortageManifest.MaterialEntry entry = manifest.materials.First(m => m.name == "MI_Block");
            var file = new PortageMappingFile();
            file.SetVector(PortageMappingFile.KeyOf(entry), "Tint", "");
            file.Save(export);

            PortageImportReport report = PortageImporter.Import(export, Root + "/Block", null);

            Assert.AreEqual(0, report.ErrorCount);
            Assert.AreEqual(1, report.Entries.Count(e => e.Code == "colorMask.unused"));
            Assert.IsFalse(report.Entries.Any(e => e.Code == "colorMask.baked"));
            Assert.IsNotNull(AssetDatabase.LoadAssetAtPath<Texture2D>(Root + "/Block/Textures/T_Block_D.png"));
        }
    }
}
