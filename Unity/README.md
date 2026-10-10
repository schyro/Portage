# Portage — Unity importer

Imports a folder exported by the [Portage Unreal exporter](../Unreal/README.md) and builds
meshes, textures and URP Lit materials from it.

Tested with Unity 6000.3.12f1 and URP 17.3.0, on exports from Unreal Engine 5.8.3.

## Install

The project must use the Universal Render Pipeline. In the Package Manager choose
**Add package from git URL** and enter:

```
https://github.com/schyro/Portage.git?path=/Unity
```

## Import an export folder

1. Open **Tools > Portage > Import...**
2. Pick the export folder, the one that contains `manifest.json`.
3. Review **Parameter mapping** (see below).
4. Press **Import**.

The assets are written to `Meshes`, `Textures` and `Materials` under the target folder, which
defaults to `Assets/Portage/<export folder name>`. A report lists what was created, what was
guessed and what was skipped.

Importing the same folder again updates the existing assets in place.

## Parameter mapping

Every asset pack names its material parameters differently, so the importer has to know which
parameter is the base color, which is the normal map, and so on. The window lists the
parameters of each master material with a dropdown saying what each one is used for.

For each parameter the importer uses the first of these:

| | Source | Shown as |
| --- | --- | --- |
| 1 | Your choice in the dropdown | `saved` |
| 2 | A rule in the assigned mapping table | `table` |
| 3 | A guess from the parameter and texture names | `guessed` |

**Your choices** are saved to `portage-mapping.json` in the export folder as soon as you change
a dropdown. They apply to every material that shares the master material, are reused the next
time you import that folder, and survive exporting again from Unreal.
[File format](../Docs/mapping-format.md).

**Guesses** compare names without case, spaces or a trailing "Texture" / "Map", so
`Base Color Texture` is recognised as a base color. Channel-packed masks are recognised from a
list in the name, such as `ORMA(AO,Rough,Mat,Emis)`, or from an upper-case suffix such as
`_ORM`, `_ARM` or `_RMA`. A parameter named like `Color_Mask`, `Tint Mask` or `Paint Mask` is guessed as a color mask, even when it holds a placeholder texture such as `T_Gray`; a guessed placeholder is only used when it is one uniform color. Emission maps and engine placeholder textures are otherwise never guessed.

**Mapping tables** are optional. Create one with **Assets > Create > Portage > Mapping Table**
when you want to reuse the same rules across many export folders or share them with a team.

### Ambient occlusion

The occlusion channel of a mask is not used unless you tick **Apply ambient occlusion** in the
import window. Unreal mostly ignores material ambient occlusion under dynamic lighting, so asset
packs often ship occlusion channels with large black areas that are invisible there. URP applies
occlusion to all ambient light, which would turn those areas black in shadow.

### Texture usages

| Usage | Goes to |
| --- | --- |
| BaseColor | Base map |
| Normal | Normal map |
| Emission | Emission map |
| Mask | Repacked into the metallic / smoothness / occlusion texture. Set what each of R, G, B and A contains; roughness is inverted into smoothness. |
| Ignore | Not used |
| ColorMask | Limits the base color tint to the white areas of this texture. Only the red channel is read, so a mask with a separate zone per channel is not supported. The tint is baked into a copy of the base color texture, `<Material>_BaseColor.png`, where tint values above 1 are clamped; with a uniform mask it only scales `_BaseColor`. Needs a base color texture and a vector parameter mapped to `_BaseColor`: without a base color texture the tint covers the whole surface. |

## What you get

- **Meshes** with scale 1 and no rotation. A point at Unreal `(X, Y, Z)` centimetres ends up at
  Unity `(X, Z, -Y)` metres.
- **Materials** using `Universal Render Pipeline/Lit`, assigned to the right slots. Blend mode
  (opaque, masked, translucent) and the two-sided flag are carried over.
- **Textures** with the correct sRGB setting. Normal maps get their green channel flipped,
  because Unreal and Unity use opposite conventions.
- **No stray materials**: the materials embedded in the FBX are remapped to the generated ones
  instead of being extracted.

A material that Unreal reported as not convertible keeps the grey material embedded in the FBX
and is listed in the report.

## Limitations

- Anything a master material computes in its graph is lost: dirt and dust layers, detail
  normals, tiling, multipliers on roughness. The result is the plain PBR surface.
- Two materials with the same asset name in different Unreal folders overwrite each other.
- Packed masks can only be read from PNG or JPG files.
- Lower-case packed suffixes such as `_Arm` are not guessed; pick the usage by hand.

## Scripting

```csharp
PortageImportReport report = PortageImporter.Import(exportFolder, "Assets/Portage/MyProps", table);
```

`table` may be `null`. The mapping file in the export folder is picked up automatically.

## For contributors

### Tests

EditMode tests live in `Tests/Editor`. To run them, add `com.unity.test-framework` to a project
and list `com.schyro.portage` under `testables` in its `Packages/manifest.json`.

The tests use two self-made export folders:

- `Tests/Fixtures~/Block`: a real Unreal 5.8.3 export of an asymmetric box, produced by
  `Unreal/Tests/run_export_test.py`, with local file paths removed from the FBX. It covers
  units, orientation and slot matching.
- `Tests/Fixtures~/Cube`: generated with Blender by `Tests/Fixtures~/generate_cube.py`, with
  known pixel values for the texture conversion tests. It is not an Unreal export.

### How URP Lit reads its textures

Checked against the shader source of URP 17.3.0:

| Surface input | Property | Channel | Keyword |
| --- | --- | --- | --- |
| Base color | `_BaseMap`, `_BaseColor` | RGB (A = alpha) | — |
| Metallic | `_MetallicGlossMap`, `_Metallic` | R | `_METALLICSPECGLOSSMAP` |
| Smoothness | `_MetallicGlossMap`, `_Smoothness` | A | `_METALLICSPECGLOSSMAP` |
| Occlusion | `_OcclusionMap`, `_OcclusionStrength` | G | `_OCCLUSIONMAP` |
| Normal | `_BumpMap`, `_BumpScale` | tangent space | `_NORMALMAP` |
| Emission | `_EmissionMap`, `_EmissionColor` | RGB | `_EMISSION` |

What follows from this:

- Metallic, smoothness and occlusion use different channels, so one repacked texture
  (R = metallic, G = occlusion, A = smoothness) serves both `_MetallicGlossMap` and
  `_OcclusionMap`. The occlusion slot is only assigned when ambient occlusion is turned on.
- With a metallic map assigned, the map's alpha is multiplied by `_Smoothness` and `_Metallic`
  is ignored. Generated materials therefore set `_Smoothness` to 1.
- FBX materials are matched to manifest slots by material name, which is what Unreal writes
  into the FBX, then by slot name, then by index.
