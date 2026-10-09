"""Editor menu entries for the exporter."""

import unreal

_MENUS = ("LevelEditor.MainMenu.Tools", "ContentBrowser.AssetContextMenu.StaticMesh")
_SECTION = "Portage"
_COMMAND = "import portage_exporter; portage_exporter.export_selected()"


def register():
    """Adds 'Export Selected to Unity' to the Tools menu and the static mesh context menu."""
    menus = unreal.ToolMenus.get()
    for menu_name in _MENUS:
        menu = menus.extend_menu(menu_name)
        menu.add_section(_SECTION, "Portage")

        entry = unreal.ToolMenuEntry(name="PortageExportSelected", type=unreal.MultiBlockType.MENU_ENTRY)
        entry.set_label("Export Selected to Unity")
        entry.set_tool_tip(
            "Exports the static meshes selected in the Content Browser, with their materials and "
            "textures, to a folder the Portage Unity importer can read."
        )
        entry.set_string_command(unreal.ToolMenuStringCommandType.PYTHON, "", _COMMAND)
        menu.add_menu_entry(_SECTION, entry)

    menus.refresh_all_widgets()
