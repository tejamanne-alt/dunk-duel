using System;
using System.Collections.Generic;
using System.Text;

namespace DunkDuel.Sim.Data
{
    public enum JsonKind : byte { Null, Bool, Number, String, Array, Object }

    /// <summary>
    /// Minimal JSON DOM for data tables. Numbers are kept as their original text so that they can be
    /// converted to fixed-point with integer math only. Object members keep their file order; lookups are
    /// linear (tables are small and only read at bake time). No floats, no dictionaries.
    /// </summary>
    public sealed class JsonValue
    {
        public JsonKind Kind;
        public string Text;                          // number literal or string value
        public bool BoolValue;
        public List<JsonValue> Items;                // array
        public List<KeyValuePair<string, JsonValue>> Members; // object
        public string Path = "$";

        public bool IsObject => Kind == JsonKind.Object;
        public bool IsArray => Kind == JsonKind.Array;

        public JsonValue Get(string key)
        {
            if (Members == null) return null;
            for (int i = 0; i < Members.Count; i++)
            {
                if (Members[i].Key == key) return Members[i].Value;
            }
            return null;
        }

        public bool Has(string key) => Get(key) != null;

        public JsonValue Require(string key)
        {
            JsonValue v = Get(key);
            if (v == null) throw new DataException(Path + ": missing key '" + key + "'");
            return v;
        }

        public string AsString()
        {
            if (Kind != JsonKind.String) throw new DataException(Path + ": expected string");
            return Text;
        }

        public Fix AsFix()
        {
            if (Kind == JsonKind.Number || Kind == JsonKind.String)
            {
                if (Fix.TryParse(Text, out Fix f)) return f;
            }
            throw new DataException(Path + ": expected decimal number, got '" + Text + "'");
        }

        public int AsInt()
        {
            if (Kind == JsonKind.Number || Kind == JsonKind.String)
            {
                if (TryParseInt(Text, out int v)) return v;
            }
            throw new DataException(Path + ": expected integer, got '" + Text + "'");
        }

        public bool AsBool()
        {
            if (Kind != JsonKind.Bool) throw new DataException(Path + ": expected true/false");
            return BoolValue;
        }

        public static bool TryParseInt(string s, out int value)
        {
            value = 0;
            if (string.IsNullOrEmpty(s)) return false;
            int i = 0;
            bool neg = false;
            if (s[0] == '-' || s[0] == '+')
            {
                neg = s[0] == '-';
                i = 1;
            }
            if (i >= s.Length) return false;
            long acc = 0;
            for (; i < s.Length; i++)
            {
                char c = s[i];
                if (c < '0' || c > '9') return false;
                acc = acc * 10 + (c - '0');
                if (acc > 2147483648L) return false;
            }
            if (neg) acc = -acc;
            if (acc > int.MaxValue || acc < int.MinValue) return false;
            value = (int)acc;
            return true;
        }
    }

    public sealed class DataException : Exception
    {
        public DataException(string message) : base(message) { }
    }

    /// <summary>Strict JSON parser (RFC 8259 subset: no comments, no trailing commas).</summary>
    public static class Json
    {
        public static JsonValue Parse(string text, string sourceName = "$")
        {
            var p = new Parser(text, sourceName);
            p.SkipWs();
            JsonValue v = p.ParseValue(sourceName);
            p.SkipWs();
            if (p.Pos != text.Length) throw p.Error("trailing characters");
            return v;
        }

        private sealed class Parser
        {
            private readonly string _s;
            private readonly string _source;
            public int Pos;

            public Parser(string s, string source)
            {
                _s = s ?? throw new ArgumentNullException(nameof(s));
                _source = source;
            }

            public DataException Error(string what)
            {
                int line = 1;
                for (int i = 0; i < Pos && i < _s.Length; i++) if (_s[i] == '\n') line++;
                return new DataException(_source + " line " + line + ": " + what);
            }

            public void SkipWs()
            {
                while (Pos < _s.Length)
                {
                    char c = _s[Pos];
                    if (c == ' ' || c == '\t' || c == '\n' || c == '\r') Pos++;
                    else break;
                }
            }

