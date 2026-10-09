using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditor.UIElements;
using UnityEngine;
using UnityEngine.UIElements;
using Object = UnityEngine.Object;

namespace Portage
{
    /// <summary>Editor window for importing a Portage export folder.</summary>
    public sealed class PortageImportWindow : EditorWindow
    {
        const string ExportFolderKey = "Portage.ExportFolder";
        const string TablePathKey = "Portage.MappingTablePath";

        static readonly TextureUsage[] UsageOptions =
        {
            TextureUsage.BaseColor, TextureUsage.Normal, TextureUsage.Emission, TextureUsage.Mask, TextureUsage.Ignore,
            TextureUsage.ColorMask,
        };

        static readonly string[] VectorProperties = { "_BaseColor", "_EmissionColor" };

        static readonly ScalarOption[] ScalarOptions =
        {
            new ScalarOption("(ignore)", "", ScalarConversion.None),
            new ScalarOption("_Metallic", "_Metallic", ScalarConversion.None),
            new ScalarOption("_Smoothness (1 - value)", "_Smoothness", ScalarConversion.OneMinus),
            new ScalarOption("_Smoothness", "_Smoothness", ScalarConversion.None),
            new ScalarOption("_BumpScale", "_BumpScale", ScalarConversion.None),
            new ScalarOption("_OcclusionStrength", "_OcclusionStrength", ScalarConversion.None),
            new ScalarOption("_Cutoff", "_Cutoff", ScalarConversion.None),
        };

        const string NotMapped = "(not mapped)";
        const string Ignore = "(ignore)";

        readonly struct ScalarOption
        {
            public readonly string Label;
            public readonly string Property;
            public readonly ScalarConversion Conversion;

            public ScalarOption(string label, string property, ScalarConversion conversion)
            {
                Label = label;
                Property = property;
                Conversion = conversion;
            }
        }

        // All manifest materials that share a base material and therefore share parameter choices.
        sealed class Group
        {
            public string Key = "";
            public readonly List<PortageManifest.MaterialEntry> Materials = new List<PortageManifest.MaterialEntry>();
            public readonly List<PortageMappingResolution> Resolutions = new List<PortageMappingResolution>();
            public readonly List<Action> Refreshers = new List<Action>();
        }

        TextField _exportField;
        TextField _targetField;
        ObjectField _tableField;
        Button _importButton;
        Label _statusLabel;
        VisualElement _resultRoot;
        VisualElement _mappingRoot;

        PortageManifest _manifest;
        PortageMappingFile _mapping;
        PortageMappingTable _defaultTable;

        bool _targetEditedByUser;
        string _autoTarget = "";

        [MenuItem("Tools/Portage/Import...")]
        static void Open()
        {
            var window = GetWindow<PortageImportWindow>();
            window.titleContent = new GUIContent("Portage Import");
            window.minSize = new Vector2(420, 360);
        }

