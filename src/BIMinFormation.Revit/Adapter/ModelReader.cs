using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using BIMinFormation.Core.Mapping;
using BIMinFormation.Exporter;
using BIMinFormation.Ids;
using BIMinFormation.Rules;

namespace BIMinFormation.Revit.Adapter
{
    /// <summary>Where the effective IFC entity of the element came from.</summary>
    public enum EntityOrigin
    {
        /// <summary><c>IFC_EXPORT_ELEMENT_AS</c> on the instance.</summary>
        Instance,

        /// <summary><c>IFC_EXPORT_ELEMENT_TYPE_AS</c> on the type.</summary>
        Type,

        /// <summary>Default mapping of the category (approximation of the exporter); unmapped categories: proxy.</summary>
        Inferred,
    }

    /// <summary>
    /// Reads Revit elements as <see cref="ElementState"/>: the view the exported IFC will have. Entity and predefined type
    /// from the export parameters (instance before type), properties from the parameters named <c>Set.Property</c>
    /// (instance) and <c>Set.Property[Type]</c> (type, inherited by the occurrence), values converted to SI, attributes from
    /// the override parameters and <c>ClassificationCode</c>.
    /// </summary>
    public sealed class ModelReader
    {
        private static readonly Dictionary<BuiltInCategory, string> CategoryEntities = new Dictionary<BuiltInCategory, string>
        {
            [BuiltInCategory.OST_Walls] = "IfcWall",
            [BuiltInCategory.OST_Floors] = "IfcSlab",
            [BuiltInCategory.OST_Roofs] = "IfcRoof",
            [BuiltInCategory.OST_Ceilings] = "IfcCovering",
            [BuiltInCategory.OST_Doors] = "IfcDoor",
            [BuiltInCategory.OST_Windows] = "IfcWindow",
            [BuiltInCategory.OST_Columns] = "IfcColumn",
            [BuiltInCategory.OST_StructuralColumns] = "IfcColumn",
            [BuiltInCategory.OST_StructuralFraming] = "IfcBeam",
            [BuiltInCategory.OST_StructuralFoundation] = "IfcFooting",
            [BuiltInCategory.OST_Stairs] = "IfcStair",
            [BuiltInCategory.OST_Railings] = "IfcRailing",
            [BuiltInCategory.OST_StairsRailing] = "IfcRailing", // the railing elements (Railing); checked in 2027: one IfcRailing each
            [BuiltInCategory.OST_Ramps] = "IfcRamp",
            [BuiltInCategory.OST_CurtainWallPanels] = "IfcPlate",
            [BuiltInCategory.OST_CurtainWallMullions] = "IfcMember",
            [BuiltInCategory.OST_Furniture] = "IfcFurniture",
            [BuiltInCategory.OST_GenericModel] = "IfcBuildingElementProxy",
            [BuiltInCategory.OST_PipeCurves] = "IfcPipeSegment",
            [BuiltInCategory.OST_PipeFitting] = "IfcPipeFitting",
            [BuiltInCategory.OST_DuctCurves] = "IfcDuctSegment",
            [BuiltInCategory.OST_DuctFitting] = "IfcDuctFitting",
            [BuiltInCategory.OST_CableTray] = "IfcCableCarrierSegment",
            [BuiltInCategory.OST_Conduit] = "IfcCableCarrierSegment",
            [BuiltInCategory.OST_LightingFixtures] = "IfcLightFixture",
            [BuiltInCategory.OST_PlumbingFixtures] = "IfcSanitaryTerminal",
            [BuiltInCategory.OST_Rooms] = "IfcSpace",
#if REVIT2025_OR_GREATER
            [BuiltInCategory.OST_Toposolid] = "IfcGeographicElement", // checked: 2025 to 2027 export it so; the site stays on Project Information
#endif
        };

        /// <summary>Suffix of the state id of <c>IfcProject</c>, read from Project Information like <c>IfcBuilding</c>.</summary>
        public const string ProjectSuffix = "#IfcProject";

        /// <summary>Suffix of the state id of <c>IfcSite</c> when it is read from Project Information.</summary>
        public const string SiteSuffix = "#IfcSite";

