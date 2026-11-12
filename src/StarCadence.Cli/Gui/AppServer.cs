// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at http://mozilla.org/MPL/2.0/.

using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using StarCadence.Core.Astro;
using StarCadence.Core.Export;
using StarCadence.Core.Model;
using StarCadence.Core.Planning;
using StarCadence.Core.Sources;

/// <summary>
/// The app window: a page served only to this computer (127.0.0.1) and opened in the default browser.
/// Every change needs a per-launch token, and requests must name this computer as the host, so other web pages
/// cannot drive it. Closing the browser tab (or the Quit button) stops it.
/// </summary>
static class AppServer {
    public const int PreferredPort = 8711;

    public static string DefaultDataDir() {
        var docs = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        if (string.IsNullOrEmpty(docs)) docs = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(docs, "StarCadence");
    }

    public static async Task<int> Run(string dataDir, int port, bool openBrowser, bool stayOpen) {
        dataDir = Path.GetFullPath(dataDir);
        Directory.CreateDirectory(dataDir);
        var cfgPath = Path.Combine(dataDir, "config.json");
        var firstRun = !File.Exists(cfgPath);
        if (firstRun) File.WriteAllText(cfgPath, Template.ConfigJson);

        // Already running? Open it again instead of starting a second copy.
        if (port == PreferredPort && await IsRunning(port)) {
            var existing = $"http://127.0.0.1:{port}/";
            Console.WriteLine($"StarCadence is already running at {existing}");
            if (openBrowser) OpenBrowser(existing);
            return 0;
        }

        if (!PortFree(port)) port = 0;   // something else uses it: take any free port
        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { ContentRootPath = AppContext.BaseDirectory });
        builder.Logging.ClearProviders();
        builder.WebHost.UseKestrel(o => o.Listen(IPAddress.Loopback, port));
        var app = builder.Build();
        var state = new Session(dataDir, cfgPath, firstRun);
        var runs = new RunService();

        // Only this computer, only with the token for changes.
        app.Use(async (ctx, next) => {
            var host = ctx.Request.Host.Host;
            if (host is not ("127.0.0.1" or "localhost")) { ctx.Response.StatusCode = 403; return; }
            if (!HttpMethods.IsGet(ctx.Request.Method) && ctx.Request.Headers["X-StarCadence"] != token) { ctx.Response.StatusCode = 403; return; }
            state.Seen();
            await next(ctx);
        });

        app.MapGet("/", () => Results.Content(Resource("index.html").Replace("__SC_TOKEN__", token), "text/html; charset=utf-8"));
        app.MapGet("/icon.svg", () => Results.Content(Resource("icon.svg"), "image/svg+xml"));
        app.MapGet("/api/ping", () => Results.Text("starcadence"));
        app.MapGet("/api/state", () => Json(state.Describe()));
        app.MapPost("/api/heartbeat", () => Results.Ok());
        app.MapPost("/api/quit", (IHostApplicationLifetime life) => {
            if (runs.Running) return Error("A night is being run. Stop it first, then quit.");
            _ = Task.Delay(300).ContinueWith(_ => life.StopApplication());
            return Results.Ok();
        });

        app.MapPost("/api/config", async (HttpContext ctx) => {
            Config? cfg;
            try { cfg = await JsonSerializer.DeserializeAsync<Config>(ctx.Request.Body, Config.Json); }
            catch (JsonException e) { return Error("That setting could not be read: " + e.Message); }
            if (cfg is null) return Error("No settings received.");
            try { cfg.Validate(); } catch (InvalidDataException e) { return Error(e.Message); }
            cfg.Save(cfgPath);
            return Json(state.Describe());
        });