        void CreateGUI()
        {
            VisualElement window = rootVisualElement;
            window.style.paddingLeft = 8;
            window.style.paddingRight = 8;
            window.style.paddingTop = 8;
            window.style.paddingBottom = 8;

            // The whole body scrolls so long parameter lists cannot push the Import button off-screen.
            var root = new ScrollView(ScrollViewMode.Vertical) { style = { flexGrow = 1 } };
            window.Add(root);

            var exportRow = new VisualElement { style = { flexDirection = FlexDirection.Row } };
            _exportField = new TextField("Export folder") { style = { flexGrow = 1 }, isDelayed = true };
            var browse = new Button(BrowseExportFolder) { text = "Browse..." };
            exportRow.Add(_exportField);
            exportRow.Add(browse);
            root.Add(exportRow);

            _targetField = new TextField("Target folder");
            root.Add(_targetField);

            _tableField = new ObjectField("Mapping table")
            {
                objectType = typeof(PortageMappingTable),
                allowSceneObjects = false,
            };
            root.Add(_tableField);
            root.Add(new HelpBox("Optional. When empty, the built-in default mapping rules are used.", HelpBoxMessageType.Info));

            _mappingRoot = new VisualElement { style = { marginTop = 6 } };
            root.Add(_mappingRoot);

            _importButton = new Button(RunImport) { text = "Import" };
            _importButton.style.marginTop = 6;
            _importButton.style.height = 26;
            root.Add(_importButton);

            _statusLabel = new Label();
            _statusLabel.style.marginTop = 6;
            root.Add(_statusLabel);

            _resultRoot = new VisualElement { style = { flexGrow = 1 } };
            root.Add(_resultRoot);

            // Restore remembered values before registering callbacks.
            string exportFolder = EditorPrefs.GetString(ExportFolderKey, "");
            _exportField.SetValueWithoutNotify(exportFolder);
            string tablePath = EditorPrefs.GetString(TablePathKey, "");
            if (!string.IsNullOrEmpty(tablePath))
                _tableField.SetValueWithoutNotify(AssetDatabase.LoadAssetAtPath<PortageMappingTable>(tablePath));
            UpdateAutoTarget(exportFolder);

            _exportField.RegisterValueChangedCallback(e => OnExportFolderChanged(e.newValue));
            _targetField.RegisterValueChangedCallback(e => _targetEditedByUser = e.newValue != _autoTarget);
            _tableField.RegisterValueChangedCallback(e =>
            {
                string path = e.newValue != null ? AssetDatabase.GetAssetPath(e.newValue) : "";
                EditorPrefs.SetString(TablePathKey, path);
                RebuildMappingSection();
            });

            RefreshImportButton();
            RebuildMappingSection();
        }

        void BrowseExportFolder()
        {
            string start = Directory.Exists(_exportField.value) ? _exportField.value : "";
            string selected = EditorUtility.OpenFolderPanel("Select Portage export folder", start, "");
            if (!string.IsNullOrEmpty(selected))
                _exportField.value = selected;
        }

        void OnExportFolderChanged(string folder)
        {
            EditorPrefs.SetString(ExportFolderKey, folder);
            UpdateAutoTarget(folder);
            RefreshImportButton();
            RebuildMappingSection();
        }

        void OnDestroy()
        {
            if (_defaultTable != null)
                DestroyImmediate(_defaultTable);
        }

        void UpdateAutoTarget(string exportFolder)
        {
            string name = string.IsNullOrEmpty(exportFolder)
                ? ""
                : Path.GetFileName(exportFolder.TrimEnd('/', '\\'));
            _autoTarget = string.IsNullOrEmpty(name) ? "Assets/Portage" : "Assets/Portage/" + name;

            // Keep following the export folder name until the user types their own target.
            if (!_targetEditedByUser || string.IsNullOrEmpty(_targetField.value))
            {
                _targetEditedByUser = false;
                _targetField.SetValueWithoutNotify(_autoTarget);
            }
        }

        void RefreshImportButton()
        {
            bool valid = !string.IsNullOrEmpty(_exportField.value)
                         && File.Exists(Path.Combine(_exportField.value, "manifest.json"));
            _importButton.SetEnabled(valid);
            _statusLabel.text = valid || string.IsNullOrEmpty(_exportField.value)
                ? ""
                : "manifest.json was not found in the export folder.";
        }

        PortageMappingTable ActiveTable()
        {
            if (_tableField.value is PortageMappingTable assigned)
                return assigned;

            if (_defaultTable == null)
            {
                _defaultTable = PortageMappingTable.CreateDefault();
                _defaultTable.hideFlags = HideFlags.DontSave;
            }

            return _defaultTable;
        }

