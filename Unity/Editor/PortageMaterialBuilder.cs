using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEditor;
using UnityEditor.Rendering.Universal.ShaderGUI;
using UnityEngine;

namespace Portage
{
    /// <summary>Builds URP Lit materials (metallic workflow) from convertible manifest materials.</summary>
    public sealed class PortageMaterialBuilder
    {
        const string ShaderName = "Universal Render Pipeline/Lit";

        readonly PortageManifest _manifest;
        readonly PortageMappingTable _table;
        readonly PortageTextureProcessor _textures;
        readonly string _materialFolder;
        readonly PortageImportReport _report;

        public PortageMaterialBuilder(PortageManifest manifest, PortageMappingTable table,
            PortageTextureProcessor textures, string materialFolder, PortageImportReport report)
        {
            _manifest = manifest;
            _table = table;
            _textures = textures;
            _materialFolder = materialFolder;
            _report = report;
        }

        /// <summary>
        /// Builds (or updates in place) the material for a manifest entry. Returns null when the
        /// material is not convertible or cannot be built; the reason is in the report.
        /// </summary>
        public Material Build(PortageManifest.MaterialEntry entry)
        {
            if (!entry.convertible)
            {
                if (!_manifest.issues.Exists(i => i.asset == entry.id))
                    _report.Warning("material.notConvertible", entry.id, $"{entry.name} is not convertible and was skipped.");
                return null;
            }

            Shader shader = Shader.Find(ShaderName);
            if (shader == null)
            {
                _report.Error("shader.missing", entry.id, $"Shader '{ShaderName}' was not found. Is URP installed?");
                return null;
            }

            string assetPath = _materialFolder + "/" + PortageAssetUtil.SanitizeFileName(entry.name) + ".mat";
            var existing = AssetDatabase.LoadAssetAtPath<Material>(assetPath);
            var fresh = new Material(shader);
            Material material = existing != null ? existing : fresh;

            try
            {
                if (existing != null)
                {
                    // Reset to the shader defaults so values from an earlier import do not linger.
                    existing.shader = shader;
                    existing.CopyPropertiesFromMaterial(fresh);
                    existing.shaderKeywords = fresh.shaderKeywords;
                    existing.renderQueue = fresh.renderQueue;
                    existing.globalIlluminationFlags = fresh.globalIlluminationFlags;
                }

                Populate(entry, material);
                ApplyKeywords(material);

                if (existing != null)
                {
                    EditorUtility.SetDirty(existing);
                    AssetDatabase.SaveAssetIfDirty(existing);
                }
                else
                {
                    AssetDatabase.CreateAsset(fresh, assetPath);
                    fresh = null; // now owned by the asset database
                }
            }
            finally
            {
                if (fresh != null)
                    UnityEngine.Object.DestroyImmediate(fresh);
            }

            _report.CreatedMaterials.Add(assetPath);
            return material;
        }

