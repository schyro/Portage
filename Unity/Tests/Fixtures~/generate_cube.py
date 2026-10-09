"""Generates the self-made Cube export fixture used by the importer tests.

Run with Blender (tested with 5.2):

    blender --background --factory-startup --python generate_cube.py

The output mimics the layout of a Portage export folder, but it is produced by Blender, not by
Unreal. It says nothing about the units or axes of a real Unreal FBX export.
"""

import json
import math
import os
import struct
import zlib

import bpy

OUT_DIR = os.path.join(os.path.dirname(os.path.abspath(__file__)), "Cube")
SIZE = 64


def write_png(path, pixel):
    """Writes an 8-bit RGB PNG. pixel(x, y) returns (r, g, b); y = 0 is the top row."""
    raw = bytearray()
    for y in range(SIZE):
        raw.append(0)
        for x in range(SIZE):
            raw.extend(pixel(x, y))

    def chunk(tag, data):
        body = tag + data
        return struct.pack(">I", len(data)) + body + struct.pack(">I", zlib.crc32(body))

    header = struct.pack(">IIBBBBB", SIZE, SIZE, 8, 2, 0, 0, 0)
    with open(path, "wb") as f:
        f.write(b"\x89PNG\r\n\x1a\n")
        f.write(chunk(b"IHDR", header))
        f.write(chunk(b"IDAT", zlib.compress(bytes(raw), 9)))
        f.write(chunk(b"IEND", b""))


def base_color(x, y):
    return (200, 120, 40) if (x // 8 + y // 8) % 2 == 0 else (90, 90, 100)


def normal(x, y):
    # DirectX convention, as Unreal authors it: green points down the image.
    fx = 2.0 * math.pi * 4.0 * x / SIZE
    fy = 2.0 * math.pi * 4.0 * y / SIZE
    nx = -0.3 * math.cos(fx) * math.sin(fy)
    ny = -0.3 * math.sin(fx) * math.cos(fy)
    nz = math.sqrt(max(0.0, 1.0 - nx * nx - ny * ny))
    return tuple(int(round((c * 0.5 + 0.5) * 255.0)) for c in (nx, ny, nz))


def orm(x, y):
    # R = ambient occlusion, G = roughness, B = metallic. The tests rely on these exact values.
    border = x < 4 or y < 4 or x >= SIZE - 4 or y >= SIZE - 4
    occlusion = 128 if border else 255
    roughness = 51 if x < SIZE // 2 else 204
    metallic = 0 if y < SIZE // 2 else 255
    return (occlusion, roughness, metallic)


def build_cube():
    bpy.ops.wm.read_factory_settings(use_empty=True)
    bpy.ops.mesh.primitive_cube_add(size=1.0, location=(0.0, 0.0, 0.0))
    cube = bpy.context.active_object
    cube.name = "SM_Cube"
    cube.data.name = "SM_Cube"

    cube.data.materials.append(bpy.data.materials.new("MI_Cube"))
    cube.data.materials.append(bpy.data.materials.new("M_Embedded"))
    for polygon in cube.data.polygons:
        polygon.material_index = 1 if polygon.normal.z > 0.5 else 0
    return cube


def export_fbx(path):
    bpy.ops.export_scene.fbx(
        filepath=path,
        use_selection=False,
        object_types={"MESH"},
        apply_scale_options="FBX_SCALE_UNITS",
        bake_space_transform=True,
        bake_anim=False,
    )


def texture_entry(name, srgb, compression):
    return {
        "id": "/Game/Cube/%s.%s" % (name, name),
        "name": name,
        "file": "Textures/%s.png" % name,
        "width": SIZE,
        "height": SIZE,
        "srgb": srgb,
        "compression": compression,
        "flipGreenChannel": False,
    }


def write_manifest(path):
    manifest = {
        "formatVersion": 1,
        "generator": {
            "name": "Portage test fixture",
            "version": "0.1.0",
            "engineVersion": "",
            "exportedAt": "2026-10-09T00:00:00Z",
        },
        "meshes": [
            {
                "id": "/Game/Cube/SM_Cube.SM_Cube",
                "name": "SM_Cube",
                "file": "Meshes/SM_Cube.fbx",
                "slots": [
                    {"index": 0, "name": "MI_Cube", "material": "/Game/Cube/MI_Cube.MI_Cube"},
                    {
                        "index": 1,
                        "name": "M_Embedded",
                        "material": "/Game/Cube/M_Embedded.M_Embedded",
                    },
                ],
            }
        ],
        "materials": [
            {
                "id": "/Game/Cube/MI_Cube.MI_Cube",
                "name": "MI_Cube",
                "kind": "instance",
                "parent": "/Game/Cube/M_Prop.M_Prop",
                "baseMaterial": "/Game/Cube/M_Prop.M_Prop",
                "blendMode": "opaque",
                "shadingModel": "defaultLit",
                "twoSided": False,
                "opacityMaskClipValue": 0.3333,
                "scalarParameters": [{"name": "WetnessAmount", "value": 0.25}],
                "vectorParameters": [{"name": "Tint", "value": [1.0, 0.9, 0.8, 1.0]}],
                "textureParameters": [
                    {"name": "BaseColor", "texture": "/Game/Cube/T_Cube_D.T_Cube_D"},
                    {"name": "Normal", "texture": "/Game/Cube/T_Cube_N.T_Cube_N"},
                    {"name": "ORM", "texture": "/Game/Cube/T_Cube_ORM.T_Cube_ORM"},
                ],
                "convertible": True,
            },
            {
                "id": "/Game/Cube/M_Embedded.M_Embedded",
                "name": "M_Embedded",
                "kind": "material",
                "parent": "",
                "baseMaterial": "/Game/Cube/M_Embedded.M_Embedded",
                "blendMode": "opaque",
                "shadingModel": "defaultLit",
                "twoSided": False,
                "opacityMaskClipValue": 0.3333,
                "scalarParameters": [],
                "vectorParameters": [],
                "textureParameters": [],
                "convertible": False,
            },
        ],
        "textures": [
            texture_entry("T_Cube_D", True, "Default"),
            texture_entry("T_Cube_N", False, "Normalmap"),
            texture_entry("T_Cube_ORM", False, "Masks"),
        ],
        "issues": [
            {
                "severity": "warning",
                "code": "material.noParameters",
                "asset": "/Game/Cube/M_Embedded.M_Embedded",
                "message": "M_Embedded exposes no parameters and was not converted.",
            }
        ],
    }
    with open(path, "w", encoding="utf-8", newline="\n") as f:
        json.dump(manifest, f, indent=2)
        f.write("\n")


def main():
    os.makedirs(os.path.join(OUT_DIR, "Meshes"), exist_ok=True)
    os.makedirs(os.path.join(OUT_DIR, "Textures"), exist_ok=True)

    write_png(os.path.join(OUT_DIR, "Textures", "T_Cube_D.png"), base_color)
    write_png(os.path.join(OUT_DIR, "Textures", "T_Cube_N.png"), normal)
    write_png(os.path.join(OUT_DIR, "Textures", "T_Cube_ORM.png"), orm)

    build_cube()
    export_fbx(os.path.join(OUT_DIR, "Meshes", "SM_Cube.fbx"))
    write_manifest(os.path.join(OUT_DIR, "manifest.json"))


main()
