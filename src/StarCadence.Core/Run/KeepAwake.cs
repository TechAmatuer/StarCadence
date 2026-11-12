// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at http://mozilla.org/MPL/2.0/.

using System.Diagnostics;
using System.Runtime.InteropServices;

namespace StarCadence.Core.Run;

/// <summary>
/// Keeps the computer from going to sleep while a night is being run; the screen may still switch off.
/// Windows: SetThreadExecutionState from a thread that lives as long as the run. macOS: caffeinate.
/// Linux: systemd-inhibit when it is available. Dispose to let the computer sleep again.
/// </summary>
public sealed class KeepAwake : IDisposable {
    readonly ManualResetEventSlim? _release;
    readonly Thread? _thread;
    readonly Process? _helper;
    int _disposed;

    /// <summary>True when the system accepted the request.</summary>
    public bool Active { get; }
    /// <summary>One line for the log saying what was done.</summary>
    public string How { get; }
    /// <summary>The helper process holding the request (macOS and Linux), for tests.</summary>
    public Process? Helper => _helper;

    KeepAwake(bool active, string how, ManualResetEventSlim? release = null, Thread? thread = null, Process? helper = null) {
        Active = active; How = how; _release = release; _thread = thread; _helper = helper;
    }

    public static KeepAwake Start(string why) {
        try {
            if (OperatingSystem.IsWindows()) return StartWindows();
            if (OperatingSystem.IsMacOS()) return StartHelper("/usr/bin/caffeinate", new[] { "-i", "-s", "-w", Environment.ProcessId.ToString() }, "macOS (caffeinate)");
            if (OperatingSystem.IsLinux()) {
                var inhibit = new[] { "/usr/bin/systemd-inhibit", "/bin/systemd-inhibit" }.FirstOrDefault(File.Exists);
                if (inhibit is null) return new KeepAwake(false, "This computer may sleep during the run (no systemd-inhibit); turn sleep off in its power settings.");
                return StartHelper(inhibit, new[] { "--what=idle:sleep", "--who=StarCadence", "--why=" + why, "--mode=block",
                    "tail", "--pid=" + Environment.ProcessId, "-f", "/dev/null" }, "Linux (systemd-inhibit)");
            }
        } catch (Exception e) {
            return new KeepAwake(false, "Could not stop this computer from sleeping: " + e.Message);
        }
        return new KeepAwake(false, "This computer may sleep during the run; turn sleep off in its power settings.");
    }

    // ---- Windows ----

    [DllImport("kernel32.dll")]
    static extern uint SetThreadExecutionState(uint flags);
    const uint EsContinuous = 0x80000000, EsSystemRequired = 0x00000001;

    static KeepAwake StartWindows() {
        // The request belongs to the thread that makes it, so a dedicated thread holds it for the whole run.
        var release = new ManualResetEventSlim();
        var ready = new ManualResetEventSlim();
        var accepted = false;
        var thread = new Thread(() => {
            SetThreadExecutionState(EsContinuous | EsSystemRequired);
            // The call returns the state it replaces, so asking again shows whether the first request took hold.
            accepted = (SetThreadExecutionState(EsContinuous | EsSystemRequired) & EsSystemRequired) != 0;
            ready.Set();
            release.Wait();
            SetThreadExecutionState(EsContinuous);
        }) { IsBackground = true, Name = "StarCadence keep awake" };
        thread.Start();
        ready.Wait(TimeSpan.FromSeconds(5));
        return accepted
            ? new KeepAwake(true, "Windows will not go to sleep while the night runs (the screen may still turn off).", release, thread)
            : new KeepAwake(false, "Windows did not accept the request to stay awake; turn sleep off in its power settings.", release, thread);
    }

    // ---- macOS and Linux: a helper process that holds the request and ends with us ----

    static KeepAwake StartHelper(string exe, string[] args, string label) {
        var psi = new ProcessStartInfo(exe) { UseShellExecute = false, RedirectStandardError = true, CreateNoWindow = true };
        foreach (var a in args) psi.ArgumentList.Add(a);
        var p = Process.Start(psi);
        if (p is null) return new KeepAwake(false, $"Could not start {exe}; this computer may sleep during the run.");
        // A helper that cannot take the lock (no logind in a container, for example) exits at once.
        if (p.WaitForExit(500)) {
            var err = p.StandardError.ReadToEnd().Trim();
            return new KeepAwake(false, $"This computer may sleep during the run ({label}: {(err.Length > 0 ? err : "exit " + p.ExitCode)}).");
        }
        return new KeepAwake(true, $"This computer will not go to sleep while the night runs ({label}; the screen may still turn off).", helper: p);
    }

    public void Dispose() {
        if (Interlocked.Exchange(ref _disposed, 1) == 1) return;
        if (_release is not null) { _release.Set(); _thread?.Join(TimeSpan.FromSeconds(2)); }
        if (_helper is not null) {
            try { if (!_helper.HasExited) { _helper.Kill(entireProcessTree: true); _helper.WaitForExit(2000); } } catch (Exception) { }
            _helper.Dispose();
        }
    }
}
