using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace FakerSwap.Core
{
    public enum JsonKind { Null, Bool, Number, String, Array, Object }

    /// <summary>
    /// Small dependency-free JSON DOM.  The mod runs on Mono 2.0/net35 and we do not want to
    /// drag Newtonsoft/YamlDotNet into the Mods folder, so parsing is done by hand.
    /// </summary>
    public sealed class JsonValue
    {
        public JsonKind Kind { get; private set; }
        public bool Bool;
        public double Number;
        public string Str;
        public List<JsonValue> Items;
        public Dictionary<string, JsonValue> Props;

        public static JsonValue Null() => new JsonValue { Kind = JsonKind.Null };
        public static JsonValue Of(bool v) => new JsonValue { Kind = JsonKind.Bool, Bool = v };
        public static JsonValue Of(double v) => new JsonValue { Kind = JsonKind.Number, Number = v };
        public static JsonValue Of(string v) => v == null ? Null() : new JsonValue { Kind = JsonKind.String, Str = v };
        public static JsonValue NewArray() => new JsonValue { Kind = JsonKind.Array, Items = new List<JsonValue>() };
        public static JsonValue NewObject() => new JsonValue { Kind = JsonKind.Object, Props = new Dictionary<string, JsonValue>(StringComparer.Ordinal) };

        public bool IsNull => Kind == JsonKind.Null;
        public int Count => Kind == JsonKind.Array ? Items.Count : (Kind == JsonKind.Object ? Props.Count : 0);

        public JsonValue this[string key]
        {
            get
            {
                if (Kind != JsonKind.Object || Props == null) return Null();
                JsonValue v;
                return Props.TryGetValue(key, out v) ? v : Null();
            }
            set
            {
                if (Kind != JsonKind.Object) { Kind = JsonKind.Object; Props = new Dictionary<string, JsonValue>(StringComparer.Ordinal); }
                Props[key] = value ?? Null();
            }
        }

        public bool Has(string key) => Kind == JsonKind.Object && Props != null && Props.ContainsKey(key);

        public JsonValue this[int index]
        {
            get
            {
                if (Kind != JsonKind.Array || Items == null || index < 0 || index >= Items.Count) return Null();
                return Items[index];
            }
        }

        public void Add(JsonValue v)
        {
            if (Kind != JsonKind.Array) { Kind = JsonKind.Array; Items = new List<JsonValue>(); }
            Items.Add(v ?? Null());
        }

        public string AsString(string fallback = null) => Kind == JsonKind.String ? Str : (Kind == JsonKind.Number ? Number.ToString(CultureInfo.InvariantCulture) : fallback);
        public int AsInt(int fallback = 0) => Kind == JsonKind.Number ? (int)Math.Round(Number) : (Kind == JsonKind.String && int.TryParse(Str, NumberStyles.Integer, CultureInfo.InvariantCulture, out var i) ? i : fallback);
        public long AsLong(long fallback = 0) => Kind == JsonKind.Number ? (long)Math.Round(Number) : (Kind == JsonKind.String && long.TryParse(Str, NumberStyles.Integer, CultureInfo.InvariantCulture, out var i) ? i : fallback);
        public double AsDouble(double fallback = 0) => Kind == JsonKind.Number ? Number : (Kind == JsonKind.String && double.TryParse(Str, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : fallback);
        public bool AsBool(bool fallback = false) => Kind == JsonKind.Bool ? Bool : (Kind == JsonKind.Number ? Math.Abs(Number) > 0.5 : fallback);

        // ------------------------------------------------------------------ parsing

        public static JsonValue Parse(string text)
        {
            if (text == null) throw new FormatException("json is null");
            int i = 0;
            JsonValue v = ParseValue(text, ref i);
            SkipWs(text, ref i);
            return v;
        }

        public static bool TryParse(string text, out JsonValue value, out string error)
        {
            try
            {
                value = Parse(text);
                error = null;
                return true;
            }
            catch (Exception e)
            {
                value = null;
                error = e.Message;
                return false;
            }
        }

        private static void SkipWs(string s, ref int i)
        {
            while (i < s.Length)
            {
                char c = s[i];
                if (c == ' ' || c == '\t' || c == '\r' || c == '\n' || c == '\ufeff') i++;
                else break;
            }
        }

        private static JsonValue ParseValue(string s, ref int i)
        {
            SkipWs(s, ref i);
            if (i >= s.Length) throw new FormatException("unexpected end of json");
            char c = s[i];
            switch (c)
            {
                case '{': return ParseObject(s, ref i);
                case '[': return ParseArray(s, ref i);
                case '"': return Of(ParseString(s, ref i));
                case 't':
                    Expect(s, ref i, "true"); return Of(true);
                case 'f':
                    Expect(s, ref i, "false"); return Of(false);
                case 'n':
                    Expect(s, ref i, "null"); return Null();
                default:
                    return ParseNumber(s, ref i);
            }
        }

        private static void Expect(string s, ref int i, string token)
        {
            if (i + token.Length > s.Length || string.CompareOrdinal(s, i, token, 0, token.Length) != 0)
                throw new FormatException("expected '" + token + "' at " + i);
            i += token.Length;
        }

        private static JsonValue ParseObject(string s, ref int i)
        {
            var o = NewObject();
            i++; // {
            SkipWs(s, ref i);
            if (i < s.Length && s[i] == '}') { i++; return o; }
            while (true)
            {
                SkipWs(s, ref i);
                if (i >= s.Length || s[i] != '"') throw new FormatException("expected key at " + i);
                string key = ParseString(s, ref i);
                SkipWs(s, ref i);
                if (i >= s.Length || s[i] != ':') throw new FormatException("expected ':' at " + i);
                i++;
                o.Props[key] = ParseValue(s, ref i);
                SkipWs(s, ref i);
                if (i >= s.Length) throw new FormatException("unterminated object");
                if (s[i] == ',') { i++; continue; }
                if (s[i] == '}') { i++; return o; }
                throw new FormatException("expected ',' or '}' at " + i);
            }
        }

        private static JsonValue ParseArray(string s, ref int i)
        {
            var a = NewArray();
            i++; // [
            SkipWs(s, ref i);
            if (i < s.Length && s[i] == ']') { i++; return a; }
            while (true)
            {
                a.Items.Add(ParseValue(s, ref i));
                SkipWs(s, ref i);
                if (i >= s.Length) throw new FormatException("unterminated array");
                if (s[i] == ',') { i++; continue; }
                if (s[i] == ']') { i++; return a; }
                throw new FormatException("expected ',' or ']' at " + i);
            }
        }

        private static string ParseString(string s, ref int i)
        {
            i++; // opening quote
            var sb = new StringBuilder();
            while (true)
            {
                if (i >= s.Length) throw new FormatException("unterminated string");
                char c = s[i++];
                if (c == '"') return sb.ToString();
                if (c != '\\') { sb.Append(c); continue; }
                if (i >= s.Length) throw new FormatException("bad escape");
                char e = s[i++];
                switch (e)
                {
                    case '"': sb.Append('"'); break;
                    case '\\': sb.Append('\\'); break;
                    case '/': sb.Append('/'); break;
                    case 'b': sb.Append('\b'); break;
                    case 'f': sb.Append('\f'); break;
                    case 'n': sb.Append('\n'); break;
                    case 'r': sb.Append('\r'); break;
                    case 't': sb.Append('\t'); break;
                    case 'u':
                        if (i + 4 > s.Length) throw new FormatException("bad unicode escape");
                        sb.Append((char)Convert.ToInt32(s.Substring(i, 4), 16));
                        i += 4;
                        break;
                    default: throw new FormatException("bad escape '\\" + e + "'");
                }
            }
        }

        private static JsonValue ParseNumber(string s, ref int i)
        {
            int start = i;
            while (i < s.Length)
            {
                char c = s[i];
                if ((c >= '0' && c <= '9') || c == '-' || c == '+' || c == '.' || c == 'e' || c == 'E') i++;
                else break;
            }
            if (i == start) throw new FormatException("invalid value at " + start);
            double d = double.Parse(s.Substring(start, i - start), NumberStyles.Float, CultureInfo.InvariantCulture);
            return Of(d);
        }

        // ------------------------------------------------------------------ writing

        public string ToJson(bool pretty = true)
        {
            var sb = new StringBuilder();
            Write(sb, pretty, 0);
            return sb.ToString();
        }

        private void Write(StringBuilder sb, bool pretty, int depth)
        {
            switch (Kind)
            {
                case JsonKind.Null: sb.Append("null"); break;
                case JsonKind.Bool: sb.Append(Bool ? "true" : "false"); break;
                case JsonKind.Number:
                    if (Math.Abs(Number - Math.Round(Number)) < 1e-9 && Math.Abs(Number) < 1e15)
                        sb.Append(((long)Math.Round(Number)).ToString(CultureInfo.InvariantCulture));
                    else
                        sb.Append(Number.ToString("R", CultureInfo.InvariantCulture));
                    break;
                case JsonKind.String: WriteString(sb, Str); break;
                case JsonKind.Array:
                    {
                        if (Items.Count == 0) { sb.Append("[]"); break; }
                        sb.Append('[');
                        for (int k = 0; k < Items.Count; k++)
                        {
                            if (k > 0) sb.Append(',');
                            if (pretty) { sb.Append('\n'); Indent(sb, depth + 1); }
                            Items[k].Write(sb, pretty, depth + 1);
                        }
                        if (pretty) { sb.Append('\n'); Indent(sb, depth); }
                        sb.Append(']');
                        break;
                    }
                case JsonKind.Object:
                    {
                        if (Props.Count == 0) { sb.Append("{}"); break; }
                        sb.Append('{');
                        bool first = true;
                        foreach (var kv in Props)
                        {
                            if (!first) sb.Append(',');
                            first = false;
                            if (pretty) { sb.Append('\n'); Indent(sb, depth + 1); }
                            WriteString(sb, kv.Key);
                            sb.Append(':');
                            if (pretty) sb.Append(' ');
                            kv.Value.Write(sb, pretty, depth + 1);
                        }
                        if (pretty) { sb.Append('\n'); Indent(sb, depth); }
                        sb.Append('}');
                        break;
                    }
            }
        }

        private static void Indent(StringBuilder sb, int depth) => sb.Append(' ', depth * 2);

        private static void WriteString(StringBuilder sb, string s)
        {
            sb.Append('"');
            if (s != null)
            {
                foreach (char c in s)
                {
                    switch (c)
                    {
                        case '"': sb.Append("\\\""); break;
                        case '\\': sb.Append("\\\\"); break;
                        case '\n': sb.Append("\\n"); break;
                        case '\r': sb.Append("\\r"); break;
                        case '\t': sb.Append("\\t"); break;
                        case '\b': sb.Append("\\b"); break;
                        case '\f': sb.Append("\\f"); break;
                        default:
                            if (c < ' ') sb.Append("\\u").Append(((int)c).ToString("x4"));
                            else sb.Append(c);
                            break;
                    }
                }
            }
            sb.Append('"');
        }
    }
}
