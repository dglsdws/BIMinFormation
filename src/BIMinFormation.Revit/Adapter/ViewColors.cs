using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;

namespace BIMinFormation.Revit.Adapter
{
    /// <summary>Color of an element in the view, from the pre-validation.</summary>
    public enum ViewColor
    {
        Ok,
        ToCheck,
        Fail,
    }

    /// <summary>
    /// Pre-validation colors in the view: overrides the appearance of the elements and keeps the override each one had, to
    /// restore it when clearing. Element overrides are saved in the view; that is why the window clears them when it closes
    /// and the user is not left with the plugin colors in the model. Needs an open transaction.
    /// </summary>
    public sealed class ViewColors
    {
        public static readonly Color Red = new Color(214, 48, 49);
        public static readonly Color Orange = new Color(236, 150, 0);
        public static readonly Color Green = new Color(46, 160, 67);

        /// <summary>Original override of each colored element, per view.</summary>
        private readonly Dictionary<ElementId, Dictionary<ElementId, OverrideGraphicSettings>> _saved =
            new Dictionary<ElementId, Dictionary<ElementId, OverrideGraphicSettings>>();

        public bool IsActive => _saved.Count > 0;

        public static Color ColorOf(ViewColor color) => color switch
        {
            ViewColor.Fail => Red,
            ViewColor.ToCheck => Orange,
            _ => Green,
        };

        /// <summary>Colors the elements in the view; the ones that were colored and left the map go back to the original.</summary>
        public int Apply(View view, IReadOnlyDictionary<ElementId, ViewColor> colors)
        {
            if (!view.AreGraphicsOverridesAllowed()) return 0;
            var document = view.Document;
            if (!_saved.TryGetValue(view.Id, out var saved))
            {
                saved = new Dictionary<ElementId, OverrideGraphicSettings>();
                _saved[view.Id] = saved;
            }

            foreach (var id in saved.Keys.Where(id => !colors.ContainsKey(id)).ToList())
            {
                if (document.GetElement(id) != null) view.SetElementOverrides(id, saved[id]);
                saved.Remove(id);
            }

            var solid = SolidFill(document);
            int painted = 0;
            foreach (var pair in colors)
            {
                if (document.GetElement(pair.Key) == null) continue;
                if (!saved.ContainsKey(pair.Key)) saved[pair.Key] = view.GetElementOverrides(pair.Key);
                var color = ColorOf(pair.Value);
                var settings = new OverrideGraphicSettings(saved[pair.Key])
                    .SetProjectionLineColor(color)
                    .SetCutLineColor(color)
                    .SetSurfaceForegroundPatternColor(color)
                    .SetCutForegroundPatternColor(color);
                if (solid != null)
                {
                    settings.SetSurfaceForegroundPatternId(solid).SetCutForegroundPatternId(solid);
                }

                view.SetElementOverrides(pair.Key, settings);
                painted++;
            }

            if (saved.Count == 0) _saved.Remove(view.Id);
            return painted;
        }

        /// <summary>Restores the original appearance of every colored element, in every view.</summary>
        public int Clear(Document document)
        {
            int restored = 0;
            foreach (var pair in _saved)
            {
                if (!(document.GetElement(pair.Key) is View view)) continue;
                foreach (var element in pair.Value)
                {
                    if (document.GetElement(element.Key) == null) continue;
                    view.SetElementOverrides(element.Key, element.Value);
                    restored++;
                }
            }

            _saved.Clear();
            return restored;
        }

        private static ElementId? SolidFill(Document document) =>
            new FilteredElementCollector(document).OfClass(typeof(FillPatternElement)).Cast<FillPatternElement>()
                .FirstOrDefault(p => p.GetFillPattern().IsSolidFill)?.Id;
    }
}
