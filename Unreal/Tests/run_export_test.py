"""Builds self-made test content in the open project and exports it with Portage.

Run headless from the project that has the Portage plugin enabled:

    UnrealEditor-Cmd.exe <Project>.uproject -run=pythonscript -script=<this file> -unattended -nullrhi

Everything is created by this script under /Game/PortageTest: textures, a parameterised master
material with an instance, a material without parameters and an asymmetric box mesh. The content
is deleted and rebuilt on every run, so do not run it against a project that is open in the
editor; an empty project with the plugin enabled is enough. The export
goes to <Project>/Saved/PortageTest/Block (or the folder in the PORTAGE_TEST_OUT environment
variable). A result.json next to the manifest records what Unreal reports about the content, so
the Unity side can be compared against it. A traceback.txt is written instead if the run fails.
"""

import json
import math
import os
import struct
import traceback
import zlib

import unreal

CONTENT = "/Game/PortageTest"
SIZE = 64
OUT_DIR = os.environ.get("PORTAGE_TEST_OUT") or os.path.join(
    unreal.SystemLibrary.get_project_saved_directory(), "PortageTest", "Block"
)
SOURCE_DIR = os.path.join(unreal.SystemLibrary.get_project_saved_directory(), "PortageTest", "Source")

# The box spans 0..1 m on X, 0..2 m on Y and 0..3 m on Z, so its size and the position of its
# bounds reveal any axis swap or mirroring on the way to Unity.
BOX_MIN = unreal.Vector(0.0, 0.0, 0.0)
BOX_MAX = unreal.Vector(100.0, 200.0, 300.0)


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

    with open(path, "wb") as stream:
        stream.write(b"\x89PNG\r\n\x1a\n")
        stream.write(chunk(b"IHDR", struct.pack(">IIBBBBB", SIZE, SIZE, 8, 2, 0, 0, 0)))
        stream.write(chunk(b"IDAT", zlib.compress(bytes(raw), 9)))
        stream.write(chunk(b"IEND", b""))


