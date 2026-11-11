// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at http://mozilla.org/MPL/2.0/.

using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using StarCadence.Core.Astro;
using StarCadence.Core.Model;
using StarCadence.Core.Util;

namespace StarCadence.Core.Sources;

public sealed record SourceResult(string Source, List<Target> Targets, List<string> Messages);

/// <summary>Any CSV target list with Name/RA/Dec headers (AAVSO Target Tool export, ExoPlanets plug-in lists, your own).</summary>
public static class CsvTargets {
    public static SourceResult Load(string path, string? sourceName = null) {
        var name = sourceName ?? Path.GetFileName(path);
        var msgs = new List<string>();
        var rows = Csv.ReadFile(path);
        if (rows.Count == 0) return new(name, new(), new() { $"{name}: empty" });
        var map = TargetRows.MapHeaders(rows[0].Keys);
        var missing = new[] { "name", "ra", "dec" }.Where(f => !map.ContainsKey(f) && !(f == "ra" && map.ContainsKey("rahours"))).ToList();
        if (missing.Count > 0) return new(name, new(), new() { $"{name}: missing column(s) {string.Join(", ", missing)}; columns are {string.Join(", ", rows[0].Keys)}" });
        var list = new List<Target>();
        var line = 1;
        foreach (var r in rows) {
            line++;
            var t = TargetRows.FromRow(r, map, name, out var problem);
            if (t is null) { msgs.Add($"{name} row {line}: skipped ({problem})"); continue; }
            list.Add(t);
        }
        msgs.Insert(0, $"{name}: {list.Count} targets");
        return new(name, list, msgs);
    }
}

/// <summary>
/// AAVSO Target Tool: the API with the observer's own key (https://targettool.aavso.org), or its CSV export.
/// The key is read from an environment variable or a key file you create; it is never written by this program.
/// </summary>
public static class TargetTool {
    public const string Api = "https://targettool.aavso.org/TargetTool/api/v1/targets";

    public static Uri Url(IEnumerable<string> sections) =>
        new(Api + "?" + string.Join("&", sections.Select(s => "obs_section=" + Uri.EscapeDataString(s))));

    public static string? FindKey(SourceSettings s, string configDir) {
        var v = Environment.GetEnvironmentVariable(s.TargetToolKeyVariable);
        if (!string.IsNullOrWhiteSpace(v)) return v.Trim();
        if (!string.IsNullOrWhiteSpace(s.TargetToolKeyFile)) {
            var p = Path.IsPathRooted(s.TargetToolKeyFile) ? s.TargetToolKeyFile : Path.Combine(configDir, s.TargetToolKeyFile);
            if (File.Exists(p)) return File.ReadAllText(p).Trim();
        }
        return null;
    }

    public static async Task<string> FetchAsync(HttpClient http, string key, IEnumerable<string> sections, CancellationToken ct = default) {
        using var req = new HttpRequestMessage(HttpMethod.Get, Url(sections));
        req.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes(key + ":api_token")));
        using var res = await http.SendAsync(req, ct).ConfigureAwait(false);
        res.EnsureSuccessStatusCode();
        return await res.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Targets from the API's JSON ({"targets": [...]} or a bare list).</summary>
    public static SourceResult ParseJson(string json) {
        using var doc = JsonDocument.Parse(json);
        var arr = doc.RootElement.ValueKind == JsonValueKind.Array ? doc.RootElement
            : doc.RootElement.TryGetProperty("targets", out var t) ? t : default;
        var list = new List<Target>(); var msgs = new List<string>();
        if (arr.ValueKind != JsonValueKind.Array) return new("AAVSO Target Tool", list, new() { "AAVSO Target Tool: no 'targets' list in the answer" });
        foreach (var el in arr.EnumerateArray()) {
            if (el.ValueKind != JsonValueKind.Object) continue;
            var row = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var p in el.EnumerateObject()) row[p.Name] = Flatten(p.Value);
            var map = TargetRows.MapHeaders(row.Keys);
            var tg = TargetRows.FromRow(row, map, "AAVSO Target Tool", out var problem);
            if (tg is null) { msgs.Add("AAVSO Target Tool: skipped " + problem); continue; }
            if (row.TryGetValue("solar_conjunction", out var sc) && sc.Equals("true", StringComparison.OrdinalIgnoreCase)) { msgs.Add($"{tg.Name}: near solar conjunction, skipped"); continue; }
            if (row.TryGetValue("obs_mode", out var mode) && mode.Length > 0 && !mode.Contains("phot", StringComparison.OrdinalIgnoreCase) && !mode.Equals("all", StringComparison.OrdinalIgnoreCase)) {
                msgs.Add($"{tg.Name}: wants {mode}, not photometry, skipped"); continue;
            }
            list.Add(tg);
        }
        msgs.Insert(0, $"AAVSO Target Tool: {list.Count} targets");
        return new("AAVSO Target Tool", list, msgs);
    }

    static string Flatten(JsonElement v) => v.ValueKind switch {
        JsonValueKind.Array => string.Join(",", v.EnumerateArray().Select(Flatten)),
        JsonValueKind.String => v.GetString() ?? "",
        JsonValueKind.Number => v.GetRawText(),
        JsonValueKind.True => "true",
        JsonValueKind.False => "false",
        JsonValueKind.Null => "",
        _ => v.GetRawText()
    };
}

