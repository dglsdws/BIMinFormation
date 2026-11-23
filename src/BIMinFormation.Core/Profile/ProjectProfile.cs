using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;
using BIMinFormation.Ids;

namespace BIMinFormation.Core.Profile
{
    /// <summary>
    /// Link: the property comes from a parameter Revit already has (the exporter reads it by name, on the instance and on
    /// the type) instead of a new plugin parameter. With <see cref="DerivedMaterial"/> the value is calculated by the plugin
    /// (the element material) and written to the plugin parameter on every Fill In Parameters and Export.
    /// </summary>
    [DataContract]
    public sealed class ParameterMapping
    {
        /// <summary>Source calculated by the plugin: names of the element materials.</summary>
        public const string DerivedMaterial = "@Material";

        public bool IsDerived => Parameter.StartsWith("@", StringComparison.Ordinal);

        [DataMember(Name = "propertySet", Order = 1)]
        public string PropertySet { get; set; } = "";

        [DataMember(Name = "property", Order = 2)]
        public string Property { get; set; } = "";

        /// <summary>Revit parameter name, or <c>BuiltInParameter.XXX</c> for a built-in one.</summary>
        [DataMember(Name = "parameter", Order = 3)]
        public string Parameter { get; set; } = "";

        /// <summary>IFC entity the link is limited to (e.g. <c>IfcCovering</c>); <c>null</c> = all.</summary>
        [DataMember(Name = "entity", Order = 4, EmitDefaultValue = false)]
        public string? Entity { get; set; }

        /// <summary>
        /// Identifier of the Revit built-in parameter (e.g. <c>HOST_AREA_COMPUTED</c>), the same in any language. The name in
        /// <see cref="Parameter"/> is translated: on every load the plugin replaces it with the name in the Revit in use.
        /// </summary>
        [DataMember(Name = "builtIn", Order = 5, EmitDefaultValue = false)]
        public string? BuiltIn { get; set; }

        /// <summary>GUID of the shared parameter (the name may differ in the project); <c>null</c> when it is not shared.</summary>
        [DataMember(Name = "sharedGuid", Order = 6, EmitDefaultValue = false)]
        public string? SharedGuid { get; set; }

        /// <summary>Category of the element where the link was made (e.g. <c>OST_Roofs</c>): where to look up the parameter name.</summary>
        [DataMember(Name = "category", Order = 7, EmitDefaultValue = false)]
        public string? Category { get; set; }
    }

    /// <summary>Changes the Revit spec used for an exporter type (e.g. Power → HvacPower).</summary>
    [DataContract]
    public sealed class SpecOverride
    {
        [DataMember(Name = "propertyType", Order = 1)]
        public string PropertyType { get; set; } = "";

        [DataMember(Name = "revitSpec", Order = 2)]
        public string RevitSpec { get; set; } = "";
    }

    /// <summary>
    /// Project profile: what the whole team uses in the same <c>.rvt</c>. It lives in the document as Extensible Storage
    /// (JSON text) and can be imported/exported as a file. The plugin only reads the <c>.ids</c> files: the profile keeps
    /// paths and never changes the files.
    /// </summary>
    [DataContract]
    public sealed class ProjectProfile
    {
        public const int CurrentVersion = 1;

        [DataMember(Name = "version", Order = 1)]
        public int Version { get; set; } = CurrentVersion;

        /// <summary>Folder of the <c>.ids</c> files.</summary>
        [DataMember(Name = "idsFolder", Order = 2)]
        public string? IdsFolder { get; set; }

        /// <summary>
        /// Active files, relative to the folder (pick files, not only the folder). Empty = every <c>.ids</c> in the folder and
        /// its subfolders.
        /// </summary>
        [DataMember(Name = "idsFiles", Order = 3)]
        public List<string> IdsFiles { get; set; } = new List<string>();

        /// <summary>Target schema (<c>IFC2X3</c>, <c>IFC4</c>, <c>IFC4X3_ADD2</c>); empty = the files' schema.</summary>
        [DataMember(Name = "targetSchema", Order = 4)]
        public string? TargetSchema { get; set; }

        /// <summary>Hides from the Fill In Parameters grid the requirements with no value to fill in (calculated, manual).</summary>
        [DataMember(Name = "hideWhenNoValue", Order = 6)]
        public bool HideWhenNoValue { get; set; } = true;

        /// <summary>Shared parameter file where the plugin creates its parameters.</summary>
        [DataMember(Name = "parametersFile", Order = 7)]
        public string? ParametersFile { get; set; }

        /// <summary>Destination of the standalone PropertySets file (Settings → PropertySets).</summary>
        [DataMember(Name = "propertySetsFile", Order = 8)]
        public string? PropertySetsFile { get; set; }

        /// <summary>Properties written on the type, as <c>Pset/Property</c>.</summary>
        [DataMember(Name = "typeProperties", Order = 10)]
        public List<string> TypeProperties { get; set; } = new List<string>();

        [DataMember(Name = "mappings", Order = 11)]
        public List<ParameterMapping> Mappings { get; set; } = new List<ParameterMapping>();

