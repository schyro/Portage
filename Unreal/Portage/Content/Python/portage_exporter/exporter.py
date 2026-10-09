"""Exports static meshes with the materials and textures they use.

The output folder follows Docs/manifest-format.md: a manifest.json next to Meshes/*.fbx and
Textures/*.png. Materials are not baked; the parameters of each material are written to the
manifest and converted on the Unity side.
"""

import datetime
import json
import os
import re

import unreal

FORMAT_VERSION = 1
EXPORTER_VERSION = "0.1.0"

_BLEND_MODES = {
    "BLEND_OPAQUE": "opaque",
    "BLEND_MASKED": "masked",
    "BLEND_TRANSLUCENT": "translucent",
    "BLEND_ADDITIVE": "additive",
    "BLEND_MODULATE": "modulate",
}

# Manifest spelling of the texture compression settings: the C++ enum name without "TC_".
_COMPRESSION = {
    "TC_DEFAULT": "Default",
    "TC_NORMALMAP": "Normalmap",
    "TC_MASKS": "Masks",
    "TC_GRAYSCALE": "Grayscale",
    "TC_DISPLACEMENTMAP": "Displacementmap",
    "TC_VECTOR_DISPLACEMENTMAP": "VectorDisplacementmap",
    "TC_HDR": "HDR",
    "TC_EDITOR_ICON": "EditorIcon",
    "TC_ALPHA": "Alpha",
    "TC_DISTANCE_FIELD_FONT": "DistanceFieldFont",
    "TC_HDR_COMPRESSED": "HDR_Compressed",
    "TC_BC7": "BC7",
    "TC_HALF_FLOAT": "HalfFloat",
    "TC_SINGLE_FLOAT": "SingleFloat",
    "TC_HDR_F32": "HDR_F32",
}


