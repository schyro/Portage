using System.IO;
using UnityEditor;
using UnityEditor.UIElements;
using UnityEngine;
using UnityEngine.UIElements;

namespace Portage
{
    /// <summary>Editor window for importing a Portage export folder.</summary>
    public sealed class PortageImportWindow : EditorWindow
    {
        const string ExportFolderKey = "Portage.ExportFolder";
        const string TablePathKey = "Portage.MappingTablePath";

        TextField _exportField;
        TextField _targetField;
        ObjectField _tableField;
        Button _importButton;
        Label _statusLabel;
        VisualElement _resultRoot;

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
            VisualElement root = rootVisualElement;
            root.style.paddingLeft = 8;
            root.style.paddingRight = 8;
            root.style.paddingTop = 8;
            root.style.paddingBottom = 8;

            var exportRow = new VisualElement { style = { flexDirection = FlexDirection.Row } };
            _exportField = new TextField("Export folder") { style = { flexGrow = 1 } };
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
            });

            RefreshImportButton();
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

        void RunImport()
        {
            PortageImportReport report = PortageImporter.Import(
                _exportField.value, _targetField.value, _tableField.value as PortageMappingTable);
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