def base_color(x, y):
    # A red square marks the top-left corner so a flipped or rotated texture is visible.
    if x < SIZE // 4 and y < SIZE // 4:
        return (220, 30, 30)
    return (200, 160, 60) if (x // 8 + y // 8) % 2 == 0 else (70, 80, 110)


def normal(x, y):
    # DirectX convention: green points down the image.
    fx = 2.0 * math.pi * 2.0 * x / SIZE
    fy = 2.0 * math.pi * 2.0 * y / SIZE
    nx = -0.4 * math.cos(fx) * math.sin(fy)
    ny = -0.4 * math.sin(fx) * math.cos(fy)
    nz = math.sqrt(max(0.0, 1.0 - nx * nx - ny * ny))
    return tuple(int(round((c * 0.5 + 0.5) * 255.0)) for c in (nx, ny, nz))


def orm(x, y):
    # R = ambient occlusion, G = roughness, B = metallic.
    border = x < 4 or y < 4 or x >= SIZE - 4 or y >= SIZE - 4
    return (128 if border else 255, 51 if x < SIZE // 2 else 204, 0 if y < SIZE // 2 else 255)


def import_texture(name, pixel, srgb, compression):
    os.makedirs(SOURCE_DIR, exist_ok=True)
    path = os.path.join(SOURCE_DIR, name + ".png")
    write_png(path, pixel)

    task = unreal.AssetImportTask()
    task.set_editor_property("filename", path)
    task.set_editor_property("destination_path", CONTENT)
    task.set_editor_property("automated", True)
    task.set_editor_property("replace_existing", True)
    task.set_editor_property("save", False)
    unreal.AssetToolsHelpers.get_asset_tools().import_asset_tasks([task])

    texture = unreal.EditorAssetLibrary.load_asset("%s/%s" % (CONTENT, name))
    if texture is None:
        raise RuntimeError("Texture import failed: %s" % name)
    texture.set_editor_property("srgb", srgb)
    texture.set_editor_property("compression_settings", compression)
    return texture


def create_asset(name, asset_class, factory):
    path = "%s/%s" % (CONTENT, name)
    if unreal.EditorAssetLibrary.does_asset_exist(path):
        unreal.EditorAssetLibrary.delete_asset(path)
    asset = unreal.AssetToolsHelpers.get_asset_tools().create_asset(name, CONTENT, asset_class, factory)
    if asset is None:
        raise RuntimeError("Could not create %s" % path)
    return asset


def texture_parameter(material, name, texture, sampler, y):
    library = unreal.MaterialEditingLibrary
    node = library.create_material_expression(material, unreal.MaterialExpressionTextureSampleParameter2D, -600, y)
    node.set_editor_property("parameter_name", name)
    node.set_editor_property("texture", texture)
    node.set_editor_property("sampler_type", sampler)
    return node


def static_switch(material, name, default, x, y):
    library = unreal.MaterialEditingLibrary
    node = library.create_material_expression(material, unreal.MaterialExpressionStaticSwitchParameter, x, y)
    node.set_editor_property("parameter_name", name)
    node.set_editor_property("default_value", default)
    return node


def build_master_material(base, normal_map, mask):
    library = unreal.MaterialEditingLibrary
    material = create_asset("M_Prop", unreal.Material, unreal.MaterialFactoryNew())

    color = texture_parameter(material, "BaseColor", base, unreal.MaterialSamplerType.SAMPLERTYPE_COLOR, 0)
    tint = library.create_material_expression(material, unreal.MaterialExpressionVectorParameter, -600, -250)
    tint.set_editor_property("parameter_name", "Tint")
    tint.set_editor_property("default_value", unreal.LinearColor(1.0, 1.0, 1.0, 1.0))
    multiply = library.create_material_expression(material, unreal.MaterialExpressionMultiply, -300, -100)
    library.connect_material_expressions(color, "RGB", multiply, "A")
    library.connect_material_expressions(tint, "", multiply, "B")

    # One switch the instance overrides and one it inherits, to check both are read correctly.
    use_tint = static_switch(material, "Use Tint", False, 0, -100)
    library.connect_material_expressions(multiply, "", use_tint, "True")
    library.connect_material_expressions(color, "RGB", use_tint, "False")
    library.connect_material_property(use_tint, "", unreal.MaterialProperty.MP_BASE_COLOR)

    normal_node = texture_parameter(material, "Normal", normal_map, unreal.MaterialSamplerType.SAMPLERTYPE_NORMAL, 300)
    flat = library.create_material_expression(material, unreal.MaterialExpressionConstant3Vector, -600, 450)
    flat.set_editor_property("constant", unreal.LinearColor(0.0, 0.0, 1.0, 0.0))
    use_normal = static_switch(material, "Use Normal", True, 0, 300)
    library.connect_material_expressions(normal_node, "RGB", use_normal, "True")
    library.connect_material_expressions(flat, "", use_normal, "False")
    library.connect_material_property(use_normal, "", unreal.MaterialProperty.MP_NORMAL)

    mask_node = texture_parameter(material, "ORM", mask, unreal.MaterialSamplerType.SAMPLERTYPE_MASKS, 600)
    library.connect_material_property(mask_node, "R", unreal.MaterialProperty.MP_AMBIENT_OCCLUSION)
    library.connect_material_property(mask_node, "G", unreal.MaterialProperty.MP_ROUGHNESS)
    library.connect_material_property(mask_node, "B", unreal.MaterialProperty.MP_METALLIC)

    # One scalar the instance overrides and one it inherits, to check both are read correctly.
    for name, default, y in (("WetnessAmount", 0.1, 900), ("InheritedAmount", 0.7, 1000)):
        scalar = library.create_material_expression(material, unreal.MaterialExpressionScalarParameter, -600, y)
        scalar.set_editor_property("parameter_name", name)
        scalar.set_editor_property("default_value", default)
        if name == "WetnessAmount":
            library.connect_material_property(scalar, "", unreal.MaterialProperty.MP_SPECULAR)

    library.recompile_material(material)
    return material


def build_instance(parent):
    library = unreal.MaterialEditingLibrary
    instance = create_asset("MI_Block", unreal.MaterialInstanceConstant, unreal.MaterialInstanceConstantFactoryNew())
    library.set_material_instance_parent(instance, parent)
    library.set_material_instance_scalar_parameter_value(instance, "WetnessAmount", 0.25)
    library.set_material_instance_vector_parameter_value(instance, "Tint", unreal.LinearColor(1.0, 0.9, 0.8, 1.0))
    library.set_material_instance_static_switch_parameter_value(instance, "Use Tint", True)

    overrides = instance.get_editor_property("base_property_overrides")
    overrides.set_editor_property("override_two_sided", True)
    overrides.set_editor_property("two_sided", True)
    instance.set_editor_property("base_property_overrides", overrides)

    library.update_material_instance(instance)
    return instance


def build_embedded_material(base):
    library = unreal.MaterialEditingLibrary
    material = create_asset("M_Embedded", unreal.Material, unreal.MaterialFactoryNew())
    sample = library.create_material_expression(material, unreal.MaterialExpressionTextureSample, -400, 0)
    sample.set_editor_property("texture", base)
    library.connect_material_property(sample, "RGB", unreal.MaterialProperty.MP_BASE_COLOR)
    library.recompile_material(material)
    return material


def build_mesh(body_material, cap_material):
    mesh = create_asset("SM_Block", unreal.StaticMesh, None)

    description = unreal.StaticMesh.create_static_mesh_description(mesh)
    body = description.create_polygon_group()
    cap = description.create_polygon_group()
    description.set_polygon_group_material_slot_name(body, "Body")
    description.set_polygon_group_material_slot_name(cap, "Cap")

    # create_cube does not place its corners where center and half extents suggest (UE 5.8.3),
    # so it is only used for the topology and every corner is positioned explicitly.
    faces = description.create_cube(unreal.Vector(0.0, 0.0, 0.0), unreal.Vector(1.0, 1.0, 1.0), body)
    description.set_polygon_polygon_group(faces[4], cap)  # the +Z face
    for face in faces:
        for vertex in description.get_polygon_vertices(face):
            corner = description.get_vertex_position(vertex)
            description.set_vertex_position(
                vertex,
                unreal.Vector(
                    BOX_MAX.x if corner.x > 0.0 else BOX_MIN.x,
                    BOX_MAX.y if corner.y > 0.0 else BOX_MIN.y,
                    BOX_MAX.z if corner.z > 0.0 else BOX_MIN.z,
                ),
            )

    mesh.build_from_static_mesh_descriptions([description], False, False)

    materials = []
    for slot, material in (("Body", body_material), ("Cap", cap_material)):
        static_material = unreal.StaticMaterial()
        static_material.set_editor_property("material_slot_name", slot)
        static_material.set_editor_property("material_interface", material)
        materials.append(static_material)
    mesh.set_editor_property("static_materials", materials)
    return mesh


def vector(value):
    return [round(value.x, 4), round(value.y, 4), round(value.z, 4)]


def main():
    import portage_exporter

    base = import_texture("T_Block_D", base_color, True, unreal.TextureCompressionSettings.TC_DEFAULT)
    normal_map = import_texture("T_Block_N", normal, False, unreal.TextureCompressionSettings.TC_NORMALMAP)
    mask = import_texture("T_Block_ORM", orm, False, unreal.TextureCompressionSettings.TC_MASKS)

    master = build_master_material(base, normal_map, mask)
    instance = build_instance(master)
    embedded = build_embedded_material(base)
    mesh = build_mesh(instance, embedded)
    unreal.EditorAssetLibrary.save_directory(CONTENT)

    manifest = portage_exporter.export_assets([mesh], OUT_DIR)

    bounds = mesh.get_bounds()
    result = {
        "engineVersion": unreal.SystemLibrary.get_engine_version(),
        "boundsOriginCm": vector(bounds.origin),
        "boundsExtentCm": vector(bounds.box_extent),
        "slots": [
            [str(m.get_editor_property("material_slot_name")), m.get_editor_property("material_interface").get_name()]
            for m in mesh.get_editor_property("static_materials")
        ],
        "switches": {
            str(name): bool(unreal.MaterialEditingLibrary.get_material_instance_static_switch_parameter_value(instance, name))
            for name in unreal.MaterialEditingLibrary.get_static_switch_parameter_names(instance)
        },
        "masterSwitches": {
            str(name): bool(unreal.MaterialEditingLibrary.get_material_default_static_switch_parameter_value(master, name))
            for name in unreal.MaterialEditingLibrary.get_static_switch_parameter_names(master)
        },
        "meshes": len(manifest["meshes"]),
        "materials": len(manifest["materials"]),
        "textures": len(manifest["textures"]),
        "issues": manifest["issues"],
    }
    with open(os.path.join(OUT_DIR, "result.json"), "w", encoding="utf-8") as stream:
        json.dump(result, stream, indent=2)


try:
    os.makedirs(OUT_DIR, exist_ok=True)
    main()
except Exception:
    with open(os.path.join(OUT_DIR, "traceback.txt"), "w", encoding="utf-8") as stream:
        stream.write(traceback.format_exc())
    raise
