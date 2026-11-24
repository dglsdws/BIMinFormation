using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using BIMinFormation.Core.Fill;
using BIMinFormation.Exporter;
using BIMinFormation.Rules;

namespace BIMinFormation.Revit.Adapter
{
    /// <summary>A Revit parameter that may suit an IDS property.</summary>
    public sealed class LinkCandidate
    {
        public string Name { get; set; } = "";

        /// <summary>Value as the exporter writes it (text formatted as Revit shows it, for text properties).</summary>
        public string Value { get; set; } = "";

        public bool OnType { get; set; }
        public bool Compatible { get; set; }
        public double Score { get; set; }

        /// <summary>Revit built-in parameter (e.g. <c>HOST_AREA_COMPUTED</c>); the name changes with the language, this does not.</summary>
        public string? BuiltIn { get; set; }

        /// <summary>GUID when the parameter is shared.</summary>
        public string? SharedGuid { get; set; }

        /// <summary>Category of the element (e.g. <c>OST_Roofs</c>), to find the parameter name in another language.</summary>
        public string? Category { get; set; }
    }

    /// <summary>
    /// Links on the Revit side. The Revit 2023 exporter finds the parameter by its visible name, on the instance and then
    /// on the type (checked: "Área", "Inclinação" and "Espessura-padrão" come out; by internal identifier half of them
    /// fail), and a text property gets the value formatted as Revit shows it (e.g. <c>16.70°</c>). The same rules here.
    /// </summary>
    public static class RevitLinks
    {
        public static ParameterKind KindOf(Parameter parameter)
        {
            switch (parameter.StorageType)
            {
                case StorageType.String:
                    return ParameterKind.Text;
                case StorageType.Integer:
                    return parameter.Definition.GetDataType() == SpecTypeId.Boolean.YesNo ? ParameterKind.YesNo : ParameterKind.Integer;
                case StorageType.Double:
                    return ParameterKind.Number;
                default:
                    return ParameterKind.Other;
            }
        }

        /// <summary>Text the exporter writes in a text property linked to this parameter.</summary>
        public static string? ExportedText(Parameter parameter) =>
            !parameter.HasValue ? null : parameter.StorageType == StorageType.String ? parameter.AsString() : parameter.AsValueString();

        /// <summary>Parameter by name, on the instance and then on the type (as the exporter looks it up).</summary>
        public static Parameter? Find(Element element, ElementType? type, string name) =>
            Usable(element.LookupParameter(name)) ?? (type == null ? null : Usable(type.LookupParameter(name)));

        private static Parameter? Usable(Parameter? parameter) =>
            parameter != null && !(parameter.Definition is InternalDefinition { Visible: false }) ? parameter : null;

