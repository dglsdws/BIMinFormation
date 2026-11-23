using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Autodesk.Revit.DB;
using BIMinFormation.Core.Classification;
using BIMinFormation.Core.Fill;
using BIMinFormation.Exporter;
using BIMinFormation.Ids;
using BIMinFormation.Revit.Adapter;
using BIMinFormation.Rules;

namespace BIMinFormation.Revit.Services
{
    /// <summary>A requirement of an element in the Fill In Parameters property grid.</summary>
    public sealed class FillCellModel
    {
        public ElementId ElementId { get; set; } = ElementId.InvalidElementId;
        public PlannedRequirement Requirement { get; set; } = null!;
        public OriginResult Origin { get; set; } = null!;

        /// <summary>Requirement key (e.g. <c>property:BIMPR/Tipo</c>).</summary>
        public string Key => Requirement.Key;

        public string Label => Requirement.Kind == FacetKind.Property
            ? Requirement.PropertySet + " / " + Requirement.Name
            : Requirement.Name ?? Requirement.Key;

        public string Specs => string.Join(", ", Requirement.Sources.Select(s => s.Name).Distinct());

        public StateValue? ModelValue { get; set; }

        /// <summary>Typed value (or proposed by the .ids); <c>null</c> = not edited.</summary>
        public string? Typed { get; set; }

        /// <summary>The typed value came from the .ids itself (single value): "automatic" status.</summary>
        public bool FromIds { get; set; }

        /// <summary>Parameter that receives the value, and whether it is on the type.</summary>
        public string? Parameter { get; set; }

        public bool OnType { get; set; }

        public string? Conflict { get; set; }

        /// <summary>Profile link for the property on this entity.</summary>
        public BIMinFormation.Core.Profile.ParameterMapping? Link { get; set; }

        /// <summary>The value comes from a Revit parameter: the plugin does not write it, the exporter reads it.</summary>
        public bool Linked => Link != null && !Link.IsDerived;

        /// <summary>The value is calculated by the plugin (the element material) and written to the plugin parameter.</summary>
        public bool Derived => Link != null && Link.IsDerived;

        public bool IsWritable => Conflict == null && Parameter != null && !Linked
            && (Origin.Origin == ValueOrigin.CustomParameter || Origin.Origin == ValueOrigin.StandardParameter
                || Origin.Origin == ValueOrigin.AttributeOverride);

        public CellResult Evaluate()
        {
            var result = Conflict != null
                ? new CellResult(CellStatus.Invalid, Tr.T("conflito entre fichas: ") + Conflict)
                : Linked
                    ? FillCell.Evaluate(Requirement, Origin.Origin, ModelValue, null, DataType, Choices)
                : !IsWritable && Origin.Origin != ValueOrigin.ExporterQuantity && Origin.Origin != ValueOrigin.NotExportable
                    ? new CellResult(CellStatus.Manual, Origin.Detail)
                    : FillCell.Evaluate(Requirement, Origin.Origin, ModelValue, Typed, DataType, Choices);

            // number in the IDS (e.g. IFCCOUNTMEASURE) written to an integer Revit parameter (Pset_DoorWindowGlazingType.GlassLayers):
            // only integers are written
            if (result.Status == CellStatus.Changed && IntegerTarget && Typed != null
                && !long.TryParse(Typed.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out _))
            {
                return new CellResult(CellStatus.Invalid, Tr.T("o parâmetro do Revit aceita só número inteiro"));
            }

            return result;
        }

        /// <summary>The parameter that receives the value stores integers (installed standard, or plugin parameter of integer type).</summary>
        public bool IntegerTarget =>
            Origin.Origin == ValueOrigin.StandardParameter ? Origin.Parameter?.RevitDataType == "INTEGER"
            : Origin.Origin == ValueOrigin.CustomParameter && Origin.DataType?.RevitSpec == "Int.Integer";

        /// <summary>
        /// Values accepted in the grid: the IDS ones or, for an enumerated standard property (e.g. Pset_WallCommon.Status),
        /// the schema enumeration, the only ones the exporter writes (filtered by the IDS ones, if any).
        /// </summary>
        public IReadOnlyList<string>? Choices { get; set; }

        /// <summary>
        /// IFC type of the value: the IDS one or, when the IDS does not say, the one of the standard parameter installed by
        /// Autodesk (e.g. NL asks for Pset_WallCommon.LoadBearing without a dataType, and the parameter is Yes/No).
        /// </summary>
        public string? DataType
        {
            get
            {
                if (Requirement.DataType != null) return Requirement.DataType;
                string? standard = Origin.Parameter?.IfcType;
                return standard != null && standard.StartsWith("Ifc", StringComparison.OrdinalIgnoreCase)
                    ? standard.ToUpperInvariant()
                    : null;
            }
        }

