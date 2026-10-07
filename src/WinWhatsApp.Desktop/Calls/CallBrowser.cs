using System.Diagnostics;
using WinWhatsApp.Core;

namespace WinWhatsApp.App.Calls;

/// <summary>
/// A browser of the system that runs the calling engine's page without a
/// window. The engine needs what current browsers have and the web views of
/// macOS and Linux lack, WebTransport above all, so the app borrows a browser
/// that is installed: Chrome, Chromium, Edge, Brave or Vivaldi, or Firefox.
/// </summary>
/// <remarks>
/// The browser gets a profile of its own, made for each run and deleted after
/// it, so it shares nothing with the browser the person uses: no history, no
/// cookies, no extensions. WINWHATSAPP_BROWSER names another program to use.
/// </remarks>
internal sealed class CallBrowser
{
    private const string ProfilePrefix = "winwhatsapp-calls-";

    private static readonly string[] s_chromiumNames =
    [
        "google-chrome-stable", "google-chrome", "chromium", "chromium-browser", "microsoft-edge-stable", "microsoft-edge",
        "brave-browser", "brave", "vivaldi-stable", "vivaldi",
    ];

    private static readonly string[] s_firefoxNames = ["firefox", "firefox-esr"];

    private static readonly (string App, string Program)[] s_macChromium =
    [
        ("Google Chrome", "Google Chrome"), ("Microsoft Edge", "Microsoft Edge"), ("Brave Browser", "Brave Browser"),
        ("Chromium", "Chromium"), ("Vivaldi", "Vivaldi"),
    ];

    private readonly string _program;
    private readonly bool _firefox;
    private readonly string _profiles;

    private CallBrowser(string program, bool firefox)
    {
        _program = program;
        _firefox = firefox;
        _profiles = ProfilesFolder(program);
    }

    /// <summary>What the log calls it.</summary>
    public string Name => _program;

    /// <summary>The browser to use, or null when none of those the engine runs in is installed.</summary>
    public static CallBrowser? Find()
    {
        if (Environment.GetEnvironmentVariable("WINWHATSAPP_BROWSER") is { Length: > 0 } chosen && File.Exists(chosen))
        {
            return new CallBrowser(chosen, Path.GetFileName(chosen).Contains("firefox", StringComparison.OrdinalIgnoreCase));
        }
        if (OperatingSystem.IsMacOS())
        {
            string[] folders = ["/Applications", Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Applications")];
            foreach ((string app, string program) in s_macChromium)
            {
                foreach (string folder in folders)
                {
                    string path = Path.Combine(folder, app + ".app", "Contents", "MacOS", program);
                    if (File.Exists(path))
                    {
                        return new CallBrowser(path, firefox: false);
                    }
                }
            }
            foreach (string folder in folders)
            {
                string path = Path.Combine(folder, "Firefox.app", "Contents", "MacOS", "firefox");
                if (File.Exists(path))
                {
                    return new CallBrowser(path, firefox: true);
                }
            }
            return null;
        }
        if (s_chromiumNames.Select(OnPath).FirstOrDefault(p => p is not null) is { } chromium)
        {
            return new CallBrowser(chromium, firefox: false);
        }
        if (s_firefoxNames.Select(OnPath).FirstOrDefault(p => p is not null) is { } firefox)
        {
            return new CallBrowser(firefox, firefox: true);
        }
        return null;
    }

    private static string? OnPath(string name)
    {
        foreach (string folder in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            string path = Path.Combine(folder, name);
            if (File.Exists(path))
            {
                return path;
            }
        }
        return null;
    }

    /// <summary>
    /// Where the browser's profiles go: the app's data folder, or for a
    /// browser installed as a snap the one folder of the person's that the
    /// snap may write to outside its own.
    /// </summary>
    private static string ProfilesFolder(string program)
    {
        if (OperatingSystem.IsLinux() && SnapName(program) is { } snap)
        {
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "snap", snap, "common");
        }
        return Path.Combine(AppPaths.DataFolder, "Calls");
    }

