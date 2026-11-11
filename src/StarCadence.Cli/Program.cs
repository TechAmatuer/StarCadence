// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at http://mozilla.org/MPL/2.0/.

using System.Globalization;
using System.Text.Json;
using StarCadence.Core.Astro;
using StarCadence.Core.Export;
using StarCadence.Core.Model;
using StarCadence.Core.Planning;
using StarCadence.Core.Sources;

CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
CultureInfo.DefaultThreadCurrentUICulture = CultureInfo.InvariantCulture;
CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;

var a = new Args(args);
try {
    return a.Command switch {
        "app" => await AppServer.Run(a.Get("data") ?? AppServer.DefaultDataDir(), int.TryParse(a.Get("port"), out var port) ? port : AppServer.PreferredPort, !a.Has("no-browser"), a.Has("stay")),
        "init" => Commands.Init(a),
        "fetch" => await Commands.Fetch(a),
        "plan" => await Commands.Plan(a),
        "run" => await Commands.Run(a),
        "record" => Commands.Record(a),
        "exposures" => Commands.Exposures(a),
        "ephem" => Commands.Ephem(a),
        "version" or "--version" => Commands.Version(),
        "help" or "--help" or "-h" => Commands.Help(),
        _ => Commands.Help(unknown: a.Command)
    };
} catch (Exception e) when (e is InvalidDataException or FileNotFoundException or JsonException or ArgumentException or DirectoryNotFoundException) {
    Console.Error.WriteLine("Error: " + e.Message);
    return 2;
}

sealed class Args {
    public string Command { get; }
    readonly Dictionary<string, string> _opts = new(StringComparer.OrdinalIgnoreCase);
    readonly HashSet<string> _flags = new(StringComparer.OrdinalIgnoreCase);

    public Args(string[] argv) {
        Command = argv.Length > 0 ? argv[0].ToLowerInvariant() : "app";   // double-clicked: open the app window
        for (var i = 1; i < argv.Length; i++) {
            var k = argv[i];
            if (!k.StartsWith("--")) continue;
            k = k[2..];
            if (i + 1 < argv.Length && !argv[i + 1].StartsWith("--")) _opts[k] = argv[++i];
            else _flags.Add(k);
        }
    }

    public string? Get(string k) => _opts.TryGetValue(k, out var v) ? v : null;
    public bool Has(string k) => _flags.Contains(k) || _opts.ContainsKey(k);
    public string ConfigPath => Path.GetFullPath(Get("config") ?? "config.json");
}

static class Commands {
    public static int Help(string? unknown = null) {
        if (unknown is not null) Console.Error.WriteLine($"Unknown command '{unknown}'.\n");
        Console.WriteLine("""
            StarCadence: night planner for variable stars and exoplanet transits (Windows, macOS, Linux)

              starcadence                                    open the app window (same as: starcadence app)
              starcadence app       [--data FOLDER] [--port N] [--no-browser] [--stay]

              starcadence init      [--dir FOLDER]                 write an example config.json to edit
              starcadence fetch     [--config FILE]                download AAVSO Target Tool and ExoClock data
              starcadence plan      [--config FILE] [--date YYYY-MM-DD] [--from now|ISO-TIME]
                                    [--out FOLDER] [--formats txt,json,csv,ekos,nina] [--fetch]
              starcadence run       [--config FILE] --indi [HOST[:PORT]] [--no-park]      run tonight through an INDI server
              starcadence run       [--config FILE] --simulate [--from TIME --speed N]  run tonight with no hardware (a rehearsal)
              starcadence record    [--config FILE] --target NAME [--utc ISO-TIME]
                                    or --plan plan.json [--until ISO-TIME]   mark observations done
              starcadence exposures [--config FILE] --mag M [--bright M]   exposure per filter for a star
              starcadence ephem     [--config FILE] --utc ISO-TIME [--ra RA --dec DEC [--bjd JD]] [--night YYYY-MM-DD]
              starcadence version

            Times are UTC unless they end with an offset. Coordinates: "05 34 31.9" (hours) / "+22 00 52" or decimal degrees.
            """);
        return unknown is null ? 0 : 1;
    }

    public static int Version() {
        Console.WriteLine("StarCadence " + typeof(Planner).Assembly.GetName().Version + " on .NET " + Environment.Version + " (" + System.Runtime.InteropServices.RuntimeInformation.OSDescription + ")");
        return 0;
    }

