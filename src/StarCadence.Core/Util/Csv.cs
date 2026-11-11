// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at http://mozilla.org/MPL/2.0/.

using System.Globalization;
using System.Text;

namespace StarCadence.Core.Util;

/// <summary>Small RFC 4180 CSV reader/writer (quoted fields, embedded commas and newlines), no dependencies.</summary>
public static class Csv {
    public static List<Dictionary<string, string>> ReadFile(string path) => Read(File.ReadAllText(path, Encoding.UTF8));

    public static List<Dictionary<string, string>> Read(string text) {
        var rows = Parse(text.TrimStart('﻿'));
        var result = new List<Dictionary<string, string>>();
        if (rows.Count == 0) return result;
        var header = rows[0].Select(h => h.Trim()).ToList();
        foreach (var r in rows.Skip(1)) {
            if (r.All(string.IsNullOrWhiteSpace)) continue;
            var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < header.Count; i++) {
                if (string.IsNullOrEmpty(header[i]) || d.ContainsKey(header[i])) continue;
                d[header[i]] = i < r.Count ? r[i].Trim() : "";
            }
            result.Add(d);
        }
        return result;
    }

    public static List<List<string>> Parse(string text) {
        var rows = new List<List<string>>();
        var row = new List<string>();
        var field = new StringBuilder();
        var quoted = false;
        for (var i = 0; i < text.Length; i++) {
            var ch = text[i];
            if (quoted) {
                if (ch == '"') {
                    if (i + 1 < text.Length && text[i + 1] == '"') { field.Append('"'); i++; } else quoted = false;
                } else field.Append(ch);
                continue;
            }
            switch (ch) {
                case '"': quoted = true; break;
                case ',': row.Add(field.ToString()); field.Clear(); break;
                case '\r': break;
                case '\n': row.Add(field.ToString()); field.Clear(); rows.Add(row); row = new List<string>(); break;
                default: field.Append(ch); break;
            }
        }
        if (field.Length > 0 || row.Count > 0) { row.Add(field.ToString()); rows.Add(row); }
        return rows;
    }

    public static string Escape(string? s) {
        s ??= "";
        return s.IndexOfAny(new[] { ',', '"', '\n', '\r' }) >= 0 ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;
    }

    public static string Line(IEnumerable<object?> values) =>
        string.Join(",", values.Select(v => Escape(v switch {
            null => "",
            double d => d.ToString("0.###", CultureInfo.InvariantCulture),
            IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
            _ => v.ToString()
        })));
}
