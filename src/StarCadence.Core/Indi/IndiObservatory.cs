// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at http://mozilla.org/MPL/2.0/.

using System.Globalization;
using StarCadence.Core.Astro;
using StarCadence.Core.Model;
using StarCadence.Core.Run;

namespace StarCadence.Core.Indi;

/// <summary>Settings for running the night directly through an INDI server (KStars/Ekos setups, Mac and Linux included).</summary>
public sealed class IndiSettings {
    public string Host { get; set; } = "localhost";
    public int Port { get; set; } = 7624;
    /// <summary>Device names; blank = pick the first device of that kind.</summary>
    public string? Telescope { get; set; }
    public string? Camera { get; set; }
    public string? FilterWheel { get; set; }
    /// <summary>Where frames are saved (relative to the config folder); a sub-folder per night is added.</summary>
    public string ImageDir { get; set; } = "images";
    public double SettleSeconds { get; set; } = 3;
    public double SlewTimeoutSeconds { get; set; } = 180;
    public bool ParkAtEnd { get; set; } = true;
    public int BinX { get; set; } = 1;
    public int BinY { get; set; } = 1;
    /// <summary>Cool the camera to this temperature at the start (null = leave it).</summary>
    public double? CameraTemperature { get; set; }
}

/// <summary>
/// Drives a mount, filter wheel and camera over INDI. What it does: unpark, slew with tracking, change filter,
/// expose, save each frame as FITS with OBJECT/FILTER/RA/DEC cards. What it does not do: plate-solve, focus or
/// guide; when you need those, use the Ekos export instead and let Ekos run the plan.
/// </summary>
public sealed class IndiObservatory : IObservatory, IAsyncDisposable {
    readonly IndiSettings _s;
    readonly string _imageRoot;
    readonly Action<string> _log;
    readonly IndiClient _client = new();
    readonly IReadOnlyList<FilterSpec> _filters;
    string _mount = "", _camera = "", _wheel = "";
    string _blobProperty = "CCD1";
    Dictionary<string, int> _slots = new(StringComparer.OrdinalIgnoreCase);
    public int FramesSaved { get; private set; }
    public List<string> SavedFiles { get; } = new();

    public IndiObservatory(IndiSettings settings, IReadOnlyList<FilterSpec> filters, string imageRoot, Action<string>? log = null) {
        _s = settings; _filters = filters; _imageRoot = imageRoot; _log = log ?? (_ => { });
        _client.Message += (d, m) => { if (m.Length > 0) _log($"[{d}] {m}"); };
    }

    public string Mount => _mount;
    public string Camera => _camera;
    public string FilterWheel => _wheel;
    public IReadOnlyDictionary<string, int> FilterSlots => _slots;

    /// <summary>Connect to the server, find the devices, connect them, and read the filter names.</summary>
    public async Task ConnectAsync(CancellationToken ct) {
        _log($"INDI: connecting to {_s.Host}:{_s.Port}");
        await _client.ConnectAsync(_s.Host, _s.Port, ct).ConfigureAwait(false);
        // Give the server a moment to list every device and its driver information.
        var until = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < until) {
            await Task.Delay(300, ct).ConfigureAwait(false);
            if (_client.Devices.Count > 0 && _client.Devices.All(d => _client.Get(d, "DRIVER_INFO") is not null)) break;
        }
        if (_client.ReaderError is not null) throw new IOException("INDI connection failed: " + _client.ReaderError.Message);
        var devices = _client.Devices;
        if (devices.Count == 0) throw new InvalidOperationException($"No INDI devices at {_s.Host}:{_s.Port}. Is the INDI server running with drivers?");
        _mount = Pick(_s.Telescope, devices, 1, "telescope");
        _camera = Pick(_s.Camera, devices, 2, "camera");
        // Prefer a separate filter wheel; fall back to a wheel built into the camera.
        var wheels = devices.Where(d => (_client.Interface(d) & 16) != 0 && d != _camera).ToList();
        _wheel = !string.IsNullOrWhiteSpace(_s.FilterWheel) ? Pick(_s.FilterWheel, devices, 16, "filter wheel")
               : wheels.Count > 0 ? wheels[0]
               : (_client.Interface(_camera) & 16) != 0 || _client.Get(_camera, "FILTER_SLOT") is not null ? _camera : "";
        _log($"INDI: mount '{_mount}', camera '{_camera}', filter wheel '{(_wheel == "" ? "(none)" : _wheel)}'");

        foreach (var d in new[] { _mount, _camera, _wheel }.Where(d => d != "").Distinct()) await ConnectDevice(d, ct).ConfigureAwait(false);