/// <summary>
/// ExoClock (https://www.exoclock.space) planets list: transiting planets with ephemerides and an observing priority.
/// Field names as used by the ExoPlanets plug-in's ExoClockTarget model. Each planet becomes a Transit target whose
/// windows the planner works out per night (ingress - baseline to egress + baseline, widened by the ephemeris
/// uncertainty propagated to that night).
/// </summary>
public static class ExoClock {
    public const string Url = "https://www.exoclock.space/database/planets_json";

    public static SourceResult ParseJson(string json, SourceSettings s, DateTime? forNightUtc = null) {
        using var doc = JsonDocument.Parse(json);
        var list = new List<Target>(); var msgs = new List<string>();
        IEnumerable<JsonElement> planets = doc.RootElement.ValueKind switch {
            JsonValueKind.Object => doc.RootElement.EnumerateObject().Select(p => p.Value),
            JsonValueKind.Array => doc.RootElement.EnumerateArray(),
            _ => Enumerable.Empty<JsonElement>()
        };
        var wanted = s.ExoClockPriorities.Select(x => x.ToLowerInvariant()).ToHashSet();
        int dropped = 0;
        foreach (var p in planets) {
            if (p.ValueKind != JsonValueKind.Object) continue;   // comments or other non-planet entries
            string? Str(string k) => p.TryGetProperty(k, out var v) && v.ValueKind != JsonValueKind.Null ? (v.ValueKind == JsonValueKind.String ? v.GetString() : v.GetRawText()) : null;
            double? Dbl(string k) => TargetRows.Num(Str(k));
            var name = Str("name");
            var ra = Coords.ParseRa(Str("ra_j2000"), plainIsHours: false);
            var dec = Coords.ParseDec(Str("dec_j2000"));
            var t0 = Dbl("ephem_mid_time"); var per = Dbl("ephem_period"); var dur = Dbl("duration_hours");
            if (name is null || ra is null || dec is null || t0 is null || per is null or <= 0) { dropped++; continue; }
            var pri = (Str("priority") ?? "").ToLowerInvariant();
            var vmag = Dbl("v_mag"); var rmag = Dbl("r_mag"); var depth = Dbl("depth_r_mmag");
            if (wanted.Count > 0 && !wanted.Contains(pri)) { dropped++; continue; }
            if ((rmag ?? vmag) is double m && m > s.ExoClockMaxMag) { dropped++; continue; }
            if (depth is double d && d < s.ExoClockMinDepthMmag) { dropped++; continue; }
            var fmt = (Str("ephem_mid_time_format") ?? "BJD_TDB").ToUpperInvariant();
            var epoch = t0.Value < 100000 ? t0.Value + 2400000 : t0.Value;
            if (fmt.Contains("MJD")) epoch = t0.Value + 2400000.5;
            var tg = new Target {
                Name = name, RaDeg = ra.Value, DecDeg = dec.Value, Kind = TargetKind.Transit, Source = "ExoClock",
                EpochJd = epoch, PeriodDays = per, DurationHours = dur ?? 2, DepthMmag = depth,
                Mag = vmag ?? rmag, Priority = pri switch { "alert" => 1.0, "high" => 0.8, "medium" => 0.55, "low" => 0.3, _ => 0.4 },
                RecentObservations = (int?)Dbl("total_observations_recent"),
                Notes = $"ExoClock priority {pri}" + (depth is double dd ? $", depth {dd:0.#} mmag" : ""),
            };
            if (vmag is double v) tg.BandMags["V"] = v;
            if (rmag is double r) tg.BandMags["R"] = r;
            // Ephemeris uncertainty at the planned night, in minutes (sigma_T0^2 + (n sigma_P)^2)^0.5, times 2 for margin.
            var e0 = Math.Max(Math.Abs(Dbl("ephem_mid_time_e1") ?? 0), Math.Abs(Dbl("ephem_mid_time_e2") ?? 0));
            var ep = Math.Max(Math.Abs(Dbl("ephem_period_e1") ?? 0), Math.Abs(Dbl("ephem_period_e2") ?? 0));
            var when = AstroTime.JulianDate(forNightUtc ?? DateTime.UtcNow);
            var n = Math.Abs((when - epoch) / per.Value);
            tg.OcRangeMin = Math.Min(120, 2 * Math.Sqrt(e0 * e0 + n * n * ep * ep) * 1440 + Math.Abs(Dbl("current_oc_min") ?? 0));
            list.Add(tg);
        }
        msgs.Add($"ExoClock: {list.Count} planets kept ({dropped} left out by priority, brightness or depth)");
        return new("ExoClock", list, msgs);
    }
}

