using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Autodesk.Revit.DB;
using BIMinFormation.Core.Mapping;
using BIMinFormation.Core.Profile;
using BIMinFormation.Exporter;
using BIMinFormation.Ids;
using BIMinFormation.Revit.Adapter;

namespace BIMinFormation.Revit.Services
{
    /// <summary>
    /// What the windows need from a document: profile (from the .rvt, or from the user preferences), loaded specifications,
    /// target schema, catalogs and the model reader. The specifications are cached while the files do not change.
    /// </summary>
    public sealed class PluginContext
    {
        private static string? _cacheKey;
        private static IdsLibrary? _cacheLibrary;

        public ProjectProfile Profile { get; }
        public IdsLibrary Library { get; }
        public IfcSchemaVersion Schema { get; }
        public IfcSchemaCatalog SchemaCatalog { get; }
        public ExporterCatalog Exporter { get; }
        public ProfileStatus Status { get; }
        public ModelReader Reader { get; }

        /// <summary>User sets per entity, as the PropertySets file will declare them.</summary>
        public ExportMapping Mapping { get; }

        /// <summary>Mapping format: the one of the Revit exporter in use.</summary>
        public MappingFormat Format { get; }

        /// <summary>Data type required by the .ids for each (set, property).</summary>
        public IReadOnlyDictionary<string, string> DataTypes { get; }

        private static InstalledParameters? _installed;

        public static InstalledParameters Installed => _installed ??= InstalledParameters.Load();

        private PluginContext(ProjectProfile profile, ProfileStatus status, IdsLibrary library, IfcSchemaVersion schema,
            ExporterCatalog exporter)
        {
            Profile = profile;
            Status = status;
            Library = library;
            Schema = schema;
            SchemaCatalog = IfcSchemaCatalog.For(schema);
            Exporter = exporter;
            var dataTypes = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var facet in library.Specifications.SelectMany(s => s.Applicability.Facets.Concat(s.Requirements.Facets)).OfType<PropertyFacet>())
            {
                if (facet.PropertySet is SimpleValue p && facet.BaseName is SimpleValue b && facet.DataType != null)
                {
                    string key = p.Text + "/" + b.Text;
                    if (!dataTypes.ContainsKey(key))
                    {
                        dataTypes[key] = facet.DataType;
                    }
                }
            }

            DataTypes = dataTypes;
            Format = MappingFormats.ForRevitYear(exporter.Year); // the format of the Revit exporter in use
            // only the specifications in use in the model (or all, by the option or before the first computation)
            Mapping = ExportMapping.Build(profile.MappedSpecifications(library.Specifications), schema, exporter, profile, Format);
            Reader = new ModelReader((s, p) => DataTypes.TryGetValue(s + "/" + p, out var t) ? t : null, Mapping);
        }

        /// <summary>Document profile; without a profile in the .rvt, a new profile with the folder from the preferences.</summary>
        public static ProjectProfile LoadProfile(Document document)
        {
            ProjectProfile? profile = null;
            try
            {
                profile = ProfileStorage.Load(document);
            }
            catch (Exception ex)
            {
                Log.Error("Unreadable project profile; using the user preferences.", ex);
            }

            if (profile == null)
            {
                profile = new ProjectProfile { IdsFolder = PluginSettings.Load().IdsFolder };
            }

            return profile;
        }

        /// <summary>
        /// Builds the context, or returns <c>null</c> with a message about what is missing (folder not chosen, no specifications,
        /// no schema defined).
        /// </summary>
        public static PluginContext? Create(Document document, out string? problem)
        {
            var profile = LoadProfile(document);
            RevitLinks.ResolveNames(document, profile); // links: parameter name in the language of the Revit in use
            var exporter = RuntimeExporter.Current;
            var status = ProfileStatus.Evaluate(profile, exporter);
            if (status.Library == null || status.Library.Specifications.Count == 0)
            {
                problem = status.Summary;
                return null;
            }

            if (status.Schema == null)
            {
                problem = Tr.T("Defina o esquema IFC alvo em Configurações (os .ids misturam versões).");
                return null;
            }

            problem = null;
            return new PluginContext(profile, status, Cached(profile, status.Library), status.Schema.Value, exporter);
        }

        private static IdsLibrary Cached(ProjectProfile profile, IdsLibrary fresh)
        {
            string key = string.Join("|", profile.ResolveFiles().Select(f => f + "@" + (File.Exists(f) ? File.GetLastWriteTimeUtc(f).Ticks : 0)));
            if (key == _cacheKey && _cacheLibrary != null)
            {
                return _cacheLibrary;
            }

            _cacheKey = key;
            _cacheLibrary = fresh;
            return fresh;
        }
    }
}