    public static int Init(Args a) {
        var dir = Path.GetFullPath(a.Get("dir") ?? ".");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "config.json");
        if (File.Exists(path) && !a.Has("force")) { Console.Error.WriteLine($"{path} already exists (use --force to overwrite)."); return 1; }
        File.WriteAllText(path, Template.ConfigJson);
        Console.WriteLine($"Wrote {path}. Edit site, filters and sources, then run: starcadence plan --config \"{path}\"");
        return 0;
    }

    static (Config cfg, string dir) LoadConfig(Args a) {
        var path = a.ConfigPath;
        if (!File.Exists(path)) throw new FileNotFoundException($"No configuration at {path}. Run 'starcadence init' first, or pass --config.");
        return (Config.Load(path), Path.GetDirectoryName(path)!);
    }

    static HttpClient NewHttp() {
        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(120) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("StarCadence/0.1 (+https://www.aavso.org)");
        return http;
    }

    public static async Task<int> Fetch(Args a) {
        var (cfg, dir) = LoadConfig(a);
        using var http = NewHttp();
        foreach (var line in await new SourceLoader(cfg, dir, http).FetchAsync()) Console.WriteLine(line);
        return 0;
    }

    public static async Task<int> Plan(Args a) {
        var (cfg, dir) = LoadConfig(a);
        if (a.Has("fetch")) await Fetch(a);
        DateTime? from = a.Get("from") is string f ? (f.Equals("now", StringComparison.OrdinalIgnoreCase) ? DateTime.UtcNow : ParseTime(f)) : null;
        DateOnly? date = a.Get("date") is string d ? DateOnly.ParseExact(d, "yyyy-MM-dd", CultureInfo.InvariantCulture) : null;
        var formats = (a.Get("formats") ?? "txt,json,csv").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var run = PlanRunner.Run(cfg, dir, date, from, a.Get("out"), formats, out var error);
        if (run is null) { Console.Error.WriteLine(error); return 1; }
        foreach (var r in run.Sources) foreach (var m in r.Messages.Take(a.Has("verbose") ? 1000 : 3)) Console.WriteLine("  " + m);
        foreach (var m in run.Messages) Console.WriteLine("  " + m);
        Console.WriteLine();
        Console.WriteLine(a.Has("quiet") ? run.Text.Split('\n')[2] : run.Text);
        foreach (var p in run.Files) Console.WriteLine("Wrote " + p);
        return 0;
    }

    public static async Task<int> Run(Args a) {
        var (cfg, dir) = LoadConfig(a);
        var req = new RunRequest { Park = !a.Has("no-park") };
        if (a.Has("indi")) {
            req.Mode = "indi";
            var spec = a.Get("indi");
            if (!string.IsNullOrWhiteSpace(spec)) {
                var parts = spec.Split(':');
                req.Host = parts[0];
                if (parts.Length > 1 && int.TryParse(parts[1], out var port)) req.Port = port;
            }
        } else if (a.Has("simulate")) {
            req.Mode = "simulate";
            if (a.Get("from") is string f) req.FromUtc = f.Equals("now", StringComparison.OrdinalIgnoreCase) ? DateTime.UtcNow : ParseTime(f);
            if (double.TryParse(a.Get("speed"), NumberStyles.Float, CultureInfo.InvariantCulture, out var sp)) req.Speed = sp;
        } else {
            Console.Error.WriteLine("Say how to run: --indi [host:port] for a real observatory, or --simulate for a rehearsal without hardware.");
            return 1;
        }
        var svc = new RunService();
        using var stop = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; Console.WriteLine("Stopping after the current exposure..."); _ = svc.StopAsync(); };
        try { await svc.StartAsync(cfg, dir, req, blocking: true, echo: Console.WriteLine); }
        catch (Exception e) when (e is IOException or InvalidOperationException or TimeoutException or System.Net.Sockets.SocketException) {
            Console.Error.WriteLine("Could not start: " + e.Message);
            return 1;
        }
        return svc.Error is null ? 0 : 1;
    }

    public static int Record(Args a) {
        var (cfg, dir) = LoadConfig(a);
        var path = Resolve(dir, cfg.HistoryFile);
        var h = History.Load(path);
        var n = 0;
        if (a.Get("plan") is string planFile) {
            var plan = JsonSerializer.Deserialize<Plan>(File.ReadAllText(planFile), Config.Json) ?? throw new InvalidDataException("Unreadable plan file.");
            var until = a.Get("until") is string u ? ParseTime(u) : DateTime.UtcNow;
            foreach (var b in plan.Blocks.Where(b => b.EndUtc <= until)) { h.Record(b.Target, b.ExposeFromUtc); n++; }
        } else if (a.Get("target") is string name) {
            h.Record(name, a.Get("utc") is string t ? ParseTime(t) : DateTime.UtcNow); n++;
        } else { Console.Error.WriteLine("record needs --target NAME or --plan plan.json"); return 1; }
        h.Save(path);
        Console.WriteLine($"Recorded {n} observation(s) in {path}");
        return 0;
    }

    public static int Exposures(Args a) {
        var (cfg, _) = LoadConfig(a);
        var mag = StarCadence.Core.Sources.TargetRows.Num(a.Get("mag")) ?? throw new ArgumentException("--mag is needed");
        var t = new Target { Name = "star", Mag = mag, MagBright = StarCadence.Core.Sources.TargetRows.Num(a.Get("bright")) };
        Console.WriteLine($"Star of magnitude {mag:0.0}" + (t.MagBright is double b ? $", can brighten to {b:0.0}" : ""));
        foreach (var f in cfg.Filters) {
            var s = ExposureCalculator.For(f, t);
            Console.WriteLine($"  {f.Name,-6} {s.Count} x {s.Seconds,5:0.#} s  (planned mag {s.PlanMag:0.0}){(s.Note is null ? "" : "  " + s.Note)}");
        }
        return 0;
    }

    public static int Ephem(Args a) {
        var (cfg, _) = LoadConfig(a);
        var utc = ParseTime(a.Get("utc") ?? DateTime.UtcNow.ToString("o"));
        var jd = AstroTime.JulianDate(utc);
        var (sra, sdec, _) = Sun.Position(jd);
        var (mra, mdec, mdist) = Moon.Position(jd);
        var lst = AstroTime.LmstDegrees(jd, cfg.Site.Longitude);
        Console.WriteLine($"UTC {utc:o}  JD {jd:F6}  LST {lst / 15:F4} h");
        Console.WriteLine($"Sun  RA {sra:F4} Dec {sdec:F4}  alt {Sun.Altitude(jd, cfg.Site.Latitude, cfg.Site.Longitude):F3}");
        Console.WriteLine($"Moon RA {mra:F4} Dec {mdec:F4}  dist {mdist:F0} km  alt {Moon.Altitude(jd, cfg.Site.Latitude, cfg.Site.Longitude):F3}  lit {Moon.Illumination(jd):F3}");
        if (a.Get("ra") is string r && a.Get("dec") is string d) {
            var ra = Coords.ParseRa(r) ?? throw new ArgumentException("bad --ra"); var dec = Coords.ParseDec(d) ?? throw new ArgumentException("bad --dec");
            var (pra, pdec) = Coords.PrecessFromJ2000(ra, dec, jd);
            var (alt, az) = Coords.Horizontal(pra, pdec, cfg.Site.Latitude, lst);
            Console.WriteLine($"Target alt {alt:F3} az {az:F3}  airmass {Coords.Airmass(alt):F3}  moon sep {AstroMath.Separation(pra, pdec, mra, mdec):F3}");
            if (StarCadence.Core.Sources.TargetRows.Num(a.Get("bjd")) is double bjd)
                Console.WriteLine($"BJD_TDB {bjd:F6} = UTC JD {Sun.BjdTdbToJdUtc(bjd, ra, dec):F6} ({AstroTime.FromJulianDate(Sun.BjdTdbToJdUtc(bjd, ra, dec)):yyyy-MM-ddTHH:mm:ss}Z)");
        }
        if (a.Get("night") is string nd) {
            var (ds, de) = Night.DarkLimits(cfg.Site, DateOnly.ParseExact(nd, "yyyy-MM-dd", CultureInfo.InvariantCulture));
            Console.WriteLine($"Night of {nd}: dark (Sun below {cfg.Site.SunAltitude}) {ds:yyyy-MM-ddTHH:mm:ss}Z to {de:yyyy-MM-ddTHH:mm:ss}Z");
        }
        return 0;
    }

    static string Resolve(string dir, string p) => PlanRunner.Resolve(dir, p);

    static DateTime ParseTime(string s) =>
        DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var d)
            ? DateTime.SpecifyKind(d, DateTimeKind.Utc) : throw new ArgumentException($"cannot read time '{s}' (use e.g. 2026-11-12T03:00:00Z)");
}
