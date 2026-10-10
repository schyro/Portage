using System;
using System.Collections.Generic;
using UnityEngine;

namespace Portage
{
    /// <summary>What a texture parameter is used for in the generated material.</summary>
    public enum TextureUsage
    {
        /// <summary>Assigned to <c>_BaseMap</c>.</summary>
        BaseColor,

        /// <summary>Assigned to <c>_BumpMap</c>.</summary>
        Normal,

        /// <summary>Assigned to <c>_EmissionMap</c>.</summary>
        Emission,

        /// <summary>
        /// A data texture whose channels hold metallic, roughness, smoothness and/or occlusion.
        /// Its channels are repacked into the layout URP Lit expects.
        /// </summary>
        Mask,

        /// <summary>The parameter is deliberately unused. Assets serialize the ints, so never reorder members.</summary>
        Ignore,

        /// <summary>
        /// Limits the base color tint to the white areas of this texture (its red channel). URP Lit has no
        /// tint mask, so the tint is baked into a copy of the base color texture. Always the last member.
        /// </summary>
        ColorMask,
    }

    /// <summary>What a single channel of a <see cref="TextureUsage.Mask"/> texture contains.</summary>
    public enum MaskChannel
    {
        None,
        Metallic,
        Roughness,
        Smoothness,
        Occlusion,

        /// <summary>
        /// An emissive mask. URP Lit reads emission from its own texture, so the channel is copied into a
        /// grayscale emission map. Assets serialize the ints, so new members go last.
        /// </summary>
        Emission,
    }

    /// <summary>
    /// What a static switch parameter turns on in the Unreal material. A switch with a role that is off in a
    /// material keeps the inputs it controls out of the generated material.
    /// </summary>
    public enum SwitchRole
    {
        /// <summary>The switch has no effect on the conversion. Assets serialize the ints, so never reorder members.</summary>
        None,

        /// <summary>On: base color comes from the texture. Off: from the base color constant.</summary>
        BaseColorTexture,

        /// <summary>On: metallic comes from a texture channel. Off: from the metallic scalar.</summary>
        MetallicTexture,

        /// <summary>On: roughness or smoothness comes from a texture channel. Off: from the scalar.</summary>
        RoughnessTexture,

        /// <summary>On: the normal map is used.</summary>
        NormalTexture,

        /// <summary>On: the occlusion channel is used. It is still only applied when ambient occlusion is enabled for the import.</summary>
        Occlusion,

        /// <summary>On: the color mask limits the base color tint.</summary>
        ColorMask,

        /// <summary>On: the material is emissive. Emission is only guessed for materials that have a switch with this role.</summary>
        Emission,

        /// <summary>On: emission is multiplied by a texture. Off: the emission color alone is used.</summary>
        EmissionTexture,

        /// <summary>On: the emission texture is its own texture parameter. Off: it is a channel of a mask texture.</summary>
        SeparateEmissionTexture,
    }

    /// <summary>How a scalar parameter value is converted before it is written to the material.</summary>
    public enum ScalarConversion
    {
        None,

        /// <summary>Writes <c>1 - value</c>, for example roughness to smoothness.</summary>
        OneMinus,
    }

    /// <summary>
    /// Editable rules that map Unreal material parameter names to URP Lit inputs. Parameter names
    /// and channel layouts differ per asset pack, so create one table per convention.
    /// </summary>
    [CreateAssetMenu(fileName = "PortageMappingTable", menuName = "Portage/Mapping Table")]
    public sealed class PortageMappingTable : ScriptableObject
    {
        /// <summary>
        /// Not a shader property: a scalar mapped to this name multiplies <c>_EmissionColor</c>, because URP Lit
        /// has no separate emission intensity.
        /// </summary>
        public const string EmissionIntensityProperty = "_EmissionIntensity";

        [Serializable]
        public sealed class TextureRule
        {
            [Tooltip("Unreal texture parameter names this rule applies to. Case-insensitive.")]
            public List<string> parameterNames = new List<string>();

            public TextureUsage usage;

            [Tooltip("Content of the red channel. Only used when Usage is Mask.")]
            public MaskChannel red;

            [Tooltip("Content of the green channel. Only used when Usage is Mask.")]
            public MaskChannel green;

            [Tooltip("Content of the blue channel. Only used when Usage is Mask.")]
            public MaskChannel blue;

            [Tooltip("Content of the alpha channel. Only used when Usage is Mask.")]
            public MaskChannel alpha;
        }

        [Serializable]
        public sealed class ScalarRule
        {
            [Tooltip("Unreal scalar parameter names this rule applies to. Case-insensitive.")]
            public List<string> parameterNames = new List<string>();

            [Tooltip("Float property of the target shader, for example _Metallic.")]
            public string property = "";

            public ScalarConversion conversion;
        }

        [Serializable]
        public sealed class VectorRule
        {
            [Tooltip("Unreal vector parameter names this rule applies to. Case-insensitive.")]
            public List<string> parameterNames = new List<string>();

            [Tooltip("Color property of the target shader, for example _BaseColor.")]
            public string property = "";

