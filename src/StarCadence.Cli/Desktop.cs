// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at http://mozilla.org/MPL/2.0/.

using System.Diagnostics;
using System.Runtime.InteropServices;

/// <summary>
/// What a double-clicked program needs on each desktop: a console window on Windows that does not freeze when
/// clicked and says what it is, and a startup error the person can actually read (the Windows window would
/// otherwise close at once; a Mac app started from Finder has no window at all).
/// </summary>
static class Desktop {
    [DllImport("kernel32.dll")] static extern IntPtr GetStdHandle(int handle);
    [DllImport("kernel32.dll")] static extern bool GetConsoleMode(IntPtr handle, out uint mode);
    [DllImport("kernel32.dll")] static extern bool SetConsoleMode(IntPtr handle, uint mode);
    [DllImport("kernel32.dll")] static extern uint GetConsoleProcessList(uint[] processes, uint count);
    const int StdInput = -10;
    const uint QuickEditMode = 0x0040, ExtendedFlags = 0x0080;

    /// <summary>Windows: name the window and turn off Quick Edit, so a stray click in the window cannot pause the program.</summary>
    public static void Prepare() {
        if (!OperatingSystem.IsWindows()) return;
        Title("StarCadence");
        try {
            var input = GetStdHandle(StdInput);
            if (GetConsoleMode(input, out var mode)) SetConsoleMode(input, (mode & ~QuickEditMode) | ExtendedFlags);
        } catch (Exception) { }
    }

    public static void Title(string title) {
        if (!OperatingSystem.IsWindows()) return;
        try { Console.Title = title; } catch (Exception) { }
    }

    /// <summary>True on Windows when this program has a console window of its own (double-clicked), which closes when it ends.</summary>
    static bool OwnsConsoleWindow() {
        if (!OperatingSystem.IsWindows()) return false;
        try { return GetConsoleProcessList(new uint[4], 4) == 1; } catch (Exception) { return false; }
    }

    /// <summary>Report a failure to start so the person sees it, then return the exit code.</summary>
    public static int StartupFailed(Exception e, string? dataDir) {
        var message = Explain(e, dataDir);
        var log = WriteLog(e, dataDir);
        Console.Error.WriteLine();
        Console.Error.WriteLine("StarCadence could not start.");
        Console.Error.WriteLine(message);
        if (log is not null) Console.Error.WriteLine($"Details: {log}");
        if (OwnsConsoleWindow()) {
            Console.Error.WriteLine();
            Console.Error.WriteLine("Press Enter to close this window.");
            try { Console.ReadLine(); } catch (Exception) { }
        } else if (OperatingSystem.IsMacOS() && Console.IsOutputRedirected) {
            Alert("StarCadence could not start", message + (log is null ? "" : "\n\nDetails: " + log));
        }
        return 1;
    }

    static string Explain(Exception e, string? dataDir) => e switch {
        UnauthorizedAccessException when OperatingSystem.IsWindows() =>
            $"Windows would not let StarCadence write to {dataDir ?? "its settings folder"}. If \"Controlled folder access\" is on in Windows Security, " +
            "allow StarCadence.exe there (Virus & threat protection > Ransomware protection > Allow an app through Controlled folder access), " +
            "or start StarCadence with --data and another folder.",
        UnauthorizedAccessException when OperatingSystem.IsMacOS() =>
            $"StarCadence is not allowed to write to {dataDir ?? "its settings folder"}. If macOS asked about the Documents folder, allow it " +
            "(System Settings > Privacy & Security > Files and Folders), or start StarCadence with --data and another folder.",
        UnauthorizedAccessException =>
            $"StarCadence is not allowed to write to {dataDir ?? "its settings folder"}. Start it with --data and a folder you can write to.",
        System.Net.Sockets.SocketException or IOException when e.Message.Contains("address", StringComparison.OrdinalIgnoreCase) =>
            "Another program is using the network port StarCadence wanted. Close other copies of StarCadence and try again. (" + e.Message + ")",
        _ => e.Message,
    };

    static string? WriteLog(Exception e, string? dataDir) {
        foreach (var dir in new[] { dataDir, Path.GetTempPath() }) {
            if (string.IsNullOrEmpty(dir)) continue;
            try {
                Directory.CreateDirectory(dir);
                var path = Path.Combine(dir, "startup-error.txt");
                File.WriteAllText(path, $"{DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} UTC  StarCadence {AppServer.Version()} on {RuntimeInformation.OSDescription}\n{e}\n");
                return path;
            } catch (Exception) { }
        }
        return null;
    }

    static void Alert(string title, string message) {
        static string Quote(string s) => "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
        try {
            var psi = new ProcessStartInfo("/usr/bin/osascript") { UseShellExecute = false };
            psi.ArgumentList.Add("-e");
            psi.ArgumentList.Add($"display alert {Quote(title)} message {Quote(message)} as critical");
            Process.Start(psi)?.WaitForExit(120_000);
        } catch (Exception) { }
    }
}
