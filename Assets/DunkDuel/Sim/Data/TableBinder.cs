using System;
using System.Collections.Generic;
using System.Reflection;

namespace DunkDuel.Sim.Data
{
    /// <summary>How a JSON number is converted when baked into a sim table.</summary>
    public enum Unit : byte
    {
        None = 0,
        /// <summary>JSON value is per second (m/s); baked per frame.</summary>
        PerSecond = 1,
        /// <summary>JSON value is per second squared (m/s^2); baked per frame squared.</summary>
        PerSecondSq = 2,
        /// <summary>JSON value is seconds (decimal); baked to whole frames.</summary>
        Seconds = 3,
    }

    [AttributeUsage(AttributeTargets.Field)]
    public sealed class UnitAttribute : Attribute
    {
        public readonly Unit Unit;
        public UnitAttribute(Unit unit) { Unit = unit; }
    }

    /// <summary>Field may be omitted from JSON (keeps its default). Everything else is required.</summary>
    [AttributeUsage(AttributeTargets.Field)]
    public sealed class OptionalAttribute : Attribute { }

    /// <summary>Overrides the JSON key (default: camelCase field name), e.g. "roundSeconds" for a frames field.</summary>
    [AttributeUsage(AttributeTargets.Field)]
    public sealed class JsonKeyAttribute : Attribute
    {
        public readonly string Key;
        public JsonKeyAttribute(string key) { Key = key; }
    }

    /// <summary>
    /// Binds JSON objects onto table classes by field name (camelCase in JSON). Strict: unknown keys and
    /// missing required keys are errors, so a typo in a data file can never silently fall back to a default.
    /// Only runs at bake time; the resulting tables are treated as immutable by the simulation.
    /// </summary>
    public static class TableBinder
    {
        public static T Bind<T>(JsonValue json, int tickRate) where T : class, new()
        {
            return (T)BindObject(typeof(T), json, tickRate);
        }

        public static object BindObject(Type type, JsonValue json, int tickRate)
        {
            if (json == null || !json.IsObject) throw new DataException((json?.Path ?? "$") + ": expected object for " + type.Name);
            object instance = Activator.CreateInstance(type);
            FieldInfo[] fields = type.GetFields(BindingFlags.Public | BindingFlags.Instance);

            for (int m = 0; m < json.Members.Count; m++)
            {
                string key = json.Members[m].Key;
                if (key.Length > 0 && key[0] == '_') continue; // "_comment" style annotations
                bool known = false;
                for (int f = 0; f < fields.Length; f++)
                {
                    if (KeyOf(fields[f]) == key) { known = true; break; }
                }
                if (!known) throw new DataException(json.Path + ": unknown key '" + key + "' for " + type.Name);
            }

            for (int f = 0; f < fields.Length; f++)
            {
                FieldInfo field = fields[f];
                if (field.IsInitOnly || field.IsLiteral) continue;
                string key = KeyOf(field);
                JsonValue v = json.Get(key);
                if (v == null)
                {
                    if (field.GetCustomAttribute<OptionalAttribute>() != null) continue;
                    throw new DataException(json.Path + ": missing key '" + key + "' for " + type.Name);
                }
                UnitAttribute unit = field.GetCustomAttribute<UnitAttribute>();
                field.SetValue(instance, Convert(field.FieldType, v, unit != null ? unit.Unit : Unit.None, tickRate));
            }
            return instance;
        }

        public static string KeyOf(FieldInfo field)
        {
            JsonKeyAttribute alias = field.GetCustomAttribute<JsonKeyAttribute>();
            return alias != null ? alias.Key : JsonName(field.Name);
        }

        public static string JsonName(string fieldName)
        {
            if (string.IsNullOrEmpty(fieldName)) return fieldName;
            return char.ToLowerInvariant(fieldName[0]) + fieldName.Substring(1);
        }

        private static object Convert(Type t, JsonValue v, Unit unit, int tickRate)
        {
            if (t == typeof(Fix))
            {
                Fix f = v.AsFix();
                switch (unit)
                {
                    case Unit.PerSecond: return f / tickRate;
                    case Unit.PerSecondSq: return f / (tickRate * tickRate);
                    case Unit.None: return f;
                    default: throw new DataException(v.Path + ": unit " + unit + " is not valid for decimals");
                }
            }
            if (t == typeof(int))
            {
                if (unit == Unit.Seconds) return (v.AsFix() * tickRate).RoundToInt();
                if (unit != Unit.None) throw new DataException(v.Path + ": unit " + unit + " is not valid for integers");
                return v.AsInt();
            }
            if (t == typeof(bool)) return v.AsBool();
            if (t == typeof(string)) return v.AsString();
            if (t.IsEnum)
            {
                string s = v.AsString();
                try
                {
                    return Enum.Parse(t, s, true);
                }
                catch (ArgumentException)
                {
                    throw new DataException(v.Path + ": '" + s + "' is not a " + t.Name);
                }
            }
            if (t.IsArray)
            {
                if (!v.IsArray) throw new DataException(v.Path + ": expected array");
                Type et = t.GetElementType();
                Array arr = Array.CreateInstance(et, v.Items.Count);
                for (int i = 0; i < v.Items.Count; i++) arr.SetValue(Convert(et, v.Items[i], unit, tickRate), i);
                return arr;
            }
            if (t.IsClass) return BindObject(t, v, tickRate);
            throw new DataException(v.Path + ": unsupported field type " + t.Name);
        }

        /// <summary>FNV-1a 64 over UTF-16 code units of text with CR removed (line-ending independent).</summary>
        public static ulong HashText(string text, ulong hash = 14695981039346656037UL)
        {
            unchecked
            {
                for (int i = 0; i < text.Length; i++)
                {
                    char c = text[i];
                    if (c == '\r') continue;
                    hash ^= (byte)(c & 0xFF);
                    hash *= 1099511628211UL;
                    hash ^= (byte)(c >> 8);
                    hash *= 1099511628211UL;
                }
                return hash;
            }
        }
    }

    /// <summary>Where the data tables come from: Unity Resources, a folder on disk, or embedded resources.</summary>
    public interface IDataSource
    {
        /// <summary>Returns the JSON text of a table such as "tuning" or "fighters/kofi", or null if absent.</summary>
        string Read(string name);
    }

    /// <summary>In-memory data source (tests, the web harness, the Balance Lab's tuning sweeps).</summary>
    public sealed class MemoryDataSource : IDataSource
    {
        private readonly List<KeyValuePair<string, string>> _files = new List<KeyValuePair<string, string>>();

        public MemoryDataSource Add(string name, string json)
        {
            for (int i = 0; i < _files.Count; i++)
            {
                if (_files[i].Key == name)
                {
                    _files[i] = new KeyValuePair<string, string>(name, json);
                    return this;
                }
            }
            _files.Add(new KeyValuePair<string, string>(name, json));
            return this;
        }

        public string Read(string name)
        {
            for (int i = 0; i < _files.Count; i++) if (_files[i].Key == name) return _files[i].Value;
            return null;
        }
    }
}