class _Export:
    """State of one export run."""

    def __init__(self, output_dir):
        self.output_dir = output_dir
        self.meshes = []
        self.materials = {}  # id -> manifest entry, in insertion order
        self.textures = {}  # id -> manifest entry, or None when the texture could not be exported
        self.issues = []
        self._used_files = set()

    def issue(self, severity, code, asset, message):
        self.issues.append({"severity": severity, "code": code, "asset": asset, "message": message})
        log = unreal.log_error if severity == "error" else unreal.log_warning
        log("Portage: %s" % message)

    def unique_file(self, folder, name, extension):
        """Returns a relative file path that no other exported asset uses."""
        safe = re.sub(r"[^A-Za-z0-9_.-]", "_", name) or "Asset"
        candidate = "%s/%s%s" % (folder, safe, extension)
        suffix = 2
        while candidate.lower() in self._used_files:
            candidate = "%s/%s_%d%s" % (folder, safe, suffix, extension)
            suffix += 1
        self._used_files.add(candidate.lower())
        return candidate

    def absolute(self, relative):
        return os.path.join(self.output_dir, *relative.split("/"))

    # ----- meshes -------------------------------------------------------------------------

    def add_mesh(self, mesh):
        mesh_id = mesh.get_path_name()
        relative = self.unique_file("Meshes", mesh.get_name(), ".fbx")

        options = unreal.FbxExportOption()
        options.set_editor_property("ascii", False)
        options.set_editor_property("collision", False)
        # Source data instead of render data, so Nanite meshes are not exported as their fallback.
        options.set_editor_property("export_source_mesh", True)
        options.set_editor_property("level_of_detail", False)
        options.set_editor_property("vertex_color", True)
        options.set_editor_property("force_front_x_axis", False)
        options.set_editor_property("bake_material_inputs", unreal.FbxMaterialBakeMode.DISABLED)

        if not _run_export(mesh, self.absolute(relative), unreal.StaticMeshExporterFBX(), options):
            self.issue("error", "mesh.exportFailed", mesh_id, "%s could not be exported as FBX." % mesh.get_name())
            return

        slots = []
        for index, static_material in enumerate(mesh.get_editor_property("static_materials")):
            material = static_material.get_editor_property("material_interface")
            slots.append(
                {
                    "index": index,
                    "name": str(static_material.get_editor_property("material_slot_name")),
                    "material": self.add_material(material) if material else "",
                }
            )

        self.meshes.append({"id": mesh_id, "name": mesh.get_name(), "file": relative, "slots": slots})

    # ----- materials ----------------------------------------------------------------------

    def add_material(self, material):
        material_id = material.get_path_name()
        if material_id in self.materials:
            return material_id

        name = material.get_name()
        is_instance = isinstance(material, unreal.MaterialInstanceConstant)
        base = material.get_base_material()
        parent = material.get_editor_property("parent") if is_instance else None
        two_sided, clip_value, shading_model = _effective_properties(material, base)

        entry = {
            "id": material_id,
            "name": name,
            "kind": "instance" if is_instance else "material",
            "parent": parent.get_path_name() if parent else "",
            "baseMaterial": base.get_path_name() if base else "",
            "blendMode": self._blend_mode(material, material_id, name),
            "shadingModel": _lower_camel(shading_model.name, "MSM_"),
            "twoSided": bool(two_sided),
            "opacityMaskClipValue": float(clip_value),
            "scalarParameters": [],
            "vectorParameters": [],
            "textureParameters": [],
            "convertible": True,
        }
        # Registered before the parameters are read so the id is reserved.
        self.materials[material_id] = entry

        library = unreal.MaterialEditingLibrary
        for parameter in library.get_scalar_parameter_names(material):
            if is_instance:
                value = library.get_material_instance_scalar_parameter_value(material, parameter)
            else:
                value = library.get_material_default_scalar_parameter_value(material, parameter)
            entry["scalarParameters"].append({"name": str(parameter), "value": float(value)})

        for parameter in library.get_vector_parameter_names(material):
            if is_instance:
                color = library.get_material_instance_vector_parameter_value(material, parameter)
            else:
                color = library.get_material_default_vector_parameter_value(material, parameter)
            entry["vectorParameters"].append(
                {"name": str(parameter), "value": [float(color.r), float(color.g), float(color.b), float(color.a)]}
            )

        for parameter in library.get_texture_parameter_names(material):
            if is_instance:
                texture = library.get_material_instance_texture_parameter_value(material, parameter)
            else:
                texture = library.get_material_default_texture_parameter_value(material, parameter)
            entry["textureParameters"].append(
                {"name": str(parameter), "texture": self.add_texture(texture) if texture else ""}
            )

        if entry["shadingModel"] != "defaultLit":
            self.issue(
                "warning",
                "material.unsupportedShadingModel",
                material_id,
                "%s uses the shading model '%s', which has no URP Lit equivalent." % (name, entry["shadingModel"]),
            )

        if not (entry["scalarParameters"] or entry["vectorParameters"] or entry["textureParameters"]):
            entry["convertible"] = False
            self.issue(
                "warning",
                "material.noParameters",
                material_id,
                "%s exposes no parameters and was not converted. Its textures and values are embedded "
                "in the material graph." % name,
            )

        return material_id

    def _blend_mode(self, material, material_id, name):
        mode = material.get_blend_mode().name
        if mode in _BLEND_MODES:
            return _BLEND_MODES[mode]

        self.issue(
            "warning",
            "material.blendModeApproximated",
            material_id,
            "%s uses the blend mode '%s', which is exported as translucent." % (name, _lower_camel(mode, "BLEND_")),
        )
        return "translucent"

    # ----- textures -----------------------------------------------------------------------

    def add_texture(self, texture):
        """Exports a texture once. Returns its id, or an empty string if it cannot be exported."""
        texture_id = texture.get_path_name()
        if texture_id in self.textures:
            return texture_id if self.textures[texture_id] else ""

        name = texture.get_name()
        self.textures[texture_id] = None

        if not isinstance(texture, unreal.Texture2D):
            self.issue(
                "warning",
                "texture.unsupportedType",
                texture_id,
                "%s is a %s; only 2D textures are exported." % (name, texture.get_class().get_name()),
            )
            return ""

        relative = self.unique_file("Textures", name, ".png")
        if not _run_export(texture, self.absolute(relative), unreal.TextureExporterPNG(), None):
            self.issue("error", "texture.exportFailed", texture_id, "%s could not be exported as PNG." % name)
            return ""

        compression = texture.get_editor_property("compression_settings").name
        self.textures[texture_id] = {
            "id": texture_id,
            "name": name,
            "file": relative,
            "width": int(texture.blueprint_get_size_x()),
            "height": int(texture.blueprint_get_size_y()),
            "srgb": bool(texture.get_editor_property("srgb")),
            "compression": _COMPRESSION.get(compression, compression.replace("TC_", "", 1)),
            "flipGreenChannel": bool(texture.get_editor_property("flip_green_channel")),
        }
        return texture_id

    # ----- manifest -----------------------------------------------------------------------

    def manifest(self):
        return {
            "formatVersion": FORMAT_VERSION,
            "generator": {
                "name": "Portage",
                "version": EXPORTER_VERSION,
                "engineVersion": unreal.SystemLibrary.get_engine_version().split("-")[0],
                "exportedAt": datetime.datetime.now(datetime.timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ"),
            },
            "meshes": self.meshes,
            "materials": list(self.materials.values()),
            "textures": [entry for entry in self.textures.values() if entry],
            "issues": self.issues,
        }


def _run_export(asset, filename, exporter, options):
    os.makedirs(os.path.dirname(filename), exist_ok=True)

    task = unreal.AssetExportTask()
    task.set_editor_property("object", asset)
    task.set_editor_property("filename", filename)
    task.set_editor_property("exporter", exporter)
    task.set_editor_property("automated", True)
    task.set_editor_property("prompt", False)
    task.set_editor_property("replace_identical", True)
    if options is not None:
        task.set_editor_property("options", options)

    return bool(unreal.Exporter.run_asset_export_task(task)) and os.path.isfile(filename)


def _effective_properties(material, base):
    """Two-sided flag, opacity mask clip value and shading model after instance overrides."""
    chain = []
    current = material
    while isinstance(current, unreal.MaterialInstanceConstant):
        chain.append(current)
        current = current.get_editor_property("parent")

    two_sided = base.get_editor_property("two_sided")
    clip_value = base.get_editor_property("opacity_mask_clip_value")
    shading_model = base.get_editor_property("shading_model")

    # Apply overrides from the instance closest to the base material down to the leaf.
    for instance in reversed(chain):
        overrides = instance.get_editor_property("base_property_overrides")
        if overrides.get_editor_property("override_two_sided"):
            two_sided = overrides.get_editor_property("two_sided")
        if overrides.get_editor_property("override_opacity_mask_clip_value"):
            clip_value = overrides.get_editor_property("opacity_mask_clip_value")
        if overrides.get_editor_property("override_shading_model"):
            shading_model = overrides.get_editor_property("shading_model")

    return two_sided, clip_value, shading_model


def _lower_camel(enum_name, prefix):
    """MSM_DEFAULT_LIT -> defaultLit."""
    words = enum_name.replace(prefix, "", 1).lower().split("_")
    return words[0] + "".join(word.capitalize() for word in words[1:])


def export_assets(assets, output_dir):
    """Exports the static meshes in `assets` to `output_dir` and returns the manifest as a dict.

    Materials and textures used by the meshes are included automatically. Assets of other types
    are skipped and listed in the manifest issues.
    """
    output_dir = os.path.abspath(output_dir)
    os.makedirs(output_dir, exist_ok=True)
    export = _Export(output_dir)

    assets = list(assets)
    with unreal.ScopedSlowTask(len(assets), "Exporting to Unity") as progress:
        progress.make_dialog(True)
        for asset in assets:
            if progress.should_cancel():
                export.issue("warning", "export.cancelled", "", "The export was cancelled before it finished.")
                break
            progress.enter_progress_frame(1, "Exporting %s" % asset.get_name())

            if isinstance(asset, unreal.StaticMesh):
                export.add_mesh(asset)
            else:
                export.issue(
                    "info",
                    "asset.unsupportedType",
                    asset.get_path_name(),
                    "%s is a %s; only static meshes are exported." % (asset.get_name(), asset.get_class().get_name()),
                )

    manifest = export.manifest()
    with open(os.path.join(output_dir, "manifest.json"), "w", encoding="utf-8", newline="\n") as stream:
        json.dump(manifest, stream, indent=2)
        stream.write("\n")
    return manifest


def export_selected():
    """Exports the static meshes selected in the Content Browser to a folder the user picks."""
    title = "Portage"
    meshes = [asset for asset in unreal.EditorUtilityLibrary.get_selected_assets() if isinstance(asset, unreal.StaticMesh)]
    if not meshes:
        unreal.EditorDialog.show_message(
            title, "Select one or more static meshes in the Content Browser first.", unreal.AppMsgType.OK
        )
        return

    output_dir = _ask_directory()
    if not output_dir:
        return

    manifest = export_assets(meshes, output_dir)
    summary = "Exported %d mesh(es), %d material(s) and %d texture(s) to:\n%s" % (
        len(manifest["meshes"]),
        len(manifest["materials"]),
        len(manifest["textures"]),
        output_dir,
    )
    if manifest["issues"]:
        summary += "\n\n%d issue(s):\n" % len(manifest["issues"])
        summary += "\n".join("- " + issue["message"] for issue in manifest["issues"][:15])
        if len(manifest["issues"]) > 15:
            summary += "\n- ... see manifest.json for the rest."
    unreal.EditorDialog.show_message(title, summary, unreal.AppMsgType.OK)


def _ask_directory():
    """Shows a folder picker. Falls back to Saved/Portage in the project if no picker is available."""
    try:
        import tkinter
        from tkinter import filedialog

        root = tkinter.Tk()
    except Exception as error:  # tkinter or its Tcl/Tk runtime may be missing from the editor's Python.
        fallback = os.path.join(unreal.SystemLibrary.get_project_saved_directory(), "Portage")
        unreal.log_warning("Portage: no folder picker available (%s), exporting to %s" % (error, fallback))
        return fallback

    root.withdraw()
    root.attributes("-topmost", True)
    try:
        return filedialog.askdirectory(parent=root, title="Choose the Portage export folder", mustexist=False)
    finally:
        root.destroy()
