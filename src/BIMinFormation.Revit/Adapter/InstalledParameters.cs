using System;
using System.Collections.Generic;
using System.IO;
using Autodesk.Revit.DB;
using BIMinFormation.Ids;

namespace BIMinFormation.Revit.Adapter
{
    /// <summary>
    /// Shared parameters Revit installs for the standard Psets/Qtos (<c>IFC Shared Parameters-RevitIFCBuiltIn*_ALL.txt</c>
    /// files in the Revit folder). The plugin creates the standard parameter with the Autodesk GUID, so the same data always
    /// uses the same parameter in any project.
    /// </summary>
    public sealed class InstalledParameters
    {
        private readonly Dictionary<string, (Guid Guid, string DataType)> _byName =
            new Dictionary<string, (Guid, string)>(StringComparer.Ordinal);

        public static string RevitFolder => Path.GetDirectoryName(typeof(Document).Assembly.Location)!;

        public static InstalledParameters Load(string? folder = null)
        {
            var result = new InstalledParameters();
            folder ??= RevitFolder;
            foreach (string file in new[] { "IFC Shared Parameters-RevitIFCBuiltIn_ALL.txt", "IFC Shared Parameters-RevitIFCBuiltIn-Type_ALL.txt" })
            {
                string path = Path.Combine(folder, file);
                if (!File.Exists(path))
                {
                    continue;
                }

                foreach (string line in File.ReadLines(path))
                {
                    string[] f = line.Split('\t');
                    if (f.Length >= 4 && f[0] == "PARAM" && Guid.TryParse(f[1], out Guid guid))
                    {
                        result._byName[f[2]] = (guid, f[3]);
                    }
                }
            }

            return result;
        }

        public int Count => _byName.Count;

        /// <summary>Request to create the standard parameter (e.g. <c>Pset_WallCommon.IsExternal</c>), or <c>null</c>.</summary>
        public ParameterRequest? Request(string parameterName)
        {
            if (!_byName.TryGetValue(parameterName, out var entry))
            {
                return null;
            }

            var spec = SpecOf(entry.DataType);
            return spec == null
                ? null
                : new ParameterRequest(parameterName, spec, entry.Guid, parameterName.EndsWith("[Type]", StringComparison.Ordinal));
        }

        private static ForgeTypeId? SpecOf(string revitDataType)
        {
            switch (revitDataType)
            {
                case "TEXT":
                case "MULTILINETEXT":
                case "URL":
                    return SpecTypeId.String.Text;
                case "YESNO":
                    return SpecTypeId.Boolean.YesNo;
                case "INTEGER":
                    return SpecTypeId.Int.Integer;
                case "NUMBER":
                    return SpecTypeId.Number;
                case "LENGTH":
                    return SpecTypeId.Length;
                case "AREA":
                    return SpecTypeId.Area;
                case "VOLUME":
                    return SpecTypeId.Volume;
                case "ANGLE":
                    return SpecTypeId.Angle;
                case "MASS_DENSITY":
                    return SpecTypeId.MassDensity;
                default:
                    return null;
            }
        }
    }
}
