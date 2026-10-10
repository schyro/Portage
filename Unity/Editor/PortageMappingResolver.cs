using System.Collections.Generic;

namespace Portage
{
    /// <summary>Where the decision for a parameter came from.</summary>
    public enum MappingSource
    {
        /// <summary>No decision: the parameter is not mapped.</summary>
        None,

        /// <summary>The <c>portage-mapping.json</c> sidecar.</summary>
        Saved,

        /// <summary>A rule in the mapping table.</summary>
        Table,

        /// <summary>A guess from names and texture settings.</summary>
        Guess,
    }

    /// <summary>The final decision for one texture parameter.</summary>
    public sealed class TextureDecision
    {
        public string Parameter = "";
        public MappingSource Source;
        public TextureUsage Usage;
        public MaskChannel Red, Green, Blue, Alpha;

        public bool IsMapped => Source != MappingSource.None;
    }

    /// <summary>The final decision for one scalar parameter. An empty property means "ignore".</summary>
    public sealed class ScalarDecision
    {
        public string Parameter = "";
        public MappingSource Source;
        public string Property = "";
        public ScalarConversion Conversion;

        public bool IsMapped => Source != MappingSource.None;
        public bool IsIgnored => IsMapped && string.IsNullOrEmpty(Property);
    }

    /// <summary>The final decision for one vector parameter. An empty property means "ignore".</summary>
    public sealed class VectorDecision
    {
        public string Parameter = "";
        public MappingSource Source;
        public string Property = "";

        /// <summary>The value replaces the texture of the input and is only used when the material has none.</summary>
        public bool Constant;

        public bool IsMapped => Source != MappingSource.None;
        public bool IsIgnored => IsMapped && string.IsNullOrEmpty(Property);
    }

    /// <summary>
    /// The final decision for one static switch parameter. The role is shared by every material of a base
    /// material; the value is the one of this material.
    /// </summary>
    public sealed class SwitchDecision
    {
        public string Parameter = "";
        public MappingSource Source;
        public SwitchRole Role;

        /// <summary>Whether the switch is on in this material.</summary>
        public bool Value;

        public bool IsMapped => Source != MappingSource.None;
    }

    /// <summary>
    /// The decisions for every parameter of one material. Each list has one entry per parameter, in the order
    /// of the manifest material, so index i belongs to parameter i.
    /// </summary>
    public sealed class PortageMappingResolution
    {
        public readonly List<TextureDecision> Textures = new List<TextureDecision>();
        public readonly List<ScalarDecision> Scalars = new List<ScalarDecision>();
        public readonly List<VectorDecision> Vectors = new List<VectorDecision>();
        public readonly List<SwitchDecision> Switches = new List<SwitchDecision>();

        public TextureDecision FindTexture(string parameter) => Textures.Find(d => d.Parameter == parameter);
        public ScalarDecision FindScalar(string parameter) => Scalars.Find(d => d.Parameter == parameter);
        public VectorDecision FindVector(string parameter) => Vectors.Find(d => d.Parameter == parameter);
        public SwitchDecision FindSwitch(string parameter) => Switches.Find(d => d.Parameter == parameter);

        /// <summary>Returns the switch that has the role, or null. A material without it behaves as before switches existed.</summary>
        public SwitchDecision SwitchWith(SwitchRole role)
        {
            return role == SwitchRole.None ? null : Switches.Find(d => d.IsMapped && d.Role == role);
        }

        /// <summary>True when a switch has the role and is on in this material.</summary>
        public bool IsOn(SwitchRole role)
        {
            SwitchDecision decision = SwitchWith(role);
            return decision != null && decision.Value;
        }

        /// <summary>True when a switch has the role and is off in this material.</summary>
        public bool IsOff(SwitchRole role)
        {
            SwitchDecision decision = SwitchWith(role);
            return decision != null && !decision.Value;
        }
    }