        /// <summary>Project-level entities: one per IFC file, linked to the IFC by entity (their GlobalId is not an element's).</summary>
        public static readonly IReadOnlyCollection<string> ProjectLevelEntities = new[] { "IFCPROJECT", "IFCBUILDING", "IFCSITE" };

        /// <summary>Revit element of a state id (<c>123</c>, or <c>123#IfcSite</c> for the states read from Project Information).</summary>
        public static ElementId ElementIdOf(string stateId)
        {
            int hash = stateId.IndexOf('#');
            return long.Parse(hash < 0 ? stateId : stateId.Substring(0, hash), System.Globalization.CultureInfo.InvariantCulture).ToElementId();
        }

        private readonly Func<string, string, string?> _dataTypeOf;
        private readonly ExportMapping? _mapping;
        private readonly IfcSchemaCatalog? _schema;

        /// <param name="dataTypeOf">
        /// IFC type the exporter will write <c>(set, property)</c> with (from the PropertySets file or the standard Pset);
        /// <c>null</c> lets the type come from the parameter spec.
        /// </param>
        /// <param name="mapping">
        /// Mapping of the user sets per entity: the value of each property comes from the parameter the exporter will read for
        /// the element's entity (the same property may have parameters of different types).
        /// </param>
        /// <param name="schema">Schema of the export: entity hierarchy for what the exporter drops (see <see cref="DropsPredefinedType"/>).</param>
        public ModelReader(Func<string, string, string?>? dataTypeOf = null, ExportMapping? mapping = null, IfcSchemaCatalog? schema = null)
        {
            _dataTypeOf = dataTypeOf ?? ((s, p) => null);
            _mapping = mapping;
            _schema = schema;
        }

        /// <summary>
        /// The exporter does not write the predefined type: measured in Revit 2027 (IFC 4.3), an element without a type (a
        /// generic shape) exported as an installation element (<c>IfcDistributionElement</c> and subtypes) or a surface
        /// feature comes out with PredefinedType empty, while building elements keep it.
        /// </summary>
        public static bool DropsPredefinedType(ElementType? type, string? entity, IfcSchemaCatalog schema) =>
            type == null && entity != null
            && (schema.IsSubtypeOf(entity, "IfcDistributionElement") || schema.IsSubtypeOf(entity, "IfcFeatureElement"));

