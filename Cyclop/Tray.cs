using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Reflection;
using System.Windows.Forms;
using Cyclop.Stores;
using Microsoft.Win32;

namespace Cyclop;

/// The notification-area icon: Windows' counterpart of the menu bar item.
/// A left click toggles the panel; the menu holds the rest.
sealed class Tray : IDisposable
{
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string RunValue = "Cyclop";

    readonly NotifyIcon icon;
    readonly Icon image;

    public static string Version
    {
        get
        {
            var version = Assembly.GetExecutingAssembly().GetName().Version;
            return version == null ? "?" : $"{version.Major}.{version.Minor}.{version.Build}";
        }
    }

    public Tray(Action toggle, Action quit, bool catchScreenshots, Action<bool> setCatchScreenshots)
    {
        image = DrawIcon();

        // An entry written before the flag existed would pop the panel open at
        // every sign-in; brought up to date once, the first time it is seen.
        if (LaunchesAtLogin && !AutostartIsQuiet) SetLaunchAtLogin(true);

        var launchAtLogin = new ToolStripMenuItem("Launch at login") { Checked = LaunchesAtLogin, CheckOnClick = true };
        launchAtLogin.CheckedChanged += (_, _) => SetLaunchAtLogin(launchAtLogin.Checked);

        var catching = new ToolStripMenuItem("Catch screenshots") { Checked = catchScreenshots, CheckOnClick = true };
        catching.CheckedChanged += (_, _) => setCatchScreenshots(catching.Checked);

        var menu = new ContextMenuStrip();
        menu.Items.Add(new ToolStripMenuItem($"Cyclop {Version}") { Enabled = false });
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Show panel", null, (_, _) => toggle());
        menu.Items.Add(catching);
        menu.Items.Add("Open screenshots folder", null, (_, _) =>
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{ScreenshotVault.Folder}\"") { UseShellExecute = true }));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(launchAtLogin);
        menu.Items.Add("Open data folder", null, (_, _) =>
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{Support.Folder}\"") { UseShellExecute = true }));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Quit Cyclop", null, (_, _) => quit());

        icon = new NotifyIcon
        {
            Icon = image,
            Text = "Cyclop",
            ContextMenuStrip = menu,
            Visible = true,
        };
        icon.MouseClick += (_, e) =>
        {
            if (e.Button == MouseButtons.Left) toggle();
        };
    }

    static bool LaunchesAtLogin
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(RunValue) is string;
        }
    }

    static bool AutostartIsQuiet
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(RunValue) is string command && command.Contains(App.BackgroundArgument);
        }
    }

    static void SetLaunchAtLogin(bool on)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey);
        if (on)
            key.SetValue(RunValue, $"\"{Environment.ProcessPath}\" {App.BackgroundArgument}");
        else
            key.DeleteValue(RunValue, throwOnMissingValue: false);
    }

    /// Drawn in code, like the Mac icon: an eye — a ring around a pupil.
    static Icon DrawIcon()
    {
        using var bitmap = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bitmap))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);
            using var fill = new SolidBrush(Color.FromArgb(20, 20, 22));
            g.FillEllipse(fill, 1, 1, 30, 30);
            using var ring = new Pen(Color.White, 3f);
            g.DrawEllipse(ring, 6, 6, 20, 20);
            using var pupil = new SolidBrush(Color.White);
            g.FillEllipse(pupil, 12, 12, 8, 8);
        }
        var handle = bitmap.GetHicon();
        // Icon.FromHandle does not own the handle; clone, then free it.
        using var borrowed = Icon.FromHandle(handle);
        var owned = (Icon)borrowed.Clone();
        Native.DestroyIcon(handle);
        return owned;
    }

    public void Dispose()
    {
        icon.Visible = false;
        icon.Dispose();
        image.Dispose();
    }
}
