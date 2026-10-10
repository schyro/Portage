using System;
using System.Collections.Generic;
using System.Text;

namespace Portage
{
    /// <summary>
    /// Guesses which URP Lit input a material parameter feeds, from the parameter name, the texture name
    /// and the texture settings. Pure logic: it touches no assets, so it can be unit-tested.
    /// </summary>
    public static class PortageMappingGuesser
    {
        /// <summary>Textures that exist only because a parameter needs some value. Never guessed.</summary>
        static readonly string[] PlaceholderTextureNames =
        {
            "DefaultNormal", "T_Gray", "T_Black", "T_White", "DefaultTexture", "DefaultDiffuse",
            "WhiteSquareTexture", "BlackSquareTexture",
        };

        // Uppercase words made only of layout letters that are not channel layouts.
        static readonly string[] NotLayoutWords =
        {
            "MESH", "MASS", "MASH", "HARM", "SHAM", "DRAM", "ROAM", "SEAM", "MARS", "MOSS", "MODS", "DORM",
        };

        static readonly string[] TrailingWords = { "texture", "tex", "map" };
        static readonly string[] ConstantWords = { "constant" };

        static readonly HashSet<string> BaseColorNames = new HashSet<string>
        {
            "basecolor", "albedo", "diffuse", "color", "basecolour", "diffusecolor", "albedocolor",
        };

        static readonly HashSet<string> ColorMaskNames = new HashSet<string>
        {
            "colormask", "colourmask", "tintmask", "paintmask",
        };

        static readonly HashSet<string> NormalNames = new HashSet<string> { "normal", "normals", "nrm", "basenormal" };

        static readonly HashSet<string> RoughnessNames = new HashSet<string> { "roughness" };
        static readonly HashSet<string> MetallicNames = new HashSet<string> { "metallic", "metalness", "metal" };
        static readonly HashSet<string> OcclusionNames = new HashSet<string> { "ao", "ambientocclusion", "occlusion" };
        static readonly HashSet<string> SmoothnessNames = new HashSet<string> { "smoothness", "gloss", "glossiness" };

        static readonly HashSet<string> MetallicScalarNames = new HashSet<string> { "metallic", "metalness" };
        static readonly HashSet<string> RoughnessScalarNames = new HashSet<string> { "roughness" };

        static readonly HashSet<string> BumpScaleNames = new HashSet<string>
        {
            "normalstrength", "normalintensity", "normalscale", "bumpscale",
        };

        static readonly HashSet<string> BaseColorVectorNames = new HashSet<string>
        {
            "basecolormultiplier", "basecolortint", "tint", "colortint", "basecolor", "color", "albedotint",
        };

        static readonly HashSet<string> EmissionNames = new HashSet<string>
        {
            "emissive", "emission", "emissivecolor", "emissioncolor", "emissivecolour", "emissioncolour",
        };

        // What follows "emissive" or "emission" in the name of a scalar that scales the emission.
        static readonly string[] EmissionIntensityWords =
        {
            "intensity", "intencity", "strength", "power", "multiplier", "brightness", "boost", "scale",
        };

        static readonly string[] SwitchPrefixes = { "use", "enable", "has", "with" };
        static readonly string[] SwitchSuffixes = { "input", "enabled", "switch", "toggle" };
        static readonly string[] SeparateWords = { "separate", "seperate", "unique" };

        public sealed class TextureGuess
        {
            public string Parameter = "";
            public TextureUsage Usage;
            public MaskChannel Red, Green, Blue, Alpha;
        }

        public sealed class ScalarGuess
        {
            public string Parameter = "";
            public string Property = "";
            public ScalarConversion Conversion;
        }

        public sealed class VectorGuess
        {
            public string Parameter = "";
            public string Property = "";

            /// <summary>The value replaces the texture of the input and is only used when the material has none.</summary>
            public bool Constant;
        }

        public sealed class SwitchGuess
        {
            public string Parameter = "";
            public SwitchRole Role;
        }