        /// <summary>
        /// Entity the exporter writes for the one asked (measured in Revit 2027): what is not a physical element (not an
        /// <c>IfcProduct</c>, e.g. <c>IfcConstructionEquipmentResource</c>) goes out as proxy; in IFC 4.3 <c>IfcFlowTerminal</c>
        /// and <c>IfcEnergyConversionDevice</c> go out as <c>IfcDistributionElement</c>.
        /// </summary>
        public static string ExportedEntity(string entity, IfcSchemaCatalog schema)
        {
            // a type entity (IfcBoilerType) goes out as the occurrence (IfcBoiler), as Entity reads IfcExportAs
            if (entity.EndsWith("Type", StringComparison.OrdinalIgnoreCase) && schema.Entity(entity.Substring(0, entity.Length - 4)) != null)
            {
                entity = entity.Substring(0, entity.Length - 4);
            }

            if (schema.Entity(entity) != null && !schema.IsSubtypeOf(entity, "IfcProduct")) return "IfcBuildingElementProxy";
            if (schema.Version == IfcSchemaVersion.Ifc4X3Add2
                && (string.Equals(entity, "IfcFlowTerminal", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(entity, "IfcEnergyConversionDevice", StringComparison.OrdinalIgnoreCase)))
            {
                return "IfcDistributionElement";
            }

            return entity;
        }

        /// <summary>
        /// Entity the exporter cannot give to the element: <c>IfcRamp</c> on what is not a Revit ramp makes the export fail for
        /// the element ("unexpected error", checked in 2027) and it is left out of the IFC.
        /// </summary>
        public static bool FailsExport(Element element, string? entity) =>
            string.Equals(entity, "IfcRamp", StringComparison.OrdinalIgnoreCase)
            && element.Category?.Id.IdValue() != (long)BuiltInCategory.OST_Ramps;

        /// <summary>Model elements that go into the grid (no types, views, levels, grids or reference planes).</summary>
        private static readonly HashSet<long> ExcludedCategories = new HashSet<long>
        {
            (int)BuiltInCategory.OST_ProjectBasePoint,
            (int)BuiltInCategory.OST_SharedBasePoint,
            (int)BuiltInCategory.OST_Cameras,
            (int)BuiltInCategory.OST_RvtLinks,
            (int)BuiltInCategory.OST_ProjectInformation,
            (int)BuiltInCategory.OST_Viewers,
            (int)BuiltInCategory.OST_Levels,
            (int)BuiltInCategory.OST_Grids,
            (int)BuiltInCategory.OST_CLines,
            (int)BuiltInCategory.OST_Materials,
            (int)BuiltInCategory.OST_IOSModelGroups,
            (int)BuiltInCategory.OST_Lines,
            (int)BuiltInCategory.OST_SketchLines,
            (int)BuiltInCategory.OST_Sun,
            (int)BuiltInCategory.OST_HVAC_Zones,
            (int)BuiltInCategory.OST_LegendComponents,

            // created by Revit along with other elements and not exported on their own (checked in 2027): the center line of
            // pipes, ducts, conduits and cable trays, and the parts of a railing (it comes out as one IfcRailing). Read as
            // elements, they took the specification of their host and Fill In Parameters failed on read-only parameters
            (int)BuiltInCategory.OST_CenterLines,
            (int)BuiltInCategory.OST_PipeCurvesCenterLine,
            (int)BuiltInCategory.OST_PipeFittingCenterLine,
            (int)BuiltInCategory.OST_FlexPipeCurvesCenterLine,
            (int)BuiltInCategory.OST_DuctCurvesCenterLine,
            (int)BuiltInCategory.OST_DuctFittingCenterLine,
            (int)BuiltInCategory.OST_FlexDuctCurvesCenterLine,
            (int)BuiltInCategory.OST_ConduitCenterLine,
            (int)BuiltInCategory.OST_ConduitFittingCenterLine,
            (int)BuiltInCategory.OST_CableTrayCenterLine,
            (int)BuiltInCategory.OST_CableTrayFittingCenterLine,
            (int)BuiltInCategory.OST_RailingTopRail,
            (int)BuiltInCategory.OST_RailingHandRail,
            (int)BuiltInCategory.OST_StairsRailingBaluster,
            (int)BuiltInCategory.OST_RailingSupport,
            (int)BuiltInCategory.OST_RailingTermination,
            (int)BuiltInCategory.OST_RailingRailPathExtensionLines,
            (int)BuiltInCategory.OST_RailingRailPathLines,
        };

        public static IList<Element> ModelElements(Document document) =>
            new FilteredElementCollector(document)
                .WhereElementIsNotElementType()
                .WhereElementIsViewIndependent()
                .Where(e => e.Category != null && e.Category.CategoryType == CategoryType.Model
                            && !ExcludedCategories.Contains(e.Category.Id.IdValue())
                            && !(e is Level) && !(e is Grid) && !(e is ReferencePlane) && !(e is SketchBase)
                            && !(e is InternalOrigin) && !(e is BasePoint)
                            && e.get_BoundingBox(null) != null) // no geometry: resources, settings, legends
                .ToList();

        /// <summary>Parameters of an element, gone through once, with lookup by name (LookupParameter goes through the list on every lookup).</summary>
        private sealed class ParameterIndex
        {
            private readonly Dictionary<string, Parameter> _byName = new Dictionary<string, Parameter>(StringComparer.Ordinal);

            public List<Parameter> All { get; } = new List<Parameter>();

            public ParameterIndex(Element element)
            {
                foreach (Parameter parameter in element.Parameters)
                {
                    if (parameter == null) continue;
                    All.Add(parameter);
                    string? name = parameter.Definition?.Name;
                    if (name != null && !_byName.ContainsKey(name)) _byName[name] = parameter;
                }
            }

            public Parameter? Get(string name) => _byName.TryGetValue(name, out var parameter) ? parameter : null;

            private Dictionary<Guid, Parameter>? _byGuid;

            /// <summary>Shared parameter by GUID (the map is built on the first lookup).</summary>
            public Parameter? Get(Guid guid)
            {
                if (_byGuid == null)
                {
                    _byGuid = new Dictionary<Guid, Parameter>();
                    foreach (var parameter in All)
                    {
                        if (parameter.IsShared && !_byGuid.ContainsKey(parameter.GUID)) _byGuid[parameter.GUID] = parameter;
                    }
                }

                return _byGuid.TryGetValue(guid, out var found) ? found : null;
            }

            /// <summary>Like <see cref="ModelReader.Lookup"/>: <c>BuiltInParameter.XXX</c> or name.</summary>
            public Parameter? Lookup(Element element, string name) =>
                name.StartsWith("BuiltInParameter.", StringComparison.Ordinal) ? ModelReader.Lookup(element, name) : Get(name);
        }

        /// <summary>Accumulated time per read step (performance probe for the tests).</summary>
        internal static readonly Dictionary<string, System.Diagnostics.Stopwatch> Profile = new Dictionary<string, System.Diagnostics.Stopwatch>();

        private static System.Diagnostics.Stopwatch Watch(string step)
        {
            if (!Profile.TryGetValue(step, out var watch))
            {
                watch = new System.Diagnostics.Stopwatch();
                Profile[step] = watch;
            }

            return watch;
        }

        /// <summary>Types already read in this read: type parameters are read once, not once per instance.</summary>
        private sealed class Session
        {
            public Dictionary<long, (ParameterIndex Index, Dictionary<string, Dictionary<string, PropertyState>> Sets)> Types { get; } =
                new Dictionary<long, (ParameterIndex, Dictionary<string, Dictionary<string, PropertyState>>)>();
        }

        private (ParameterIndex Index, Dictionary<string, Dictionary<string, PropertyState>> Sets) TypeData(ElementType type, Session session)
        {
            if (!session.Types.TryGetValue(type.Id.IdValue(), out var data))
            {
                var index = new ParameterIndex(type);
                var sets = new Dictionary<string, Dictionary<string, PropertyState>>(StringComparer.Ordinal);
                Collect(index.All, PropertySource.Type, sets);
                data = (index, sets);
                session.Types[type.Id.IdValue()] = data;
            }

            return data;
        }

        public ModelState Read(Document document, IfcSchemaVersion schema)
        {
            var session = new Session();
            return new ModelState(schema, ModelElements(document).Select(e => Read(e, session)).ToList());
        }

        public ElementState Read(Element element) => Read(element, new Session());

        /// <summary>
        /// Topography surface that the exporter writes as the <c>IfcSite</c> (checked in 2023: the site properties come from
        /// it); <c>null</c> when there is none, and then the site comes from Project Information. A toposolid (2025+) is
        /// exported as <c>IfcGeographicElement</c> and does not take the site.
        /// </summary>
        public static Element? SiteTopography(Document document) =>
            new FilteredElementCollector(document).OfClass(typeof(Autodesk.Revit.DB.Architecture.TopographySurface))
                .Cast<Autodesk.Revit.DB.Architecture.TopographySurface>()
                .Where(t => !t.IsSiteSubRegion).OrderBy(t => t.Id.IdValue()).FirstOrDefault();

        /// <summary>
        /// <c>IfcProject</c>, <c>IfcBuilding</c> and (without a site topography) <c>IfcSite</c>: the exporter writes their
        /// properties from the Project Information parameters (checked in 2023, 2025, 2026 and 2027). Without a GlobalId:
        /// the exporter gives them project-level GUIDs, not the export id of an element.
        /// </summary>
        public List<ElementState> ReadProjectLevel(Document document)
        {
            var info = document.ProjectInformation;
            if (info == null) return new List<ElementState>();
            var session = new Session();
            string id = info.Id.IdValue().ToString();
            var states = new List<ElementState>
            {
                Read(info, session, "IfcProject", id + ProjectSuffix, ProjectLevelGuid(document, "IfcProject")),
                Read(info, session, "IfcBuilding", id, ProjectLevelGuid(document, "IfcBuilding")),
            };
            if (SiteTopography(document) == null) states.Add(Read(info, session, "IfcSite", id + SiteSuffix, ProjectLevelGuid(document, "IfcSite")));
            return states;
        }

        /// <summary>Element id whose export GUID the exporter gives each project-level entity (measured in the IFC).</summary>
        internal static readonly IReadOnlyDictionary<string, long> ProjectLevelIds = new Dictionary<string, long>
        {
            ["IfcProject"] = -16, ["IfcBuilding"] = -15, ["IfcSite"] = -14,
        };

        /// <summary>
        /// GlobalId the exporter writes for IfcProject, IfcBuilding and IfcSite: the one stored in Project Information (when
        /// the export keeps the IFC GUIDs), otherwise the export GUID of a fixed special element id.
        /// </summary>
        public static string ProjectLevelGuid(Document document, string entity)
        {
            var stored = entity switch
            {
                "IfcProject" => BuiltInParameter.IFC_PROJECT_GUID,
                "IfcBuilding" => BuiltInParameter.IFC_BUILDING_GUID,
                _ => BuiltInParameter.IFC_SITE_GUID,
            };
            string? value = document.ProjectInformation == null ? null : Text(document.ProjectInformation, stored);
            if (value != null && value.Length == 22) return value;
            return IfcGuid.Compress(ExportUtils.GetExportId(document, ProjectLevelIds[entity].ToElementId()));
        }

        /// <summary>Reads several elements in a single session (types read once).</summary>
        public List<ElementState> Read(IEnumerable<Element> elements)
        {
            var session = new Session();
            return elements.Select(e => Read(e, session)).ToList();
        }

        private ElementState Read(Element element, Session session, string? entityOverride = null, string? idOverride = null,
            string? globalId = null)
        {
            var watch = Watch(Tr.T("índice de parâmetros"));
            watch.Start();
            var type = element.Document.GetElement(element.GetTypeId()) as ElementType;
            var own = new ParameterIndex(element);
            var typeData = type == null ? ((ParameterIndex Index, Dictionary<string, Dictionary<string, PropertyState>> Sets)?)null : TypeData(type, session);
            var typeIndex = typeData?.Index;
            watch.Stop();
            watch = Watch(Tr.T("entidade e atributos"));
            watch.Start();
            string? entity = entityOverride ?? Entity(element, type).Entity;
            bool swapped = false;
            if (entityOverride == null && entity != null && _schema != null)
            {
                string exported = ExportedEntity(entity, _schema);
                swapped = !string.Equals(exported, entity, StringComparison.OrdinalIgnoreCase);
                entity = exported; // read as the IFC will have it
            }

            string? predefined = Text(element, BuiltInParameter.IFC_EXPORT_PREDEFINEDTYPE)
                                 ?? (type == null ? null : Text(type, BuiltInParameter.IFC_EXPORT_PREDEFINEDTYPE_TYPE));
            if (_schema != null && DropsPredefinedType(type, entity, _schema)
                || swapped && string.Equals(entity, "IfcDistributionElement", StringComparison.OrdinalIgnoreCase))
            {
                predefined = null; // read as the IFC will have it
            }
            string? objectType = Text(own, "IfcObjectType") ?? Text(typeIndex, "IfcObjectType[Type]");
            bool userDefined = predefined == "USERDEFINED";
            if (userDefined)
            {
                predefined = objectType;
            }

            var attributes = new Dictionary<string, StateValue>(StringComparer.Ordinal);
            // the same as the exported IFC; the site (also from a topography surface) has the project-level GUID
            string? exportedId = entityOverride != null ? globalId
                : entity == "IfcSite" ? ProjectLevelGuid(element.Document, "IfcSite") : IfcGuid.For(element);
            if (exportedId != null) attributes["GlobalId"] = StateValue.Of(exportedId);
            watch.Stop();
            foreach (var pair in new[] { ("Name", "IfcName"), ("Description", "IfcDescription"), ("ObjectType", "IfcObjectType"),
                         ("Tag", "IfcTag"), ("LongName", "IfcLongName") })
            {
                string? value = Text(own, pair.Item2) ?? Text(typeIndex, pair.Item2 + "[Type]");
                if (value != null)
                {
                    attributes[pair.Item1] = StateValue.Of(value);
                }
            }

            // storey: the exporter gives the IfcBuildingStorey the level name (no IfcName override)
            if (element is Level && !attributes.ContainsKey("Name"))
            {
                attributes["Name"] = StateValue.Of(element.Name);
            }

            return new ElementState(
                idOverride ?? element.Id.IdValue().ToString(),
                (entity ?? "IfcBuildingElementProxy").ToUpperInvariant(),
                predefinedType: predefined,
                isUserDefined: userDefined,
                attributes: attributes,
                propertySets: PropertySets(element, own, type, typeIndex, typeData?.Sets, entity),
                classifications: Classifications(own, typeIndex));
        }

        /// <summary>
        /// Levels the exporter writes as IfcBuildingStorey: marked as building storey and with some model element (the 2023
        /// exporter does not write an empty storey; checked in the IFC). They stay out of the Classification and Fill In
        /// Parameters grids and go into the pre-validation (e.g. storey name required by the .ids).
        /// </summary>
        public static IList<Element> Storeys(Document document, IEnumerable<Element> modelElements)
        {
            var used = new HashSet<long>(modelElements.Select(e => e.LevelId.IdValue()));
            return new FilteredElementCollector(document).OfClass(typeof(Level)).Cast<Level>()
                .Where(l => used.Contains(l.Id.IdValue()) && l.get_Parameter(BuiltInParameter.LEVEL_IS_BUILDING_STORY)?.AsInteger() != 0)
                .Cast<Element>()
                .ToList();
        }

        /// <summary>Effective entity: instance &gt; type &gt; category. <c>IfcWall.SOLIDWALL</c> becomes just <c>IfcWall</c>.</summary>
        public static (string? Entity, EntityOrigin Origin) Entity(Element element, ElementType? type)
        {
            if (element is Level)
            {
                return ("IfcBuildingStorey", EntityOrigin.Inferred);
            }

            if (element is Autodesk.Revit.DB.Architecture.TopographySurface topography && !topography.IsSiteSubRegion
                && SiteTopography(element.Document)?.Id == element.Id)
            {
                return ("IfcSite", EntityOrigin.Inferred);
            }

            string? instance = Text(element, BuiltInParameter.IFC_EXPORT_ELEMENT_AS);
            if (instance != null)
            {
                return (StripPredefined(instance), EntityOrigin.Instance);
            }

            string? typeValue = type == null ? null : Text(type, BuiltInParameter.IFC_EXPORT_ELEMENT_TYPE_AS);
            if (typeValue != null)
            {
                return (StripPredefined(typeValue), EntityOrigin.Type);
            }

            if (element is DirectShape)
            {
                return ("IfcBuildingElementProxy", EntityOrigin.Inferred);
            }

            // a category without an entity in the table goes out as proxy (exporter default), and so it is read and offered
            // in the Classification (planting, specialty equipment...)
            return element.Category != null && CategoryEntities.TryGetValue((BuiltInCategory)element.Category.Id.IdValue(), out string? mapped)
                ? (mapped, EntityOrigin.Inferred)
                : ("IfcBuildingElementProxy", EntityOrigin.Inferred);
        }

        private static string StripPredefined(string value)
        {
            int dot = value.IndexOf('.');
            string entity = dot > 0 ? value.Substring(0, dot) : value;
            return entity.EndsWith("Type", StringComparison.OrdinalIgnoreCase) ? entity.Substring(0, entity.Length - 4) : entity;
        }

        private List<PropertySetState> PropertySets(Element element, ParameterIndex own, ElementType? type, ParameterIndex? typeIndex,
            Dictionary<string, Dictionary<string, PropertyState>>? typeSets, string? entity)
        {
            var watch = Watch("propriedades");
            watch.Start();
            // type first (read once per type); the occurrence overrides per property (IDS rule)
            var sets = new Dictionary<string, Dictionary<string, PropertyState>>(StringComparer.Ordinal);
            if (typeSets != null)
            {
                foreach (var pair in typeSets)
                {
                    sets[pair.Key] = new Dictionary<string, PropertyState>(pair.Value, StringComparer.Ordinal);
                }
            }

            Collect(own.All, PropertySource.Occurrence, sets);
            watch.Stop();
            if (_mapping != null)
            {
                watch = Watch(Tr.T("propriedades do mapeamento"));
                watch.Start();
                CollectMapped(element, own, type, typeIndex, entity ?? ExportMapping.FallbackEntity, sets);
                watch.Stop();
            }

            return sets.Select(s => new PropertySetState(s.Key, s.Value.Values.ToList())).ToList();
        }

        /// <summary>User sets: the parameter the PropertySets file names for the entity.</summary>
        private void CollectMapped(Element element, ParameterIndex own, ElementType? type, ParameterIndex? typeIndex, string entity,
            Dictionary<string, Dictionary<string, PropertyState>> sets)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var mapped in _mapping!.For(entity))
            {
                Element? owner = mapped.Scope == ParameterScope.Type ? type : element;
                var ownerIndex = mapped.Scope == ParameterScope.Type ? typeIndex : own;
                Parameter? parameter;
                StateValue? value;
                if (mapped.ParameterGuid == null)
                {
                    // link: like the exporter, by name on the instance and then on the type; a text property gets the value formatted
                    // as Revit shows it
                    parameter = RevitLinks.Find(element, type, mapped.Parameter) ?? (owner == null ? null : Lookup(owner, mapped.Parameter));
                    bool text = mapped.IdsDataType == null || IfcDataTypes.Classify(mapped.IdsDataType) == IfcValueKind.String;
                    value = parameter == null || !parameter.HasValue ? null
                        : text && parameter.StorageType != StorageType.String
                            ? (RevitLinks.ExportedText(parameter) is { } shown ? StateValue.Of(shown) : null)
                            : Value(parameter);
                }
                else
                {
                    parameter = owner == null || ownerIndex == null ? null
                        : ownerIndex.Get(mapped.ParameterGuid.Value) ?? ownerIndex.Lookup(owner, mapped.Parameter);
                    value = parameter != null && parameter.HasValue ? Value(parameter) : null;
                }

                if (parameter == null || value == null)
                {
                    continue;
                }

                if (!sets.TryGetValue(mapped.PropertySet, out var properties))
                {
                    properties = new Dictionary<string, PropertyState>(StringComparer.Ordinal);
                    sets[mapped.PropertySet] = properties;
                }

                var source = mapped.Scope == ParameterScope.Type ? PropertySource.Type : PropertySource.Occurrence;
                string? dataType = mapped.IdsDataType ?? DataTypeOf(parameter);
                if (properties.TryGetValue(mapped.Name, out var previous) && previous.Source == source && seen.Contains(mapped.PropertySet + "\t" + mapped.Name + "\t" + source))
                {
                    continue; // type variants: the first with a value wins (the exporter would write both)
                }

                seen.Add(mapped.PropertySet + "\t" + mapped.Name + "\t" + source);
                properties[mapped.Name] = new PropertyState(mapped.Name, PropertyKind.Single, dataType, new[] { value }, source);
            }
        }