    /// <summary>
    /// Applies the precedence of Docs/mapping-format.md: sidecar, then mapping table, then guess. A guess never
    /// takes an input that a sidecar entry or a table rule already assigned for the same material.
    /// Decisions depend on the names of the parameters and switches only, so every material of a base material
    /// gets the same ones; the switch values of a material are applied when its material is built.
    /// </summary>
    public static class PortageMappingResolver
    {
        sealed class Taken
        {
            public bool BaseColor, Normal, ColorMask, EmissionTexture, Metallic, Smoothness, Occlusion;
            public readonly HashSet<string> ScalarProperties = new HashSet<string>();
            public readonly HashSet<string> VectorProperties = new HashSet<string>();
        }

        /// <param name="table">Mapping table, or null for none.</param>
        /// <param name="file">Sidecar choices, or null for none.</param>
        public static PortageMappingResolution Resolve(PortageManifest.MaterialEntry entry, PortageManifest manifest,
            PortageMappingTable table, PortageMappingFile file)
        {
            var resolution = new PortageMappingResolution();
            var taken = new Taken();
            string key = PortageMappingFile.KeyOf(entry);

            // Switches first: their roles decide which guesses are made for the other parameters.
            ISet<SwitchRole> roles = ResolveSwitches(entry, key, table, file, resolution);

            // Sources 1 and 2.
            foreach (PortageManifest.TextureParameter parameter in entry.textureParameters)
            {
                TextureDecision decision = ResolveStoredTexture(key, parameter.name, table, file);
                resolution.Textures.Add(decision);
                if (decision.IsMapped && manifest.FindTexture(parameter.texture) != null)
                    Claim(taken, decision);
            }

            foreach (PortageManifest.ScalarParameter parameter in entry.scalarParameters)
            {
                ScalarDecision decision = ResolveStoredScalar(key, parameter.name, table, file);
                resolution.Scalars.Add(decision);
                if (decision.IsMapped && !string.IsNullOrEmpty(decision.Property))
                    taken.ScalarProperties.Add(decision.Property);
            }

            foreach (PortageManifest.VectorParameter parameter in entry.vectorParameters)
            {
                VectorDecision decision = ResolveStoredVector(key, parameter.name, table, file);
                resolution.Vectors.Add(decision);
                if (decision.IsMapped && !string.IsNullOrEmpty(decision.Property))
                    taken.VectorProperties.Add(VectorSlot(decision.Property, decision.Constant));
            }

            // Source 3.
            var decided = new HashSet<string>();
            foreach (TextureDecision decision in resolution.Textures)
            {
                if (decision.IsMapped)
                    decided.Add(decision.Parameter);
            }

            PortageMappingGuesser.Result guesses = PortageMappingGuesser.Guess(entry, manifest, decided, roles);

            foreach (TextureDecision decision in resolution.Textures)
            {
                if (decision.IsMapped)
                    continue;

                PortageMappingGuesser.TextureGuess guess = guesses.FindTexture(decision.Parameter);
                if (guess != null)
                    ApplyTextureGuess(decision, guess, taken);
            }

            foreach (ScalarDecision decision in resolution.Scalars)
            {
                PortageMappingGuesser.ScalarGuess guess = decision.IsMapped ? null : guesses.FindScalar(decision.Parameter);
                if (guess == null || !taken.ScalarProperties.Add(guess.Property))
                    continue;

                decision.Source = MappingSource.Guess;
                decision.Property = guess.Property;
                decision.Conversion = guess.Conversion;
            }

            foreach (VectorDecision decision in resolution.Vectors)
            {
                PortageMappingGuesser.VectorGuess guess = decision.IsMapped ? null : guesses.FindVector(decision.Parameter);
                if (guess == null || !taken.VectorProperties.Add(VectorSlot(guess.Property, guess.Constant)))
                    continue;

                decision.Source = MappingSource.Guess;
                decision.Property = guess.Property;
                decision.Constant = guess.Constant;
            }

            return resolution;
        }

