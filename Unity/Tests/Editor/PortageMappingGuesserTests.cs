using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
using UnityEngine;

namespace Portage.Tests
{
    public sealed class PortageMappingGuesserTests
    {
        const string BaseMaterial = "/Game/Test/M_Master.M_Master";

        static PortageManifest.TextureEntry Tex(string name, bool srgb, string compression)
        {
            return new PortageManifest.TextureEntry
            {
                id = "/Game/Test/" + name + "." + name, name = name, file = "Textures/" + name + ".png",
                srgb = srgb, compression = compression,
            };
        }

        // Adds the texture to the manifest and a parameter that uses it to the material.
        static void AddTexture(PortageManifest manifest, PortageManifest.MaterialEntry entry, string parameter,
            PortageManifest.TextureEntry texture)
        {
            manifest.textures.Add(texture);
            entry.textureParameters.Add(new PortageManifest.TextureParameter { name = parameter, texture = texture.id });
        }

        static PortageManifest NewManifest(out PortageManifest.MaterialEntry entry)
        {
            var manifest = new PortageManifest { formatVersion = 1 };
            entry = new PortageManifest.MaterialEntry
            {
                id = "/Game/Test/MI_Prop.MI_Prop", name = "MI_Prop", baseMaterial = BaseMaterial, convertible = true,
            };
            manifest.materials.Add(entry);
            return manifest;
        }

        static PortageManifest DecoratedNames(out PortageManifest.MaterialEntry entry)
        {
            PortageManifest manifest = NewManifest(out entry);
            AddTexture(manifest, entry, "Base Color Texture", Tex("T_Base", true, "Default"));
            AddTexture(manifest, entry, "Normal Texture", Tex("T_Normal", false, "Normalmap"));
            AddTexture(manifest, entry, "Detail Normal Texture", Tex("DefaultNormal", false, "Normalmap"));
            AddTexture(manifest, entry, "ORMA(AO,Rough,Mat,Emis)", Tex("T_X_ORME", false, "Default"));
            AddTexture(manifest, entry, "Color_Mask", Tex("T_Gray", false, "Default"));
            AddTexture(manifest, entry, "Unique_Emissive_Texture", Tex("T_Gray", false, "Default"));
            AddTexture(manifest, entry, "Roughness_Grunge_01", Tex("Grunge_One", false, "Grayscale"));
            AddTexture(manifest, entry, "Roughness_Grunge_02", Tex("Grunge_Two", false, "Grayscale"));

            foreach (string name in new[]
                     {
                         "Roughness Multiplier", "Metallic Constant", "Roughness Constant", "Normal Strength",
                         "Dust_Roughness", "Emissive_Intencity",
                     })
                entry.scalarParameters.Add(new PortageManifest.ScalarParameter { name = name, value = 0.5f });

            foreach (string name in new[] { "Base Color Constant", "Emissive Constant", "Dust Color", "Base Color Multiplier" })
                entry.vectorParameters.Add(new PortageManifest.VectorParameter { name = name, value = new[] { 1f, 1f, 1f, 1f } });

            return manifest;
        }

        static PortageMappingGuesser.TextureGuess[] GuessedTextures(PortageMappingGuesser.Result result)
        {
            return result.Textures.ToArray();
        }

        [Test]
        public void DecoratedNames_GuessesOnlyTheSafeParameters()
        {
            PortageManifest manifest = DecoratedNames(out PortageManifest.MaterialEntry entry);

            PortageMappingGuesser.Result result = PortageMappingGuesser.Guess(entry, manifest);

            Assert.AreEqual(4, GuessedTextures(result).Length);
            Assert.AreEqual(TextureUsage.BaseColor, result.FindTexture("Base Color Texture").Usage);
            Assert.AreEqual(TextureUsage.Normal, result.FindTexture("Normal Texture").Usage);

            PortageMappingGuesser.TextureGuess mask = result.FindTexture("ORMA(AO,Rough,Mat,Emis)");
            Assert.AreEqual(TextureUsage.Mask, mask.Usage);
            Assert.AreEqual(MaskChannel.Occlusion, mask.Red);
            Assert.AreEqual(MaskChannel.Roughness, mask.Green);
            Assert.AreEqual(MaskChannel.Metallic, mask.Blue);
            Assert.AreEqual(MaskChannel.None, mask.Alpha);

            // A color mask is guessed even though its texture is a placeholder.
            Assert.AreEqual(TextureUsage.ColorMask, result.FindTexture("Color_Mask").Usage);

            Assert.AreEqual(1, result.Scalars.Count);
            Assert.AreEqual("_BumpScale", result.FindScalar("Normal Strength").Property);

            Assert.AreEqual(1, result.Vectors.Count);
            Assert.AreEqual("_BaseColor", result.FindVector("Base Color Multiplier").Property);
        }