        // Rebuilds the "Parameter mapping" section from the manifest in the export folder.
        void RebuildMappingSection()
        {
            _mappingRoot.Clear();
            _manifest = null;
            _mapping = null;

            string manifestPath = Path.Combine(_exportField.value ?? "", "manifest.json");
            if (string.IsNullOrEmpty(_exportField.value) || !File.Exists(manifestPath))
                return;

            try
            {
                _manifest = PortageManifest.Load(manifestPath);
            }
            catch (PortageManifestException e)
            {
                _mappingRoot.Add(new HelpBox(e.Message, HelpBoxMessageType.Error));
                return;
            }

            _mapping = PortageMappingFile.Load(_exportField.value);

            _mappingRoot.Add(new Label("Parameter mapping") { style = { unityFontStyleAndWeight = FontStyle.Bold } });
            if (!string.IsNullOrEmpty(_mapping.LoadError))
                _mappingRoot.Add(new HelpBox(_mapping.LoadError + " It will be replaced when you change a choice.", HelpBoxMessageType.Warning));
            _mappingRoot.Add(new Label("Changes are saved to " + PortageMappingFile.FileName + " in the export folder immediately.")
            {
                style = { color = new Color(0.6f, 0.6f, 0.6f), whiteSpace = WhiteSpace.Normal },
            });

            var occlusionToggle = new Toggle("Apply ambient occlusion")
            {
                value = _mapping.applyOcclusion,
                tooltip = "Assign the occlusion channel of mask textures to the materials. Off by default: Unreal " +
                          "mostly ignores material ambient occlusion under dynamic lighting, so applying it can make " +
                          "a model darker in Unity than it is in Unreal.",
            };
            occlusionToggle.RegisterValueChangedCallback(changed =>
            {
                _mapping.applyOcclusion = changed.newValue;
                try
                {
                    _mapping.Save(_exportField.value);
                }
                catch (Exception e)
                {
                    Debug.LogWarning($"[Portage] Could not save {PortageMappingFile.FileName}: {e.Message}");
                }
            });
            _mappingRoot.Add(occlusionToggle);

            var groups = new List<Group>();
            foreach (PortageManifest.MaterialEntry entry in _manifest.materials)
            {
                if (!entry.convertible)
                    continue;

                string key = PortageMappingFile.KeyOf(entry);
                Group group = groups.Find(g => g.Key == key);
                if (group == null)
                {
                    group = new Group { Key = key };
                    groups.Add(group);
                }

                group.Materials.Add(entry);
            }

            if (groups.Count == 0)
            {
                _mappingRoot.Add(new Label("No convertible materials."));
                return;
            }

            foreach (Group group in groups)
                AddGroup(group, groups.Count <= 3);
        }

        void ResolveGroup(Group group)
        {
            PortageMappingTable table = ActiveTable();
            group.Resolutions.Clear();
            foreach (PortageManifest.MaterialEntry entry in group.Materials)
                group.Resolutions.Add(PortageMappingResolver.Resolve(entry, _manifest, table, _mapping));
        }

        void OnChoiceChanged(Group group)
        {
            try
            {
                _mapping.Save(_exportField.value);
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[Portage] Could not save {PortageMappingFile.FileName}: {e.Message}");
            }

            ResolveGroup(group);
            foreach (Action refresh in group.Refreshers)
                refresh();
        }

        void AddGroup(Group group, bool expanded)
        {
            ResolveGroup(group);

            string assetName = group.Key.Substring(group.Key.LastIndexOf('.') + 1);
            string count = group.Materials.Count == 1 ? "1 material" : group.Materials.Count + " materials";
            var foldout = new Foldout { text = $"{assetName} ({count})", value = expanded, tooltip = group.Key };
            _mappingRoot.Add(foldout);

            var reset = new Button(() =>
            {
                _mapping.ClearMaterial(group.Key);
                OnChoiceChanged(group);
            })
            {
                text = "Reset to automatic",
                tooltip = "Removes the saved choices of this base material.",
            };
            reset.style.alignSelf = Align.FlexStart;
            foldout.Add(reset);

            // Distinct parameters over all materials of the group; the first material that has a value is the example.
            var textures = new List<PortageManifest.TextureParameter>();
            var scalars = new List<PortageManifest.ScalarParameter>();
            var vectors = new List<PortageManifest.VectorParameter>();
            foreach (PortageManifest.MaterialEntry entry in group.Materials)
            {
                foreach (PortageManifest.TextureParameter parameter in entry.textureParameters)
                {
                    int i = textures.FindIndex(p => p.name == parameter.name);
                    if (i < 0)
                        textures.Add(parameter);
                    else if (string.IsNullOrEmpty(textures[i].texture) && !string.IsNullOrEmpty(parameter.texture))
                        textures[i] = parameter;
                }

                foreach (PortageManifest.VectorParameter parameter in entry.vectorParameters)
                {
                    if (!vectors.Exists(p => p.name == parameter.name))
                        vectors.Add(parameter);
                }

                foreach (PortageManifest.ScalarParameter parameter in entry.scalarParameters)
                {
                    if (!scalars.Exists(p => p.name == parameter.name))
                        scalars.Add(parameter);
                }
            }

            AddSectionHeader(foldout, "Textures", textures.Count);
            foreach (PortageManifest.TextureParameter parameter in textures)
                AddTextureRow(foldout, group, parameter);

            AddSectionHeader(foldout, "Vectors", vectors.Count);
            foreach (PortageManifest.VectorParameter parameter in vectors)
                AddVectorRow(foldout, group, parameter);

            if (scalars.Count > 0)
            {
                var scalarFoldout = new Foldout { text = $"Scalars ({scalars.Count})", value = false };
                foldout.Add(scalarFoldout);
                foreach (PortageManifest.ScalarParameter parameter in scalars)
                    AddScalarRow(scalarFoldout, group, parameter);
            }
        }