        /// <summary>Text of the STATUS column.</summary>
        public string StatusText()
        {
            var result = Evaluate();
            if (Linked)
            {
                return result.Status == CellStatus.Ok ? Tr.T("vinculado")
                    : result.Status == CellStatus.Pending ? Tr.T("vínculo vazio")
                    : result.Status == CellStatus.Invalid ? Tr.T("inválido") : "vinculado";
            }

            switch (result.Status)
            {
                case CellStatus.Ok: return "ok";
                case CellStatus.Automatic: return Tr.T("calculado");
                case CellStatus.Changed: return FromIds ? Tr.T("automático") : Tr.T("alterado");
                case CellStatus.Pending: return Tr.T("pendente");
                case CellStatus.Optional: return Tr.T("opcional");
                case CellStatus.Invalid: return Tr.T("inválido");
                case CellStatus.NotExportable: return Tr.T("não exportável");
                case CellStatus.Prohibited: return Tr.T("proibido");
                default: return Tr.T("manual");
            }
        }

        public string TypeText
        {
            get
            {
                string? dataType = DataType;
                switch (IfcDataTypes.Classify(dataType))
                {
                    case IfcValueKind.Boolean: return Tr.T("Booleano");
                    case IfcValueKind.Integer: return Tr.T("Inteiro");
                    case IfcValueKind.String: return Tr.T("Texto");
                }

                switch (dataType)
                {
                    case "IFCLENGTHMEASURE":
                    case "IFCPOSITIVELENGTHMEASURE":
                    case "IFCNONNEGATIVELENGTHMEASURE":
                        return Tr.T("Comprimento (m)");
                    case "IFCAREAMEASURE": return Tr.T("Área (m²)");
                    case "IFCVOLUMEMEASURE": return Tr.T("Volume (m³)");
                    case "IFCMASSMEASURE": return Tr.T("Massa (kg)");
                    case "IFCPOWERMEASURE": return Tr.T("Potência (W)");
                    case "IFCELECTRICVOLTAGEMEASURE": return Tr.T("Tensão (V)");
                    case "IFCELECTRICCURRENTMEASURE": return Tr.T("Corrente (A)");
                    case "IFCVOLUMETRICFLOWRATEMEASURE": return Tr.T("Vazão (m³/s)");
                    case "IFCPLANEANGLEMEASURE":
                    case "IFCPOSITIVEPLANEANGLEMEASURE":
                        return Tr.T("Ângulo (rad)");
                    case null: return Tr.T("Texto");
                    default: return Tr.T("Número (") + dataType.Replace("IFC", "").Replace("MEASURE", "").ToLowerInvariant() + ")";
                }
            }
        }

        public bool IsBoolean => IfcDataTypes.Classify(DataType) == IfcValueKind.Boolean;

        /// <summary>The value to show in the VALUE column.</summary>
        public string ValueText => Typed ?? (ModelValue == null || ModelValue.IsEmpty ? "" : ModelValue.ToString());

        public bool ToWrite()
        {
            if (!IsWritable || Typed == null || Typed.Length == 0) return false;
            var status = Evaluate().Status;
            return status == CellStatus.Changed;
        }
    }

    /// <summary>An element in the Elements tab of Fill In Parameters.</summary>
    public sealed class FillElementRow
    {
        public ElementId Id { get; set; } = ElementId.InvalidElementId;
        public int Order { get; set; }
        public string Name { get; set; } = "";
        public string Category { get; set; } = "";
        public List<Specification> Specifications { get; set; } = new List<Specification>();
        public List<FillCellModel> Cells { get; set; } = new List<FillCellModel>();
        public List<string> Notes { get; set; } = new List<string>();

        public string ElementText => Name + " · " + Category;

        public string SpecText => Specifications.Count == 0 ? "" : Specifications.Count == 1
            ? Specifications[0].Name
            : Specifications.Count + Tr.T(" fichas");

        public List<FillCellModel> Pending() =>
            Cells.Where(c => c.Evaluate().Status == CellStatus.Pending).ToList();
    }

    public sealed class FillSummary
    {
        public int Written { get; set; }
        public int AlreadyCorrect { get; set; }
        public int Pending { get; set; }
        public int Invalid { get; set; }
        public List<string> Failures { get; } = new List<string>();