        void Populate(PortageManifest.MaterialEntry entry, Material material)
        {
            var recipe = new PortageTextureProcessor.MaskRecipe();
            var maskNames = new List<string>();
            Texture2D baseMap = null, normalMap = null, emissionMap = null;

            foreach (PortageManifest.TextureParameter parameter in entry.textureParameters)
            {
                PortageMappingTable.TextureRule rule = _table.FindTextureRule(parameter.name);
                if (rule == null)
                {
                    ReportUnmapped(entry, "texture", parameter.name);
                    continue;
                }

                if (string.IsNullOrEmpty(parameter.texture))
                {
                    _report.Warning("texture.unset", entry.id,
                        $"{entry.name}: texture parameter '{parameter.name}' has no texture.");
                    continue;
                }

                PortageManifest.TextureEntry texture = _manifest.FindTexture(parameter.texture);
                if (texture == null)
                {
                    _report.Warning("texture.notInManifest", entry.id,
                        $"{entry.name}: texture parameter '{parameter.name}' references '{parameter.texture}', which is not in the manifest.");
                    continue;
                }

                switch (rule.usage)
                {
                    case TextureUsage.BaseColor:
                        baseMap = ImportAndLoad(texture) ?? baseMap;
                        break;
                    case TextureUsage.Normal:
                        normalMap = ImportAndLoad(texture) ?? normalMap;
                        break;
                    case TextureUsage.Emission:
                        emissionMap = ImportAndLoad(texture) ?? emissionMap;
                        break;
                    case TextureUsage.Mask:
                        string path = _textures.ResolveExportPath(texture.file);
                        if (!maskNames.Contains(texture.name))
                            maskNames.Add(texture.name);
                        AddMaskChannel(recipe, rule.red, PortageTextureProcessor.SourceChannel.R, path);
                        AddMaskChannel(recipe, rule.green, PortageTextureProcessor.SourceChannel.G, path);
                        AddMaskChannel(recipe, rule.blue, PortageTextureProcessor.SourceChannel.B, path);
                        AddMaskChannel(recipe, rule.alpha, PortageTextureProcessor.SourceChannel.A, path);
                        break;
                }
            }

            // Scalars are collected first because the mask packing depends on them.
            var scalars = new Dictionary<string, float>();
            foreach (PortageManifest.ScalarParameter parameter in entry.scalarParameters)
            {
                PortageMappingTable.ScalarRule rule = _table.FindScalarRule(parameter.name);
                if (rule == null)
                {
                    ReportUnmapped(entry, "scalar", parameter.name);
                    continue;
                }

                float value = rule.conversion == ScalarConversion.OneMinus ? 1f - parameter.value : parameter.value;
                scalars[rule.property] = value;
            }

            SetTexture(entry, material, "_BaseMap", baseMap);
            SetTexture(entry, material, "_BumpMap", normalMap);

            // Packed mask: R = metallic, G = occlusion, A = smoothness.
            if (!recipe.IsEmpty)
            {
                float metallicScalar = scalars.TryGetValue("_Metallic", out float m)
                    ? m
                    : (material.HasProperty("_Metallic") ? material.GetFloat("_Metallic") : 0f);
                byte metallicFill = (byte)Mathf.RoundToInt(Mathf.Clamp01(metallicScalar) * 255f);

                string maskPath = _textures.BuildMask(recipe, string.Join("_", maskNames), metallicFill);
                var mask = maskPath != null ? AssetDatabase.LoadAssetAtPath<Texture2D>(maskPath) : null;
                if (mask != null)
                {
                    SetTexture(entry, material, "_MetallicGlossMap", mask);
                    if (recipe.Occlusion != null)
                        SetTexture(entry, material, "_OcclusionMap", mask);

                    // The shader multiplies the map alpha by _Smoothness, so it must be 1 when the
                    // alpha holds the smoothness. Without a smoothness source the alpha is 255 and
                    // _Smoothness keeps carrying the scalar.
                    if (recipe.Smoothness != null)
                    {
                        if (scalars.ContainsKey("_Smoothness"))
                        {
                            _report.Info("smoothness.scalarIgnored", entry.id,
                                $"{entry.name}: a smoothness texture is used, so the smoothness scalar parameter is ignored.");
                        }

                        scalars["_Smoothness"] = 1f;
                    }
                }
            }

            foreach (KeyValuePair<string, float> scalar in scalars)
                SetFloat(entry, material, scalar.Key, scalar.Value);

            if (emissionMap != null)
            {
                SetTexture(entry, material, "_EmissionMap", emissionMap);
                SetColor(entry, material, "_EmissionColor", Color.white);
            }

            foreach (PortageManifest.VectorParameter parameter in entry.vectorParameters)
            {
                PortageMappingTable.VectorRule rule = _table.FindVectorRule(parameter.name);
                if (rule == null)
                {
                    ReportUnmapped(entry, "vector", parameter.name);
                    continue;
                }

                SetColor(entry, material, rule.property, ToPropertySpace(material, rule.property, parameter.ToColor()));
            }

            if (material.HasProperty("_EmissionColor") && material.GetColor("_EmissionColor").maxColorComponent > 0f)
                material.globalIlluminationFlags = MaterialGlobalIlluminationFlags.BakedEmissive;

            ApplyBlendMode(entry, material);

            if (entry.twoSided)
                SetFloat(entry, material, "_Cull", 0f);

            if (!string.Equals(entry.shadingModel, "defaultLit", StringComparison.OrdinalIgnoreCase))
            {
                _report.Warning("material.shadingModel", entry.id,
                    $"{entry.name}: shading model '{entry.shadingModel}' has no URP Lit equivalent; converted as a default lit material.");
            }
        }

