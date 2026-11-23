using Autodesk.Revit.DB;

namespace BIMinFormation.Revit
{
    /// <summary>
    /// Element id in both APIs: in 2023, <c>IntegerValue</c> (32 bits); from 2024 on, <c>Value</c> (64 bits;
    /// <c>IntegerValue</c> becomes obsolete). The plugin always uses 64 bits.
    /// </summary>
    internal static class RevitIds
    {
        public static long IdValue(this ElementId id) =>
#if REVIT2025_OR_GREATER
            id.Value;
#else
            id.IntegerValue;
#endif

        public static ElementId ToElementId(this long value) =>
#if REVIT2025_OR_GREATER
            new ElementId(value);
#else
            new ElementId((int)value);
#endif
    }
}