        static void AddSectionHeader(VisualElement parent, string title, int count)
        {
            if (count == 0)
                return;
            parent.Add(new Label($"{title} ({count})") { style = { unityFontStyleAndWeight = FontStyle.Bold, marginTop = 4 } });
        }

        static VisualElement NewRow()
        {
            return new VisualElement
            {
                style = { flexDirection = FlexDirection.Row, flexWrap = Wrap.Wrap, alignItems = Align.Center, marginBottom = 2 },
            };
        }

        static Label NewCell(string text, float width, string tooltip = null)
        {
            return new Label(text)
            {
                tooltip = tooltip ?? text,
                style =
                {
                    width = width, flexShrink = 0, marginRight = 6, overflow = Overflow.Hidden,
                    textOverflow = TextOverflow.Ellipsis, whiteSpace = WhiteSpace.NoWrap,
                },
            };
        }

        static Label NewSourceLabel()
        {
            return new Label { style = { color = new Color(0.55f, 0.55f, 0.55f), width = 56, flexShrink = 0 } };
        }

        static string SourceText(MappingSource source)
        {
            switch (source)
            {
                case MappingSource.Saved: return "saved";
                case MappingSource.Table: return "table";
                case MappingSource.Guess: return "guessed";
                default: return "";
            }
        }

        static int IndexOf(DropdownField field)
        {
            return field.choices.IndexOf(field.value);
        }

        void AddTextureRow(VisualElement parent, Group group, PortageManifest.TextureParameter sample)
        {
            string name = sample.name;
            PortageManifest.TextureEntry texture = _manifest.FindTexture(sample.texture);

            VisualElement row = NewRow();
            row.Add(NewCell(name, 170));
            row.Add(NewCell(texture != null ? texture.name : "(no texture)", 120, sample.texture));

            var choices = new List<string> { NotMapped };
            foreach (TextureUsage usage in UsageOptions)
                choices.Add(usage.ToString());
            var usageField = new DropdownField(choices, 0) { style = { width = 110, marginRight = 6 } };
            row.Add(usageField);

            string[] channelNames = Enum.GetNames(typeof(MaskChannel));
            var channelRow = new VisualElement { style = { flexDirection = FlexDirection.Row } };
            var channelFields = new DropdownField[4];
            string[] channelLabels = { "R", "G", "B", "A" };
            for (int i = 0; i < 4; i++)
            {
                var field = new DropdownField(channelLabels[i], new List<string>(channelNames), 0)
                {
                    style = { width = 100, marginRight = 4 },
                };
                field.labelElement.style.minWidth = 14;
                field.labelElement.style.width = 14;
                channelFields[i] = field;
                channelRow.Add(field);
            }

            row.Add(channelRow);

            Label source = NewSourceLabel();
            row.Add(source);
            parent.Add(row);

            void Commit()
            {
                int index = IndexOf(usageField);
                if (index <= 0)
                {
                    _mapping.RemoveTexture(group.Key, name);
                }
                else
                {
                    _mapping.SetTexture(group.Key, name, UsageOptions[index - 1],
                        (MaskChannel)IndexOf(channelFields[0]), (MaskChannel)IndexOf(channelFields[1]),
                        (MaskChannel)IndexOf(channelFields[2]), (MaskChannel)IndexOf(channelFields[3]));
                }

                OnChoiceChanged(group);
            }

            void Refresh()
            {
                TextureDecision decision = null;
                foreach (PortageMappingResolution resolution in group.Resolutions)
                {
                    decision = resolution.FindTexture(name);
                    if (decision != null)
                        break;
                }

                bool mapped = decision != null && decision.IsMapped;
                usageField.SetValueWithoutNotify(mapped ? decision.Usage.ToString() : NotMapped);
                channelRow.style.display = mapped && decision.Usage == TextureUsage.Mask ? DisplayStyle.Flex : DisplayStyle.None;
                if (mapped)
                {
                    channelFields[0].SetValueWithoutNotify(decision.Red.ToString());
                    channelFields[1].SetValueWithoutNotify(decision.Green.ToString());
                    channelFields[2].SetValueWithoutNotify(decision.Blue.ToString());
                    channelFields[3].SetValueWithoutNotify(decision.Alpha.ToString());
                }

                source.text = mapped ? SourceText(decision.Source) : "";
            }

            usageField.RegisterValueChangedCallback(_ => Commit());
            foreach (DropdownField field in channelFields)
                field.RegisterValueChangedCallback(_ => Commit());

            group.Refreshers.Add(Refresh);
            Refresh();
        }

