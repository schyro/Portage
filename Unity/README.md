# Portage — Unity importer

UPM package `com.schyro.portage`. Imports a Portage export folder into a Unity project and
generates URP Lit materials from its [manifest](../Docs/manifest-format.md).

**Not implemented yet.** The package manifest is in place so the package id and install URL are
fixed; the editor code follows.

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
