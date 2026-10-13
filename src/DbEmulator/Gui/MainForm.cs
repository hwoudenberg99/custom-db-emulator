using System.Runtime.InteropServices;
using System.Text.Json;
using DbEmulator.App;
using DbEmulator.Hosting;
using DbEmulator.Storage;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace DbEmulator.Gui;

/// <summary>
/// The desktop window: hosts the app's UI in WebView2 and lives on in the system tray when closed, because the
/// local Cosmos endpoint must keep running while you develop.
/// </summary>
internal sealed class MainForm : Form, IAppWindow
{
    private const string AppOrigin = "https://app.dbemulator";

    private readonly AppController _app;
    private readonly WebView2 _web = new() { Dock = DockStyle.Fill, DefaultBackgroundColor = Color.FromArgb(246, 247, 249) };
    private readonly NotifyIcon _tray = new();
    private readonly System.Windows.Forms.Timer _titleTimer = new() { Interval = 30_000 };
    private bool _quitting;
    private bool _trayHintShown;

    public MainForm(AppController app)
    {
        _app = app;
        _app.Window = this;
        Text = "DbEmulator";
        Icon = LoadIcon();
        MinimumSize = new Size(960, 640);
        Size = new Size(1280, 840);
        StartPosition = FormStartPosition.CenterScreen;
        Controls.Add(_web);

        _tray.Icon = Icon;
        _tray.Text = "DbEmulator";
        _tray.Visible = true;
        _tray.DoubleClick += (_, _) => ShowFromTray();
        var menu = new ContextMenuStrip();
        menu.Items.Add("Open DbEmulator", null, (_, _) => ShowFromTray());
        menu.Items.Add("Fetch a fresh copy", null, (_, _) =>
        {
            if (_app.Runtime.ResolveSource(out _) is null)
            {
                ShowFromTray();
            }
            else
            {
                _app.StartFetch();
            }
        });
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Quit", null, (_, _) => Quit());
        _tray.ContextMenuStrip = menu;

        _titleTimer.Tick += (_, _) => UpdateTitle();
        _titleTimer.Start();
        _app.Changed += StatusChanged;
    }

    public void StatusChanged()
    {
        if (IsHandleCreated && !IsDisposed)
        {
            BeginInvoke(UpdateTitle);
        }
    }

    public void Quit()
    {
        if (InvokeRequired)
        {
            BeginInvoke(Quit);
            return;
        }

        _quitting = true;
        Close();
    }

    /// <summary>Brings the window back (from the tray, or when the exe is started a second time).</summary>
    public void ShowFromTray()
    {
        if (InvokeRequired)
        {
            BeginInvoke(ShowFromTray);
            return;
        }

        Show();
        if (WindowState == FormWindowState.Minimized)
        {
            WindowState = FormWindowState.Normal;
        }

        Activate();
    }

    protected override async void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        UpdateTitle();
        if (!await InitializeWebViewAsync())
        {
            return;
        }

        // Loading a large copy is CPU-bound; keep the window responsive while it happens.
        await Task.Run(_app.StartAsync);
        UpdateTitle();
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (!_quitting && e.CloseReason == CloseReason.UserClosing && _app.Options.App.CloseToTray)
        {
            e.Cancel = true;
            Hide();
            if (!_trayHintShown)
            {
                _trayHintShown = true;
                _tray.ShowBalloonTip(4000, "DbEmulator is still running",
                    "Your app can keep using it. Open or quit it from the icon in the system tray.", ToolTipIcon.Info);
            }

            return;
        }

