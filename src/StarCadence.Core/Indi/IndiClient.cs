// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at http://mozilla.org/MPL/2.0/.

using System.Globalization;
using System.Net.Sockets;
using System.Text;
using System.Xml.Linq;

namespace StarCadence.Core.Indi;

public enum IndiState { Idle, Ok, Busy, Alert }
public enum IndiType { Number, Switch, Text, Light, Blob }

public sealed class IndiElement {
    public string Name { get; init; } = "";
    public string Label { get; set; } = "";
    public string Text { get; set; } = "";
    public double Number { get; set; }
    public bool On { get; set; }
    public string? Format { get; set; }
    public double Min { get; set; }
    public double Max { get; set; }
}

public sealed class IndiProperty {
    public string Device { get; init; } = "";
    public string Name { get; init; } = "";
    public IndiType Type { get; init; }
    public string Label { get; set; } = "";
    public string Group { get; set; } = "";
    public string Perm { get; set; } = "rw";
    public string Rule { get; set; } = "";
    public IndiState State { get; set; }
    public Dictionary<string, IndiElement> Elements { get; } = new(StringComparer.Ordinal);
    public IndiElement? this[string element] => Elements.TryGetValue(element, out var e) ? e : null;
    public override string ToString() => $"{Device}.{Name} [{Type} {State}] " + string.Join(", ", Elements.Values.Select(e => Type switch {
        IndiType.Number => $"{e.Name}={e.Number.ToString(CultureInfo.InvariantCulture)}", IndiType.Switch => $"{e.Name}={(e.On ? "On" : "Off")}", _ => $"{e.Name}={e.Text}" }));
}

public sealed record IndiBlob(string Device, string Property, string Element, string Format, byte[] Data, DateTime ReceivedUtc);

/// <summary>
/// A small INDI client (the protocol KStars/Ekos and many observatories use): one TCP connection, XML messages in
/// both directions. It keeps the current value of every property, lets callers wait for a state, and hands out
/// BLOBs (images). INDI protocol 1.7; see indilib.org for the message definitions.
/// </summary>
public sealed class IndiClient : IAsyncDisposable {
    TcpClient? _tcp;
    NetworkStream? _stream;
    Task? _reader;
    readonly CancellationTokenSource _cts = new();
    readonly object _lock = new();
    readonly Dictionary<string, Dictionary<string, IndiProperty>> _devices = new(StringComparer.Ordinal);
    readonly List<(Func<IndiProperty, bool> test, TaskCompletionSource<IndiProperty> tcs)> _waiters = new();
    readonly List<(string device, string prop, TaskCompletionSource<IndiBlob> tcs)> _blobWaiters = new();
    readonly SemaphoreSlim _send = new(1, 1);

    public event Action<IndiProperty>? PropertyChanged;
    public event Action<string, string>? Message;
    public event Action<IndiBlob>? BlobReceived;
    public Exception? ReaderError { get; private set; }
    public bool Connected => _tcp?.Connected == true && ReaderError is null;

