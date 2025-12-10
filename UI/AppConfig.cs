using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace GalaxyAngel2Localization.UI
{
    internal sealed class AppConfig
    {
        public List<string> Projects { get; } = new();
        public string CurrentProject { get; set; } = string.Empty;
        public List<string> ExtractExtensions { get; } = new();
        public Dictionary<string, List<string>> PrePackCommandsPerProject { get; } =
            new(StringComparer.OrdinalIgnoreCase);
        public string Language { get; set; } = string.Empty;

        bool _languageWasInFile;

        public static AppConfig Load(string path)
        {
            var cfg = new AppConfig();

            if (!File.Exists(path))
            {
                AddDefaultExts(cfg);
                cfg.Language = CultureInfo.CurrentUICulture.Name;
                return cfg;
            }

            string? section = null;

            foreach (var raw in File.ReadAllLines(path))
            {
                var line = raw.Trim();
                if (line.Length == 0 || line.StartsWith("#") || line.StartsWith(";"))
                    continue;

                if (line.StartsWith("[") && line.EndsWith("]"))
                {
                    section = line[1..^1].Trim();
                    continue;
                }

                int eq = line.IndexOf('=');
                if (eq <= 0) continue;

                var key = line[..eq].Trim();
                var value = line[(eq + 1)..].Trim();

                if (section == null) continue;

                if (section.Equals("Projects", StringComparison.OrdinalIgnoreCase))
                {
                    if (key.Equals("Names", StringComparison.OrdinalIgnoreCase))
                        ParseList(value, cfg.Projects);
                    else if (key.Equals("Current", StringComparison.OrdinalIgnoreCase))
                        cfg.CurrentProject = value;
                }
                else if (section.Equals("Extract", StringComparison.OrdinalIgnoreCase))
                {
                    if (key.Equals("Extensions", StringComparison.OrdinalIgnoreCase))
                        ParseList(value, cfg.ExtractExtensions);
                }
                else if (section.Equals("UI", StringComparison.OrdinalIgnoreCase))
                {
                    if (key.Equals("Language", StringComparison.OrdinalIgnoreCase))
                    {
                        cfg.Language = value;
                        cfg._languageWasInFile = true;
                    }
                }
                else if (section.StartsWith("PrePack.", StringComparison.OrdinalIgnoreCase))
                {
                    var proj = section["PrePack.".Length..].Trim();
                    if (proj.Length == 0) continue;

                    if (key.Equals("Commands", StringComparison.OrdinalIgnoreCase))
                    {
                        if (!cfg.PrePackCommandsPerProject.TryGetValue(proj, out var list))
                        {
                            list = new List<string>();
                            cfg.PrePackCommandsPerProject[proj] = list;
                        }
                        ParseList(value, list);
                    }
                }
            }

            if (cfg.ExtractExtensions.Count == 0)
                AddDefaultExts(cfg);

            if (!cfg._languageWasInFile)
                cfg.Language = CultureInfo.CurrentUICulture.Name;

            return cfg;
        }

        public void Save(string path)
        {
            var lines = new List<string>
            {
                "[Projects]",
                "Names = [" + string.Join(", ", Projects) + "]",
                "Current = " + CurrentProject,
                "",
                "[Extract]",
                "Extensions = [" + string.Join(", ", ExtractExtensions) + "]",
                "",
                "[UI]",
                "Language = " + Language,
                ""
            };

            foreach (var kv in PrePackCommandsPerProject)
            {
                if (kv.Value.Count == 0) continue;
                lines.Add("[PrePack." + kv.Key + "]");
                lines.Add("Commands = [" + string.Join(", ", kv.Value) + "]");
                lines.Add("");
            }

            File.WriteAllLines(path, lines);
        }

        static void ParseList(string value, List<string> output)
        {
            var s = value.Trim();
            if (s.StartsWith("[") && s.EndsWith("]") && s.Length >= 2)
                s = s[1..^1];

            foreach (var part in s.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var item = part.Trim();
                if (item.Length > 0)
                    output.Add(item);
            }
        }

        static void AddDefaultExts(AppConfig cfg)
        {
            cfg.ExtractExtensions.AddRange(new[]
            {
                "tbl", "txt", "scn", "isb", "asb", "dat", "agi"
            });
        }
    }
}