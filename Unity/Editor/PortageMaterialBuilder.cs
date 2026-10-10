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
        readonly PortageMappingFile _mapping;
        readonly PortageTextureProcessor _textures;
        readonly string _materialFolder;
        readonly PortageImportReport _report;

        public PortageMaterialBuilder(PortageManifest manifest, PortageMappingTable table,
            PortageTextureProcessor textures, string materialFolder, PortageImportReport report,
            PortageMappingFile mapping = null)
        {
            _manifest = manifest;
            _table = table;
            _mapping = mapping;
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
            PortageMappingResolution resolution =
                PortageMappingResolver.Resolve(entry, _manifest, _table, _mapping);

            var recipe = new PortageTextureProcessor.MaskRecipe();
            var maskNames = new List<string>();
            Texture2D normalMap = null, emissionMap = null;
            // Base color and color mask are imported after the loop: the base texture is not imported
            // at all when a tinted copy replaces it.
            PortageManifest.TextureEntry baseTexture = null, colorMaskTexture = null;
            string colorMaskParameter = null;
            bool colorMaskGuessed = false;
            bool anyResolved = false;

            for (int i = 0; i < entry.textureParameters.Count; i++)
            {
                PortageManifest.TextureParameter parameter = entry.textureParameters[i];
                TextureDecision decision = resolution.Textures[i];
                if (!decision.IsMapped)
                {
                    ReportUnmapped(entry, "texture", parameter.name);
                    continue;
                }

                anyResolved = true;
                if (decision.Usage == TextureUsage.Ignore)
                    continue;

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

                if (decision.Source == MappingSource.Guess)
                {
                    string what = decision.Usage == TextureUsage.Mask
                        ? $"Mask (R={decision.Red}, G={decision.Green}, B={decision.Blue}, A={decision.Alpha})"
                        : decision.Usage.ToString();
                    ReportGuessed(entry, "texture", parameter.name, what);
                }

                switch (decision.Usage)
                {
                    case TextureUsage.BaseColor:
                        if (baseTexture != null)
                        {
                            ReportDuplicate(entry, parameter.name, decision.Usage);
                            break;
                        }

                        // A missing file does not take the slot, so a later parameter can still fill it;
                        // importing it only reports the problem.
                        if (System.IO.File.Exists(_textures.ResolveExportPath(texture.file)))
                            baseTexture = texture;
                        else
                            ImportAndLoad(texture);
                        break;
                    case TextureUsage.ColorMask:
                        if (colorMaskTexture != null)
                        {
                            ReportDuplicate(entry, parameter.name, decision.Usage);
                            break;
                        }

                        colorMaskTexture = texture;
                        colorMaskParameter = parameter.name;
                        colorMaskGuessed = decision.Source == MappingSource.Guess;
                        break;
                    case TextureUsage.Normal:
                        if (normalMap != null)
                        {
                            ReportDuplicate(entry, parameter.name, decision.Usage);
                            break;
                        }

                        normalMap = ImportAndLoad(texture);
                        break;
                    case TextureUsage.Emission:
                        if (emissionMap != null)
                        {
                            ReportDuplicate(entry, parameter.name, decision.Usage);
                            break;
                        }

                        emissionMap = ImportAndLoad(texture);
                        break;
                    case TextureUsage.Mask:
                        string path = _textures.ResolveExportPath(texture.file);
                        if (!maskNames.Contains(texture.name))
                            maskNames.Add(texture.name);
                        AddMaskChannel(recipe, decision.Red, PortageTextureProcessor.SourceChannel.R, path);
                        AddMaskChannel(recipe, decision.Green, PortageTextureProcessor.SourceChannel.G, path);
                        AddMaskChannel(recipe, decision.Blue, PortageTextureProcessor.SourceChannel.B, path);
                        AddMaskChannel(recipe, decision.Alpha, PortageTextureProcessor.SourceChannel.A, path);
                        break;
                }
            }

            if (entry.textureParameters.Count > 0 && !anyResolved)
            {
                _report.Warning("material.noTextureMatched", entry.id,
                    $"{entry.name}: none of its {entry.textureParameters.Count} texture parameters could be mapped " +
                    "(no saved choice, mapping table rule or guess), so the material has no textures. Review the " +
                    "parameter mapping in the import window or assign a mapping table with rules for this asset pack.");
            }

            // The vector parameter that ends up in _BaseColor; like the vector loop below, the last one wins.
            int tintIndex = -1;
            for (int i = 0; i < entry.vectorParameters.Count; i++)
            {
                VectorDecision decision = resolution.Vectors[i];
                if (decision.IsMapped && decision.Property == "_BaseColor")
                    tintIndex = i;
            }

            Texture2D baseMap = null;
            Color? baseColorOverride = null;
            if (colorMaskTexture != null && tintIndex < 0)
            {
                _report.Info("colorMask.unused", entry.id,
                    $"{entry.name}: color mask '{colorMaskParameter}' has no effect because the material has no " +
                    "base color tint (a vector parameter mapped to _BaseColor) to limit.");
            }
            else if (colorMaskTexture != null && baseTexture == null)
            {
                _report.Warning("colorMask.unused", entry.id,
                    $"{entry.name}: color mask '{colorMaskParameter}' was not applied because the material has no " +
                    "base color texture to bake it into, so the base color tint covers the whole surface.");
            }
            else if (colorMaskTexture != null)
            {
                Color tint = entry.vectorParameters[tintIndex].ToColor();
                // A white tint changes nothing, with or without a mask, so the mask is not even read.
                bool whiteTint = Mathf.Approximately(tint.r, 1f) && Mathf.Approximately(tint.g, 1f) &&
                                 Mathf.Approximately(tint.b, 1f);
                PortageTextureProcessor.ColorMaskImage mask =
                    whiteTint ? null : _textures.LoadColorMask(colorMaskTexture, entry.name);
                if (mask != null && mask.IsUniform)
                {
                    // A uniform mask needs no baking: the tint is scaled by the mask value instead. The alpha
                    // is not masked, on any path.
                    Color scaled = Color.Lerp(Color.white, tint, mask.UniformValue);
                    scaled.a = tint.a;
                    baseColorOverride = scaled;
                }
                else if (mask != null && colorMaskGuessed && PortageMappingGuesser.IsPlaceholder(colorMaskTexture))
                {
                    // The guess accepts a placeholder because a uniform one scales the tint. One with a pattern
                    // is an unused default rather than a mask.
                    _report.Info("colorMask.placeholder", entry.id,
                        $"{entry.name}: color mask '{colorMaskParameter}' was guessed, but its texture " +
                        $"'{colorMaskTexture.name}' is a placeholder that is not one uniform color, so it was not " +
                        "used. Set the parameter to ColorMask in the import window to use it anyway.");
                }
                else if (mask != null)
                {
                    string bakedPath = _textures.BakeTintedBaseColor(entry.name, baseTexture, colorMaskTexture, mask, tint);
                    baseMap = bakedPath != null ? AssetDatabase.LoadAssetAtPath<Texture2D>(bakedPath) : null;
                    if (baseMap != null)
                    {
                        baseColorOverride = new Color(1f, 1f, 1f, tint.a);
                        _report.Info("colorMask.baked", entry.id,
                            $"{entry.name}: the base color tint was limited to the white areas of color mask " +
                            $"'{colorMaskParameter}' and baked into {bakedPath}.");
                    }
                }
            }

            if (baseMap == null && baseTexture != null)
                baseMap = ImportAndLoad(baseTexture);

            // Scalars are collected first because the mask packing depends on them.
            var scalars = new Dictionary<string, float>();
            for (int i = 0; i < entry.scalarParameters.Count; i++)
            {
                PortageManifest.ScalarParameter parameter = entry.scalarParameters[i];
                ScalarDecision decision = resolution.Scalars[i];
                if (!decision.IsMapped)
                {
                    ReportUnmapped(entry, "scalar", parameter.name);
                    continue;
                }

                if (decision.IsIgnored)
                    continue;

                if (decision.Source == MappingSource.Guess)
                {
                    ReportGuessed(entry, "scalar", parameter.name,
                        decision.Conversion == ScalarConversion.OneMinus
                            ? $"{decision.Property} (1 - value)"
                            : decision.Property);
                }

                float value = decision.Conversion == ScalarConversion.OneMinus ? 1f - parameter.value : parameter.value;
                scalars[decision.Property] = value;
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

                bool applyOcclusion = _mapping != null && _mapping.applyOcclusion;
                string maskPath = _textures.BuildMask(recipe, string.Join("_", maskNames), metallicFill, applyOcclusion);
                var mask = maskPath != null ? AssetDatabase.LoadAssetAtPath<Texture2D>(maskPath) : null;
                if (mask != null)
                {
                    SetTexture(entry, material, "_MetallicGlossMap", mask);
                    // The occlusion channel is always packed, but only assigned when asked for.
                    if (recipe.Occlusion != null && applyOcclusion)
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

            for (int i = 0; i < entry.vectorParameters.Count; i++)
            {
                PortageManifest.VectorParameter parameter = entry.vectorParameters[i];
                VectorDecision decision = resolution.Vectors[i];
                if (!decision.IsMapped)
                {
                    ReportUnmapped(entry, "vector", parameter.name);
                    continue;
                }

                if (decision.IsIgnored)
                    continue;

                if (decision.Source == MappingSource.Guess)
                    ReportGuessed(entry, "vector", parameter.name, decision.Property);

                Color value = i == tintIndex && baseColorOverride.HasValue ? baseColorOverride.Value : parameter.ToColor();
                SetColor(entry, material, decision.Property, ToPropertySpace(material, decision.Property, value));
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
                $"Material '{entry.name}': {kind} parameter '{parameterName}' has no saved choice, table rule or guess and was ignored.");
        }

        void ReportGuessed(PortageManifest.MaterialEntry entry, string kind, string parameterName, string what)
        {
            _report.Info("parameter.guessed", entry.id,
                $"Material '{entry.name}': {kind} parameter '{parameterName}' was guessed as {what}. Review it in the import window.");
        }

        void ReportDuplicate(PortageManifest.MaterialEntry entry, string parameterName, TextureUsage usage)
        {
            _report.Info("parameter.duplicateUsage", entry.id,
                $"Material '{entry.name}': texture parameter '{parameterName}' also maps to {usage}, which an earlier parameter already uses; it was ignored.");
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
