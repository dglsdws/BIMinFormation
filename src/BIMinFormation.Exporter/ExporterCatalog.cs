using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using BIMinFormation.Ids;

namespace BIMinFormation.Exporter
{
    public enum ParameterScope
    {
        Instance,
        Type,
    }

    /// <summary>
    /// Shared parameter Revit installs for a standard Pset/Qto (e.g. <c>Pset_WallCommon.IsExternal</c>, or
    /// <c>Pset_WallCommon.IsExternal[Type]</c> on the type).
    /// </summary>
    public sealed class StandardParameter
    {
        public ParameterScope Scope { get; }
        public string PropertySet { get; }
        public string Property { get; }

        /// <summary>Parameter type in the Revit file (e.g. <c>YESNO</c>, <c>LENGTH</c>, <c>TEXT</c>).</summary>
        public string RevitDataType { get; }

        /// <summary>IFC type from the description (e.g. <c>IfcBoolean</c>, <c>PEnum_ElementStatus</c>); <c>null</c> if empty.</summary>
        public string? IfcType { get; }

        public StandardParameter(ParameterScope scope, string propertySet, string property, string revitDataType, string? ifcType)
        {
            Scope = scope;
            PropertySet = propertySet;
            Property = property;
            RevitDataType = revitDataType;
            IfcType = ifcType;
        }

        public string ParameterName => PropertySet + "." + Property + (Scope == ParameterScope.Type ? "[Type]" : "");

        public override string ToString() => ParameterName;
    }

    /// <summary>
    /// What the IFC exporter of a Revit version can do: IFC versions in the API, types accepted in the PropertySets file,
    /// parameters of the standard Psets/Qtos and entity lists per MVD. Generated from the Revit installation by
    /// <c>tools/exporter-catalog</c> and embedded in the DLL.
    /// </summary>
    public sealed class ExporterCatalog
    {
        private static readonly ConcurrentDictionary<int, ExporterCatalog> Cache = new ConcurrentDictionary<int, ExporterCatalog>();

        private readonly List<string> _ifcVersions = new List<string>();
        private readonly List<string> _schemaFiles = new List<string>();
        private readonly HashSet<string> _propertyTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, StandardParameter> _parameters = new Dictionary<string, StandardParameter>(StringComparer.Ordinal);
        private readonly HashSet<string> _standardSets = new HashSet<string>(StringComparer.Ordinal);
        private readonly Dictionary<string, HashSet<string>> _mvdEntities = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        private readonly Dictionary<string, HashSet<string>> _mvdSets = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);

        public int Year { get; private set; }

        /// <summary>Version of Revit.IFC.Export.dll (e.g. <c>23.0.1.318</c>).</summary>
        public string ExporterVersion { get; private set; } = "";

        /// <summary>Values of the <c>Autodesk.Revit.DB.IFCVersion</c> enum (e.g. <c>IFC2x3CV2</c>, <c>IFC4RV</c>).</summary>
        public IReadOnlyList<string> IfcVersions => _ifcVersions;

        /// <summary>Installed EXPRESS schemas (e.g. <c>IFC4X3_RC4</c>); having the file does not mean it can export them.</summary>
        public IReadOnlyList<string> SchemaFiles => _schemaFiles;

        /// <summary>Values of the <c>PropertyType</c> enum: the types accepted in the PropertySets file.</summary>
        public IReadOnlyCollection<string> PropertyTypes => _propertyTypes;

        public IEnumerable<StandardParameter> StandardParameters => _parameters.Values;

        public IReadOnlyCollection<string> MvdNames => _mvdEntities.Keys;

        public static IReadOnlyList<int> AvailableYears { get; } = new[] { 2023, 2025, 2026, 2027 };

        public static ExporterCatalog For(int year) => Cache.GetOrAdd(year, Load);

        public static ExporterCatalog Parse(TextReader reader)
        {
            var catalog = new ExporterCatalog();
            catalog.Read(reader);
            return catalog;
        }

        public bool IsPropertyType(string name) => _propertyTypes.Contains(name);