        /// <summary>Parameter by name, or built-in when the mapping says <c>BuiltInParameter.XXX</c>.</summary>
        internal static Parameter? Lookup(Element element, string name)
        {
            const string prefix = "BuiltInParameter.";
            if (name.StartsWith(prefix, StringComparison.Ordinal)
                && Enum.TryParse(name.Substring(prefix.Length), out BuiltInParameter builtIn))
            {
                return element.get_Parameter(builtIn);
            }

            return element.LookupParameter(name);
        }

        private void Collect(IEnumerable<Parameter> parameters, PropertySource source, Dictionary<string, Dictionary<string, PropertyState>> sets)
        {
            foreach (Parameter parameter in parameters)
            {
                string? name = parameter?.Definition?.Name;
                if (parameter == null || name == null)
                {
                    continue;
                }

                if (name.EndsWith("[Type]", StringComparison.Ordinal))
                {
                    name = name.Substring(0, name.Length - "[Type]".Length);
                }

                // "BIMPR.Altura (Label)": type variant of a user property
                int suffix = name.EndsWith(")", StringComparison.Ordinal) ? name.LastIndexOf(" (", StringComparison.Ordinal) : -1;
                if (suffix > 0)
                {
                    name = name.Substring(0, suffix);
                }

                int dot = name.IndexOf('.');
                if (dot <= 0 || dot == name.Length - 1 || !parameter.HasValue)
                {
                    continue;
                }

                string set = name.Substring(0, dot);
                string property = name.Substring(dot + 1);
                if (_mapping != null && _mapping.IsCustom(set, property))
                {
                    continue; // read through the mapping, with the right parameter for the entity
                }

                var value = Value(parameter);
                if (value == null)
                {
                    continue;
                }

                string? dataType = _dataTypeOf(set, property) ?? DataTypeOf(parameter);
                if (!sets.TryGetValue(set, out var properties))
                {
                    properties = new Dictionary<string, PropertyState>(StringComparer.Ordinal);
                    sets[set] = properties;
                }

                properties[property] = new PropertyState(property, PropertyKind.Single, dataType, new[] { value }, source);
            }
        }