        [Test]
        public void ColorMask_IsGuessedForRealTexturesAndOnlyOncePerMaterial()
        {
            PortageManifest manifest = NewManifest(out PortageManifest.MaterialEntry entry);
            AddTexture(manifest, entry, "Tint Mask Texture", Tex("T_Mask", false, "Masks"));
            AddTexture(manifest, entry, "Paint_Mask", Tex("T_Mask2", false, "Masks"));

            PortageMappingGuesser.Result result = PortageMappingGuesser.Guess(entry, manifest);

            Assert.AreEqual(TextureUsage.ColorMask, result.FindTexture("Tint Mask Texture").Usage);
            Assert.IsNull(result.FindTexture("Paint_Mask"));
        }

        [Test]
        public void ColorMask_SavedChoiceRoundTripsAndBeatsTheGuess()
        {
            PortageManifest manifest = NewManifest(out PortageManifest.MaterialEntry entry);
            AddTexture(manifest, entry, "Color_Mask", Tex("T_Gray", false, "Default"));
            var file = new PortageMappingFile();
            file.SetTexture(BaseMaterial, "Color_Mask", TextureUsage.ColorMask);

            Assert.AreEqual("ColorMask", file.FindTexture(BaseMaterial, "Color_Mask").usage);
            TextureDecision saved = PortageMappingResolver.Resolve(entry, manifest, null, file).FindTexture("Color_Mask");
            Assert.AreEqual(MappingSource.Saved, saved.Source);
            Assert.AreEqual(TextureUsage.ColorMask, saved.Usage);

            TextureDecision guessed = PortageMappingResolver.Resolve(entry, manifest, null, null).FindTexture("Color_Mask");
            Assert.AreEqual(MappingSource.Guess, guessed.Source);
            Assert.AreEqual(TextureUsage.ColorMask, guessed.Usage);
        }

        [Test]
        public void NormalizeName_StripsOneTrailingWord()
        {
            Assert.AreEqual("basecolor", PortageMappingGuesser.NormalizeName("Base Color Texture"));
            Assert.AreEqual("normal", PortageMappingGuesser.NormalizeName("NormalMap"));
            Assert.AreEqual("normal", PortageMappingGuesser.NormalizeName("Normal_Tex"));
            Assert.AreEqual("map", PortageMappingGuesser.NormalizeName("Map"));
            Assert.AreEqual("roughnessconstant", PortageMappingGuesser.NormalizeName("Roughness Constant"));
        }

        static PortageMappingGuesser.TextureGuess GuessPacked(string textureName)
        {
            PortageManifest manifest = NewManifest(out PortageManifest.MaterialEntry entry);
            AddTexture(manifest, entry, "Packed Data", Tex(textureName, false, "Default"));
            return PortageMappingGuesser.Guess(entry, manifest).FindTexture("Packed Data");
        }

        static void AssertLayout(string textureName, MaskChannel r, MaskChannel g, MaskChannel b, MaskChannel a)
        {
            PortageMappingGuesser.TextureGuess guess = GuessPacked(textureName);
            Assert.IsNotNull(guess, textureName);
            Assert.AreEqual(TextureUsage.Mask, guess.Usage, textureName);
            Assert.AreEqual((r, g, b, a), (guess.Red, guess.Green, guess.Blue, guess.Alpha), textureName);
        }

