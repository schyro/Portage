# Portage manifest format

**Format version: 1 — draft.** This version has not been produced by a real Unreal export yet.
Fields may still change before the first release without a version bump.

The exporter writes one `manifest.json` at the root of every export folder. The manifest is the
only contract between the Unreal exporter and the Unity importer: the importer never needs the
Unreal project.

## Conventions

- UTF-8, no BOM.
- File paths are relative to the folder containing `manifest.json` and use `/` as separator.
- Assets are identified by their Unreal object path, for example
  `/Game/Props/Crate/SM_Crate.SM_Crate`. Identifiers are case-sensitive and unique within their
  list.
- Collections are arrays of objects, never maps keyed by name, so the file can be read with
  Unity's built-in `JsonUtility`.
- A field that has no value is written as an empty string or an empty array, never `null`.
- Colors and vectors are linear RGBA float arrays of length 4, exactly as stored in Unreal.
- Values are raw Unreal values. The manifest contains nothing Unity-specific; all conversion
  (roughness → smoothness, channel repacking, property names) is the importer's job.

## Versioning

`formatVersion` is an integer. It is incremented on any change that an older importer would
misread. An importer must refuse a manifest whose `formatVersion` is higher than it supports, and
must ignore unknown fields.

## Layout of an export folder

```
MyExport/
├─ manifest.json
├─ Meshes/
│  └─ SM_Crate.fbx
└─ Textures/
   ├─ T_Crate_D.png
   ├─ T_Crate_N.png
   └─ T_Crate_ORM.png
```

The folder names are a convention of the exporter. The importer follows the `file` fields only.

## Root object

