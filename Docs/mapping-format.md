# Portage mapping file

**Format version: 1 — draft.**

`portage-mapping.json` stores the choices a user made in the Unity import window: which Unreal
material parameter feeds which URP Lit input. It lives in the export folder, next to
`manifest.json`.

- It is written by the Unity importer only. The Unreal exporter never writes, reads or deletes
  it, so exporting again into the same folder keeps the choices.
- It is optional. Without it the importer uses the assigned mapping table and its own guesses.
- It follows the conventions of the [manifest](manifest-format.md): UTF-8, arrays instead of maps,
  readable with Unity's `JsonUtility`, empty strings instead of `null`.

## Why it is keyed by base material

Parameter names belong to the master material, and every instance of that master shares them.
Choices are therefore stored once per `baseMaterial` id and apply to all materials in the
manifest that have this base material.

## Precedence

For each parameter of a material the importer uses the first of:

1. the entry in `portage-mapping.json` for the material's base material,
2. a rule in the mapping table assigned in the import window (or the built-in default table),
3. a guess from the parameter name, the texture name and the texture's settings.

Guesses never override an input that a higher-precedence source already assigned: if a table
rule maps a normal map, no second normal map is guessed.

The same precedence gives every static switch its role. Decisions depend on the names of the
parameters and switches only, so they are the same for every material of a base material.

## Switches

A role says what a static switch turns on. When a material is built, a switch that has a role
and is off in that material keeps the inputs it controls out, whichever source mapped them. The
switch values come from the manifest and are never stored here.

| Role | Controls |
| --- | --- |
| `None` | Nothing. A stored `None` also stops the importer from guessing a role. |
| `BaseColorTexture` | The `BaseColor` texture. Off: a vector with `constant` set is the base color instead. |
| `MetallicTexture` | `Metallic` mask channels. Off: the scalar mapped to `_Metallic` applies. |
| `RoughnessTexture` | `Roughness` and `Smoothness` mask channels. Off: the scalar mapped to `_Smoothness` applies. |
| `NormalTexture` | The `Normal` texture. |
| `Occlusion` | `Occlusion` mask channels. On does not apply occlusion without `applyOcclusion`. |
| `ColorMask` | The `ColorMask` texture. |
| `Emission` | Everything emissive: `Emission` textures and mask channels, `_EmissionColor` and `_EmissionIntensity`. |
| `EmissionTexture` | `Emission` textures and mask channels, but not the emission color. |
| `SeparateEmissionTexture` | On: the `Emission` texture is used and `Emission` mask channels are not. Off: the other way round. |

A role belongs to at most one switch of a base material. Some guesses are only made when a
switch has the matching role: a constant needs the switch that chooses between it and a texture,
and anything emissive needs a switch with the `Emission` role.

## Root object

| Field | Type | Description |
| --- | --- | --- |
| `version` | int | `1` |
| `materials` | array | One entry per base material. |
| `applyOcclusion` | bool | Assign the occlusion channel of mask textures to the generated materials. `false` when missing. Unreal mostly ignores material ambient occlusion under dynamic lighting, so applying it in URP can darken a model that looks fine in Unreal. |

### materials

| Field | Type | Description |
| --- | --- | --- |
| `baseMaterial` | string | `baseMaterial` id from the manifest. |
| `textures` | array | Choices for texture parameters. |
| `scalars` | array | Choices for scalar parameters. |
| `vectors` | array | Choices for vector parameters. |
| `switches` | array | Roles of static switch parameters. Missing in files written before switches were supported. |

A parameter that is not listed has no stored choice and falls through to the table and the
guess.

### textures

| Field | Type | Description |
| --- | --- | --- |
| `parameter` | string | Unreal parameter name, exactly as in the manifest. |
| `usage` | string | `BaseColor`, `Normal`, `Emission`, `Mask`, `Ignore` or `ColorMask`. `ColorMask` limits the base color tint to the white areas of the texture; only its red channel is read. |
| `red`, `green`, `blue`, `alpha` | string | Content of each channel when `usage` is `Mask`: `None`, `Metallic`, `Roughness`, `Smoothness`, `Occlusion` or `Emission`. An `Emission` channel is copied into a grayscale emission map. |

### scalars

| Field | Type | Description |
| --- | --- | --- |
| `parameter` | string | Unreal parameter name. |
| `property` | string | Float property of the target shader, for example `_BumpScale`. Empty means ignore. `_EmissionIntensity` is not a shader property: its value multiplies `_EmissionColor`. |
| `conversion` | string | `None` or `OneMinus`. |

### vectors

| Field | Type | Description |
| --- | --- | --- |
| `parameter` | string | Unreal parameter name. |
| `property` | string | Color property of the target shader, for example `_BaseColor`. Empty means ignore. |
| `constant` | bool | Only for `_BaseColor`: the value replaces the base color texture and is used when the material has none, for example a base color constant behind a switch. It then also replaces a tint mapped to `_BaseColor`; with a texture it is ignored. `false` when missing. |

### switches

| Field | Type | Description |
| --- | --- | --- |
| `parameter` | string | Unreal static switch parameter name. |
| `role` | string | One of the roles under [Switches](#switches). |

## Example

```json
{
  "version": 1,
  "applyOcclusion": false,
  "materials": [
    {
      "baseMaterial": "/Game/Materials/M_Prop.M_Prop",
      "textures": [
        { "parameter": "Base Color Texture", "usage": "BaseColor", "red": "None", "green": "None", "blue": "None", "alpha": "None" },
        { "parameter": "Packed", "usage": "Mask", "red": "Occlusion", "green": "Roughness", "blue": "Metallic", "alpha": "None" },
        { "parameter": "Dust", "usage": "Ignore", "red": "None", "green": "None", "blue": "None", "alpha": "None" }
      ],
      "scalars": [
        { "parameter": "Normal Strength", "property": "_BumpScale", "conversion": "None" }
      ],
      "vectors": [
        { "parameter": "Base Color Multiplier", "property": "_BaseColor", "constant": false },
        { "parameter": "Base Color Constant", "property": "_BaseColor", "constant": true }
      ],
      "switches": [
        { "parameter": "Base Color Texture Input", "role": "BaseColorTexture" },
        { "parameter": "Use Dust", "role": "None" }
      ]
    }
  ]
}
```