        await _client.WaitForAsync(_mount, "EQUATORIAL_EOD_COORD", _ => true, TimeSpan.FromSeconds(20), ct).ConfigureAwait(false);
        await _client.WaitForAsync(_camera, "CCD_EXPOSURE", _ => true, TimeSpan.FromSeconds(20), ct).ConfigureAwait(false);
        var blob = _client.Properties(_camera).FirstOrDefault(p => p.Type == IndiType.Blob);
        if (blob is not null) _blobProperty = blob.Name;
        await _client.EnableBlobAsync(_camera, "Also", ct).ConfigureAwait(false);
        if (_client.Get(_camera, "UPLOAD_MODE") is not null) await _client.SelectAsync(_camera, "UPLOAD_MODE", "UPLOAD_CLIENT", ct).ConfigureAwait(false);
        if (_client.Get(_camera, "CCD_BINNING") is not null) await _client.SetNumberAsync(_camera, "CCD_BINNING", ct, ("HOR_BIN", _s.BinX), ("VER_BIN", _s.BinY)).ConfigureAwait(false);
        if (_s.CameraTemperature is double temp && _client.Get(_camera, "CCD_TEMPERATURE") is not null)
            await _client.SetNumberAsync(_camera, "CCD_TEMPERATURE", ct, ("CCD_TEMPERATURE_VALUE", temp)).ConfigureAwait(false);