| Field | Type | Description |
| --- | --- | --- |
| `formatVersion` | int | `1` |
| `generator` | object | See [generator](#generator). |
| `meshes` | array | See [meshes](#meshes). |
| `materials` | array | See [materials](#materials). |
| `textures` | array | See [textures](#textures). |
| `issues` | array | See [issues](#issues). |

### generator

| Field | Type | Description |
| --- | --- | --- |
| `name` | string | `"Portage"` |
| `version` | string | Exporter version. |
| `engineVersion` | string | Unreal Engine version that produced the export, for example `"5.8.3"`. |
| `exportedAt` | string | UTC timestamp, ISO 8601. |

### meshes

| Field | Type | Description |
| --- | --- | --- |
| `id` | string | Object path of the static mesh. |
| `name` | string | Asset name, for example `SM_Crate`. |
| `file` | string | Path of the exported FBX. |
| `slots` | array | Material slots in Unreal slot order. |

Each slot:

| Field | Type | Description |
| --- | --- | --- |
| `index` | int | Slot index in Unreal. |
| `name` | string | Material slot name. The importer matches FBX materials by this name first, then by the material's `name`, then by `index`. Which name Unreal writes into the FBX is not verified yet. |
| `material` | string | `id` of an entry in `materials`, or empty if the slot has no material. |

The FBX is exported with Unreal's units (centimetres) and axes untouched. Scale and axis
conversion happen in the Unity FBX import settings.

### materials

| Field | Type | Description |
| --- | --- | --- |
| `id` | string | Object path of the material or material instance. |
| `name` | string | Asset name. |
| `kind` | string | `"instance"` for a material instance, `"material"` for a master material. |
| `parent` | string | Object path of the direct parent. Empty for a master material. The parent is not necessarily listed in `materials`. |
| `baseMaterial` | string | Object path of the master material at the root of the parent chain. |
| `blendMode` | string | `"opaque"`, `"masked"`, `"translucent"`, `"additive"`, `"modulate"`. |
| `shadingModel` | string | Unreal shading model name in lower camel case, for example `"defaultLit"`, `"unlit"`. |
| `twoSided` | bool | |
| `opacityMaskClipValue` | float | Only meaningful when `blendMode` is `"masked"`. |
| `scalarParameters` | array | `{ "name": string, "value": float }` |
| `vectorParameters` | array | `{ "name": string, "value": [r, g, b, a] }` |
| `textureParameters` | array | `{ "name": string, "texture": string }` where `texture` is an `id` in `textures`, or empty if the parameter is unset. |
| `convertible` | bool | `false` when the material exposes no parameters the importer could map. Such a material also has an entry in `issues`. |

Parameter lists contain the effective values of the instance: overridden values where the
instance overrides them, inherited values otherwise. Parameter names are written exactly as they
appear in Unreal; mapping them to shader properties is configured on the Unity side.

### textures

| Field | Type | Description |
| --- | --- | --- |
| `id` | string | Object path of the texture. |
| `name` | string | Asset name. |
| `file` | string | Path of the exported image. |
| `width` | int | Source width in pixels. |
| `height` | int | Source height in pixels. |
| `srgb` | bool | Unreal's sRGB flag. `false` for data textures such as normal maps and ORM masks. |
| `compression` | string | Unreal compression setting without the `TC_` prefix, for example `"Default"`, `"Normalmap"`, `"Masks"`. The importer uses `"Normalmap"` to detect normal maps. |
| `flipGreenChannel` | bool | Unreal's *Flip Green Channel* flag. The exported image holds the source pixels, so the flag still has to be applied by the importer. |

Unreal normal maps follow the DirectX convention (green pointing down), Unity expects OpenGL
(green pointing up). The manifest does not record this: it is a property of the engine, and the
importer handles it through its mapping table.

### issues

Everything the exporter could not transfer faithfully. The importer shows these in its report.

| Field | Type | Description |
| --- | --- | --- |
| `severity` | string | `"info"`, `"warning"`, `"error"`. |
| `code` | string | Stable machine-readable code, see below. |
| `asset` | string | `id` of the asset concerned, or empty. |
| `message` | string | Human-readable explanation, in English. |

Codes defined in version 1:

| Code | Meaning |
| --- | --- |
| `material.noParameters` | Master material or instance with no exposed parameters; textures are embedded in the graph. Not converted. |
| `material.unsupportedShadingModel` | Shading model with no URP Lit equivalent. |
| `texture.exportFailed` | The texture could not be written. |
| `mesh.exportFailed` | The FBX could not be written. |

## Example

```json
{
  "formatVersion": 1,
  "generator": {
    "name": "Portage",
    "version": "0.1.0",
    "engineVersion": "5.8.3",
    "exportedAt": "2026-10-09T18:30:00Z"
  },
  "meshes": [
    {
      "id": "/Game/Props/Crate/SM_Crate.SM_Crate",
      "name": "SM_Crate",
      "file": "Meshes/SM_Crate.fbx",
      "slots": [
        {
          "index": 0,
          "name": "Body",
          "material": "/Game/Props/Crate/MI_Crate.MI_Crate"
        }
      ]
    }
  ],
  "materials": [
    {
      "id": "/Game/Props/Crate/MI_Crate.MI_Crate",
      "name": "MI_Crate",
      "kind": "instance",
      "parent": "/Game/Materials/M_Prop.M_Prop",
      "baseMaterial": "/Game/Materials/M_Prop.M_Prop",
      "blendMode": "opaque",
      "shadingModel": "defaultLit",
      "twoSided": false,
      "opacityMaskClipValue": 0.3333,
      "scalarParameters": [
        { "name": "RoughnessMultiplier", "value": 1.0 }
      ],
      "vectorParameters": [
        { "name": "Tint", "value": [1.0, 1.0, 1.0, 1.0] }
      ],
      "textureParameters": [
        { "name": "BaseColor", "texture": "/Game/Props/Crate/T_Crate_D.T_Crate_D" },
        { "name": "Normal", "texture": "/Game/Props/Crate/T_Crate_N.T_Crate_N" },
        { "name": "ORM", "texture": "/Game/Props/Crate/T_Crate_ORM.T_Crate_ORM" }
      ],
      "convertible": true
    }
  ],
  "textures": [
    {
      "id": "/Game/Props/Crate/T_Crate_D.T_Crate_D",
      "name": "T_Crate_D",
      "file": "Textures/T_Crate_D.png",
      "width": 2048,
      "height": 2048,
      "srgb": true,
      "compression": "Default",
      "flipGreenChannel": false
    },
    {
      "id": "/Game/Props/Crate/T_Crate_N.T_Crate_N",
      "name": "T_Crate_N",
      "file": "Textures/T_Crate_N.png",
      "width": 2048,
      "height": 2048,
      "srgb": false,
      "compression": "Normalmap",
      "flipGreenChannel": false
    },
    {
      "id": "/Game/Props/Crate/T_Crate_ORM.T_Crate_ORM",
      "name": "T_Crate_ORM",
      "file": "Textures/T_Crate_ORM.png",
      "width": 2048,
      "height": 2048,
      "srgb": false,
      "compression": "Masks",
      "flipGreenChannel": false
    }
  ],
  "issues": []
}
```

## Unverified until a real Unreal 5.8 export exists

- That Unreal's FBX exporter can leave units and axes untouched in a way Unity's import settings
  convert to scale 1 and rotation 0. If not, the exporter will have to record the applied
  conversion in the manifest. One data point so far: a Blender FBX exported without baking its
  axis conversion imported with a 90 degree rotation on X despite *Bake Axis Conversion*.
- That every field above can be read through the Unreal Python API (blend mode, shading model,
  effective parameter values of an instance, compression setting, *Flip Green Channel*).
- That PNG is sufficient for all exported textures; HDR and 16-bit sources may need another
  format.

## Reserved for later versions

LODs, collision and sockets on meshes; skeletal meshes and animation sequences; level placement.
These will be added as new fields or new root arrays together with a `formatVersion` bump where
an older importer would otherwise misread the file.