        void AddVectorRow(VisualElement parent, Group group, PortageManifest.VectorParameter sample)
        {
            string name = sample.name;
            Color c = sample.ToColor();
            string example = $"({c.r:0.##}, {c.g:0.##}, {c.b:0.##}, {c.a:0.##})";

            VisualElement row = NewRow();
            row.Add(NewCell(name, 170));
            row.Add(NewCell(example, 120));
            var field = new DropdownField(new List<string> { Ignore }, 0) { style = { width = 170, marginRight = 6 } };
            row.Add(field);
            Label source = NewSourceLabel();
            row.Add(source);
            parent.Add(row);

            void Refresh()
            {
                VectorDecision decision = null;
                foreach (PortageMappingResolution resolution in group.Resolutions)
                {
                    decision = resolution.FindVector(name);
                    if (decision != null)
                        break;
                }

                string property = decision != null && decision.IsMapped ? decision.Property : "";
                var choices = new List<string> { Ignore };
                choices.AddRange(VectorProperties);
                if (!string.IsNullOrEmpty(property) && !choices.Contains(property))
                    choices.Add(property);

                field.choices = choices;
                field.SetValueWithoutNotify(string.IsNullOrEmpty(property) ? Ignore : property);
                source.text = decision != null ? SourceText(decision.Source) : "";
            }

            field.RegisterValueChangedCallback(_ =>
            {
                int index = IndexOf(field);
                _mapping.SetVector(group.Key, name, index <= 0 ? "" : field.choices[index]);
                OnChoiceChanged(group);
            });

            group.Refreshers.Add(Refresh);
            Refresh();
        }

        void AddScalarRow(VisualElement parent, Group group, PortageManifest.ScalarParameter sample)
        {
            string name = sample.name;
            var options = new List<ScalarOption>(ScalarOptions);

            VisualElement row = NewRow();
            row.Add(NewCell(name, 170));
            row.Add(NewCell(sample.value.ToString("0.###"), 120));
            var field = new DropdownField(new List<string> { Ignore }, 0) { style = { width = 170, marginRight = 6 } };
            row.Add(field);
            Label source = NewSourceLabel();
            row.Add(source);
            parent.Add(row);

            void Refresh()
            {
                ScalarDecision decision = null;
                foreach (PortageMappingResolution resolution in group.Resolutions)
                {
                    decision = resolution.FindScalar(name);
                    if (decision != null)
                        break;
                }

                options.Clear();
                options.AddRange(ScalarOptions);

                int selected = 0;
                if (decision != null && decision.IsMapped && !decision.IsIgnored)
                {
                    selected = options.FindIndex(o => o.Property == decision.Property && o.Conversion == decision.Conversion);
                    if (selected < 0)
                    {
                        // A property from a mapping table or saved file that is not in the standard list.
                        string label = decision.Conversion == ScalarConversion.OneMinus
                            ? decision.Property + " (1 - value)"
                            : decision.Property;
                        options.Add(new ScalarOption(label, decision.Property, decision.Conversion));
                        selected = options.Count - 1;
                    }
                }

                var labels = new List<string>();
                foreach (ScalarOption option in options)
                    labels.Add(option.Label);
                field.choices = labels;
                field.SetValueWithoutNotify(labels[selected]);
                source.text = decision != null ? SourceText(decision.Source) : "";
            }

            field.RegisterValueChangedCallback(_ =>
            {
                int index = IndexOf(field);
                if (index < 0)
                    return;
                ScalarOption option = options[index];
                _mapping.SetScalar(group.Key, name, option.Property, option.Conversion);
                OnChoiceChanged(group);
            });

            group.Refreshers.Add(Refresh);
            Refresh();
        }