        public override string ToString() =>
            Tr.T("Gravados: ") + Written + Tr.T("\nJá corretos: ") + AlreadyCorrect + Tr.T("\nPendentes: ") + Pending + Tr.T("\nInválidos: ") + Invalid
            + Tr.T("\nFalhas: ") + Failures.Count + (Failures.Count > 0 ? "\n\n" + string.Join("\n", Failures.Take(20)) : "");
    }

    /// <summary>Plan of each element with a specification, and writing of the values.</summary>
    public static class FillService
    {
        public static List<FillElementRow> BuildRows(Document document, PluginContext context)
        {
            var model = ModelCache.Read(document, context);
            var recognizer = new Recognizer(context.Library.Specifications);
            var rows = new List<FillElementRow>();
            int order = 0;
            foreach (var element in ModelReader.ModelElements(document))
            {
                var state = model.Find(element.Id.IdValue().ToString());
                if (state == null)
                {
                    continue;
                }

                var recognition = recognizer.Recognize(state, model);
                var row = new FillElementRow
                {
                    Id = element.Id,
                    Order = order++,
                    Name = element.Name,
                    Category = element.Category?.Name ?? "",
                    Specifications = recognition.Specifications.ToList(),
                };
                if (recognition.Specifications.Count > 0)
                {
                    row.Cells = Cells(element, state, recognition.Specifications, context, row.Notes);
                }

                rows.Add(row);
            }

            // project, building and site: written to the Project Information parameters, which the exporter reads for them
            var project = document.ProjectInformation;
            if (project != null)
            {
                string id = project.Id.IdValue().ToString();
                foreach (var (stateId, name) in new[]
                         {
                             (id + ModelReader.ProjectSuffix, Tr.T("Projeto")), (id, Tr.T("Edifício")), (id + ModelReader.SiteSuffix, Tr.T("Terreno")),
                         })
                {
                    var state = model.Find(stateId);
                    if (state == null) continue;
                    var recognition = recognizer.Recognize(state, model);
                    if (recognition.Specifications.Count == 0) continue;
                    var row = new FillElementRow
                    {
                        Id = project.Id,
                        Order = order++,
                        Name = name,
                        Category = project.Category?.Name ?? "",
                        Specifications = recognition.Specifications.ToList(),
                    };
                    row.Cells = Cells(project, state, recognition.Specifications, context, row.Notes);
                    rows.Add(row);
                }
            }

            return rows;
        }

