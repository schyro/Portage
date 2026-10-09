using System.IO;
using System.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace Portage.Tests
{
    public sealed class PortageImportTests
    {
        const string Root = "Assets/__PortageTests";
        const string Target = Root + "/Cube";

        [SetUp]
        public void SetUp() => Cleanup();

        [TearDown]
        public void TearDown() => Cleanup();

        static void Cleanup()
        {
            if (AssetDatabase.IsValidFolder(Root))
                AssetDatabase.DeleteAsset(Root);
        }

        static PortageImportReport ImportFixture() =>
            PortageImporter.Import(PortageManifestTests.FixturePath, Target, null);

        // Tests run with the project root as working directory.
        static string Absolute(string assetPath) => Path.GetFullPath(assetPath);

        [Test]
        public void ImportFixture_ProducesExpectedAssets()
        {
            PortageImportReport report = ImportFixture();

            Assert.AreEqual(0, report.ErrorCount,
                string.Join("\n", report.Entries.Where(e => e.Severity == PortageSeverity.Error)
                    .Select(e => e.Code + ": " + e.Message)));

            string materialPath = Target + "/Materials/MI_Cube.mat";
            var material = AssetDatabase.LoadAssetAtPath<Material>(materialPath);
            Assert.IsNotNull(material);
            Assert.AreEqual("Universal Render Pipeline/Lit", material.shader.name);

            Assert.IsNotNull(material.GetTexture("_BaseMap"));
            Assert.IsNotNull(material.GetTexture("_BumpMap"));
            Assert.IsNotNull(material.GetTexture("_MetallicGlossMap"));
            Assert.IsNotNull(material.GetTexture("_OcclusionMap"));
            Assert.AreEqual(1f, material.GetFloat("_Smoothness"), 1e-5f);
            Assert.IsTrue(material.IsKeywordEnabled("_NORMALMAP"));
            Assert.IsTrue(material.IsKeywordEnabled("_METALLICSPECGLOSSMAP"));
            Assert.IsTrue(material.IsKeywordEnabled("_OCCLUSIONMAP"));

            // The manifest value is linear; a non-HDR color property stores its sRGB equivalent.
            Color baseColor = material.GetColor("_BaseColor").linear;
            Assert.AreEqual(1f, baseColor.r, 1e-3f);
            Assert.AreEqual(0.9f, baseColor.g, 1e-3f);
            Assert.AreEqual(0.8f, baseColor.b, 1e-3f);
            Assert.AreEqual(1f, baseColor.a, 1e-3f);

            var baseImporter = (TextureImporter)AssetImporter.GetAtPath(Target + "/Textures/T_Cube_D.png");
            Assert.IsTrue(baseImporter.sRGBTexture);

            var maskImporter = (TextureImporter)AssetImporter.GetAtPath(Target + "/Textures/T_Cube_ORM_Mask.png");
            Assert.IsNotNull(maskImporter);
            Assert.IsFalse(maskImporter.sRGBTexture);

            var normalImporter = (TextureImporter)AssetImporter.GetAtPath(Target + "/Textures/T_Cube_N.png");
            Assert.AreEqual(TextureImporterType.NormalMap, normalImporter.textureType);
            Assert.IsTrue(normalImporter.flipGreenChannel);

            Assert.IsNull(AssetDatabase.LoadAssetAtPath<Material>(Target + "/Materials/M_Embedded.mat"));
            Assert.IsEmpty(Directory.GetFiles(Absolute(Target + "/Meshes"), "*.mat"));

            Assert.IsTrue(report.Entries.Any(e => e.Code == "material.noParameters"));
            Assert.IsTrue(report.Entries.Any(e => e.Code == "parameter.unmapped" && e.Message.Contains("WetnessAmount")));

            var model = AssetDatabase.LoadAssetAtPath<GameObject>(Target + "/Meshes/SM_Cube.fbx");
            Assert.IsNotNull(model);
            Renderer renderer = model.GetComponentInChildren<Renderer>();
            Assert.AreEqual(material, renderer.sharedMaterials[0]);

            // Holds for the Blender-made fixture only; a real Unreal export is not covered here.
            Assert.Less(Quaternion.Angle(Quaternion.identity, model.transform.localRotation), 0.1f);
            Assert.Less(Quaternion.Angle(Quaternion.identity, renderer.transform.rotation), 0.1f);
            TestContext.WriteLine(
                $"Model root localScale={model.transform.localScale}, localEulerAngles={model.transform.localEulerAngles}, " +
                $"renderer localScale={renderer.transform.localScale}, renderer localEulerAngles={renderer.transform.localEulerAngles}");

            Assert.AreEqual(Vector3.one, model.transform.localScale);
            Mesh mesh = model.GetComponentInChildren<MeshFilter>().sharedMesh;
            TestContext.WriteLine($"Mesh bounds size={mesh.bounds.size}");
            Assert.AreEqual(1f, mesh.bounds.size.x, 0.01f);
            Assert.AreEqual(1f, mesh.bounds.size.y, 0.01f);
            Assert.AreEqual(1f, mesh.bounds.size.z, 0.01f);
        }

        [Test]
        public void ImportTwice_KeepsMaterialGuid()
        {
            ImportFixture();
            string path = Target + "/Materials/MI_Cube.mat";
            string first = AssetDatabase.AssetPathToGUID(path);
            Assert.IsNotEmpty(first);

            PortageImportReport second = ImportFixture();

            Assert.AreEqual(0, second.ErrorCount);
            Assert.AreEqual(first, AssetDatabase.AssetPathToGUID(path));
            Assert.AreEqual(1, Directory.GetFiles(Absolute(Target + "/Materials"), "*.mat").Length);
            var model = AssetDatabase.LoadAssetAtPath<GameObject>(Target + "/Meshes/SM_Cube.fbx");
            Assert.AreEqual(AssetDatabase.LoadAssetAtPath<Material>(path),
                model.GetComponentInChildren<Renderer>().sharedMaterials[0]);
        }
    }
}