/// <summary>The watch list in the configuration: stars you always want, with optional windows and events.</summary>
public static class WatchList {
    public static SourceResult Load(IEnumerable<WatchItem> items) {
        var list = new List<Target>(); var msgs = new List<string>();
        foreach (var w in items) {
            var ra = Coords.ParseRa(w.Ra); var dec = Coords.ParseDec(w.Dec);
            if (string.IsNullOrWhiteSpace(w.Name) || ra is null || dec is null) { msgs.Add($"watch list: '{w.Name}' needs ra and dec"); continue; }
            var t = new Target {
                Name = w.Name, RaDeg = ra.Value, DecDeg = dec.Value, Source = "watch list", Mag = w.Mag, MagBright = w.MagBright ?? w.Mag,
                CadenceDays = w.CadenceDays, Priority = w.Priority ?? 0.7, SeriesMinutes = w.SeriesMinutes, Notes = w.Notes ?? "",
                WantFilters = w.Filters?.Select(f => f.ToUpperInvariant()).ToList() ?? new(),
                ActiveUntilUtc = TargetRows.When(w.Until),
            };
            if (w.SeriesMinutes is > 0) t.Kind = TargetKind.Series;
            if (w.Epoch is > 0 && w.Period is > 0) {
                t.Kind = TargetKind.Event; t.EpochJd = w.Epoch < 100000 ? w.Epoch + 2400000 : w.Epoch; t.PeriodDays = w.Period;
                t.EventPhase = w.Phase ?? 0; t.EventHalfWidthMin = w.EventMinutes;
            }
            foreach (var spec in w.Windows ?? new()) {
                var win = ParseWindow(spec);
                if (win is null) msgs.Add($"watch list: {w.Name}: cannot read window '{spec}' (use start/end ISO times)");
                else t.Windows.Add(win);
            }
            list.Add(t);
        }
        msgs.Insert(0, $"watch list: {list.Count} targets");
        return new("watch list", list, msgs);
    }

    /// <summary>"2026-11-12T02:00Z/2026-11-12T04:30Z", optionally prefixed "fixed:".</summary>
    public static TimeWindow? ParseWindow(string spec) {
        var fixedWin = spec.StartsWith("fixed:", StringComparison.OrdinalIgnoreCase);
        var body = fixedWin ? spec[6..] : spec;
        var parts = body.Split('/', 2, StringSplitOptions.TrimEntries);
        if (parts.Length != 2) return null;
        var a = TargetRows.When(parts[0]); var b = TargetRows.When(parts[1]);
        return a is null || b is null || b <= a ? null : new TimeWindow(a.Value, b.Value, fixedWin, fixedWin ? "fixed window" : "window");
    }
}

/// <summary>Loads every configured source, from files and (optionally) the network, and caches downloads.</summary>
public sealed class SourceLoader {
    readonly Config _cfg;
    readonly string _dir;
    readonly HttpClient? _http;

