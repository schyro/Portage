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
    }
}
