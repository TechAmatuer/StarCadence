// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at http://mozilla.org/MPL/2.0/.

using System.Text;

namespace StarCadence.Core.Util;

/// <summary>
/// Reading and writing the small settings and history files so that a moment's lock does not break a night.
/// On Windows another program often holds a file open for a fraction of a second (OneDrive syncing Documents,
/// antivirus scanning a file that was just written, the app window reading while N.I.N.A. writes), and a plain
/// write or rename then fails. These helpers share the file while reading and retry briefly while writing.
/// </summary>
public static class Files {
    static readonly Encoding Utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    /// <summary>How long to wait before the first retry; each further retry waits one step longer (8 tries, about 1.4 s in all).</summary>
    public static TimeSpan RetryStep { get; set; } = TimeSpan.FromMilliseconds(50);
    const int Attempts = 8;

    public static string ReadAllText(string path) => Retry(() => {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(fs, Utf8, detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    });

    /// <summary>Replace a whole file: write a temporary copy beside it, then swap it in, retrying while the file is in use.</summary>
    public static void WriteAllText(string path, string text) {
        var full = Path.GetFullPath(path);
        var dir = Path.GetDirectoryName(full);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        var tmp = $"{full}.{Environment.ProcessId}.tmp";
        Retry(() => { File.WriteAllText(tmp, text, Utf8); return 0; });
        try {
            Retry(() => { File.Move(tmp, full, overwrite: true); return 0; });
        } catch (Exception e) when (e is IOException or UnauthorizedAccessException) {
            // The file is held open in a way that blocks replacing it: write over it in place instead.
            try { Retry(() => { File.WriteAllText(full, text, Utf8); return 0; }); }
            finally { try { File.Delete(tmp); } catch (Exception) { } }
        }
    }

    static T Retry<T>(Func<T> op) {
        for (var attempt = 1; ; attempt++) {
            try { return op(); }
            catch (Exception e) when (attempt < Attempts && Transient(e)) {
                Thread.Sleep(RetryStep * attempt);
            }
        }
    }

    // A sharing violation is an IOException; a rename onto a file that is open (or being deleted) can also be "access denied".
    static bool Transient(Exception e) =>
        e is UnauthorizedAccessException || (e is IOException && e is not FileNotFoundException && e is not DirectoryNotFoundException && e is not PathTooLongException);
}