        app.MapPost("/api/plan", async (HttpContext ctx) => {
            var req = await Body<PlanRequest>(ctx) ?? new PlanRequest(null, false);
            Config cfg;
            try { cfg = state.LoadConfig(); } catch (Exception e) when (e is InvalidDataException or JsonException) { return Error(e.Message); }
            DateOnly? date = null;
            if (!string.IsNullOrWhiteSpace(req.Date)) {
                if (!DateOnly.TryParseExact(req.Date, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)) return Error("Pick a date.");
                date = d;
            }
            var run = PlanRunner.Run(cfg, dataDir, date, req.FromNow ? DateTime.UtcNow : null, null, PlanRunner.AllFormats, out var err);
            if (run is null) return Error(err ?? "Could not plan this night.");
            return Json(new {
                plan = run.Plan,
                date = run.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                folder = run.OutDir,
                files = run.Files.Select(f => Path.GetRelativePath(dataDir, f).Replace('\\', '/')).ToList(),
                ekos = Path.GetRelativePath(dataDir, Path.GetDirectoryName(run.Files.FirstOrDefault(f => f.EndsWith("plan.esl")) ?? run.OutDir)!).Replace('\\', '/'),
                messages = run.Messages,
                sources = run.Sources.Select(s => new { s.Source, count = s.Targets.Count, messages = s.Messages.Take(6) }),
                local = run.Plan.Blocks.Select(b => new { start = run.Plan.Local(b.StartUtc), end = run.Plan.Local(b.EndUtc) }),
                dark = new { start = run.Plan.Local(run.Plan.NightStartUtc), end = run.Plan.Local(run.Plan.NightEndUtc), from = run.Plan.Local(run.Plan.PlannedFromUtc) },
                zone = TimeZones.Find(cfg.Site.TimeZone) is null ? "UTC" : cfg.Site.TimeZone,
                offsetHours = (TimeZones.ToLocal(run.Plan.NightStartUtc, cfg.Site.TimeZone) - run.Plan.NightStartUtc).TotalHours,
            });
        });

        app.MapGet("/api/file", (string path) => {
            var full = Inside(dataDir, path);
            if (full is null || !File.Exists(full)) return Results.NotFound();
            return Results.File(full, ContentType(full), Path.GetFileName(full));
        });

        app.MapGet("/api/ekoszip", (string path) => {
            var full = Inside(dataDir, path);
            if (full is null || !Directory.Exists(full)) return Results.NotFound();
            var ms = new MemoryStream();
            using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
                foreach (var f in Directory.GetFiles(full)) zip.CreateEntryFromFile(f, Path.GetFileName(f));
            ms.Position = 0;
            return Results.File(ms, "application/zip", "starcadence-ekos.zip");
        });

        app.MapPost("/api/fetch", async () => {
            Config cfg;
            try { cfg = state.LoadConfig(); } catch (Exception e) when (e is InvalidDataException or JsonException) { return Error(e.Message); }
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(120) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("StarCadence/" + Version());
            var log = await new SourceLoader(cfg, dataDir, http).FetchAsync();
            return Json(new { messages = log, state = state.Describe() });
        });

        app.MapPost("/api/list", async (HttpContext ctx) => {
            var req = await Body<ListUpload>(ctx);
            if (req is null || string.IsNullOrWhiteSpace(req.Text)) return Error("The file was empty.");
            var listsDir = Path.Combine(dataDir, "lists");
            Directory.CreateDirectory(listsDir);
            var name = PlanExport.Safe(Path.GetFileNameWithoutExtension(req.Name ?? "list")) + ".csv";
            var full = Path.Combine(listsDir, name);
            await File.WriteAllTextAsync(full, req.Text, Encoding.UTF8);
            var check = CsvTargets.Load(full);
            if (check.Targets.Count == 0) { File.Delete(full); return Error(string.Join(" ", check.Messages.Take(3))); }
            var cfg = state.LoadConfig();
            var rel = "lists/" + name;
            if (!cfg.Sources.CsvFiles.Contains(rel)) cfg.Sources.CsvFiles.Add(rel);
            cfg.Save(cfgPath);
            return Json(new { messages = check.Messages.Take(6), state = state.Describe() });
        });

        app.MapPost("/api/list/remove", async (HttpContext ctx) => {
            var req = await Body<PathRequest>(ctx);
            var cfg = state.LoadConfig();
            cfg.Sources.CsvFiles.RemoveAll(f => f == req?.Path);
            cfg.Save(cfgPath);
            return Json(state.Describe());
        });