        [Test]
        public void PackedLetterTokens_AreParsedFromTheTextureName()
        {
            const MaskChannel O = MaskChannel.Occlusion, R = MaskChannel.Roughness, M = MaskChannel.Metallic,
                N = MaskChannel.None;
            AssertLayout("T_Rock_ARM", O, R, M, N);
            AssertLayout("T_Rock_RMA", R, M, O, N);
            AssertLayout("T_Rock_MRAO", M, R, O, N);
            AssertLayout("T_Rock_ORM", O, R, M, N);
            AssertLayout("T_Rock_ORMA", O, R, M, N);
            AssertLayout("T_Rock_ORME", O, R, M, N);
            AssertLayout("T_Rock_ORM_01", O, R, M, N);
        }

        [Test]
        public void OrdinaryWords_AreNotReadAsLayouts()
        {
            foreach (string name in new[]
                     {
                         "T_Normal", "T_Master", "T_Mesh", "T_Rose", "T_Norm", "T_Arm_01", "T_MESH", "T_Rock",
                         "Normal", "Master",
                     })
                Assert.IsNull(GuessPacked(name), name);
        }

        [Test]
        public void PlaceholderTextures_AreNeverGuessed()
        {
            PortageManifest manifest = NewManifest(out PortageManifest.MaterialEntry entry);
            AddTexture(manifest, entry, "Base Color Texture", Tex("T_Gray", true, "Default"));
            AddTexture(manifest, entry, "Normal Texture", Tex("DefaultNormal", false, "Normalmap"));
            PortageManifest.TextureEntry engine = Tex("Foo", true, "Default");
            engine.id = "/Engine/EngineResources/Foo.Foo";
            AddTexture(manifest, entry, "Albedo", engine);

            Assert.AreEqual(0, PortageMappingGuesser.Guess(entry, manifest).Textures.Count);
        }

        [Test]
        public void Normal_FallsBackToTheOnlyNormalMapTexture()
        {
            PortageManifest manifest = NewManifest(out PortageManifest.MaterialEntry entry);
            AddTexture(manifest, entry, "Surface Relief", Tex("T_A", false, "Normalmap"));
            AddTexture(manifest, entry, "Detail Relief", Tex("T_B", false, "Normalmap"));

            PortageMappingGuesser.Result result = PortageMappingGuesser.Guess(entry, manifest);

            Assert.AreEqual(TextureUsage.Normal, result.FindTexture("Surface Relief").Usage);
            Assert.IsNull(result.FindTexture("Detail Relief"));
        }

        [Test]
        public void Normal_FallbackIsSkippedWhenAmbiguous()
        {
            PortageManifest manifest = NewManifest(out PortageManifest.MaterialEntry entry);
            AddTexture(manifest, entry, "Surface Relief", Tex("T_A", false, "Normalmap"));
            AddTexture(manifest, entry, "Other Relief", Tex("T_B", false, "Normalmap"));

            Assert.AreEqual(0, PortageMappingGuesser.Guess(entry, manifest).Textures.Count);
        }

        [Test]
        public void BaseColor_PrefersTheSrgbTexture()
        {
            PortageManifest manifest = NewManifest(out PortageManifest.MaterialEntry entry);
            AddTexture(manifest, entry, "Albedo", Tex("T_A", false, "Default"));
            AddTexture(manifest, entry, "Base Color", Tex("T_B", true, "Default"));

            PortageMappingGuesser.Result result = PortageMappingGuesser.Guess(entry, manifest);

            Assert.IsNull(result.FindTexture("Albedo"));
            Assert.AreEqual(TextureUsage.BaseColor, result.FindTexture("Base Color").Usage);
        }

        [Test]
        public void SingleChannelMasks_FillOnlyWhatThePackedMaskLacks()
        {
            PortageManifest manifest = NewManifest(out PortageManifest.MaterialEntry entry);
            AddTexture(manifest, entry, "Packed", Tex("T_Rock_RM", false, "Default")); // no layout: RM is too short
            AddTexture(manifest, entry, "Roughness", Tex("T_R", false, "Grayscale"));
            AddTexture(manifest, entry, "AO", Tex("T_AO", false, "Grayscale"));

            PortageMappingGuesser.Result result = PortageMappingGuesser.Guess(entry, manifest);

            Assert.IsNull(result.FindTexture("Packed"));
            Assert.AreEqual(MaskChannel.Roughness, result.FindTexture("Roughness").Red);
            Assert.AreEqual(MaskChannel.Occlusion, result.FindTexture("AO").Red);
        }

