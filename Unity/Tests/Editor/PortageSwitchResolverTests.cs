using System.IO;
using NUnit.Framework;

namespace Portage.Tests
{
    public sealed class PortageSwitchResolverTests
    {
        const string BaseMaterial = "/Game/Test/M_Master.M_Master";

        string _tempFolder;

        [TearDown]
        public void TearDown()
        {
            if (_tempFolder != null && Directory.Exists(_tempFolder))
                Directory.Delete(_tempFolder, true);
            _tempFolder = null;
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

        static void AddSwitch(PortageManifest.MaterialEntry entry, string name, bool value = true)
        {
            entry.staticSwitchParameters.Add(new PortageManifest.StaticSwitchParameter { name = name, value = value });
        }

        static void AddVector(PortageManifest.MaterialEntry entry, string name)
        {
            entry.vectorParameters.Add(new PortageManifest.VectorParameter { name = name, value = new[] { 1f, 1f, 1f, 1f } });
        }

        static PortageMappingTable NewTable()
        {
            var table = PortageMappingTable.CreateDefault();
            table.switchRules.Clear();
            return table;
        }

        static PortageMappingTable.SwitchRule Rule(SwitchRole role, params string[] names)
        {
            return new PortageMappingTable.SwitchRule
                { parameterNames = new System.Collections.Generic.List<string>(names), role = role };
        }

        // ---- Precedence ----

        [Test]
        public void Switch_WithoutAnyRule_IsGuessedAndKeepsItsValue()
        {
            PortageManifest manifest = NewManifest(out PortageManifest.MaterialEntry entry);
            AddSwitch(entry, "Use Normal", false);
            AddSwitch(entry, "WS Dust", true);

            PortageMappingResolution resolution = PortageMappingResolver.Resolve(entry, manifest, null, null);

            Assert.AreEqual(2, resolution.Switches.Count);
            SwitchDecision normal = resolution.FindSwitch("Use Normal");
            Assert.AreEqual(MappingSource.Guess, normal.Source);
            Assert.AreEqual(SwitchRole.NormalTexture, normal.Role);
            Assert.IsFalse(normal.Value);
            Assert.IsTrue(normal.IsMapped);

            SwitchDecision dust = resolution.FindSwitch("WS Dust");
            Assert.AreEqual(MappingSource.None, dust.Source);
            Assert.AreEqual(SwitchRole.None, dust.Role);
            Assert.IsTrue(dust.Value);
            Assert.IsFalse(dust.IsMapped);
            Assert.IsNull(resolution.FindSwitch("Nothing"));
        }

        [Test]
        public void Switch_TableRuleBeatsTheGuess()
        {
            PortageManifest manifest = NewManifest(out PortageManifest.MaterialEntry entry);
            AddSwitch(entry, "Use Normal");
            PortageMappingTable table = NewTable();
            table.switchRules.Add(Rule(SwitchRole.Occlusion, "use normal"));

            SwitchDecision decision = PortageMappingResolver.Resolve(entry, manifest, table, null).FindSwitch("Use Normal");

            Assert.AreEqual(MappingSource.Table, decision.Source);
            Assert.AreEqual(SwitchRole.Occlusion, decision.Role);
        }

        [Test]
        public void Switch_TableRuleCanGiveARoleToAnUnguessableName()
        {
            PortageManifest manifest = NewManifest(out PortageManifest.MaterialEntry entry);
            AddSwitch(entry, "WS Dust");
            PortageMappingTable table = NewTable();
            table.switchRules.Add(Rule(SwitchRole.Emission, "WS Dust"));

            PortageMappingResolution resolution = PortageMappingResolver.Resolve(entry, manifest, table, null);

            Assert.AreEqual(MappingSource.Table, resolution.FindSwitch("WS Dust").Source);
            Assert.IsTrue(resolution.IsOn(SwitchRole.Emission));
        }

        [Test]
        public void Switch_SavedChoiceBeatsTableAndGuess()
        {
            PortageManifest manifest = NewManifest(out PortageManifest.MaterialEntry entry);
            AddSwitch(entry, "Use Normal");
            PortageMappingTable table = NewTable();
            table.switchRules.Add(Rule(SwitchRole.Occlusion, "Use Normal"));
            var file = new PortageMappingFile();
            file.SetSwitch(PortageMappingFile.KeyOf(entry), "Use Normal", SwitchRole.Emission);

            SwitchDecision decision = PortageMappingResolver.Resolve(entry, manifest, table, file).FindSwitch("Use Normal");

            Assert.AreEqual(MappingSource.Saved, decision.Source);
            Assert.AreEqual(SwitchRole.Emission, decision.Role);
        }

        [Test]
        public void Switch_SavedChoiceOfAnotherMaterialIsNotUsed()
        {
            PortageManifest manifest = NewManifest(out PortageManifest.MaterialEntry entry);
            AddSwitch(entry, "Use Normal");
            var file = new PortageMappingFile();
            file.SetSwitch("/Game/Test/M_Other.M_Other", "Use Normal", SwitchRole.Emission);

            SwitchDecision decision = PortageMappingResolver.Resolve(entry, manifest, null, file).FindSwitch("Use Normal");

            Assert.AreEqual(MappingSource.Guess, decision.Source);
            Assert.AreEqual(SwitchRole.NormalTexture, decision.Role);
        }

        [Test]
        public void Switch_SavedNone_IgnoresTheSwitchAndBlocksTheGuess()
        {
            PortageManifest manifest = NewManifest(out PortageManifest.MaterialEntry entry);
            AddSwitch(entry, "Use Normal", false);
            var file = new PortageMappingFile();
            file.SetSwitch(PortageMappingFile.KeyOf(entry), "Use Normal", SwitchRole.None);

            PortageMappingResolution resolution = PortageMappingResolver.Resolve(entry, manifest, null, file);

            SwitchDecision decision = resolution.FindSwitch("Use Normal");
            Assert.AreEqual(MappingSource.Saved, decision.Source);
            Assert.AreEqual(SwitchRole.None, decision.Role);
            Assert.IsNull(resolution.SwitchWith(SwitchRole.NormalTexture));
            Assert.IsFalse(resolution.IsOff(SwitchRole.NormalTexture));
        }

        [Test]
        public void Switch_SavedNone_LetsALaterSwitchTakeTheRole()
        {
            PortageManifest manifest = NewManifest(out PortageManifest.MaterialEntry entry);
            AddSwitch(entry, "Use Normal");
            AddSwitch(entry, "Use Normal Map");
            var file = new PortageMappingFile();
            file.SetSwitch(PortageMappingFile.KeyOf(entry), "Use Normal", SwitchRole.None);

            PortageMappingResolution resolution = PortageMappingResolver.Resolve(entry, manifest, null, file);

            Assert.AreEqual(SwitchRole.None, resolution.FindSwitch("Use Normal").Role);
            Assert.AreEqual("Use Normal Map", resolution.SwitchWith(SwitchRole.NormalTexture).Parameter);
        }

        [Test]
        public void Switch_UnknownSavedRoleCountsAsNoChoice()
        {
            PortageManifest manifest = NewManifest(out PortageManifest.MaterialEntry entry);
            AddSwitch(entry, "Use Normal");
            var file = new PortageMappingFile();
            file.SetSwitch(PortageMappingFile.KeyOf(entry), "Use Normal", SwitchRole.Occlusion);
            file.FindSwitch(PortageMappingFile.KeyOf(entry), "Use Normal").role = "NotARole";

            SwitchDecision decision = PortageMappingResolver.Resolve(entry, manifest, null, file).FindSwitch("Use Normal");

            Assert.AreEqual(MappingSource.Guess, decision.Source);
            Assert.AreEqual(SwitchRole.NormalTexture, decision.Role);
        }

        // ---- Role uniqueness ----

        [Test]
        public void Switch_RoleTakenBySavedChoice_IsNotGuessedForAnotherSwitch()
        {
            PortageManifest manifest = NewManifest(out PortageManifest.MaterialEntry entry);
            AddSwitch(entry, "WS Dust");
            AddSwitch(entry, "Use Normal");
            var file = new PortageMappingFile();
            file.SetSwitch(PortageMappingFile.KeyOf(entry), "WS Dust", SwitchRole.NormalTexture);

            PortageMappingResolution resolution = PortageMappingResolver.Resolve(entry, manifest, null, file);

            Assert.AreEqual("WS Dust", resolution.SwitchWith(SwitchRole.NormalTexture).Parameter);
            SwitchDecision normal = resolution.FindSwitch("Use Normal");
            Assert.AreEqual(SwitchRole.None, normal.Role);
            Assert.AreEqual(MappingSource.None, normal.Source);
        }

        [Test]
        public void Switch_RoleTakenByTableRule_IsNotGuessedForAnotherSwitch()
        {
            PortageManifest manifest = NewManifest(out PortageManifest.MaterialEntry entry);
            AddSwitch(entry, "Use Normal");
            AddSwitch(entry, "WS Dust");
            PortageMappingTable table = NewTable();
            table.switchRules.Add(Rule(SwitchRole.NormalTexture, "WS Dust"));

            PortageMappingResolution resolution = PortageMappingResolver.Resolve(entry, manifest, table, null);

            Assert.AreEqual("WS Dust", resolution.SwitchWith(SwitchRole.NormalTexture).Parameter);
            Assert.AreEqual(MappingSource.None, resolution.FindSwitch("Use Normal").Source);
            Assert.AreEqual(SwitchRole.None, resolution.FindSwitch("Use Normal").Role);
        }

        [Test]
        public void Switch_TwoGuessesForOneRole_FirstInManifestOrderWins()
        {
            PortageManifest manifest = NewManifest(out PortageManifest.MaterialEntry entry);
            AddSwitch(entry, "Use Normal Map", false);
            AddSwitch(entry, "Use Normal", true);

            PortageMappingResolution resolution = PortageMappingResolver.Resolve(entry, manifest, null, null);

            Assert.AreEqual("Use Normal Map", resolution.SwitchWith(SwitchRole.NormalTexture).Parameter);
            Assert.IsFalse(resolution.FindSwitch("Use Normal").IsMapped);
            Assert.IsTrue(resolution.IsOff(SwitchRole.NormalTexture));
        }

        [Test]
        public void Switch_TwoRulesForOneRole_OnlyTheFirstKeepsIt()
        {
            PortageManifest manifest = NewManifest(out PortageManifest.MaterialEntry entry);
            AddSwitch(entry, "WS Dust");
            AddSwitch(entry, "Multiply_Breakup");
            PortageMappingTable table = NewTable();
            table.switchRules.Add(Rule(SwitchRole.Emission, "WS Dust", "Multiply_Breakup"));

            PortageMappingResolution resolution = PortageMappingResolver.Resolve(entry, manifest, table, null);

            Assert.AreEqual("WS Dust", resolution.SwitchWith(SwitchRole.Emission).Parameter);
            Assert.AreEqual(SwitchRole.None, resolution.FindSwitch("Multiply_Breakup").Role);
        }

        // ---- IsOn / IsOff / SwitchWith ----

        [Test]
        public void IsOnAndIsOff_FollowTheValueOfTheSwitchWithTheRole()
        {
            PortageManifest manifest = NewManifest(out PortageManifest.MaterialEntry entry);
            AddSwitch(entry, "Use Normal", true);
            AddSwitch(entry, "Use AO", false);

            PortageMappingResolution resolution = PortageMappingResolver.Resolve(entry, manifest, null, null);

            Assert.IsTrue(resolution.IsOn(SwitchRole.NormalTexture));
            Assert.IsFalse(resolution.IsOff(SwitchRole.NormalTexture));
            Assert.IsFalse(resolution.IsOn(SwitchRole.Occlusion));
            Assert.IsTrue(resolution.IsOff(SwitchRole.Occlusion));
            Assert.AreEqual("Use AO", resolution.SwitchWith(SwitchRole.Occlusion).Parameter);
        }

        [Test]
        public void IsOnAndIsOff_AreBothFalseWhenNoSwitchHasTheRole()
        {
            PortageManifest manifest = NewManifest(out PortageManifest.MaterialEntry entry);
            AddSwitch(entry, "Use Normal", true);

            PortageMappingResolution resolution = PortageMappingResolver.Resolve(entry, manifest, null, null);

            Assert.IsNull(resolution.SwitchWith(SwitchRole.Emission));
            Assert.IsFalse(resolution.IsOn(SwitchRole.Emission));
            Assert.IsFalse(resolution.IsOff(SwitchRole.Emission));
            Assert.IsNull(resolution.SwitchWith(SwitchRole.None));
            Assert.IsFalse(resolution.IsOn(SwitchRole.None));
            Assert.IsFalse(resolution.IsOff(SwitchRole.None));
        }

        [Test]
        public void MaterialWithoutSwitches_HasNoSwitchDecisions()
        {
            PortageManifest manifest = NewManifest(out PortageManifest.MaterialEntry entry);

            PortageMappingResolution resolution = PortageMappingResolver.Resolve(entry, manifest, null, null);

            Assert.AreEqual(0, resolution.Switches.Count);
            Assert.IsFalse(resolution.IsOn(SwitchRole.NormalTexture));
            Assert.IsFalse(resolution.IsOff(SwitchRole.NormalTexture));
        }

        // ---- Vector constant ----

        [Test]
        public void Vector_ConstantComesFromTheSavedChoice()
        {
            PortageManifest manifest = NewManifest(out PortageManifest.MaterialEntry entry);
            AddVector(entry, "Paint");
            var file = new PortageMappingFile();
            file.SetVector(PortageMappingFile.KeyOf(entry), "Paint", "_BaseColor", true);

            VectorDecision decision = PortageMappingResolver.Resolve(entry, manifest, null, file).FindVector("Paint");

            Assert.AreEqual(MappingSource.Saved, decision.Source);
            Assert.AreEqual("_BaseColor", decision.Property);
            Assert.IsTrue(decision.Constant);
        }

        [Test]
        public void Vector_SavedWithoutConstantIsNotAConstant()
        {
            PortageManifest manifest = NewManifest(out PortageManifest.MaterialEntry entry);
            AddVector(entry, "Paint");
            var file = new PortageMappingFile();
            file.SetVector(PortageMappingFile.KeyOf(entry), "Paint", "_BaseColor");

            Assert.IsFalse(PortageMappingResolver.Resolve(entry, manifest, null, file).FindVector("Paint").Constant);
        }

        [Test]
        public void Vector_ConstantFlagIsDroppedWhenTheParameterIsIgnored()
        {
            var file = new PortageMappingFile();
            file.SetVector(BaseMaterial, "Paint", "", true);

            Assert.IsFalse(file.FindVector(BaseMaterial, "Paint").constant);
        }

        [Test]
        public void Vector_ConstantComesFromTheTableRule()
        {
            PortageManifest manifest = NewManifest(out PortageManifest.MaterialEntry entry);
            AddVector(entry, "Paint");
            PortageMappingTable table = NewTable();
            table.vectorRules.Add(new PortageMappingTable.VectorRule
            {
                parameterNames = new System.Collections.Generic.List<string> { "Paint" },
                property = "_BaseColor", constant = true,
            });

            VectorDecision decision = PortageMappingResolver.Resolve(entry, manifest, table, null).FindVector("Paint");

            Assert.AreEqual(MappingSource.Table, decision.Source);
            Assert.IsTrue(decision.Constant);
        }

        [Test]
        public void Vector_ConstantComesFromTheGuessWhenTheMaterialHasABaseColorTextureSwitch()
        {
            PortageManifest manifest = NewManifest(out PortageManifest.MaterialEntry entry);
            AddSwitch(entry, "Base Color Texture Input");
            AddVector(entry, "Base Color Constant");
            AddVector(entry, "Base Color Multiplier");

            PortageMappingResolution resolution = PortageMappingResolver.Resolve(entry, manifest, null, null);

            VectorDecision constant = resolution.FindVector("Base Color Constant");
            Assert.AreEqual(MappingSource.Guess, constant.Source);
            Assert.AreEqual("_BaseColor", constant.Property);
            Assert.IsTrue(constant.Constant);

            // The tint and the constant both target _BaseColor and both stay mapped.
            VectorDecision tint = resolution.FindVector("Base Color Multiplier");
            Assert.AreEqual(MappingSource.Guess, tint.Source);
            Assert.AreEqual("_BaseColor", tint.Property);
            Assert.IsFalse(tint.Constant);
        }

        [Test]
        public void Vector_ConstantIsNotGuessedWithoutTheSwitch()
        {
            PortageManifest manifest = NewManifest(out PortageManifest.MaterialEntry entry);
            AddVector(entry, "Base Color Constant");
            AddVector(entry, "Base Color Multiplier");

            PortageMappingResolution resolution = PortageMappingResolver.Resolve(entry, manifest, null, null);

            Assert.IsFalse(resolution.FindVector("Base Color Constant").IsMapped);
            Assert.IsTrue(resolution.FindVector("Base Color Multiplier").IsMapped);
        }

        [Test]
        public void Vector_SavedSwitchRoleEnablesTheConstantGuess()
        {
            PortageManifest manifest = NewManifest(out PortageManifest.MaterialEntry entry);
            AddSwitch(entry, "WS Dust");
            AddVector(entry, "Base Color Constant");
            var file = new PortageMappingFile();
            file.SetSwitch(PortageMappingFile.KeyOf(entry), "WS Dust", SwitchRole.BaseColorTexture);

            VectorDecision decision = PortageMappingResolver.Resolve(entry, manifest, null, file).FindVector("Base Color Constant");

            Assert.AreEqual(MappingSource.Guess, decision.Source);
            Assert.IsTrue(decision.Constant);
        }

        // ---- Mapping file ----

        [Test]
        public void MappingFile_RoundTripsSwitchesAndTheConstantFlag()
        {
            _tempFolder = Path.Combine(Path.GetTempPath(), "portage-switch-" + System.Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_tempFolder);
            var file = new PortageMappingFile();
            file.SetSwitch(BaseMaterial, "Use Normal", SwitchRole.NormalTexture);
            file.SetSwitch(BaseMaterial, "WS Dust", SwitchRole.None);
            file.SetVector(BaseMaterial, "Paint", "_BaseColor", true);
            file.SetVector(BaseMaterial, "Tint", "_BaseColor");

            file.Save(_tempFolder);
            PortageMappingFile loaded = PortageMappingFile.Load(_tempFolder);

            Assert.AreEqual("", loaded.LoadError);
            Assert.AreEqual("NormalTexture", loaded.FindSwitch(BaseMaterial, "Use Normal").role);
            Assert.AreEqual("None", loaded.FindSwitch(BaseMaterial, "WS Dust").role);
            Assert.IsNull(loaded.FindSwitch(BaseMaterial, "Missing"));
            Assert.IsTrue(loaded.FindVector(BaseMaterial, "Paint").constant);
            Assert.IsFalse(loaded.FindVector(BaseMaterial, "Tint").constant);
            Assert.AreEqual(2, loaded.FindMaterial(BaseMaterial).switches.Count);
        }

        [Test]
        public void MappingFile_SetSwitchReplacesAndRemoveSwitchDeletes()
        {
            var file = new PortageMappingFile();
            file.SetSwitch(BaseMaterial, "Use Normal", SwitchRole.NormalTexture);
            file.SetSwitch(BaseMaterial, "Use Normal", SwitchRole.Occlusion);

            Assert.AreEqual(1, file.FindMaterial(BaseMaterial).switches.Count);
            Assert.AreEqual("Occlusion", file.FindSwitch(BaseMaterial, "Use Normal").role);

            file.RemoveSwitch(BaseMaterial, "Use Normal");
            Assert.IsNull(file.FindSwitch(BaseMaterial, "Use Normal"));
            Assert.DoesNotThrow(() => file.RemoveSwitch("/Game/Test/Unknown.Unknown", "Use Normal"));
        }

        [Test]
        public void MappingFile_WrittenBeforeSwitchesExistedLoadsWithoutErrors()
        {
            _tempFolder = Path.Combine(Path.GetTempPath(), "portage-switch-" + System.Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_tempFolder);
            File.WriteAllText(PortageMappingFile.PathIn(_tempFolder),
                "{\n  \"version\": 1,\n  \"materials\": [\n    {\n      \"baseMaterial\": \"" + BaseMaterial + "\",\n" +
                "      \"textures\": [ { \"parameter\": \"BaseColor\", \"usage\": \"Ignore\" } ],\n" +
                "      \"scalars\": [],\n" +
                "      \"vectors\": [ { \"parameter\": \"Tint\", \"property\": \"_BaseColor\" } ]\n" +
                "    }\n  ],\n  \"applyOcclusion\": true\n}\n");

            PortageMappingFile loaded = PortageMappingFile.Load(_tempFolder);

            Assert.AreEqual("", loaded.LoadError);
            Assert.IsTrue(loaded.applyOcclusion);
            PortageMappingFile.MaterialChoices material = loaded.FindMaterial(BaseMaterial);
            Assert.IsNotNull(material);
            Assert.IsNotNull(material.switches);
            Assert.AreEqual(0, material.switches.Count);
            Assert.IsNull(loaded.FindSwitch(BaseMaterial, "Use Normal"));
            Assert.AreEqual("_BaseColor", loaded.FindVector(BaseMaterial, "Tint").property);
            Assert.IsFalse(loaded.FindVector(BaseMaterial, "Tint").constant);

            // It resolves like a file without switch choices.
            PortageManifest manifest = NewManifest(out PortageManifest.MaterialEntry entry);
            AddSwitch(entry, "Use Normal");
            AddVector(entry, "Tint");
            PortageMappingResolution resolution = PortageMappingResolver.Resolve(entry, manifest, null, loaded);
            Assert.AreEqual(MappingSource.Guess, resolution.FindSwitch("Use Normal").Source);
            Assert.AreEqual(MappingSource.Saved, resolution.FindVector("Tint").Source);
            Assert.IsFalse(resolution.FindVector("Tint").Constant);
        }
    }
}
