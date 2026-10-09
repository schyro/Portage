"""Runs when the editor starts. Registers the Portage menu entries."""

import unreal

if not unreal.SystemLibrary.is_unattended():
    try:
        from portage_exporter import menu

        menu.register()
    except Exception as error:  # Never break editor startup because of a menu.
        unreal.log_warning("Portage: could not register menu entries: %s" % error)
