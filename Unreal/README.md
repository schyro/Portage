# Portage — Unreal exporter

An Unreal Engine plugin that exports static meshes, with their materials and textures, into a
folder the [Portage Unity importer](../Unity/README.md) can read.

Tested on Unreal Engine 5.8.3. The plugin is content-only and written in Python, so nothing has
to be compiled.

## Install

1. Copy the `Portage` folder into `<YourProject>/Plugins/`. Create the `Plugins` folder if the
   project does not have one.
2. Restart the editor.
3. If the menu entry below is missing, enable **Portage** under **Edit > Plugins** and restart
   again.

The plugin turns on *Python Editor Script Plugin* and *Editor Scripting Utilities*, which ship
with the engine.

## Export

1. Select one or more static meshes in the Content Browser.
2. Choose **Tools > Export Selected to Unity**. The same entry is in the right-click menu of a
   static mesh.
3. Pick an output folder.

The materials and textures the meshes use are included automatically. When the export finishes,
a summary lists anything that could not be transferred.

The output folder contains:

```
MyExport/
├─ manifest.json     what was exported and how to interpret it
├─ Meshes/*.fbx
└─ Textures/*.png
```

You can export into the same folder again. Mapping choices made in Unity
(`portage-mapping.json`) are left untouched.

## What is exported

| | |
| --- | --- |
| Static meshes | FBX, from the source mesh data of LOD 0, with vertex colors. No collision, no extra LODs. |
| Materials | As data, not baked: blend mode, shading model, two-sided flag and the scalar, vector, texture and static switch parameter values in effect for the material or instance. |
| Textures | Every 2D texture assigned to a texture parameter, as PNG. |

A material that exposes no parameters cannot be read this way, because its textures and
constants live inside the material graph. It is marked as not convertible and listed as an
issue; the mesh is still exported.

## Scripting

```python
import portage_exporter

manifest = portage_exporter.export_assets(meshes, "D:/Exports/MyProps")
```

`meshes` is a list of loaded `unreal.StaticMesh` assets. The return value is the manifest as a
dictionary, including its `issues` list.

## Limitations

- Only static meshes. Skeletal meshes, animations and levels are planned.
- HDR, 16-bit and virtual textures have not been tested and may fail to export or lose
  precision.
- Static component mask parameters are not exported.

## Only export content you may use outside Unreal

See [Content licensing](../README.md#content-licensing) in the main README.

## For contributors

`Tests/run_export_test.py` builds self-made test content in a project (textures, a master
material with an instance, a material without parameters and an asymmetric box), exports it and
writes a `result.json` with what Unreal reports about it. It deletes and rebuilds
`/Game/PortageTest` on every run, so use a project that is not open in the editor; an empty one
with the plugin enabled is enough. Run it headless:

```
UnrealEditor-Cmd.exe <Project>.uproject -run=pythonscript -script=<repo>/Unreal/Tests/run_export_test.py -unattended -nullrhi
```

Its output is checked in as `Unity/Tests/Fixtures~/Block` and used by the Unity tests. Remove
local file paths from the FBX before committing a new version.

The format of `manifest.json` is specified in
[Docs/manifest-format.md](../Docs/manifest-format.md).
