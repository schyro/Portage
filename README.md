# Portage

Transfers assets from an Unreal Engine project to Unity: static meshes, textures and material
instances first, animations and level layouts later.

> **Status: pre-alpha.** The repository currently contains the project layout and the draft
> manifest format only. Neither the exporter nor the importer is implemented yet.

## How it works

Portage is a two-part pipeline with a JSON manifest in between.

```
Unreal Editor ──(Python exporter)──► export folder ──(Unity importer)──► Unity project
                                      ├─ manifest.json
                                      ├─ Meshes/*.fbx
                                      └─ Textures/*.png
```

1. **Unreal exporter** (`Unreal/`) — Python editor scripts. Exports meshes as FBX and textures as
   images, and reads material instance parameters instead of baking materials. No C++ and no
   per-engine-version compile step.
2. **Manifest** ([Docs/manifest-format.md](Docs/manifest-format.md)) — describes which mesh uses
   which material, which parameters each material has, and how each texture must be interpreted.
3. **Unity importer** (`Unity/`) — a UPM package. Imports the export folder, generates URP Lit
   materials, splits packed ORM textures into what URP expects, and assigns materials to slots.
   Parameter-name and channel mappings live in an editable asset, because they differ per asset
   pack.

## Requirements

| | Version |
| --- | --- |
| Unreal Engine | 5.8, opened in the editor with the *Python Editor Script Plugin* enabled (target; not tested yet) |
| Unity | 6.3 (6000.3) with the Universal Render Pipeline |

Only projects opened in the Unreal Editor are supported. Cooked `.pak` content is not.

## Installing the Unity package

Not usable yet. Once the importer exists it will install through the Package Manager with:

```
https://github.com/schyro/Portage.git?path=/Unity
```

## Scope

Milestone 1: static mesh + textures + simple material instance → URP Lit.

Planned afterwards: LODs, collision and sockets; skeletal meshes and animation sequences; level
placement as prefabs or scenes.

Out of scope, because it cannot be transferred or has to be rebuilt by hand: Blueprint logic,
Niagara, complex material graphs, Chaos cloth, Control Rig, MetaHuman grooms, full-fidelity
Landscape and foliage. Master materials whose textures are embedded in the graph with no exposed
parameters are reported, not converted.

## Content licensing

Portage is a tool. It does not grant you any rights to the content you run it on.

- Use it only on content you have the right to use outside Unreal Engine.
- Do not transfer content that is licensed for use in Unreal Engine only (legacy "UE-Only"
  content, and Epic's own sample content such as the Mannequin or Starter Content).
- Check the license of every asset before moving it. For Fab content see the
  [Fab licenses](https://dev.epicgames.com/documentation/en-us/fab/licenses-and-pricing-in-fab).

This repository contains no Unreal Engine source code and no third-party assets, and
contributions must keep it that way.

## License

[MIT](LICENSE)
