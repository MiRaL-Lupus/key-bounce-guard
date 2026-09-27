using Microsoft.Win32;
using System.Reflection;
using System.Windows.Forms;

namespace KeyBounceGuardSetup;

internal static class Program
{
    private const string AppName = "Key Bounce Guard";
    private const string StartupValueName = "KeyBounceGuard";

    [STAThread]
    private static void Main()
    {
        ApplicationConfiguration.Initialize();

        MessageBox.Show(
            "Key Bounce Guard is a free community utility created by a keyboard user.\n\n" +
            "It blocks same-key false repeats under 49 ms and logs only blocked suspected bounce events. " +
            "It does not repair the keyboard and is not affiliated with a keyboard manufacturer.",
            AppName, MessageBoxButtons.OK, MessageBoxIcon.Information);

        using var folderDialog = new FolderBrowserDialog
        {
            Description = "Choose where to install Key Bounce Guard",
            InitialDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs")
        };
        if (folderDialog.ShowDialog() != DialogResult.OK)
            return;

        var installDirectory = Path.Combine(folderDialog.SelectedPath, AppName);
        Directory.CreateDirectory(installDirectory);
        var targetExe = Path.Combine(installDirectory, "KeyBounceGuard.exe");

        if (File.Exists(targetExe))
        {
            var overwrite = MessageBox.Show(
                "KeyBounceGuard.exe already exists in this folder. Replace it?",
                AppName, MessageBoxButtons.YesNo, MessageBoxIcon.Question);
            if (overwrite != DialogResult.Yes)
                return;
        }

        using (var resource = Assembly.GetExecutingAssembly().GetManifestResourceStream("KeyBounceGuard.exe")
            ?? throw new InvalidOperationException("Installer payload is missing."))
        using (var target = File.Create(targetExe))
            resource.CopyTo(target);

        var startAtSignIn = MessageBox.Show(
            "Start Key Bounce Guard automatically when you sign in to Windows?\n\n" +
            "A normal desktop app cannot filter the Windows sign-in password screen. This option starts it immediately after sign-in.",
            AppName, MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes;

        using (var runKey = Registry.CurrentUser.CreateSubKey("Software\\Microsoft\\Windows\\CurrentVersion\\Run", writable: true))
        {
            if (startAtSignIn)
                runKey?.SetValue(StartupValueName, $"\"{targetExe}\"");
            else
                runKey?.DeleteValue(StartupValueName, throwOnMissingValue: false);
        }

        var launch = MessageBox.Show("Installation complete. Start Key Bounce Guard now?", AppName, MessageBoxButtons.YesNo, MessageBoxIcon.Question);
        if (launch == DialogResult.Yes)
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo { FileName = targetExe, UseShellExecute = true });
    }
}