        /// <summary>Parameter value as the IDS sees it (SI, boolean for Yes/No).</summary>
        public static StateValue? Value(Parameter parameter)
        {
            switch (parameter.StorageType)
            {
                case StorageType.String:
                    string? text = parameter.AsString();
                    return text == null ? null : StateValue.Of(text);
                case StorageType.Integer:
                    return parameter.Definition.GetDataType() == SpecTypeId.Boolean.YesNo
                        ? StateValue.Of(parameter.AsInteger() != 0)
                        : StateValue.Of((long)parameter.AsInteger());
                case StorageType.Double:
                    return StateValue.Of(RevitSpecs.ToSi(parameter.AsDouble(), parameter.Definition.GetDataType()));
                default:
                    return null;
            }
        }

        private static string? DataTypeOf(Parameter parameter)
        {
            var spec = parameter.Definition.GetDataType();
            if (spec == SpecTypeId.String.Text) return "IFCLABEL";
            if (spec == SpecTypeId.Boolean.YesNo) return "IFCBOOLEAN";
            if (spec == SpecTypeId.Int.Integer) return "IFCINTEGER";
            if (spec == SpecTypeId.Length) return "IFCLENGTHMEASURE";
            if (spec == SpecTypeId.Area) return "IFCAREAMEASURE";
            if (spec == SpecTypeId.Volume) return "IFCVOLUMEMEASURE";
            if (spec == SpecTypeId.Mass) return "IFCMASSMEASURE";
            return spec == SpecTypeId.Number ? "IFCREAL" : null;
        }