        app.MapPost("/api/key", async (HttpContext ctx) => {
            var req = await Body<KeyRequest>(ctx);
            var cfg = state.LoadConfig();
            var keyFile = Path.Combine(dataDir, "targettool.key");
            if (string.IsNullOrWhiteSpace(req?.Key)) {
                if (File.Exists(keyFile)) File.Delete(keyFile);
                cfg.Sources.TargetToolKeyFile = null;
            } else {
                await File.WriteAllTextAsync(keyFile, req.Key.Trim());
                if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(keyFile, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                cfg.Sources.TargetToolKeyFile = "targettool.key";
            }
            cfg.Save(cfgPath);
            return Json(state.Describe());
        });

        app.MapGet("/api/run/status", () => Json(runs.Status()));
        app.MapPost("/api/run/start", async (HttpContext ctx) => {
            var req = await Body<RunRequest>(ctx) ?? new RunRequest();
            Config cfg;
            try { cfg = state.LoadConfig(); } catch (Exception e) when (e is InvalidDataException or JsonException) { return Error(e.Message); }
            if (req.Mode != "indi" && req.Mode != "simulate") return Error("Mode must be indi or simulate.");
            try { await runs.StartAsync(cfg, dataDir, req, blocking: false); }
            catch (Exception e) when (e is IOException or InvalidOperationException or TimeoutException or System.Net.Sockets.SocketException) { return Error(e.Message); }
            return Json(runs.Status());
        });
        app.MapPost("/api/run/stop", async () => { await runs.StopAsync(); return Json(runs.Status()); });

        app.MapPost("/api/record", async (HttpContext ctx) => {
            var req = await Body<PathRequest>(ctx);
            var full = req?.Path is null ? null : Inside(dataDir, req.Path);
            if (full is null || !File.Exists(full)) return Error("Plan the night first.");
            var cfg = state.LoadConfig();
            var plan = JsonSerializer.Deserialize<Plan>(await File.ReadAllTextAsync(full), Config.Json);
            if (plan is null) return Error("That plan file could not be read.");
            var histPath = PlanRunner.Resolve(dataDir, cfg.HistoryFile);
            var h = History.Load(histPath);
            var now = DateTime.UtcNow;
            var done = plan.Blocks.Where(b => b.EndUtc <= now).ToList();
            foreach (var b in done) h.Record(b.Target, b.ExposeFromUtc);
            h.Save(histPath);
            return Json(new { recorded = done.Count, notYet = plan.Blocks.Count - done.Count });
        });

        await app.StartAsync();
        var url = app.Services.GetRequiredService<Microsoft.AspNetCore.Hosting.Server.IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First()
            .Replace("[::1]", "127.0.0.1").TrimEnd('/') + "/";
        Console.WriteLine($"StarCadence is running at {url}");
        Console.WriteLine($"Your settings and plans are in {dataDir}");
        Console.WriteLine(stayOpen ? "Press Ctrl+C to stop." : "It stops a few minutes after you close its browser tab (or press Quit, or close this window).");
        if (openBrowser) OpenBrowser(url);

        if (!stayOpen) {
            var life = app.Services.GetRequiredService<IHostApplicationLifetime>();
            _ = Task.Run(async () => {
                while (!life.ApplicationStopping.IsCancellationRequested) {
                    await Task.Delay(TimeSpan.FromSeconds(20));
                    if (runs.Running) continue;   // never stop while a night is being run
                    var idle = DateTime.UtcNow - state.LastSeen;
                    // Stop once the page has gone away (browsers slow background tabs to about one heartbeat a minute).
                    if (state.EverSeen && idle > TimeSpan.FromMinutes(4)) life.StopApplication();
                    if (!state.EverSeen && idle > TimeSpan.FromMinutes(30)) life.StopApplication();
                }
            });
        }
        await app.WaitForShutdownAsync();
        if (runs.Running) {
            // Ctrl+C or the system shutting the program down during a night: finish the current step and park first.
            Console.WriteLine("Stopping the night run (finishing the current step, then parking)...");
            await runs.StopAsync(TimeSpan.FromMinutes(3));
        }
        Console.WriteLine("StarCadence stopped.");
        return 0;
    }

    sealed record PlanRequest(string? Date, bool FromNow);
    sealed record ListUpload(string? Name, string? Text);
    sealed record PathRequest(string? Path);
    sealed record KeyRequest(string? Key);

    sealed class Session(string dataDir, string cfgPath, bool firstRun) {
        public DateTime LastSeen { get; private set; } = DateTime.UtcNow;
        public bool EverSeen { get; private set; }
        public void Seen() { LastSeen = DateTime.UtcNow; EverSeen = true; }

        public Config LoadConfig() => StarCadence.Core.Model.Config.Load(cfgPath);

