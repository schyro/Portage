using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Portage
{
    /// <summary>Imports a Portage export folder: textures, URP Lit materials and FBX meshes.</summary>
    public static class PortageImporter
    {
        /// <summary>
        /// Imports an export folder into the project.
        /// </summary>
        /// <param name="exportFolder">Absolute path of the folder containing manifest.json.</param>
        /// <param name="targetFolder">Project-relative target, for example Assets/Portage/Cube.</param>
        /// <param name="table">Mapping rules, or null for the built-in defaults.</param>
        public static PortageImportReport Import(string exportFolder, string targetFolder, PortageMappingTable table)
        {
            var report = new PortageImportReport();

            targetFolder = (targetFolder ?? "").Replace('\\', '/').TrimEnd('/');
            if (targetFolder != "Assets" && !targetFolder.StartsWith("Assets/", StringComparison.Ordinal))
            {
                report.Error("target.invalid", targetFolder, "The target folder must be inside the project's Assets folder.");
                return report;
            }

            PortageManifest manifest;
            try
            {
                manifest = PortageManifest.Load(Path.Combine(exportFolder ?? "", "manifest.json"));
            }
            catch (PortageManifestException e)
            {
                report.Error("manifest.invalid", "", e.Message);
                return report;
            }

            PortageMappingTable temporaryTable = null;
            if (table == null)
            {
                temporaryTable = PortageMappingTable.CreateDefault();
                table = temporaryTable;
            }

            try
            {
                foreach (PortageManifest.IssueEntry issue in manifest.issues)
                    report.AddManifestIssue(issue);

                string meshFolder = targetFolder + "/Meshes";
                string textureFolder = targetFolder + "/Textures";
                string materialFolder = targetFolder + "/Materials";
                PortageAssetUtil.EnsureFolder(meshFolder);
                PortageAssetUtil.EnsureFolder(textureFolder);
                PortageAssetUtil.EnsureFolder(materialFolder);

                // Textures are imported on demand while materials are built, so only textures that a
                // convertible material uses through a matching rule end up in the project.
                var textures = new PortageTextureProcessor(exportFolder, textureFolder, table, report);
                var builder = new PortageMaterialBuilder(manifest, table, textures, materialFolder, report);
                var materials = new Dictionary<string, Material>();
                foreach (PortageManifest.MaterialEntry entry in manifest.materials)
                {
                    Material material = builder.Build(entry);
                    if (material != null)
                        materials[entry.id] = material;
                }

                foreach (PortageManifest.MeshEntry mesh in manifest.meshes)
                    ImportMesh(exportFolder, meshFolder, manifest, mesh, materials, report);

                AssetDatabase.SaveAssets();
                AssetDatabase.Refresh();
            }
            finally
            {
                if (temporaryTable != null)
                    Object.DestroyImmediate(temporaryTable);
            }

            return report;
        }

        static void ImportMesh(string exportFolder, string meshFolder, PortageManifest manifest,
            PortageManifest.MeshEntry mesh, Dictionary<string, Material> materials, PortageImportReport report)
        {
            string source = Path.GetFullPath(Path.Combine(exportFolder, mesh.file ?? ""));
            if (!File.Exists(source))
            {
                report.Error("mesh.missingFile", mesh.id, $"Mesh file not found: {source}");
                return;
            }

            string assetPath = meshFolder + "/" + Path.GetFileName(source);
            PortageAssetUtil.CopyIfChanged(source, PortageAssetUtil.ToAbsolutePath(assetPath));
            AssetDatabase.ImportAsset(assetPath, ImportAssetOptions.ForceSynchronousImport);

            var importer = AssetImporter.GetAtPath(assetPath) as ModelImporter;
            if (importer == null)
            {
                report.Error("mesh.importFailed", mesh.id, $"Unity could not import {assetPath} as a model.");
                return;
            }

            // Phase 1: fixed settings, no remaps, so the renderers show the embedded FBX materials
            // and their names can be read.
            bool dirty = false;
            Set(ref dirty, importer.globalScale, 1f, v => importer.globalScale = v);
            Set(ref dirty, importer.useFileScale, true, v => importer.useFileScale = v);
            Set(ref dirty, importer.bakeAxisConversion, true, v => importer.bakeAxisConversion = v);
            Set(ref dirty, importer.importCameras, false, v => importer.importCameras = v);
            Set(ref dirty, importer.importLights, false, v => importer.importLights = v);
            Set(ref dirty, importer.importAnimation, false, v => importer.importAnimation = v);
            Set(ref dirty, importer.animationType, ModelImporterAnimationType.None, v => importer.animationType = v);
            Set(ref dirty, importer.importBlendShapes, false, v => importer.importBlendShapes = v);
            Set(ref dirty, importer.materialImportMode, ModelImporterMaterialImportMode.ImportViaMaterialDescription,
                v => importer.materialImportMode = v);
            Set(ref dirty, importer.materialLocation, ModelImporterMaterialLocation.InPrefab,
                v => importer.materialLocation = v);

            // Remove stale remaps from earlier imports.
            foreach (KeyValuePair<AssetImporter.SourceAssetIdentifier, Object> remap
                     in new Dictionary<AssetImporter.SourceAssetIdentifier, Object>(importer.GetExternalObjectMap()))
            {
                if (remap.Key.type == typeof(Material))
                {
                    importer.RemoveRemap(remap.Key);
                    dirty = true;
                }
            }

            if (dirty)
                importer.SaveAndReimport();

            List<string> fbxMaterialNames = ReadFbxMaterialNames(assetPath);

            // Phase 2: remap every FBX material that has a generated counterpart.
            int remapped = 0;
            for (int i = 0; i < fbxMaterialNames.Count; i++)
            {
                PortageManifest.SlotEntry slot = FindSlot(manifest, mesh, fbxMaterialNames[i], i);
                if (slot == null)
                {
                    report.Info("slot.unmatched", mesh.id,
                        $"{mesh.name}: FBX material '{fbxMaterialNames[i]}' matches no manifest slot and keeps the embedded material.");
                    continue;
                }

                if (!materials.TryGetValue(slot.material ?? "", out Material generated))
                    continue; // slot without a converted material keeps the embedded one

                importer.AddRemap(new AssetImporter.SourceAssetIdentifier(typeof(Material), fbxMaterialNames[i]), generated);
                remapped++;
            }

            if (remapped > 0)
                importer.SaveAndReimport();

            report.CreatedMeshes.Add(assetPath);
        }

        static void Set<T>(ref bool dirty, T current, T wanted, Action<T> apply)
        {
            if (EqualityComparer<T>.Default.Equals(current, wanted))
                return;
            apply(wanted);
            dirty = true;
        }

        /// <summary>Material names of the first renderer of the imported model, in slot order.</summary>
        static List<string> ReadFbxMaterialNames(string assetPath)
        {
            var names = new List<string>();
            var root = AssetDatabase.LoadAssetAtPath<GameObject>(assetPath);
            if (root == null)
                return names;

            Renderer renderer = root.GetComponentInChildren<Renderer>(true);
            if (renderer == null)
                return names;

            foreach (Material material in renderer.sharedMaterials)
                names.Add(material != null ? material.name : "");
            return names;
        }

        /// <summary>Matches by slot name, then by the slot material's name, then by slot index.</summary>
        static PortageManifest.SlotEntry FindSlot(PortageManifest manifest, PortageManifest.MeshEntry mesh,
            string fbxMaterialName, int index)
        {
            PortageManifest.SlotEntry slot = mesh.slots.Find(s => s.name == fbxMaterialName);
            if (slot != null)
                return slot;

            slot = mesh.slots.Find(s => manifest.FindMaterial(s.material)?.name == fbxMaterialName);
            if (slot != null)
                return slot;

            return mesh.slots.Find(s => s.index == index);
        }
    }
}