        private static readonly string[] ClassificationNames =
            new[] { "ClassificationCode" }.Concat(Enumerable.Range(2, 9).Select(i => "ClassificationCode(" + i + ")")).ToArray();

        private static List<ClassificationState> Classifications(ParameterIndex own, ParameterIndex? type)
        {
            var result = new List<ClassificationState>();
            foreach (string name in ClassificationNames)
            {
                string? code = Text(own, name) ?? Text(type, name + "[Type]") ?? Text(type, name);
                if (code == null)
                {
                    continue;
                }

                // [System]Code:Description (description after the last ':')
                string? system = null;
                if (code.StartsWith("[", StringComparison.Ordinal) && code.IndexOf(']') > 0)
                {
                    system = code.Substring(1, code.IndexOf(']') - 1);
                    code = code.Substring(code.IndexOf(']') + 1);
                }

                int colon = code.LastIndexOf(':');
                result.Add(new ClassificationState(system, new[] { colon > 0 ? code.Substring(0, colon) : code }));
            }

            return result;
        }

        internal static string? Text(Element element, BuiltInParameter id)
        {
            var parameter = element.get_Parameter(id);
            string? value = parameter?.AsString();
            return string.IsNullOrWhiteSpace(value) ? null : value;
        }

        private static string? Text(ParameterIndex? index, string name) => index == null ? null : Text(index.Get(name));

        internal static string? Text(Element element, string name) => Text(element.LookupParameter(name));

        private static string? Text(Parameter? parameter)
        {
            if (parameter == null || !parameter.HasValue)
            {
                return null;
            }

            string? value = parameter.StorageType == StorageType.String ? parameter.AsString() : parameter.AsValueString();
            return string.IsNullOrWhiteSpace(value) ? null : value;
        }
    }
}
