using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using BIMinFormation.Core.Classification;
using BIMinFormation.Exporter;
using BIMinFormation.Ids;
using BIMinFormation.Revit.Adapter;
using BIMinFormation.Rules;

namespace BIMinFormation.Revit.Services
{
    /// <summary>A row of the Classification grid.</summary>
    public sealed class ClassificationRow
    {
        public ElementId Id { get; set; } = ElementId.InvalidElementId;
        public int Order { get; set; }
        public string Name { get; set; } = "";
        public string Category { get; set; } = "";
        public string TypeName { get; set; } = "";
        public string Level { get; set; } = "";
        public string? CurrentEntity { get; set; }
        public string? CurrentPredefined { get; set; }
        public EntityOrigin Origin { get; set; }
        public RecognitionResult? Recognition { get; set; }

        /// <summary>Specification chosen and not applied yet.</summary>
        public ClassificationAction? Chosen { get; set; }

        public string ElementText => Name + " · " + Category + (TypeName.Length > 0 ? " · " + TypeName : "");

        public string SpecText
        {
            get
            {
                if (Chosen != null)
                {
                    return Chosen.Specification.Name + (Chosen.NeedsPredefinedChoice ? Tr.T(" (escolha o tipo predefinido)") : "");
                }

                if (Recognition == null)
                {
                    return "";
                }

                switch (Recognition.Status)
                {
                    case RecognitionStatus.Classified:
                        return Recognition.Specifications[0].Name;
                    case RecognitionStatus.Multiple:
                        return Recognition.Specifications.Count + Tr.T(" fichas aplicáveis");
                    case RecognitionStatus.OrphanIdentity:
                        return Tr.T("(sem ficha: ") + Recognition.OrphanValue + ")";
                    default:
                        return "";
                }
            }
        }

        /// <summary>Text of the IFCEXPORTAS column.</summary>
        public string ExportAsText
        {
            get
            {
                string Current() => CurrentEntity == null
                    ? Tr.T("(vazio)")
                    : CurrentEntity + (CurrentPredefined != null ? " (" + CurrentPredefined + ")" : "");

                if (Chosen == null)
                {
                    switch (Origin)
                    {
                        case EntityOrigin.Instance: return Current();
                        case EntityOrigin.Type: return Tr.T("(tipo) ") + Current();
                        case EntityOrigin.Inferred: return Tr.T("(inferido) ") + Current();
                        default: return Tr.T("(vazio)");
                    }
                }

                if (Chosen.Entity == null)
                {
                    return Chosen.Problems.Any(p => p == Tr.T("A ficha aceita mais de uma entidade: escolha a entidade à mão.")) ? Tr.T("(alternativa: escolha à mão)") : Tr.T("(ficha sem entity)");
                }

                string target = Chosen.Entity + (Chosen.PredefinedType != null ? " (" + Chosen.PredefinedType + ")" : "");
                bool sameEntity = Origin == EntityOrigin.Instance && string.Equals(CurrentEntity, Chosen.Entity, StringComparison.OrdinalIgnoreCase);
                if (sameEntity && (Chosen.PredefinedType == null || CurrentPredefined == Chosen.PredefinedType))
                {
                    return target + Tr.T(" (já)");
                }

                if (sameEntity)
                {
                    return Current() + " → " + Chosen.PredefinedType;
                }

                return "→ " + target;
            }
        }

        public string ToolTip =>
            "ID " + Id.IdValue() + (Level.Length > 0 ? Tr.T(" · nível ") + Level : "")
            + (Recognition != null && Recognition.Specifications.Count > 1
                ? "\n" + string.Join("\n", Recognition.Specifications.Select(s => s.Name))
                : "")
            + (Chosen != null && Chosen.Problems.Count > 0 ? "\n" + string.Join("\n", Chosen.Problems) : "");

        public bool HasSpec => Chosen != null || (Recognition != null && Recognition.Specifications.Count > 0);
    }

    public sealed class ApplySummary
    {
        public int Applied { get; set; }
        public int Unchanged { get; set; }
        public List<string> Failures { get; } = new List<string>();
        public List<string> Notes { get; } = new List<string>();

