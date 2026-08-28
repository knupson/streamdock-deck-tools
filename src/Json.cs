using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace StreamDeckAudio
{
    internal static class Json
    {
        public static Dictionary<string, object> Dict(Dictionary<string, object> source, string key)
        {
            object value;
            if (source == null || !source.TryGetValue(key, out value)) return null;
            return value as Dictionary<string, object>;
        }

        public static string Str(Dictionary<string, object> source, string key)
        {
            object value;
            if (source == null || !source.TryGetValue(key, out value) || value == null) return null;
            return value as string ?? value.ToString();
        }

        public static int Int(Dictionary<string, object> source, string key, int fallback)
        {
            object value;
            if (source == null || !source.TryGetValue(key, out value) || value == null) return fallback;
            try
            {
                return Convert.ToInt32(value, CultureInfo.InvariantCulture);
            }
            catch (Exception)
            {
                return fallback;
            }
        }

        public static bool Bool(Dictionary<string, object> source, string key, bool fallback)
        {
            object value;
            if (source == null || !source.TryGetValue(key, out value) || value == null) return fallback;
            if (value is bool) return (bool)value;

            bool parsed;
            return bool.TryParse(value.ToString(), out parsed) ? parsed : fallback;
        }

        public static string Escape(string text)
        {
            if (string.IsNullOrEmpty(text)) return string.Empty;

            var builder = new StringBuilder(text.Length + 8);
            foreach (char c in text)
            {
                switch (c)
                {
                    case '"': builder.Append("\\\""); break;
                    case '\\': builder.Append("\\\\"); break;
                    case '\n': builder.Append("\\n"); break;
                    case '\r': builder.Append("\\r"); break;
                    case '\t': builder.Append("\\t"); break;
                    default:
                        if (c < 0x20) builder.Append("\\u").Append(((int)c).ToString("x4"));
                        else builder.Append(c);
                        break;
                }
            }
            return builder.ToString();
        }

        /// <summary>Builds a JSON array of objects from already-escaped pairs.</summary>
        public static string Array(IEnumerable<string> objects)
        {
            var builder = new StringBuilder("[");
            bool first = true;
            foreach (string item in objects)
            {
                if (!first) builder.Append(',');
                builder.Append(item);
                first = false;
            }
            return builder.Append(']').ToString();
        }
    }
}