        // A constant and a tint can both target one property: only one of them is in effect per material.
        static string VectorSlot(string property, bool constant)
        {
            return constant ? property + "|constant" : property;
        }

        // Fills resolution.Switches and returns the roles the switches of the material have.
        static ISet<SwitchRole> ResolveSwitches(PortageManifest.MaterialEntry entry, string key,
            PortageMappingTable table, PortageMappingFile file, PortageMappingResolution resolution)
        {
            var roles = new HashSet<SwitchRole>();
            var decided = new HashSet<string>();
            foreach (PortageManifest.StaticSwitchParameter parameter in entry.staticSwitchParameters)
            {
                var decision = new SwitchDecision { Parameter = parameter.name, Value = parameter.value };
                resolution.Switches.Add(decision);

                PortageMappingFile.SwitchChoice choice = file?.FindSwitch(key, parameter.name);
                PortageMappingTable.SwitchRule rule = table != null ? table.FindSwitchRule(parameter.name) : null;
                if (choice != null && PortageMappingFile.TryParseEnum(choice.role, out SwitchRole role))
                {
                    decision.Source = MappingSource.Saved;
                    decision.Role = role;
                }
                else if (rule != null)
                {
                    decision.Source = MappingSource.Table;
                    decision.Role = rule.role;
                }
                else
                    continue;

                decided.Add(parameter.name);
                // A role belongs to one switch; a later switch that claims it again is left without a role.
                if (decision.Role != SwitchRole.None && !roles.Add(decision.Role))
                    decision.Role = SwitchRole.None;
            }

            foreach (PortageMappingGuesser.SwitchGuess guess in PortageMappingGuesser.GuessSwitches(entry, decided, roles))
            {
                SwitchDecision decision = resolution.FindSwitch(guess.Parameter);
                decision.Source = MappingSource.Guess;
                decision.Role = guess.Role;
                roles.Add(guess.Role);
            }

            return roles;
        }

        static TextureDecision ResolveStoredTexture(string key, string parameter, PortageMappingTable table,
            PortageMappingFile file)
        {
            var decision = new TextureDecision { Parameter = parameter };

            PortageMappingFile.TextureChoice choice = file?.FindTexture(key, parameter);
            if (choice != null && PortageMappingFile.TryParseEnum(choice.usage, out TextureUsage usage))
            {
                decision.Source = MappingSource.Saved;
                decision.Usage = usage;
                decision.Red = PortageMappingFile.ParseChannel(choice.red);
                decision.Green = PortageMappingFile.ParseChannel(choice.green);
                decision.Blue = PortageMappingFile.ParseChannel(choice.blue);
                decision.Alpha = PortageMappingFile.ParseChannel(choice.alpha);
                return decision;
            }

            PortageMappingTable.TextureRule rule = table != null ? table.FindTextureRule(parameter) : null;
            if (rule != null)
            {
                decision.Source = MappingSource.Table;
                decision.Usage = rule.usage;
                decision.Red = rule.red;
                decision.Green = rule.green;
                decision.Blue = rule.blue;
                decision.Alpha = rule.alpha;
            }

            return decision;
        }

        static ScalarDecision ResolveStoredScalar(string key, string parameter, PortageMappingTable table,
            PortageMappingFile file)
        {
            var decision = new ScalarDecision { Parameter = parameter };

            PortageMappingFile.ScalarChoice choice = file?.FindScalar(key, parameter);
            if (choice != null)
            {
                decision.Source = MappingSource.Saved;
                decision.Property = choice.property ?? "";
                decision.Conversion = PortageMappingFile.ParseConversion(choice.conversion);
                return decision;
            }

            PortageMappingTable.ScalarRule rule = table != null ? table.FindScalarRule(parameter) : null;
            if (rule != null)
            {
                decision.Source = MappingSource.Table;
                decision.Property = rule.property ?? "";
                decision.Conversion = rule.conversion;
            }

            return decision;
        }

