using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace BIMinFormation.Rules.Ifc
{
    public enum StepKind
    {
        /// <summary><c>$</c></summary>
        Null,

        /// <summary><c>*</c> (redeclared derived attribute)</summary>
        Derived,
        String,

        /// <summary><c>.X.</c> (enumeration; .T./.F./.U. stay as text)</summary>
        Enumeration,
        Integer,
        Real,
        Binary,
        Reference,
        List,

        /// <summary>Value of a defined type inside a SELECT, e.g. <c>IFCLABEL('x')</c>.</summary>
        Typed,
    }

    /// <summary>An argument of a STEP instance (ISO 10303-21).</summary>
    public sealed class StepValue
    {
        public StepKind Kind { get; }
        public string? Text { get; }
        public long Integer { get; }
        public double Real { get; }

        /// <summary>Number of the referenced instance (<see cref="StepKind.Reference"/>).</summary>
        public long Reference { get; }

        public IReadOnlyList<StepValue> Items { get; }

        /// <summary>Type name (upper case) and value of a <see cref="StepKind.Typed"/>.</summary>
        public string? TypeName { get; }

        public StepValue? Inner { get; }

        private StepValue(StepKind kind, string? text = null, long integer = 0, double real = 0, long reference = 0,
            IReadOnlyList<StepValue>? items = null, string? typeName = null, StepValue? inner = null)
        {
            Kind = kind;
            Text = text;
            Integer = integer;
            Real = real;
            Reference = reference;
            Items = items ?? Array.Empty<StepValue>();
            TypeName = typeName;
            Inner = inner;
        }

        public static readonly StepValue Null = new StepValue(StepKind.Null);
        public static readonly StepValue Derived = new StepValue(StepKind.Derived);

        public static StepValue OfString(string text) => new StepValue(StepKind.String, text: text);
        public static StepValue OfEnumeration(string text) => new StepValue(StepKind.Enumeration, text: text);
        public static StepValue OfBinary(string text) => new StepValue(StepKind.Binary, text: text);
        public static StepValue OfInteger(long value) => new StepValue(StepKind.Integer, integer: value, real: value);
        public static StepValue OfReal(double value) => new StepValue(StepKind.Real, real: value);
        public static StepValue OfReference(long id) => new StepValue(StepKind.Reference, reference: id);
        public static StepValue OfList(IReadOnlyList<StepValue> items) => new StepValue(StepKind.List, items: items);
        public static StepValue OfTyped(string type, StepValue inner) => new StepValue(StepKind.Typed, typeName: type, inner: inner);

        public bool IsNull => Kind == StepKind.Null || Kind == StepKind.Derived;

        public override string ToString() => Kind switch
        {
            StepKind.String => "'" + Text + "'",
            StepKind.Enumeration => "." + Text + ".",
            StepKind.Integer => Integer.ToString(CultureInfo.InvariantCulture),
            StepKind.Real => Real.ToString("R", CultureInfo.InvariantCulture),
            StepKind.Reference => "#" + Reference,
            StepKind.List => "(" + string.Join(",", Items) + ")",
            StepKind.Typed => TypeName + "(" + Inner + ")",
            StepKind.Derived => "*",
            _ => "$",
        };
    }

    /// <summary>
    /// IFC file in STEP text (ISO 10303-21). Indexes the instances (number, type and span) in one pass and only parses the
    /// arguments of an instance when asked, to cope with large files (geometry is never read).
    /// </summary>
    public sealed class StepFile
    {
        private readonly byte[] _data;
        private readonly Dictionary<long, (int Start, int End, string Type)> _index = new Dictionary<long, (int, int, string)>();
        private readonly Dictionary<long, IReadOnlyList<StepValue>> _parsed = new Dictionary<long, IReadOnlyList<StepValue>>();
        private readonly List<long> _ids = new List<long>();

        /// <summary>Header schema (e.g. <c>IFC4X3_ADD2</c>).</summary>
        public string Schema { get; private set; } = "";

        public IReadOnlyList<long> Ids => _ids;

        private StepFile(byte[] data)
        {
            _data = data;
        }

        public static StepFile Open(string path) => Parse(File.ReadAllBytes(path));

        public static StepFile Parse(byte[] data)
        {
            var file = new StepFile(data);
            file.Index();
            return file;
        }

        public bool Contains(long id) => _index.ContainsKey(id);

        /// <summary>Instance type in upper case (e.g. <c>IFCWALL</c>), or <c>null</c>.</summary>
        public string? TypeOf(long id) => _index.TryGetValue(id, out var entry) ? entry.Type : null;

        /// <summary>Instance arguments (parsed the first time and kept).</summary>
        public IReadOnlyList<StepValue> Arguments(long id)
        {
            if (_parsed.TryGetValue(id, out var cached)) return cached;
            if (!_index.TryGetValue(id, out var entry)) return Array.Empty<StepValue>();
            int position = entry.Start;
            var value = ParseValue(ref position);
            var result = value.Kind == StepKind.List ? value.Items : new[] { value };
            _parsed[id] = result;
            return result;
        }

        /// <summary>Releases the arguments already parsed (reading the properties of a large model keeps many).</summary>
        public void ClearCache() => _parsed.Clear();

        // ---- index: #id = TYPE( ... ) ;
        private void Index()
        {
            int length = _data.Length;
            Schema = ReadSchema();
            int dataStart = IndexOf("DATA;", 0);
            int position = dataStart < 0 ? 0 : dataStart + 5;
            while (position < length)
            {
                SkipSpaceAndComments(ref position);
                if (position >= length) break;
                byte c = _data[position];
                if (c != (byte)'#')
                {
                    // ENDSEC; or other text outside an instance
                    if (Matches(position, "ENDSEC")) break;
                    position++;
                    continue;
                }

                position++;
                long id = 0;
                while (position < length && _data[position] >= (byte)'0' && _data[position] <= (byte)'9')
                {
                    id = id * 10 + (_data[position] - '0');
                    position++;
                }

                SkipSpaceAndComments(ref position);
                if (position >= length || _data[position] != (byte)'=') continue;
                position++;
                SkipSpaceAndComments(ref position);
                int typeStart = position;
                while (position < length && (IsIdentifier(_data[position]))) position++;
                string type = TypeName(typeStart, position - typeStart);
                SkipSpaceAndComments(ref position);
                int argumentsStart = position;
                int end = FindInstanceEnd(position);
                if (type.Length > 0 && !_index.ContainsKey(id))
                {
                    _index[id] = (argumentsStart, end, type);
                    _ids.Add(id);
                }

                position = end + 1;
            }

            _ids.Sort();
        }

        private readonly Dictionary<ulong, string> _typeNames = new Dictionary<ulong, string>();

        /// <summary>Type name in upper case, reusing the same string for every instance of the type.</summary>
        private string TypeName(int start, int length)
        {
            ulong hash = 14695981039346656037UL ^ (ulong)length;
            for (int i = 0; i < length; i++)
            {
                byte c = _data[start + i];
                if (c >= (byte)'a' && c <= (byte)'z') c = (byte)(c - 32);
                hash = (hash ^ c) * 1099511628211UL;
            }

            if (_typeNames.TryGetValue(hash, out var name) && name.Length == length && SameName(name, start)) return name;
            name = Encoding.ASCII.GetString(_data, start, length).ToUpperInvariant();
            _typeNames[hash] = name;
            return name;
        }

        private bool SameName(string name, int start)
        {
            for (int i = 0; i < name.Length; i++)
            {
                byte c = _data[start + i];
                if (c >= (byte)'a' && c <= (byte)'z') c = (byte)(c - 32);
                if (c != name[i]) return false;
            }

            return true;
        }

        private string ReadSchema()
        {
            int at = IndexOf("FILE_SCHEMA", 0);
            if (at < 0) return "";
            int quote = Array.IndexOf(_data, (byte)'\'', at);
            if (quote < 0) return "";
            int close = Array.IndexOf(_data, (byte)'\'', quote + 1);
            return close < 0 ? "" : Encoding.ASCII.GetString(_data, quote + 1, close - quote - 1).Trim().ToUpperInvariant();
        }

        /// <summary>Position of the ';' that closes the instance (skipping quoted text and comments).</summary>
        private int FindInstanceEnd(int position)
        {
            int length = _data.Length;
            while (position < length)
            {
                byte c = _data[position];
                if (c == (byte)'\'')
                {
                    position++;
                    while (position < length)
                    {
                        if (_data[position] == (byte)'\'')
                        {
                            if (position + 1 < length && _data[position + 1] == (byte)'\'')
                            {
                                position += 2;
                                continue;
                            }

                            break;
                        }

                        position++;
                    }
                }
                else if (c == (byte)'/' && position + 1 < length && _data[position + 1] == (byte)'*')
                {
                    int close = IndexOf("*/", position + 2);
                    position = close < 0 ? length : close + 1;
                }
                else if (c == (byte)';')
                {
                    return position;
                }

                position++;
            }

            return length - 1;
        }

        // ---- arguments
        private StepValue ParseValue(ref int position)
        {
            SkipSpaceAndComments(ref position);
            byte c = _data[position];
            switch (c)
            {
                case (byte)'$':
                    position++;
                    return StepValue.Null;
                case (byte)'*':
                    position++;
                    return StepValue.Derived;
                case (byte)'\'':
                    return StepValue.OfString(ParseString(ref position));
                case (byte)'"':
                {
                    int close = Array.IndexOf(_data, (byte)'"', position + 1);
                    string text = Encoding.ASCII.GetString(_data, position + 1, close - position - 1);
                    position = close + 1;
                    return StepValue.OfBinary(text);
                }

                case (byte)'.':
                {
                    int close = Array.IndexOf(_data, (byte)'.', position + 1);
                    string text = Encoding.ASCII.GetString(_data, position + 1, close - position - 1).Trim();
                    position = close + 1;
                    return StepValue.OfEnumeration(text);
                }

                case (byte)'#':
                {
                    position++;
                    long id = 0;
                    while (_data[position] >= (byte)'0' && _data[position] <= (byte)'9')
                    {
                        id = id * 10 + (_data[position] - '0');
                        position++;
                    }

                    return StepValue.OfReference(id);
                }

                case (byte)'(':
                {
                    position++;
                    var items = new List<StepValue>();
                    SkipSpaceAndComments(ref position);
                    if (_data[position] == (byte)')')
                    {
                        position++;
                        return StepValue.OfList(items);
                    }

                    while (true)
                    {
                        items.Add(ParseValue(ref position));
                        SkipSpaceAndComments(ref position);
                        byte next = _data[position++];
                        if (next == (byte)')') break;
                        // ',' moves on to the next item
                    }

                    return StepValue.OfList(items);
                }
            }

            if (c == (byte)'-' || c == (byte)'+' || (c >= (byte)'0' && c <= (byte)'9'))
            {
                int start = position;
                bool real = false;
                position++;
                while (position < _data.Length)
                {
                    byte d = _data[position];
                    if (d >= (byte)'0' && d <= (byte)'9' || d == (byte)'-' || d == (byte)'+')
                    {
                        position++;
                    }
                    else if (d == (byte)'.' || d == (byte)'E' || d == (byte)'e')
                    {
                        real = true;
                        position++;
                    }
                    else
                    {
                        break;
                    }
                }

                string text = Encoding.ASCII.GetString(_data, start, position - start);
                return real
                    ? StepValue.OfReal(double.Parse(text, NumberStyles.Float, CultureInfo.InvariantCulture))
                    : StepValue.OfInteger(long.Parse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture));
            }

            if (IsIdentifier(c))
            {
                // typed value: IFCLABEL('x')
                int start = position;
                while (IsIdentifier(_data[position])) position++;
                string type = TypeName(start, position - start);
                SkipSpaceAndComments(ref position);
                position++; // (
                var inner = ParseValue(ref position);
                SkipSpaceAndComments(ref position);
                position++; // )
                return StepValue.OfTyped(type, inner);
            }

            throw new InvalidDataException("Invalid STEP near position " + position);
        }

        /// <summary>Quoted text with the STEP encodings: '' \\ \X2\..\X0\ \X4\..\X0\ \X\hh \S\c \P.\</summary>
        private string ParseString(ref int position)
        {
            position++; // '
            var raw = new StringBuilder();
            while (true)
            {
                byte c = _data[position];
                if (c == (byte)'\'')
                {
                    if (position + 1 < _data.Length && _data[position + 1] == (byte)'\'')
                    {
                        raw.Append('\'');
                        position += 2;
                        continue;
                    }

                    position++;
                    break;
                }

                raw.Append((char)c);
                position++;
            }

            return Decode(raw.ToString());
        }

        internal static string Decode(string text)
        {
            if (text.IndexOf('\\') < 0) return text;
            var result = new StringBuilder(text.Length);
            int i = 0;
            while (i < text.Length)
            {
                char c = text[i];
                if (c != '\\')
                {
                    result.Append(c);
                    i++;
                    continue;
                }

                if (Starts(text, i, "\\\\"))
                {
                    result.Append('\\');
                    i += 2;
                }
                else if (Starts(text, i, "\\X2\\") || Starts(text, i, "\\X4\\"))
                {
                    int width = text[i + 2] == '2' ? 4 : 8;
                    i += 4;
                    while (i < text.Length && !Starts(text, i, "\\X0\\"))
                    {
                        int code = int.Parse(text.Substring(i, width), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                        if (width == 4) result.Append((char)code);
                        else result.Append(char.ConvertFromUtf32(code));
                        i += width;
                    }

                    i += 4; // \X0\
                }
                else if (Starts(text, i, "\\X\\") && i + 4 < text.Length + 1)
                {
                    result.Append((char)int.Parse(text.Substring(i + 3, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture));
                    i += 5;
                }
                else if (Starts(text, i, "\\S\\") && i + 3 < text.Length)
                {
                    result.Append((char)(text[i + 3] + 128));
                    i += 4;
                }
                else if (Starts(text, i, "\\P") && i + 3 < text.Length && text[i + 3] == '\\')
                {
                    i += 4; // code page: ignored
                }
                else
                {
                    result.Append(c);
                    i++;
                }
            }

            return result.ToString();
        }

        private static bool Starts(string text, int index, string prefix) =>
            index + prefix.Length <= text.Length && string.CompareOrdinal(text, index, prefix, 0, prefix.Length) == 0;

        private static bool IsIdentifier(byte c) =>
            c >= (byte)'A' && c <= (byte)'Z' || c >= (byte)'a' && c <= (byte)'z' || c >= (byte)'0' && c <= (byte)'9' || c == (byte)'_';

        private void SkipSpaceAndComments(ref int position)
        {
            while (position < _data.Length)
            {
                byte c = _data[position];
                if (c == (byte)' ' || c == (byte)'\r' || c == (byte)'\n' || c == (byte)'\t')
                {
                    position++;
                }
                else if (c == (byte)'/' && position + 1 < _data.Length && _data[position + 1] == (byte)'*')
                {
                    int close = IndexOf("*/", position + 2);
                    position = close < 0 ? _data.Length : close + 2;
                }
                else
                {
                    break;
                }
            }
        }

        private bool Matches(int position, string text)
        {
            if (position + text.Length > _data.Length) return false;
            for (int i = 0; i < text.Length; i++)
            {
                if (_data[position + i] != (byte)text[i]) return false;
            }

            return true;
        }

        private int IndexOf(string text, int from)
        {
            for (int i = from; i <= _data.Length - text.Length; i++)
            {
                if (_data[i] == (byte)text[0] && Matches(i, text)) return i;
            }

            return -1;
        }
    }
}