        internal static List<FillCellModel> Cells(Element element, ElementState state, IReadOnlyList<Specification> specs,
            PluginContext context, List<string> notes)
        {
            var plan = RequirementPlanner.Plan(state, specs);
            var conflicts = plan.Conflicts.GroupBy(c => c.Requirement.Key).ToDictionary(g => g.Key, g => string.Join("; ", g.Select(c => c.Detail)));
            notes.AddRange(plan.Unplanned.Select(f => Tr.T("não gravável: ") + f.Kind));
            var cells = new List<FillCellModel>();
            foreach (var requirement in plan.Requirements)
            {
                if (requirement.Kind != FacetKind.Property && requirement.Kind != FacetKind.Attribute)
                {
                    continue; // entity (Classification), classification, material and partOf go in the notes
                }

                var origin = ValueOrigins.Classify(requirement.Facets[0], context.SchemaCatalog, context.Exporter);
                var cell = new FillCellModel
                {
                    ElementId = element.Id,
                    Requirement = requirement,
                    Origin = origin,
                    Conflict = conflicts.TryGetValue(requirement.Key, out var conflict) ? conflict : null,
                };

                if (requirement.Kind == FacetKind.Property)
                {
                    cell.ModelValue = state.PropertySets.FirstOrDefault(p => p.Name == requirement.PropertySet)
                        ?.Properties.FirstOrDefault(p => p.Name == requirement.Name)?.Values.FirstOrDefault(v => !v.IsEmpty);
                    cell.OnType = context.Profile.IsTypeProperty(requirement.PropertySet!, requirement.Name!);
                    if (origin.Origin == ValueOrigin.CustomParameter && origin.DataType?.PropertyType != null)
                    {
                        // the parameter the PropertySets file names for the entity of this element
                        var mapped = context.Mapping.Find(state.Entity, requirement.PropertySet!, requirement.Name!, origin.DataType.PropertyType);
                        var other = mapped == null ? context.Mapping.Find(state.Entity, requirement.PropertySet!, requirement.Name!) : null;
                        if (other != null && cell.Conflict == null)
                        {
                            cell.Conflict = Tr.T("o exportador escreve ") + other.PropertySet + "." + other.Name + Tr.T(" como ") + other.PropertyType
                                            + Tr.T(" para ") + state.Entity + " (" + other.Specification + Tr.T("), e esta ficha pede ")
                                            + origin.DataType.PropertyType;
                        }

                        cell.Link = context.Profile.Link(requirement.PropertySet!, requirement.Name!, state.Entity);
                        cell.Parameter = mapped?.Parameter
                                         ?? context.Profile.MappedParameter(requirement.PropertySet!, requirement.Name!, state.Entity)
                                         ?? context.Mapping.NameFor(requirement.PropertySet!, requirement.Name!, origin.DataType.PropertyType,
                                             cell.OnType ? ParameterScope.Type : ParameterScope.Instance);
                    }
                    else if (origin.Origin == ValueOrigin.StandardParameter && origin.Parameter != null)
                    {
                        cell.Parameter = requirement.PropertySet + "." + requirement.Name + (cell.OnType ? "[Type]" : "");
                    }
                }
                else if (requirement.Name != null && ValueOrigins.AttributeOverrides.TryGetValue(requirement.Name, out var overrideName))
                {
                    state.Attributes.TryGetValue(requirement.Name, out var attribute);
                    cell.ModelValue = attribute;
                    cell.Parameter = overrideName;
                }

                // material calculated by the plugin (derived link): proposed as "automatic" when it differs from the written one
                if (cell.Derived && cell.IsWritable)
                {
                    string? material = RevitLinks.MaterialNames(element, element.Document.GetElement(element.GetTypeId()) as ElementType);
                    if (material != null && (cell.ModelValue == null || cell.ModelValue.IsEmpty || cell.ModelValue.ToString() != material))
                    {
                        cell.Typed = material;
                        cell.FromIds = true;
                    }
                }

                // single value required by the .ids and still missing in the model: proposed as "automatic"
                if (cell.IsWritable && (cell.ModelValue == null || cell.ModelValue.IsEmpty)
                    && requirement.AllowedValues != null && requirement.AllowedValues.Count == 1
                    && requirement.Cardinality != FacetCardinality.Prohibited)
                {
                    cell.Typed = requirement.AllowedValues[0];
                    cell.FromIds = true;
                }

                if (requirement.Kind == FacetKind.Property && origin.Origin == ValueOrigin.StandardParameter)
                {
                    var template = context.SchemaCatalog.PropertySet(requirement.PropertySet!)?.Property(requirement.Name!);
                    if (template != null && template.EnumerationValues.Count > 0)
                    {
                        cell.Choices = requirement.AllowedValues == null
                            ? template.EnumerationValues
                            : template.EnumerationValues.Where(v => requirement.AllowedValues.Contains(v)).ToList();
                    }
                }

                cells.Add(cell);
            }

            // writable first; the non-writable ones (gray) at the end
            return cells.OrderBy(c => c.IsWritable ? 0 : 1).ToList();
        }

        /// <summary>
        /// Copy values: the requirements the plugin writes, with a value, from one element (requirement key → value). The
        /// calculated material and the ones linked to Revit, which the model already provides, are left out.
        /// </summary>
        public static Dictionary<string, string> CopyValues(FillElementRow row) =>
            row.Cells.Where(c => c.IsWritable && !c.Derived && c.ValueText.Length > 0 && c.ValueText != FillCell.Mixed)
                .GroupBy(c => c.Key).ToDictionary(g => g.Key, g => g.First().ValueText, StringComparer.Ordinal);

        /// <summary>
        /// Pastes values into the same requirements of the elements: they come in as "changed" (written on Apply). A value the
        /// target does not accept (outside the list, EOI of another specification, wrong type) does not go in and counts as rejected.
        /// </summary>
        public static (int Pasted, int Rejected) PasteValues(IEnumerable<FillElementRow> rows, IReadOnlyDictionary<string, string> values)
        {
            int pasted = 0, rejected = 0;
            foreach (var cell in rows.SelectMany(r => r.Cells).Where(c => c.IsWritable && !c.Derived))
            {
                if (!values.TryGetValue(cell.Key, out string? value)) continue;
                if (cell.ModelValue != null && !cell.ModelValue.IsEmpty && cell.ModelValue.ToString() == value)
                {
                    cell.Typed = null; // the element already has the value
                    cell.FromIds = false;
                    continue;
                }

                var (typed, fromIds) = (cell.Typed, cell.FromIds);
                cell.Typed = value;
                cell.FromIds = false;
                var status = cell.Evaluate().Status;
                if (status == CellStatus.Invalid || status == CellStatus.Prohibited)
                {
                    (cell.Typed, cell.FromIds) = (typed, fromIds);
                    rejected++;
                }
                else
                {
                    pasted++;
                }
            }

            return (pasted, rejected);
        }

