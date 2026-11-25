using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Autodesk.Revit.ApplicationServices;
using Autodesk.Revit.DB;
using BIMinFormation.Core.Classification;
using BIMinFormation.Core.Fill;
using BIMinFormation.Exporter;
using BIMinFormation.Rules;
using BIMinFormation.Ids;

namespace BIMinFormation.Revit.Adapter
{
    /// <summary>A parameter the plugin creates (or reuses) and binds to categories.</summary>
    public sealed class ParameterRequest
    {
        public string Name { get; }
        public ForgeTypeId Spec { get; }
        public Guid Guid { get; }
        public bool IsType { get; }

        public ParameterRequest(string name, ForgeTypeId spec, Guid guid, bool isType)
        {
            Name = name;
            Spec = spec;
            Guid = guid;
            IsType = isType;
        }

        /// <summary>Parameter for <c>Set.Property</c> with the exporter type and a deterministic GUID.</summary>
        public static ParameterRequest For(string propertySet, string property, ExporterDataType dataType, bool isType,
            string? specOverride = null, string? name = null)
        {
            string specName = specOverride ?? dataType.RevitSpec
                ?? throw new InvalidOperationException(Tr.T("Sem especificação do Revit para ") + dataType.IdsDataType);
            var spec = RevitSpecs.Resolve(specName) ?? throw new InvalidOperationException(Tr.T("Especificação desconhecida: ") + specName);
            return new ParameterRequest(
                name ?? propertySet + "." + property + (isType ? "[Type]" : ""),
                spec,
                ParameterGuid.For(propertySet, property, dataType.PropertyType!, isType),
                isType);
        }
    }

    /// <summary>
    /// Every write of the plugin to the model: creates and binds shared parameters, writes values and the export
    /// parameters. Does not open a transaction: the caller groups everything in a single transaction (undo in one step).
    /// Each write only changes what differs (applying again changes nothing) and returns how many values changed.
    /// </summary>
    public sealed class ParameterWriter
    {
        public const string GroupName = "BIMinFormation";

        private readonly Document _document;
        private readonly Application _application;
        private readonly string _parametersFile;

        public ParameterWriter(Document document, string parametersFile)
        {
            _document = document;
            _application = document.Application;
            _parametersFile = parametersFile;
        }

        /// <summary>Default plugin parameter file, in %AppData%\BIMinFormation.</summary>
        public static string DefaultParametersFile =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "BIMinFormation",
                "BIMinFormation-parametros.txt");

        /// <summary>Makes sure the parameter exists in the project and is bound to the requested categories.</summary>
        public void Ensure(ParameterRequest request, IEnumerable<Category> categories)
        {
            // binding happens with the plugin parameter file open and alive: in Revit 2025+ (.NET 8/10) the definition becomes
            // invalid ("The referenced object is not valid") when the user's file comes back or when the DefinitionFile is
            // collected by .NET before the binding (intermittent in 2027)
            string previous = _application.SharedParametersFilename;
            DefinitionFile? file = null;
            try
            {
                file = Open();
                Bind(Definition(file, request), request, categories);
            }
            finally
            {
                GC.KeepAlive(file);
                _application.SharedParametersFilename = previous;
            }
        }

        private void Bind(ExternalDefinition definition, ParameterRequest request, IEnumerable<Category> categories)
        {
            var map = _document.ParameterBindings;
            var existing = map.get_Item(definition) as ElementBinding;
            var set = _application.Create.NewCategorySet();
            if (existing != null)
            {
                foreach (Category category in existing.Categories)
                {
                    set.Insert(category);
                }
            }

            bool changed = existing == null;
            foreach (var category in categories.Where(c => c.AllowsBoundParameters))
            {
                if (!set.Contains(category))
                {
                    set.Insert(category);
                    changed = true;
                }
            }

            if (!changed)
            {
                return;
            }

            Binding binding = request.IsType
                ? (Binding)_application.Create.NewTypeBinding(set)
                : _application.Create.NewInstanceBinding(set);
            if (existing == null)
            {
                map.Insert(definition, binding, GroupTypeId.Ifc);
            }
            else
            {
                map.ReInsert(definition, binding, GroupTypeId.Ifc);
            }
        }

