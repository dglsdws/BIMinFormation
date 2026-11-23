using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.ExtensibleStorage;
using Autodesk.Revit.UI;
using BIMinFormation.Core.Mapping;
using BIMinFormation.Ids;

namespace BIMinFormation.Revit.Adapter
{
    /// <summary>
    /// Saved configuration of the Revit IFC export window (revit-ifc, <c>IFCExportConfigurationsMap</c>): each
    /// configuration is a <see cref="DataStorage"/> with the JSON schema <c>C2A3E6FE-…</c> and the <c>MapField</c> field,
    /// the same from 2023 to 2027. The plugin writes its own with the <see cref="ExportSetup"/> fields and keeps the rest
    /// (what the user changed in the window stays). The Revit window does the export.
    /// </summary>
    public static class ExportConfiguration
    {
        private static readonly Guid SchemaId = new Guid("C2A3E6FE-CE51-4F35-8FF1-20C34567B687");
        private const string Field = "MapField";
        private const string UiApplication = "BIM.IFC.Export.UI.IFCCommandOverrideApplication";

        /// <summary>2023 exporter default (<c>IFCExportConfiguration.CreateDefaultConfiguration</c>), base of a new configuration.</summary>
        private const string Template =
            "{\"IFCVersion\":21,\"ExchangeRequirement\":3,\"IFCFileType\":0,\"ActivePhaseId\":-1,\"SpaceBoundaries\":0,"
            + "\"SplitWallsAndColumns\":false,\"IncludeSteelElements\":true,\"ProjectAddress\":{\"UpdateProjectInformation\":false,"
            + "\"AssignAddressToSite\":false,\"AssignAddressToBuilding\":true},\"Export2DElements\":false,\"ExportLinkedFiles\":false,"
            + "\"VisibleElementsOfCurrentView\":false,\"ExportRoomsInView\":false,\"ExportInternalRevitPropertySets\":false,"
            + "\"ExportIFCCommonPropertySets\":true,\"ExportBaseQuantities\":false,\"ExportSchedulesAsPsets\":false,"
            + "\"ExportSpecificSchedules\":false,\"ExportUserDefinedPsets\":false,\"ExportUserDefinedPsetsFileName\":\"\","
            + "\"ExportUserDefinedParameterMapping\":false,\"ExportUserDefinedParameterMappingFileName\":\"\","
            + "\"ClassificationSettings\":{\"ClassificationName\":null,\"ClassificationEdition\":null,\"ClassificationSource\":null,"
            + "\"ClassificationEditionDate\":\"\\/Date(-62135589600000)\\/\",\"ClassificationLocation\":null,\"ClassificationFieldName\":null},"
            + "\"TessellationLevelOfDetail\":0.5,\"ExportPartsAsBuildingElements\":false,\"ExportSolidModelRep\":false,"
            + "\"UseActiveViewGeometry\":false,\"UseFamilyAndTypeNameForReference\":false,\"Use2DRoomBoundaryForVolume\":false,"
            + "\"IncludeSiteElevation\":false,\"StoreIFCGUID\":false,\"ExportBoundingBox\":false,\"UseOnlyTriangulation\":false,"
            + "\"UseTypeNameOnlyForIfcType\":false,\"UseVisibleRevitNameAsEntityName\":false,\"SelectedSite\":null,\"SitePlacement\":0,"
            + "\"GeoRefCRSName\":\"\",\"GeoRefCRSDesc\":\"\",\"GeoRefEPSGCode\":\"\",\"GeoRefGeodeticDatum\":\"\",\"GeoRefMapUnit\":\"\","
            + "\"ExcludeFilter\":\"\",\"COBieCompanyInfo\":\"\",\"COBieProjectInfo\":\"\",\"Name\":\"\"}";

        private static Schema Schema()
        {
            // same definition as the Revit window: name and a text field, default access
            var existing = Autodesk.Revit.DB.ExtensibleStorage.Schema.Lookup(SchemaId);
            if (existing != null) return existing;
            var builder = new SchemaBuilder(SchemaId);
            builder.SetSchemaName("IFCExportConfigurationMap");
            builder.AddSimpleField(Field, typeof(string));
            return builder.Finish();
        }