        base.OnFormClosing(e);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _app.Changed -= StatusChanged;
            _titleTimer.Dispose();
            _tray.Visible = false;
            _tray.Dispose();
            _web.Dispose();
        }

        base.Dispose(disposing);
    }

    private async Task<bool> InitializeWebViewAsync()
    {
        try
        {
            var userData = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DbEmulator", "WebView2");
            var environment = await CoreWebView2Environment.CreateAsync(null, userData);
            await _web.EnsureCoreWebView2Async(environment);
        }
        catch (Exception e) when (e is WebView2RuntimeNotFoundException or COMException or InvalidOperationException)
        {
            var open = MessageBox.Show(this,
                "DbEmulator needs the Microsoft Edge WebView2 Runtime, which is part of Windows 11 but appears to be missing.\n\n" +
                "Open the download page now? (You can also start DbEmulator with --console.)",
                "DbEmulator", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
            if (open == DialogResult.Yes)
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("https://go.microsoft.com/fwlink/p/?LinkId=2124703") { UseShellExecute = true });
            }

            _quitting = true;
            Close();
            return false;
        }

        var core = _web.CoreWebView2;
        core.Settings.AreDevToolsEnabled = System.Diagnostics.Debugger.IsAttached;
        core.Settings.IsStatusBarEnabled = false;
        core.Settings.IsZoomControlEnabled = true;
        core.Settings.AreBrowserAcceleratorKeysEnabled = true;
        core.AddWebResourceRequestedFilter($"{AppOrigin}/*", CoreWebView2WebResourceContext.All);
        core.WebResourceRequested += (_, args) =>
        {
            var path = new Uri(args.Request.Uri).AbsolutePath;
            args.Response = UiAssets.TryGet(path, out var content, out var type)
                ? core.Environment.CreateWebResourceResponse(new MemoryStream(content), 200, "OK", $"Content-Type: {type}")
                : core.Environment.CreateWebResourceResponse(null, 404, "Not Found", string.Empty);
        };
        core.NewWindowRequested += (_, args) =>
        {
            // Links open in the normal browser.
            args.Handled = true;
            if (Uri.TryCreate(args.Uri, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps)
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
            }
        };
        core.NavigationStarting += (_, args) =>
        {
            if (!args.Uri.StartsWith(AppOrigin, StringComparison.OrdinalIgnoreCase) && !args.Uri.StartsWith("about:", StringComparison.Ordinal))
            {
                args.Cancel = true;
            }
        };
        core.WebMessageReceived += OnWebMessage;
        core.Navigate($"{AppOrigin}/index.html");
        return true;
    }

    private async void OnWebMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        long id = 0;
        string reply;
        try
        {
            using var message = JsonDocument.Parse(e.WebMessageAsJson);
            var root = message.RootElement;
            id = root.TryGetProperty("id", out var idElement) && idElement.TryGetInt64(out var n) ? n : 0;
            var method = root.GetString("method") ?? string.Empty;
            var args = root.TryGetProperty("params", out var p) ? p.Clone() : default;
            var result = await Task.Run(() => _app.Bridge.InvokeAsync(method, args, fromDesktop: true));
            reply = JsonSerializer.Serialize(new { id, result }, Json.SerializerOptions);
        }
        catch (Exception ex) when (ex is BridgeException or InvalidOperationException or ArgumentException or IOException or JsonException)
        {
            reply = JsonSerializer.Serialize(new { id, error = ex.Message }, Json.SerializerOptions);
        }
        catch (Exception ex)
        {
            _app.Log.Error($"Unexpected error: {ex}");
            reply = JsonSerializer.Serialize(new { id, error = "Something went wrong: " + ex.Message }, Json.SerializerOptions);
        }

        if (!IsDisposed && _web.CoreWebView2 is not null)
        {
            _web.CoreWebView2.PostWebMessageAsJson(reply);
        }
    }

    private void UpdateTitle()
    {
        var runtime = _app.Runtime;
        string status;
        if (_app.Phase == "starting")
        {
            status = "starting...";
        }
        else if (runtime.IsFetching)
        {
            status = "fetching...";
        }
        else
        {
            var last = runtime.LastFetch;
            status = last is null ? "nothing fetched yet" : $"last fetch {Ago(last.Manifest.CompletedUtc)}";
        }

        Text = $"DbEmulator — {status}";
        var tip = $"DbEmulator — {status}";
        _tray.Text = tip.Length > 127 ? tip[..127] : tip;
    }

    private static string Ago(DateTimeOffset utc)
    {
        var age = DateTimeOffset.UtcNow - utc;
        return age.TotalSeconds switch
        {
            < 90 => "just now",
            < 5400 => $"{age.TotalMinutes:0} minutes ago",
            < 129600 => $"{age.TotalHours:0} hours ago",
            _ => $"{age.TotalDays:0} days ago",
        };
    }

    private static Icon LoadIcon()
    {
        using var stream = typeof(MainForm).Assembly.GetManifestResourceStream("app.ico");
        return stream is null ? SystemIcons.Application : new Icon(stream);
    }
}