    /// <summary>The snap a program belongs to, or null. Ubuntu installs Chromium and Firefox as snaps, behind a script of the usual name.</summary>
    private static string? SnapName(string program)
    {
        try
        {
            string name = Path.GetFileName(program) is "chromium-browser" ? "chromium" : Path.GetFileName(program);
            if (!Directory.Exists("/snap/" + name))
            {
                return null;
            }
            if (program.StartsWith("/snap/", StringComparison.Ordinal))
            {
                return name;
            }
            var info = new FileInfo(program);
            if (info.LinkTarget is { } target && target.Contains("snap", StringComparison.Ordinal))
            {
                return name;
            }
            // The script that stands in for the program.
            return info.Length < 64 * 1024 && File.ReadAllText(program).Contains("/snap/", StringComparison.Ordinal) ? name : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Starts the browser on a page, without a window, and returns it with the profile it uses.</summary>
    public (Process Process, string Profile) Start(string url)
    {
        Directory.CreateDirectory(_profiles);
        // What an earlier run left behind when it could not clean up.
        foreach (string old in Directory.GetDirectories(_profiles, ProfilePrefix + "*"))
        {
            Remove(old);
        }
        string profile = Path.Combine(_profiles, ProfilePrefix + Environment.ProcessId);
        Directory.CreateDirectory(profile);

        bool debug = Environment.GetEnvironmentVariable("WINWHATSAPP_DEBUG") == "1";
        var start = new ProcessStartInfo("/bin/sh") { UseShellExecute = false, RedirectStandardInput = true };
        start.ArgumentList.Add("-c");
        if (_firefox)
        {
            File.WriteAllText(Path.Combine(profile, "user.js"), FirefoxSettings);
            start.ArgumentList.Add("exec \"$0\" \"$@\"" + (debug ? "" : " > /dev/null 2>&1"));
            start.ArgumentList.Add(_program);
            foreach (string argument in (string[])["--headless", "--no-remote", "--new-instance", "--profile", profile, url])
            {
                start.ArgumentList.Add(argument);
            }
        }
        else
        {
            // The browser is told to take commands from the app over a pipe, which
            // the app never sends: the pipe is there because the browser quits
            // when it closes, and it closes when the app is gone, however it went.
            start.ArgumentList.Add("exec \"$0\" \"$@\" 3<&0 4> /dev/null" + (debug ? "" : " > /dev/null 2>&1"));
            start.ArgumentList.Add(_program);
            foreach (string argument in ChromiumArguments(profile, url))
            {
                start.ArgumentList.Add(argument);
            }
        }
        Process process = Process.Start(start) ?? throw new InvalidOperationException("The browser did not start.");
        return (process, profile);
    }

    private static IEnumerable<string> ChromiumArguments(string profile, string url) =>
    [
        "--headless=new",
        "--remote-debugging-pipe",
        "--user-data-dir=" + profile,
        "--no-first-run",
        "--no-default-browser-check",
        "--disable-extensions",
        "--disable-sync",
        "--disable-component-update",
        "--disable-background-networking",
        "--disable-background-mode",
        // The page is never visible, so the browser must not slow it down as
        // it does background tabs, and it plays sound without a click.
        "--disable-background-timer-throttling",
        "--disable-renderer-backgrounding",
        "--disable-backgrounding-occluded-windows",
        "--autoplay-policy=no-user-gesture-required",
        // The microphone without asking: there is no window to ask in.
        "--use-fake-ui-for-media-stream",
        // No questions about the system's password store for a profile that holds nothing.
        OperatingSystem.IsMacOS() ? "--use-mock-keychain" : "--password-store=basic",
        url,
    ];

    // The same for Firefox, which takes its settings from a file in the profile.
    private const string FirefoxSettings = """
        user_pref("media.navigator.permission.disabled", true);
        user_pref("media.autoplay.default", 0);
        user_pref("media.autoplay.blocking_policy", 0);
        user_pref("media.setsinkid.enabled", true);
        user_pref("dom.allow_scripts_to_close_windows", true);
        user_pref("dom.timeout.enable_budget_timer_throttling", false);
        user_pref("browser.shell.checkDefaultBrowser", false);
        user_pref("browser.aboutwelcome.enabled", false);
        user_pref("browser.tabs.warnOnClose", false);
        user_pref("app.update.auto", false);
        user_pref("datareporting.policy.dataSubmissionEnabled", false);
        user_pref("datareporting.healthreport.uploadEnabled", false);
        user_pref("toolkit.telemetry.reportingpolicy.firstRun", false);

        """;

    /// <summary>Stops a browser this started and deletes its profile. Does not wait for either.</summary>
    public static void Stop(Process process, string profile) => _ = Task.Run(async () =>
    {
        try
        {
            // Closing the pipe asks the browser to quit; it gets a moment for that.
            process.StandardInput.Close();
            using var patience = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            try
            {
                await process.WaitForExitAsync(patience.Token);
            }
            catch (OperationCanceledException)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync();
            }
        }
        catch (Exception e) when (e is InvalidOperationException or IOException or System.ComponentModel.Win32Exception)
        {
            Log.Error("Could not stop the calling engine's browser", e);
        }
        process.Dispose();
        Remove(profile);
    });

    private static void Remove(string profile)
    {
        try
        {
            Directory.Delete(profile, recursive: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // In use by a browser that is still closing; the next run removes it.
        }
    }
}