    public SourceLoader(Config cfg, string configDir, HttpClient? http) {
        _cfg = cfg; _dir = configDir; _http = http;
    }

    public string CachePath(string file) => Path.Combine(Resolve(_cfg.Sources.CacheDir), file);
    string Resolve(string p) => Path.IsPathRooted(p) ? p : Path.GetFullPath(Path.Combine(_dir, p));

    /// <summary>Download Target Tool and ExoClock data into the cache (skips what is not configured).</summary>
    public async Task<List<string>> FetchAsync(CancellationToken ct = default) {
        var log = new List<string>();
        if (_http is null) { log.Add("offline: nothing fetched"); return log; }
        Directory.CreateDirectory(Resolve(_cfg.Sources.CacheDir));
        var key = TargetTool.FindKey(_cfg.Sources, _dir);
        if (key is null) log.Add($"AAVSO Target Tool: no key (set the {_cfg.Sources.TargetToolKeyVariable} environment variable or targetToolKeyFile)");
        else {
            try {
                var json = await TargetTool.FetchAsync(_http, key, _cfg.Sources.TargetToolSections, ct).ConfigureAwait(false);
                await File.WriteAllTextAsync(CachePath("targettool.json"), json, ct).ConfigureAwait(false);
                log.Add($"AAVSO Target Tool: saved {CachePath("targettool.json")}");
            } catch (Exception e) when (e is HttpRequestException or TaskCanceledException) { log.Add("AAVSO Target Tool: download failed: " + e.Message); }
        }
        if (_cfg.Sources.ExoClock) {
            try {
                var json = await _http.GetStringAsync(ExoClock.Url, ct).ConfigureAwait(false);
                await File.WriteAllTextAsync(CachePath("exoclock.json"), json, ct).ConfigureAwait(false);
                log.Add($"ExoClock: saved {CachePath("exoclock.json")}");
            } catch (Exception e) when (e is HttpRequestException or TaskCanceledException) { log.Add("ExoClock: download failed: " + e.Message); }
        }
        return log;
    }

    /// <summary>All targets from the watch list, CSV files, the Target Tool (cache or export) and ExoClock (cache or file).</summary>
    public List<SourceResult> LoadAll(DateTime nightUtc) {
        var s = _cfg.Sources;
        var results = new List<SourceResult> { WatchList.Load(_cfg.WatchList) };
        foreach (var f in s.CsvFiles) results.Add(SafeCsv(Resolve(f), null));
        var ttCache = CachePath("targettool.json");
        if (File.Exists(ttCache)) {
            var r = Safe("AAVSO Target Tool", ttCache, () => TargetTool.ParseJson(File.ReadAllText(ttCache)));
            r.Messages.Add($"(from {Path.GetFileName(ttCache)}, downloaded {File.GetLastWriteTimeUtc(ttCache):yyyy-MM-dd HH:mm} UTC)");
            results.Add(r);
        } else if (!string.IsNullOrWhiteSpace(s.TargetToolCsv)) results.Add(SafeCsv(Resolve(s.TargetToolCsv), "AAVSO Target Tool export"));
        if (s.ExoClock) {
            var file = !string.IsNullOrWhiteSpace(s.ExoClockFile) ? Resolve(s.ExoClockFile) : CachePath("exoclock.json");
            results.Add(File.Exists(file) ? Safe("ExoClock", file, () => ExoClock.ParseJson(File.ReadAllText(file), s, nightUtc))
                : new SourceResult("ExoClock", new(), new() { $"ExoClock: no data yet ({file}); run 'fetch' or set sources.exoClockFile" }));
        }
        return results;
    }

    /// <summary>One broken source file must not stop the night: report it and carry on with the others.</summary>
    static SourceResult Safe(string name, string path, Func<SourceResult> load) {
        try { return load(); }
        catch (Exception e) when (e is JsonException or InvalidOperationException or FormatException or IOException) {
            return new SourceResult(name, new(), new() { $"{name}: could not read {path}: {e.Message}" });
        }
    }

    static SourceResult SafeCsv(string path, string? name) {
        if (!File.Exists(path)) return new(name ?? Path.GetFileName(path), new(), new() { $"{path}: file not found" });
        try { return CsvTargets.Load(path, name); } catch (Exception e) { return new(name ?? path, new(), new() { $"{path}: {e.Message}" }); }
    }
}
