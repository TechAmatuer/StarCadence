// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at http://mozilla.org/MPL/2.0/.

using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace StarCadence.Core.Util;

/// <summary>
/// Small RFC 4180 CSV reader/writer (quoted fields, embedded commas and newlines), no dependencies.
/// Reading also accepts what spreadsheets save in decimal-comma languages (Excel in Germany, Portugal, Brazil...):
/// semicolon- or tab-separated columns with numbers such as 12,5 and "05 34 31,9".
/// </summary>
public static partial class Csv {
    public static List<Dictionary<string, string>> ReadFile(string path) => Read(Files.ReadAllText(path));

    public static List<Dictionary<string, string>> Read(string text) {
        text = text.TrimStart('\ufeff');
        var sep = Separator(ref text);
        var rows = Parse(text, sep);
        var result = new List<Dictionary<string, string>>();
        if (rows.Count == 0) return result;
        var header = rows[0].Select(h => h.Trim()).ToList();
        foreach (var r in rows.Skip(1)) {
            if (r.All(string.IsNullOrWhiteSpace)) continue;
            var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < header.Count; i++) {
                if (string.IsNullOrEmpty(header[i]) || d.ContainsKey(header[i])) continue;
                var v = i < r.Count ? r[i].Trim() : "";
                if (sep != ',' && NumberLike().IsMatch(v)) v = DecimalComma().Replace(v, ".");
                d[header[i]] = v;
            }
            result.Add(d);
        }
        return result;
    }

    /// <summary>The column separator, from a leading "sep=;" line (removed) or from the header line: comma, semicolon or tab.</summary>
    public static char Separator(ref string text) {
        if (text.StartsWith("sep=", StringComparison.OrdinalIgnoreCase) && text.Length > 4) {
            var c = text[4];
            var nl = text.IndexOf('\n');
            text = nl < 0 ? "" : text[(nl + 1)..];
            return c;
        }
        int commas = 0, semicolons = 0, tabs = 0;
        var quoted = false;
        foreach (var ch in text) {
            if (ch == '"') { quoted = !quoted; continue; }
            if (quoted) continue;
            if (ch == '\n') break;
            if (ch == ',') commas++; else if (ch == ';') semicolons++; else if (ch == '\t') tabs++;
        }
        if (tabs > commas && tabs >= semicolons) return '\t';
        return semicolons > commas ? ';' : ',';
    }

    // A number, sexagesimal coordinate or limit such as "12,5", "-05 23 28,1", "<14,8" (no letters other than h/d/m/s).
    [GeneratedRegex(@"^[\s\d+\-\u2212.,:;<>~()\[\]hdms\u00b0'""]*\d,\d[\s\d+\-\u2212.,:<>~()\[\]hdms\u00b0'""]*$")]
    private static partial Regex NumberLike();

    [GeneratedRegex(@"(?<=\d),(?=\d)")]
    private static partial Regex DecimalComma();

    public static List<List<string>> Parse(string text) => Parse(text, ',');

    public static List<List<string>> Parse(string text, char sep) {
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
                case var c when c == sep: row.Add(field.ToString()); field.Clear(); break;
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
