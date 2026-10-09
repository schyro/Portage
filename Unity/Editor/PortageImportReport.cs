using System;
using System.Collections.Generic;
using UnityEngine;

namespace Portage
{
    /// <summary>Severity of a <see cref="PortageImportReport.Entry"/>.</summary>
    public enum PortageSeverity
    {
        Info,
        Warning,
        Error,
    }

    /// <summary>
    /// Everything that happened during one import: messages with a severity and the assets that
    /// were created or updated. Manifest issues are copied into it so the user sees one list.
    /// </summary>
    public sealed class PortageImportReport
    {
        public sealed class Entry
        {
            public PortageSeverity Severity;

            /// <summary>Stable machine-readable code, for example <c>parameter.unmapped</c>.</summary>
            public string Code = "";

            /// <summary>Id or name of the asset concerned, or empty.</summary>
            public string Asset = "";

            public string Message = "";
        }

        public readonly List<Entry> Entries = new List<Entry>();

        /// <summary>Project-relative paths of materials created or updated.</summary>
        public readonly List<string> CreatedMaterials = new List<string>();

        /// <summary>Project-relative paths of textures created or updated, including packed masks.</summary>
        public readonly List<string> CreatedTextures = new List<string>();

        /// <summary>Project-relative paths of meshes (FBX files) created or updated.</summary>
        public readonly List<string> CreatedMeshes = new List<string>();

        public int InfoCount => Count(PortageSeverity.Info);
        public int WarningCount => Count(PortageSeverity.Warning);
        public int ErrorCount => Count(PortageSeverity.Error);
        public bool HasErrors => ErrorCount > 0;

        public void Info(string code, string asset, string message) => Add(PortageSeverity.Info, code, asset, message);

        public void Warning(string code, string asset, string message) => Add(PortageSeverity.Warning, code, asset, message);

        public void Error(string code, string asset, string message) => Add(PortageSeverity.Error, code, asset, message);

        public void Add(PortageSeverity severity, string code, string asset, string message)
        {
            Entries.Add(new Entry
            {
                Severity = severity,
                Code = code ?? "",
                Asset = asset ?? "",
                Message = message ?? "",
            });
        }

        /// <summary>
        /// Writes every warning and error to the Unity console. Info entries are left out, because
        /// they are numerous and need no action.
        /// </summary>
        public void LogProblemsToConsole()
        {
            foreach (Entry entry in Entries)
            {
                if (entry.Severity == PortageSeverity.Info)
                    continue;

                string text = $"[Portage] {entry.Message} ({entry.Code})";
                if (entry.Severity == PortageSeverity.Error)
                    Debug.LogError(text);
                else
                    Debug.LogWarning(text);
            }
        }

        /// <summary>Copies a manifest issue into the report. Unknown severities become warnings.</summary>
        public void AddManifestIssue(PortageManifest.IssueEntry issue)
        {
            Add(ParseSeverity(issue.severity), issue.code, issue.asset, issue.message);
        }

        public static PortageSeverity ParseSeverity(string severity)
        {
            if (string.Equals(severity, "info", StringComparison.OrdinalIgnoreCase))
                return PortageSeverity.Info;
            if (string.Equals(severity, "error", StringComparison.OrdinalIgnoreCase))
                return PortageSeverity.Error;
            return PortageSeverity.Warning;
        }

        int Count(PortageSeverity severity)
        {
            int count = 0;
            foreach (Entry entry in Entries)
            {
                if (entry.Severity == severity)
                    count++;
            }

            return count;
        }
    }
}
