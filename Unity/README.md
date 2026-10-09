# Portage — Unity importer

UPM package `com.schyro.portage`. Imports a Portage export folder into a Unity project and
generates URP Lit materials from its [manifest](../Docs/manifest-format.md).

Tested with Unity 6000.3.12f1 and URP 17.3.0, against a self-made export only. It has not been
run on a real Unreal export yet.

## Usage

1. Open **Tools > Portage > Import...**
2. Pick the export folder (the one containing `manifest.json`).
3. Optionally change the target folder and assign a mapping table.
4. Press **Import**. Textures, materials and meshes are written to `Textures`, `Materials` and
   `Meshes` under the target folder, and a report lists everything that was skipped or guessed.

Importing the same folder again updates the existing assets in place.

From code: `PortageImporter.Import(exportFolder, targetFolder, table)`.

## Mapping table

Parameter names and channel layouts differ per asset pack, so they are data, not code. Create a
table with **Assets > Create > Portage > Mapping Table** and edit its rules:

- **Texture rules** map a texture parameter name to a usage: base color, normal, emission, or
  *mask*. For a mask you state what each channel holds (metallic, roughness, smoothness,
  occlusion); the importer repacks those channels into the single texture URP Lit reads.
- **Scalar rules** and **vector rules** map a parameter name to a shader property, for example
  `Roughness` to `_Smoothness` with the *One Minus* conversion.
- **Flip Normal Green** converts Unreal's DirectX-style normal maps to Unity's convention.

Without a table the built-in defaults are used. Parameters that match no rule are listed in the
import report as `parameter.unmapped`.

## What the importer does

- FBX: file scale on, axis conversion baked, no cameras, lights or animation. FBX materials are
  kept inside the model and remapped to the generated materials, so no material assets are
  extracted from the FBX. A slot whose material was not converted keeps the embedded material.
- Slots are matched to FBX materials by slot name, then by material name, then by index.
- Materials with `convertible: false` are reported, not converted.
- Colors from the manifest are linear and are converted to the color space of the target
  property.

## Tests

EditMode tests live in `Tests/Editor`. `Tests/Fixtures~/Cube` is a small export folder generated
with Blender by `Tests/Fixtures~/generate_cube.py`; it is self-made and says nothing about the
units or axes of a real Unreal FBX. The fixture bakes its axis conversion at export time, so the
identity rotation the tests assert is a regression check for this fixture, not proof that an
Unreal export imports unrotated. To run the tests, add `com.unity.test-framework` to the project
and list the package under `testables` in the project manifest.

## Target shader

`Universal Render Pipeline/Lit`, metallic workflow. Checked against the shader source of URP
17.3.0 (Unity 6000.3.12f1):

| Surface input | Property | Channel | Keyword |
| --- | --- | --- | --- |
| Base color | `_BaseMap`, `_BaseColor` | RGB (A = alpha) | — |
| Metallic | `_MetallicGlossMap`, `_Metallic` | R | `_METALLICSPECGLOSSMAP` |
| Smoothness | `_MetallicGlossMap`, `_Smoothness` | A | `_METALLICSPECGLOSSMAP` |
| Occlusion | `_OcclusionMap`, `_OcclusionStrength` | G | `_OCCLUSIONMAP` |
| Normal | `_BumpMap`, `_BumpScale` | tangent space | `_NORMALMAP` |
| Emission | `_EmissionMap`, `_EmissionColor` | RGB | `_EMISSION` |

Consequences for the importer:

- Metallic, smoothness and occlusion read from different channels, so a single repacked texture
  (R = metallic, G = occlusion, A = smoothness) can be assigned to both `_MetallicGlossMap` and
  `_OcclusionMap`.
- With `_METALLICSPECGLOSSMAP` enabled the sampled alpha is multiplied by `_Smoothness`, whose
  default is 0.5, and `_Metallic` is ignored. Generated materials must set `_Smoothness` to 1.
- Unity normal maps use the OpenGL convention (green up) while Unreal uses DirectX (green down).
  This comes from the engines' conventions, not from the shader source, and still has to be
  confirmed visually with a real export.