        /// <summary>Writes, in a single transaction, every "automatic" or "changed" cell of every element.</summary>
        public static FillSummary Apply(Document document, PluginContext context, IReadOnlyList<FillElementRow> rows, string parametersFile)
        {
            var summary = new FillSummary
            {
                Pending = rows.Sum(r => r.Pending().Count),
                Invalid = rows.Sum(r => r.Cells.Count(c => c.Evaluate().Status == CellStatus.Invalid)),
            };
            var cells = rows.SelectMany(r => r.Cells).Where(c => c.ToWrite()).ToList();
            if (cells.Count == 0)
            {
                return summary;
            }

            var writer = new ParameterWriter(document, parametersFile);
            using var transaction = new Transaction(document, Tr.T("BIMinFormation: Preencher Parâmetros"));
            transaction.Start();

            // 1. definitions and bindings, with the union of the batch categories
            var requests = new Dictionary<string, ParameterRequest>(StringComparer.Ordinal);
            foreach (var group in cells.GroupBy(c => c.Parameter!))
            {
                var first = group.First();
                var categories = group.Select(c => document.GetElement(c.ElementId)?.Category).Where(c => c != null).Cast<Category>()
                    .GroupBy(c => c.Id.IdValue()).Select(g => g.First()).ToList();
                try
                {
                    var request = Request(first, context);
                    if (request != null)
                    {
                        writer.Ensure(request, categories);
                        requests[group.Key] = request;
                    }
                }
                catch (Exception ex)
                {
                    summary.Failures.Add(first.Parameter + ": " + ex.Message);
                }
            }

            // 2. values, in Revit internal units
            foreach (var cell in cells)
            {
                var element = document.GetElement(cell.ElementId);
                Element target = cell.OnType ? document.GetElement(element.GetTypeId()) ?? element : element;
                try
                {
                    int changed = requests.TryGetValue(cell.Parameter!, out var request)
                        ? writer.Set(target, request, Normalize(cell))
                        : writer.Set(target, cell.Parameter!, Normalize(cell));
                    if (changed > 0) summary.Written++; else summary.AlreadyCorrect++;
                }
                catch (Exception ex)
                {
                    summary.Failures.Add(element.Name + " · " + cell.Label + ": " + ex.Message);
                }
            }

            // 3. commit if anything changed
            if (summary.Written > 0)
            {
                transaction.Commit();
            }
            else
            {
                transaction.RollBack();
            }

            return summary;
        }

        private static ParameterRequest? Request(FillCellModel cell, PluginContext context)
        {
            var requirement = cell.Requirement;
            switch (cell.Origin.Origin)
            {
                case ValueOrigin.CustomParameter:
                    if (cell.Linked)
                    {
                        return null; // linked Revit parameter: the exporter reads it, the plugin does not write it
                    }

                    var spec = context.Profile.SpecOverrides.FirstOrDefault(o => o.PropertyType == cell.Origin.DataType!.PropertyType)?.RevitSpec;
                    return ParameterRequest.For(requirement.PropertySet!, requirement.Name!, cell.Origin.DataType!, cell.OnType, spec,
                        name: cell.Parameter);
                case ValueOrigin.StandardParameter:
                    return PluginContext.Installed.Request(cell.Parameter!)
                           ?? throw new InvalidOperationException(Tr.T("parâmetro padrão não instalado: ") + cell.Parameter);
                case ValueOrigin.AttributeOverride:
                    return Overrides.Request(cell.Parameter!);
                default:
                    return null;
            }
        }

        /// <summary>Text in IDS format (decimal point, lower-case boolean) for the ParameterWriter.</summary>
        private static string Normalize(FillCellModel cell)
        {
            string text = cell.Typed!.Trim();
            switch (IfcDataTypes.Classify(cell.DataType))
            {
                case IfcValueKind.Real:
                    return text.Replace(',', '.');
                case IfcValueKind.Boolean:
                    return text.ToLowerInvariant() is "sim" or "yes" ? "true" : text.ToLowerInvariant() is "não" or "no" ? "false" : text;
                default:
                    return text;
            }
        }
    }
}
