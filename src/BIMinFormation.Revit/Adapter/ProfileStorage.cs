using System;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.ExtensibleStorage;
using BIMinFormation.Core.Profile;
using BIMinFormation.Ids;

namespace BIMinFormation.Revit.Adapter
{
    /// <summary>
    /// Project profile kept in the .rvt itself, as Extensible Storage in a <see cref="DataStorage"/>. Writing needs a
    /// transaction but does not save the document: the user saves it.
    /// </summary>
    public static class ProfileStorage
    {
        public static readonly Guid SchemaGuid = new Guid("3f8d2a61-7c4b-4e19-a0d5-6b2c9e1f7a38");
        private const string Field = "ProfileJson";
        private const string StorageName = "BIMinFormation";

        private static Schema GetSchema()
        {
            var schema = Schema.Lookup(SchemaGuid);
            if (schema != null)
            {
                return schema;
            }

            var builder = new SchemaBuilder(SchemaGuid);
            builder.SetSchemaName("BIMinFormationProfile");
            builder.SetVendorId("BIMINFORMATION");
            builder.SetReadAccessLevel(AccessLevel.Public);
            builder.SetWriteAccessLevel(AccessLevel.Vendor);
            builder.SetDocumentation(Tr.T("Perfil do projeto do BIMinFormation (JSON)."));
            builder.AddSimpleField(Field, typeof(string));
            return builder.Finish();
        }

        private static DataStorage? Find(Document document)
        {
            var schema = Schema.Lookup(SchemaGuid);
            if (schema == null)
            {
                return null;
            }

            return new FilteredElementCollector(document)
                .OfClass(typeof(DataStorage))
                .Cast<DataStorage>()
                .FirstOrDefault(d => d.GetEntity(schema).IsValid());
        }

        public static ProjectProfile? Load(Document document)
        {
            var storage = Find(document);
            if (storage == null)
            {
                return null;
            }

            string json = storage.GetEntity(GetSchema()).Get<string>(Field);
            return string.IsNullOrEmpty(json) ? null : ProjectProfile.FromJson(json);
        }

        /// <summary>Writes the profile (needs an open transaction).</summary>
        public static void Save(Document document, ProjectProfile profile)
        {
            var schema = GetSchema();
            var storage = Find(document) ?? DataStorage.Create(document);
            storage.Name = StorageName;
            var entity = new Entity(schema);
            entity.Set(Field, profile.ToJson());
            storage.SetEntity(entity);
        }
    }
}