        [Test]
        public void SavedIgnore_BeatsTheGuess()
        {
            PortageManifest manifest = DecoratedNames(out PortageManifest.MaterialEntry entry);
            var file = new PortageMappingFile();
            file.SetTexture(BaseMaterial, "Base Color Texture", TextureUsage.Ignore);

            PortageMappingResolution resolution = PortageMappingResolver.Resolve(entry, manifest, null, file);

            TextureDecision decision = resolution.FindTexture("Base Color Texture");
            Assert.AreEqual(MappingSource.Saved, decision.Source);
            Assert.AreEqual(TextureUsage.Ignore, decision.Usage);
            foreach (TextureDecision other in resolution.Textures)
                Assert.IsFalse(other.Source == MappingSource.Guess && other.Usage == TextureUsage.BaseColor);
        }

        [Test]
        public void TableRule_BeatsTheGuess_AndBlocksASecondNormalMap()
        {
            PortageManifest manifest = DecoratedNames(out PortageManifest.MaterialEntry entry);
            var table = ScriptableObject.CreateInstance<PortageMappingTable>();
            try
            {
                table.textureRules = new List<PortageMappingTable.TextureRule>
                {
                    new PortageMappingTable.TextureRule
                    {
                        parameterNames = new List<string> { "Detail Normal Texture" }, usage = TextureUsage.Normal,
                    },
                    new PortageMappingTable.TextureRule
                    {
                        parameterNames = new List<string> { "Base Color Texture" }, usage = TextureUsage.Emission,
                    },
                };
                table.scalarRules = new List<PortageMappingTable.ScalarRule>
                {
                    new PortageMappingTable.ScalarRule
                    {
                        parameterNames = new List<string> { "Roughness Multiplier" }, property = "_BumpScale",
                    },
                };

                PortageMappingResolution resolution = PortageMappingResolver.Resolve(entry, manifest, table, null);

                Assert.AreEqual(MappingSource.Table, resolution.FindTexture("Base Color Texture").Source);
                Assert.AreEqual(TextureUsage.Emission, resolution.FindTexture("Base Color Texture").Usage);
                Assert.AreEqual(MappingSource.Table, resolution.FindTexture("Detail Normal Texture").Source);
                Assert.IsFalse(resolution.FindTexture("Normal Texture").IsMapped, "second normal map");
                Assert.AreEqual(MappingSource.Guess, resolution.FindTexture("ORMA(AO,Rough,Mat,Emis)").Source);

                // _BumpScale is already taken by the table rule.
                Assert.IsFalse(resolution.FindScalar("Normal Strength").IsMapped);
                Assert.AreEqual(MappingSource.Table, resolution.FindScalar("Roughness Multiplier").Source);
            }
            finally
            {
                Object.DestroyImmediate(table);
            }
        }

        [Test]
        public void Guess_DoesNotReplaceMaskChannelsTheTableProvides()
        {
            PortageManifest manifest = DecoratedNames(out PortageManifest.MaterialEntry entry);
            var table = ScriptableObject.CreateInstance<PortageMappingTable>();
            try
            {
                table.textureRules = new List<PortageMappingTable.TextureRule>
                {
                    new PortageMappingTable.TextureRule
                    {
                        parameterNames = new List<string> { "Roughness_Grunge_01" }, usage = TextureUsage.Mask,
                        red = MaskChannel.Roughness,
                    },
                };

                TextureDecision mask = PortageMappingResolver.Resolve(entry, manifest, table, null)
                    .FindTexture("ORMA(AO,Rough,Mat,Emis)");

                Assert.AreEqual(MappingSource.Guess, mask.Source);
                Assert.AreEqual(MaskChannel.Occlusion, mask.Red);
                Assert.AreEqual(MaskChannel.None, mask.Green, "roughness is provided by the table rule");
                Assert.AreEqual(MaskChannel.Metallic, mask.Blue);
            }
            finally
            {
                Object.DestroyImmediate(table);
            }
        }

