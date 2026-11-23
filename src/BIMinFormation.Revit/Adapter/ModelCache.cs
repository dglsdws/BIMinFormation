using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.ApplicationServices;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Events;
using BIMinFormation.Revit.Services;
using BIMinFormation.Rules;

namespace BIMinFormation.Revit.Adapter
{
    /// <summary>
    /// State of each element kept between reads: going through the parameters of every element costs ~1 ms per element in
    /// the Revit API, and the panel and windows read the model again on every change. Through DocumentChanged only the
    /// elements that were changed, created or had their type changed are read again; a different profile, specification
    /// set, schema or format (the way of reading changes) reads everything again.
    /// </summary>
    public static class ModelCache
    {
        private sealed class Entry
        {
            public Document Document = null!;
            public string Key = "";
            public bool AllDirty = true;
            public readonly Dictionary<long, ElementState> States = new Dictionary<long, ElementState>();
            public readonly Dictionary<long, long> TypeOf = new Dictionary<long, long>();
            public readonly HashSet<long> Dirty = new HashSet<long>();
        }

        private static readonly List<Entry> Entries = new List<Entry>();

        /// <summary>Elements read again in the last read (tests).</summary>
        internal static int LastReadCount { get; private set; }

        public static void Subscribe(ControlledApplication application)
        {
            application.DocumentChanged += OnChanged;
            application.DocumentClosing += (s, e) => Entries.RemoveAll(x => x.Document.Equals(e.Document));
        }

        private static Entry? Find(Document document) => Entries.FirstOrDefault(e => e.Document.Equals(document));

        private static void OnChanged(object? sender, DocumentChangedEventArgs e)
        {
            var entry = Find(e.GetDocument());
            if (entry == null || entry.AllDirty) return;
            var document = e.GetDocument();
            foreach (var id in e.GetAddedElementIds()) entry.Dirty.Add(id.IdValue());
            foreach (var id in e.GetDeletedElementIds())
            {
                entry.States.Remove(id.IdValue());
                entry.TypeOf.Remove(id.IdValue());
            }

            var changedTypes = new HashSet<long>();
            foreach (var id in e.GetModifiedElementIds())
            {
                entry.Dirty.Add(id.IdValue());
                if (document.GetElement(id) is ElementType) changedTypes.Add(id.IdValue());
            }

            if (changedTypes.Count > 0)
            {
                foreach (var pair in entry.TypeOf.Where(p => changedTypes.Contains(p.Value))) entry.Dirty.Add(pair.Key);
            }
        }

        /// <summary>The model as the IFC will see it, reading again only what changed since the last read of this document.</summary>
        public static ModelState Read(Document document, PluginContext context)
        {
            string key = context.Profile.ToJson() + "|" + context.Schema + "|" + context.Format + "|" + context.Library.Specifications.Count;
            var entry = Find(document);
            if (entry == null)
            {
                entry = new Entry { Document = document };
                Entries.Add(entry);
            }

            if (entry.Key != key)
            {
                entry.Key = key;
                entry.AllDirty = true;
            }

            var model = ModelReader.ModelElements(document);
            var elements = model.Concat(ModelReader.Storeys(document, model)).ToList();
            var toRead = elements.Where(e => entry.AllDirty || entry.Dirty.Contains(e.Id.IdValue()) || !entry.States.ContainsKey(e.Id.IdValue()))
                .ToList();
            if (entry.AllDirty)
            {
                entry.States.Clear();
                entry.TypeOf.Clear();
            }

            var read = context.Reader.Read(toRead);
            for (int i = 0; i < toRead.Count; i++)
            {
                long id = toRead[i].Id.IdValue();
                entry.States[id] = read[i];
                entry.TypeOf[id] = toRead[i].GetTypeId().IdValue();
            }

            entry.Dirty.Clear();
            entry.AllDirty = false;
            LastReadCount = toRead.Count;
            var present = new HashSet<long>(elements.Select(e => e.Id.IdValue()));
            foreach (int gone in entry.States.Keys.Where(k => !present.Contains(k)).ToList())
            {
                entry.States.Remove(gone);
                entry.TypeOf.Remove(gone);
            }

            // project, building and site from Project Information: few, read every time
            return new ModelState(context.Schema, elements.Select(e => entry.States[e.Id.IdValue()])
                .Concat(context.Reader.ReadProjectLevel(document)).ToList());
        }
    }
}