            public JsonValue ParseValue(string path)
            {
                if (Pos >= _s.Length) throw Error("unexpected end");
                char c = _s[Pos];
                switch (c)
                {
                    case '{': return ParseObject(path);
                    case '[': return ParseArray(path);
                    case '"': return new JsonValue { Kind = JsonKind.String, Text = ParseString(), Path = path };
                    case 't': Expect("true"); return new JsonValue { Kind = JsonKind.Bool, BoolValue = true, Path = path };
                    case 'f': Expect("false"); return new JsonValue { Kind = JsonKind.Bool, BoolValue = false, Path = path };
                    case 'n': Expect("null"); return new JsonValue { Kind = JsonKind.Null, Path = path };
                    default:
                        if (c == '-' || (c >= '0' && c <= '9')) return new JsonValue { Kind = JsonKind.Number, Text = ParseNumber(), Path = path };
                        throw Error("unexpected '" + c + "'");
                }
            }

            private void Expect(string word)
            {
                if (string.CompareOrdinal(_s, Pos, word, 0, word.Length) != 0) throw Error("expected " + word);
                Pos += word.Length;
            }

            private JsonValue ParseObject(string path)
            {
                var v = new JsonValue { Kind = JsonKind.Object, Members = new List<KeyValuePair<string, JsonValue>>(), Path = path };
                Pos++;
                SkipWs();
                if (Pos < _s.Length && _s[Pos] == '}') { Pos++; return v; }
                while (true)
                {
                    SkipWs();
                    if (Pos >= _s.Length || _s[Pos] != '"') throw Error("expected key");
                    string key = ParseString();
                    for (int i = 0; i < v.Members.Count; i++)
                    {
                        if (v.Members[i].Key == key) throw Error("duplicate key '" + key + "'");
                    }
                    SkipWs();
                    if (Pos >= _s.Length || _s[Pos] != ':') throw Error("expected ':'");
                    Pos++;
                    SkipWs();
                    JsonValue child = ParseValue(path + "." + key);
                    v.Members.Add(new KeyValuePair<string, JsonValue>(key, child));
                    SkipWs();
                    if (Pos >= _s.Length) throw Error("unexpected end in object");
                    if (_s[Pos] == ',') { Pos++; continue; }
                    if (_s[Pos] == '}') { Pos++; return v; }
                    throw Error("expected ',' or '}'");
                }
            }

            private JsonValue ParseArray(string path)
            {
                var v = new JsonValue { Kind = JsonKind.Array, Items = new List<JsonValue>(), Path = path };
                Pos++;
                SkipWs();
                if (Pos < _s.Length && _s[Pos] == ']') { Pos++; return v; }
                while (true)
                {
                    SkipWs();
                    v.Items.Add(ParseValue(path + "[" + v.Items.Count + "]"));
                    SkipWs();
                    if (Pos >= _s.Length) throw Error("unexpected end in array");
                    if (_s[Pos] == ',') { Pos++; continue; }
                    if (_s[Pos] == ']') { Pos++; return v; }
                    throw Error("expected ',' or ']'");
                }
            }

            private string ParseNumber()
            {
                int start = Pos;
                if (_s[Pos] == '-') Pos++;
                while (Pos < _s.Length && ((_s[Pos] >= '0' && _s[Pos] <= '9') || _s[Pos] == '.')) Pos++;
                if (Pos < _s.Length && (_s[Pos] == 'e' || _s[Pos] == 'E')) throw Error("exponents are not allowed in data tables");
                return _s.Substring(start, Pos - start);
            }

            private string ParseString()
            {
                Pos++; // opening quote
                var sb = new StringBuilder();
                while (true)
                {
                    if (Pos >= _s.Length) throw Error("unterminated string");
                    char c = _s[Pos++];
                    if (c == '"') break;
                    if (c != '\\')
                    {
                        sb.Append(c);
                        continue;
                    }
                    if (Pos >= _s.Length) throw Error("bad escape");
                    char e = _s[Pos++];
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
                            if (Pos + 4 > _s.Length) throw Error("bad unicode escape");
                            int code = 0;
                            for (int i = 0; i < 4; i++)
                            {
                                char h = _s[Pos++];
                                int d = h >= '0' && h <= '9' ? h - '0' : (h >= 'a' && h <= 'f' ? h - 'a' + 10 : (h >= 'A' && h <= 'F' ? h - 'A' + 10 : -1));
                                if (d < 0) throw Error("bad unicode escape");
                                code = code * 16 + d;
                            }
                            sb.Append((char)code);
                            break;
                        default: throw Error("bad escape");
                    }
                }
                return sb.ToString();
            }
        }
    }
}
