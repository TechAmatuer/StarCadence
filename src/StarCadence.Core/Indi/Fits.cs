// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at http://mozilla.org/MPL/2.0/.

using System.Globalization;
using System.Text;

namespace StarCadence.Core.Indi;

/// <summary>Adds or replaces header cards in an uncompressed FITS file (primary header only), keeping the data intact.</summary>
public static class Fits {
    const int Block = 2880;

    public static bool LooksLikeFits(byte[] data) => data.Length >= 80 && Encoding.ASCII.GetString(data, 0, 6) == "SIMPLE";

    public static byte[] WithCards(byte[] data, IEnumerable<(string key, object? value, string? comment)> cards) {
        if (!LooksLikeFits(data)) return data;
        // Find the END card.
        var headerEnd = -1;
        for (var pos = 0; pos + 80 <= data.Length; pos += 80) {
            var key = Encoding.ASCII.GetString(data, pos, 8);
            if (key == "END     ") { headerEnd = pos; break; }
        }
        if (headerEnd < 0) return data;
        var headerBytes = (headerEnd / Block + 1) * Block;
        var existing = new List<string>();
        for (var pos = 0; pos < headerEnd; pos += 80) existing.Add(Encoding.ASCII.GetString(data, pos, 80));
        foreach (var (key, value, comment) in cards) {
            var card = Card(key, value, comment);
            var k = card[..8];
            var idx = existing.FindIndex(c => c[..8] == k);
            if (idx >= 0) existing[idx] = card; else existing.Add(card);
        }
        existing.Add("END".PadRight(80));
        var newHeaderBytes = ((existing.Count * 80 + Block - 1) / Block) * Block;
        var header = new StringBuilder(newHeaderBytes);
        foreach (var c in existing) header.Append(c);
        header.Append(' ', newHeaderBytes - existing.Count * 80);
        var outData = new byte[newHeaderBytes + (data.Length - headerBytes)];
        Encoding.ASCII.GetBytes(header.ToString(), 0, newHeaderBytes, outData, 0);
        Buffer.BlockCopy(data, headerBytes, outData, newHeaderBytes, data.Length - headerBytes);
        return outData;
    }

    public static string Card(string key, object? value, string? comment) {
        key = key.ToUpperInvariant().PadRight(8)[..8];
        string val = value switch {
            null => "",
            string s => "'" + s.Replace("'", "''").PadRight(8) + "'",
            bool b => (b ? "T" : "F").PadLeft(20),
            int i => i.ToString(CultureInfo.InvariantCulture).PadLeft(20),
            long l => l.ToString(CultureInfo.InvariantCulture).PadLeft(20),
            double d => d.ToString("G15", CultureInfo.InvariantCulture).PadLeft(20),
            float f => f.ToString("G9", CultureInfo.InvariantCulture).PadLeft(20),
            _ => "'" + value.ToString()!.Replace("'", "''").PadRight(8) + "'"
        };
        if (val.Length > 70) val = val[..70];
        var card = key + "= " + val;
        if (!string.IsNullOrEmpty(comment)) {
            var room = 80 - card.Length - 3;
            if (room > 0) card += " / " + (comment.Length > room ? comment[..room] : comment);
        }
        return card.PadRight(80)[..80];
    }

    /// <summary>Read string/number header values (for tests and for checking files).</summary>
    public static Dictionary<string, string> ReadHeader(byte[] data) {
        var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!LooksLikeFits(data)) return d;
        for (var pos = 0; pos + 80 <= data.Length; pos += 80) {
            var card = Encoding.ASCII.GetString(data, pos, 80);
            var key = card[..8].Trim();
            if (key == "END") break;
            if (card.Length < 10 || card[8] != '=') continue;
            var rest = card[10..];
            string val;
            if (rest.TrimStart().StartsWith('\'')) {
                var t = rest.TrimStart()[1..];
                var end = t.IndexOf('\'');
                val = end >= 0 ? t[..end].TrimEnd() : t.Trim();
            } else {
                var slash = rest.IndexOf('/');
                val = (slash >= 0 ? rest[..slash] : rest).Trim();
            }
            d[key] = val;
        }
        return d;
    }
}
