using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;

namespace Portage.Tests
{
    public sealed class PortageSwitchGuesserTests
    {
        const string BaseMaterial = "/Game/Test/M_Master.M_Master";

        static PortageManifest.TextureEntry Tex(string name, bool srgb = false, string compression = "Default")
        {
            return new PortageManifest.TextureEntry
            {
                id = "/Game/Test/" + name + "." + name, name = name, file = "Textures/" + name + ".png",
                srgb = srgb, compression = compression,
            };
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

        static void AddTexture(PortageManifest manifest, PortageManifest.MaterialEntry entry, string parameter,
            PortageManifest.TextureEntry texture)
        {
            manifest.textures.Add(texture);
            entry.textureParameters.Add(new PortageManifest.TextureParameter { name = parameter, texture = texture.id });
        }

        static void AddScalar(PortageManifest.MaterialEntry entry, string name, float value = 0.5f)
        {
            entry.scalarParameters.Add(new PortageManifest.ScalarParameter { name = name, value = value });
        }

        static void AddVector(PortageManifest.MaterialEntry entry, string name)
        {
            entry.vectorParameters.Add(new PortageManifest.VectorParameter { name = name, value = new[] { 1f, 1f, 1f, 1f } });
        }

        static void AddSwitch(PortageManifest.MaterialEntry entry, string name, bool value = true)
        {
            entry.staticSwitchParameters.Add(new PortageManifest.StaticSwitchParameter { name = name, value = value });
        }

        static HashSet<SwitchRole> Roles(params SwitchRole[] roles) => new HashSet<SwitchRole>(roles);

        // ---- GuessSwitchRole ----

        [TestCase("Use Normal", SwitchRole.NormalTexture)]
        [TestCase("Use Normal Map", SwitchRole.NormalTexture)]
        [TestCase("Use Detail Normal", SwitchRole.None)]
        [TestCase("Use AO", SwitchRole.Occlusion)]
        [TestCase("Use Color Mask", SwitchRole.ColorMask)]
        [TestCase("Use Emissive", SwitchRole.Emission)]
        [TestCase("Emissive Texture", SwitchRole.EmissionTexture)]
        [TestCase("Seperate_Emissive_Texture", SwitchRole.SeparateEmissionTexture)]
        [TestCase("Base Color Texture Input", SwitchRole.BaseColorTexture)]
        [TestCase("Metallic Texture Input", SwitchRole.MetallicTexture)]
        [TestCase("Roughness Texture Input", SwitchRole.RoughnessTexture)]
        [TestCase("Use Roughness Map", SwitchRole.RoughnessTexture)]
        [TestCase("Base Color Alpha Texture Input", SwitchRole.None)]
        [TestCase("Use Metallic", SwitchRole.None)]
        [TestCase("WS Dust", SwitchRole.None)]
        [TestCase("Multiply_Breakup", SwitchRole.None)]
        public void GuessSwitchRole_ReadsTheName(string name, SwitchRole expected)
        {
            Assert.AreEqual(expected, PortageMappingGuesser.GuessSwitchRole(name), name);
        }

        // ---- GuessSwitches ----

        [Test]
        public void GuessSwitches_GivesEachRoleToTheFirstSwitchInManifestOrder()
        {
            PortageManifest.MaterialEntry entry;
            NewManifest(out entry);
            AddSwitch(entry, "WS Dust");
            AddSwitch(entry, "Use Normal");
            AddSwitch(entry, "Use Normal Map");
            AddSwitch(entry, "Use AO");

            List<PortageMappingGuesser.SwitchGuess> guesses = PortageMappingGuesser.GuessSwitches(entry);

            Assert.AreEqual(2, guesses.Count);
            Assert.AreEqual("Use Normal", guesses[0].Parameter);
            Assert.AreEqual(SwitchRole.NormalTexture, guesses[0].Role);
            Assert.AreEqual("Use AO", guesses[1].Parameter);
            Assert.AreEqual(SwitchRole.Occlusion, guesses[1].Role);
            Assert.AreEqual(guesses.Count, guesses.Select(g => g.Role).Distinct().Count());
        }

        [Test]
        public void GuessSwitches_SkipsExcludedSwitchesAndTakenRoles()
        {
            PortageManifest.MaterialEntry entry;
            NewManifest(out entry);
            AddSwitch(entry, "Use Normal");
            AddSwitch(entry, "Use Normal Map");
            AddSwitch(entry, "Use AO");

            // The first normal switch has a decision already, so the second one takes the role.
            List<PortageMappingGuesser.SwitchGuess> excluded =
                PortageMappingGuesser.GuessSwitches(entry, new HashSet<string> { "Use Normal" });
            Assert.AreEqual(new[] { "Use Normal Map", "Use AO" }, excluded.Select(g => g.Parameter).ToArray());

            // A role that a decision holds is not guessed for any other switch.
            List<PortageMappingGuesser.SwitchGuess> taken =
                PortageMappingGuesser.GuessSwitches(entry, null, Roles(SwitchRole.NormalTexture));
            Assert.AreEqual(new[] { "Use AO" }, taken.Select(g => g.Parameter).ToArray());
        }

        [Test]
        public void GuessSwitches_DoesNotChangeTheCallersTakenSet()
        {
            PortageManifest.MaterialEntry entry;
            NewManifest(out entry);
            AddSwitch(entry, "Use AO");
            HashSet<SwitchRole> taken = Roles(SwitchRole.NormalTexture);

            PortageMappingGuesser.GuessSwitches(entry, null, taken);

            CollectionAssert.AreEquivalent(new[] { SwitchRole.NormalTexture }, taken);
        }

        // ---- Guess without switch roles ----

        // A material with the parameters that only make sense together with switches.
        static PortageManifest SwitchDependentNames(out PortageManifest.MaterialEntry entry)
        {
            PortageManifest manifest = NewManifest(out entry);
            AddTexture(manifest, entry, "ORMA(AO,Rough,Mat,Emis)", Tex("T_Packed"));
            AddTexture(manifest, entry, "Unique_Emissive_Texture", Tex("T_Glow"));
            AddScalar(entry, "Metallic Constant");
            AddScalar(entry, "Roughness Constant");
            AddScalar(entry, "Emissive_Intencity");
            AddScalar(entry, "Emissive Intensity");
            AddVector(entry, "Base Color Constant");
            AddVector(entry, "Base Color Multiplier");
            AddVector(entry, "Emissive Constant");
            AddVector(entry, "Emissive Color");
            return manifest;
        }

        static void AssertUnchanged(PortageMappingGuesser.Result result)
        {
            // Constants and emission are not guessed.
            Assert.IsNull(result.FindScalar("Metallic Constant"));
            Assert.IsNull(result.FindScalar("Roughness Constant"));
            Assert.IsNull(result.FindScalar("Emissive_Intencity"));
            Assert.IsNull(result.FindScalar("Emissive Intensity"));
            Assert.IsNull(result.FindVector("Base Color Constant"));
            Assert.IsNull(result.FindVector("Emissive Constant"));
            Assert.IsNull(result.FindVector("Emissive Color"));
            Assert.IsNull(result.FindTexture("Unique_Emissive_Texture"));

            // The tint is still guessed, as a tint.
            PortageMappingGuesser.VectorGuess tint = result.FindVector("Base Color Multiplier");
            Assert.IsNotNull(tint);
            Assert.AreEqual("_BaseColor", tint.Property);
            Assert.IsFalse(tint.Constant);
            Assert.AreEqual(1, result.Vectors.Count);
            Assert.AreEqual(0, result.Scalars.Count);

            // The parenthesised layout has no alpha without emission.
            PortageMappingGuesser.TextureGuess mask = result.FindTexture("ORMA(AO,Rough,Mat,Emis)");
            Assert.IsNotNull(mask);
            Assert.AreEqual(TextureUsage.Mask, mask.Usage);
            Assert.AreEqual(MaskChannel.Occlusion, mask.Red);
            Assert.AreEqual(MaskChannel.Roughness, mask.Green);
            Assert.AreEqual(MaskChannel.Metallic, mask.Blue);
            Assert.AreEqual(MaskChannel.None, mask.Alpha);
        }

        [Test]
        public void Guess_WithoutRoles_IsUnchanged()
        {
            PortageManifest manifest = SwitchDependentNames(out PortageManifest.MaterialEntry entry);

            AssertUnchanged(PortageMappingGuesser.Guess(entry, manifest));
            AssertUnchanged(PortageMappingGuesser.Guess(entry, manifest, null, null));
            AssertUnchanged(PortageMappingGuesser.Guess(entry, manifest, null, new HashSet<SwitchRole>()));
        }

        [Test]
        public void Guess_WithUnrelatedRoles_StillGuessesNoConstantsOrEmission()
        {
            PortageManifest manifest = SwitchDependentNames(out PortageManifest.MaterialEntry entry);

            // Normal and occlusion switches say nothing about constants or emission.
            AssertUnchanged(PortageMappingGuesser.Guess(entry, manifest, null,
                Roles(SwitchRole.NormalTexture, SwitchRole.Occlusion, SwitchRole.ColorMask)));
        }

        [Test]
        public void Guess_UpperCaseTokenWithE_HasNoEmissionChannelWithoutRoles()
        {
            PortageManifest manifest = NewManifest(out PortageManifest.MaterialEntry entry);
            AddTexture(manifest, entry, "ORME", Tex("T_Packed"));

            PortageMappingGuesser.TextureGuess mask = PortageMappingGuesser.Guess(entry, manifest).FindTexture("ORME");

            Assert.IsNotNull(mask);
            Assert.AreEqual(MaskChannel.None, mask.Alpha);
        }

        // ---- Guess with switch roles ----

        [Test]
        public void Guess_MetallicTextureRole_GuessesTheMetallicConstant()
        {
            PortageManifest manifest = SwitchDependentNames(out PortageManifest.MaterialEntry entry);

            PortageMappingGuesser.Result result =
                PortageMappingGuesser.Guess(entry, manifest, null, Roles(SwitchRole.MetallicTexture));

            Assert.AreEqual("_Metallic", result.FindScalar("Metallic Constant").Property);
            Assert.AreEqual(ScalarConversion.None, result.FindScalar("Metallic Constant").Conversion);
            Assert.IsNull(result.FindScalar("Roughness Constant"));
        }

        [Test]
        public void Guess_RoughnessTextureRole_GuessesTheRoughnessConstantAsInvertedSmoothness()
        {
            PortageManifest manifest = SwitchDependentNames(out PortageManifest.MaterialEntry entry);

            PortageMappingGuesser.Result result =
                PortageMappingGuesser.Guess(entry, manifest, null, Roles(SwitchRole.RoughnessTexture));

            PortageMappingGuesser.ScalarGuess roughness = result.FindScalar("Roughness Constant");
            Assert.IsNotNull(roughness);
            Assert.AreEqual("_Smoothness", roughness.Property);
            Assert.AreEqual(ScalarConversion.OneMinus, roughness.Conversion);
            Assert.IsNull(result.FindScalar("Metallic Constant"));
        }

        [Test]
        public void Guess_BaseColorTextureRole_GuessesTheConstantAndKeepsTheTint()
        {
            PortageManifest manifest = SwitchDependentNames(out PortageManifest.MaterialEntry entry);

            PortageMappingGuesser.Result result =
                PortageMappingGuesser.Guess(entry, manifest, null, Roles(SwitchRole.BaseColorTexture));

            PortageMappingGuesser.VectorGuess constant = result.FindVector("Base Color Constant");
            Assert.IsNotNull(constant);
            Assert.AreEqual("_BaseColor", constant.Property);
            Assert.IsTrue(constant.Constant);

            PortageMappingGuesser.VectorGuess tint = result.FindVector("Base Color Multiplier");
            Assert.IsNotNull(tint);
            Assert.AreEqual("_BaseColor", tint.Property);
            Assert.IsFalse(tint.Constant);
        }

        [Test]
        public void Guess_EmissionRole_GuessesColorAndIntensity()
        {
            PortageManifest manifest = SwitchDependentNames(out PortageManifest.MaterialEntry entry);

            PortageMappingGuesser.Result result =
                PortageMappingGuesser.Guess(entry, manifest, null, Roles(SwitchRole.Emission));

            // One guess per property: the first parameter in manifest order wins.
            Assert.AreEqual("_EmissionColor", result.FindVector("Emissive Constant").Property);
            Assert.IsFalse(result.FindVector("Emissive Constant").Constant);
            Assert.IsNull(result.FindVector("Emissive Color"));
            Assert.AreEqual(PortageMappingTable.EmissionIntensityProperty, result.FindScalar("Emissive_Intencity").Property);
            Assert.IsNull(result.FindScalar("Emissive Intensity"));
            Assert.AreEqual("_EmissionIntensity", PortageMappingTable.EmissionIntensityProperty);
        }

        [TestCase("Emissive Color")]
        [TestCase("Emissive Constant")]
        public void Guess_EmissionRole_AcceptsBothColorNames(string name)
        {
            NewManifest(out PortageManifest.MaterialEntry entry);
            AddVector(entry, name);

            PortageMappingGuesser.Result result =
                PortageMappingGuesser.Guess(entry, new PortageManifest(), null, Roles(SwitchRole.Emission));

            Assert.AreEqual("_EmissionColor", result.FindVector(name).Property);
        }

        [TestCase("Emissive_Intencity")]
        [TestCase("Emissive Intensity")]
        public void Guess_EmissionRole_AcceptsBothIntensitySpellings(string name)
        {
            NewManifest(out PortageManifest.MaterialEntry entry);
            AddScalar(entry, name);

            PortageMappingGuesser.Result result =
                PortageMappingGuesser.Guess(entry, new PortageManifest(), null, Roles(SwitchRole.Emission));

            Assert.AreEqual(PortageMappingTable.EmissionIntensityProperty, result.FindScalar(name).Property);
        }

        [Test]
        public void Guess_EmissionRole_GuessesAnEmissiveTexture()
        {
            PortageManifest manifest = NewManifest(out PortageManifest.MaterialEntry entry);
            AddTexture(manifest, entry, "Unique_Emissive_Texture", Tex("T_Glow"));

            PortageMappingGuesser.TextureGuess guess =
                PortageMappingGuesser.Guess(entry, manifest, null, Roles(SwitchRole.Emission))
                    .FindTexture("Unique_Emissive_Texture");

            Assert.IsNotNull(guess);
            Assert.AreEqual(TextureUsage.Emission, guess.Usage);
        }

        [Test]
        public void Guess_EmissionRole_IgnoresAPlaceholderEmissiveTexture()
        {
            PortageManifest manifest = NewManifest(out PortageManifest.MaterialEntry entry);
            AddTexture(manifest, entry, "Unique_Emissive_Texture", Tex("T_Gray"));

            PortageMappingGuesser.Result result =
                PortageMappingGuesser.Guess(entry, manifest, null, Roles(SwitchRole.Emission));

            Assert.IsNull(result.FindTexture("Unique_Emissive_Texture"));
        }

        [Test]
        public void Guess_EmissionRole_ParenthesisedLayoutGetsAnEmissionAlpha()
        {
            PortageManifest manifest = SwitchDependentNames(out PortageManifest.MaterialEntry entry);

            PortageMappingGuesser.TextureGuess mask =
                PortageMappingGuesser.Guess(entry, manifest, null, Roles(SwitchRole.Emission))
                    .FindTexture("ORMA(AO,Rough,Mat,Emis)");

            Assert.AreEqual(TextureUsage.Mask, mask.Usage);
            Assert.AreEqual(MaskChannel.Occlusion, mask.Red);
            Assert.AreEqual(MaskChannel.Roughness, mask.Green);
            Assert.AreEqual(MaskChannel.Metallic, mask.Blue);
            Assert.AreEqual(MaskChannel.Emission, mask.Alpha);
        }

        [Test]
        public void Guess_EmissionRole_UpperCaseTokenWithEHasAnEmissionChannel()
        {
            PortageManifest manifest = NewManifest(out PortageManifest.MaterialEntry entry);
            AddTexture(manifest, entry, "ORME", Tex("T_Packed"));

            PortageMappingGuesser.TextureGuess mask =
                PortageMappingGuesser.Guess(entry, manifest, null, Roles(SwitchRole.Emission)).FindTexture("ORME");

            Assert.IsNotNull(mask);
            Assert.AreEqual(MaskChannel.Occlusion, mask.Red);
            Assert.AreEqual(MaskChannel.Roughness, mask.Green);
            Assert.AreEqual(MaskChannel.Metallic, mask.Blue);
            Assert.AreEqual(MaskChannel.Emission, mask.Alpha);
        }
    }
}
