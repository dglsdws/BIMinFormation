using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace BIMinFormation.Revit
{
    /// <summary>
    /// User preferences in <c>%AppData%\BIMinFormation\settings.txt</c> (key=value, UTF-8). The project profile is kept
    /// in the .rvt.
    /// </summary>
    internal sealed class PluginSettings
    {
        public static string FilePath { get; } = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "BIMinFormation", "settings.txt");

        public string? IdsFolder { get; set; }

        /// <summary>Interface language: <c>auto</c> (Revit's), <c>pt-BR</c> or <c>en</c>.</summary>
        public string Language { get; set; } = Auto;

        public const string Auto = "auto";

        public static PluginSettings Load()
        {
            var settings = new PluginSettings();
            try
            {
                if (!File.Exists(FilePath))
                {
                    return settings;
                }

                foreach (string line in File.ReadAllLines(FilePath, Encoding.UTF8))
                {
                    int separator = line.IndexOf('=');
                    if (separator <= 0)
                    {
                        continue;
                    }

                    string key = line.Substring(0, separator).Trim();
                    string value = line.Substring(separator + 1).Trim();
                    if (key == "idsFolder" && value.Length > 0)
                    {
                        settings.IdsFolder = value;
                    }
                    else if (key == "language" && (value == Auto || value == Ids.Tr.Portuguese || value == Ids.Tr.English))
                    {
                        settings.Language = value;
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Error("Could not read " + FilePath, ex);
            }

            return settings;
        }

        public void Save()
        {
            var lines = new List<string> { "idsFolder=" + (IdsFolder ?? ""), "language=" + Language };
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllLines(FilePath, lines, new UTF8Encoding(false));
        }
    }
}
