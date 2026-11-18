using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace Scenoco
{
    // ------------------------------------------------------------------------------------------
    // Sync layer: a merge conflict written into a file, by the tool that defines the format —
    // `git merge-file`, a three-way merge of three texts (no repository involved):
    //
    //   <<<<<<< Level1 (your changes)
    //       <Rigidbody2D mass="3" />
    //   =======
    //       <Rigidbody2D mass="5" />
    //   >>>>>>> Level1.uscene
    //
    // Editors, IDEs and agents all know these markers. The texts Twin hands over differ only
    // where two changes clash, so every marker is a real conflict.
    // ------------------------------------------------------------------------------------------

    public static class ConflictMarkers
    {
        static readonly Regex Marker = new(@"^<<<<<<< ", RegexOptions.Multiline);

        /// <summary>Whether a text still has conflict markers in it.</summary>
        public static bool In(string text) => Marker.IsMatch(text);

        /// <summary>The line of the first marker (1 when there is none).</summary>
        public static int FirstLine(string text)
        {
            var m = Marker.Match(text);
            if (!m.Success) return 1;
            var line = 1;
            for (var i = 0; i < m.Index; i++) if (text[i] == '\n') line++;
            return line;
        }

        /// <summary>Merge two edits of <paramref name="original"/>; clashes come back marked, and counted in <paramref name="conflicts"/>.</summary>
        public static string Merge(string ours, string original, string theirs, string oursLabel, string theirsLabel, out int conflicts)
        {
            var dir = Path.Combine("Temp", "Scenoco");
            Directory.CreateDirectory(dir);
            string File_(string name, string text)
            {
                var path = Path.Combine(dir, name);
                File.WriteAllText(path, text);
                return path;
            }
            var info = new ProcessStartInfo("git")
            {
                UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
            };
            foreach (var arg in new[] { "merge-file", "-p", "-L", oursLabel, "-L", "before", "-L", theirsLabel, File_("ours", ours), File_("base", original), File_("theirs", theirs) })
                info.ArgumentList.Add(arg);
            try
            {
                using var git = Process.Start(info);
                var merged = git.StandardOutput.ReadToEnd();
                var error = git.StandardError.ReadToEnd();
                git.WaitForExit();
                if (git.ExitCode < 0 || git.ExitCode > 127) throw new InvalidOperationException($"git merge-file failed: {error.Trim()}");
                conflicts = git.ExitCode;
                return merged;
            }
            catch (System.ComponentModel.Win32Exception)
            {
                throw new InvalidOperationException("writing a conflict into the file needs git on the PATH; answer with \"keep\" or \"file\" instead");
            }
        }
    }
}
