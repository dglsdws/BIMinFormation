using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using BIMinFormation.Core.Classification;
using BIMinFormation.Core.Profile;
using BIMinFormation.Revit.Adapter;
using BIMinFormation.Ids;

namespace BIMinFormation.Revit.Services
{
    /// <summary>
    /// Specifications in use in the model: the ones that apply to some element (by applicability: EOI, entity…). They go to
    /// the profile and restrict the mapping, Fill In Parameters and the PropertySets file.
    /// </summary>
    public static class SpecificationUse
    {
        /// <summary>Names of the specifications that apply to some model element, in library order.</summary>
        public static List<string> InUse(Document document, PluginContext context)
        {
            var model = ModelCache.Read(document, context);
            var recognizer = new Recognizer(context.Library.Specifications);
            var used = new HashSet<string>(model.Elements.SelectMany(e => recognizer.Recognize(e, model).Specifications).Select(s => s.Name));
            return context.Library.Specifications.Select(s => s.Name).Where(used.Contains).Distinct().ToList();
        }

        /// <summary>
        /// Recomputes the specifications in use and writes them to the profile if they changed; returns the context with the
        /// updated mapping (the same one, when nothing changed).
        /// </summary>
        public static PluginContext Refresh(Document document, PluginContext context)
        {
            var names = InUse(document, context);
            if (context.Profile.SpecificationsInUse != null && context.Profile.SpecificationsInUse.SequenceEqual(names)) return context;
            var profile = ProjectProfile.FromJson(context.Profile.ToJson());
            profile.SpecificationsInUse = names;
            using (var transaction = new Transaction(document, Tr.T("BIMinFormation: fichas em uso")))
            {
                transaction.Start();
                ProfileStorage.Save(document, profile);
                transaction.Commit();
            }

            Log.Info("Specifications in use: " + names.Count + " of " + context.Library.Specifications.Count);
            return PluginContext.Create(document, out _) ?? context;
        }
    }
}
