using System.IO;
using System.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace Portage.Tests
{
    public sealed class PortageMappingImportTests
    {
        const string Root = "Assets/__PortageMappingTests";

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

        // Copies the Block export so a sidecar can be written without touching the repository.
        string CopyBlockExport()
        {
            string source = Path.Combine(Path.GetDirectoryName(PortageManifestTests.FixturePath), "Block");
            _tempExport = Path.Combine(Path.GetTempPath(), "portage-block-" + System.Guid.NewGuid().ToString("N"));
            foreach (string file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
            {
                string target = Path.Combine(_tempExport, Path.GetRelativePath(source, file));
                Directory.CreateDirectory(Path.GetDirectoryName(target));
                File.Copy(file, target);
            }

            return _tempExport;
        }

        static PortageManifest.MaterialEntry BlockMaterial(string export, out PortageManifest manifest)
        {
            manifest = PortageManifest.Load(Path.Combine(export, "manifest.json"));
            return manifest.materials.First(m => m.name == "MI_Block");
        }

        [Test]
        public void WithoutSidecar_TableRulesStillApply()
        {
            string export = CopyBlockExport();

            PortageImportReport report = PortageImporter.Import(export, Root + "/Block", null);

            Assert.AreEqual(0, report.ErrorCount);
            var material = AssetDatabase.LoadAssetAtPath<Material>(Root + "/Block/Materials/MI_Block.mat");
            Assert.IsNotNull(material.GetTexture("_BaseMap"));
            Assert.IsFalse(report.Entries.Any(e => e.Code == "parameter.guessed"));
        }

        [Test]
        public void SidecarIgnore_RemovesTheBaseMap()
        {
            string export = CopyBlockExport();
            PortageManifest.MaterialEntry entry = BlockMaterial(export, out _);
            var file = new PortageMappingFile();
            file.SetTexture(PortageMappingFile.KeyOf(entry), "BaseColor", TextureUsage.Ignore);
            file.Save(export);

            PortageImportReport report = PortageImporter.Import(export, Root + "/Block", null);

            Assert.AreEqual(0, report.ErrorCount);
            var material = AssetDatabase.LoadAssetAtPath<Material>(Root + "/Block/Materials/MI_Block.mat");
            Assert.IsNotNull(material);
            Assert.IsNull(material.GetTexture("_BaseMap"));
            Assert.IsNotNull(material.GetTexture("_BumpMap"));
            Assert.IsFalse(report.Entries.Any(e => e.Code == "parameter.unmapped" && e.Message.Contains("'BaseColor'")));
        }

        [Test]
        public void InvalidSidecar_IsReportedAndIgnored()
        {
            string export = CopyBlockExport();
            File.WriteAllText(Path.Combine(export, PortageMappingFile.FileName), "{ broken");

            PortageImportReport report = PortageImporter.Import(export, Root + "/Block", null);

            Assert.AreEqual(0, report.ErrorCount);
            Assert.IsTrue(report.Entries.Any(e => e.Code == "mapping.invalid" && e.Severity == PortageSeverity.Warning));
            var material = AssetDatabase.LoadAssetAtPath<Material>(Root + "/Block/Materials/MI_Block.mat");
            Assert.IsNotNull(material.GetTexture("_BaseMap"));
        }
    }
}