        public sealed class Result
        {
            public readonly List<TextureGuess> Textures = new List<TextureGuess>();
            public readonly List<ScalarGuess> Scalars = new List<ScalarGuess>();
            public readonly List<VectorGuess> Vectors = new List<VectorGuess>();

            public TextureGuess FindTexture(string parameter) => Textures.Find(g => g.Parameter == parameter);
            public ScalarGuess FindScalar(string parameter) => Scalars.Find(g => g.Parameter == parameter);
            public VectorGuess FindVector(string parameter) => Vectors.Find(g => g.Parameter == parameter);
        }

        sealed class Candidate
        {
            public PortageManifest.TextureParameter Parameter;
            public PortageManifest.TextureEntry Texture;
            public string Norm = "";
        }

        /// <summary>
        /// Lowercases a name, removes every non-alphanumeric character and strips one trailing
        /// <c>texture</c>, <c>tex</c> or <c>map</c>. <c>Base Color Texture</c> becomes <c>basecolor</c>.
        /// </summary>
        public static string NormalizeName(string name)
        {
            string s = Flatten(name);
            foreach (string word in TrailingWords)
            {
                if (s.Length > word.Length && s.EndsWith(word, StringComparison.Ordinal))
                    return s.Substring(0, s.Length - word.Length);
            }

            return s;
        }

        /// <summary>True for engine default textures and well-known placeholder names.</summary>
        public static bool IsPlaceholder(PortageManifest.TextureEntry texture)
        {
            if (texture.id != null && texture.id.StartsWith("/Engine/", StringComparison.Ordinal))
                return true;

            foreach (string name in PlaceholderTextureNames)
            {
                if (string.Equals(texture.name, name, StringComparison.OrdinalIgnoreCase))
                    return true;
            }

            return false;
        }

        /// <summary>
        /// Guesses what a static switch turns on, from its name: <c>Use Normal</c>, <c>Metallic Texture Input</c>,
        /// <c>Use Emissive</c>. A switch for base color, metallic or roughness must name the texture, because
        /// without it the name does not say what the switch chooses between.
        /// </summary>
        public static SwitchRole GuessSwitchRole(string name)
        {
            string s = Flatten(name);
            s = StripStart(s, SwitchPrefixes, out _);
            s = StripEnd(s, SwitchSuffixes, out _);
            s = StripEnd(s, TrailingWords, out bool texture);

            if (ColorMaskNames.Contains(s))
                return SwitchRole.ColorMask;
            if (NormalNames.Contains(s))
                return SwitchRole.NormalTexture;
            if (OcclusionNames.Contains(s))
                return SwitchRole.Occlusion;
            if (EmissionNames.Contains(s))
                return texture ? SwitchRole.EmissionTexture : SwitchRole.Emission;
            if (!texture)
                return SwitchRole.None;

            // "color" alone is too vague for a switch.
            if (s != "color" && BaseColorNames.Contains(s))
                return SwitchRole.BaseColorTexture;
            if (MetallicNames.Contains(s))
                return SwitchRole.MetallicTexture;
            if (RoughnessNames.Contains(s) || SmoothnessNames.Contains(s))
                return SwitchRole.RoughnessTexture;
            if (EmissionNames.Contains(StripStart(s, SeparateWords, out bool separate)) && separate)
                return SwitchRole.SeparateEmissionTexture;
            return SwitchRole.None;
        }

        /// <summary>
        /// Guesses the roles of the static switches of a material. Every role is given to at most one switch,
        /// the first in manifest order.
        /// </summary>
        /// <param name="excludedParameters">Switch names that already have a decision; they are not considered.</param>
        /// <param name="takenRoles">Roles a decision already assigned; they are not guessed again.</param>
        public static List<SwitchGuess> GuessSwitches(PortageManifest.MaterialEntry entry,
            ISet<string> excludedParameters = null, ISet<SwitchRole> takenRoles = null)
        {
            var guesses = new List<SwitchGuess>();
            var taken = takenRoles != null ? new HashSet<SwitchRole>(takenRoles) : new HashSet<SwitchRole>();
            foreach (PortageManifest.StaticSwitchParameter parameter in entry.staticSwitchParameters)
            {
                if (excludedParameters != null && excludedParameters.Contains(parameter.name))
                    continue;

                SwitchRole role = GuessSwitchRole(parameter.name);
                if (role != SwitchRole.None && taken.Add(role))
                    guesses.Add(new SwitchGuess { Parameter = parameter.name, Role = role });
            }

            return guesses;
        }

