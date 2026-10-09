using System.IO;
using NUnit.Framework;
using UnityEngine;

namespace Portage.Tests
{
    public sealed class PortageTexturePackingTests
    {
        const int Size = 64;

        static Color32[] _orm;

        [OneTimeSetUp]
        public void LoadFixture()
        {
            var texture = new Texture2D(2, 2, TextureFormat.RGBA32, false, true);
            try
            {
                string path = Path.Combine(PortageManifestTests.FixturePath, "Textures", "T_Cube_ORM.png");
                Assert.IsTrue(texture.LoadImage(File.ReadAllBytes(path)));
                Assert.AreEqual(Size, texture.width);
                Assert.AreEqual(Size, texture.height);
                _orm = texture.GetPixels32();
            }
            finally
            {
                Object.DestroyImmediate(texture);
            }
        }

        static Color32[] Pack(out int width, out int height)
        {
            PortageTextureProcessor.PixelSource Source(PortageTextureProcessor.SourceChannel channel, bool invert) =>
                new PortageTextureProcessor.PixelSource
                {
                    Pixels = _orm, Width = Size, Height = Size, Channel = channel, Invert = invert,
                };

            return PortageTextureProcessor.PackMask(
                Source(PortageTextureProcessor.SourceChannel.B, false), // metallic
                Source(PortageTextureProcessor.SourceChannel.G, true), // roughness -> smoothness
                Source(PortageTextureProcessor.SourceChannel.R, false), // occlusion
                0, out width, out height);
        }

        // The generator writes PNG row 0 at the top; Unity pixel arrays start at the bottom.
        static Color32 At(Color32[] pixels, int x, int pngY) => pixels[(Size - 1 - pngY) * Size + x];

        [Test]
        public void LeftTopInterior_PacksExpectedChannels()
        {
            Color32 p = At(Pack(out _, out _), 10, 10);

            Assert.AreEqual(0, p.r);
            Assert.AreEqual(255, p.g);
            Assert.AreEqual(0, p.b);
            Assert.AreEqual(204, p.a);
        }

        [Test]
        public void RightBottomInterior_PacksExpectedChannels()
        {
            Color32 p = At(Pack(out _, out _), 50, 50);

            Assert.AreEqual(255, p.r);
            Assert.AreEqual(255, p.g);
            Assert.AreEqual(51, p.a);
        }

        [Test]
        public void BorderPixel_HasReducedOcclusion()
        {
            Color32 p = At(Pack(out int width, out int height), 1, 30);

            Assert.AreEqual(Size, width);
            Assert.AreEqual(Size, height);
            Assert.AreEqual(128, p.g);
        }

        [Test]
        public void MissingSources_UseFallbacks()
        {
            var smoothness = new PortageTextureProcessor.PixelSource
            {
                Pixels = new[] { new Color32(0, 100, 0, 0) },
                Width = 1,
                Height = 1,
                Channel = PortageTextureProcessor.SourceChannel.G,
            };

            Color32[] result = PortageTextureProcessor.PackMask(null, smoothness, null, 77, out _, out _);

            Assert.AreEqual(77, result[0].r);
            Assert.AreEqual(255, result[0].g);
            Assert.AreEqual(100, result[0].a);
        }
    }
}