            [Tooltip("The value replaces the texture of this input and is only used when the material has none, for example a base color constant.")]
            public bool constant;
        }

        [Serializable]
        public sealed class SwitchRule
        {
            [Tooltip("Unreal static switch parameter names this rule applies to. Case-insensitive.")]
            public List<string> parameterNames = new List<string>();

            [Tooltip("What the switch turns on in the Unreal material.")]
            public SwitchRole role;
        }

        [Tooltip("Flip the green channel of normal maps. Unreal uses the DirectX convention, Unity the OpenGL one, so this is normally on.")]
        public bool flipNormalGreen = true;

        public List<TextureRule> textureRules = new List<TextureRule>();
        public List<ScalarRule> scalarRules = new List<ScalarRule>();
        public List<VectorRule> vectorRules = new List<VectorRule>();
        public List<SwitchRule> switchRules = new List<SwitchRule>();

        /// <summary>Creates an in-memory table holding the default rules.</summary>
        public static PortageMappingTable CreateDefault()
        {
            var table = CreateInstance<PortageMappingTable>();
            table.ResetToDefaults();
            return table;
        }

        /// <summary>Returns the rule for a texture parameter name, or null.</summary>
        public TextureRule FindTextureRule(string parameterName)
        {
            return textureRules.Find(r => Contains(r.parameterNames, parameterName));
        }

        /// <summary>Returns the rule for a scalar parameter name, or null.</summary>
        public ScalarRule FindScalarRule(string parameterName)
        {
            return scalarRules.Find(r => Contains(r.parameterNames, parameterName));
        }

        /// <summary>Returns the rule for a vector parameter name, or null.</summary>
        public VectorRule FindVectorRule(string parameterName)
        {
            return vectorRules.Find(r => Contains(r.parameterNames, parameterName));
        }

        /// <summary>Returns the rule for a static switch parameter name, or null.</summary>
        public SwitchRule FindSwitchRule(string parameterName)
        {
            return switchRules.Find(r => Contains(r.parameterNames, parameterName));
        }

        /// <summary>Replaces all rules with the defaults for the common Unreal conventions.</summary>
        [ContextMenu("Reset To Defaults")]
        public void ResetToDefaults()
        {
            flipNormalGreen = true;

            textureRules = new List<TextureRule>
            {
                Texture(TextureUsage.BaseColor, "BaseColor", "Base Color", "Albedo", "Diffuse", "Color"),
                Texture(TextureUsage.Normal, "Normal", "NormalMap", "Normal Map"),
                Texture(TextureUsage.Emission, "Emissive", "Emission", "EmissiveColor"),
                Mask(new[] { "ORM", "OcclusionRoughnessMetallic", "ARM" },
                    MaskChannel.Occlusion, MaskChannel.Roughness, MaskChannel.Metallic),
                Mask(new[] { "Roughness", "RoughnessMap" }, MaskChannel.Roughness),
                Mask(new[] { "Metallic", "Metalness", "MetallicMap" }, MaskChannel.Metallic),
                Mask(new[] { "AO", "AmbientOcclusion", "Occlusion" }, MaskChannel.Occlusion),
            };

            scalarRules = new List<ScalarRule>
            {
                Scalar("_Metallic", ScalarConversion.None, "Metallic", "Metalness"),
                Scalar("_Smoothness", ScalarConversion.OneMinus, "Roughness"),
                Scalar("_BumpScale", ScalarConversion.None, "NormalStrength", "NormalIntensity", "Normal Strength"),
            };

            vectorRules = new List<VectorRule>
            {
                Vector("_BaseColor", "BaseColor", "Base Color", "Tint", "Color", "BaseColorTint"),
                Vector("_EmissionColor", "EmissiveColor", "Emissive", "Emission"),
            };

            // Switch names differ too much between packs for default rules; their roles are guessed.
            switchRules = new List<SwitchRule>();
        }

        void Reset()
        {
            ResetToDefaults();
        }

        static bool Contains(List<string> names, string name)
        {
            foreach (string candidate in names)
            {
                if (string.Equals(candidate, name, StringComparison.OrdinalIgnoreCase))
                    return true;
            }

            return false;
        }

        static TextureRule Texture(TextureUsage usage, params string[] names)
        {
            return new TextureRule { parameterNames = new List<string>(names), usage = usage };
        }

        static TextureRule Mask(string[] names, MaskChannel red, MaskChannel green = MaskChannel.None,
            MaskChannel blue = MaskChannel.None, MaskChannel alpha = MaskChannel.None)
        {
            return new TextureRule
            {
                parameterNames = new List<string>(names),
                usage = TextureUsage.Mask,
                red = red,
                green = green,
                blue = blue,
                alpha = alpha,
            };
        }

        static ScalarRule Scalar(string property, ScalarConversion conversion, params string[] names)
        {
            return new ScalarRule
            {
                parameterNames = new List<string>(names),
                property = property,
                conversion = conversion,
            };
        }

        static VectorRule Vector(string property, params string[] names)
        {
            return new VectorRule { parameterNames = new List<string>(names), property = property };
        }
    }
}