        /// <param name="excludedTextureParameters">
        /// Texture parameter names that already have a decision; they are not considered.
        /// </param>
        /// <param name="switchRoles">
        /// Roles of the static switches of the material. Some guesses are only made for a material that has a
        /// switch for them: emission needs <see cref="SwitchRole.Emission"/>, and a constant needs the switch
        /// that chooses between it and a texture. Null or empty for a material without such switches.
        /// </param>
        public static Result Guess(PortageManifest.MaterialEntry entry, PortageManifest manifest,
            ISet<string> excludedTextureParameters = null, ISet<SwitchRole> switchRoles = null)
        {
            var result = new Result();
            GuessTextures(entry, manifest, excludedTextureParameters, switchRoles, result);
            GuessScalars(entry, switchRoles, result);
            GuessVectors(entry, switchRoles, result);
            return result;
        }

        static bool Has(ISet<SwitchRole> roles, SwitchRole role) => roles != null && roles.Contains(role);

        static void GuessTextures(PortageManifest.MaterialEntry entry, PortageManifest manifest,
            ISet<string> excluded, ISet<SwitchRole> roles, Result result)
        {
            bool emission = Has(roles, SwitchRole.Emission);
            var candidates = new List<Candidate>();
            bool colorMaskFound = false;
            foreach (PortageManifest.TextureParameter parameter in entry.textureParameters)
            {
                if (excluded != null && excluded.Contains(parameter.name))
                    continue;

                PortageManifest.TextureEntry texture = manifest.FindTexture(parameter.texture);
                if (texture == null)
                    continue;

                // A color mask is the one usage a placeholder texture is allowed for: a uniform grey mask
                // legitimately means "apply the tint partially". Whether it is uniform is only known once it
                // is read, so the material builder drops a guessed placeholder that is not. At most one per
                // material.
                if (ColorMaskNames.Contains(NormalizeName(parameter.name)))
                {
                    if (!colorMaskFound)
                    {
                        colorMaskFound = true;
                        result.Textures.Add(new TextureGuess { Parameter = parameter.name, Usage = TextureUsage.ColorMask });
                    }

                    continue;
                }

                if (IsPlaceholder(texture))
                    continue;

                candidates.Add(new Candidate
                {
                    Parameter = parameter, Texture = texture, Norm = NormalizeName(parameter.name),
                });
            }

            var used = new HashSet<Candidate>();

            Candidate baseColor = null;
            foreach (Candidate c in candidates)
            {
                if (!BaseColorNames.Contains(c.Norm))
                    continue;
                if (baseColor == null || (!baseColor.Texture.srgb && c.Texture.srgb))
                    baseColor = c;
            }

            if (baseColor != null)
            {
                used.Add(baseColor);
                result.Textures.Add(new TextureGuess { Parameter = baseColor.Parameter.name, Usage = TextureUsage.BaseColor });
            }

            Candidate normal = PickNormal(candidates, used);
            if (normal != null)
            {
                used.Add(normal);
                result.Textures.Add(new TextureGuess { Parameter = normal.Parameter.name, Usage = TextureUsage.Normal });
            }

            // Only a material with an emission switch gets an emission map guessed. A parameter that is not
            // in use holds a placeholder, which is not a candidate.
            if (emission)
            {
                foreach (Candidate c in candidates)
                {
                    if (used.Contains(c) || !IsEmissionName(c.Norm))
                        continue;

                    used.Add(c);
                    result.Textures.Add(new TextureGuess { Parameter = c.Parameter.name, Usage = TextureUsage.Emission });
                    break;
                }
            }

            // At most one packed mask per material: the first parameter, in manifest order, with a layout.
            bool metallic = false, smoothness = false, occlusion = false;
            foreach (Candidate c in candidates)
            {
                if (used.Contains(c) || !TryGetPackedLayout(c, emission, out MaskChannel[] channels))
                    continue;

                used.Add(c);
                result.Textures.Add(MaskGuess(c.Parameter.name, channels[0], channels[1], channels[2], channels[3]));
                foreach (MaskChannel channel in channels)
                {
                    metallic |= channel == MaskChannel.Metallic;
                    smoothness |= channel == MaskChannel.Roughness || channel == MaskChannel.Smoothness;
                    occlusion |= channel == MaskChannel.Occlusion;
                }

                break;
            }

            // Single-channel masks fill only what the packed mask did not provide.
            foreach (Candidate c in candidates)
            {
                if (used.Contains(c))
                    continue;

                MaskChannel channel = MaskChannel.None;
                if (RoughnessNames.Contains(c.Norm) && !smoothness)
                {
                    channel = MaskChannel.Roughness;
                    smoothness = true;
                }
                else if (SmoothnessNames.Contains(c.Norm) && !smoothness)
                {
                    channel = MaskChannel.Smoothness;
                    smoothness = true;
                }
                else if (MetallicNames.Contains(c.Norm) && !metallic)
                {
                    channel = MaskChannel.Metallic;
                    metallic = true;
                }
                else if (OcclusionNames.Contains(c.Norm) && !occlusion)
                {
                    channel = MaskChannel.Occlusion;
                    occlusion = true;
                }

                if (channel == MaskChannel.None)
                    continue;

                used.Add(c);
                result.Textures.Add(MaskGuess(c.Parameter.name, channel, MaskChannel.None, MaskChannel.None, MaskChannel.None));
            }
        }

