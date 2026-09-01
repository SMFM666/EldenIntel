using System;
using System.Diagnostics;
using System.IO;
using System.Windows.Forms;

internal static class EldenIntelLauncher
{
    [STAThread]
    private static void Main()
    {
        string root = AppDomain.CurrentDomain.BaseDirectory;
        string executable = Path.Combine(
            root,
            "release",
            "EldenIntelV1-current-splitcam",
            "EldenIntel.exe");

        if (!File.Exists(executable))
        {
            MessageBox.Show(
                "The portable EldenIntel build could not be found:\n\n" + executable,
                "EldenIntel",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
            return;
        }

        Process.Start(new ProcessStartInfo
        {
            FileName = executable,
            WorkingDirectory = Path.GetDirectoryName(executable),
            UseShellExecute = true
        });
    }
}
