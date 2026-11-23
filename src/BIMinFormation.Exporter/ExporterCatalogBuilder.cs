using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace BIMinFormation.Exporter
{
    /// <summary>
    /// Builds the exporter catalog text from a Revit installation. Used by the command-line generator (which reads the
    /// enums from the DLL metadata) and by the plugin inside Revit (which reads them by reflection), so the catalog matches
    /// the version in use (e.g. 2023.1 exports IFC 4.3, 2023.0 does not).
    /// </summary>
    public static class ExporterCatalogBuilder
    {
        public const string InstanceParametersFile = "IFC Shared Parameters-RevitIFCBuiltIn_ALL.txt";
        public const string TypeParametersFile = "IFC Shared Parameters-RevitIFCBuiltIn-Type_ALL.txt";

        public static List<string> Lines(string year, string exporterVersion, IEnumerable<string> ifcVersions,
            IEnumerable<string> propertyTypes, string revitFolder)
        {
            var lines = new List<string>
            {
                "# Catálogo do exportador IFC do Revit " + year
                + " gerado por tools/exporter-catalog a partir da instalação. Não editar.",
                "EXPORTER\t" + year + "\t" + exporterVersion,
            };
            lines.AddRange(ifcVersions.Select(v => "IFCVERSION\t" + v));

            foreach (string file in Directory.GetFiles(revitFolder, "*.txexp").Select(f => Path.GetFileNameWithoutExtension(f))
                         .OrderBy(f => f, StringComparer.Ordinal))
            {
                // IFC4X3_RC4_22.12_16 → IFC4X3_RC4
                int last = file.LastIndexOf('_');
                int cut = last > 0 ? file.LastIndexOf('_', last - 1) : -1;
                lines.Add("SCHEMAFILE\t" + (cut > 0 ? file.Substring(0, cut) : file));
            }

            lines.AddRange(propertyTypes.Select(t => "PROPERTYTYPE\t" + t));
            AddSharedParameters(lines, Path.Combine(revitFolder, InstanceParametersFile), "I");
            AddSharedParameters(lines, Path.Combine(revitFolder, TypeParametersFile), "T");
            return lines;
        }

        public static string Text(IEnumerable<string> lines) => string.Join("\n", lines) + "\n";

        /// <summary>
        /// Revit shared parameter file: GROUP id name; PARAM guid name type category group visible description. The name is
        /// <c>Pset.Property</c> (with <c>[Type]</c> in the type file) and the description carries the IFC type.
        /// </summary>
        private static void AddSharedParameters(List<string> lines, string path, string scope)
        {
            if (!File.Exists(path))
            {
                return;
            }

            var groups = new Dictionary<string, string>(StringComparer.Ordinal);
            var rows = new List<string>();
            foreach (string raw in File.ReadLines(path))
            {
                string[] f = raw.Split('\t');
                if (f[0] == "GROUP" && f.Length >= 3)
                {
                    groups[f[1]] = f[2];
                }
                else if (f[0] == "PARAM" && f.Length >= 8)
                {
                    string group = groups.TryGetValue(f[5], out var g) ? g : "";
                    string name = f[2];
                    if (name.EndsWith("[Type]", StringComparison.Ordinal))
                    {
                        name = name.Substring(0, name.Length - "[Type]".Length);
                    }

                    string property = name.StartsWith(group + ".", StringComparison.Ordinal) ? name.Substring(group.Length + 1) : name;
                    rows.Add(string.Join("\t", "PARAM", scope, group, property, f[3], f[7].Trim().Length == 0 ? "-" : f[7].Trim()));
                }
            }

            lines.AddRange(rows.Distinct().OrderBy(r => r, StringComparer.Ordinal));
        }
    }
}