        if (_wheel != "") await RefreshFiltersAsync(ct).ConfigureAwait(false);
        else if (_filters.Count > 1) _log("INDI: no filter wheel found; every exposure will use whatever filter is in place");
    }

    /// <summary>Re-read the wheel's slot names (after renaming them in the driver).</summary>
    public async Task RefreshFiltersAsync(CancellationToken ct) {
        var names = await _client.WaitForAsync(_wheel, "FILTER_NAME", _ => true, TimeSpan.FromSeconds(20), ct).ConfigureAwait(false);
        _slots = ReadSlots(names);
        _log("INDI: filters " + string.Join(", ", _slots.OrderBy(k => k.Value).Select(k => $"{k.Value}={k.Key}")));
        var missing = _filters.Where(f => ResolveSlot(f.Name) is null).Select(f => f.Name).ToList();
        if (missing.Count > 0) _log($"INDI: these configured filters are not in the wheel: {string.Join(", ", missing)} (name them the same in both places)");
    }

    /// <summary>Rename the wheel's slots in the driver (what Ekos does in its filter settings).</summary>
    public async Task NameFiltersAsync(IReadOnlyList<string> names, CancellationToken ct) {
        if (_wheel == "") throw new InvalidOperationException("No filter wheel.");
        var prop = _client.Get(_wheel, "FILTER_NAME") ?? throw new InvalidOperationException("The wheel has no FILTER_NAME property.");
        var keys = prop.Elements.Keys.OrderBy(k => int.TryParse(System.Text.RegularExpressions.Regex.Match(k, @"\d+$").Value, out var n) ? n : 0).ToList();
        var values = keys.Select((k, i) => (k, i < names.Count ? names[i] : prop.Elements[k].Text)).ToArray();
        await _client.SetTextAsync(_wheel, "FILTER_NAME", ct, values).ConfigureAwait(false);
        await _client.WaitForAsync(_wheel, "FILTER_NAME", p => p.State != IndiState.Busy && keys.Take(names.Count).Select(k => p[k]?.Text).SequenceEqual(names), TimeSpan.FromSeconds(15), ct).ConfigureAwait(false);
        await RefreshFiltersAsync(ct).ConfigureAwait(false);
    }

    public IndiClient Client => _client;

    string Pick(string? wanted, IReadOnlyList<string> devices, int bit, string kind) {
        if (!string.IsNullOrWhiteSpace(wanted)) {
            var d = devices.FirstOrDefault(x => x.Equals(wanted, StringComparison.OrdinalIgnoreCase)) ?? throw new InvalidOperationException($"INDI {kind} '{wanted}' not found. Devices: {string.Join(", ", devices)}");
            return d;
        }
        var byInterface = devices.Where(x => (_client.Interface(x) & bit) != 0).ToList();
        if (byInterface.Count == 0) throw new InvalidOperationException($"No INDI {kind} found. Devices: {string.Join(", ", devices.Select(x => $"{x} ({_client.Interface(x)})"))}");
        if (bit == 16 && byInterface.Count > 1) byInterface = byInterface.Where(x => (_client.Interface(x) & 2) == 0).Concat(byInterface).ToList();
        return byInterface[0];
    }

    static Dictionary<string, int> ReadSlots(IndiProperty names) {
        var d = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in names.Elements.Values) {
            var m = System.Text.RegularExpressions.Regex.Match(e.Name, @"(\d+)$");
            if (m.Success && e.Text.Length > 0 && !d.ContainsKey(e.Text)) d[e.Text] = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
        }
        return d;
    }

    async Task ConnectDevice(string device, CancellationToken ct) {
        var conn = _client.Get(device, "CONNECTION");
        if (conn?["CONNECT"]?.On == true) return;
        _log($"INDI: connecting {device}");
        await _client.SelectAsync(device, "CONNECTION", "CONNECT", ct).ConfigureAwait(false);
        var p = await _client.WaitForAsync(device, "CONNECTION", x => x.State != IndiState.Busy && (x["CONNECT"]?.On == true || x.State == IndiState.Alert), TimeSpan.FromSeconds(30), ct).ConfigureAwait(false);
        if (p["CONNECT"]?.On != true) throw new IOException($"INDI device {device} did not connect (driver reported an error).");
    }

    int? ResolveSlot(string filterName) {
        if (_slots.TryGetValue(filterName, out var slot)) return slot;
        var spec = _filters.FirstOrDefault(f => f.Name.Equals(filterName, StringComparison.OrdinalIgnoreCase));
        if (spec is not null && _slots.TryGetValue(spec.Aavso, out slot)) return slot;
        var close = _slots.Keys.FirstOrDefault(k => k.StartsWith(filterName, StringComparison.OrdinalIgnoreCase) || filterName.StartsWith(k, StringComparison.OrdinalIgnoreCase));
        return close is null ? null : _slots[close];
    }

    public async Task SlewAsync(double raDeg, double decDeg, string targetName, CancellationToken ct) {
        var park = _client.Get(_mount, "TELESCOPE_PARK");
        if (park?["PARK"]?.On == true) {
            _log("INDI: unparking");
            await _client.SelectAsync(_mount, "TELESCOPE_PARK", "UNPARK", ct).ConfigureAwait(false);
            await _client.WaitForAsync(_mount, "TELESCOPE_PARK", p => p.State != IndiState.Busy, TimeSpan.FromSeconds(120), ct).ConfigureAwait(false);
        }
        if (_client.Get(_mount, "ON_COORD_SET") is not null) await _client.SelectAsync(_mount, "ON_COORD_SET", "TRACK", ct).ConfigureAwait(false);
        var (ra, dec) = Coords.PrecessFromJ2000(raDeg, decDeg, AstroTime.JulianDate(DateTime.UtcNow));
        await _client.SetNumberAsync(_mount, "EQUATORIAL_EOD_COORD", ct, ("RA", ra / 15.0), ("DEC", dec)).ConfigureAwait(false);
        // The driver answers Busy while slewing, then Ok (or Alert on failure).
        var sawBusy = false;
        var done = await _client.WaitForAsync(_mount, "EQUATORIAL_EOD_COORD", p => {
            if (p.State == IndiState.Busy) { sawBusy = true; return false; }
            return p.State == IndiState.Alert || (sawBusy && p.State is IndiState.Ok or IndiState.Idle) || (p.State == IndiState.Ok && Close(p, ra / 15.0, dec));
        }, TimeSpan.FromSeconds(_s.SlewTimeoutSeconds), ct).ConfigureAwait(false);
        if (done.State == IndiState.Alert) throw new IOException($"The mount refused the slew to {targetName} (below the horizon or limits?).");
        if (_s.SettleSeconds > 0) await Task.Delay(TimeSpan.FromSeconds(_s.SettleSeconds), ct).ConfigureAwait(false);
    }

    static bool Close(IndiProperty p, double raH, double dec) =>
        p["RA"] is IndiElement r && p["DEC"] is IndiElement d && Math.Abs(AstroMath.Norm180((r.Number - raH) * 15)) < 0.02 && Math.Abs(d.Number - dec) < 0.02;

    public async Task SetFilterAsync(string filterName, CancellationToken ct) {
        if (_wheel == "") return;
        var slot = ResolveSlot(filterName) ?? throw new InvalidOperationException($"Filter '{filterName}' is not in the wheel ({string.Join(", ", _slots.Keys)}).");
        var current = _client.Get(_wheel, "FILTER_SLOT")?["FILTER_SLOT_VALUE"]?.Number;
        if (current is double c && (int)Math.Round(c) == slot) return;
        await _client.SetNumberAsync(_wheel, "FILTER_SLOT", ct, ("FILTER_SLOT_VALUE", slot)).ConfigureAwait(false);
        var p = await _client.WaitForAsync(_wheel, "FILTER_SLOT", x => x.State != IndiState.Busy && (x.State == IndiState.Alert || (int)Math.Round(x["FILTER_SLOT_VALUE"]?.Number ?? -1) == slot), TimeSpan.FromSeconds(90), ct).ConfigureAwait(false);
        if (p.State == IndiState.Alert) throw new IOException($"The filter wheel could not move to {filterName}.");
    }

    public async Task ExposeAsync(ExposureRequest r, CancellationToken ct) {
        if (_client.Get(_camera, "CCD_FRAME_TYPE") is not null) await _client.SelectAsync(_camera, "CCD_FRAME_TYPE", "FRAME_LIGHT", ct).ConfigureAwait(false);
        var startedUtc = DateTime.UtcNow;
        var blobTask = _client.NextBlob(_camera, _blobProperty, TimeSpan.FromSeconds(r.Seconds + 90), ct);
        await _client.SetNumberAsync(_camera, "CCD_EXPOSURE", ct, ("CCD_EXPOSURE_VALUE", r.Seconds)).ConfigureAwait(false);
        var blob = await blobTask.ConfigureAwait(false);
        var data = blob.Data;
        var ext = blob.Format.StartsWith('.') ? blob.Format : "." + blob.Format;
        if (string.IsNullOrWhiteSpace(blob.Format)) ext = ".fits";
        if (ext.Equals(".fits", StringComparison.OrdinalIgnoreCase) || ext.Equals(".fit", StringComparison.OrdinalIgnoreCase)) {
            data = Fits.WithCards(data, new (string, object?, string?)[] {
                ("OBJECT", r.Target, "target name"),
                ("FILTER", r.Filter, "filter name"),
                ("OBJCTRA", Coords.FormatRa(r.RaDeg, 2).Replace(':', ' '), "J2000 RA of target"),
                ("OBJCTDEC", Coords.FormatDec(r.DecDeg, 1).Replace(':', ' '), "J2000 Dec of target"),
                ("RA", Math.Round(r.RaDeg, 6), "J2000 RA of target, degrees"),
                ("DEC", Math.Round(r.DecDeg, 6), "J2000 Dec of target, degrees"),
                ("IMAGETYP", "Light Frame", null),
                ("SWCREATE", "StarCadence", "planned and run by StarCadence"),
            });
        }
        var night = Planning.Night.NightDateOf(new Site(), startedUtc).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var dir = Path.Combine(_imageRoot, night);
        Directory.CreateDirectory(dir);
        var name = $"{Export.PlanExport.Safe(r.Target)}_{r.Filter}_{startedUtc:yyyyMMdd_HHmmss}_{r.Index:000}{ext}";
        var path = Path.Combine(dir, name);
        await File.WriteAllBytesAsync(path, data, ct).ConfigureAwait(false);
        FramesSaved++;
        SavedFiles.Add(path);
    }

    public Task<bool> IsSafeAsync(CancellationToken ct) => Task.FromResult(true);

    public async Task FinishAsync(CancellationToken ct) {
        if (_s.ParkAtEnd && _mount != "" && _client.Connected && _client.Get(_mount, "TELESCOPE_PARK") is not null) {
            try {
                _log("INDI: parking");
                await _client.SelectAsync(_mount, "TELESCOPE_PARK", "PARK", ct).ConfigureAwait(false);
                await _client.WaitForAsync(_mount, "TELESCOPE_PARK", p => p.State != IndiState.Busy, TimeSpan.FromSeconds(180), ct).ConfigureAwait(false);
            } catch (Exception e) { _log("INDI: park failed: " + e.Message); }
        }
    }

    /// <summary>Stop tracking while nothing is being observed, so the mount does not track into the meridian or a limit.
    /// The next slew (ON_COORD_SET = TRACK) starts tracking again.</summary>
    public async Task WaitingAsync(TimeSpan duration, CancellationToken ct) {
        if (_mount == "" || !_client.Connected || _client.Get(_mount, "TELESCOPE_TRACK_STATE") is null) return;
        var park = _client.Get(_mount, "TELESCOPE_PARK");
        if (park is not null && park.Elements.TryGetValue("PARK", out var parked) && parked.On) return;
        await _client.SelectAsync(_mount, "TELESCOPE_TRACK_STATE", "TRACK_OFF", ct).ConfigureAwait(false);
        _log($"INDI: tracking off while waiting ({duration.TotalMinutes:0} min)");
    }

    public ValueTask DisposeAsync() => _client.DisposeAsync();
}