        /// <summary>Parameters of the element and of the type that suit the property, the most similar names first.</summary>
        public static List<LinkCandidate> Candidates(Element element, ElementType? type, PlannedRequirement requirement, ExporterCatalog exporter)
        {
            var dataType = requirement.DataType == null ? null : DataTypeMap.Resolve(requirement.DataType, exporter);
            var expected = dataType?.RevitSpec == null ? null : RevitSpecs.Resolve(dataType.RevitSpec);
            string own = requirement.PropertySet + ".";
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var result = new List<LinkCandidate>();
            foreach (var (owner, onType) in new[] { (element, false), ((Element?)type, true) })
            {
                if (owner == null) continue;
                foreach (Parameter parameter in owner.Parameters)
                {
                    string? name = parameter.Definition?.Name;
                    if (name == null || Usable(parameter) == null || !seen.Add(name)) continue;
                    if (name.StartsWith(own, StringComparison.Ordinal) || name.StartsWith("Ifc", StringComparison.Ordinal)
                        || name.StartsWith("ClassificationCode", StringComparison.Ordinal))
                    {
                        continue; // the plugin's own and the exporter overrides
                    }

                    var kind = KindOf(parameter);
                    var spec = parameter.Definition!.GetDataType();
                    string? specName = expected != null && spec == expected ? dataType!.RevitSpec : spec?.TypeId;
                    result.Add(new LinkCandidate
                    {
                        Name = name,
                        Value = ExportedText(parameter) ?? "",
                        OnType = onType,
                        Compatible = LinkSuggestions.Compatible(requirement.DataType, dataType?.RevitSpec, kind, specName),
                        Score = LinkSuggestions.Score(requirement.Name ?? "", name),
                        BuiltIn = parameter.Definition is InternalDefinition { BuiltInParameter: var id } && id != BuiltInParameter.INVALID
                            ? id.ToString()
                            : null,
                        SharedGuid = parameter.IsShared ? parameter.GUID.ToString() : null,
                        Category = element.Category?.Id.IdValue() is long category && category < 0 ? ((BuiltInCategory)category).ToString() : null,
                    });
                }
            }

            return result.OrderByDescending(c => c.Compatible).ThenByDescending(c => c.Score)
                .ThenBy(c => c.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        }

        /// <summary>
        /// Replaces the name kept in each link with the parameter name in the Revit in use: built-in parameter names change
        /// with the language ("Área" / "Area"), and the exporter looks up by name. The name comes from the parameter itself on
        /// a model element (the Revit schedule label is not always the name the element shows). In memory only.
        /// </summary>
        public static int ResolveNames(Document document, Core.Profile.ProjectProfile profile)
        {
            int changed = 0;
            var cache = new Dictionary<string, string?>(StringComparer.Ordinal);
            foreach (var link in profile.Mappings.Where(m => !m.IsDerived && (m.BuiltIn != null || m.SharedGuid != null)))
            {
                string key = link.BuiltIn ?? link.SharedGuid!;
                if (!cache.TryGetValue(key, out string? name))
                {
                    name = link.BuiltIn != null ? BuiltInName(document, link.BuiltIn, link.Category) : SharedName(document, link.SharedGuid!);
                    cache[key] = name;
                }

                if (name != null && name != link.Parameter)
                {
                    link.Parameter = name;
                    changed++;
                }
            }

            return changed;
        }

        /// <summary>Name of each built-in parameter in the Revit in use: the language does not change during the session.</summary>
        private static readonly Dictionary<BuiltInParameter, string> SessionNames = new Dictionary<BuiltInParameter, string>();

        private static string? BuiltInName(Document document, string builtIn, string? category)
        {
            if (!Enum.TryParse(builtIn, out BuiltInParameter id) || id == BuiltInParameter.INVALID) return null;
            lock (SessionNames)
            {
                if (SessionNames.TryGetValue(id, out string? known)) return known;
            }

            string? found = FindBuiltInName(document, id, category);
            if (found != null)
            {
                lock (SessionNames) SessionNames[id] = found;
            }

            return found;
        }

        private static string? FindBuiltInName(Document document, BuiltInParameter id, string? category)
        {
            // an element (or type) of the category where the link was made; searching the whole model takes seconds
            if (category != null && Enum.TryParse(category, out BuiltInCategory builtInCategory))
            {
                foreach (bool types in new[] { false, true })
                {
                    var collector = new FilteredElementCollector(document).OfCategory(builtInCategory);
                    foreach (var element in (types ? collector.WhereElementIsElementType() : collector.WhereElementIsNotElementType()).Take(20))
                    {
                        string? name = element.get_Parameter(id)?.Definition?.Name;
                        if (!string.IsNullOrEmpty(name)) return name;
                    }
                }
            }

            try
            {
                return LabelUtils.GetLabelFor(id);
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static string? SharedName(Document document, string guid) =>
            Guid.TryParse(guid, out var id) ? SharedParameterElement.Lookup(document, id)?.GetDefinition()?.Name : null;

        /// <summary>
        /// Material of the element (source calculated by the plugin): the type layers, in order, or the geometry materials;
        /// distinct names separated by " / ". <c>null</c> when the element has no material.
        /// </summary>
        public static string? MaterialNames(Element element, ElementType? type)
        {
            var names = Materials(element, type);
            return names.Count == 0 ? null : string.Join(" / ", names);
        }

        /// <summary>Material names of the element, each once: the layers of the type, or the element and structural materials.</summary>
        public static List<string> Materials(Element element, ElementType? type)
        {
            var document = element.Document;
            var ids = new List<ElementId>();
            if (type is HostObjAttributes host && host.GetCompoundStructure() is CompoundStructure structure)
            {
                ids.AddRange(structure.GetLayers().Select(l => l.MaterialId));
            }

            if (!ids.Any(id => id != ElementId.InvalidElementId))
            {
                ids.AddRange(element.GetMaterialIds(false));
                var structural = element.get_Parameter(BuiltInParameter.STRUCTURAL_MATERIAL_PARAM)?.AsElementId()
                                 ?? type?.get_Parameter(BuiltInParameter.STRUCTURAL_MATERIAL_PARAM)?.AsElementId();
                if (structural != null) ids.Add(structural);
            }

            return ids.Where(id => id != ElementId.InvalidElementId)
                .Select(id => document.GetElement(id)?.Name)
                .Where(n => !string.IsNullOrWhiteSpace(n))
                .Select(n => n!)
                .Distinct()
                .ToList();
        }
    }
}