    public async Task ConnectAsync(string host, int port, CancellationToken ct) {
        if (_tcp is not null) throw new InvalidOperationException("Already connected; make a new client to reconnect.");
        _tcp = new TcpClient { NoDelay = true, ReceiveBufferSize = 1 << 20 };
        using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct)) {
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            await _tcp.ConnectAsync(host, port, timeout.Token).ConfigureAwait(false);
        }
        _stream = _tcp.GetStream();
        _reader = Task.Run(() => ReadLoop(_cts.Token));
        await SendAsync("<getProperties version=\"1.7\"/>", ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Reads the stream and hands every complete top-level element to Handle. INDI sends a sequence of elements with
    /// no root, so the stream is split by tracking tag depth (text and BLOB contents never contain '&lt;'), and each
    /// element is parsed on its own.
    /// </summary>
    async Task ReadLoop(CancellationToken ct) {
        var decoder = Encoding.UTF8.GetDecoder();
        var bytes = new byte[1 << 16];
        var chars = new char[1 << 17];
        var element = new StringBuilder();
        var tag = new StringBuilder();
        var depth = 0; var inTag = false; char quote = '\0';
        var leading = true;   // skipping whitespace between elements
        try {
            while (!ct.IsCancellationRequested) {
                var n = await _stream!.ReadAsync(bytes, ct).ConfigureAwait(false);
                if (n == 0) throw new IOException("the INDI server closed the connection");
                var count = decoder.GetChars(bytes, 0, n, chars, 0);
                for (var i = 0; i < count; i++) {
                    var ch = chars[i];
                    if (leading) { if (char.IsWhiteSpace(ch)) continue; leading = false; }
                    element.Append(ch);
                    if (inTag) {
                        tag.Append(ch);
                        if (quote != '\0') { if (ch == quote) quote = '\0'; continue; }
                        if (ch == '"' || ch == '\'') { quote = ch; continue; }
                        if (ch != '>') continue;
                        inTag = false;
                        var t = tag.ToString(); tag.Clear();
                        if (t.StartsWith("<?") || t.StartsWith("<!")) { if (depth == 0) { element.Clear(); leading = true; } continue; }
                        if (t.StartsWith("</")) depth--;
                        else if (!t.EndsWith("/>")) depth++;
                        if (depth <= 0) {
                            depth = 0;
                            var xml = element.ToString(); element.Clear(); leading = true;
                            try { Handle(XElement.Parse(xml, LoadOptions.None)); }
                            catch (System.Xml.XmlException e) { Message?.Invoke("", "unreadable INDI message: " + e.Message); }
                        }
                    } else if (ch == '<') { inTag = true; tag.Clear(); tag.Append(ch); }
                }
            }
        } catch (Exception e) when (!ct.IsCancellationRequested) {
            ReaderError = e;
            FailWaiters(e);
        }
    }

    void FailWaiters(Exception e) {
        lock (_lock) {
            foreach (var (_, tcs) in _waiters) tcs.TrySetException(e);
            foreach (var (_, _, tcs) in _blobWaiters) tcs.TrySetException(e);
            _waiters.Clear(); _blobWaiters.Clear();
        }
    }

    static IndiState ParseState(string? s) => s switch { "Ok" => IndiState.Ok, "Busy" => IndiState.Busy, "Alert" => IndiState.Alert, _ => IndiState.Idle };

    void Handle(XElement el) {
        var tag = el.Name.LocalName;
        var device = (string?)el.Attribute("device") ?? "";
        var name = (string?)el.Attribute("name") ?? "";
        if (tag == "message") { Message?.Invoke(device, ((string?)el.Attribute("message") ?? "").Trim()); return; }
        if (tag == "delProperty") {
            lock (_lock) {
                if (string.IsNullOrEmpty(name)) _devices.Remove(device);
                else if (_devices.TryGetValue(device, out var d)) d.Remove(name);
            }
            return;
        }
        var isDef = tag.StartsWith("def"); var isSet = tag.StartsWith("set");
        if (!isDef && !isSet) return;
        var kind = tag[3..].Replace("Vector", "");
        var type = kind switch { "Number" => IndiType.Number, "Switch" => IndiType.Switch, "Text" => IndiType.Text, "Light" => IndiType.Light, "BLOB" => IndiType.Blob, _ => (IndiType?)null };
        if (type is null) return;
        IndiProperty prop;
        List<IndiBlob> blobs = new();
        lock (_lock) {
            if (!_devices.TryGetValue(device, out var props)) _devices[device] = props = new(StringComparer.Ordinal);
            if (!props.TryGetValue(name, out prop!) || (isDef && prop.Type != type)) props[name] = prop = new IndiProperty { Device = device, Name = name, Type = type.Value };
            if (isDef) {
                prop.Label = (string?)el.Attribute("label") ?? name; prop.Group = (string?)el.Attribute("group") ?? "";
                prop.Perm = (string?)el.Attribute("perm") ?? prop.Perm; prop.Rule = (string?)el.Attribute("rule") ?? "";
            }
            if (el.Attribute("state") is XAttribute st) prop.State = ParseState(st.Value);
            foreach (var child in el.Elements()) {
                var en = (string?)child.Attribute("name") ?? "";
                if (!prop.Elements.TryGetValue(en, out var e)) prop.Elements[en] = e = new IndiElement { Name = en };
                if (isDef) {
                    e.Label = (string?)child.Attribute("label") ?? en;
                    e.Format = (string?)child.Attribute("format");
                    e.Min = Dbl((string?)child.Attribute("min")); e.Max = Dbl((string?)child.Attribute("max"));
                }
                var text = child.Value.Trim();
                switch (type) {
                    case IndiType.Number: e.Number = Sexagesimal(text); break;
                    case IndiType.Switch: e.On = text.Equals("On", StringComparison.OrdinalIgnoreCase); break;
                    case IndiType.Text: case IndiType.Light: e.Text = text; break;
                    case IndiType.Blob:
                        if (isSet && text.Length > 0) {
                            var fmt = (string?)child.Attribute("format") ?? "";
                            byte[] data;
                            try { data = Convert.FromBase64String(text); } catch (FormatException) { data = Array.Empty<byte>(); }
                            blobs.Add(new IndiBlob(device, name, en, fmt, data, DateTime.UtcNow));
                        }
                        break;
                }
            }
        }
        PropertyChanged?.Invoke(prop);
        foreach (var b in blobs) {
            BlobReceived?.Invoke(b);
            List<TaskCompletionSource<IndiBlob>> done = new();
            lock (_lock) {
                foreach (var w in _blobWaiters.Where(w => w.device == b.Device && w.prop == b.Property).ToList()) { done.Add(w.tcs); _blobWaiters.Remove(w); }
            }
            foreach (var t in done) t.TrySetResult(b);
        }
        List<TaskCompletionSource<IndiProperty>> fire = new();
        lock (_lock) {
            foreach (var w in _waiters.ToList()) {
                bool hit;
                try { hit = w.test(prop); } catch { hit = false; }
                if (hit) { fire.Add(w.tcs); _waiters.Remove(w); }
            }
        }
        foreach (var t in fire) t.TrySetResult(prop);
    }

    static double Dbl(string? s) => double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : 0;

    /// <summary>INDI numbers may be sexagesimal ("12:34:56.7" or "12 34 56").</summary>
    public static double Sexagesimal(string s) {
        s = s.Trim();
        if (double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v)) return v;
        var parts = s.Split(new[] { ':', ' ' }, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return 0;
        var neg = parts[0].StartsWith('-');
        double total = 0, scale = 1;
        foreach (var p in parts.Take(3)) { total += Math.Abs(Dbl(p)) / scale; scale *= 60; }
        return neg ? -total : total;
    }

    public IReadOnlyList<string> Devices { get { lock (_lock) return _devices.Keys.ToList(); } }

    public IndiProperty? Get(string device, string property) {
        lock (_lock) return _devices.TryGetValue(device, out var p) && p.TryGetValue(property, out var prop) ? prop : null;
    }

    public IReadOnlyList<IndiProperty> Properties(string device) {
        lock (_lock) return _devices.TryGetValue(device, out var p) ? p.Values.ToList() : new List<IndiProperty>();
    }

    /// <summary>The DRIVER_INTERFACE bit mask of a device (telescope 1, CCD 2, filter 16, weather 128, ...).</summary>
    public int Interface(string device) => (int)(Get(device, "DRIVER_INFO")?["DRIVER_INTERFACE"] is IndiElement e ? Dbl(e.Text) : 0);

    public Task<IndiProperty> WaitForAsync(Func<IndiProperty, bool> test, TimeSpan timeout, CancellationToken ct, string what = "property") {
        if (ReaderError is not null) throw new IOException("INDI connection lost: " + ReaderError.Message, ReaderError);
        var tcs = new TaskCompletionSource<IndiProperty>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_lock) {
            foreach (var d in _devices.Values) foreach (var p in d.Values) if (Safe(test, p)) return Task.FromResult(p);
            _waiters.Add((test, tcs));
        }
        return WithTimeout(tcs, timeout, ct, what);
    }

    static bool Safe(Func<IndiProperty, bool> test, IndiProperty p) { try { return test(p); } catch { return false; } }

    public Task<IndiProperty> WaitForAsync(string device, string property, Func<IndiProperty, bool> test, TimeSpan timeout, CancellationToken ct) =>
        WaitForAsync(p => p.Device == device && p.Name == property && test(p), timeout, ct, $"{device}.{property}");

    public Task<IndiBlob> WaitForBlobAsync(string device, string property, TimeSpan timeout, CancellationToken ct) {
        var tcs = new TaskCompletionSource<IndiBlob>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_lock) _blobWaiters.Add((device, property, tcs));
        return WithTimeout(tcs, timeout, ct, $"image from {device}");
    }

    /// <summary>Call before starting an exposure so the next image is not missed.</summary>
    public Task<IndiBlob> NextBlob(string device, string property, TimeSpan timeout, CancellationToken ct) => WaitForBlobAsync(device, property, timeout, ct);

    async Task<T> WithTimeout<T>(TaskCompletionSource<T> tcs, TimeSpan timeout, CancellationToken ct, string what) {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        using var reg = cts.Token.Register(() => tcs.TrySetCanceled());
        try { return await tcs.Task.ConfigureAwait(false); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw new TimeoutException($"Timed out after {timeout.TotalSeconds:0} s waiting for {what}."); }
        finally { lock (_lock) { _waiters.RemoveAll(w => ReferenceEquals(w.tcs, tcs)); _blobWaiters.RemoveAll(w => ReferenceEquals(w.tcs, tcs)); } }
    }

    static string X(string s) => System.Security.SecurityElement.Escape(s) ?? "";

    public Task SetNumberAsync(string device, string property, CancellationToken ct, params (string name, double value)[] values) =>
        SendAsync($"<newNumberVector device=\"{X(device)}\" name=\"{X(property)}\">" + string.Join("", values.Select(v => $"<oneNumber name=\"{X(v.name)}\">{v.value.ToString("R", CultureInfo.InvariantCulture)}</oneNumber>")) + "</newNumberVector>", ct);

    public Task SetSwitchAsync(string device, string property, CancellationToken ct, params (string name, bool on)[] values) =>
        SendAsync($"<newSwitchVector device=\"{X(device)}\" name=\"{X(property)}\">" + string.Join("", values.Select(v => $"<oneSwitch name=\"{X(v.name)}\">{(v.on ? "On" : "Off")}</oneSwitch>")) + "</newSwitchVector>", ct);

    /// <summary>Turn one switch of a one-of-many property on.</summary>
    public Task SelectAsync(string device, string property, string element, CancellationToken ct) => SetSwitchAsync(device, property, ct, (element, true));

    public Task SetTextAsync(string device, string property, CancellationToken ct, params (string name, string value)[] values) =>
        SendAsync($"<newTextVector device=\"{X(device)}\" name=\"{X(property)}\">" + string.Join("", values.Select(v => $"<oneText name=\"{X(v.name)}\">{X(v.value)}</oneText>")) + "</newTextVector>", ct);

    /// <summary>Ask the server to send this device's BLOBs (images) to us: mode Also, Only or Never.</summary>
    public Task EnableBlobAsync(string device, string mode, CancellationToken ct) => SendAsync($"<enableBLOB device=\"{X(device)}\">{mode}</enableBLOB>", ct);

    public async Task SendAsync(string xml, CancellationToken ct) {
        if (_stream is null) throw new InvalidOperationException("Not connected.");
        var bytes = Encoding.UTF8.GetBytes(xml + "\n");
        await _send.WaitAsync(ct).ConfigureAwait(false);
        try { await _stream.WriteAsync(bytes, ct).ConfigureAwait(false); await _stream.FlushAsync(ct).ConfigureAwait(false); }
        finally { _send.Release(); }
    }

    public async ValueTask DisposeAsync() {
        _cts.Cancel();
        try { _stream?.Dispose(); } catch { }
        try { _tcp?.Dispose(); } catch { }
        if (_reader is not null) { try { await Task.WhenAny(_reader, Task.Delay(1000)).ConfigureAwait(false); } catch { } }
        _cts.Dispose();
    }
}