        [Test]
        public void Sidecar_RoundTrips()
        {
            string folder = Path.Combine(Path.GetTempPath(), "portage-map-" + System.Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(folder);
            try
            {
                var file = new PortageMappingFile();
                file.SetTexture(BaseMaterial, "Packed", TextureUsage.Mask, MaskChannel.Occlusion, MaskChannel.Roughness,
                    MaskChannel.Metallic, MaskChannel.None);
                file.SetTexture(BaseMaterial, "Dust", TextureUsage.Ignore);
                file.SetScalar(BaseMaterial, "Rough", "_Smoothness", ScalarConversion.OneMinus);
                file.SetVector(BaseMaterial, "Tint", "");
                file.Save(folder);

                string text = File.ReadAllText(Path.Combine(folder, PortageMappingFile.FileName));
                Assert.IsFalse(text.Contains("\r"));
                Assert.IsTrue(text.EndsWith("\n"));
                Assert.AreNotEqual(0xEF, File.ReadAllBytes(Path.Combine(folder, PortageMappingFile.FileName))[0], "BOM");

                PortageMappingFile loaded = PortageMappingFile.Load(folder);

                Assert.AreEqual("", loaded.LoadError);
                PortageMappingFile.TextureChoice packed = loaded.FindTexture(BaseMaterial, "Packed");
                Assert.AreEqual("Mask", packed.usage);
                Assert.AreEqual("Occlusion", packed.red);
                Assert.AreEqual("Roughness", packed.green);
                Assert.AreEqual("Metallic", packed.blue);
                Assert.AreEqual("None", packed.alpha);
                Assert.AreEqual("Ignore", loaded.FindTexture(BaseMaterial, "Dust").usage);
                Assert.AreEqual("_Smoothness", loaded.FindScalar(BaseMaterial, "Rough").property);
                Assert.AreEqual("OneMinus", loaded.FindScalar(BaseMaterial, "Rough").conversion);
                Assert.AreEqual("", loaded.FindVector(BaseMaterial, "Tint").property);

                loaded.ClearMaterial(BaseMaterial);
                Assert.IsNull(loaded.FindTexture(BaseMaterial, "Packed"));
            }
            finally
            {
                Directory.Delete(folder, true);
            }
        }

        [Test]
        public void Sidecar_MissingFile_LoadsEmpty()
        {
            string folder = Path.Combine(Path.GetTempPath(), "portage-map-" + System.Guid.NewGuid().ToString("N"));
            PortageMappingFile loaded = PortageMappingFile.Load(folder);

            Assert.AreEqual("", loaded.LoadError);
            Assert.AreEqual(0, loaded.materials.Count);
        }

        [Test]
        public void Sidecar_InvalidOrNewer_LoadsEmptyWithError()
        {
            string folder = Path.Combine(Path.GetTempPath(), "portage-map-" + System.Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(folder);
            try
            {
                string path = Path.Combine(folder, PortageMappingFile.FileName);

                File.WriteAllText(path, "{ not json");
                PortageMappingFile broken = PortageMappingFile.Load(folder);
                Assert.IsNotEmpty(broken.LoadError);
                Assert.AreEqual(0, broken.materials.Count);

                File.WriteAllText(path, "{\"version\": 2, \"materials\": [{\"baseMaterial\": \"x\"}]}");
                PortageMappingFile newer = PortageMappingFile.Load(folder);
                Assert.IsNotEmpty(newer.LoadError);
                Assert.AreEqual(0, newer.materials.Count);
            }
            finally
            {
                Directory.Delete(folder, true);
            }
        }

        [Test]
        public void Sidecar_UnknownUsage_CountsAsNoChoice()
        {
            PortageManifest manifest = DecoratedNames(out PortageManifest.MaterialEntry entry);
            var file = new PortageMappingFile();
            file.SetTexture(BaseMaterial, "Base Color Texture", TextureUsage.Normal);
            file.FindTexture(BaseMaterial, "Base Color Texture").usage = "Bogus";

            TextureDecision decision = PortageMappingResolver.Resolve(entry, manifest, null, file)
                .FindTexture("Base Color Texture");

            Assert.AreEqual(MappingSource.Guess, decision.Source);
            Assert.AreEqual(TextureUsage.BaseColor, decision.Usage);
        }
    }
}
