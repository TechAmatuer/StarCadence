// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at http://mozilla.org/MPL/2.0/.

using System.Text.Json;
using StarCadence.Core.Model;

namespace StarCadence.Core.Planning;

/// <summary>When this observatory last observed each target (drives the repeat cadence). A small JSON file.</summary>
public sealed class History {
    public Dictionary<string, List<DateTime>> Visits { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public static History Load(string? path) {
        if (string.IsNullOrEmpty(path) || !File.Exists(path)) return new History();
        var h = JsonSerializer.Deserialize<History>(File.ReadAllText(path), Config.Json) ?? new History();
        h.Visits = new Dictionary<string, List<DateTime>>(
            h.Visits.ToDictionary(kv => Target.NormaliseName(kv.Key), kv => kv.Value.Select(Astro.AstroTime.AsUtc).OrderBy(x => x).ToList()),
            StringComparer.OrdinalIgnoreCase);
        return h;
    }

    public void Save(string path) {
        var dir = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(this, Config.Json));
        File.Move(tmp, path, overwrite: true);
    }

    public void Record(string target, DateTime utc) {
        var key = Target.NormaliseName(target);
        if (!Visits.TryGetValue(key, out var list)) Visits[key] = list = new List<DateTime>();
        utc = Astro.AstroTime.AsUtc(utc);
        if (!list.Contains(utc)) { list.Add(utc); list.Sort(); }
        if (list.Count > 500) list.RemoveRange(0, list.Count - 500);
    }

    public DateTime? Last(string target, DateTime? before = null) {
        if (!Visits.TryGetValue(Target.NormaliseName(target), out var list) || list.Count == 0) return null;
        if (before is null) return list[^1];
        for (var i = list.Count - 1; i >= 0; i--) if (list[i] <= before) return list[i];
        return null;
    }
}