        static Candidate PickNormal(List<Candidate> candidates, HashSet<Candidate> used)
        {
            Candidate best = null;
            foreach (Candidate c in candidates)
            {
                if (used.Contains(c) || !NormalNames.Contains(c.Norm) || c.Norm.Contains("detail"))
                    continue;
                if (best == null || (!best.Texture.IsNormalMap && c.Texture.IsNormalMap))
                    best = c;
            }

            if (best != null)
                return best;

            // Fall back to a normal-map texture, but only when it is unambiguous.
            Candidate only = null;
            int count = 0;
            foreach (Candidate c in candidates)
            {
                if (used.Contains(c) || !c.Texture.IsNormalMap || c.Norm.Contains("detail"))
                    continue;
                only = c;
                count++;
            }

            return count == 1 ? only : null;
        }

        static TextureGuess MaskGuess(string parameter, MaskChannel r, MaskChannel g, MaskChannel b, MaskChannel a)
        {
            return new TextureGuess
            {
                Parameter = parameter, Usage = TextureUsage.Mask, Red = r, Green = g, Blue = b, Alpha = a,
            };
        }

        static bool IsEmissionName(string norm)
        {
            return EmissionNames.Contains(norm) || norm.EndsWith("emissive", StringComparison.Ordinal) ||
                   norm.EndsWith("emission", StringComparison.Ordinal);
        }

        // An emissive channel is only part of the layout when emission is guessed for the material.
        static bool TryGetPackedLayout(Candidate c, bool emission, out MaskChannel[] channels)
        {
            if (TryParenthesisedLayout(c.Parameter.name, emission, out channels))
                return true;

            foreach (string token in Tokens(c.Parameter.name))
            {
                if (TryPackedToken(token, emission, out channels))
                    return true;
            }

            // Only the last meaningful token of the texture name: T_Rock_ORM, T_Rock_ORM_01.
            List<string> textureTokens = Tokens(c.Texture.name);
            for (int i = textureTokens.Count - 1; i >= 0; i--)
            {
                if (IsAllDigits(textureTokens[i]))
                    continue;
                return TryPackedToken(textureTokens[i], emission, out channels);
            }

            channels = null;
            return false;
        }