        public object Describe() {
            Config? cfg = null; string? problem = null;
            try { cfg = LoadConfig(); } catch (Exception e) when (e is InvalidDataException or JsonException) { problem = e.Message; }
            var keyStatus = "none";
            if (cfg is not null) {
                if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(cfg.Sources.TargetToolKeyVariable))) keyStatus = "environment";
                else if (TargetTool.FindKey(cfg.Sources, dataDir) is not null) keyStatus = "saved";
            }
            List<object> sources = new();
            string? tonight = null;
            if (cfg is not null) {
                var loader = new SourceLoader(cfg, dataDir, null);
                foreach (var r in loader.LoadAll(DateTime.UtcNow)) sources.Add(new { r.Source, count = r.Targets.Count, messages = r.Messages.Take(6) });
                tonight = Night.CurrentOrNext(cfg.Site, DateTime.UtcNow).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                var tt = loader.CachePath("targettool.json");
                var ex = loader.CachePath("exoclock.json");
                return new {
                    version = Version(), dataDir, configPath = cfgPath, firstRun, problem, config = cfg, keyStatus, tonight, sources,
                    downloads = new {
                        targetTool = File.Exists(tt) ? File.GetLastWriteTimeUtc(tt).ToString("yyyy-MM-dd HH:mm") + " UTC" : null,
                        exoClock = File.Exists(ex) ? File.GetLastWriteTimeUtc(ex).ToString("yyyy-MM-dd HH:mm") + " UTC" : null,
                    },
                    rules = RuleWeights(cfg),
                    plans = LatestPlans(),
                };
            }
            return new { version = Version(), dataDir, configPath = cfgPath, firstRun, problem, keyStatus, sources };
        }

        static List<object> RuleWeights(Config cfg) {
            var engine = new ScoringEngine(cfg.Planner.RuleWeights);
            return engine.RuleSet.Select(r => (object)new { r.Name, weight = engine.Weight(r.Name) }).ToList();
        }

        List<string> LatestPlans() {
            var dir = Path.Combine(dataDir, "plans");
            if (!Directory.Exists(dir)) return new();
            return Directory.GetDirectories(dir).Select(Path.GetFileName).Where(n => n is not null).OrderByDescending(n => n).Take(10).ToList()!;
        }
    }

    static IResult Json(object o) => Results.Json(o, Config.Json);
    static IResult Error(string message) => Results.Json(new { error = message }, Config.Json, statusCode: 400);

    static async Task<T?> Body<T>(HttpContext ctx) where T : class {
        try { return await JsonSerializer.DeserializeAsync<T>(ctx.Request.Body, Config.Json); } catch (JsonException) { return null; }
    }

    /// <summary>A path inside the data folder, or null (no "..", no absolute paths elsewhere).</summary>
    static string? Inside(string root, string rel) {
        if (string.IsNullOrWhiteSpace(rel)) return null;
        var full = Path.GetFullPath(Path.Combine(root, rel));
        var r = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return full.StartsWith(r, OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal) ? full : null;
    }

    static string ContentType(string path) => Path.GetExtension(path).ToLowerInvariant() switch {
        ".csv" => "text/csv", ".json" => "application/json", ".txt" => "text/plain; charset=utf-8", ".esl" or ".esq" => "application/xml", _ => "application/octet-stream"
    };

    static string Resource(string name) {
        using var s = typeof(AppServer).Assembly.GetManifestResourceStream(name) ?? throw new FileNotFoundException(name);
        using var r = new StreamReader(s);
        return r.ReadToEnd();
    }

    public static string Version() => typeof(Planner).Assembly.GetName().Version?.ToString(3) ?? "0";

    static bool PortFree(int port) {
        if (port == 0) return true;
        try { var l = new System.Net.Sockets.TcpListener(IPAddress.Loopback, port); l.Start(); l.Stop(); return true; }
        catch (System.Net.Sockets.SocketException) { return false; }
    }

    static async Task<bool> IsRunning(int port) {
        try {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(1) };
            return (await http.GetStringAsync($"http://127.0.0.1:{port}/api/ping")) == "starcadence";
        } catch (Exception) { return false; }
    }

    public static void OpenBrowser(string url) {
        try {
            if (OperatingSystem.IsWindows()) Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            else if (OperatingSystem.IsMacOS()) Process.Start("open", url);
            else Process.Start("xdg-open", url);
        } catch (Exception) {
            Console.WriteLine("Open this address in your web browser: " + url);
        }
    }
}