        void RunImport()
        {
            PortageImportReport report = PortageImporter.Import(
                _exportField.value, _targetField.value, _tableField.value as PortageMappingTable);
            report.LogProblemsToConsole();
            ShowReport(report);
        }

        void ShowReport(PortageImportReport report)
        {
            _resultRoot.Clear();
            _statusLabel.text =
                $"{report.CreatedMeshes.Count} mesh(es), {report.CreatedMaterials.Count} material(s), " +
                $"{report.CreatedTextures.Count} texture(s). {report.ErrorCount} error(s), " +
                $"{report.WarningCount} warning(s), {report.InfoCount} info.";

            var scroll = new ScrollView(ScrollViewMode.Vertical) { style = { flexGrow = 1, marginTop = 4 } };
            _resultRoot.Add(scroll);

            AddAssetList(scroll, "Meshes", report.CreatedMeshes);
            AddAssetList(scroll, "Materials", report.CreatedMaterials);
            AddAssetList(scroll, "Textures", report.CreatedTextures);

            var header = new Label("Report") { style = { unityFontStyleAndWeight = FontStyle.Bold, marginTop = 6 } };
            scroll.Add(header);
            foreach (PortageImportReport.Entry entry in report.Entries)
            {
                var row = new VisualElement { style = { flexDirection = FlexDirection.Row, marginBottom = 2 } };
                var severity = new Label(entry.Severity.ToString()) { style = { width = 60, color = SeverityColor(entry.Severity) } };
                // Asset ids are long Unreal object paths; show the asset name and keep the id as tooltip.
                string assetName = entry.Asset.Substring(entry.Asset.LastIndexOf('.') + 1);
                var asset = new Label(assetName)
                {
                    style =
                    {
                        width = 120, flexShrink = 0, marginRight = 6, overflow = Overflow.Hidden,
                        textOverflow = TextOverflow.Ellipsis, whiteSpace = WhiteSpace.NoWrap,
                    },
                };
                var message = new Label(entry.Message) { style = { flexGrow = 1, flexShrink = 1, whiteSpace = WhiteSpace.Normal } };
                asset.tooltip = entry.Asset;
                row.Add(severity);
                row.Add(asset);
                row.Add(message);
                scroll.Add(row);
            }

            if (report.Entries.Count == 0)
                scroll.Add(new Label("No messages."));
        }

        static void AddAssetList(VisualElement parent, string title, System.Collections.Generic.List<string> paths)
        {
            if (paths.Count == 0)
                return;

            parent.Add(new Label(title) { style = { unityFontStyleAndWeight = FontStyle.Bold, marginTop = 6 } });
            foreach (string path in paths)
            {
                string captured = path;
                var link = new Label(captured) { tooltip = "Click to ping in the Project window" };
                link.style.color = new Color(0.4f, 0.7f, 1f);
                link.RegisterCallback<ClickEvent>(_ =>
                {
                    Object asset = AssetDatabase.LoadMainAssetAtPath(captured);
                    if (asset != null)
                        EditorGUIUtility.PingObject(asset);
                });
                parent.Add(link);
            }
        }

        static Color SeverityColor(PortageSeverity severity)
        {
            switch (severity)
            {
                case PortageSeverity.Error: return new Color(1f, 0.35f, 0.35f);
                case PortageSeverity.Warning: return new Color(1f, 0.8f, 0.3f);
                default: return new Color(0.7f, 0.7f, 0.7f);
            }
        }
    }
}