        /// <summary>
        /// Specifications in use in the model (with some element). The mapping, Fill In Parameters and the PropertySets file use
        /// only these: fewer sets and, in Revit 2025+, fewer type conflicts. <c>null</c> = not computed yet (uses all);
        /// recomputed on every Export and standalone PropertySets file, and the Classification adds the specification it applies.
        /// </summary>
        [DataMember(Name = "specificationsInUse", Order = 13, EmitDefaultValue = false)]
        public List<string>? SpecificationsInUse { get; set; }

        /// <summary>Map every specification in the folder, not only the ones in use.</summary>
        [DataMember(Name = "mapAllSpecifications", Order = 14)]
        public bool MapAllSpecifications { get; set; }

        /// <summary>The specifications that go into the mapping: the ones in use, or all (option, or list not computed yet).</summary>
        public IReadOnlyList<Specification> MappedSpecifications(IReadOnlyList<Specification> all)
        {
            if (MapAllSpecifications || SpecificationsInUse == null) return all;
            var inUse = new HashSet<string>(SpecificationsInUse, StringComparer.Ordinal);
            return all.Where(s => inUse.Contains(s.Name)).ToList();
        }

        [DataMember(Name = "specOverrides", Order = 12)]
        public List<SpecOverride> SpecOverrides { get; set; } = new List<SpecOverride>();

        public IfcSchemaVersion? Schema =>
            TargetSchema != null && IdsParser.TryParseSchema(TargetSchema, out var version) ? version : (IfcSchemaVersion?)null;

        public static string Key(string propertySet, string property) => propertySet + "/" + property;

        public bool IsTypeProperty(string propertySet, string property) =>
            TypeProperties.Contains(Key(propertySet, property));

        /// <summary>Link of the property for the entity: the entity-specific one, otherwise the general one.</summary>
        public ParameterMapping? Link(string propertySet, string property, string? entity = null) =>
            (entity == null ? null : Mappings.FirstOrDefault(m => m.PropertySet == propertySet && m.Property == property
                                                                   && string.Equals(m.Entity, entity, StringComparison.OrdinalIgnoreCase)))
            ?? Mappings.FirstOrDefault(m => m.PropertySet == propertySet && m.Property == property && m.Entity == null);

        /// <summary>Revit parameter the exporter reads for the property; <c>null</c> = plugin parameter.</summary>
        public string? MappedParameter(string propertySet, string property, string? entity = null)
        {
            var link = Link(propertySet, property, entity);
            return link == null || link.IsDerived ? null : link.Parameter;
        }

        /// <summary>Creates, changes or (with a null <paramref name="parameter"/>) removes the link.</summary>
        public void SetLink(string propertySet, string property, string? entity, string? parameter, string? builtIn = null,
            string? sharedGuid = null, string? category = null)
        {
            Mappings.RemoveAll(m => m.PropertySet == propertySet && m.Property == property
                                    && string.Equals(m.Entity, entity, StringComparison.OrdinalIgnoreCase));
            if (!string.IsNullOrEmpty(parameter))
            {
                Mappings.Add(new ParameterMapping
                {
                    PropertySet = propertySet,
                    Property = property,
                    Entity = entity,
                    Parameter = parameter!,
                    BuiltIn = builtIn,
                    SharedGuid = sharedGuid,
                    Category = category,
                });
            }
        }

        /// <summary>Full paths of the active <c>.ids</c> files (without checking that they exist).</summary>
        public IReadOnlyList<string> ResolveFiles()
        {
            if (string.IsNullOrEmpty(IdsFolder))
            {
                return new string[0];
            }

            if (IdsFiles.Count == 0)
            {
                return Directory.Exists(IdsFolder)
                    ? Directory.EnumerateFiles(IdsFolder, "*.ids", SearchOption.AllDirectories)
                        .OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToList()
                    : (IReadOnlyList<string>)new string[0];
            }

            return IdsFiles.Select(f => Path.IsPathRooted(f) ? f : Path.Combine(IdsFolder, f)).ToList();
        }

        public string ToJson()
        {
            var serializer = new DataContractJsonSerializer(typeof(ProjectProfile));
            using var stream = new MemoryStream();
            serializer.WriteObject(stream, this);
            return Encoding.UTF8.GetString(stream.ToArray());
        }

        public static ProjectProfile FromJson(string json)
        {
            var serializer = new DataContractJsonSerializer(typeof(ProjectProfile));
            using var stream = new MemoryStream(Encoding.UTF8.GetBytes(json));
            var profile = (ProjectProfile)serializer.ReadObject(stream)!;
            profile.IdsFiles ??= new List<string>();
            profile.TypeProperties ??= new List<string>();
            profile.Mappings ??= new List<ParameterMapping>();
            profile.SpecOverrides ??= new List<SpecOverride>();
            if (profile.Version > CurrentVersion)
            {
                throw new InvalidDataException(Tr.T("Perfil gravado por uma versão mais nova do plugin (") + profile.Version + ").");
            }

            return profile;
        }

        [OnDeserializing]
        private void OnDeserializing(StreamingContext context)
        {
            // the serializer does not run initializers: defaults for missing fields
            Version = CurrentVersion;
            HideWhenNoValue = true;
            IdsFiles = new List<string>();
            TypeProperties = new List<string>();
            Mappings = new List<ParameterMapping>();
            SpecOverrides = new List<SpecOverride>();
        }
    }
}