        void ApplyBlendMode(PortageManifest.MaterialEntry entry, Material material)
        {
            switch ((entry.blendMode ?? "").ToLowerInvariant())
            {
                case "masked":
                    SetFloat(entry, material, "_AlphaClip", 1f);
                    SetFloat(entry, material, "_Cutoff", entry.opacityMaskClipValue);
                    break;
                case "translucent":
                    SetTransparent(entry, material, BaseShaderGUI.BlendMode.Alpha);
                    break;
                case "additive":
                    SetTransparent(entry, material, BaseShaderGUI.BlendMode.Additive);
                    break;
                case "modulate":
                    SetTransparent(entry, material, BaseShaderGUI.BlendMode.Multiply);
                    break;
                case "opaque":
                case "":
                    break;
                default:
                    _report.Warning("material.blendMode", entry.id,
                        $"{entry.name}: unknown blend mode '{entry.blendMode}'; treated as opaque.");
                    break;
            }
        }

        void SetTransparent(PortageManifest.MaterialEntry entry, Material material, BaseShaderGUI.BlendMode blend)
        {
            SetFloat(entry, material, "_Surface", (float)BaseShaderGUI.SurfaceType.Transparent);
            SetFloat(entry, material, "_Blend", (float)blend);
        }

        static void AddMaskChannel(PortageTextureProcessor.MaskRecipe recipe, MaskChannel content,
            PortageTextureProcessor.SourceChannel channel, string path)
        {
            var source = new PortageTextureProcessor.MaskSource { Path = path, Channel = channel };
            switch (content)
            {
                case MaskChannel.Metallic:
                    recipe.Metallic = recipe.Metallic ?? source;
                    break;
                case MaskChannel.Roughness:
                    source.Invert = true;
                    recipe.Smoothness = recipe.Smoothness ?? source;
                    break;
                case MaskChannel.Smoothness:
                    recipe.Smoothness = recipe.Smoothness ?? source;
                    break;
                case MaskChannel.Occlusion:
                    recipe.Occlusion = recipe.Occlusion ?? source;
                    break;
            }
        }

        Texture2D ImportAndLoad(PortageManifest.TextureEntry texture)
        {
            string path = _textures.ImportTexture(texture);
            return path != null ? AssetDatabase.LoadAssetAtPath<Texture2D>(path) : null;
        }

        void ReportUnmapped(PortageManifest.MaterialEntry entry, string kind, string parameterName)
        {
            _report.Info("parameter.unmapped", entry.id,
                $"Material '{entry.name}': {kind} parameter '{parameterName}' has no rule in the mapping table and was ignored.");
        }

        void SetTexture(PortageManifest.MaterialEntry entry, Material material, string property, Texture texture)
        {
            if (texture == null)
                return;
            if (!material.HasProperty(property))
            {
                ReportMissingProperty(entry, property);
                return;
            }

            material.SetTexture(property, texture);
        }

        void SetFloat(PortageManifest.MaterialEntry entry, Material material, string property, float value)
        {
            if (!material.HasProperty(property))
            {
                ReportMissingProperty(entry, property);
                return;
            }

            material.SetFloat(property, value);
        }

        void SetColor(PortageManifest.MaterialEntry entry, Material material, string property, Color value)
        {
            if (!material.HasProperty(property))
            {
                ReportMissingProperty(entry, property);
                return;
            }

            material.SetColor(property, value);
        }

        // Manifest colors are linear. Unity stores non-HDR color properties in sRGB and converts
        // them when rendering in linear color space; HDR properties are stored linear.
        static Color ToPropertySpace(Material material, string property, Color linear)
        {
            int index = material.shader.FindPropertyIndex(property);
            bool hdr = index >= 0 &&
                       (material.shader.GetPropertyFlags(index) & UnityEngine.Rendering.ShaderPropertyFlags.HDR) != 0;
            return hdr ? linear : linear.gamma;
        }

        void ReportMissingProperty(PortageManifest.MaterialEntry entry, string property)
        {
            _report.Warning("property.missing", entry.id,
                $"Material '{entry.name}': the shader has no property '{property}'; the value was skipped.");
        }

        // LitShader.ValidateMaterial calls exactly this. LitDetailGUI is internal to the URP editor
        // assembly, so its keyword function is resolved through reflection.
        static void ApplyKeywords(Material material)
        {
            BaseShaderGUI.SetMaterialKeywords(material, LitGUI.SetMaterialKeywords, DetailKeywordFunction());
        }

        static Action<Material> DetailKeywordFunction()
        {
            Type type = Type.GetType(
                "UnityEditor.Rendering.Universal.ShaderGUI.LitDetailGUI, Unity.RenderPipelines.Universal.Editor", false);
            MethodInfo method = type?.GetMethod("SetMaterialKeywords",
                BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(Material) }, null);
            return method != null ? (Action<Material>)Delegate.CreateDelegate(typeof(Action<Material>), method) : null;
        }
    }
}