        /// <summary>Configurations saved in the project (name → JSON).</summary>
        public static Dictionary<string, string> Saved(Document document)
        {
            var result = new Dictionary<string, string>(StringComparer.Ordinal);
            var schema = Autodesk.Revit.DB.ExtensibleStorage.Schema.Lookup(SchemaId);
            if (schema == null) return result;
            foreach (var (_, json, values) in Entries(document, schema))
            {
                if (values.GetString("Name") is string text) result[text] = json;
            }

            return result;
        }

        private static IEnumerable<(DataStorage Storage, string Json, JsonMap Values)> Entries(Document document, Schema schema)
        {
            foreach (var storage in new FilteredElementCollector(document).OfClass(typeof(DataStorage)).Cast<DataStorage>())
            {
                var entity = storage.GetEntity(schema);
                if (entity == null || !entity.IsValid()) continue;
                string json = entity.Get<string>(Field);
                var values = JsonMap.Parse(json);
                if (values != null) yield return (storage, json, values);
            }
        }

        /// <summary>
        /// Creates or updates the plugin configuration in the project. Needs an open transaction. <paramref name="propertyMapping"/>:
        /// Revit 2027 parameter template the window uses instead of the set and quantity options.
        /// </summary>
        public static void Save(Document document, ExportSetup setup, string? propertyMapping = null)
        {
            if (setup.IfcVersion == null) throw new InvalidOperationException(Tr.T("Configuração sem versão IFC."));
            var schema = Schema();
            var current = Entries(document, schema).FirstOrDefault(e => setup.Name.Equals(e.Values.GetString("Name")));

            var values = current.Values ?? JsonMap.Parse(Template)!;
            values.Set("Name", setup.Name);
            values.Set("IFCVersion", (int)Enum.Parse(typeof(IFCVersion), setup.IfcVersion));
            values.Set("IFCFileType", 0); // .ifc
            values.Set("ExportBaseQuantities", setup.BaseQuantities);
            values.Set("ExportIFCCommonPropertySets", setup.CommonPropertySets);
            values.Set("ExportInternalRevitPropertySets", setup.InternalRevitPropertySets);
            values.Set("ExportSchedulesAsPsets", false);
            values.Set("ExportUserDefinedPsets", setup.PropertySetsFile != null);
            values.Set("ExportUserDefinedPsetsFileName", setup.PropertySetsFile ?? "");
            if (propertyMapping != null) values.Set("PropertyMapping", propertyMapping);

            var storage = current.Storage ?? DataStorage.Create(document);
            var entity = new Entity(schema);
            entity.Set(Field, values.ToString());
            storage.SetEntity(entity);
        }

        /// <summary>
        /// Opens the Revit IFC export window. Tries to have the configuration already selected (the window starts with the last
        /// one used in the session, kept by the Autodesk add-in); returns whether it succeeded.
        /// </summary>
        public static bool OpenDialog(UIApplication application, string name)
        {
            bool selected = false;
            try
            {
                foreach (var loaded in application.LoadedApplications.Cast<IExternalApplication>())
                {
                    var type = loaded.GetType();
                    if (type.FullName != UiApplication) continue;
                    var field = type.GetField("m_mruConfiguration", BindingFlags.Instance | BindingFlags.NonPublic);
                    if (field != null && field.FieldType == typeof(string))
                    {
                        field.SetValue(loaded, name);
                        selected = true;
                    }

                    // an edit of the same configuration made earlier in the session would replace the one saved now
                    var last = type.Assembly.GetType("BIM.IFC.Export.UI.IFCExport")
                        ?.GetProperty("LastSelectedConfig", BindingFlags.Static | BindingFlags.Public)?.GetValue(null) as IDictionary;
                    last?.Remove(name);
                }
            }
            catch (Exception ex)
            {
                Log.Info("Export: could not preselect the configuration: " + ex.Message);
                selected = false;
            }

            application.PostCommand(RevitCommandId.LookupPostableCommandId(PostableCommand.ExportIFC));
            return selected;
        }
    }
}