        /// <summary>The API of this version can export the schema (from the <c>IFCVersion</c> list).</summary>
        public bool CanExport(IfcSchemaVersion schema)
        {
            switch (schema)
            {
                case IfcSchemaVersion.Ifc2X3:
                    return _ifcVersions.Any(v => v.StartsWith("IFC2x3", StringComparison.OrdinalIgnoreCase));
                case IfcSchemaVersion.Ifc4:
                    return _ifcVersions.Any(v => v.StartsWith("IFC4", StringComparison.OrdinalIgnoreCase)
                                                 && !v.StartsWith("IFC4x3", StringComparison.OrdinalIgnoreCase));
                default:
                    return _ifcVersions.Any(v => v.StartsWith("IFC4x3", StringComparison.OrdinalIgnoreCase));
            }
        }

        /// <summary>
        /// The IFC 4.3 of this version writes quantities: from 2026 on (IFC4x3 Design Transfer View). 2025 only has the
        /// Reference View, which writes no quantities (checked in 25.5.10), and 2023.1/2024 only the experimental 4.3.
        /// </summary>
        public bool Ifc4x3WithQuantities => Year >= 2026 && CanExport(IfcSchemaVersion.Ifc4X3Add2);

        /// <summary>Revit has parameters for this standard Pset/Qto.</summary>
        public bool HasStandardSet(string propertySet) => _standardSets.Contains(propertySet);

        public StandardParameter? FindStandardParameter(string propertySet, string property, ParameterScope scope) =>
            _parameters.TryGetValue(Key(scope, propertySet, property), out var parameter) ? parameter : null;

        public bool MvdHasEntity(string mvd, string entity) =>
            _mvdEntities.TryGetValue(mvd, out var set) && set.Contains(entity);

        public bool MvdHasPropertySet(string mvd, string propertySet) =>
            _mvdSets.TryGetValue(mvd, out var set) && set.Contains(propertySet);

        private static string Key(ParameterScope scope, string propertySet, string property) =>
            (scope == ParameterScope.Type ? "T" : "I") + "\t" + propertySet + "\t" + property;

        private static ExporterCatalog Load(int year)
        {
            string resource = "BIMinFormation.Exporter.Catalog.revit-" + year + ".txt";
            using var stream = typeof(ExporterCatalog).GetTypeInfo().Assembly.GetManifestResourceStream(resource)
                ?? throw new ArgumentOutOfRangeException(nameof(year), "Sem catálogo do exportador para o Revit " + year);
            using var reader = new StreamReader(stream);
            return Parse(reader);
        }

        private void Read(TextReader reader)
        {
            string? line;
            while ((line = reader.ReadLine()) != null)
            {
                if (line.Length == 0 || line[0] == '#')
                {
                    continue;
                }

                string[] f = line.Split('\t');
                switch (f[0])
                {
                    case "EXPORTER":
                        Year = int.Parse(f[1], System.Globalization.CultureInfo.InvariantCulture);
                        ExporterVersion = f[2];
                        break;
                    case "IFCVERSION":
                        _ifcVersions.Add(f[1]);
                        break;
                    case "SCHEMAFILE":
                        _schemaFiles.Add(f[1]);
                        break;
                    case "PROPERTYTYPE":
                        _propertyTypes.Add(f[1]);
                        break;
                    case "PARAM":
                        var scope = f[1] == "T" ? ParameterScope.Type : ParameterScope.Instance;
                        var parameter = new StandardParameter(scope, f[2], f[3], f[4], f[5] == "-" ? null : f[5]);
                        _parameters[Key(scope, f[2], f[3])] = parameter;
                        _standardSets.Add(f[2]);
                        break;
                    case "MVDENTITY":
                        Set(_mvdEntities, f[1]).Add(f[2]);
                        break;
                    case "MVDPSET":
                        Set(_mvdSets, f[1]).Add(f[2]);
                        break;
                }
            }
        }

        private static HashSet<string> Set(Dictionary<string, HashSet<string>> map, string key)
        {
            if (!map.TryGetValue(key, out var set))
            {
                set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                map[key] = set;
            }

            return set;
        }
    }
}