        public override string ToString() =>
            Tr.T("Aplicados: ") + Applied + Tr.T("\nSem alterações: ") + Unchanged + Tr.T("\nFalhas: ") + Failures.Count
            + (Failures.Count > 0 ? "\n\n" + string.Join("\n", Failures.Take(20)) : "")
            + (Notes.Count > 0 ? "\n\n" + string.Join("\n", Notes.Distinct().Take(10)) : "");
    }

    /// <summary>Builds the Classification grid and applies the chosen specifications.</summary>
    public static class ClassificationService
    {
        public static List<ClassificationRow> BuildRows(Document document, PluginContext context)
        {
            var model = ModelCache.Read(document, context);
            var recognizer = new Recognizer(context.Library.Specifications);
            var rows = new List<ClassificationRow>();
            int order = 0;
            foreach (var element in ModelReader.ModelElements(document))
            {
                var type = document.GetElement(element.GetTypeId()) as ElementType;
                var (entity, origin) = ModelReader.Entity(element, type);
                var state = model.Find(element.Id.IdValue().ToString());
                rows.Add(new ClassificationRow
                {
                    Id = element.Id,
                    Order = order++,
                    Name = element.Name,
                    Category = element.Category?.Name ?? "",
                    TypeName = type?.Name ?? "",
                    Level = (document.GetElement(element.LevelId) as Level)?.Name ?? "",
                    CurrentEntity = entity,
                    CurrentPredefined = ModelReader.Text(element, BuiltInParameter.IFC_EXPORT_PREDEFINEDTYPE)
                                        ?? (type == null ? null : ModelReader.Text(type, BuiltInParameter.IFC_EXPORT_PREDEFINEDTYPE_TYPE)),
                    Origin = origin,
                    Recognition = state == null ? null : recognizer.Recognize(state, model),
                });
            }

            // the building: Project Information, which the exporter writes as IfcBuilding. Offered so that a specification
            // applied by a property (an identity such as an EOI) can be written there; the entity is fixed
            var info = document.ProjectInformation;
            if (info != null)
            {
                var state = model.Find(info.Id.IdValue().ToString());
                rows.Add(new ClassificationRow
                {
                    Id = info.Id,
                    Order = order++,
                    Name = Tr.T("Edifício (Informações do projeto)"),
                    Category = info.Category?.Name ?? "",
                    CurrentEntity = "IfcBuilding",
                    Origin = EntityOrigin.Inferred,
                    Recognition = state == null ? null : recognizer.Recognize(state, model),
                });
            }

            return rows;
        }

