using System;
#if REVIT2025_OR_GREATER
using System.Text.Json;
using System.Text.Json.Nodes;
#else
using System.Collections.Generic;
using System.Web.Script.Serialization;
#endif

namespace BIMinFormation.Revit.Adapter
{
    /// <summary>
    /// JSON object editable by key (configuration of the Revit export window). In 2023 (.NET Framework) through
    /// JavaScriptSerializer, the same the window uses; in 2025+ (.NET 8/10) through System.Text.Json. Unknown keys pass
    /// through untouched.
    /// </summary>
    internal sealed class JsonMap
    {
#if REVIT2025_OR_GREATER
        private readonly JsonObject _values;

        private JsonMap(JsonObject values) => _values = values;

        public static JsonMap? Parse(string json)
        {
            try
            {
                return JsonNode.Parse(json) is JsonObject values ? new JsonMap(values) : null;
            }
            catch (JsonException)
            {
                return null;
            }
        }

        public string? GetString(string key) =>
            _values.TryGetPropertyValue(key, out var node) && node is JsonValue value && value.TryGetValue(out string? text) ? text : null;

        public void Set(string key, string value) => _values[key] = value;

        public void Set(string key, bool value) => _values[key] = value;

        public void Set(string key, int value) => _values[key] = value;

        public override string ToString() => _values.ToJsonString();
#else
        private static readonly JavaScriptSerializer Serializer = new JavaScriptSerializer();
        private readonly Dictionary<string, object> _values;

        private JsonMap(Dictionary<string, object> values) => _values = values;

        public static JsonMap? Parse(string json)
        {
            try
            {
                var values = Serializer.Deserialize<Dictionary<string, object>>(json);
                return values == null ? null : new JsonMap(values);
            }
            catch (ArgumentException)
            {
                return null;
            }
            catch (InvalidOperationException)
            {
                return null;
            }
        }

        public string? GetString(string key) => _values.TryGetValue(key, out object value) ? value as string : null;

        public void Set(string key, string value) => _values[key] = value;

        public void Set(string key, bool value) => _values[key] = value;

        public void Set(string key, int value) => _values[key] = value;

        public override string ToString() => Serializer.Serialize(_values);
#endif
    }
}