        /// <summary>Opens the plugin parameter file (the caller restores the user's previous file).</summary>
        private DefinitionFile Open()
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_parametersFile)!);
            if (!File.Exists(_parametersFile))
            {
                File.WriteAllText(_parametersFile, "");
            }

            _application.SharedParametersFilename = _parametersFile;
            return _application.OpenSharedParameterFile()
                   ?? throw new InvalidOperationException(Tr.T("O Revit não abriu o arquivo de parâmetros ") + _parametersFile);
        }

        /// <summary>Definition of the request in the plugin parameter file (created if missing).</summary>
        private static ExternalDefinition Definition(DefinitionFile file, ParameterRequest request)
        {
            var group = file.Groups.get_Item(GroupName) ?? file.Groups.Create(GroupName);
            foreach (var definitionGroup in file.Groups)
            {
                foreach (var definition in definitionGroup.Definitions.OfType<ExternalDefinition>())
                {
                    if (definition.GUID == request.Guid)
                    {
                        return definition;
                    }
                }
            }

            var options = new ExternalDefinitionCreationOptions(request.Name, request.Spec)
            {
                GUID = request.Guid,
                Description = Tr.T("Criado pelo BIMinFormation"),
            };
            return (ExternalDefinition)group.Definitions.Create(options);
        }

        /// <summary>Writes to the shared parameter of the request, found by GUID (does not depend on the name in the project).</summary>
        public int Set(Element element, ParameterRequest request, string text) =>
            Set(element, element.get_Parameter(request.Guid) ?? element.LookupParameter(request.Name), request.Name, text);

        /// <summary>Writes the value text (in IDS format, SI) to the parameter. Returns 1 if it changed, 0 if it was already the same.</summary>
        public int Set(Element element, string parameterName, string text) =>
            Set(element, ModelReader.Lookup(element, parameterName), parameterName, text);

        private static int Set(Element element, Parameter? parameter, string parameterName, string text)
        {
            if (parameter == null)
            {
                throw new InvalidOperationException(Tr.T("Parâmetro '") + parameterName + Tr.T("' não existe em ") + element.Id);
            }

            if (parameter.IsReadOnly)
            {
                throw new InvalidOperationException(Tr.T("Parâmetro '") + parameterName + Tr.T("' é somente leitura em ") + element.Id);
            }

            switch (parameter.StorageType)
            {
                case StorageType.String:
                    if (parameter.AsString() == text) return 0;
                    parameter.Set(text);
                    return 1;
                case StorageType.Integer:
                    int integer = parameter.Definition.GetDataType() == SpecTypeId.Boolean.YesNo
                        ? (text == "true" || text == "1" ? 1 : text == "false" || text == "0" ? 0 : throw new FormatException(text))
                        : int.Parse(text, NumberStyles.Integer, CultureInfo.InvariantCulture);
                    if (parameter.HasValue && parameter.AsInteger() == integer) return 0;
                    parameter.Set(integer);
                    return 1;
                case StorageType.Double:
                    double si = double.Parse(text, NumberStyles.Float, CultureInfo.InvariantCulture);
                    double value = RevitSpecs.FromSi(si, parameter.Definition.GetDataType());
                    if (parameter.HasValue && Math.Abs(parameter.AsDouble() - value) <= 1e-9 * Math.Max(1, Math.Abs(value))) return 0;
                    parameter.Set(value);
                    return 1;
                default:
                    throw new InvalidOperationException(Tr.T("Parâmetro '") + parameterName + Tr.T("' não guarda valor simples."));
            }
        }

        /// <summary>
        /// Applies the Classification: entity and predefined type on the instance (<c>IFC_EXPORT_ELEMENT_AS</c>,
        /// <c>IFC_EXPORT_PREDEFINEDTYPE</c>), <c>IfcObjectType</c> in the fallback and the identity properties. The identity
        /// parameters must exist (see <see cref="Ensure"/>).
        /// </summary>
        public int Apply(Element element, ClassificationAction action, Func<IdentityWrite, ParameterRequest>? identity = null)
        {
            if (!action.CanApply || action.NeedsPredefinedChoice)
            {
                throw new InvalidOperationException(Tr.T("A ficha ") + action.Specification.Name + Tr.T(" não pode ser aplicada: ")
                                                    + string.Join(" ", action.Problems));
            }

            int changes = 0;
            if (action.ExportAs != null)
            {
                changes += SetBuiltIn(element, BuiltInParameter.IFC_EXPORT_ELEMENT_AS, action.ExportAs);
            }

            if (action.PredefinedType != null)
            {
                changes += SetBuiltIn(element, BuiltInParameter.IFC_EXPORT_PREDEFINEDTYPE, action.PredefinedType);
            }

            if (action.ObjectType != null)
            {
                changes += Set(element, "IfcObjectType", action.ObjectType);
            }

            foreach (var write in action.Properties)
            {
                changes += identity != null
                    ? Set(element, identity(write), write.Value)
                    : Set(element, write.PropertySet + "." + write.Property, write.Value);
            }

            foreach (var attribute in action.Attributes)
            {
                if (ValueOrigins.AttributeOverrides.TryGetValue(attribute.Key, out string? parameter))
                {
                    changes += Set(element, parameter, attribute.Value);
                }
            }

            foreach (string code in action.ClassificationCodes)
            {
                changes += Set(element, "ClassificationCode", code);
            }

            return changes;
        }

        /// <summary>
        /// Undoes the Classification of a specification: clears what <see cref="Apply"/> writes for it, only where the element
        /// still has exactly that value (a value changed by hand since then stays). Text parameters become empty, which the
        /// exporter treats as no value; the entity and predefined type go back to the category default. Returns how many
        /// parameters were cleared.
        /// </summary>
        public int Clear(Element element, ClassificationAction action, Func<IdentityWrite, ParameterRequest>? identity = null)
        {
            int changes = 0;
            if (action.ExportAs != null) changes += ClearIf(element.get_Parameter(BuiltInParameter.IFC_EXPORT_ELEMENT_AS), action.ExportAs);
            if (action.PredefinedType != null) changes += ClearIf(element.get_Parameter(BuiltInParameter.IFC_EXPORT_PREDEFINEDTYPE), action.PredefinedType);
            if (action.ObjectType != null) changes += ClearIf(element.LookupParameter("IfcObjectType"), action.ObjectType);
            foreach (var write in action.Properties)
            {
                var request = identity?.Invoke(write);
                var parameter = request == null
                    ? element.LookupParameter(write.PropertySet + "." + write.Property)
                    : element.get_Parameter(request.Guid) ?? element.LookupParameter(request.Name);
                changes += ClearIf(parameter, write.Value);
            }

            foreach (var attribute in action.Attributes)
            {
                if (ValueOrigins.AttributeOverrides.TryGetValue(attribute.Key, out string? parameter))
                {
                    changes += ClearIf(ModelReader.Lookup(element, parameter), attribute.Value);
                }
            }

            foreach (string code in action.ClassificationCodes)
            {
                changes += ClearIf(element.LookupParameter("ClassificationCode"), code);
            }

            return changes;
        }

        private static int ClearIf(Parameter? parameter, string value)
        {
            if (parameter == null || parameter.IsReadOnly || parameter.StorageType != StorageType.String) return 0;
            string? current = parameter.AsString();
            if (string.IsNullOrEmpty(current) || !string.Equals(current, value, StringComparison.Ordinal)) return 0;
            parameter.Set("");
            return 1;
        }

        private static int SetBuiltIn(Element element, BuiltInParameter id, string value)
        {
            var parameter = element.get_Parameter(id)
                ?? throw new InvalidOperationException(Tr.T("Parâmetro ") + id + Tr.T(" não existe em ") + element.Id);
            if (parameter.AsString() == value) return 0;
            parameter.Set(value);
            return 1;
        }
    }
}