        // Layout written into the parameter name, for example ORMA(AO,Rough,Mat,Emis).
        static bool TryParenthesisedLayout(string parameterName, bool emission, out MaskChannel[] channels)
        {
            channels = null;
            int open = parameterName.IndexOf('(');
            int close = open >= 0 ? parameterName.IndexOf(')', open + 1) : -1;
            if (open < 0 || close < 0)
                return false;

            string[] items = parameterName.Substring(open + 1, close - open - 1).Split(',');
            var found = new MaskChannel[4];
            int recognised = 0;
            for (int i = 0; i < items.Length && i < 4; i++)
            {
                found[i] = ClassifyItem(Flatten(items[i]));
                if (found[i] == MaskChannel.Emission)
                {
                    // Does not count as recognised: an emissive channel alone does not make a packed mask.
                    if (!emission)
                        found[i] = MaskChannel.None;
                }
                else if (found[i] != MaskChannel.None)
                    recognised++;
            }

            if (recognised < 2)
                return false;

            channels = found;
            return true;
        }

        static MaskChannel ClassifyItem(string item)
        {
            if (item.StartsWith("ao", StringComparison.Ordinal) || item.StartsWith("occ", StringComparison.Ordinal) ||
                item.StartsWith("ambient", StringComparison.Ordinal))
                return MaskChannel.Occlusion;
            if (item.StartsWith("rough", StringComparison.Ordinal))
                return MaskChannel.Roughness;
            if (item.StartsWith("met", StringComparison.Ordinal) || item.StartsWith("mat", StringComparison.Ordinal))
                return MaskChannel.Metallic;
            if (item.StartsWith("smooth", StringComparison.Ordinal) || item.StartsWith("gloss", StringComparison.Ordinal))
                return MaskChannel.Smoothness;
            if (item.StartsWith("emis", StringComparison.Ordinal) || item.StartsWith("glow", StringComparison.Ordinal))
                return MaskChannel.Emission;
            return MaskChannel.None;
        }

        // An uppercase token such as ORM, ORMA, ORME, ARM, RMA or MRAO. Only uppercase tokens count, so
        // words like Mesh or Master are not read as layouts.
        static bool TryPackedToken(string token, bool emission, out MaskChannel[] channels)
        {
            channels = null;
            if (token.Length < 2 || token.Length > 5)
                return false;

            foreach (char ch in token)
            {
                if (ch < 'A' || ch > 'Z')
                    return false;
            }

            if (Array.IndexOf(NotLayoutWords, token) >= 0)
                return false;

            string t = token.Replace("AO", "O");
            if (t.Length < 3 || t.Length > 4)
                return false;

            foreach (char ch in t)
            {
                if ("ORMASEHD".IndexOf(ch) < 0)
                    return false;
            }

            if ((t.IndexOf('R') < 0 && t.IndexOf('S') < 0) || t.IndexOf('M') < 0)
                return false;

            bool hasO = t.IndexOf('O') >= 0;
            var found = new MaskChannel[4];
            for (int i = 0; i < t.Length; i++)
            {
                switch (t[i])
                {
                    case 'O': found[i] = MaskChannel.Occlusion; break;
                    case 'R': found[i] = MaskChannel.Roughness; break;
                    case 'M': found[i] = MaskChannel.Metallic; break;
                    case 'S': found[i] = MaskChannel.Smoothness; break;
                    case 'A': found[i] = hasO ? MaskChannel.None : MaskChannel.Occlusion; break;
                    case 'E': found[i] = emission ? MaskChannel.Emission : MaskChannel.None; break;
                }
            }

            channels = found;
            return true;
        }