        static VectorDecision ResolveStoredVector(string key, string parameter, PortageMappingTable table,
            PortageMappingFile file)
        {
            var decision = new VectorDecision { Parameter = parameter };

            PortageMappingFile.VectorChoice choice = file?.FindVector(key, parameter);
            if (choice != null)
            {
                decision.Source = MappingSource.Saved;
                decision.Property = choice.property ?? "";
                decision.Constant = choice.constant && !string.IsNullOrEmpty(decision.Property);
                return decision;
            }

            PortageMappingTable.VectorRule rule = table != null ? table.FindVectorRule(parameter) : null;
            if (rule != null)
            {
                decision.Source = MappingSource.Table;
                decision.Property = rule.property ?? "";
                decision.Constant = rule.constant && !string.IsNullOrEmpty(decision.Property);
            }

            return decision;
        }

        static void ApplyTextureGuess(TextureDecision decision, PortageMappingGuesser.TextureGuess guess, Taken taken)
        {
            switch (guess.Usage)
            {
                case TextureUsage.BaseColor:
                    if (taken.BaseColor)
                        return;
                    break;
                case TextureUsage.Normal:
                    if (taken.Normal)
                        return;
                    break;
                case TextureUsage.ColorMask:
                    if (taken.ColorMask)
                        return;
                    break;
                case TextureUsage.Emission:
                    if (taken.EmissionTexture)
                        return;
                    break;
                case TextureUsage.Mask:
                    decision.Red = Available(guess.Red, taken);
                    decision.Green = Available(guess.Green, taken, decision.Red);
                    decision.Blue = Available(guess.Blue, taken, decision.Red, decision.Green);
                    decision.Alpha = Available(guess.Alpha, taken, decision.Red, decision.Green, decision.Blue);
                    if (decision.Red == MaskChannel.None && decision.Green == MaskChannel.None &&
                        decision.Blue == MaskChannel.None && decision.Alpha == MaskChannel.None)
                        return;
                    break;
                default:
                    return;
            }

            decision.Source = MappingSource.Guess;
            decision.Usage = guess.Usage;
            Claim(taken, decision);
        }

        // A channel is dropped when its content is already provided, by a stored decision or an earlier channel.
        static MaskChannel Available(MaskChannel channel, Taken taken, params MaskChannel[] earlier)
        {
            if (channel == MaskChannel.None || IsTaken(channel, taken))
                return MaskChannel.None;

            foreach (MaskChannel other in earlier)
            {
                if (Slot(other) == Slot(channel))
                    return MaskChannel.None;
            }

            return channel;
        }

        static MaskChannel Slot(MaskChannel channel)
        {
            return channel == MaskChannel.Roughness ? MaskChannel.Smoothness : channel;
        }

        static bool IsTaken(MaskChannel channel, Taken taken)
        {
            switch (channel)
            {
                case MaskChannel.Metallic: return taken.Metallic;
                case MaskChannel.Roughness:
                case MaskChannel.Smoothness: return taken.Smoothness;
                case MaskChannel.Occlusion: return taken.Occlusion;
                default: return false;
            }
        }

        static void Claim(Taken taken, TextureDecision decision)
        {
            switch (decision.Usage)
            {
                case TextureUsage.BaseColor:
                    taken.BaseColor = true;
                    break;
                case TextureUsage.Normal:
                    taken.Normal = true;
                    break;
                case TextureUsage.ColorMask:
                    taken.ColorMask = true;
                    break;
                case TextureUsage.Emission:
                    taken.EmissionTexture = true;
                    break;
                case TextureUsage.Mask:
                    foreach (MaskChannel channel in new[] { decision.Red, decision.Green, decision.Blue, decision.Alpha })
                    {
                        switch (channel)
                        {
                            case MaskChannel.Metallic: taken.Metallic = true; break;
                            case MaskChannel.Roughness:
                            case MaskChannel.Smoothness: taken.Smoothness = true; break;
                            case MaskChannel.Occlusion: taken.Occlusion = true; break;
                        }
                    }

                    break;
            }
        }
    }
}