        /// <summary>Writes, in a single transaction, every row with a chosen specification (not only the selection).</summary>
        public static ApplySummary Apply(Document document, PluginContext context, IReadOnlyList<ClassificationRow> rows, string parametersFile)
        {
            var summary = new ApplySummary();
            var pending = rows.Where(r => r.Chosen != null).ToList();
            if (pending.Count == 0)
            {
                return summary;
            }

            var writer = new ParameterWriter(document, parametersFile);
            var categories = pending.Select(r => document.GetElement(r.Id)?.Category).Where(c => c != null).Cast<Category>()
                .GroupBy(c => c.Id.IdValue()).Select(g => g.First()).ToList();

            using var transaction = new Transaction(document, Tr.T("BIMinFormation: Classificação"));
            transaction.Start();

            // 1. identity parameters (and the fallback IfcObjectType), bound to the union of the batch categories
            ParameterRequest Identity(Core.Classification.IdentityWrite write)
            {
                var dataType = DataTypeMap.Resolve(write.DataType ?? "IFCLABEL", context.Exporter);
                string name = context.Mapping.NameFor(write.PropertySet, write.Property, dataType.PropertyType ?? "Label", ParameterScope.Instance);
                return ParameterRequest.For(write.PropertySet, write.Property, dataType, false, name: name);
            }

            foreach (var write in pending.SelectMany(r => r.Chosen!.Properties).GroupBy(w => w.PropertySet + "/" + w.Property).Select(g => g.First()))
            {
                writer.Ensure(Identity(write), categories);
            }

            if (pending.Any(r => r.Chosen!.ObjectType != null))
            {
                writer.Ensure(Overrides.Request("IfcObjectType"), categories);
            }

            foreach (string attribute in pending.SelectMany(r => r.Chosen!.Attributes.Keys).Distinct())
            {
                if (ValueOrigins.AttributeOverrides.TryGetValue(attribute, out var parameter))
                {
                    writer.Ensure(Overrides.Request(parameter), categories);
                }
            }

            if (pending.Any(r => r.Chosen!.ClassificationCodes.Count > 0))
            {
                writer.Ensure(Overrides.Request("ClassificationCode"), categories);
            }

            // 2. entity, predefined type and identity, element by element
            foreach (var row in pending)
            {
                var element = document.GetElement(row.Id);
                if (element == null)
                {
                    summary.Failures.Add(row.Id.IdValue() + Tr.T(": elemento não existe mais"));
                    continue;
                }

                var action = row.Chosen!;
                if (!action.CanApply || action.NeedsPredefinedChoice)
                {
                    summary.Failures.Add(row.ElementText + ": " + (action.NeedsPredefinedChoice
                        ? Tr.T("escolha o tipo predefinido da ficha")
                        : string.Join(" ", action.Problems)));
                    continue;
                }

                bool building = element is ProjectInfo;
                if (building && action.Entity != null && !string.Equals(action.Entity, "IfcBuilding", StringComparison.OrdinalIgnoreCase))
                {
                    summary.Failures.Add(row.ElementText + ": " + Tr.T("as Informações do projeto são o edifício (IfcBuilding); escolha uma ficha de edifício."));
                    continue;
                }

                try
                {
                    int changes = writer.Apply(element, action, Identity, writeEntity: !building);
                    if (changes > 0) summary.Applied++; else summary.Unchanged++;

                    // what the exporter will not do with this element, said now instead of only in the IFC check
                    var type = document.GetElement(element.GetTypeId()) as ElementType;
                    if (action.PredefinedType != null && action.Entity != null && ModelReader.DropsPredefinedType(type, action.Entity, context.SchemaCatalog))
                    {
                        summary.Notes.Add(row.ElementText + ": " + Tr.T("o exportador do Revit não escreve o tipo predefinido de ") + action.Entity
                                          + Tr.T(" num elemento sem tipo (forma genérica); use uma família com tipo."));
                    }

                    string? exported = action.Entity == null ? null : ModelReader.ExportedEntity(action.Entity, context.SchemaCatalog);
                    if (exported != null && !string.Equals(exported, action.Entity, StringComparison.OrdinalIgnoreCase))
                    {
                        summary.Notes.Add(row.ElementText + ": " + Tr.T("o exportador do Revit escreve ") + action.Entity + Tr.T(" como ") + exported + ".");
                    }

                    if (ModelReader.FailsExport(element, action.Entity))
                    {
                        summary.Notes.Add(row.ElementText + ": " + Tr.T("o exportador do Revit não exporta IfcRamp de um elemento que não é rampa do Revit (o elemento fica fora do IFC)."));
                    }
                }
                catch (Exception ex)
                {
                    summary.Failures.Add(row.ElementText + ": " + ex.Message);
                }
            }

            // the applied specification joins the specifications in use (if the list was computed): the mapping now covers it
            var inUse = context.Profile.SpecificationsInUse;
            var added = pending.Select(r => r.Chosen!.Specification.Name).Where(n => inUse != null && !inUse.Contains(n)).Distinct().ToList();
            if (summary.Applied > 0 && added.Count > 0)
            {
                var profile = Core.Profile.ProjectProfile.FromJson(context.Profile.ToJson());
                profile.SpecificationsInUse!.AddRange(added);
                ProfileStorage.Save(document, profile);
            }

            if (summary.Applied > 0)
            {
                transaction.Commit();
            }
            else
            {
                transaction.RollBack();
            }

            return summary;
        }
    }

    /// <summary>Exporter override parameters (text), with a deterministic GUID.</summary>
    public static class Overrides
    {
        public static ParameterRequest Request(string name) =>
            new ParameterRequest(name, SpecTypeId.String.Text,
                Core.Fill.ParameterGuid.Create(Core.Fill.ParameterGuid.Namespace, "override:" + name), false);
    }
}