        static void GuessScalars(PortageManifest.MaterialEntry entry, ISet<SwitchRole> roles, Result result)
        {
            var taken = new HashSet<string>();
            foreach (PortageManifest.ScalarParameter parameter in entry.scalarParameters)
            {
                // A constant is what the material uses instead of a texture. It is only guessed when a switch
                // chooses between the two, so the importer knows which one is in effect.
                string norm = StripEnd(NormalizeName(parameter.name), ConstantWords, out bool constant);
                if (norm.Contains("constant"))
                    continue;

                string property = null;
                var conversion = ScalarConversion.None;
                if (MetallicScalarNames.Contains(norm))
                {
                    if (!constant || Has(roles, SwitchRole.MetallicTexture))
                        property = "_Metallic";
                }
                else if (RoughnessScalarNames.Contains(norm))
                {
                    if (!constant || Has(roles, SwitchRole.RoughnessTexture))
                    {
                        property = "_Smoothness";
                        conversion = ScalarConversion.OneMinus;
                    }
                }
                else if (constant)
                    continue;
                else if (BumpScaleNames.Contains(norm))
                    property = "_BumpScale";
                else if (Has(roles, SwitchRole.Emission) && IsEmissionIntensity(norm))
                    property = PortageMappingTable.EmissionIntensityProperty;

                if (property == null || !taken.Add(property))
                    continue;

                result.Scalars.Add(new ScalarGuess { Parameter = parameter.name, Property = property, Conversion = conversion });
            }
        }

        static bool IsEmissionIntensity(string norm)
        {
            foreach (string prefix in new[] { "emissive", "emission" })
            {
                if (norm.StartsWith(prefix, StringComparison.Ordinal) &&
                    Array.IndexOf(EmissionIntensityWords, norm.Substring(prefix.Length)) >= 0)
                    return true;
            }

            return false;
        }

        static void GuessVectors(PortageManifest.MaterialEntry entry, ISet<SwitchRole> roles, Result result)
        {
            bool tintTaken = false, constantTaken = false, emissionTaken = false;
            foreach (PortageManifest.VectorParameter parameter in entry.vectorParameters)
            {
                string norm = StripEnd(NormalizeName(parameter.name), ConstantWords, out bool constant);
                if (norm.Contains("constant"))
                    continue;

                if (EmissionNames.Contains(norm))
                {
                    if (emissionTaken || !Has(roles, SwitchRole.Emission))
                        continue;

                    emissionTaken = true;
                    result.Vectors.Add(new VectorGuess { Parameter = parameter.name, Property = "_EmissionColor" });
                }
                else if (constant)
                {
                    if (constantTaken || !Has(roles, SwitchRole.BaseColorTexture) || !BaseColorNames.Contains(norm))
                        continue;

                    constantTaken = true;
                    result.Vectors.Add(new VectorGuess { Parameter = parameter.name, Property = "_BaseColor", Constant = true });
                }
                else if (!tintTaken && BaseColorVectorNames.Contains(norm))
                {
                    tintTaken = true;
                    result.Vectors.Add(new VectorGuess { Parameter = parameter.name, Property = "_BaseColor" });
                }
            }
        }

        // Removes the first of the words the name starts with, unless nothing would be left.
        static string StripStart(string s, string[] words, out bool stripped)
        {
            foreach (string word in words)
            {
                if (s.Length > word.Length && s.StartsWith(word, StringComparison.Ordinal))
                {
                    stripped = true;
                    return s.Substring(word.Length);
                }
            }

            stripped = false;
            return s;
        }

        static string StripEnd(string s, string[] words, out bool stripped)
        {
            foreach (string word in words)
            {
                if (s.Length > word.Length && s.EndsWith(word, StringComparison.Ordinal))
                {
                    stripped = true;
                    return s.Substring(0, s.Length - word.Length);
                }
            }

            stripped = false;
            return s;
        }

        static string Flatten(string name)
        {
            var sb = new StringBuilder();
            foreach (char ch in name ?? "")
            {
                if (char.IsLetterOrDigit(ch))
                    sb.Append(char.ToLowerInvariant(ch));
            }

            return sb.ToString();
        }

        static List<string> Tokens(string name)
        {
            var tokens = new List<string>();
            var sb = new StringBuilder();
            foreach (char ch in name ?? "")
            {
                if (char.IsLetterOrDigit(ch))
                {
                    sb.Append(ch);
                    continue;
                }

                if (sb.Length > 0)
                    tokens.Add(sb.ToString());
                sb.Clear();
            }

            if (sb.Length > 0)
                tokens.Add(sb.ToString());
            return tokens;
        }

        static bool IsAllDigits(string s)
        {
            foreach (char ch in s)
            {
                if (!char.IsDigit(ch))
                    return false;
            }

            return s.Length > 0;
        }
    }
}
