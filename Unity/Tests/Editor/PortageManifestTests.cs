using System.IO;
using NUnit.Framework;

namespace Portage.Tests
{
    public sealed class PortageManifestTests
    {
        // A local (file:) package has no folder under Packages/ on disk, so resolve its real path.
        public static string FixturePath => Path.GetFullPath(Path.Combine(
            UnityEditor.PackageManager.PackageInfo.FindForAssembly(typeof(PortageManifest).Assembly).resolvedPath,
            "Tests", "Fixtures~", "Cube"));

        [Test]
        public void Fixture_Parses()
        {
            PortageManifest manifest = PortageManifest.Load(Path.Combine(FixturePath, "manifest.json"));

            Assert.AreEqual(1, manifest.formatVersion);
            Assert.AreEqual(1, manifest.meshes.Count);
            Assert.AreEqual(2, manifest.materials.Count);
            Assert.AreEqual(3, manifest.textures.Count);
            Assert.IsTrue(manifest.FindTexture("/Game/Cube/T_Cube_N.T_Cube_N").IsNormalMap);
        }

        [Test]
        public void HigherFormatVersion_IsRejected()
        {
            Assert.Throws<PortageManifestException>(() => PortageManifest.Parse("{\"formatVersion\": 2}"));
        }

        [Test]
        public void MissingVersion_IsRejected()
        {
            Assert.Throws<PortageManifestException>(() => PortageManifest.Parse("{\"meshes\": []}"));
        }

        [Test]
        public void InvalidJson_IsRejected()
        {
            Assert.Throws<PortageManifestException>(() => PortageManifest.Parse("{ not json"));
        }

        [Test]
        public void StaticSwitchParameters_AreParsed()
        {
            PortageManifest manifest = PortageManifest.Parse(
                "{\"formatVersion\":1,\"materials\":[{\"id\":\"/Game/Test/MI_A.MI_A\",\"name\":\"MI_A\"," +
                "\"staticSwitchParameters\":[{\"name\":\"Use Normal\",\"value\":true}," +
                "{\"name\":\"Use Emissive\",\"value\":false}]}]}");

            var switches = manifest.materials[0].staticSwitchParameters;
            Assert.AreEqual(2, switches.Count);
            Assert.AreEqual("Use Normal", switches[0].name);
            Assert.IsTrue(switches[0].value);
            Assert.AreEqual("Use Emissive", switches[1].name);
            Assert.IsFalse(switches[1].value);
        }

        [Test]
        public void MissingStaticSwitchParameters_GiveAnEmptyList()
        {
            PortageManifest manifest = PortageManifest.Parse(
                "{\"formatVersion\":1,\"materials\":[{\"id\":\"/Game/Test/MI_A.MI_A\",\"name\":\"MI_A\"}]}");

            Assert.IsNotNull(manifest.materials[0].staticSwitchParameters);
            Assert.AreEqual(0, manifest.materials[0].staticSwitchParameters.Count);
        }

        [Test]
        public void BlockFixture_HasStaticSwitches()
        {
            string path = Path.Combine(Path.GetDirectoryName(FixturePath), "Block", "manifest.json");
            PortageManifest manifest = PortageManifest.Load(path);

            PortageManifest.MaterialEntry block = manifest.materials.Find(m => m.name == "MI_Block");
            Assert.IsNotNull(block);
            Assert.AreEqual(2, block.staticSwitchParameters.Count);
            Assert.AreEqual("Use Tint", block.staticSwitchParameters[0].name);
            Assert.IsTrue(block.staticSwitchParameters[0].value);
            Assert.AreEqual("Use Normal", block.staticSwitchParameters[1].name);
            Assert.IsTrue(block.staticSwitchParameters[1].value);
        }
    }
}
