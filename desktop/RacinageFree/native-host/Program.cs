using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Web.Script.Serialization;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Win32;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace RacinageFreeDesktop {
  internal static class Program {
    [DllImport("user32.dll")]
    private static extern bool SetProcessDPIAware();

    [DllImport("user32.dll")]
    private static extern bool SetProcessDpiAwarenessContext(IntPtr dpiContext);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetDllDirectory(string lpPathName);

    [STAThread]
    private static void Main(string[] args) {
      SetDllDirectory(AppDomain.CurrentDomain.BaseDirectory);
      PortablePaths.EnsureMutableFolders();
      PayloadSamples.Ensure();
      EnableHighDpiRendering();
      Application.EnableVisualStyles();
      Application.SetCompatibleTextRenderingDefault(false);

      LocalStore store = new LocalStore();
      LocalServer server = null;
      try {
        store.Initialize();
        store.ImportCommandLineShare(args);
        server = new LocalServer(store);
        server.Start();
        Application.Run(new RacinageWindow(server, store));
      } catch (Exception error) {
        Log("Fatal startup error: " + error);
        MessageBox.Show(
          "Racinage Free could not start.\r\n\r\n" + error.Message,
          "Racinage Free",
          MessageBoxButtons.OK,
          MessageBoxIcon.Error);
      } finally {
        if (server != null) server.Stop();
      }
    }

    internal static void Log(string message) {
      try {
        Directory.CreateDirectory(PortablePaths.LogsDir);
        File.AppendAllText(
          Path.Combine(PortablePaths.LogsDir, "racinage-free.log"),
          DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture) + " " + message + Environment.NewLine,
          Encoding.UTF8);
      } catch {
      }
    }

    private static void EnableHighDpiRendering() {
      try {
        if (SetProcessDpiAwarenessContext(new IntPtr(-4))) return;
      } catch {
      }
      try {
        SetProcessDPIAware();
      } catch {
      }
    }
  }

  internal static class PortablePaths {
    internal const string Version = "0.17.0";
    internal const string AppName = "Racinage Free";
    internal const string PricingUrl = "https://racinage.com/pricing";
    internal const string PluginCatalogUrl = "https://plugins.racinage.com/api/catalog";

    internal static readonly string Root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), AppName);
    internal static readonly string DataDir = Path.Combine(Root, "data");
    internal static readonly string MediaDir = Path.Combine(Root, "media");
    internal static readonly string LogsDir = Path.Combine(Root, "logs");
    internal static readonly string UpdatesDir = Path.Combine(Root, "updates");
    internal static readonly string TokensDir = Path.Combine(Root, "device-tokens");
    internal static readonly string WebViewDir = Path.Combine(Root, "webview");
    internal static readonly string PluginsDir = Path.Combine(Root, "plugins");
    internal static readonly string PluginCacheDir = Path.Combine(Root, "plugin-cache");
    internal static readonly string ShareInboxDir = Path.Combine(DataDir, "share-inbox");

    internal static void EnsureMutableFolders() {
      foreach (string path in new[] { Root, DataDir, MediaDir, LogsDir, UpdatesDir, TokensDir, WebViewDir, PluginsDir, PluginCacheDir, ShareInboxDir }) {
        Directory.CreateDirectory(path);
      }
    }
  }

  internal static class PayloadSamples {
    internal static void Ensure() {
      string payloadDir = AppDomain.CurrentDomain.BaseDirectory;
      WriteIfMissing(Path.Combine(payloadDir, "config.sample.json"),
        "{\r\n" +
        "  \"app\": \"Racinage Free\",\r\n" +
        "  \"mode\": \"local-lite-free\",\r\n" +
        "  \"server\": \"https://racinage.com\",\r\n" +
        "  \"database\": \"%LOCALAPPDATA%\\\\Racinage Free\\\\data\\\\racinage-free.sqlite\",\r\n" +
        "  \"media\": \"%LOCALAPPDATA%\\\\Racinage Free\\\\media\"\r\n" +
        "}\r\n");
    }

    private static void WriteIfMissing(string path, string contents) {
      try {
        if (!File.Exists(path)) File.WriteAllText(path, contents, Encoding.UTF8);
      } catch {
      }
    }
  }

  internal sealed class RacinageWindow : Form {
    [DllImport("user32.dll")] private static extern bool ReleaseCapture();
    [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr hWnd, int msg, int wParam, int lParam);
    private const int WmNcHitTest = 0x0084;
    private const int HtClient = 1;
    private const int HtLeft = 10;
    private const int HtRight = 11;
    private const int HtTop = 12;
    private const int HtTopLeft = 13;
    private const int HtTopRight = 14;
    private const int HtBottom = 15;
    private const int HtBottomLeft = 16;
    private const int HtBottomRight = 17;
    private const int ResizeBorder = 7;

    private readonly LocalServer server;
    private readonly LocalStore store;
    private readonly WebView2 browser = new WebView2();
    private readonly StatusDotControl statusDot = new StatusDotControl();
    private readonly System.Windows.Forms.Timer statusTimer = new System.Windows.Forms.Timer();
    private readonly NotifyIcon calendarReminderIcon = new NotifyIcon();
    private readonly FileSystemWatcher shareInboxWatcher = new FileSystemWatcher();
    private Label statusText;
    private string lastError = "";
    private DateTime nextCalendarReminderCheckUtc = DateTime.MinValue;

    internal RacinageWindow(LocalServer server, LocalStore store) {
      this.server = server;
      this.store = store;
      Text = PortablePaths.AppName;
      FormBorderStyle = FormBorderStyle.None;
      StartPosition = FormStartPosition.CenterScreen;
      MinimumSize = new Size(980, 620);
      Size = new Size(1180, 760);
      BackColor = Color.White;
      BuildChrome();
      browser.Dock = DockStyle.Fill;
      Controls.Add(browser);
      browser.BringToFront();
      calendarReminderIcon.Icon = SystemIcons.Information;
      calendarReminderIcon.Text = "Racinage Free Calendar";
      calendarReminderIcon.Visible = true;
      calendarReminderIcon.BalloonTipClicked += delegate {
        if (browser.CoreWebView2 != null) browser.CoreWebView2.Navigate(server.BaseUrl + "/calendar");
      };
      shareInboxWatcher.Path = PortablePaths.ShareInboxDir;
      shareInboxWatcher.Filter = "*.json";
      shareInboxWatcher.NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite;
      shareInboxWatcher.Created += ShareInboxChanged;
      shareInboxWatcher.Renamed += ShareInboxChanged;
      shareInboxWatcher.EnableRaisingEvents = true;
      FormClosing += delegate {
        statusTimer.Stop();
        calendarReminderIcon.Visible = false;
        calendarReminderIcon.Dispose();
        shareInboxWatcher.EnableRaisingEvents = false;
        shareInboxWatcher.Dispose();
      };
      Shown += async delegate { await StartBrowser(); };
      statusTimer.Interval = 4000;
      statusTimer.Tick += delegate { RefreshStatus(); };
      statusTimer.Start();
    }

    private void BuildChrome() {
      Panel titleBar = new Panel {
        Dock = DockStyle.Top,
        Height = 42,
        BackColor = Color.FromArgb(6, 38, 43),
        Padding = new Padding(12, 0, 10, 0)
      };
      titleBar.MouseDown += BeginWindowDrag;
      titleBar.MouseDoubleClick += delegate { ToggleMaximize(); };

      Label title = new Label {
        Text = "Racinage Free",
        Dock = DockStyle.Left,
        Width = 180,
        TextAlign = ContentAlignment.MiddleLeft,
        ForeColor = Color.White,
        Font = new Font("Segoe UI", 10.5f, FontStyle.Bold)
      };
      title.MouseDown += BeginWindowDrag;
      title.MouseDoubleClick += delegate { ToggleMaximize(); };
      titleBar.Controls.Add(title);

      FlowLayoutPanel actions = new FlowLayoutPanel {
        Dock = DockStyle.Right,
        FlowDirection = FlowDirection.RightToLeft,
        WrapContents = false,
        AutoSize = true,
        Height = 42,
        Padding = new Padding(0, 6, 0, 0)
      };

      Button close = TitleButton("x");
      close.Click += delegate { Close(); };
      Button maximize = TitleButton("□");
      maximize.Click += delegate { ToggleMaximize(); };
      Button minimize = TitleButton("-");
      minimize.Click += delegate { WindowState = FormWindowState.Minimized; };
      Button upgrade = UpgradeButton();
      upgrade.Click += delegate { OpenExternal(PortablePaths.PricingUrl); };

      statusDot.StatusColor = Color.FromArgb(212, 121, 47);
      statusDot.Margin = new Padding(8, 5, 8, 0);
      statusDot.Click += delegate { ShowSyncDetails(); };
      statusText = new Label {
        Text = "Starting",
        AutoSize = false,
        Width = 70,
        Height = 28,
        Margin = new Padding(0, 1, 3, 0),
        TextAlign = ContentAlignment.MiddleLeft,
        ForeColor = Color.FromArgb(198, 212, 208),
        Font = new Font("Segoe UI", 8.5f, FontStyle.Regular)
      };

      actions.Controls.Add(close);
      actions.Controls.Add(maximize);
      actions.Controls.Add(minimize);
      actions.Controls.Add(statusDot);
      actions.Controls.Add(statusText);
      actions.Controls.Add(upgrade);
      titleBar.Controls.Add(actions);
      Controls.Add(titleBar);
    }

    private void BeginWindowDrag(object sender, MouseEventArgs args) {
      if (args.Button != MouseButtons.Left) return;
      ReleaseCapture();
      SendMessage(Handle, 0xA1, 0x2, 0);
    }

    private void ToggleMaximize() {
      WindowState = WindowState == FormWindowState.Maximized
        ? FormWindowState.Normal
        : FormWindowState.Maximized;
    }

    protected override void OnHandleCreated(EventArgs args) {
      base.OnHandleCreated(args);
      UpdateMaximizedBounds();
    }

    protected override void OnLocationChanged(EventArgs args) {
      base.OnLocationChanged(args);
      if (WindowState == FormWindowState.Normal) UpdateMaximizedBounds();
    }

    private void UpdateMaximizedBounds() {
      Screen screen = Screen.FromControl(this);
      if (screen != null) MaximizedBounds = screen.WorkingArea;
    }

    protected override void WndProc(ref Message message) {
      base.WndProc(ref message);
      if (
        message.Msg != WmNcHitTest
        || WindowState != FormWindowState.Normal
        || (int)message.Result != HtClient
      ) return;

      long packed = message.LParam.ToInt64();
      Point pointer = PointToClient(new Point(
        unchecked((short)(packed & 0xffff)),
        unchecked((short)((packed >> 16) & 0xffff))));
      bool left = pointer.X <= ResizeBorder;
      bool right = pointer.X >= ClientSize.Width - ResizeBorder;
      bool top = pointer.Y <= ResizeBorder;
      bool bottom = pointer.Y >= ClientSize.Height - ResizeBorder;

      if (left && top) message.Result = (IntPtr)HtTopLeft;
      else if (right && top) message.Result = (IntPtr)HtTopRight;
      else if (left && bottom) message.Result = (IntPtr)HtBottomLeft;
      else if (right && bottom) message.Result = (IntPtr)HtBottomRight;
      else if (left) message.Result = (IntPtr)HtLeft;
      else if (right) message.Result = (IntPtr)HtRight;
      else if (top) message.Result = (IntPtr)HtTop;
      else if (bottom) message.Result = (IntPtr)HtBottom;
    }

    private static Button TitleButton(string text) {
      Button button = new Button {
        Text = text,
        Width = 34,
        Height = 28,
        Margin = new Padding(1, 1, 0, 0),
        FlatStyle = FlatStyle.Flat,
        BackColor = Color.Transparent,
        ForeColor = Color.White,
        Font = new Font("Segoe UI", 9f, FontStyle.Bold),
        TabStop = false
      };
      button.FlatAppearance.BorderSize = 0;
      button.FlatAppearance.MouseOverBackColor = Color.FromArgb(22, 71, 78);
      return button;
    }

    private static Button UpgradeButton() {
      Button button = new Button {
        Text = "Upgrade",
        Width = 92,
        Height = 28,
        Margin = new Padding(8, 1, 8, 0),
        FlatStyle = FlatStyle.Flat,
        BackColor = Color.FromArgb(255, 225, 200),
        ForeColor = Color.FromArgb(0, 69, 80),
        Font = new Font("Segoe UI", 8.5f, FontStyle.Bold),
        TabStop = false
      };
      button.FlatAppearance.BorderSize = 0;
      button.FlatAppearance.MouseOverBackColor = Color.FromArgb(255, 214, 173);
      return button;
    }

    private async Task StartBrowser() {
      try {
        CoreWebView2EnvironmentOptions options = new CoreWebView2EnvironmentOptions();
        CoreWebView2Environment environment = await CoreWebView2Environment.CreateAsync(null, PortablePaths.WebViewDir, options);
        await browser.EnsureCoreWebView2Async(environment);
        browser.CoreWebView2.Settings.AreDevToolsEnabled = false;
        try { browser.CoreWebView2.Settings.UserAgent = "RacinageFreePortable/" + PortablePaths.Version; } catch { }
        browser.CoreWebView2.NavigationStarting += BrowserNavigationStarting;
        browser.CoreWebView2.Navigate(server.BaseUrl + (store.PendingShareReceiptCount() > 0 ? "/share" : "/"));
        RefreshStatus();
      } catch (Exception error) {
        lastError = error.Message;
        SetStatus(Color.FromArgb(185, 51, 51), "Error");
        Program.Log("Browser startup error: " + error);
        MessageBox.Show("The local Racinage Free browser could not start.\r\n\r\n" + error.Message, PortablePaths.AppName);
      }
    }

    private void BrowserNavigationStarting(object sender, CoreWebView2NavigationStartingEventArgs e) {
      Uri uri;
      if (!Uri.TryCreate(e.Uri, UriKind.Absolute, out uri)) return;
      string path = uri.AbsolutePath.ToLowerInvariant();
      if (path.StartsWith("/admin7839", StringComparison.OrdinalIgnoreCase)) {
        e.Cancel = true;
        MessageBox.Show("The super admin dashboard is not available in Racinage Free portable.", PortablePaths.AppName);
        return;
      }
      if (uri.Scheme == "https" && uri.Host.Equals("racinage.com", StringComparison.OrdinalIgnoreCase) && path.StartsWith("/pricing")) {
        e.Cancel = true;
        OpenExternal(PortablePaths.PricingUrl);
        return;
      }
      if (!uri.Host.Equals("127.0.0.1", StringComparison.OrdinalIgnoreCase) && !uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase)) {
        e.Cancel = true;
        OpenExternal(uri.ToString());
      }
    }

    private void RefreshStatus() {
      CheckCalendarReminders();
      if (!server.IsRunning) {
        SetStatus(Color.FromArgb(185, 51, 51), "Offline");
        return;
      }
      if (lastError != "") {
        SetStatus(Color.FromArgb(185, 51, 51), "Error");
        return;
      }
      SetStatus(Color.FromArgb(19, 151, 47), "Synced");
    }

    private void ShareInboxChanged(object sender, FileSystemEventArgs args) {
      try {
        BeginInvoke((MethodInvoker)delegate {
          store.ImportShareInbox();
          if (browser.CoreWebView2 != null) browser.CoreWebView2.Navigate(server.BaseUrl + "/share");
        });
      } catch { }
    }

    private void CheckCalendarReminders() {
      if (DateTime.UtcNow < nextCalendarReminderCheckUtc) return;
      nextCalendarReminderCheckUtc = DateTime.UtcNow.AddSeconds(30);
      try {
        List<Dictionary<string,string> > reminders = store.ClaimDueCalendarReminders(DateTime.Now);
        if (reminders.Count == 0) return;
        string title = reminders.Count == 1 ? reminders[0]["title"] : reminders.Count.ToString(CultureInfo.InvariantCulture) + " Calendar reminders";
        string message = String.Join("\r\n", reminders.Take(4).Select(item => item["title"] + " - " + item["occurrence_at"]));
        if (reminders.Count > 4) message += "\r\n+" + (reminders.Count - 4).ToString(CultureInfo.InvariantCulture) + " more";
        if (message.Length > 240) message = message.Substring(0, 237) + "...";
        calendarReminderIcon.ShowBalloonTip(8000, title, message, ToolTipIcon.Info);
      } catch (Exception error) {
        Program.Log("Calendar reminder check failed: " + error.Message);
      }
    }

    private void SetStatus(Color color, string text) {
      statusDot.StatusColor = color;
      statusText.Text = text;
      statusDot.Invalidate();
    }

    private void ShowSyncDetails() {
      string message =
        "Mode: Local Lite Free\r\n" +
        "Device status: active\r\n" +
        "Server: racinage.com (upgrade links only)\r\n" +
        "Local URL: " + server.BaseUrl + "\r\n" +
        "Local database: " + store.DatabasePath + "\r\n" +
        "Database protection: " + store.DatabaseProtectionNote + "\r\n" +
        "Tracked local changes: " + store.PendingChangeCount().ToString(CultureInfo.InvariantCulture) + "\r\n" +
        "Device ID: " + store.DeviceId + "\r\n" +
        "Last error: " + (lastError == "" ? "none" : lastError);
      MessageBox.Show(message, "Racinage Free Sync Details", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    private static void OpenExternal(string url) {
      try {
        Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
      } catch (Exception error) {
        MessageBox.Show("Could not open the link.\r\n\r\n" + error.Message, PortablePaths.AppName);
      }
    }
  }

  internal sealed class StatusDotControl : Control {
    internal Color StatusColor = Color.FromArgb(212, 121, 47);

    internal StatusDotControl() {
      SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
      Width = 22;
      Height = 28;
      MinimumSize = new Size(22, 28);
      MaximumSize = new Size(22, 28);
      Cursor = Cursors.Hand;
    }

    protected override void OnPaint(PaintEventArgs e) {
      base.OnPaint(e);
      e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
      int diameter = Math.Max(8, Math.Min(12, Math.Min(Width, Height) - 10));
      int x = (Width - diameter) / 2;
      int y = (Height - diameter) / 2;
      using (SolidBrush brush = new SolidBrush(StatusColor)) {
        e.Graphics.FillEllipse(brush, x, y, diameter, diameter);
      }
    }
  }

  internal sealed partial class LocalServer {
    private readonly LocalStore store;
    private readonly PortableAiService ai;
    private readonly ConnectedMessaging connected;
    private readonly PluginCatalogClient pluginCatalog = new PluginCatalogClient();
    private readonly JavaScriptSerializer json = new JavaScriptSerializer { MaxJsonLength = 64 * 1024 * 1024 };
    private HttpListener listener;
    private Thread thread;
    private volatile bool running;

    internal string BaseUrl { get; private set; }
    internal bool IsRunning { get { return running; } }

    internal LocalServer(LocalStore store) {
      this.store = store;
      ai = new PortableAiService(store);
      connected = new ConnectedMessaging(store);
    }

    internal void Start() {
      int port = ReservePort();
      BaseUrl = "http://127.0.0.1:" + port.ToString(CultureInfo.InvariantCulture);
      listener = new HttpListener();
      listener.Prefixes.Add(BaseUrl + "/");
      listener.Start();
      File.WriteAllText(Path.Combine(PortablePaths.UpdatesDir, "local-port.txt"), port.ToString(CultureInfo.InvariantCulture), Encoding.UTF8);
      running = true;
      connected.Start();
      store.ImportShareInbox();
      store.ProcessPendingKitchenImportsAsync();
      thread = new Thread(ListenLoop);
      thread.IsBackground = true;
      thread.Start();
    }

    internal void Stop() {
      running = false;
      connected.Stop();
      try { if (listener != null) listener.Stop(); } catch { }
      try { if (listener != null) listener.Close(); } catch { }
    }

    private static int ReservePort() {
      TcpListener tcp = new TcpListener(IPAddress.Loopback, 0);
      tcp.Start();
      int port = ((IPEndPoint)tcp.LocalEndpoint).Port;
      tcp.Stop();
      return port;
    }

    private void ListenLoop() {
      while (running) {
        try {
          HttpListenerContext context = listener.GetContext();
          ThreadPool.QueueUserWorkItem(delegate { Handle(context); });
        } catch {
          if (running) Thread.Sleep(150);
        }
      }
    }

    private void Handle(HttpListenerContext context) {
      try {
        string path = context.Request.Url.AbsolutePath.TrimEnd('/');
        if (path == "") path = "/";
        if (path.StartsWith("/admin7839", StringComparison.OrdinalIgnoreCase)) {
          WriteHtml(context, Page("Not available", "<section class='panel'><h1>Not available</h1><p>The super admin dashboard is not accessible from Racinage Free portable.</p><p><a class='button' href='/'>Back home</a></p></section>"), 403);
          return;
        }
        if (path == "/fonts/inter/InterVariable.woff2") { WriteFile(context, "fonts\\inter\\InterVariable.woff2", "font/woff2"); return; }
        if (path == "/fonts/inter/InterVariable-Italic.woff2") { WriteFile(context, "fonts\\inter\\InterVariable-Italic.woff2", "font/woff2"); return; }
        if (path == "/assets/ai-assistant.css") { WriteFile(context, "assets\\ai-assistant.css", "text/css; charset=utf-8"); return; }
        if (path == "/assets/ai-assistant.js") { WriteFile(context, "assets\\ai-assistant.js", "application/javascript; charset=utf-8"); return; }
        if (path == "/health") { WriteJson(context, "{\"ok\":true,\"product\":\"Racinage Free\",\"version\":\"" + PortablePaths.Version + "\"}"); return; }
        if (path == "/upgrade") { Redirect(context, PortablePaths.PricingUrl); return; }
        if (path == "/login") { Login(context); return; }
        if (path == "/start-free") { StartFree(context); return; }
        if (path == "/family") { Family(context); return; }
        if (path == "/messages") {
          if (!IsAuthenticated(context)) { Redirect(context, "/login"); return; }
          WriteHtml(context, Page(
            "Messages",
            connected.Render(context.Request.QueryString["conversation"])));
          return;
        }
        if (path == "/calendar") { Calendar(context); return; }
        if (path == "/share") { Share(context); return; }
        if (path == "/connected-messaging-api") {
          ConnectedMessagingApi(context);
          return;
        }
        if (path == "/connected-messaging-upload") {
          ConnectedMessagingUpload(context);
          return;
        }
        if (path == "/manage" || path == "/manage/plugins" || path == "/manage/family" || path == "/manage/settings" || path == "/manage/ai") { Manage(context, path); return; }
        if (path == "/local-ai-api") { LocalAiApi(context); return; }
        if (path.StartsWith("/local-plugin-api/", StringComparison.OrdinalIgnoreCase)) { LocalPluginApi(context, path.Substring(18)); return; }
        if (path.StartsWith("/plugin/", StringComparison.OrdinalIgnoreCase)) { PortablePlugin(context, path.Substring(8)); return; }
        if (path == "/logout") { store.ClearSession(); ExpireCookie(context); Redirect(context, "/"); return; }
        WriteHtml(context, Home(context));
      } catch (Exception error) {
        Program.Log("Request error: " + error);
        WriteHtml(context, Page("Error", "<section class='panel'><h1>Something went wrong</h1><p>" + H(error.Message) + "</p><p><a class='button' href='/'>Back home</a></p></section>"), 500);
      }
    }

    private string Home(HttpListenerContext context) {
      bool authenticated = IsAuthenticated(context);
      string state = store.HasUser()
        ? "<p class='note'>This device already has one local Lite Free account. Sign in to continue.</p>"
        : "<p class='note'>Create one local user and one local family account. Your records stay on this Windows device.</p>";
      string action = authenticated
        ? "<a class='button' href='/family'>Open local family</a><a class='button ghost' href='/logout'>Log out</a>"
        : "<a class='button' href='/login'>Login</a><a class='button ghost' href='/start-free'>Start Free</a>";
      return Page("Racinage Free",
        "<section class='hero'>" +
        "<div><p class='kicker'>Lite Free Portable</p><h1>Racinage Free</h1><p>Organise family records locally without an internet connection. Upgrade when you want hosted sharing, sync, and collaboration tools.</p><div class='actions'>" + action + "</div>" + state + "</div>" +
        "</section>" +
        "<section class='grid'>" +
        "<article><h2>Local by default</h2><p>The local database, media, logs, WebView profile, and device token live under your Windows user profile, outside the app payload.</p></article>" +
        "<article><h2>Free plan limits</h2><p>Racinage Free allows one local user account and one local family account on this device.</p></article>" +
        "<article><h2>Upgrade path</h2><p>Public sharing, hosted sync, and paid-plan desktop access are available from racinage.com.</p><p><a href='" + PortablePaths.PricingUrl + "'>View pricing</a></p></article>" +
        "</section>");
    }

    private void Login(HttpListenerContext context) {
      if (context.Request.HttpMethod == "POST") {
        Dictionary<string, string> form = ReadForm(context);
        if (!CheckCsrf(form)) { WriteHtml(context, LoginPage("Your session expired. Please try again."), 400); return; }
        string username = (form.ContainsKey("username") ? form["username"] : "").Trim();
        string password = form.ContainsKey("password") ? form["password"] : "";
        if (store.ValidateLogin(username, password)) {
          string token = store.IssueSession();
          SetSessionCookie(context, token);
          Redirect(context, store.PendingShareReceiptCount() > 0 ? "/share" : "/family");
          return;
        }
        WriteHtml(context, LoginPage("Invalid username or password."), 401);
        return;
      }
      WriteHtml(context, LoginPage(""));
    }

    private string LoginPage(string error) {
      string body =
        "<section class='panel narrow'><h1>Login</h1>" +
        ErrorHtml(error) +
        "<form method='post' action='/login'>" + CsrfInput() +
        "<label>Username<input name='username' autocomplete='username' required></label>" +
        "<label>Password<input name='password' type='password' autocomplete='current-password' required></label>" +
        "<button class='button' type='submit'>Login</button>" +
        "</form><p class='note'><a href='/'>Back home</a></p></section>";
      return Page("Login", body);
    }

    private void StartFree(HttpListenerContext context) {
      if (store.HasUser()) {
        WriteHtml(context, Page("Start Free", "<section class='panel narrow'><h1>Start Free</h1><p>This portable Free plan already has its one local user account.</p><p><a class='button' href='/login'>Login</a></p></section>"));
        return;
      }
      if (context.Request.HttpMethod == "POST") {
        Dictionary<string, string> form = ReadForm(context);
        if (!CheckCsrf(form)) { WriteHtml(context, StartFreePage("Your session expired. Please try again."), 400); return; }
        string displayName = (form.ContainsKey("display_name") ? form["display_name"] : "").Trim();
        string username = (form.ContainsKey("username") ? form["username"] : "").Trim();
        string password = form.ContainsKey("password") ? form["password"] : "";
        string familyName = (form.ContainsKey("family_name") ? form["family_name"] : "").Trim();
        if (displayName == "" || username == "" || familyName == "" || password.Length < 6) {
          WriteHtml(context, StartFreePage("Please fill in all fields. Password must be at least 6 characters."), 400);
          return;
        }
        store.CreateAccount(displayName, username, password, familyName);
        string token = store.IssueSession();
        SetSessionCookie(context, token);
        Redirect(context, store.PendingShareReceiptCount() > 0 ? "/share" : "/family");
        return;
      }
      WriteHtml(context, StartFreePage(""));
    }

    private string StartFreePage(string error) {
      string body =
        "<section class='panel narrow'><h1>Start Free</h1>" +
        "<p class='note'>Create the one local user and one local family account allowed by Racinage Free.</p>" +
        ErrorHtml(error) +
        "<form method='post' action='/start-free'>" + CsrfInput() +
        "<label>Your name<input name='display_name' autocomplete='name' required></label>" +
        "<label>Username<input name='username' autocomplete='username' required></label>" +
        "<label>Password<input name='password' type='password' autocomplete='new-password' minlength='6' required></label>" +
        "<label>Family account name<input name='family_name' required></label>" +
        "<button class='button' type='submit'>Create local account</button>" +
        "</form><p class='note'><a href='/'>Back home</a></p></section>";
      return Page("Start Free", body);
    }

    private void Family(HttpListenerContext context) {
      if (!IsAuthenticated(context)) { Redirect(context, "/login"); return; }
      if (context.Request.HttpMethod == "POST") {
        Dictionary<string, string> form = ReadForm(context);
        if (!CheckCsrf(form)) { WriteHtml(context, FamilyPage("Your session expired. Please try again."), 400); return; }
        string action = form.ContainsKey("action") ? form["action"] : "";
        if (action == "save_family") {
          store.SaveFamily(
            form.ContainsKey("name") ? form["name"].Trim() : "",
            form.ContainsKey("location") ? form["location"].Trim() : "",
            form.ContainsKey("story") ? form["story"].Trim() : "");
        } else if (action == "add_person") {
          store.AddPerson(
            form.ContainsKey("full_name") ? form["full_name"].Trim() : "",
            form.ContainsKey("relationship") ? form["relationship"].Trim() : "",
            form.ContainsKey("birth_date") ? form["birth_date"].Trim() : "",
            form.ContainsKey("place") ? form["place"].Trim() : "",
            form.ContainsKey("notes") ? form["notes"].Trim() : "");
        } else if (action == "delete_person") {
          int id;
          if (int.TryParse(form.ContainsKey("id") ? form["id"] : "0", out id)) store.DeletePerson(id);
        }
        Redirect(context, "/family");
        return;
      }
      WriteHtml(context, FamilyPage(""));
    }

    private string FamilyPage(string error) {
      Dictionary<string, string> family = store.GetFamily();
      List<Dictionary<string, string> > people = store.GetPeople();
      StringBuilder rows = new StringBuilder();
      if (people.Count == 0) {
        rows.Append("<p class='empty'>No people added yet.</p>");
      } else {
        rows.Append("<div class='people'>");
        foreach (Dictionary<string, string> person in people) {
          rows.Append("<article><div><strong>" + H(person["full_name"]) + "</strong><span>" + H(person["relationship"]) + "</span></div>");
          rows.Append("<p>" + H(person["birth_date"]) + (person["place"] == "" ? "" : " - " + H(person["place"])) + "</p>");
          if (person["notes"] != "") rows.Append("<p>" + H(person["notes"]) + "</p>");
          rows.Append("<form method='post' action='/family'>" + CsrfInput() + "<input type='hidden' name='action' value='delete_person'><input type='hidden' name='id' value='" + H(person["id"]) + "'><button class='textbtn' type='submit'>Delete</button></form>");
          rows.Append("</article>");
        }
        rows.Append("</div>");
      }

      string shareButtons =
        "<div class='sharebar'>" +
        ShareButton("tree", "Share tree") +
        ShareButton("album", "Share album") +
        ShareButton("event", "Share event") +
        ShareButton("project", "Share project") +
        ShareButton("finance", "Share finance") +
        ShareButton("document", "Share document") +
        ShareButton("history", "Share history") +
        "</div>";

      string body =
        "<section class='dashhead'><div><p class='kicker'>Local family account</p><h1>" + H(family["name"]) + "</h1><p>Saved on this Windows device. Sharing and hosted sync require an upgraded Racinage plan.</p></div><div class='actions'><a class='button ghost' href='/manage'>Manage</a><a class='button ghost' href='/logout'>Log out</a></div></section>" +
        ErrorHtml(error) +
        "<section class='layout'>" +
        "<article class='panel'><h2>Family details</h2><form method='post' action='/family'>" + CsrfInput() + "<input type='hidden' name='action' value='save_family'>" +
        "<label>Family name<input name='name' value='" + A(family["name"]) + "' required></label>" +
        "<label>Location<input name='location' value='" + A(family["location"]) + "'></label>" +
        "<label>Family story<textarea name='story' rows='6'>" + H(family["story"]) + "</textarea></label>" +
        "<button class='button' type='submit'>Save family details</button></form></article>" +
        "<article class='panel'><h2>Add person</h2><form method='post' action='/family'>" + CsrfInput() + "<input type='hidden' name='action' value='add_person'>" +
        "<label>Full name<input name='full_name' required></label>" +
        "<label>Relationship<input name='relationship' placeholder='Parent, cousin, child...'></label>" +
        "<label>Birth date<input name='birth_date' type='date'></label>" +
        "<label>Place<input name='place'></label>" +
        "<label>Notes<textarea name='notes' rows='4'></textarea></label>" +
        "<button class='button' type='submit'>Add person</button></form></article>" +
        "</section>" +
        "<section class='panel wide'><div class='panelhead'><div><h2>Family records</h2><p>" + people.Count.ToString(CultureInfo.InvariantCulture) + " people saved locally.</p></div></div>" + shareButtons + rows.ToString() + "</section>" +
        UpgradeModal() + PortableAiShell("family");
      return Page("Family", body);
    }

    private void Calendar(HttpListenerContext context) {
      if (!IsAuthenticated(context)) { Redirect(context, "/login"); return; }
      string message = "";
      if (context.Request.HttpMethod == "POST") {
        Dictionary<string,string> form = ReadForm(context);
        if (!CheckCsrf(form)) { WriteHtml(context, Page("Calendar", CalendarPage(context, "Your session expired. Please try again.")), 400); return; }
        try {
          string action = form.ContainsKey("action") ? form["action"] : "";
          if (action == "save_calendar_item") { store.SaveCalendarItem(form); message = "Calendar item saved."; }
          else if (action == "delete_calendar_item") { store.DeleteCalendarItem(form.ContainsKey("long_id") ? form["long_id"] : "", form.ContainsKey("revision") ? form["revision"] : "0"); message = "Calendar item deleted."; }
          else if (action == "preview_ics") { store.PreviewCalendarIcs(form.ContainsKey("ics_text") ? form["ics_text"] : ""); message = "ICS preview is ready. Review it before importing."; }
          else if (action == "import_ics") { int imported = store.ImportPendingCalendarIcs(); message = imported.ToString(CultureInfo.InvariantCulture) + " calendar items imported."; }
          else if (action == "discard_ics") { store.DiscardPendingCalendarIcs(); message = "ICS preview discarded."; }
          else if (action == "save_calendar_preferences") { store.SaveCalendarPreferences(form); message = "Calendar preferences saved."; }
          else throw new InvalidDataException("Unknown Calendar action.");
          string view = form.ContainsKey("return_view") ? SafeCalendarView(form["return_view"]) : "month";
          string anchor = form.ContainsKey("return_anchor") && ValidDate(form["return_anchor"]) ? form["return_anchor"] : DateTime.Today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
          Redirect(context, "/calendar?view=" + Uri.EscapeDataString(view) + "&anchor=" + Uri.EscapeDataString(anchor) + "&message=" + Uri.EscapeDataString(message));
          return;
        } catch (Exception error) { message = error.Message; }
      }
      if (context.Request.QueryString["export"] == "ics") {
        string ics = store.ExportCalendarIcs();byte[] bytes = Encoding.UTF8.GetBytes(ics);context.Response.StatusCode=200;context.Response.ContentType="text/calendar; charset=utf-8";context.Response.Headers["Content-Disposition"]="attachment; filename=\"racinage-free-calendar.ics\"";context.Response.Headers["Cache-Control"]="no-store";context.Response.ContentLength64=bytes.Length;context.Response.OutputStream.Write(bytes,0,bytes.Length);context.Response.Close();return;
      }
      if (message == "") message = context.Request.QueryString["message"] ?? "";
      WriteHtml(context, Page("Calendar", CalendarPage(context, message)));
    }

    private static string SafeCalendarView(string value) { return new[]{"month","week","day","agenda","year"}.Contains(value) ? value : "month"; }
    private static object[] CalendarJsonArray(Dictionary<string,object> values,string key) { object value;if(values==null||!values.TryGetValue(key,out value)||value==null)return new object[0];object[] array=value as object[];if(array!=null)return array;ArrayList list=value as ArrayList;return list==null?new object[0]:list.ToArray(); }
    private static string SafeCalendarRecordId(string value) { if(String.IsNullOrEmpty(value)||value.Length>80)return "";foreach(char c in value)if(!Char.IsLetterOrDigit(c)&&c!='_'&&c!='-')return "";return value; }
    private static bool ValidDate(string value) { DateTime parsed;return !String.IsNullOrEmpty(value)&&DateTime.TryParseExact(value,"yyyy-MM-dd",CultureInfo.InvariantCulture,DateTimeStyles.None,out parsed); }

    private string CalendarPage(HttpListenerContext context, string message) {
      Dictionary<string,string> preferences = store.CalendarPreferences();
      string view = SafeCalendarView(context.Request.QueryString["view"] ?? (preferences.ContainsKey("view_name") ? preferences["view_name"] : "month"));
      DateTime anchor;string rawAnchor=context.Request.QueryString["anchor"]??(preferences.ContainsKey("anchor_date")?preferences["anchor_date"]:"");if(!DateTime.TryParseExact(rawAnchor,"yyyy-MM-dd",CultureInfo.InvariantCulture,DateTimeStyles.None,out anchor))anchor=DateTime.Today;
      store.RememberCalendarView(view,anchor.ToString("yyyy-MM-dd",CultureInfo.InvariantCulture));
      DateTime start,end;
      if(view=="day"){start=anchor.Date;end=start.AddDays(1);}else if(view=="week"){int offset=((int)anchor.DayOfWeek+6)%7;start=anchor.Date.AddDays(-offset);end=start.AddDays(7);}else if(view=="year"){start=new DateTime(anchor.Year,1,1);end=start.AddYears(1);}else if(view=="agenda"){start=anchor.Date;end=start.AddDays(90);}else{start=new DateTime(anchor.Year,anchor.Month,1);int offset=((int)start.DayOfWeek+6)%7;start=start.AddDays(-offset);end=start.AddDays(42);}
      List<Dictionary<string,string> > entries=store.CalendarEntries(start,end);
      Dictionary<string,object> filterSettings=new Dictionary<string,object>();try{filterSettings=json.DeserializeObject(preferences.ContainsKey("filters_json")?preferences["filters_json"]:"{}") as Dictionary<string,object>??new Dictionary<string,object>();}catch{}HashSet<string> selectedSources=new HashSet<string>(CalendarJsonArray(filterSettings,"sources").Select(value=>Convert.ToString(value,CultureInfo.InvariantCulture)),StringComparer.Ordinal),selectedKinds=new HashSet<string>(CalendarJsonArray(filterSettings,"kinds").Select(value=>Convert.ToString(value,CultureInfo.InvariantCulture)),StringComparer.Ordinal);if(selectedSources.Count>0)entries=entries.Where(item=>selectedSources.Contains(item.ContainsKey("source_id")?item["source_id"]:"")).ToList();if(selectedKinds.Count>0)entries=entries.Where(item=>selectedKinds.Contains(item.ContainsKey("item_kind")?item["item_kind"]:"")).ToList();
      DateTime previous=view=="day"?anchor.AddDays(-1):view=="week"?anchor.AddDays(-7):view=="year"?anchor.AddYears(-1):anchor.AddMonths(-1);DateTime next=view=="day"?anchor.AddDays(1):view=="week"?anchor.AddDays(7):view=="year"?anchor.AddYears(1):anchor.AddMonths(1);
      string label=view=="day"?anchor.ToString("dddd, dd MMMM yyyy",CultureInfo.CurrentCulture):view=="week"?start.ToString("dd MMM",CultureInfo.CurrentCulture)+" - "+end.AddDays(-1).ToString("dd MMM yyyy",CultureInfo.CurrentCulture):view=="year"?anchor.Year.ToString(CultureInfo.InvariantCulture):anchor.ToString("MMMM yyyy",CultureInfo.CurrentCulture);
      string controls="<div class='calendar-controls'><div class='actions'><a class='button ghost' href='/calendar?view="+A(view)+"&anchor="+previous.ToString("yyyy-MM-dd",CultureInfo.InvariantCulture)+"' aria-label='Previous'>Previous</a><a class='button ghost' href='/calendar?view="+A(view)+"&anchor="+DateTime.Today.ToString("yyyy-MM-dd",CultureInfo.InvariantCulture)+"'>Today</a><a class='button ghost' href='/calendar?view="+A(view)+"&anchor="+next.ToString("yyyy-MM-dd",CultureInfo.InvariantCulture)+"' aria-label='Next'>Next</a></div><form method='get' action='/calendar' class='calendar-jump'><input type='hidden' name='view' value='"+A(view)+"'><label>Jump to<input type='date' name='anchor' value='"+anchor.ToString("yyyy-MM-dd",CultureInfo.InvariantCulture)+"' onchange='this.form.submit()'></label></form></div>";
      string tabs="<nav class='calendar-view-tabs' aria-label='Calendar views'>"+String.Join("",new[]{"month","week","day","agenda","year"}.Select(item=>"<a class='"+(view==item?"active":"")+"' href='/calendar?view="+item+"&anchor="+anchor.ToString("yyyy-MM-dd",CultureInfo.InvariantCulture)+"'"+(view==item?" aria-current='page'":"")+">"+CultureInfo.InvariantCulture.TextInfo.ToTitleCase(item)+"</a>"))+"</nav>";
      string[] sourceValues={"core.calendar","ics.import","plugin.finance-manager","plugin.kitchen-planner"},sourceLabels={"Calendar","ICS imports","Finance Manager","Kitchen Planner"},kindValues={"event","meeting","task","reminder","transaction","target","due","meal","restock","expiry"};StringBuilder filterChoices=new StringBuilder();for(int i=0;i<sourceValues.Length;i++)filterChoices.Append("<label><input type='checkbox' data-calendar-filter-source value='"+A(sourceValues[i])+"'"+(selectedSources.Count==0||selectedSources.Contains(sourceValues[i])?" checked":"")+"><span>"+H(sourceLabels[i])+"</span></label>");StringBuilder kindChoices=new StringBuilder();foreach(string kind in kindValues)kindChoices.Append("<label><input type='checkbox' data-calendar-filter-kind value='"+A(kind)+"'"+(selectedKinds.Count==0||selectedKinds.Contains(kind)?" checked":"")+"><span>"+H(CultureInfo.InvariantCulture.TextInfo.ToTitleCase(kind))+"</span></label>");string filterPanel="<details class='calendar-filter-panel'><summary>Filters <span>"+entries.Count.ToString(CultureInfo.InvariantCulture)+" visible</span></summary><form method='post' action='/calendar' data-calendar-filter-form>"+CsrfInput()+"<input type='hidden' name='action' value='save_calendar_preferences'><input type='hidden' name='return_view' value='"+A(view)+"'><input type='hidden' name='return_anchor' value='"+anchor.ToString("yyyy-MM-dd",CultureInfo.InvariantCulture)+"'><input type='hidden' name='view_name' value='"+A(view)+"'><input type='hidden' name='anchor_date' value='"+anchor.ToString("yyyy-MM-dd",CultureInfo.InvariantCulture)+"'><input type='hidden' name='filters_json' data-calendar-filter-json value='"+A(json.Serialize(filterSettings))+"'><input type='hidden' name='working_hours_json' value='"+A(preferences.ContainsKey("working_hours_json")?preferences["working_hours_json"]:"{}")+"'><fieldset><legend>Sources</legend>"+filterChoices+"</fieldset><fieldset><legend>Types</legend>"+kindChoices+"</fieldset><p role='status'>Changes save automatically.</p></form></details>";
      string body="<section class='calendar-head'><div><p class='kicker'>Account-wide local calendar</p><h1>Calendar</h1><p>Local dates from Calendar, history, Finance Manager, Kitchen Planner, and future reviewed portable sources.</p></div><div class='actions'><a class='button ghost' href='/calendar?export=ics'>Export ICS</a><button class='button' type='button' data-calendar-open>Quick add</button></div></section>"+ErrorHtml(message)+tabs+controls+filterPanel+"<section class='calendar-title'><h2>"+H(label)+"</h2><span>"+entries.Count.ToString(CultureInfo.InvariantCulture)+" visible entries</span></section>"+RenderCalendarView(view,start,end,entries)+CalendarEditor(view,anchor,entries,context.Request.QueryString["edit"]??"")+CalendarIcsPanel()+PortableAiShell("calendar");
      return body;
    }

    private string RenderCalendarView(string view, DateTime start, DateTime end, List<Dictionary<string,string> > entries) {
      if(view=="agenda"||view=="day")return RenderCalendarAgenda(entries,view=="day"?"No items on this day.":"No upcoming items.");
      if(view=="year"){
        StringBuilder months=new StringBuilder("<div class='calendar-year'>");for(int month=1;month<=12;month++){DateTime first=new DateTime(start.Year,month,1),last=first.AddMonths(1);int count=entries.Count(item=>CalendarEntryDate(item)>=first&&CalendarEntryDate(item)<last);months.Append("<a href='/calendar?view=month&anchor="+first.ToString("yyyy-MM-dd",CultureInfo.InvariantCulture)+"'><strong>"+H(first.ToString("MMMM",CultureInfo.CurrentCulture))+"</strong><span>"+count.ToString(CultureInfo.InvariantCulture)+" items</span></a>");}return months.Append("</div>").ToString();
      }
      StringBuilder grid=new StringBuilder("<div class='calendar-grid "+(view=="week"?"is-week":"is-month")+"'>");DateTime cursor=start;while(cursor<end){List<Dictionary<string,string> > dayEntries=entries.Where(item=>CalendarEntryDate(item).Date==cursor.Date).Take(view=="month"?4:20).ToList();grid.Append("<article class='calendar-day"+(cursor.Date==DateTime.Today?" is-today":"")+"'><header><a href='/calendar?view=day&anchor="+cursor.ToString("yyyy-MM-dd",CultureInfo.InvariantCulture)+"'><span>"+H(cursor.ToString("ddd",CultureInfo.CurrentCulture))+"</span><strong>"+cursor.Day.ToString(CultureInfo.InvariantCulture)+"</strong></a><button type='button' aria-label='Add on "+cursor.ToString("dd/MM/yyyy",CultureInfo.InvariantCulture)+"' onclick=\"calendarQuickDate('"+cursor.ToString("yyyy-MM-dd",CultureInfo.InvariantCulture)+"')\">+</button></header><div>");foreach(Dictionary<string,string> item in dayEntries)grid.Append(CalendarEntryHtml(item));if(dayEntries.Count==0)grid.Append("<span class='calendar-empty-day'>No items</span>");grid.Append("</div></article>");cursor=cursor.AddDays(1);}return grid.Append("</div>").ToString();
    }

    private static DateTime CalendarEntryDate(Dictionary<string,string> item) { DateTime value;bool allDay=item.ContainsKey("all_day")&&item["all_day"]=="1";string raw=allDay?(item.ContainsKey("date_value")?item["date_value"]:""):(item.ContainsKey("start_utc")&&item["start_utc"]!=""?item["start_utc"]:(item.ContainsKey("date_value")?item["date_value"]:""));if(DateTime.TryParse(raw,CultureInfo.InvariantCulture,DateTimeStyles.AssumeUniversal|DateTimeStyles.AdjustToUniversal,out value))return allDay?value.Date:value.ToLocalTime();return DateTime.MinValue; }

    private static string CalendarEntryHtml(Dictionary<string,string> item) { string title=H(item.ContainsKey("title")?item["title"]:"Busy"),color=H(item.ContainsKey("color")?item["color"]:"#0f7370"),time=(item.ContainsKey("all_day")&&item["all_day"]=="1")?"All day":CalendarEntryDate(item).ToString("HH:mm",CultureInfo.CurrentCulture),source=item.ContainsKey("source_id")?item["source_id"]:"";string href=source=="core.calendar"?"/calendar?view=day&anchor="+CalendarEntryDate(item).ToString("yyyy-MM-dd",CultureInfo.InvariantCulture)+"&edit="+A(item.ContainsKey("long_id")?item["long_id"]:""):source=="plugin.kitchen-planner"?"/plugin/kitchen-planner":source=="plugin.finance-manager"?"/plugin/finance-manager":"/calendar?view=day&anchor="+CalendarEntryDate(item).ToString("yyyy-MM-dd",CultureInfo.InvariantCulture);return "<a class='calendar-entry' style='--entry-color:"+color+"' href='"+href+"'><span>"+H(time)+"</span><strong>"+title+"</strong></a>"; }

    private string RenderCalendarAgenda(List<Dictionary<string,string> > entries,string empty) { if(entries.Count==0)return "<p class='calendar-empty'>"+H(empty)+"</p>";StringBuilder body=new StringBuilder("<div class='calendar-agenda'>");foreach(IGrouping<DateTime,Dictionary<string,string> > group in entries.OrderBy(CalendarEntryDate).GroupBy(item=>CalendarEntryDate(item).Date)){body.Append("<section><time>"+H(group.Key.ToString("dddd dd MMMM yyyy",CultureInfo.CurrentCulture))+"</time><div>");foreach(Dictionary<string,string> item in group)body.Append(CalendarEntryHtml(item));body.Append("</div></section>");}return body.Append("</div>").ToString(); }

    private string CalendarEditor(string view,DateTime anchor,List<Dictionary<string,string> > entries,string requestedEdit) {
      requestedEdit=SafeCalendarRecordId(requestedEdit);Dictionary<string,string> item=requestedEdit==""?null:entries.FirstOrDefault(row=>row.ContainsKey("long_id")&&row["long_id"]==requestedEdit&&row.ContainsKey("source_id")&&row["source_id"]=="core.calendar");bool editing=item!=null;string editId=editing?item["long_id"]:"",title=editing&&item.ContainsKey("title")?item["title"]:"",kind=editing&&item.ContainsKey("item_kind")?item["item_kind"]:"event",date=editing?CalendarEntryDate(item).ToString("yyyy-MM-dd",CultureInfo.InvariantCulture):anchor.ToString("yyyy-MM-dd",CultureInfo.InvariantCulture),start="",end="",frequency="",reminder="",notes=editing&&item.ContainsKey("notes")?item["notes"]:"",revision=editing&&item.ContainsKey("revision")?item["revision"]:"0";
      if(editing&&item.ContainsKey("all_day")&&item["all_day"]!="1"){DateTime startDate;if(item.ContainsKey("start_utc")&&DateTime.TryParse(item["start_utc"],CultureInfo.InvariantCulture,DateTimeStyles.AssumeUniversal|DateTimeStyles.AdjustToUniversal,out startDate))start=startDate.ToLocalTime().ToString("HH:mm",CultureInfo.InvariantCulture);DateTime endDate;if(item.ContainsKey("end_utc")&&DateTime.TryParse(item["end_utc"],CultureInfo.InvariantCulture,DateTimeStyles.AssumeUniversal|DateTimeStyles.AdjustToUniversal,out endDate))end=endDate.ToLocalTime().ToString("HH:mm",CultureInfo.InvariantCulture);}
      if(editing&&item.ContainsKey("recurrence_json")&&item["recurrence_json"]!=""){try{Dictionary<string,object> rule=json.DeserializeObject(item["recurrence_json"]) as Dictionary<string,object>;object value;if(rule!=null&&rule.TryGetValue("frequency",out value)&&value!=null)frequency=Convert.ToString(value,CultureInfo.InvariantCulture);}catch{}}
      if(editing&&item.ContainsKey("reminder_json")&&item["reminder_json"]!=""){try{Dictionary<string,object> rule=json.DeserializeObject(item["reminder_json"]) as Dictionary<string,object>;if(rule!=null)reminder=Convert.ToString(rule.ContainsKey("minutes_before")?rule["minutes_before"]:"",CultureInfo.InvariantCulture);}catch{}}
      Func<string,string,string> option=(value,label)=>"<option value='"+A(value)+"'"+(value==kind?" selected":"")+">"+H(label)+"</option>";Func<string,string,string> recurrenceOption=(value,label)=>"<option value='"+A(value)+"'"+(value==frequency?" selected":"")+">"+H(label)+"</option>";Func<string,string,string> reminderOption=(value,label)=>"<option value='"+A(value)+"'"+(value==reminder?" selected":"")+">"+H(label)+"</option>";
      string modal="<div id='calendarNew' class='modal' role='dialog' aria-modal='true' aria-labelledby='calendarEditorTitle'"+(editing?"":" hidden")+"><div class='modalbox calendar-editor'><div class='panelhead'><div><p class='kicker'>Calendar item</p><h2 id='calendarEditorTitle'>"+(editing?"Edit local item":"Schedule locally")+"</h2></div><button type='button' class='textbtn' data-calendar-close>Close</button></div><form method='post' action='/calendar'>"+CsrfInput()+"<input type='hidden' name='action' value='save_calendar_item'><input type='hidden' name='long_id' value='"+A(editId)+"'><input type='hidden' name='revision' value='"+A(revision)+"'><input type='hidden' name='return_view' value='"+A(view)+"'><input type='hidden' name='return_anchor' value='"+anchor.ToString("yyyy-MM-dd",CultureInfo.InvariantCulture)+"'><label>Title<input name='title' maxlength='190' value='"+A(title)+"' required autofocus></label><label>Kind<select name='item_kind'>"+option("event","Event")+option("meeting","Meeting")+option("task","Task")+option("reminder","Reminder")+"</select></label><label>Date<input id='calendarQuickDate' type='date' name='date_value' value='"+A(date)+"' required></label><div class='calendar-editor-row'><label>Start time<input type='time' name='start_time' value='"+A(start)+"'></label><label>End time<input type='time' name='end_time' value='"+A(end)+"'></label></div><label>Repeat<select name='frequency'>"+recurrenceOption("","Does not repeat")+recurrenceOption("daily","Daily")+recurrenceOption("weekly","Weekly")+recurrenceOption("monthly","Monthly")+recurrenceOption("yearly","Yearly")+"</select></label><label>Reminder<select name='reminder_minutes'>"+reminderOption("","None")+reminderOption("10","10 minutes before")+reminderOption("60","1 hour before")+reminderOption("1440","1 day before")+"</select></label><label>Notes<textarea name='notes' maxlength='2000' rows='3'>"+H(notes)+"</textarea></label><button class='button'>"+(editing?"Save changes":"Save item")+"</button></form>";
      if(editing)modal+="<form method='post' action='/calendar' onsubmit=\"return confirm('Delete this local calendar item?')\">"+CsrfInput()+"<input type='hidden' name='action' value='delete_calendar_item'><input type='hidden' name='long_id' value='"+A(editId)+"'><input type='hidden' name='revision' value='"+A(revision)+"'><input type='hidden' name='return_view' value='"+A(view)+"'><input type='hidden' name='return_anchor' value='"+anchor.ToString("yyyy-MM-dd",CultureInfo.InvariantCulture)+"'><button class='button danger'>Delete item</button></form>";
      return modal+"</div></div>";
    }

    private string CalendarIcsPanel() {
      Dictionary<string,object> preview=store.PendingCalendarIcs();string previewHtml="";if(preview.Count>0)previewHtml="<div class='calendar-ics-preview'><strong>"+H(Convert.ToString(preview["count"],CultureInfo.InvariantCulture))+" items ready</strong><span>"+H(Convert.ToString(preview["duplicates"],CultureInfo.InvariantCulture))+" possible duplicates</span><form method='post'>"+CsrfInput()+"<input type='hidden' name='action' value='import_ics'><button class='button'>Confirm import</button></form><form method='post'>"+CsrfInput()+"<input type='hidden' name='action' value='discard_ics'><button class='button ghost'>Discard</button></form></div>";return "<section class='panel wide calendar-ics'><div class='panelhead'><div><h2>ICS import</h2><p>Preview a one-time ICS import. Duplicate fingerprints are skipped and unknown timezones remain date-safe.</p></div></div>"+previewHtml+"<form method='post' action='/calendar'>"+CsrfInput()+"<input type='hidden' name='action' value='preview_ics'><label>Paste ICS content<textarea name='ics_text' rows='6' maxlength='200000' required></textarea></label><button class='button ghost'>Preview ICS</button></form></section>";
    }

    private void Manage(HttpListenerContext context, string path) {
      if (!IsAuthenticated(context)) { Redirect(context, "/login"); return; }
      string message = "";
      bool asynchronous = String.Equals(context.Request.Headers["X-Requested-With"], "XMLHttpRequest", StringComparison.OrdinalIgnoreCase);
      if (context.Request.HttpMethod == "POST") {
        Dictionary<string, string> form = ReadForm(context);
        if (!CheckCsrf(form)) {
          if (asynchronous) WriteJson(context, json.Serialize(new Dictionary<string,object>{{"ok",false},{"message","Your session expired. Please try again."}}), 400);
          else WriteHtml(context, ManagePage(path, "Your session expired. Please try again."), 400);
          return;
        }
        string action = form.ContainsKey("action") ? form["action"] : "";
        string slug = form.ContainsKey("slug") ? form["slug"] : "";
        bool frenchNameGen = String.Equals(slug, "namegen", StringComparison.OrdinalIgnoreCase) && IsFrenchCulture();
        if (action == "install_plugin") {
          message = pluginCatalog.Install(slug, store);
          if (frenchNameGen && message.StartsWith("Plugin installed.", StringComparison.Ordinal)) message = "Extension NameGen installée. Les limites Lite restent applicables.";
        }
        else if (action == "hide_plugin") { store.SetPluginStatus(slug, "hidden"); message = frenchNameGen ? "Extension NameGen désactivée. Ses données locales ont été conservées." : "Plugin disabled. Its local data and attachments were kept."; }
        else if (action == "enable_plugin") { store.SetPluginStatus(slug, "enabled"); message = frenchNameGen ? "Extension NameGen activée." : "Plugin enabled."; }
        else if (action == "uninstall_plugin") { bool deleteData=form.ContainsKey("delete_data")&&form["delete_data"]=="yes";store.UninstallPlugin(slug,deleteData);message=frenchNameGen?(deleteData?"Extension NameGen désinstallée et données locales supprimées.":"Extension NameGen désinstallée. Ses données locales ont été conservées."):(deleteData?"Plugin uninstalled and its local data deleted.":"Plugin uninstalled. Its local data was kept."); }
        else if (action == "save_currency_settings") { store.SaveCurrencySettings(form.ContainsKey("display_currency")?form["display_currency"]:"USD",form.ContainsKey("currency_rates")?form["currency_rates"]:"");message="Currency settings saved."; }
        if (asynchronous) { WriteJson(context,json.Serialize(new Dictionary<string,object>{{"ok",true},{"message",message},{"html",PluginsPanel()}}));return; }
      }
      WriteHtml(context, ManagePage(path, message));
    }

    private void ConnectedMessagingApi(HttpListenerContext context) {
      if (
        !IsAuthenticated(context)
        || context.Request.HttpMethod != "POST"
        || !store.CheckCsrf(context.Request.Headers["X-Racinage-CSRF"])
      ) {
        WriteJson(
          context,
          json.Serialize(new Dictionary<string, object> {
            { "ok", false },
            { "message", "The local connected-account session is unavailable." }
          }),
          403);
        return;
      }
      if (context.Request.ContentLength64 < 1 || context.Request.ContentLength64 > 262144) {
        WriteJson(
          context,
          json.Serialize(new Dictionary<string, object> {
            { "ok", false },
            { "message", "The local connected-account request is too large." }
          }),
          413);
        return;
      }
      try {
        string body;
        using (StreamReader reader = new StreamReader(
          context.Request.InputStream,
          Encoding.UTF8)) {
          body = reader.ReadToEnd();
        }
        Dictionary<string, object> request =
          json.Deserialize<Dictionary<string, object> >(body)
          ?? new Dictionary<string, object>();
        WriteJson(context, json.Serialize(connected.Action(request)));
      } catch (Exception error) {
        Program.Log("Connected messaging local action failed: " + error.Message);
        WriteJson(
          context,
          json.Serialize(new Dictionary<string, object> {
            { "ok", false },
            { "message", error.Message }
          }),
          422);
      }
    }

    private void ConnectedMessagingUpload(HttpListenerContext context) {
      if (
        !IsAuthenticated(context)
        || context.Request.HttpMethod != "POST"
        || !store.CheckCsrf(context.Request.Headers["X-Racinage-CSRF"])
      ) {
        WriteJson(
          context,
          json.Serialize(new Dictionary<string, object> {
            { "ok", false },
            { "message", "The local upload session is unavailable." }
          }),
          403);
        return;
      }
      try {
        Dictionary<string, object> result = connected.QueueFile(
          context.Request.InputStream,
          context.Request.ContentLength64,
          context.Request.QueryString["conversation_id"] ?? "",
          context.Request.Headers["X-File-Name"] ?? "",
          context.Request.ContentType ?? "application/octet-stream");
        WriteJson(context, json.Serialize(result), 201);
      } catch (Exception error) {
        Program.Log("Connected messaging local file queue failed: " + error.Message);
        WriteJson(
          context,
          json.Serialize(new Dictionary<string, object> {
            { "ok", false },
            { "message", error.Message }
          }),
          422);
      }
    }

    private string ManagePage(string path, string message) {
      string active = path.EndsWith("/plugins", StringComparison.OrdinalIgnoreCase) ? "plugins" : (path.EndsWith("/settings", StringComparison.OrdinalIgnoreCase) ? "settings" : (path.EndsWith("/family", StringComparison.OrdinalIgnoreCase) ? "family" : (path.EndsWith("/ai", StringComparison.OrdinalIgnoreCase) ? "ai" : "account")));
      string tabs = "<nav class='manage-tabs' aria-label='Manage sections'>" + ManageTab("/manage", "Account", active == "account") + ManageTab("/manage/family", "Family", active == "family") + ManageTab("/manage/plugins", "Plugins", active == "plugins") + ManageTab("/manage/ai", "AI Features", active == "ai") + ManageTab("/manage/settings", "Settings", active == "settings") + "</nav>";
      string content;
      if (active == "plugins") content = PluginsPanel();
      else if (active == "ai") content = PortableAiSetup();
      else if (active == "family") {
        Dictionary<string, string> family = store.GetFamily();
        content = "<section class='manage-card'><div class='manage-card-head'><div><h2>Family account</h2><p>The local Free edition has one owner-managed family and no collaboration controls.</p></div><a class='button' href='/family'>Open family records</a></div><dl class='facts'><div><dt>Name</dt><dd>" + H(family["name"]) + "</dd></div><div><dt>Location</dt><dd>" + H(family["location"] == "" ? "Not set" : family["location"]) + "</dd></div></dl></section>";
      } else if (active == "settings") {
        List<Dictionary<string,string> > rates=store.GetCurrencyRates();string selected=store.GetDisplayCurrency();StringBuilder options=new StringBuilder(),lines=new StringBuilder();foreach(Dictionary<string,string> rate in rates){options.Append("<option value='"+A(rate["code"])+"'"+(rate["code"]==selected?" selected":"")+">"+H(rate["code"]+" - "+rate["name"])+"</option>");lines.Append(rate["code"]+" | "+rate["name"]+" | "+rate["rate"]+"\r\n");}
        content = "<section class='manage-grid'><article class='manage-card'><h2>Local settings</h2><p>Database, media, installed plugins, and device tokens stay under your Windows user profile.</p><dl class='facts'><div><dt>Edition</dt><dd>Lite Free Portable</dd></div><div><dt>Version</dt><dd>" + H(PortablePaths.Version) + "</dd></div><div><dt>Plugin updates</dt><dd>Checked only when you open the Plugins tab</dd></div></dl></article><article class='manage-card'><h2>Share with Racinage Free</h2><p>Paste a link or text into the local chooser when the optional signed Windows Share Target identity is not installed.</p><a class='button ghost' href='/share'>Open share chooser</a></article><article class='manage-card'><h2>Currency localisation</h2><p>Finance Manager uses this account display currency. Rates are entered as the amount of each currency equal to 1 USD.</p><form method='post' action='/manage/settings'>"+CsrfInput()+"<input type='hidden' name='action' value='save_currency_settings'><label>Display currency<select name='display_currency'>"+options+"</select></label><label>Offline currency rates<textarea name='currency_rates' rows='8' spellcheck='false'>"+H(lines.ToString())+"</textarea></label><small>One per line: CODE | Name | rate. USD must remain 1.</small><button class='button' type='submit'>Save currency settings</button></form></article></section>";
      } else {
        Dictionary<string, object> connectedStatus = connected.Status();
        content = "<section class='manage-grid'><article class='manage-card'><h2>Local account</h2><p>One local user owns this device's family records. Collaborative members and invitations are intentionally unavailable.</p><a class='button ghost' href='/family'>Open dashboard</a></article><article class='manage-card'><h2>Connected messaging</h2><p>State: " + H(Convert.ToString(connectedStatus["state"], CultureInfo.InvariantCulture).Replace('_', ' ')) + ". Hosted password and two-factor authentication are handled only on racinage.com.</p><a class='button ghost' href='/messages'>Open Messages</a></article><article class='manage-card'><h2>Plan</h2><p>Lite Free limits apply to local features. Reviewed plugins can add Free features, while optional Pro features are purchased through the publisher's hosted Racinage page.</p><a class='button' href='" + PortablePaths.PricingUrl + "'>View Racinage plans</a></article></section>";
      }
      string body = "<section class='manage-head'><div><p class='kicker'>Manage</p><h1>Account and features</h1><p>Manage the local account using the same clear sections as the hosted app, without collaborative controls.</p></div></section>" + tabs + ErrorHtml(message) + "<div class='manage-content'>" + content + "</div>" + PortableAiShell(active);
      return Page("Manage", body);
    }

    private string PortableAiSetup() {
      Dictionary<string, object> status = ai.Status();
      string provider = Convert.ToString(status["provider"], CultureInfo.InvariantCulture);
      string endpoint = Convert.ToString(status["endpoint"], CultureInfo.InvariantCulture);
      string model = Convert.ToString(status["model"], CultureInfo.InvariantCulture);
      string readiness = Convert.ToString(status["readiness"], CultureInfo.InvariantCulture);
      return "<section class='manage-card'><div class='manage-card-head'><div><h2>Local AI providers</h2><p>Ollama, LM Studio, and custom OpenAI-compatible loopback servers are first-class local providers.</p></div><span class='status-pill'>" + H(readiness.Replace('_', ' ')) + "</span></div>"
        + "<form class='local-ai-setup' data-portable-ai-setup><div class='local-ai-provider-cards'>"
        + LocalProviderOption("ollama", "Ollama", "Native /api/tags discovery and /api/chat tools.", provider)
        + LocalProviderOption("lmstudio", "LM Studio", "OpenAI-compatible model discovery and tools.", provider)
        + LocalProviderOption("custom", "Advanced custom local", "A loopback OpenAI-compatible server.", provider)
        + "</div><div class='local-ai-form-grid'><label>Local endpoint<input name='endpoint' value='" + A(endpoint) + "' required></label>"
        + "<label>Model<input name='model' value='" + A(model) + "' list='portableAiModels' required><datalist id='portableAiModels' data-portable-ai-models></datalist></label>"
        + "<label>Optional local token<input name='token' type='password' autocomplete='off' placeholder='Stored with Windows DPAPI'></label></div>"
        + "<div class='actions'><button class='button ghost' type='submit' data-local-ai-action='discover'>Discover models</button><button class='button ghost' type='submit' data-local-ai-action='save'>Save setup</button><button class='button' type='submit' data-local-ai-action='test'>Test capabilities</button></div>"
        + "<p class='local-ai-setup-status' data-portable-ai-setup-status>Provider tests verify reachability, model discovery, and a native structured tool round-trip. Models that fail remain available for writing and questions, but cannot prepare CRUD actions.</p></form></section>"
        + "<section class='manage-grid'><article class='manage-card local-ai-privacy'><h3>Local privacy boundary</h3><p>Only localhost, 127.0.0.1, or ::1 are accepted. Redirects and remote resolution are rejected. The optional token is protected for the current Windows user with DPAPI and is never uploaded.</p></article>"
        + "<article class='manage-card local-ai-privacy'><h3>Portable scope</h3><p>The assistant can work with the one local family, people, low-risk settings, and reviewed portable plugins. It does not claim hosted Gallery, Events, Projects, or Trees modules.</p></article>"
        + "<article class='manage-card local-ai-privacy'><h3>Hosted companion</h3><p>Pairing and hosted job execution remain unavailable in public builds until the server feature gate and trusted Windows code-signing release gate are approved.</p></article></section>";
    }

    private static string LocalProviderOption(string value, string title, string description, string selected) {
      return "<label><input type='radio' name='provider' value='" + A(value) + "'" + (selected == value ? " checked" : "") + "><span><strong>" + H(title) + "</strong><small>" + H(description) + "</small></span></label>";
    }

    private void LocalAiApi(HttpListenerContext context) {
      if (!IsAuthenticated(context) || context.Request.HttpMethod != "POST"
          || !store.CheckCsrf(context.Request.Headers["X-Racinage-CSRF"])) {
        WriteJson(context, "{\"ok\":false,\"message\":\"The local AI session is unavailable.\"}", 403);
        return;
      }
      if (context.Request.ContentLength64 < 1 || context.Request.ContentLength64 > 256 * 1024) {
        WriteJson(context, "{\"ok\":false,\"message\":\"The local AI request is too large.\"}", 413);
        return;
      }
      try {
        string body;
        using (StreamReader reader = new StreamReader(context.Request.InputStream, Encoding.UTF8)) body = reader.ReadToEnd();
        Dictionary<string, object> request = json.Deserialize<Dictionary<string, object> >(body) ?? new Dictionary<string, object>();
        string action = request.ContainsKey("action") ? Convert.ToString(request["action"], CultureInfo.InvariantCulture) : "";
        Dictionary<string, object> data;
        if (action == "status") data = ai.Status();
        else if (action == "save_config") data = ai.SaveConfig(request);
        else if (action == "discover") data = ai.Discover(request);
        else if (action == "test") data = ai.Test(request);
        else if (action == "chat") data = ai.Chat(request);
        else if (action == "apply") data = ai.Apply(request);
        else throw new InvalidOperationException("Unknown local AI action.");
        WriteJson(context, json.Serialize(new Dictionary<string, object> { { "ok", true }, { "data", data } }));
      } catch (Exception error) {
        Program.Log("Local AI action failed without prompt payload logging: " + error.GetType().Name);
        WriteJson(context, json.Serialize(new Dictionary<string, object> { { "ok", false }, { "message", error.Message } }), 400);
      }
    }

    private string PortableAiShell(string page) {
      string icon = "<svg viewBox='0 0 20 20' aria-hidden='true'><defs><linearGradient id='portableAiGradient' x1='0' y1='0' x2='1' y2='1'><stop offset='0' stop-color='#7357ff'/><stop offset='.34' stop-color='#00a6c8'/><stop offset='.68' stop-color='#21a35b'/><stop offset='1' stop-color='#e5952f'/></linearGradient></defs><g fill='url(#portableAiGradient)'>"
        + "<path d='M9.997,13.867c-0.388,0-0.702,0.315-0.702,0.702v4.335c0,0.387,0.314,0.702,0.702,0.702c0.388,0,0.702-0.315,0.702-0.702v-4.335C10.698,14.182,10.384,13.867,9.997,13.867z'></path><path d='M9.997,6.133c0.388,0,0.702-0.315,0.702-0.702V1.096c0-0.386-0.314-0.702-0.702-0.702c-0.388,0-0.702,0.316-0.702,0.702v4.335C9.295,5.818,9.609,6.133,9.997,6.133z'></path>"
        + "<path d='M12.89,13.604c-0.193-0.334-0.621-0.449-0.958-0.256c-0.335,0.193-0.45,0.623-0.256,0.958l1.568,2.719c0.129,0.224,0.364,0.35,0.607,0.35c0.119,0,0.24-0.03,0.351-0.094c0.336-0.193,0.451-0.624,0.257-0.958L12.89,13.604z'></path><path d='M7.107,6.394c0.129,0.225,0.366,0.351,0.607,0.351c0.119,0,0.239-0.031,0.35-0.095c0.336-0.193,0.451-0.623,0.256-0.958L6.753,2.976C6.561,2.639,6.13,2.527,5.796,2.72C5.46,2.913,5.345,3.344,5.54,3.678L7.107,6.394z'></path>"
        + "<path d='M6.13,10c0-0.389-0.314-0.702-0.702-0.702H1.096c-0.388,0-0.702,0.312-0.702,0.702c0,0.386,0.314,0.702,0.702,0.702h4.333C5.816,10.702,6.13,10.386,6.13,10z'></path><path d='M18.901,9.299h-4.335c-0.388,0-0.702,0.312-0.702,0.702c0,0.386,0.314,0.702,0.702,0.702h4.335c0.388,0,0.702-0.316,0.702-0.702C19.602,9.611,19.289,9.299,18.901,9.299z'></path>"
        + "<path d='M9.997,6.755c-1.789,0-3.244,1.455-3.244,3.245c0,1.789,1.455,3.244,3.244,3.244c1.79,0,3.245-1.455,3.245-3.244C13.242,8.211,11.786,6.755,9.997,6.755z M9.997,11.842c-1.015,0-1.841-0.826-1.841-1.841c0-1.017,0.826-1.842,1.841-1.842c1.015,0,1.842,0.825,1.842,1.842C11.839,11.016,11.012,11.842,9.997,11.842z'></path>"
        + "<path d='M17.021,13.245l-2.716-1.567c-0.334-0.192-0.765-0.077-0.958,0.258c-0.195,0.334-0.079,0.764,0.256,0.958l2.716,1.567c0.111,0.064,0.232,0.094,0.351,0.094c0.241,0,0.478-0.126,0.607-0.351C17.472,13.867,17.356,13.439,17.021,13.245z'></path><path d='M2.973,6.755l2.716,1.568C5.8,8.386,5.921,8.416,6.04,8.416c0.241,0,0.478-0.126,0.607-0.35c0.194-0.334,0.079-0.765-0.256-0.958L3.675,5.54C3.341,5.349,2.91,5.462,2.717,5.797C2.522,6.133,2.637,6.561,2.973,6.755z'></path>"
        + "<path d='M13.347,8.066c0.128,0.224,0.366,0.35,0.607,0.35c0.119,0,0.24-0.03,0.351-0.093l2.716-1.568c0.335-0.194,0.451-0.622,0.256-0.959c-0.193-0.337-0.623-0.45-0.958-0.257l-2.716,1.568C13.268,7.301,13.152,7.731,13.347,8.066z'></path><path d='M6.647,11.935c-0.192-0.337-0.622-0.452-0.958-0.258l-2.716,1.567c-0.335,0.194-0.45,0.622-0.256,0.959c0.129,0.224,0.366,0.351,0.607,0.351c0.119,0,0.24-0.03,0.351-0.094l2.716-1.567C6.726,12.699,6.841,12.269,6.647,11.935z'></path>"
        + "<path d='M11.931,6.65c0.111,0.064,0.232,0.095,0.351,0.095c0.241,0,0.478-0.126,0.607-0.351l1.567-2.716c0.194-0.334,0.079-0.765-0.257-0.958c-0.333-0.192-0.764-0.079-0.958,0.256l-1.568,2.716C11.481,6.026,11.596,6.457,11.931,6.65z'></path><path d='M8.065,13.348c-0.33-0.191-0.763-0.079-0.958,0.256l-1.57,2.719c-0.194,0.334-0.079,0.764,0.256,0.958c0.109,0.064,0.232,0.094,0.351,0.094c0.241,0,0.477-0.126,0.607-0.35l1.57-2.719C8.516,13.971,8.401,13.541,8.065,13.348z'></path></g></svg>";
      string headIcon = icon.Replace("portableAiGradient", "portableAiGradientHead");
      return "<div class='portable-ai-shell' data-portable-ai-shell data-page='" + A(page) + "' data-csrf='" + A(store.CsrfToken) + "'>"
        + "<button class='portable-ai-toggle' type='button' data-portable-ai-open aria-label='Open local AI assistant'>" + icon + "</button>"
        + "<aside class='portable-ai-sidebar' data-portable-ai-sidebar aria-hidden='true'><header class='portable-ai-head'>" + headIcon + "<div><strong>Local AI assistant</strong><small>Explicit loopback provider - no cloud fallback</small></div><button class='portable-ai-close' type='button' data-portable-ai-close aria-label='Close'>x</button></header>"
        + "<div class='portable-ai-messages' data-portable-ai-messages><article class='portable-ai-message assistant'><strong>Local AI</strong><p>Ask about this local family, people, settings, or reviewed portable plugins. Confirmed changes use typed previews.</p></article></div>"
        + "<form class='portable-ai-compose' data-portable-ai-chat-form><textarea data-portable-ai-input maxlength='12000' placeholder='Ask your local model...' required></textarea><div class='portable-ai-compose-actions'><p class='portable-ai-status' data-portable-ai-status></p><button class='button ghost' type='button' data-portable-ai-stop hidden>Stop</button><button class='button' type='submit'>Send</button></div></form></aside></div>";
    }

    private string PluginsPanel() {
      List<Dictionary<string, string> > installed = store.GetInstalledPlugins();
      Dictionary<string, Dictionary<string, string> > installedBySlug = new Dictionary<string, Dictionary<string, string> >(StringComparer.OrdinalIgnoreCase);
      foreach (Dictionary<string, string> row in installed) installedBySlug[row["slug"]] = row;
      StringBuilder cards = new StringBuilder();
      Dictionary<string,string> financeInstall;if(installedBySlug.TryGetValue("finance-manager",out financeInstall)){bool financeEnabled=financeInstall["status"]=="enabled";cards.Append("<article class='plugin-card'><div class='plugin-card-top'><span class='plugin-mark'>F</span><div><h3>Finance Manager</h3><p class='plugin-meta'>1.4.0 - bundled</p></div></div><p>Multiple offline Personal, Family, and Group workspaces with transactions, budgets, goals, recurring records, reports, attachments, and circles.</p><div class='actions'>"+(financeEnabled?"<a class='button' href='/plugin/finance-manager'>Open</a>":"")+"<form class='plugin-action' method='post' action='/manage/plugins'>"+CsrfInput()+"<input type='hidden' name='action' value='"+(financeEnabled?"hide_plugin":"enable_plugin")+"'><input type='hidden' name='slug' value='finance-manager'><button class='button ghost' type='submit'>"+(financeEnabled?"Hide":"Enable")+"</button></form></div></article>");}
      try {
        List<PortablePluginInfo> plugins = pluginCatalog.GetPlugins();
        store.RefreshInstalledShareContracts(plugins);
        foreach (PortablePluginInfo plugin in plugins) {
          if(String.Equals(plugin.slug,"finance-manager",StringComparison.OrdinalIgnoreCase))continue;
          Dictionary<string, string> current;
          bool isInstalled = installedBySlug.TryGetValue(plugin.slug ?? "", out current);
          int listPriceCents = Math.Max(0, plugin.price_cents);
          int effectivePriceCents = plugin.effective_price_cents.HasValue ? Math.Min(listPriceCents, Math.Max(0, plugin.effective_price_cents.Value)) : listPriceCents;
          string currency = String.IsNullOrWhiteSpace(plugin.currency) ? "USD" : plugin.currency.ToUpperInvariant();
          bool frenchNameGen=String.Equals(plugin.slug,"namegen",StringComparison.OrdinalIgnoreCase)&&IsFrenchCulture();
          string displayName = frenchNameGen&&!String.IsNullOrWhiteSpace(plugin.name_fr)?plugin.name_fr:(String.IsNullOrWhiteSpace(plugin.name) ? "Plugin" : plugin.name);
          string displaySummary = frenchNameGen&&!String.IsNullOrWhiteSpace(plugin.summary_fr)?plugin.summary_fr:plugin.summary;
          string interval = plugin.pricing_type == "subscription" ? (plugin.billing_interval == "year" ? "/year" : "/month") : "";
          string price = plugin.pricing_type == "free" || listPriceCents <= 0 ? "Free" : currency + " " + (effectivePriceCents / 100.0).ToString("0.00", CultureInfo.InvariantCulture) + interval;
          string priceMeta = H(plugin.version) + " - ";
          if (effectivePriceCents < listPriceCents) priceMeta += "<del>" + H(currency + " " + (listPriceCents / 100.0).ToString("0.00", CultureInfo.InvariantCulture)) + "</del> ";
          priceMeta += H(price);
          if (!String.IsNullOrWhiteSpace(plugin.promotion_label)) {
            DateTime promotionEnd;
            string expiry = DateTime.TryParse(plugin.promotion_expires_at, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal, out promotionEnd) ? " until " + promotionEnd.ToLocalTime().ToString("dd/MM/yyyy", CultureInfo.InvariantCulture) : "";
            priceMeta += "<small>" + H(plugin.promotion_label + expiry) + "</small>";
          }
          cards.Append("<article class='plugin-card'><div class='plugin-card-top'><span class='plugin-mark'>" + H(displayName.Substring(0, 1).ToUpperInvariant()) + "</span><div><h3>" + H(displayName) + "</h3><p class='plugin-meta'>" + priceMeta + "</p></div></div><p>" + H(displaySummary) + "</p>");
          if (plugin.local == null || !plugin.local.supported) cards.Append("<p class='notice'>Web only: " + H(plugin.local == null ? "No reviewed local runtime is available." : plugin.local.reason) + "</p>");
          else if (isInstalled) cards.Append("<div class='actions'>"+(current["status"]=="enabled"?"<a class='button' href='/plugin/" + A(plugin.slug) + "'>"+(frenchNameGen?"Ouvrir":"Open")+"</a>":"")+"<form class='plugin-action' method='post' action='/manage/plugins'>" + CsrfInput() + "<input type='hidden' name='action' value='"+(current["status"]=="enabled"?"hide_plugin":"enable_plugin")+"'><input type='hidden' name='slug' value='" + A(plugin.slug) + "'><button class='button ghost' type='submit'>"+(current["status"]=="enabled"?(frenchNameGen?"Désactiver":"Disable"):(frenchNameGen?"Activer":"Enable"))+"</button></form><form class='plugin-action' method='post' action='/manage/plugins'>"+CsrfInput()+"<input type='hidden' name='action' value='uninstall_plugin'><input type='hidden' name='slug' value='"+A(plugin.slug)+"'><button class='button ghost danger' type='submit'>"+(frenchNameGen?"Désinstaller":"Uninstall")+"</button></form></div>");
          else if ((plugin.download_url ?? "") != "") cards.Append("<form class='plugin-action' method='post' action='/manage/plugins'>" + CsrfInput() + "<input type='hidden' name='action' value='install_plugin'><input type='hidden' name='slug' value='" + A(plugin.slug) + "'><button class='button' type='submit'>"+(frenchNameGen?"Installer":"Install")+"</button></form>");
          if (plugin.pricing_type != "free" && plugin.price_cents > 0) cards.Append("<p><a href='" + A(plugin.purchase_url) + "'>Buy or manage Pro access on Racinage</a></p>");
          cards.Append("</article>");
        }
      } catch (Exception error) {
        Program.Log("Plugin catalog error: " + error);
        cards.Append("<p class='notice'>The online plugin library is unavailable right now. Installed plugins remain available.</p>");
      }
      return "<section class='manage-card'><div class='manage-card-head'><div><h2>Plugin library</h2><p>Only reviewed, checksum-verified, local-compatible bundles can be installed. Collaboration plugins and controls are excluded.</p></div><span class='status-pill'>Lite rules apply</span></div><div class='plugin-grid'>" + cards.ToString() + "</div></section>";
    }

    private static bool IsFrenchCulture() {
      CultureInfo culture = CultureInfo.CurrentUICulture;
      return String.Equals(culture.TwoLetterISOLanguageName, "fr", StringComparison.OrdinalIgnoreCase);
    }

    private void PortablePlugin(HttpListenerContext context, string slug) {
      if (!IsAuthenticated(context)) { Redirect(context, "/login"); return; }
      string entrypoint = store.PluginEntrypoint(slug);
      if (entrypoint == "" || !File.Exists(entrypoint)) { WriteHtml(context, Page("Plugin unavailable", "<section class='panel'><h1>Plugin unavailable</h1><p>This local plugin is missing or disabled.</p><a class='button' href='/manage/plugins'>Back to plugins</a></section>"), 404); return; }
      string source = File.ReadAllText(entrypoint, Encoding.UTF8);
      string pluginCsp = "<meta http-equiv=\"Content-Security-Policy\" content=\"default-src 'none'; base-uri 'none'; connect-src 'none'; form-action 'none'; frame-src 'none'; object-src 'none'; script-src 'unsafe-inline'; style-src 'unsafe-inline'; img-src data: blob:; media-src data: blob:; font-src data:\">";
      source = source.Replace("<head>", "<head>" + pluginCsp);
      string root = Path.GetDirectoryName(entrypoint);
      string cssPath = Path.Combine(root, "app.css"), jsPath = Path.Combine(root, "app.js");
      if (File.Exists(cssPath)) source = source.Replace("<link rel=\"stylesheet\" href=\"app.css\">", "<style>" + File.ReadAllText(cssPath, Encoding.UTF8) + "</style>");
      if (File.Exists(jsPath)) source = source.Replace("<script src=\"app.js\"></script>", "<script>" + File.ReadAllText(jsPath, Encoding.UTF8).Replace("</script>", "<\\/script>") + "</script>");
      string bridgeToken = RandomToken(32);
      source = source.Replace("{{FINANCE_BRIDGE_TOKEN}}", bridgeToken).Replace("{{RACINAGE_PLUGIN_BRIDGE_TOKEN}}",bridgeToken);
      string pluginName=store.PluginName(slug),pluginCopy=String.Equals(slug,"namegen",StringComparison.OrdinalIgnoreCase)?"Private name-finding records remain available without internet access.":"Private local plugin records remain available without internet access.";
      string body = "<section class='manage-head plugin-shell-head'><div><p class='kicker'>Local plugin</p><h1>"+H(pluginName)+"</h1><p>"+H(pluginCopy)+"</p></div><a class='button ghost' href='/manage/plugins'>Back to plugins</a></section><iframe class='plugin-frame' data-plugin-slug='" + A(slug) + "' data-bridge-token='" + A(bridgeToken) + "' sandbox='allow-scripts allow-downloads allow-modals' referrerpolicy='no-referrer' srcdoc='" + A(source) + "'></iframe>";
      WriteHtml(context, Page(slug, body));
    }

    private void LocalPluginApi(HttpListenerContext context, string slug) {
      if (!PluginCatalogClient.ValidSlug(slug) || !IsAuthenticated(context) || context.Request.HttpMethod != "POST" || !store.CheckCsrf(context.Request.Headers["X-Racinage-CSRF"])) { WriteJson(context, "{\"ok\":false,\"message\":\"The local session is unavailable.\"}", 403); return; }
      if (context.Request.ContentLength64 < 1 || context.Request.ContentLength64 > 40L * 1024L * 1024L) { WriteJson(context, "{\"ok\":false,\"message\":\"The plugin request is too large.\"}", 413); return; }
      try {
        string body;
        using (StreamReader reader = new StreamReader(context.Request.InputStream, Encoding.UTF8)) body = reader.ReadToEnd();
        Dictionary<string, object> request = json.Deserialize<Dictionary<string, object> >(body);
        string action = request != null && request.ContainsKey("action") ? Convert.ToString(request["action"], CultureInfo.InvariantCulture) : "";
        Dictionary<string, object> payload = request != null && request.ContainsKey("payload") ? request["payload"] as Dictionary<string, object> : null;
        Dictionary<string, object> safePayload = payload ?? new Dictionary<string, object>();
        object result;
        if (slug == "kitchen-planner" && action == "local_ai_status") {
          if (!store.PluginActionAllowed(slug, action)) throw new InvalidOperationException("This local plugin operation is not authorized.");
          result = ai.Status();
        } else if (slug == "kitchen-planner" && action == "local_ai_extract") {
          if (!store.PluginActionAllowed(slug, action)) throw new InvalidOperationException("This local plugin operation is not authorized.");
          string extractionRun = store.BeginKitchenAiExtraction(safePayload);
          try {
            Dictionary<string, object> extracted = ai.ExtractKitchenRecipes(safePayload);
            store.CompleteKitchenAiExtraction(extractionRun, extracted);
            result = extracted;
          } catch (Exception extractionError) {
            store.FailKitchenAiExtraction(extractionRun, extractionError.Message);
            throw;
          }
        } else result = store.LocalPluginAction(slug, action, safePayload);
        WriteJson(context, json.Serialize(new Dictionary<string, object> { { "ok", true }, { "result", result } }), 200);
      } catch (Exception error) {
        Program.Log("Local plugin bridge error: " + error.Message);
        WriteJson(context, json.Serialize(new Dictionary<string, object> { { "ok", false }, { "message", error.Message } }), 400);
      }
    }

    private static string ManageTab(string href, string label, bool active) {
      return "<a class='" + (active ? "active" : "") + "' href='" + href + "'>" + H(label) + "</a>";
    }

    private static string ShareButton(string feature, string label) {
      return "<button type='button' class='share' onclick=\"showUpgrade('" + H(feature) + "')\">" + H(label) + "</button>";
    }

    private bool IsAuthenticated(HttpListenerContext context) {
      Cookie cookie = context.Request.Cookies["rf_session"];
      return cookie != null && store.IsSession(cookie.Value);
    }

    private bool CheckCsrf(Dictionary<string, string> form) {
      string token = form.ContainsKey("__csrf") ? form["__csrf"] : "";
      return store.CheckCsrf(token);
    }

    private string CsrfInput() {
      return "<input type='hidden' name='__csrf' value='" + A(store.CsrfToken) + "'>";
    }

    private Dictionary<string, string> ReadForm(HttpListenerContext context) {
      using (StreamReader reader = new StreamReader(context.Request.InputStream, context.Request.ContentEncoding == null ? Encoding.UTF8 : context.Request.ContentEncoding)) {
        string body = reader.ReadToEnd();
        Dictionary<string, string> form = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        string[] pairs = body.Split('&');
        foreach (string pair in pairs) {
          if (pair == "") continue;
          int equals = pair.IndexOf('=');
          string key = equals >= 0 ? pair.Substring(0, equals) : pair;
          string value = equals >= 0 ? pair.Substring(equals + 1) : "";
          form[UrlDecode(key)] = UrlDecode(value);
        }
        return form;
      }
    }

    private static string UrlDecode(string value) {
      return Uri.UnescapeDataString(value.Replace("+", " "));
    }

    private string Page(string title, string body) {
      string assetVersion = Uri.EscapeDataString(PortablePaths.Version);
      return "<!doctype html><html><head><meta charset='utf-8'><meta name='viewport' content='width=device-width, initial-scale=1'>" +
        "<title>" + H(title) + " - Racinage Free</title><link rel='stylesheet' href='/assets/ai-assistant.css?v=" + assetVersion + "'><style>" + Css() + CalendarCss() + ShareCss() + "</style></head><body>" +
        "<header id='header'><a class='brand' href='/'>Racinage Free</a><nav><a href='/'>Home</a><a href='/family'>Dashboard</a><a href='/messages'>Messages</a><a href='/calendar'>Calendar</a><a href='/manage'>Manage</a><a href='" + PortablePaths.PricingUrl + "'>Upgrade</a></nav></header>" +
        "<main>" + body + "</main><script>" + PluginLifecycleJs() + CalendarJs() + Js() + connected.Script(store.CsrfToken) + "</script><script src='/assets/ai-assistant.js?v=" + assetVersion + "'></script></body></html>";
    }

    private static string ErrorHtml(string error) {
      return error == "" ? "" : "<p class='error'>" + H(error) + "</p>";
    }

    private static string UpgradeModal() {
      return "<div id='upgradeModal' class='modal' hidden><div class='modalbox'><h2>Upgrade required</h2><p>You cannot share <span id='upgradeFeature'>this record</span> while using the local Lite Free plan.</p><div class='actions'><a class='button' href='" + PortablePaths.PricingUrl + "'>Upgrade</a><button type='button' class='button ghost' onclick='hideUpgrade()'>Close</button></div></div></div>";
    }

    private static string CalendarCss() {
      return @"
select{width:100%;border:1px solid #cad8dd;border-radius:8px;padding:10px 12px;font:inherit;background:#fbfdfd;color:var(--text)}
.calendar-head{display:flex;align-items:flex-end;justify-content:space-between;gap:20px}.calendar-head h1{margin:5px 0 8px;font-size:42px;color:var(--brand)}.calendar-head p{margin:0;color:var(--muted)}.calendar-view-tabs{display:flex;gap:5px;margin:22px 0 12px;padding:5px;border:1px solid var(--line);border-radius:10px;background:#fff;overflow:auto}.calendar-view-tabs a{min-height:42px;display:flex;align-items:center;padding:0 14px;border-radius:7px;color:var(--muted);font-weight:700}.calendar-view-tabs a.active{background:var(--brand);color:#fff}.calendar-controls,.calendar-title{display:flex;justify-content:space-between;align-items:center;gap:16px}.calendar-controls .actions{margin:0}.calendar-jump{width:180px}.calendar-filter-panel{margin-top:12px;border:1px solid var(--line);border-radius:10px;background:#fff}.calendar-filter-panel summary{display:flex;justify-content:space-between;align-items:center;min-height:44px;padding:10px 14px;cursor:pointer;font-weight:700}.calendar-filter-panel summary span,.calendar-filter-panel form>p{color:var(--muted);font-size:12px}.calendar-filter-panel form{display:grid;grid-template-columns:1fr 1.5fr;gap:14px;padding:14px;border-top:1px solid var(--line)}.calendar-filter-panel fieldset{display:flex;flex-wrap:wrap;gap:6px;margin:0;padding:0;border:0}.calendar-filter-panel legend{width:100%;margin-bottom:4px;font-size:12px;font-weight:700}.calendar-filter-panel label{display:flex;align-items:center;gap:6px;min-height:38px;padding:6px 9px;border:1px solid var(--line);border-radius:8px}.calendar-filter-panel form>p{grid-column:1/-1;margin:0}.calendar-title{margin:22px 0 10px}.calendar-title h2{margin:0;color:var(--brand)}.calendar-title span{font-size:13px;color:var(--muted)}
.calendar-grid{display:grid;grid-template-columns:repeat(7,minmax(0,1fr));border:1px solid var(--line);border-radius:12px;overflow:hidden;background:#fff}.calendar-day{min-width:0;min-height:142px;padding:8px;border-right:1px solid var(--line);border-bottom:1px solid var(--line)}.calendar-day:nth-child(7n){border-right:0}.calendar-day header{display:flex;justify-content:space-between;align-items:center;margin-bottom:7px}.calendar-day header a{display:flex;gap:6px;align-items:center;min-height:36px;color:var(--muted);font-size:12px}.calendar-day header strong{font-size:15px;color:var(--brand)}.calendar-day header button{width:36px;height:36px;border:1px solid var(--line);border-radius:7px;background:#fff;color:var(--brand);cursor:pointer}.calendar-day.is-today{background:#f4fbf8}.calendar-day.is-today header strong{display:grid;place-items:center;width:30px;height:30px;border-radius:50%;background:var(--brand);color:#fff}
.calendar-entry{display:grid;gap:2px;min-height:34px;margin:4px 0;padding:5px 7px;border-left:3px solid var(--entry-color);border-radius:5px;background:#f5f9fa;overflow:hidden}.calendar-entry span{font-size:10px;color:var(--muted)}.calendar-entry strong{font-size:11px;color:var(--brand);white-space:nowrap;overflow:hidden;text-overflow:ellipsis}.calendar-empty-day{font-size:11px;color:#a0abad}.calendar-year{display:grid;grid-template-columns:repeat(4,minmax(0,1fr));gap:12px}.calendar-year a{display:grid;gap:8px;min-height:105px;padding:18px;border:1px solid var(--line);border-radius:10px;background:#fff}.calendar-year strong{color:var(--brand)}.calendar-year span{color:var(--muted);font-size:13px}
.calendar-agenda{display:grid;gap:14px}.calendar-agenda section{display:grid;grid-template-columns:210px minmax(0,1fr);gap:16px;padding:14px;border:1px solid var(--line);border-radius:10px;background:#fff}.calendar-agenda time{font-weight:700;color:var(--brand)}.calendar-empty{padding:40px;text-align:center;border:1px dashed var(--line);border-radius:12px;color:var(--muted)}.calendar-editor{width:min(620px,100%);max-height:calc(100vh - 48px);overflow:auto}.calendar-editor-row{display:grid;grid-template-columns:1fr 1fr;gap:10px}.calendar-ics{margin-top:22px}.calendar-ics-preview{display:flex;align-items:center;gap:12px;flex-wrap:wrap;margin:14px 0;padding:12px;border:1px solid var(--line);border-radius:9px;background:var(--pale)}.calendar-ics-preview form{display:block}.calendar-ics-preview span{color:var(--muted);font-size:13px}
@media(max-width:760px){.calendar-head,.calendar-controls{display:block}.calendar-head .actions{margin-top:12px}.calendar-jump{width:100%;margin-top:12px}.calendar-filter-panel form{grid-template-columns:1fr}.calendar-grid{display:block}.calendar-day{min-height:92px;border-right:0}.calendar-year{grid-template-columns:repeat(2,minmax(0,1fr))}.calendar-agenda section{grid-template-columns:1fr}.calendar-editor-row{grid-template-columns:1fr}}
";
    }

    private static string CalendarJs() {
      return "var calendarReturnFocus=null,calendarFilterTimer=0;function calendarOpen(value){var modal=document.getElementById('calendarNew'),input=document.getElementById('calendarQuickDate');calendarReturnFocus=document.activeElement;if(value&&input)input.value=value;if(!modal)return;modal.hidden=false;var first=modal.querySelector('[name=title],input:not([type=hidden]),select,textarea,button');if(first)first.focus();}function calendarClose(){var modal=document.getElementById('calendarNew');if(modal)modal.hidden=true;if(calendarReturnFocus&&calendarReturnFocus.focus)calendarReturnFocus.focus();}function calendarQuickDate(value){calendarOpen(value);}document.addEventListener('click',function(event){if(event.target.closest('[data-calendar-open]'))calendarOpen('');if(event.target.closest('[data-calendar-close]'))calendarClose();});document.addEventListener('change',function(event){var form=event.target.closest('[data-calendar-filter-form]');if(!form)return;clearTimeout(calendarFilterTimer);calendarFilterTimer=setTimeout(function(){var sources=Array.prototype.map.call(form.querySelectorAll('[data-calendar-filter-source]:checked'),function(field){return field.value;}),kinds=Array.prototype.map.call(form.querySelectorAll('[data-calendar-filter-kind]:checked'),function(field){return field.value;});form.querySelector('[data-calendar-filter-json]').value=JSON.stringify({sources:sources.length?sources:['__none__'],kinds:kinds.length?kinds:['__none__']});if(form.requestSubmit)form.requestSubmit();else form.submit();},250);});document.addEventListener('keydown',function(event){var modal=document.getElementById('calendarNew');if(!modal||modal.hidden)return;if(event.key==='Escape'){event.preventDefault();calendarClose();return;}if(event.key!=='Tab')return;var focusable=Array.prototype.slice.call(modal.querySelectorAll('button:not([disabled]),input:not([type=hidden]):not([disabled]),select:not([disabled]),textarea:not([disabled]),a[href]')),first=focusable[0],last=focusable[focusable.length-1];if(!first)return;if(event.shiftKey&&document.activeElement===first){event.preventDefault();last.focus();}else if(!event.shiftKey&&document.activeElement===last){event.preventDefault();first.focus();}});";
    }

    private static string Css() {
      return @"
@font-face{font-family:Inter;src:url('/fonts/inter/InterVariable.woff2?v=" + Uri.EscapeDataString(PortablePaths.Version) + @"') format('woff2');font-weight:100 900;font-style:normal;font-display:swap}
@font-face{font-family:Inter;src:url('/fonts/inter/InterVariable-Italic.woff2?v=" + Uri.EscapeDataString(PortablePaths.Version) + @"') format('woff2');font-weight:100 900;font-style:italic;font-display:swap}
:root{--brand:#004650;--accent:#c35900;--pale:#f5fafd;--line:#dbe5ea;--text:#3d4b4c;--muted:#6d7c7d}
*{box-sizing:border-box}body{margin:0;font-family:Inter,Segoe UI,Tahoma,sans-serif;background:#f8fbfc;color:var(--text)}a{color:#007584;text-decoration:none}header{height:58px;display:flex;align-items:center;justify-content:space-between;padding:0 28px;border-bottom:1px solid var(--line);background:#fff;position:sticky;top:0;z-index:5}.brand{font-weight:800;color:var(--brand)}nav{display:flex;gap:16px;align-items:center}nav a{font-size:14px;font-weight:600;color:var(--muted)}main{max-width:1120px;margin:0 auto;padding:34px 24px 70px}.hero{min-height:360px;display:grid;align-items:center;border-bottom:1px solid var(--line)}.hero h1,.dashhead h1,.manage-head h1{font-size:48px;line-height:1.02;margin:6px 0 16px;color:var(--brand)}.hero p,.dashhead p,.manage-head p,.note{font-size:17px;line-height:1.6;max-width:760px;color:var(--muted)}.kicker{font-size:12px!important;text-transform:uppercase;letter-spacing:.14em;color:var(--accent)!important;font-weight:800;margin:0}.actions{display:flex;align-items:center;gap:10px;flex-wrap:wrap;margin-top:20px}.actions form{display:block}.button{display:inline-flex;align-items:center;justify-content:center;min-height:42px;padding:0 18px;border-radius:8px;border:1px solid var(--brand);background:var(--brand);color:#fff;font:700 14px/1 inherit;cursor:pointer}.button.ghost{background:transparent;color:var(--brand);border-color:#9ab2b8}.grid,.manage-grid{display:grid;grid-template-columns:repeat(auto-fit,minmax(240px,1fr));gap:16px;margin-top:28px}.grid article,.panel,.manage-card{background:#fff;border:1px solid var(--line);border-radius:12px;padding:24px}.grid h2,.panel h2,.manage-card h2{margin:0 0 10px;color:var(--brand)}.grid p,.panel p,.manage-card p{line-height:1.55;color:var(--muted)}.narrow{max-width:460px;margin:30px auto}.wide{margin-top:18px}.layout{display:grid;grid-template-columns:1fr 1fr;gap:18px}.dashhead{display:flex;align-items:flex-end;justify-content:space-between;gap:20px;margin-bottom:22px}form{display:grid;gap:12px}label{display:grid;gap:6px;font-size:13px;font-weight:700;color:var(--brand)}input,textarea{width:100%;border:1px solid #cad8dd;border-radius:8px;padding:10px 12px;font:inherit;background:#fbfdfd;color:var(--text)}textarea{resize:vertical}.error{border:1px solid #efb5b5;background:#fff2f2;color:#9b2525;border-radius:8px;padding:10px 12px}.sharebar{display:flex;flex-wrap:wrap;gap:8px;margin:12px 0 18px}.share{border:1px solid #cddbe0;background:#f4faff;border-radius:8px;padding:8px 11px;cursor:pointer;font-weight:700;color:var(--brand)}.people{display:grid;grid-template-columns:repeat(auto-fit,minmax(240px,1fr));gap:12px}.people article{border:1px solid var(--line);border-radius:10px;padding:14px;background:#fbfdfd}.people strong{display:block;color:var(--brand)}.people span{display:block;color:var(--accent);font-size:12px;font-weight:800;text-transform:uppercase;margin-top:3px}.people p{margin:8px 0 0;font-size:14px}.textbtn{border:0;background:transparent;color:#b93333;padding:8px 0 0;cursor:pointer;font-weight:700}.empty{margin:0}.modal{position:fixed;inset:0;background:rgba(5,21,25,.55);display:grid;place-items:center;padding:24px;z-index:20}.modal[hidden]{display:none}.modalbox{width:min(440px,100%);background:#fff;border-radius:12px;padding:24px;border:1px solid var(--line)}.modalbox h2{margin:0 0 10px;color:var(--brand)}.panelhead,.manage-card-head{display:flex;justify-content:space-between;gap:16px;align-items:center}.panelhead h2,.panelhead p,.manage-card-head h2,.manage-card-head p{margin:0}.manage-head{margin-bottom:22px}.manage-tabs{display:flex;gap:6px;overflow:auto;padding:5px;border:1px solid var(--line);border-radius:10px;background:#fff}.manage-tabs a{min-height:42px;display:inline-flex;align-items:center;padding:0 16px;border-radius:7px;font-weight:700;color:var(--muted)}.manage-tabs a.active{color:#fff;background:var(--brand)}.manage-content{margin-top:18px}.manage-grid{margin-top:0}.facts{display:grid;grid-template-columns:repeat(auto-fit,minmax(180px,1fr));gap:10px;margin:18px 0 0}.facts div{padding:12px;border:1px solid var(--line);border-radius:9px}.facts dt{font-size:12px;color:var(--muted)}.facts dd{margin:5px 0 0;color:var(--brand);font-weight:700}.plugin-grid{display:grid;grid-template-columns:repeat(auto-fit,minmax(250px,1fr));gap:12px;margin-top:18px}.plugin-card{display:flex;flex-direction:column;min-height:260px;padding:16px;border:1px solid var(--line);border-radius:10px;background:#fbfdfd}.plugin-card-top{display:flex;gap:11px;align-items:center}.plugin-card h3{margin:0;color:var(--brand)}.plugin-card>p{flex:1}.plugin-mark{width:44px;height:44px;display:grid;place-items:center;border-radius:9px;color:#fff;background:var(--brand);font-size:20px;font-weight:800}.plugin-meta{margin:4px 0 0!important;font-size:12px}.notice{padding:10px;border-left:3px solid var(--accent);background:#fff8ef;font-size:13px}.status-pill{padding:7px 10px;border-radius:999px;color:var(--brand);background:#e9f3ef;font-size:12px;font-weight:800}.plugin-frame{width:100%;min-height:620px;border:1px solid var(--line);border-radius:12px;background:#fff}.connected-status{display:flex;justify-content:space-between;align-items:center;gap:20px;margin-bottom:16px}.connected-status h2,.connected-status p{margin:0}.connected-layout{display:grid;grid-template-columns:280px minmax(0,1fr);gap:16px}.connected-layout aside{display:grid;align-content:start;gap:6px;padding:10px}.connected-conversation{display:grid;gap:3px;padding:12px;border:1px solid transparent;border-radius:8px;color:var(--brand)}.connected-conversation span{font-size:12px;color:var(--muted)}.connected-conversation.active{border-color:var(--line);background:var(--pale)}.connected-thread{display:grid;gap:18px}.connected-thread>div{display:grid;gap:8px;max-height:520px;overflow:auto}.connected-message{padding:12px;border:1px solid var(--line);border-radius:9px;background:#fbfdfd}.connected-message>div{display:flex;justify-content:space-between;gap:12px}.connected-message time{font-size:12px;color:var(--muted)}.connected-message p{margin:8px 0 0;white-space:normal}.connected-attachments{margin:8px 0 0;padding-left:20px;font-size:12px;color:var(--muted)}.connected-composer{padding-top:16px;border-top:1px solid var(--line)}@media(max-width:760px){header{padding:0 16px}nav{gap:10px}.hero h1,.dashhead h1,.manage-head h1{font-size:36px}.layout,.connected-layout{grid-template-columns:1fr}.dashhead,.manage-card-head,.connected-status{display:block}.manage-card-head .button,.manage-card-head .status-pill,.connected-status .actions{margin-top:12px}}";
    }

    private string Js() {
      return "function showUpgrade(feature){var m=document.getElementById('upgradeModal');document.getElementById('upgradeFeature').textContent=feature;m.hidden=false;}function hideUpgrade(){document.getElementById('upgradeModal').hidden=true;}document.addEventListener('keydown',function(e){if(e.key==='Escape')hideUpgrade();});document.addEventListener('click',function(e){var p=e.target.closest&&e.target.closest('input[type=date],input[type=datetime-local],input[type=time],input[type=month],input[type=year]');if(!p||p.disabled||p.readOnly||typeof p.showPicker!=='function')return;try{p.showPicker();}catch(_){}});document.addEventListener('submit',async function(e){var f=e.target.closest&&e.target.closest('.plugin-action');if(!f)return;e.preventDefault();if(f.dataset.busy==='1')return;f.dataset.busy='1';try{var r=await fetch(f.action,{method:'POST',credentials:'same-origin',headers:{'X-Requested-With':'XMLHttpRequest'},body:new URLSearchParams(new FormData(f))}),j=await r.json();if(!r.ok||!j.ok)throw new Error(j.message||'Plugin action failed.');var c=document.querySelector('.manage-content');if(c)c.innerHTML=j.html;var x=document.querySelector('.error');if(x){x.textContent=j.message;x.className='feedback';}}catch(x){var c=document.querySelector('.manage-content');if(c)c.insertAdjacentHTML('afterbegin','<p class=\"error\"></p>');var p=document.querySelector('.manage-content .error');if(p)p.textContent=x.message;}finally{delete f.dataset.busy;}});window.addEventListener('message',async function(e){var f=document.querySelector('.plugin-frame[data-bridge-token]'),m=e.data,legacy=m&&m.financeBridge;if(!f||e.source!==f.contentWindow||!m||(!m.pluginBridge&&!legacy)||m.slug!==f.dataset.pluginSlug||m.bridgeToken!==f.dataset.bridgeToken)return;var reply={pluginBridgeResponse:true,financeBridgeResponse:true,bridgeToken:f.dataset.bridgeToken,requestId:m.requestId,ok:false};try{var r=await fetch('/local-plugin-api/'+encodeURIComponent(m.slug),{method:'POST',credentials:'same-origin',headers:{'Content-Type':'application/json','X-Racinage-CSRF':'" + A(store.CsrfToken) + "'},body:JSON.stringify({action:m.action,payload:m.payload||{}})}),j=await r.json();reply.ok=!!j.ok;reply.result=j.result;reply.message=j.message;}catch(_){reply.message='The local plugin service could not complete the request.';}f.contentWindow.postMessage(reply,'*');});";
    }

    private string PluginLifecycleJs() {
      string prompt=IsFrenchCulture()?"Supprimer aussi toutes les données locales de cette extension ? OK = supprimer, Annuler = conserver.":"Also delete all local data for this plugin? OK = delete, Cancel = keep.";
      return "document.addEventListener('click',function(e){var b=e.target.closest&&e.target.closest('.plugin-action button'),f=b&&b.form,a=f&&f.querySelector('input[name=action]');if(!a||a.value!=='uninstall_plugin')return;var old=f.querySelector('input[name=delete_data]');if(old)old.remove();if(confirm('"+A(prompt)+"')){var h=document.createElement('input');h.type='hidden';h.name='delete_data';h.value='yes';f.appendChild(h);}});";
    }

    private static void WriteHtml(HttpListenerContext context, string html) {
      WriteHtml(context, html, 200);
    }

    private static void WriteHtml(HttpListenerContext context, string html, int status) {
      byte[] bytes = Encoding.UTF8.GetBytes(html);
      context.Response.StatusCode = status;
      context.Response.ContentType = "text/html; charset=utf-8";
      context.Response.Headers["Cache-Control"] = "no-store, no-cache, must-revalidate, max-age=0";
      context.Response.Headers["Pragma"] = "no-cache";
      context.Response.ContentLength64 = bytes.Length;
      context.Response.OutputStream.Write(bytes, 0, bytes.Length);
      context.Response.Close();
    }

    private static void WriteFile(HttpListenerContext context, string relativePath, string contentType) {
      string root = AppDomain.CurrentDomain.BaseDirectory;
      string path = Path.GetFullPath(Path.Combine(root, relativePath));
      if (!path.StartsWith(root, StringComparison.OrdinalIgnoreCase) || !File.Exists(path)) { context.Response.StatusCode = 404; context.Response.Close(); return; }
      FileInfo file = new FileInfo(path);
      string etag = "\"" + file.LastWriteTimeUtc.Ticks.ToString("x", CultureInfo.InvariantCulture) + "-" + file.Length.ToString("x", CultureInfo.InvariantCulture) + "\"";
      bool immutable = String.Equals(context.Request.QueryString["v"], PortablePaths.Version, StringComparison.Ordinal);
      context.Response.ContentType = contentType;
      context.Response.Headers["Cache-Control"] = immutable
        ? "public, max-age=31536000, immutable"
        : "public, max-age=0, must-revalidate";
      context.Response.Headers["ETag"] = etag;
      context.Response.Headers["Last-Modified"] = file.LastWriteTimeUtc.ToString("R", CultureInfo.InvariantCulture);
      if (String.Equals(context.Request.Headers["If-None-Match"], etag, StringComparison.Ordinal)) {
        context.Response.StatusCode = 304;
        context.Response.Close();
        return;
      }
      byte[] bytes = File.ReadAllBytes(path);
      context.Response.ContentLength64 = bytes.Length;
      context.Response.OutputStream.Write(bytes, 0, bytes.Length);
      context.Response.Close();
    }

    private static void WriteJson(HttpListenerContext context, string json) {
      WriteJson(context, json, 200);
    }

    private static void WriteJson(HttpListenerContext context, string json, int status) {
      byte[] bytes = Encoding.UTF8.GetBytes(json);
      context.Response.StatusCode = status;
      context.Response.ContentType = "application/json; charset=utf-8";
      context.Response.Headers["Cache-Control"] = "no-store, no-cache, must-revalidate, max-age=0";
      context.Response.Headers["Pragma"] = "no-cache";
      context.Response.ContentLength64 = bytes.Length;
      context.Response.OutputStream.Write(bytes, 0, bytes.Length);
      context.Response.Close();
    }

    private static void Redirect(HttpListenerContext context, string target) {
      context.Response.StatusCode = 302;
      context.Response.RedirectLocation = target;
      context.Response.Headers["Cache-Control"] = "no-store, no-cache, must-revalidate, max-age=0";
      context.Response.Close();
    }

    private static void SetSessionCookie(HttpListenerContext context, string token) {
      context.Response.Headers.Add("Set-Cookie", "rf_session=" + token + "; Path=/; HttpOnly; SameSite=Lax");
    }

    private static void ExpireCookie(HttpListenerContext context) {
      context.Response.Headers.Add("Set-Cookie", "rf_session=; Path=/; HttpOnly; SameSite=Lax; Expires=Thu, 01 Jan 1970 00:00:00 GMT");
    }

    private static string H(string value) {
      if (value == null) return "";
      return value.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;").Replace("'", "&#39;");
    }

    private static string A(string value) {
      return H(value);
    }

    private static string RandomToken(int bytes) {
      byte[] value = new byte[bytes];
      using (RNGCryptoServiceProvider random = new RNGCryptoServiceProvider()) random.GetBytes(value);
      return BitConverter.ToString(value).Replace("-", "").ToLowerInvariant();
    }
  }

  internal sealed class PortableCatalogEnvelope { public string payload_base64; public string signature; public string algorithm; public string key_id; }
  internal sealed class PortableCatalogPayload { public string expires_at; public List<PortablePluginInfo> plugins; }
  internal sealed class PortableLocalSupport { public bool supported; public string reason; public string root; public string entrypoint; public string[] operations; }
  internal sealed class PortablePluginInfo {
    public string slug; public string name; public string name_fr; public string summary; public string summary_fr; public string description; public string description_fr; public string pricing_type; public int price_cents; public int? effective_price_cents; public string billing_interval; public string promotion_label; public string promotion_expires_at;
    public string currency; public string version; public string checksum_sha256; public string download_url; public string purchase_url; public PortableLocalSupport local; public PortableShareActions share_actions;
  }

  internal sealed class PluginCatalogClient {
    private const string PublicModulus = "5TSVXa+zoT6DaI2fxjDs6hBH9bDfZto00mLwUZr+RQaeTtbIxTb6Oh0+SkXsfI7dT0TunF/Js1hT9AaIf/Ug5ZKyR/Y/Axj3I49u16pu7WZEzTZsH4JapECd+NeH1aAlqxN+witHy6+ZqPLLW1EqfWKPZGEej7s/5BsVXqJ/kOCY8b7p2UzFUrWUoND18MzVKbyyQ0kfPjrEbioPqmpbmp0l4MjxP0Q5761bI1i9ISjbOIyBhF9AaYF0Ev8BF4c21xitDCc0Cqx5Nbyk2HZi5HQPqWCNSl3zsgUJCPh8TuQ68Km5PVPj9NTPZTrLftoHRJzO/FRJmHN2FZNN3tcv4FWO5WndjGYqYtA2KafhQPWTNUzCRRQevnEgQms5qLkbwHrpjh4nqI4gpGVMkhXBWpi0etxWyAHVshJ1FNtnYSrdXAm6IwvYOs0DGZ/gL6g/P//VLLLq59CcZtM/2zxtaMdg7iD0AexEm29FX1DO6SY33vk5iYXlISbkoLf3r5w7";
    private const string PublicExponent = "AQAB";
    private readonly JavaScriptSerializer json = new JavaScriptSerializer { MaxJsonLength = 8 * 1024 * 1024 };
    private List<PortablePluginInfo> cache;
    private DateTime cacheUntil = DateTime.MinValue;

    internal static bool ValidSlug(string slug) {
      if (String.IsNullOrEmpty(slug) || slug.Length < 3 || slug.Length > 80) return false;
      foreach (char c in slug) if (!(c >= 'a' && c <= 'z') && !(c >= '0' && c <= '9') && c != '-' && c != '_') return false;
      return true;
    }

    internal List<PortablePluginInfo> GetPlugins() {
      if (cache != null && cacheUntil > DateTime.UtcNow) return cache;
      ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
      string response;
      using (WebClient client = new WebClient()) {
        client.Headers[HttpRequestHeader.UserAgent] = "RacinageFreePortable/" + PortablePaths.Version;
        response = client.DownloadString(PortablePaths.PluginCatalogUrl);
      }
      PortableCatalogEnvelope envelope = json.Deserialize<PortableCatalogEnvelope>(response);
      if (envelope == null || envelope.algorithm != "RSA-SHA256" || envelope.key_id != "racinage-plugins-2026-01") throw new InvalidDataException("The plugin catalog signature metadata is invalid.");
      byte[] payload = Convert.FromBase64String(envelope.payload_base64 ?? "");
      byte[] signature = Convert.FromBase64String(envelope.signature ?? "");
      using (RSACryptoServiceProvider rsa = new RSACryptoServiceProvider()) {
        rsa.ImportParameters(new RSAParameters { Modulus = Convert.FromBase64String(PublicModulus), Exponent = Convert.FromBase64String(PublicExponent) });
        if (!rsa.VerifyData(payload, CryptoConfig.MapNameToOID("SHA256"), signature)) throw new CryptographicException("The plugin catalog signature could not be verified.");
      }
      PortableCatalogPayload catalog = json.Deserialize<PortableCatalogPayload>(Encoding.UTF8.GetString(payload));
      DateTime expires;
      if (catalog == null || !DateTime.TryParse(catalog.expires_at, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal, out expires) || expires.ToUniversalTime() <= DateTime.UtcNow) throw new InvalidDataException("The plugin catalog has expired.");
      cache = catalog.plugins ?? new List<PortablePluginInfo>();
      cacheUntil = DateTime.UtcNow.AddMinutes(5);
      return cache;
    }

    internal string Install(string slug, LocalStore store) {
      if (!ValidSlug(slug)) return "Invalid plugin selection.";
      PortablePluginInfo plugin = GetPlugins().Find(delegate(PortablePluginInfo item) { return item.slug == slug; });
      if (plugin == null || plugin.local == null || !plugin.local.supported) return "This plugin does not have a reviewed local runtime.";
      Uri uri;
      if (!Uri.TryCreate(plugin.download_url, UriKind.Absolute, out uri) || uri.Scheme != "https" || !uri.Host.Equals("plugins.racinage.com", StringComparison.OrdinalIgnoreCase)) return "The plugin download address is not trusted.";
      byte[] bundle;
      using (WebClient client = new WebClient()) {
        client.Headers[HttpRequestHeader.UserAgent] = "RacinageFreePortable/" + PortablePaths.Version;
        client.Headers["X-Racinage-Download-Client"] = store.DownloadClientToken;
        bundle = client.DownloadData(uri);
      }
      if (bundle.Length < 1 || bundle.Length > 25 * 1024 * 1024) return "The plugin bundle exceeds the local size limit.";
      string checksum;
      using (SHA256 sha = SHA256.Create()) checksum = BitConverter.ToString(sha.ComputeHash(bundle)).Replace("-", "").ToLowerInvariant();
      if (String.IsNullOrEmpty(plugin.checksum_sha256) || !FixedHexEquals(checksum, plugin.checksum_sha256.ToLowerInvariant())) return "The plugin checksum did not match the reviewed catalog.";
      string localRoot = NormalizeRelative(plugin.local.root);
      string localEntrypoint = NormalizeRelative(plugin.local.entrypoint);
      if (localRoot == "" || localEntrypoint == "" || !localEntrypoint.EndsWith(".html", StringComparison.OrdinalIgnoreCase)) return "The reviewed local entrypoint is invalid.";
      string version = String.IsNullOrEmpty(plugin.version) ? "current" : SafeSegment(plugin.version);
      string destination = Path.GetFullPath(Path.Combine(PortablePaths.PluginsDir, slug, version));
      string staging = destination + ".install-" + Guid.NewGuid().ToString("N");
      using (MemoryStream stream = new MemoryStream(bundle))
      using (ZipArchive zip = new ZipArchive(stream, ZipArchiveMode.Read)) {
        if (zip.Entries.Count < 1 || zip.Entries.Count > 2000) return "The plugin bundle contains an unsafe number of files.";
        long total = 0; bool entrypointFound = false; HashSet<string> files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string prefix = localRoot.TrimEnd('/') + "/";
        foreach (ZipArchiveEntry entry in zip.Entries) {
          string name = entry.FullName.Replace('\\', '/');
          total += entry.Length;
          if (name.StartsWith("/", StringComparison.Ordinal) || name.Contains("../") || total > 100L * 1024L * 1024L) return "The plugin bundle contains unsafe paths or expanded size.";
          if (name.EndsWith("/", StringComparison.Ordinal)) continue;
          if (!name.StartsWith(prefix, StringComparison.Ordinal)) return "The plugin package is not a production-only portable artifact.";
          string relative = NormalizeRelative(name.Substring(prefix.Length));
          if (relative == "" || !PortableProductionFile(relative) || !files.Add(relative)) return "The plugin package contains source, development, or duplicate files.";
          if (relative.Equals(localEntrypoint, StringComparison.OrdinalIgnoreCase)) entrypointFound = true;
        }
        if (!entrypointFound) return "The reviewed local entrypoint is missing from the bundle.";
        Directory.CreateDirectory(staging);
        foreach (ZipArchiveEntry entry in zip.Entries) {
          if (entry.FullName.EndsWith("/", StringComparison.Ordinal)) continue;
          string relative = NormalizeRelative(entry.FullName.Replace('\\', '/').Substring(prefix.Length));
          string output = Path.GetFullPath(Path.Combine(staging, relative.Replace('/', Path.DirectorySeparatorChar)));
          if (!output.StartsWith(staging + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) { Directory.Delete(staging, true); return "The plugin bundle tried to escape its folder."; }
          Directory.CreateDirectory(Path.GetDirectoryName(output));
          using (Stream input = entry.Open()) using (FileStream file = new FileStream(output, FileMode.CreateNew, FileAccess.Write, FileShare.None)) input.CopyTo(file);
        }
      }
      if (Directory.Exists(destination)) Directory.Delete(destination, true);
      Directory.Move(staging, destination);
      store.SavePluginInstall(plugin, version + "/" + localEntrypoint);
      return "Plugin installed. Lite constraints still apply, and Pro access remains tied to the publisher's hosted entitlement.";
    }

    private static string NormalizeRelative(string value) {
      value = (value ?? "").Replace('\\', '/').Trim('/');
      if (value == "" || value.Contains("..") || value.Contains(":")) return "";
      return value;
    }
    private static bool PortableProductionFile(string path) {
      string name = Path.GetFileName(path);
      if (name.StartsWith(".", StringComparison.Ordinal) || name.Equals("package.json", StringComparison.OrdinalIgnoreCase) || name.Equals("composer.json", StringComparison.OrdinalIgnoreCase)) return false;
      string extension = Path.GetExtension(name).ToLowerInvariant();
      return extension == ".html" || extension == ".css" || extension == ".js" || extension == ".json" || extension == ".wasm" || extension == ".svg" || extension == ".png" || extension == ".jpg" || extension == ".jpeg" || extension == ".webp" || extension == ".gif" || extension == ".ico" || extension == ".woff" || extension == ".woff2";
    }
    private static string SafeSegment(string value) { StringBuilder b = new StringBuilder(); foreach (char c in value) if (Char.IsLetterOrDigit(c) || c == '.' || c == '-' || c == '_') b.Append(c); return b.Length == 0 ? "current" : b.ToString(); }
    private static bool FixedHexEquals(string a, string b) { if (a == null || b == null || a.Length != b.Length) return false; int diff = 0; for (int i = 0; i < a.Length; i++) diff |= a[i] ^ b[i]; return diff == 0; }
  }

  internal sealed partial class LocalStore {
    private static readonly byte[] TokenEntropy = Encoding.UTF8.GetBytes("Racinage Free local token v1");
    private readonly JavaScriptSerializer json = new JavaScriptSerializer { MaxJsonLength = 64 * 1024 * 1024 };
    private readonly string dbPath = Path.Combine(PortablePaths.DataDir, "racinage-free.sqlite");
    private string deviceId;
    private string csrfToken;
    private string downloadClientToken;
    private string protectionNote = "pending";

    internal string DatabasePath { get { return dbPath; } }
    internal string DatabaseProtectionNote { get { return protectionNote; } }
    internal string DeviceId {
      get {
        if (deviceId == null) deviceId = ComputeDeviceId();
        return deviceId;
      }
    }
    internal string CsrfToken {
      get {
        if (csrfToken == null) csrfToken = GetOrCreateProtectedToken("csrf.token");
        return csrfToken;
      }
    }
    internal string DownloadClientToken {
      get {
        if (downloadClientToken == null) downloadClientToken = GetOrCreateProtectedToken("download-client.token");
        return downloadClientToken;
      }
    }

    internal void Initialize() {
      using (SqliteDb db = Open()) {
        db.Exec("PRAGMA journal_mode=WAL");
        db.Exec("CREATE TABLE IF NOT EXISTS users (id INTEGER PRIMARY KEY CHECK(id = 1), username TEXT NOT NULL UNIQUE, display_name TEXT NOT NULL, password_hash TEXT NOT NULL, created_at TEXT NOT NULL, updated_at TEXT NOT NULL)");
        db.Exec("CREATE TABLE IF NOT EXISTS families (id INTEGER PRIMARY KEY CHECK(id = 1), name TEXT NOT NULL, location TEXT NOT NULL DEFAULT '', story TEXT NOT NULL DEFAULT '', created_at TEXT NOT NULL, updated_at TEXT NOT NULL)");
        db.Exec("CREATE TABLE IF NOT EXISTS people (id INTEGER PRIMARY KEY AUTOINCREMENT, full_name TEXT NOT NULL, relationship TEXT NOT NULL DEFAULT '', birth_date TEXT NOT NULL DEFAULT '', place TEXT NOT NULL DEFAULT '', notes TEXT NOT NULL DEFAULT '', deleted_at TEXT NULL, created_at TEXT NOT NULL, updated_at TEXT NOT NULL)");
        db.Exec("CREATE TABLE IF NOT EXISTS sync_changes (id INTEGER PRIMARY KEY AUTOINCREMENT, table_name TEXT NOT NULL, primary_key TEXT NOT NULL, operation TEXT NOT NULL, changed_at TEXT NOT NULL, row_hash TEXT NOT NULL, origin_device TEXT NOT NULL)");
        db.Exec("CREATE TABLE IF NOT EXISTS media_baselines (relative_path TEXT PRIMARY KEY, sha256 TEXT NOT NULL, size INTEGER NOT NULL DEFAULT 0, updated_at TEXT NOT NULL)");
        db.Exec("CREATE TABLE IF NOT EXISTS media_deletes (relative_path TEXT PRIMARY KEY, deleted_at TEXT NOT NULL, origin_device TEXT NOT NULL)");
        db.Exec("CREATE TABLE IF NOT EXISTS plugin_installs (slug TEXT PRIMARY KEY, name TEXT NOT NULL, version TEXT NOT NULL, checksum_sha256 TEXT NOT NULL, entrypoint TEXT NOT NULL DEFAULT '', status TEXT NOT NULL DEFAULT 'enabled', installed_at TEXT NOT NULL, updated_at TEXT NOT NULL)");
        if (!HasColumn(db, "plugin_installs", "bridge_operations")) db.Exec("ALTER TABLE plugin_installs ADD COLUMN bridge_operations TEXT NOT NULL DEFAULT ''");
        if (!HasColumn(db, "plugin_installs", "share_actions_json")) db.Exec("ALTER TABLE plugin_installs ADD COLUMN share_actions_json TEXT NOT NULL DEFAULT ''");
        if (!HasColumn(db, "users", "display_currency")) db.Exec("ALTER TABLE users ADD COLUMN display_currency TEXT NOT NULL DEFAULT 'USD'");
        db.Exec("CREATE TABLE IF NOT EXISTS local_currency_rates (code TEXT PRIMARY KEY, name TEXT NOT NULL, rate REAL NOT NULL CHECK(rate > 0), updated_at TEXT NOT NULL)");
        db.Exec("INSERT OR IGNORE INTO local_currency_rates(code,name,rate,updated_at)VALUES('USD','United States Dollar',1,'" + Now() + "')");
        db.Exec("CREATE TABLE IF NOT EXISTS local_plugin_records (slug TEXT NOT NULL,record_type TEXT NOT NULL,long_id TEXT NOT NULL,workspace_long_id TEXT NOT NULL DEFAULT '',data_json TEXT NOT NULL,version INTEGER NOT NULL DEFAULT 1,status TEXT NOT NULL DEFAULT 'active',created_at TEXT NOT NULL,updated_at TEXT NOT NULL,PRIMARY KEY(slug,record_type,long_id))");
        db.Exec("CREATE INDEX IF NOT EXISTS idx_local_plugin_records ON local_plugin_records(slug,workspace_long_id,record_type,status)");
        db.Exec("CREATE TABLE IF NOT EXISTS local_plugin_attachments (slug TEXT NOT NULL,long_id TEXT NOT NULL,workspace_long_id TEXT NOT NULL,transaction_long_id TEXT NOT NULL,relative_path TEXT NOT NULL,original_name TEXT NOT NULL,mime_type TEXT NOT NULL,file_size INTEGER NOT NULL,version INTEGER NOT NULL DEFAULT 1,status TEXT NOT NULL DEFAULT 'active',created_at TEXT NOT NULL,updated_at TEXT NOT NULL,PRIMARY KEY(slug,long_id))");
        db.Exec("CREATE INDEX IF NOT EXISTS idx_local_plugin_attachments ON local_plugin_attachments(slug,transaction_long_id,status)");
        db.Exec("CREATE TABLE IF NOT EXISTS local_plugin_settings (slug TEXT NOT NULL,setting_key TEXT NOT NULL,setting_value TEXT NOT NULL,updated_at TEXT NOT NULL,PRIMARY KEY(slug,setting_key))");
        InitializeLocalShareSchema(db);
        db.Exec("CREATE TABLE IF NOT EXISTS local_calendar_items (long_id TEXT PRIMARY KEY,source_id TEXT NOT NULL DEFAULT 'core.calendar',source_opaque_id TEXT NOT NULL DEFAULT '',item_kind TEXT NOT NULL,title TEXT NOT NULL,start_utc TEXT NOT NULL DEFAULT '',end_utc TEXT NOT NULL DEFAULT '',date_value TEXT NOT NULL DEFAULT '',timezone TEXT NOT NULL DEFAULT 'UTC',all_day INTEGER NOT NULL DEFAULT 0,status TEXT NOT NULL DEFAULT 'planned',recurrence_json TEXT NOT NULL DEFAULT '',reminder_json TEXT NOT NULL DEFAULT '',color TEXT NOT NULL DEFAULT '#0f7370',notes TEXT NOT NULL DEFAULT '',revision INTEGER NOT NULL DEFAULT 1,created_at TEXT NOT NULL,updated_at TEXT NOT NULL)");
        db.Exec("CREATE INDEX IF NOT EXISTS idx_local_calendar_range ON local_calendar_items(status,date_value,start_utc)");
        db.Exec("CREATE UNIQUE INDEX IF NOT EXISTS uniq_local_calendar_source ON local_calendar_items(source_id,source_opaque_id) WHERE source_opaque_id<>''");
        db.Exec("CREATE TABLE IF NOT EXISTS local_calendar_exceptions (item_long_id TEXT NOT NULL,occurrence_key TEXT NOT NULL,action TEXT NOT NULL,override_json TEXT NOT NULL DEFAULT '',created_at TEXT NOT NULL,PRIMARY KEY(item_long_id,occurrence_key))");
        db.Exec("CREATE TABLE IF NOT EXISTS local_calendar_preferences (id INTEGER PRIMARY KEY CHECK(id=1),view_name TEXT NOT NULL DEFAULT 'month',anchor_date TEXT NOT NULL DEFAULT '',filters_json TEXT NOT NULL DEFAULT '{}',working_hours_json TEXT NOT NULL DEFAULT '{}',updated_at TEXT NOT NULL)");
        db.Exec("CREATE TABLE IF NOT EXISTS local_calendar_ics_feeds (long_id TEXT PRIMARY KEY,source_name TEXT NOT NULL,source_fingerprint TEXT NOT NULL,imported_at TEXT NOT NULL)");
        db.Exec("CREATE TABLE IF NOT EXISTS local_calendar_reminder_claims (item_long_id TEXT NOT NULL,occurrence_key TEXT NOT NULL,reminder_offset_minutes INTEGER NOT NULL,delivered_at TEXT NOT NULL,PRIMARY KEY(item_long_id,occurrence_key,reminder_offset_minutes))");
        db.Exec("CREATE TABLE IF NOT EXISTS local_ai_conversations (id INTEGER PRIMARY KEY AUTOINCREMENT,long_id TEXT NOT NULL UNIQUE,title_ciphertext TEXT NOT NULL,title_key_version INTEGER NOT NULL DEFAULT 1,is_pinned INTEGER NOT NULL DEFAULT 0,is_archived INTEGER NOT NULL DEFAULT 0,created_at TEXT NOT NULL,updated_at TEXT NOT NULL,deleted_at TEXT NULL)");
        db.Exec("CREATE INDEX IF NOT EXISTS idx_local_ai_conversations_state ON local_ai_conversations(is_archived,is_pinned,updated_at)");
        db.Exec("CREATE TABLE IF NOT EXISTS local_ai_messages (id INTEGER PRIMARY KEY AUTOINCREMENT,conversation_id INTEGER NOT NULL,role TEXT NOT NULL CHECK(role IN ('user','assistant','tool')),content_ciphertext TEXT NOT NULL,content_key_version INTEGER NOT NULL DEFAULT 1,created_at TEXT NOT NULL,deleted_at TEXT NULL,FOREIGN KEY(conversation_id) REFERENCES local_ai_conversations(id) ON DELETE CASCADE)");
        db.Exec("CREATE INDEX IF NOT EXISTS idx_local_ai_messages_conversation ON local_ai_messages(conversation_id,id)");
        db.Exec("CREATE TABLE IF NOT EXISTS local_ai_runs (id INTEGER PRIMARY KEY AUTOINCREMENT,long_id TEXT NOT NULL UNIQUE,conversation_id INTEGER NOT NULL,status TEXT NOT NULL CHECK(status IN ('queued','running','paused','completed','failed','cancelled')),provider_kind TEXT NOT NULL,model TEXT NOT NULL DEFAULT '',state_ciphertext TEXT NOT NULL,state_key_version INTEGER NOT NULL DEFAULT 1,correlation_id TEXT NOT NULL,created_at TEXT NOT NULL,updated_at TEXT NOT NULL,FOREIGN KEY(conversation_id) REFERENCES local_ai_conversations(id) ON DELETE CASCADE)");
        db.Exec("CREATE INDEX IF NOT EXISTS idx_local_ai_runs_state ON local_ai_runs(status,updated_at)");
        db.Exec("CREATE TABLE IF NOT EXISTS local_ai_tool_calls (id INTEGER PRIMARY KEY AUTOINCREMENT,run_id INTEGER NOT NULL,tool_name TEXT NOT NULL,tier INTEGER NOT NULL CHECK(tier BETWEEN 0 AND 3),arguments_ciphertext TEXT NOT NULL,result_ciphertext TEXT NOT NULL DEFAULT '',payload_key_version INTEGER NOT NULL DEFAULT 1,status TEXT NOT NULL,idempotency_key TEXT NOT NULL UNIQUE,created_at TEXT NOT NULL,updated_at TEXT NOT NULL,FOREIGN KEY(run_id) REFERENCES local_ai_runs(id) ON DELETE CASCADE)");
        db.Exec("CREATE INDEX IF NOT EXISTS idx_local_ai_tool_calls_run ON local_ai_tool_calls(run_id,id)");
        db.Exec("CREATE TABLE IF NOT EXISTS local_ai_usage_audits (id INTEGER PRIMARY KEY AUTOINCREMENT,correlation_id TEXT NOT NULL,provider_kind TEXT NOT NULL,model TEXT NOT NULL DEFAULT '',tool_name TEXT NOT NULL DEFAULT '',confirmation_outcome TEXT NOT NULL DEFAULT '',status TEXT NOT NULL,latency_ms INTEGER NOT NULL DEFAULT 0,input_tokens INTEGER NOT NULL DEFAULT 0,output_tokens INTEGER NOT NULL DEFAULT 0,created_at TEXT NOT NULL)");
        db.Exec("CREATE INDEX IF NOT EXISTS idx_local_ai_usage_audits_created ON local_ai_usage_audits(created_at)");
      }
      EnsureBuiltinFinanceManager();
      ProtectDatabaseFile();
      GetOrCreateProtectedToken("device.token");
    }

    internal Dictionary<string,string> CalendarPreferences() {
      using(SqliteDb db=Open()) {
        Dictionary<string,string> row=db.QueryOne("SELECT view_name,anchor_date,filters_json,working_hours_json FROM local_calendar_preferences WHERE id=1 LIMIT 1");
        if(row!=null)return row;
        return new Dictionary<string,string>{{"view_name","month"},{"anchor_date",DateTime.Today.ToString("yyyy-MM-dd",CultureInfo.InvariantCulture)},{"filters_json","{}"},{"working_hours_json","{}"}};
      }
    }

    internal void RememberCalendarView(string view,string anchor) {
      if(!new[]{"month","week","day","agenda","year"}.Contains(view)||!ValidDate(anchor))return;
      using(SqliteDb db=Open())db.Execute("INSERT INTO local_calendar_preferences(id,view_name,anchor_date,filters_json,working_hours_json,updated_at)VALUES(1,?,?,'{}','{}',?) ON CONFLICT(id) DO UPDATE SET view_name=excluded.view_name,anchor_date=excluded.anchor_date,updated_at=excluded.updated_at",view,anchor,Now());
    }

    internal void SaveCalendarPreferences(Dictionary<string,string> form) {
      string view=form.ContainsKey("view_name")?form["view_name"]:"month",anchor=form.ContainsKey("anchor_date")?form["anchor_date"]:DateTime.Today.ToString("yyyy-MM-dd",CultureInfo.InvariantCulture);
      if(!new[]{"month","week","day","agenda","year"}.Contains(view)||!ValidDate(anchor))throw new InvalidDataException("Choose valid Calendar preferences.");
      string filters=form.ContainsKey("filters_json")?form["filters_json"]:"{}",hours=form.ContainsKey("working_hours_json")?form["working_hours_json"]:"{}";
      if(filters.Length>20000||hours.Length>5000)throw new InvalidDataException("The Calendar preference is too large.");
      try{json.DeserializeObject(filters);json.DeserializeObject(hours);}catch{throw new InvalidDataException("The Calendar preference format is invalid.");}
      using(SqliteDb db=Open())db.Execute("INSERT INTO local_calendar_preferences(id,view_name,anchor_date,filters_json,working_hours_json,updated_at)VALUES(1,?,?,?,?,?) ON CONFLICT(id) DO UPDATE SET view_name=excluded.view_name,anchor_date=excluded.anchor_date,filters_json=excluded.filters_json,working_hours_json=excluded.working_hours_json,updated_at=excluded.updated_at",view,anchor,filters,hours,Now());
      ProtectDatabaseFile();
    }

    internal void SaveCalendarItem(Dictionary<string,string> form) {
      string longId=SafeLongId(form.ContainsKey("long_id")?form["long_id"]:""),title=(form.ContainsKey("title")?form["title"]:"").Trim(),kind=(form.ContainsKey("item_kind")?form["item_kind"]:"event").Trim(),date=form.ContainsKey("date_value")?form["date_value"]:"",start=form.ContainsKey("start_time")?form["start_time"]:"",end=form.ContainsKey("end_time")?form["end_time"]:"",frequency=form.ContainsKey("frequency")?form["frequency"]:"",reminder=form.ContainsKey("reminder_minutes")?form["reminder_minutes"]:"",notes=(form.ContainsKey("notes")?form["notes"]:"").Trim();
      if(title==""||title.Length>190)throw new InvalidDataException("Enter a Calendar title of 190 characters or fewer.");
      if(!new[]{"event","meeting","task","reminder"}.Contains(kind)||!ValidDate(date))throw new InvalidDataException("Choose a valid Calendar kind and date.");
      TimeSpan startTime=TimeSpan.Zero,endTime=TimeSpan.Zero;if(start!=""&&!TimeSpan.TryParseExact(start,"hh\\:mm",CultureInfo.InvariantCulture,out startTime))throw new InvalidDataException("Choose a valid start time.");if(end!=""&&!TimeSpan.TryParseExact(end,"hh\\:mm",CultureInfo.InvariantCulture,out endTime))throw new InvalidDataException("Choose a valid end time.");
      if(!new[]{"","daily","weekly","monthly","yearly"}.Contains(frequency))throw new InvalidDataException("Choose a supported recurrence.");int reminderMinutes;if(reminder!=""&&(!Int32.TryParse(reminder,out reminderMinutes)||!new[]{0,10,60,1440}.Contains(reminderMinutes)))throw new InvalidDataException("Choose a supported reminder.");
      if(notes.Length>2000)throw new InvalidDataException("Calendar notes must be 2,000 characters or fewer.");
      DateTime dateOnly=DateTime.ParseExact(date,"yyyy-MM-dd",CultureInfo.InvariantCulture),startUtc=DateTime.MinValue,endUtc=DateTime.MinValue;bool allDay=start=="";
      if(!allDay){startUtc=DateTime.SpecifyKind(dateOnly.Add(startTime),DateTimeKind.Local).ToUniversalTime();if(end!=""){endUtc=DateTime.SpecifyKind(dateOnly.Add(endTime),DateTimeKind.Local).ToUniversalTime();if(endUtc<startUtc)throw new InvalidDataException("The end time cannot be before the start time.");}}
      string recurrence=frequency==""?"":json.Serialize(new Dictionary<string,object>{{"frequency",frequency},{"interval",1}}),reminderJson=reminder==""?"":json.Serialize(new Dictionary<string,object>{{"minutes_before",Convert.ToInt32(reminder,CultureInfo.InvariantCulture)}}),now=Now();
      using(SqliteDb db=Open()){
        if(longId==""){longId=NewLongId("calendar_items");db.Execute("INSERT INTO local_calendar_items(long_id,source_id,source_opaque_id,item_kind,title,start_utc,end_utc,date_value,timezone,all_day,status,recurrence_json,reminder_json,color,notes,revision,created_at,updated_at)VALUES(?,'core.calendar','',?,?,?,?,?,?,?,'planned',?,?,'#0f7370',?,1,?,?)",longId,kind,title,startUtc==DateTime.MinValue?"":startUtc.ToString("o",CultureInfo.InvariantCulture),endUtc==DateTime.MinValue?"":endUtc.ToString("o",CultureInfo.InvariantCulture),date,TimeZoneInfo.Local.Id,allDay?1:0,recurrence,reminderJson,notes,now,now);}
        else {int revision=form.ContainsKey("revision")?ToInt(form["revision"]):0;Dictionary<string,string> current=db.QueryOne("SELECT revision FROM local_calendar_items WHERE long_id=? AND source_id='core.calendar' AND status!='deleted' LIMIT 1",longId);if(current==null)throw new InvalidDataException("The Calendar item is unavailable.");if(revision>0&&revision!=ToInt(current["revision"]))throw new InvalidOperationException("This Calendar item changed in another window. Reopen it and try again.");db.Execute("UPDATE local_calendar_items SET item_kind=?,title=?,start_utc=?,end_utc=?,date_value=?,timezone=?,all_day=?,recurrence_json=?,reminder_json=?,notes=?,revision=revision+1,updated_at=? WHERE long_id=? AND source_id='core.calendar'",kind,title,startUtc==DateTime.MinValue?"":startUtc.ToString("o",CultureInfo.InvariantCulture),endUtc==DateTime.MinValue?"":endUtc.ToString("o",CultureInfo.InvariantCulture),date,TimeZoneInfo.Local.Id,allDay?1:0,recurrence,reminderJson,notes,now,longId);}
      }
      ProtectDatabaseFile();
    }

    internal void DeleteCalendarItem(string longId,string revisionValue) {
      longId=SafeLongId(longId);int revision=ToInt(revisionValue);if(longId=="")throw new InvalidDataException("The Calendar item is unavailable.");
      using(SqliteDb db=Open()){Dictionary<string,string> current=db.QueryOne("SELECT revision FROM local_calendar_items WHERE long_id=? AND source_id='core.calendar' AND status!='deleted' LIMIT 1",longId);if(current==null)throw new InvalidDataException("The Calendar item is unavailable.");if(revision>0&&revision!=ToInt(current["revision"]))throw new InvalidOperationException("This Calendar item changed in another window. Reopen it and try again.");db.Execute("UPDATE local_calendar_items SET status='deleted',revision=revision+1,updated_at=? WHERE long_id=?",Now(),longId);}
      ProtectDatabaseFile();
    }

    internal List<Dictionary<string,string> > CalendarEntries(DateTime start,DateTime end) {
      List<Dictionary<string,string> > result=new List<Dictionary<string,string> >();
      using(SqliteDb db=Open()){
        Dictionary<string,HashSet<string> > skipped=new Dictionary<string,HashSet<string> >();foreach(Dictionary<string,string> exception in db.Query("SELECT item_long_id,occurrence_key FROM local_calendar_exceptions WHERE action='skip'")){if(!skipped.ContainsKey(exception["item_long_id"]))skipped[exception["item_long_id"]]=new HashSet<string>();skipped[exception["item_long_id"]].Add(exception["occurrence_key"]);}
        foreach(Dictionary<string,string> row in db.Query("SELECT long_id,source_id,source_opaque_id,item_kind,title,start_utc,end_utc,date_value,timezone,all_day,status,recurrence_json,reminder_json,color,notes,revision FROM local_calendar_items WHERE status!='deleted' ORDER BY date_value,start_utc LIMIT 5000"))ExpandCalendarRow(row,start,end,skipped.ContainsKey(row["long_id"])?skipped[row["long_id"]]:new HashSet<string>(),result);
        foreach(Dictionary<string,string> row in db.Query("SELECT slug,record_type,long_id,workspace_long_id,data_json,version FROM local_plugin_records WHERE slug IN('finance-manager','kitchen-planner') AND status='active' ORDER BY updated_at DESC LIMIT 5000"))ProjectPortableRecord(row,start,end,result);
      }
      return result.Where(item=>CalendarRecordDate(item)>=start&&CalendarRecordDate(item)<end).OrderBy(CalendarRecordDate).ThenBy(item=>item.ContainsKey("title")?item["title"]:"").Take(5000).ToList();
    }

    internal List<Dictionary<string,string> > ClaimDueCalendarReminders(DateTime localNow) {
      DateTime windowStart=localNow.AddDays(-1),windowEnd=localNow.AddDays(2);List<Dictionary<string,string> > claimed=new List<Dictionary<string,string> >();
      foreach(Dictionary<string,string> item in CalendarEntries(windowStart,windowEnd)){
        if(!item.ContainsKey("source_id")||item["source_id"]!="core.calendar"||!item.ContainsKey("reminder_json")||item["reminder_json"]=="")continue;
        int offset;try{Dictionary<string,object> reminder=json.DeserializeObject(item["reminder_json"]) as Dictionary<string,object>;offset=reminder==null?Int32.MinValue:ToInt(reminder.ContainsKey("minutes_before")?reminder["minutes_before"]:null);}catch{continue;}if(!new[]{0,10,60,1440}.Contains(offset))continue;
        DateTime occurrence=CalendarRecordDate(item);if(occurrence==DateTime.MinValue)continue;if(item.ContainsKey("all_day")&&item["all_day"]=="1")occurrence=occurrence.Date.AddHours(9);
        DateTime due=occurrence.AddMinutes(-offset);if(localNow<due||localNow>due.AddMinutes(10))continue;
        string longId=item.ContainsKey("long_id")?SafeLongId(item["long_id"]):"",occurrenceKey=item.ContainsKey("recurrence_reference")&&item["recurrence_reference"]!=""?item["recurrence_reference"]:longId+":"+occurrence.ToString("yyyy-MM-ddTHH:mm",CultureInfo.InvariantCulture);if(longId=="")continue;
        using(SqliteDb db=Open())if(db.Execute("INSERT OR IGNORE INTO local_calendar_reminder_claims(item_long_id,occurrence_key,reminder_offset_minutes,delivered_at)VALUES(?,?,?,?)",longId,occurrenceKey,offset,Now())>0)claimed.Add(new Dictionary<string,string>{{"title",item.ContainsKey("title")?item["title"]:"Calendar reminder"},{"occurrence_at",occurrence.ToString("dd/MM/yyyy HH:mm",CultureInfo.CurrentCulture)},{"long_id",longId}});
      }
      if(claimed.Count>0)ProtectDatabaseFile();return claimed;
    }

    private void ExpandCalendarRow(Dictionary<string,string> row,DateTime rangeStart,DateTime rangeEnd,HashSet<string> skipped,List<Dictionary<string,string> > result) {
      DateTime baseDate=CalendarRecordDate(row);if(baseDate==DateTime.MinValue)return;string frequency="";
      if(row.ContainsKey("recurrence_json")&&row["recurrence_json"]!=""){try{Dictionary<string,object> recurrence=json.DeserializeObject(row["recurrence_json"]) as Dictionary<string,object>;if(recurrence!=null)frequency=GetString(recurrence,"frequency");}catch{}}
      if(frequency==""){if(baseDate>=rangeStart&&baseDate<rangeEnd&&!skipped.Contains(baseDate.ToString("yyyy-MM-dd",CultureInfo.InvariantCulture)))result.Add(new Dictionary<string,string>(row,StringComparer.OrdinalIgnoreCase));return;}
      DateTime cursor=baseDate;int guard=0;while(cursor<rangeStart&&guard++<5000)cursor=NextCalendarOccurrence(cursor,frequency);while(cursor<rangeEnd&&guard++<10000){string key=cursor.ToString("yyyy-MM-dd",CultureInfo.InvariantCulture);if(!skipped.Contains(key)){Dictionary<string,string> occurrence=new Dictionary<string,string>(row,StringComparer.OrdinalIgnoreCase);occurrence["recurrence_reference"]=row["long_id"]+":"+key;if(row["all_day"]=="1")occurrence["date_value"]=key;else{DateTime startUtc;DateTime.TryParse(row["start_utc"],CultureInfo.InvariantCulture,DateTimeStyles.AdjustToUniversal|DateTimeStyles.AssumeUniversal,out startUtc);DateTime endUtc;DateTime.TryParse(row["end_utc"],CultureInfo.InvariantCulture,DateTimeStyles.AdjustToUniversal|DateTimeStyles.AssumeUniversal,out endUtc);TimeSpan duration=endUtc>startUtc?endUtc-startUtc:TimeSpan.Zero;DateTime occurrenceUtc=DateTime.SpecifyKind(cursor,DateTimeKind.Local).ToUniversalTime();occurrence["start_utc"]=occurrenceUtc.ToString("o",CultureInfo.InvariantCulture);occurrence["end_utc"]=duration>TimeSpan.Zero?occurrenceUtc.Add(duration).ToString("o",CultureInfo.InvariantCulture):"";occurrence["date_value"]=cursor.ToString("yyyy-MM-dd",CultureInfo.InvariantCulture);}result.Add(occurrence);}cursor=NextCalendarOccurrence(cursor,frequency);}
    }

    private static DateTime NextCalendarOccurrence(DateTime value,string frequency) { if(frequency=="daily")return value.AddDays(1);if(frequency=="weekly")return value.AddDays(7);if(frequency=="monthly")return value.AddMonths(1);if(frequency=="yearly")return value.AddYears(1);return DateTime.MaxValue; }
    private static DateTime CalendarRecordDate(Dictionary<string,string> row) { DateTime parsed;bool allDay=row.ContainsKey("all_day")&&row["all_day"]=="1";string raw=allDay&&row.ContainsKey("date_value")?row["date_value"]:(row.ContainsKey("start_utc")&&row["start_utc"]!=""?row["start_utc"]:row.ContainsKey("date_value")?row["date_value"]:"");if(!DateTime.TryParse(raw,CultureInfo.InvariantCulture,DateTimeStyles.AssumeUniversal|DateTimeStyles.AdjustToUniversal,out parsed))return DateTime.MinValue;return allDay?parsed.Date:parsed.ToLocalTime(); }

    private void ProjectPortableRecord(Dictionary<string,string> row,DateTime start,DateTime end,List<Dictionary<string,string> > result) {
      Dictionary<string,object> data;try{data=json.DeserializeObject(row["data_json"]) as Dictionary<string,object>;}catch{return;}if(data==null)return;string slug=row["slug"],type=row["record_type"],date="",title="",kind="event",frequency="",color=slug=="kitchen-planner"?"#c35900":"#28709b";
      if(slug=="finance-manager"){
        if(type=="transactions"){date=GetString(data,"transaction_date");title=GetString(data,"payee");if(title=="")title="Finance transaction";kind="transaction";}
        else if(type=="recurring"){date=GetString(data,"next_date");title=GetString(data,"name");kind="reminder";}
        else if(type=="goals"){date=GetString(data,"target_date");title=GetString(data,"name");kind="target";}
        else if(type=="debts"){date=GetString(data,"next_due_date");title=GetString(data,"name");kind="due";}
      }else{
        if(type=="plans"){date=FirstPortableDate(data,new[]{"date_value","planned_date","scheduled_date","start_date"});title=FirstPortableText(data,new[]{"title","meal_name","recipe_title"});kind="meal";frequency=GetString(data,"frequency");if(!new[]{"daily","weekly","monthly","yearly"}.Contains(frequency))frequency="";}
        else if(type=="reminders"){date=FirstPortableDate(data,new[]{"date_value","due_date"});title=FirstPortableText(data,new[]{"title","name"});kind=GetString(data,"reminder_kind")=="restock"?"restock":"reminder";}
        else if(type=="stock_movements"&&GetDouble(data,"quantity_delta")>0){date=FirstPortableDate(data,new[]{"expiry_date","best_before_date"});title="Pantry expiry: "+FirstPortableText(data,new[]{"ingredient_name","name"});kind="expiry";}
      }
      DateTime parsed;if(!ValidDate(date)||!DateTime.TryParseExact(date,"yyyy-MM-dd",CultureInfo.InvariantCulture,DateTimeStyles.None,out parsed))return;if(title.Trim()=="")title=slug=="kitchen-planner"?"Kitchen plan":"Finance date";
      Dictionary<string,string> projected=new Dictionary<string,string>{{"long_id",slug+":"+row["long_id"]},{"source_id","plugin."+slug},{"source_opaque_id",row["long_id"]},{"item_kind",kind},{"title",title},{"start_utc",""},{"end_utc",""},{"date_value",date},{"timezone","UTC"},{"all_day","1"},{"status","planned"},{"recurrence_json",frequency==""?"":json.Serialize(new Dictionary<string,object>{{"frequency",frequency},{"interval",1}})},{"reminder_json",""},{"color",color},{"notes",""},{"revision",row["version"]}};
      ExpandCalendarRow(projected,start,end,new HashSet<string>(),result);
    }

    private static string FirstPortableDate(Dictionary<string,object> data,string[] keys){foreach(string key in keys){string value=GetString(data,key);if(ValidDate(value))return value;}return "";}
    private static string FirstPortableText(Dictionary<string,object> data,string[] keys){foreach(string key in keys){string value=GetString(data,key).Trim();if(value!="")return value;}return "";}

    internal void PreviewCalendarIcs(string source) {
      if(String.IsNullOrWhiteSpace(source)||source.Length>200000)throw new InvalidDataException("Paste an ICS file of 200 KB or fewer.");List<Dictionary<string,object> > records=ParseCalendarIcs(source);if(records.Count==0)throw new InvalidDataException("No supported VEVENT entries were found.");if(records.Count>1000)throw new InvalidDataException("An ICS preview supports up to 1,000 items at a time.");int duplicates=0;
      using(SqliteDb db=Open()){foreach(Dictionary<string,object> record in records)if(db.QueryOne("SELECT long_id FROM local_calendar_items WHERE source_id='ics.import' AND source_opaque_id=? AND status!='deleted' LIMIT 1",GetString(record,"source_opaque_id"))!=null)duplicates++;Dictionary<string,object> preview=new Dictionary<string,object>{{"records",records},{"count",records.Count},{"duplicates",duplicates},{"fingerprint",HashText(source)}};db.Execute("INSERT INTO local_plugin_settings(slug,setting_key,setting_value,updated_at)VALUES('core-calendar','ics_preview',?,?) ON CONFLICT(slug,setting_key) DO UPDATE SET setting_value=excluded.setting_value,updated_at=excluded.updated_at",json.Serialize(preview),Now());}ProtectDatabaseFile();
    }

    internal Dictionary<string,object> PendingCalendarIcs() {
      using(SqliteDb db=Open()){Dictionary<string,string> row=db.QueryOne("SELECT setting_value FROM local_plugin_settings WHERE slug='core-calendar' AND setting_key='ics_preview' LIMIT 1");if(row==null)return new Dictionary<string,object>();try{return json.DeserializeObject(row["setting_value"]) as Dictionary<string,object>??new Dictionary<string,object>();}catch{return new Dictionary<string,object>();}}
    }

    internal void DiscardPendingCalendarIcs(){using(SqliteDb db=Open())db.Execute("DELETE FROM local_plugin_settings WHERE slug='core-calendar' AND setting_key='ics_preview'");ProtectDatabaseFile();}

    internal int ImportPendingCalendarIcs() {
      Dictionary<string,object> preview=PendingCalendarIcs();object recordsObject;if(!preview.TryGetValue("records",out recordsObject)||!(recordsObject is object[]))throw new InvalidOperationException("Preview the ICS file before importing it.");object[] records=(object[])recordsObject;int imported=0;string now=Now();
      using(SqliteDb db=Open()){db.Exec("BEGIN IMMEDIATE");try{foreach(object value in records){Dictionary<string,object> record=value as Dictionary<string,object>;if(record==null)continue;string opaque=GetString(record,"source_opaque_id");if(db.QueryOne("SELECT long_id FROM local_calendar_items WHERE source_id='ics.import' AND source_opaque_id=? AND status!='deleted' LIMIT 1",opaque)!=null)continue;db.Execute("INSERT INTO local_calendar_items(long_id,source_id,source_opaque_id,item_kind,title,start_utc,end_utc,date_value,timezone,all_day,status,recurrence_json,reminder_json,color,notes,revision,created_at,updated_at)VALUES(?,'ics.import',?,'event',?,?,?,?,?,?,'planned',?,'','#507b9b','Imported from ICS',1,?,?)",NewLongId("calendar_items"),opaque,GetString(record,"title"),GetString(record,"start_utc"),GetString(record,"end_utc"),GetString(record,"date_value"),"UTC",ToBool(record.ContainsKey("all_day")?record["all_day"]:false)?1:0,GetString(record,"recurrence_json"),now,now);imported++;}db.Execute("INSERT OR IGNORE INTO local_calendar_ics_feeds(long_id,source_name,source_fingerprint,imported_at)VALUES(?,'One-time ICS import',?,?)",NewLongId("calendar_ics"),GetString(preview,"fingerprint"),now);db.Execute("DELETE FROM local_plugin_settings WHERE slug='core-calendar' AND setting_key='ics_preview'");db.Exec("COMMIT");}catch{db.Exec("ROLLBACK");throw;}}
      ProtectDatabaseFile();return imported;
    }

    private List<Dictionary<string,object> > ParseCalendarIcs(string source) {
      source=source.Replace("\r\n ","").Replace("\r\n\t","").Replace("\n ","").Replace("\n\t","");string[] lines=source.Replace("\r\n","\n").Replace('\r','\n').Split('\n');List<Dictionary<string,object> > records=new List<Dictionary<string,object> >();Dictionary<string,string> current=null;
      foreach(string raw in lines){string line=raw.TrimEnd();if(line.Equals("BEGIN:VEVENT",StringComparison.OrdinalIgnoreCase)){current=new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);continue;}if(line.Equals("END:VEVENT",StringComparison.OrdinalIgnoreCase)){if(current!=null){Dictionary<string,object> parsed=ParseCalendarIcsEvent(current);if(parsed!=null)records.Add(parsed);}current=null;continue;}if(current==null)continue;int colon=line.IndexOf(':');if(colon<1)continue;string left=line.Substring(0,colon),key=left.Split(';')[0].ToUpperInvariant(),value=line.Substring(colon+1);current[key]=value;if(left.IndexOf("VALUE=DATE",StringComparison.OrdinalIgnoreCase)>=0)current[key+"_DATE_ONLY"]="1";}
      return records;
    }

    private Dictionary<string,object> ParseCalendarIcsEvent(Dictionary<string,string> item) {
      string rawStart=item.ContainsKey("DTSTART")?item["DTSTART"]:"",rawEnd=item.ContainsKey("DTEND")?item["DTEND"]:"",title=CalendarIcsUnescape(item.ContainsKey("SUMMARY")?item["SUMMARY"]:"Untitled calendar item");bool allDay=item.ContainsKey("DTSTART_DATE_ONLY")||rawStart.Length==8;DateTime start;if(!ParseCalendarIcsDate(rawStart,allDay,out start))return null;DateTime end;if(!ParseCalendarIcsDate(rawEnd,allDay,out end))end=DateTime.MinValue;string uid=item.ContainsKey("UID")?item["UID"].Trim():"";if(uid=="")uid=HashText(title+"|"+rawStart+"|"+rawEnd);string recurrence="";if(item.ContainsKey("RRULE")){string frequency=item["RRULE"].Split(';').Select(part=>part.Split('=')).Where(part=>part.Length==2&&part[0].Equals("FREQ",StringComparison.OrdinalIgnoreCase)).Select(part=>part[1].ToLowerInvariant()).FirstOrDefault();if(new[]{"daily","weekly","monthly","yearly"}.Contains(frequency))recurrence=json.Serialize(new Dictionary<string,object>{{"frequency",frequency},{"interval",1}});}
      return new Dictionary<string,object>{{"source_opaque_id",HashText(uid).Substring(0,40)},{"title",title.Length>190?title.Substring(0,190):title},{"start_utc",allDay?"":start.ToUniversalTime().ToString("o",CultureInfo.InvariantCulture)},{"end_utc",allDay||end==DateTime.MinValue?"":end.ToUniversalTime().ToString("o",CultureInfo.InvariantCulture)},{"date_value",start.ToString("yyyy-MM-dd",CultureInfo.InvariantCulture)},{"all_day",allDay},{"recurrence_json",recurrence}};
    }

    private static bool ParseCalendarIcsDate(string raw,bool dateOnly,out DateTime value){value=DateTime.MinValue;if(String.IsNullOrEmpty(raw))return false;if(dateOnly)return DateTime.TryParseExact(raw,"yyyyMMdd",CultureInfo.InvariantCulture,DateTimeStyles.None,out value);string[] formats={"yyyyMMdd'T'HHmmss'Z'","yyyyMMdd'T'HHmm'Z'","yyyyMMdd'T'HHmmss","yyyyMMdd'T'HHmm"};DateTimeStyles styles=raw.EndsWith("Z",StringComparison.OrdinalIgnoreCase)?DateTimeStyles.AssumeUniversal|DateTimeStyles.AdjustToUniversal:DateTimeStyles.AssumeLocal;return DateTime.TryParseExact(raw,formats,CultureInfo.InvariantCulture,styles,out value);}
    private static string CalendarIcsUnescape(string value){return (value??"").Replace("\\n"," ").Replace("\\N"," ").Replace("\\,",",").Replace("\\;",";").Replace("\\\\","\\").Trim();}

    internal string ExportCalendarIcs() {
      StringBuilder output=new StringBuilder("BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//Racinage Free//Local Calendar//EN\r\nCALSCALE:GREGORIAN\r\n");
      using(SqliteDb db=Open())foreach(Dictionary<string,string> row in db.Query("SELECT long_id,title,start_utc,end_utc,date_value,all_day,recurrence_json,updated_at FROM local_calendar_items WHERE status!='deleted' ORDER BY date_value,start_utc")){output.Append("BEGIN:VEVENT\r\nUID:").Append(CalendarIcsEscape(row["long_id"]+"@racinage-free.local")).Append("\r\nDTSTAMP:").Append(CalendarIcsUtc(row["updated_at"])).Append("\r\nSUMMARY:").Append(CalendarIcsEscape(row["title"])).Append("\r\n");if(row["all_day"]=="1")output.Append("DTSTART;VALUE=DATE:").Append(row["date_value"].Replace("-","")).Append("\r\n");else{output.Append("DTSTART:").Append(CalendarIcsUtc(row["start_utc"])).Append("\r\n");if(row["end_utc"]!="")output.Append("DTEND:").Append(CalendarIcsUtc(row["end_utc"])).Append("\r\n");}string frequency="";try{Dictionary<string,object> recurrence=json.DeserializeObject(row["recurrence_json"]) as Dictionary<string,object>;if(recurrence!=null)frequency=GetString(recurrence,"frequency").ToUpperInvariant();}catch{}if(new[]{"DAILY","WEEKLY","MONTHLY","YEARLY"}.Contains(frequency))output.Append("RRULE:FREQ=").Append(frequency).Append("\r\n");output.Append("END:VEVENT\r\n");}
      return output.Append("END:VCALENDAR\r\n").ToString();
    }

    private static string CalendarIcsUtc(string value){DateTime parsed;if(!DateTime.TryParse(value,CultureInfo.InvariantCulture,DateTimeStyles.AssumeUniversal|DateTimeStyles.AdjustToUniversal,out parsed))parsed=DateTime.UtcNow;return parsed.ToUniversalTime().ToString("yyyyMMdd'T'HHmmss'Z'",CultureInfo.InvariantCulture);}
    private static string CalendarIcsEscape(string value){return (value??"").Replace("\\","\\\\").Replace(";","\\;").Replace(",","\\,").Replace("\r","").Replace("\n","\\n");}

    private static bool HasColumn(SqliteDb db,string table,string column) {
      foreach(Dictionary<string,string> row in db.Query("PRAGMA table_info("+table+")"))if(String.Equals(row["name"],column,StringComparison.OrdinalIgnoreCase))return true;return false;
    }

    private void EnsureBuiltinFinanceManager() {
      string source=Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"plugins","finance-manager"),destination=Path.Combine(PortablePaths.PluginsDir,"finance-manager","1.4.0");
      if(!Directory.Exists(source))return;
      Directory.CreateDirectory(destination);
      foreach(string file in Directory.GetFiles(source,"*",SearchOption.AllDirectories)){
        string relative=file.Substring(source.Length).TrimStart(Path.DirectorySeparatorChar,Path.AltDirectorySeparatorChar);
        string output=Path.GetFullPath(Path.Combine(destination,relative));
        if(!output.StartsWith(Path.GetFullPath(destination)+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase))continue;
        Directory.CreateDirectory(Path.GetDirectoryName(output));File.Copy(file,output,true);
      }
      string checksum=HashDirectory(destination),now=Now();
      using(SqliteDb db=Open()){
        Dictionary<string,string> row=db.QueryOne("SELECT status,installed_at FROM plugin_installs WHERE slug='finance-manager' LIMIT 1");
        if(row==null)db.Execute("INSERT INTO plugin_installs(slug,name,version,checksum_sha256,entrypoint,status,installed_at,updated_at,bridge_operations)VALUES('finance-manager','Finance Manager','1.4.0',?,'index.html','enabled',?,?,'bootstrap,save,batch_save,delete,settings,attachment_upload,attachment_get,attachment_delete')",checksum,now,now);
        else db.Execute("UPDATE plugin_installs SET name='Finance Manager',version='1.4.0',checksum_sha256=?,entrypoint='index.html',bridge_operations='bootstrap,save,batch_save,delete,settings,attachment_upload,attachment_get,attachment_delete',updated_at=? WHERE slug='finance-manager'",checksum,now);
      }
    }

    private static string HashDirectory(string root) {
      using(SHA256 sha=SHA256.Create()){
        foreach(string file in Directory.GetFiles(root,"*",SearchOption.AllDirectories).OrderBy(value=>value,StringComparer.OrdinalIgnoreCase)){
          byte[] name=Encoding.UTF8.GetBytes(file.Substring(root.Length).Replace('\\','/').ToLowerInvariant()),content=File.ReadAllBytes(file);
          sha.TransformBlock(name,0,name.Length,null,0);sha.TransformBlock(content,0,content.Length,null,0);
        }
        sha.TransformFinalBlock(new byte[0],0,0);return BitConverter.ToString(sha.Hash).Replace("-","").ToLowerInvariant();
      }
    }

    internal object LocalPluginAction(string slug,string action,Dictionary<string,object> payload) {
      if(!PluginActionAllowed(slug,action))throw new InvalidOperationException("This local plugin operation is not authorized.");
      if(slug=="finance-manager"){
        if(action=="bootstrap")return FinanceBootstrap(slug);
        if(action=="save")return FinanceSave(slug,payload);
        if(action=="batch_save")return FinanceBatchSave(slug,payload);
        if(action=="delete")return FinanceDelete(slug,payload);
        if(action=="settings")return FinanceSettings(payload);
        if(action=="attachment_upload")return FinanceAttachmentUpload(slug,payload);
        if(action=="attachment_get")return FinanceAttachmentGet(slug,payload);
        if(action=="attachment_delete")return FinanceAttachmentDelete(slug,payload);
      }
      if(slug=="namegen"){
        if(action=="bootstrap")return NameGenBootstrap(slug);
        if(action=="save_record")return NameGenSaveRecord(slug,payload);
        if(action=="delete_record")return NameGenDeleteRecord(slug,payload);
        if(action=="save_setting")return NameGenSaveSetting(slug,payload);
        if(action=="export_data")return NameGenExport(slug);
        if(action=="import_data")return NameGenImport(slug,payload);
      }
      if(slug=="kitchen-planner"){
        if(action=="bootstrap")return KitchenBootstrap(slug);
        if(action=="save_workspace")return KitchenSaveRecord(slug,"workspaces",payload);
        if(action=="save_recipe")return KitchenSaveRecord(slug,"recipes",payload);
        if(action=="save_ingredient")return KitchenSaveRecord(slug,"ingredients",payload);
        if(action=="save_pantry_movement")return KitchenSaveRecord(slug,"stock_movements",payload);
        if(action=="save_cooking_log")return KitchenSaveCookingLog(slug,payload);
        if(action=="preview_cooking_deductions")return KitchenPreviewCookingDeductions(slug,payload);
        if(action=="save_plan")return KitchenSaveRecord(slug,"plans",payload);
        if(action=="save_profile")return KitchenSaveRecord(slug,"profiles",payload);
        if(action=="save_favorite")return KitchenSaveRecord(slug,"favorites",payload);
        if(action=="save_shopping_list")return KitchenSaveRecord(slug,"shopping_lists",payload);
        if(action=="save_reminder")return KitchenSaveRecord(slug,"reminders",payload);
        if(action=="save_taxonomy")return KitchenSaveTaxonomy(slug,payload);
        if(action=="delete_record")return KitchenDeleteRecord(slug,payload);
        if(action=="export_data")return KitchenExport(slug);
        if(action=="import_preview")return KitchenImportPreview(slug,payload);
        if(action=="import_execute")return KitchenImportExecute(slug);
        if(action=="calendar_list")return KitchenCalendarList(payload);
        if(action=="safe_fetch")return KitchenSafeFetch(payload);
        if(action=="open_source_url")return KitchenOpenSourceUrl(payload);
        if(action=="queue_source_import")return QueueKitchenSourceImport(slug,payload);
        if(action=="retry_source_import")return RetryKitchenSourceImport(slug,payload);
        if(action=="local_ai_status")return KitchenLocalAiStatus();
        if(action=="kitchen_media_upload")return KitchenMediaUpload(slug,payload);
        if(action=="kitchen_media_get")return KitchenMediaGet(slug,payload);
        if(action=="kitchen_media_delete")return KitchenMediaDelete(slug,payload);
      }
      throw new InvalidOperationException("Unknown local plugin action.");
    }

    internal bool PluginActionAllowed(string slug,string action){
      if(!PluginCatalogClient.ValidSlug(slug)||String.IsNullOrWhiteSpace(action))return false;
      if(!KnownPluginOperations(slug).Contains(action))return false;
      using(SqliteDb db=Open()){Dictionary<string,string> row=db.QueryOne("SELECT bridge_operations FROM plugin_installs WHERE slug=? AND status='enabled' LIMIT 1",slug);if(row==null)return false;return (row["bridge_operations"]??"").Split(',').Contains(action);}
    }

    private static string[] KnownPluginOperations(string slug){
      if(slug=="finance-manager")return new[]{"bootstrap","save","batch_save","delete","settings","attachment_upload","attachment_get","attachment_delete"};
      if(slug=="namegen")return new[]{"bootstrap","save_record","delete_record","save_setting","export_data","import_data"};
      if(slug=="kitchen-planner")return new[]{"bootstrap","save_workspace","save_recipe","save_ingredient","save_pantry_movement","save_cooking_log","preview_cooking_deductions","save_plan","save_profile","save_favorite","save_shopping_list","save_reminder","save_taxonomy","delete_record","export_data","import_preview","import_execute","calendar_list","safe_fetch","open_source_url","queue_source_import","retry_source_import","local_ai_status","local_ai_extract","kitchen_media_upload","kitchen_media_get","kitchen_media_delete"};
      return new string[0];
    }

    private static readonly string[] KitchenRecordTypes={"workspaces","recipes","ingredients","stock_movements","cooking_logs","plans","categories","tags","profiles","favorites","shopping_lists","reminders"};

    private object KitchenBootstrap(string slug){
      using(SqliteDb db=Open()){
        List<Dictionary<string,object> > records=new List<Dictionary<string,object> >();List<Dictionary<string,object> > media=new List<Dictionary<string,object> >();Dictionary<string,double> stock=new Dictionary<string,double>();Dictionary<string,int> cookingCounts=new Dictionary<string,int>();
        foreach(Dictionary<string,string> row in db.Query("SELECT record_type,long_id,workspace_long_id,data_json,version,status,created_at,updated_at FROM local_plugin_records WHERE slug=? AND status!='deleted' ORDER BY CASE record_type WHEN 'workspaces' THEN 0 WHEN 'categories' THEN 1 WHEN 'tags' THEN 2 WHEN 'profiles' THEN 3 WHEN 'ingredients' THEN 4 WHEN 'recipes' THEN 5 ELSE 6 END,created_at,long_id",slug)){
          Dictionary<string,object> data;try{data=json.DeserializeObject(row["data_json"]) as Dictionary<string,object>;}catch{continue;}if(data==null)data=new Dictionary<string,object>();Dictionary<string,object> item=new Dictionary<string,object>{{"record_type",row["record_type"]},{"long_id",row["long_id"]},{"workspace_long_id",row["workspace_long_id"]},{"data",data},{"version",ToInt(row["version"])},{"status",row["status"]},{"created_at",row["created_at"]},{"updated_at",row["updated_at"]}};records.Add(item);
          if(row["record_type"]=="stock_movements"){string ingredient=GetString(data,"ingredient_long_id");if(ingredient!=""){if(!stock.ContainsKey(ingredient))stock[ingredient]=0;stock[ingredient]+=GetDouble(data,"quantity_delta");}}
          if(row["record_type"]=="cooking_logs"){string recipe=GetString(data,"recipe_long_id");if(recipe!=""){if(!cookingCounts.ContainsKey(recipe))cookingCounts[recipe]=0;cookingCounts[recipe]++;}}
        }
        foreach(Dictionary<string,string> row in db.Query("SELECT long_id,workspace_long_id,transaction_long_id,original_name,mime_type,file_size,version,created_at FROM local_plugin_attachments WHERE slug=? AND status='active' ORDER BY created_at",slug))media.Add(new Dictionary<string,object>{{"long_id",row["long_id"]},{"workspace_long_id",row["workspace_long_id"]},{"recipe_long_id",row["transaction_long_id"]},{"original_name",row["original_name"]},{"mime_type",row["mime_type"]},{"file_size",ToLong(row["file_size"])},{"version",ToInt(row["version"])},{"created_at",row["created_at"]}});
        return new Dictionary<string,object>{{"format","racinage-kitchen-planner"},{"version",1},{"offline",true},{"records",records},{"media",media},{"stock",stock},{"cooking_counts",cookingCounts},{"imports",KitchenSourceImports(db,slug)},{"extraction_runs",KitchenExtractionRuns(db,slug)},{"display_currency",GetDisplayCurrency()},{"ai",KitchenLocalAiStatus()}};
      }
    }

    private object KitchenSaveTaxonomy(string slug,Dictionary<string,object> payload){string taxonomy=GetString(payload,"taxonomy_type");if(taxonomy!="category"&&taxonomy!="tag")throw new InvalidDataException("Choose a category or tag.");return KitchenSaveRecord(slug,taxonomy=="category"?"categories":"tags",payload);}

    private object KitchenSaveRecord(string slug,string type,Dictionary<string,object> payload){
      if(!KitchenRecordTypes.Contains(type))throw new InvalidDataException("Unknown Kitchen record type.");
      string workspace=SafeLongId(GetString(payload,"workspace_long_id")),longId=SafeLongId(GetString(payload,"long_id"));
      Dictionary<string,object> data=GetObject(payload,"data");
      if(type=="workspaces"){
        workspace="";string name=GetString(data,"name").Trim(),workspaceType=GetString(data,"workspace_type");
        if(name==""||name.Length>120||!new[]{"personal","family","group"}.Contains(workspaceType))throw new InvalidDataException("Enter a workspace name and valid type.");
      }else if(workspace=="")throw new InvalidDataException("Choose a Kitchen workspace.");
      if(type=="recipes"){
        string title=GetString(data,"title").Trim(),status=GetString(data,"status");double servings=GetDouble(data,"servings");
        if(title==""||title.Length>190||!new[]{"pending","active","duplicate","archived"}.Contains(status))throw new InvalidDataException("Enter a recipe title and valid status.");
        if(servings<=0||servings>10000)throw new InvalidDataException("Recipe servings must be greater than zero.");
        object[] ingredients=GetArray(data,"ingredients"),steps=GetArray(data,"steps");
        if(ingredients.Length>300||steps.Length>300)throw new InvalidDataException("A recipe allows up to 300 ingredients and 300 steps.");
        bool complete=ingredients.Length>0&&steps.Length>0;
        foreach(object value in ingredients){Dictionary<string,object> line=value as Dictionary<string,object>;if(line==null||GetString(line,"name").Trim()=="")throw new InvalidDataException("Every recipe ingredient needs a specific name.");if(!ToBool(line.ContainsKey("optional")?line["optional"]:false)&&GetDouble(line,"amount")<=0)complete=false;}
        foreach(object value in steps){Dictionary<string,object> step=value as Dictionary<string,object>;if(step==null||GetString(step,"action").Trim()=="")complete=false;}
        if(status=="active"&&!complete)throw new InvalidDataException("Keep this recipe Pending until it has useful quantities and ordered preparation steps.");
        KitchenValidateOptionalHttpsUrl(data,"source_url");
        data["fingerprint"]=KitchenRecipeFingerprint(data);
      }
      if(type=="ingredients"){
        if(GetString(data,"name").Trim()=="")throw new InvalidDataException("Enter a specific ingredient name.");
        if(GetDouble(data,"package_quantity")<0||GetDouble(data,"package_cost")<0||GetDouble(data,"minimum_stock")<0)throw new InvalidDataException("Ingredient price and stock values cannot be negative.");
        KitchenValidateOptionalHttpsUrl(data,"shopping_url");
      }
      if(type=="stock_movements"&&(GetDouble(data,"quantity_delta")==0||!ValidDate(GetString(data,"movement_date"))))throw new InvalidDataException("Enter a non-zero stock quantity and valid date.");
      if(type=="cooking_logs"&&(GetDouble(data,"servings")<=0||!ValidDate(GetString(data,"cooked_date"))))throw new InvalidDataException("Enter cooked servings and a valid date.");
      if(type=="plans"){
        if(!ValidDate(GetString(data,"date_value")))throw new InvalidDataException("Choose a valid plan date.");
        string frequency=GetString(data,"frequency");if(frequency!=""&&!new[]{"daily","weekly","monthly","yearly"}.Contains(frequency))throw new InvalidDataException("Choose a supported plan recurrence.");
      }
      if((type=="categories"||type=="tags"||type=="profiles")&&GetString(data,"name").Trim()=="")throw new InvalidDataException("Enter a name.");
      if(type=="reminders"&&!ValidDate(GetString(data,"date_value")))throw new InvalidDataException("Choose a valid reminder date.");
      string encoded;
      using(SqliteDb db=Open()){
        if(type!="workspaces")RequireReference(db,slug,"","workspaces",workspace);
        if(type=="stock_movements")RequireKitchenWorkspaceReference(db,slug,workspace,"ingredients",GetString(data,"ingredient_long_id"));
        if(type=="cooking_logs")RequireKitchenWorkspaceReference(db,slug,workspace,"recipes",GetString(data,"recipe_long_id"));
        string recipe=GetString(data,"recipe_long_id");
        if(type=="plans"&&recipe!="")RequireKitchenWorkspaceReference(db,slug,workspace,"recipes",recipe);
        if(type=="favorites"){
          RequireKitchenWorkspaceReference(db,slug,workspace,"profiles",GetString(data,"profile_long_id"));
          RequireKitchenWorkspaceReference(db,slug,workspace,"recipes",GetString(data,"recipe_long_id"));
        }
        if(type=="recipes"){
          string fingerprint=GetString(data,"fingerprint");
          foreach(Dictionary<string,string> row in db.Query("SELECT long_id,data_json FROM local_plugin_records WHERE slug=? AND record_type='recipes' AND workspace_long_id=? AND status='active' AND long_id!=? ORDER BY updated_at DESC LIMIT 5000",slug,workspace,longId)){
            Dictionary<string,object> other;try{other=json.DeserializeObject(row["data_json"]) as Dictionary<string,object>;}catch{continue;}
            if(other!=null&&fingerprint!=""&&FixedEquals(fingerprint,GetString(other,"fingerprint"))){data["status"]="duplicate";data["duplicate_of"]=row["long_id"];break;}
          }
        }
        encoded=json.Serialize(data);if(encoded.Length>250000)throw new InvalidDataException("The Kitchen record is too large.");
        string now=Now();
        if(longId!=""){
          Dictionary<string,string> current=db.QueryOne("SELECT version FROM local_plugin_records WHERE slug=? AND record_type=? AND long_id=? AND workspace_long_id=? AND status!='deleted' LIMIT 1",slug,type,longId,workspace);if(current==null)throw new InvalidDataException("The Kitchen record is unavailable.");
          int version=GetInt(payload,"version");if(version>0&&version!=ToInt(current["version"]))throw new InvalidOperationException("This Kitchen record changed in another window. Reopen it and try again.");
          db.Execute("UPDATE local_plugin_records SET data_json=?,version=version+1,status='active',updated_at=? WHERE slug=? AND record_type=? AND long_id=? AND workspace_long_id=?",encoded,now,slug,type,longId,workspace);
        }else{
          if(ToInt(db.Scalar("SELECT COUNT(*) FROM local_plugin_records WHERE slug=? AND status!='deleted'",slug))>=20000)throw new InvalidOperationException("The local Kitchen record limit was reached.");
          longId=NewLongId(type);db.Execute("INSERT INTO local_plugin_records(slug,record_type,long_id,workspace_long_id,data_json,version,status,created_at,updated_at)VALUES(?,?,?,?,?,1,'active',?,?)",slug,type,longId,workspace,encoded,now,now);
        }
      }
      ProtectDatabaseFile();return new Dictionary<string,object>{{"long_id",longId},{"status",GetString(data,"status")}};
    }

    private static string KitchenRecipeFingerprint(Dictionary<string,object> data){
      double servings=Math.Max(0.000001,GetDouble(data,"servings"));List<string> ingredients=new List<string>();
      foreach(object value in GetArray(data,"ingredients")){Dictionary<string,object> line=value as Dictionary<string,object>;if(line==null)continue;ingredients.Add(KitchenNormalize(GetString(line,"name"))+"|"+(GetDouble(line,"amount")/servings).ToString("0.######",CultureInfo.InvariantCulture)+"|"+KitchenNormalize(GetString(line,"unit"))+"|"+KitchenNormalize(GetString(line,"preparation")));}
      ingredients.Sort(StringComparer.Ordinal);List<string> steps=new List<string>();foreach(object value in GetArray(data,"steps")){Dictionary<string,object> step=value as Dictionary<string,object>;if(step!=null)steps.Add(KitchenNormalize(GetString(step,"action")));}
      return HashText(String.Join(";",ingredients)+"||"+String.Join(";",steps));
    }

    private static string KitchenNormalize(string value){return String.Join(" ",(value??"").Trim().ToLowerInvariant().Split(new[]{' ','\t','\r','\n'},StringSplitOptions.RemoveEmptyEntries));}

    private static void KitchenValidateOptionalHttpsUrl(Dictionary<string,object> data,string key){string raw=GetString(data,key).Trim();if(raw==""){data[key]="";return;}Uri uri;if(raw.Length>1000||!Uri.TryCreate(raw,UriKind.Absolute,out uri)||uri.Scheme!=Uri.UriSchemeHttps||uri.UserInfo!=""||uri.Port!=443)throw new InvalidDataException("Kitchen links must use a public HTTPS URL on the standard secure port.");data[key]=uri.AbsoluteUri;}

    private static void RequireKitchenWorkspaceReference(SqliteDb db,string slug,string workspace,string type,string longId){if(SafeLongId(longId)==""||db.QueryOne("SELECT long_id FROM local_plugin_records WHERE slug=? AND record_type=? AND long_id=? AND workspace_long_id=? AND status='active' LIMIT 1",slug,type,longId,workspace)==null)throw new InvalidDataException("The selected Kitchen record is unavailable in this workspace.");}

    private object KitchenPreviewCookingDeductions(string slug,Dictionary<string,object> payload){
      string workspace=SafeLongId(GetString(payload,"workspace_long_id")),recipe=SafeLongId(GetString(payload,"recipe_long_id"));double servings=GetDouble(payload,"servings");
      if(workspace==""||recipe==""||servings<=0||servings>10000)throw new InvalidDataException("Choose a recipe and valid number of servings.");
      using(SqliteDb db=Open())return KitchenCookingPreview(db,slug,workspace,recipe,servings);
    }

    private Dictionary<string,object> KitchenCookingPreview(SqliteDb db,string slug,string workspace,string recipe,double servings){
      Dictionary<string,string> recipeRow=db.QueryOne("SELECT data_json FROM local_plugin_records WHERE slug=? AND record_type='recipes' AND long_id=? AND workspace_long_id=? AND status='active' LIMIT 1",slug,recipe,workspace);if(recipeRow==null)throw new InvalidDataException("The selected recipe is unavailable in this workspace.");
      Dictionary<string,object> recipeData=json.DeserializeObject(recipeRow["data_json"]) as Dictionary<string,object>;if(recipeData==null)throw new InvalidDataException("The selected recipe data is unavailable.");double baseServings=GetDouble(recipeData,"servings");if(baseServings<=0)throw new InvalidDataException("The selected recipe has invalid servings.");double scale=servings/baseServings;
      Dictionary<string,double> stock=new Dictionary<string,double>();foreach(Dictionary<string,string> row in db.Query("SELECT data_json FROM local_plugin_records WHERE slug=? AND record_type='stock_movements' AND workspace_long_id=? AND status='active'",slug,workspace)){Dictionary<string,object> movement=json.DeserializeObject(row["data_json"]) as Dictionary<string,object>;if(movement==null)continue;string ingredient=GetString(movement,"ingredient_long_id");if(!stock.ContainsKey(ingredient))stock[ingredient]=0;stock[ingredient]+=GetDouble(movement,"quantity_delta");}
      List<Dictionary<string,object> > deductions=new List<Dictionary<string,object> >();bool ambiguous=false,shortages=false;double estimatedCost=0;
      foreach(object value in GetArray(recipeData,"ingredients")){
        Dictionary<string,object> line=value as Dictionary<string,object>;if(line==null||ToBool(line.ContainsKey("optional")?line["optional"]:false))continue;string ingredient=SafeLongId(GetString(line,"ingredient_long_id")),name=GetString(line,"name").Trim(),unit=GetString(line,"unit").Trim();double needed=Math.Max(0,GetDouble(line,"amount")*scale),available=ingredient!=""&&stock.ContainsKey(ingredient)?stock[ingredient]:0;bool lineAmbiguous=ingredient==""||needed<=0;Dictionary<string,string> catalogRow=null;Dictionary<string,object> catalog=null;
        if(ingredient!=""){catalogRow=db.QueryOne("SELECT data_json FROM local_plugin_records WHERE slug=? AND record_type='ingredients' AND long_id=? AND workspace_long_id=? AND status='active' LIMIT 1",slug,ingredient,workspace);if(catalogRow!=null)catalog=json.DeserializeObject(catalogRow["data_json"]) as Dictionary<string,object>;else lineAmbiguous=true;}
        string stockUnit=catalog==null?"":GetString(catalog,"unit").Trim();if(unit!=""&&stockUnit!=""&&!String.Equals(KitchenNormalize(unit),KitchenNormalize(stockUnit),StringComparison.Ordinal))lineAmbiguous=true;
        double lineCost=0,packageQuantity=catalog==null?0:GetDouble(catalog,"package_quantity"),packageCost=catalog==null?0:GetDouble(catalog,"package_cost");if(!lineAmbiguous&&packageQuantity>0&&packageCost>=0)lineCost=needed/packageQuantity*packageCost;estimatedCost+=lineCost;double shortage=Math.Max(0,needed-available);ambiguous=ambiguous||lineAmbiguous;shortages=shortages||(!lineAmbiguous&&shortage>0.000001);
        deductions.Add(new Dictionary<string,object>{{"ingredient_long_id",ingredient},{"name",name},{"needed",needed},{"available",available},{"shortage",shortage},{"unit",unit!=""?unit:stockUnit},{"ambiguous",lineAmbiguous},{"estimated_cost",lineCost}});
      }
      return new Dictionary<string,object>{{"recipe_long_id",recipe},{"servings",servings},{"deductions",deductions},{"ambiguous",ambiguous},{"shortages",shortages},{"estimated_cost",Math.Round(estimatedCost,2)},{"currency",GetDisplayCurrency()}};
    }

    private object KitchenSaveCookingLog(string slug,Dictionary<string,object> payload){
      string workspace=SafeLongId(GetString(payload,"workspace_long_id"));Dictionary<string,object> data=GetObject(payload,"data");string recipe=SafeLongId(GetString(data,"recipe_long_id")),mode=GetString(data,"pantry_mode");double servings=GetDouble(data,"servings");string cookedDate=GetString(data,"cooked_date");
      if(workspace==""||recipe==""||servings<=0||!ValidDate(cookedDate)||!new[]{"record_only","deduct"}.Contains(mode))throw new InvalidDataException("Choose a recipe, valid servings, date, and pantry option.");
      string logId=NewLongId("cooking_logs"),now=Now();using(SqliteDb db=Open()){db.Exec("BEGIN IMMEDIATE");try{
        RequireReference(db,slug,"","workspaces",workspace);Dictionary<string,object> preview=KitchenCookingPreview(db,slug,workspace,recipe,servings);bool deduct=mode=="deduct";
        if(deduct&&ToBool(preview["ambiguous"]))throw new InvalidDataException("Review ambiguous ingredient conversions before deducting pantry stock.");
        if(deduct&&ToBool(preview["shortages"]))throw new InvalidDataException("Pantry stock is insufficient. Record only or add the shortages to Shopping.");
        data["deductions"]=preview["deductions"];data["estimated_cost_snapshot"]=preview["estimated_cost"];data["currency_snapshot"]=preview["currency"];
        string encoded=json.Serialize(data);if(encoded.Length>250000)throw new InvalidDataException("The cooking record is too large.");db.Execute("INSERT INTO local_plugin_records(slug,record_type,long_id,workspace_long_id,data_json,version,status,created_at,updated_at)VALUES(?,?,?,?,?,1,'active',?,?)",slug,"cooking_logs",logId,workspace,encoded,now,now);
        if(deduct)foreach(object value in (IEnumerable)preview["deductions"]){Dictionary<string,object> line=value as Dictionary<string,object>;if(line==null||GetDouble(line,"needed")<=0)continue;Dictionary<string,object> movement=new Dictionary<string,object>{{"ingredient_long_id",GetString(line,"ingredient_long_id")},{"quantity_delta",-GetDouble(line,"needed")},{"movement_date",cookedDate},{"reason","cooking"},{"cooking_log_long_id",logId},{"notes","Cooked "+recipe}};db.Execute("INSERT INTO local_plugin_records(slug,record_type,long_id,workspace_long_id,data_json,version,status,created_at,updated_at)VALUES(?,?,?,?,?,1,'active',?,?)",slug,"stock_movements",NewLongId("stock_movements"),workspace,json.Serialize(movement),now,now);}
        db.Exec("COMMIT");
      }catch{db.Exec("ROLLBACK");throw;}}
      ProtectDatabaseFile();return new Dictionary<string,object>{{"long_id",logId},{"recorded",true}};
    }

    private object KitchenMediaUpload(string slug,Dictionary<string,object> payload){
      string workspace=SafeLongId(GetString(payload,"workspace_long_id")),recipe=SafeLongId(GetString(payload,"recipe_long_id")),name=Path.GetFileName(GetString(payload,"original_name"));if(name==""||name.Length>255)throw new InvalidDataException("The image name is invalid.");
      byte[] content;try{content=Convert.FromBase64String(GetString(payload,"content_base64"));}catch{throw new InvalidDataException("The image content is invalid.");}if(content.Length<12||content.Length>15*1024*1024)throw new InvalidDataException("Recipe images must be 15 MB or fewer.");string mime=DetectFinanceMime(content);if(!new[]{"image/jpeg","image/png","image/webp"}.Contains(mime))throw new InvalidDataException("Only private JPG, PNG, and WebP recipe images are supported.");KitchenValidateImage(content,mime);
      using(SqliteDb db=Open()){RequireKitchenWorkspaceReference(db,slug,workspace,"recipes",recipe);if(ToInt(db.Scalar("SELECT COUNT(*) FROM local_plugin_attachments WHERE slug=? AND transaction_long_id=? AND status='active'",slug,recipe))>=12)throw new InvalidOperationException("A local recipe allows up to 12 gallery images.");string longId="kitchen_media_"+Guid.NewGuid().ToString("N"),extension=mime=="image/jpeg"?".jpg":mime=="image/png"?".png":".webp",relative=Path.Combine("plugins",slug,"recipes",recipe,longId+extension),path=Path.GetFullPath(Path.Combine(PortablePaths.MediaDir,relative)),mediaRoot=Path.GetFullPath(PortablePaths.MediaDir)+Path.DirectorySeparatorChar;if(!path.StartsWith(mediaRoot,StringComparison.OrdinalIgnoreCase))throw new InvalidDataException("The recipe image path is invalid.");Directory.CreateDirectory(Path.GetDirectoryName(path));File.WriteAllBytes(path,content);string now=Now();db.Execute("INSERT INTO local_plugin_attachments(slug,long_id,workspace_long_id,transaction_long_id,relative_path,original_name,mime_type,file_size,version,status,created_at,updated_at)VALUES(?,?,?,?,?,?,?,?,1,'active',?,?)",slug,longId,workspace,recipe,relative,name,mime,content.Length,now,now);ProtectDatabaseFile();return new Dictionary<string,object>{{"long_id",longId}};}
    }

    private object KitchenMediaGet(string slug,Dictionary<string,object> payload){string workspace=SafeLongId(GetString(payload,"workspace_long_id")),recipe=SafeLongId(GetString(payload,"recipe_long_id")),longId=SafeLongId(GetString(payload,"long_id"));using(SqliteDb db=Open()){Dictionary<string,string> row=db.QueryOne("SELECT relative_path,original_name,mime_type,file_size FROM local_plugin_attachments WHERE slug=? AND long_id=? AND workspace_long_id=? AND transaction_long_id=? AND status='active' LIMIT 1",slug,longId,workspace,recipe);if(row==null)throw new InvalidOperationException("The recipe image is unavailable.");string path=Path.GetFullPath(Path.Combine(PortablePaths.MediaDir,row["relative_path"])),mediaRoot=Path.GetFullPath(PortablePaths.MediaDir)+Path.DirectorySeparatorChar;if(!path.StartsWith(mediaRoot,StringComparison.OrdinalIgnoreCase)||!File.Exists(path))throw new InvalidOperationException("The private recipe image file is missing.");if(ToLong(row["file_size"])>15*1024*1024)throw new InvalidOperationException("The private recipe image is too large to open.");return new Dictionary<string,object>{{"original_name",row["original_name"]},{"mime_type",row["mime_type"]},{"content_base64",Convert.ToBase64String(File.ReadAllBytes(path))}};}}

    private static void KitchenValidateImage(byte[] content,string mime){int width=0,height=0;if(mime=="image/webp"){if(!KitchenWebpDimensions(content,out width,out height))throw new InvalidDataException("The WebP recipe image is invalid.");}else try{using(MemoryStream stream=new MemoryStream(content,false))using(Image image=Image.FromStream(stream,true,true)){width=image.Width;height=image.Height;}}catch{throw new InvalidDataException("The recipe image could not be decoded.");}if(width<1||height<1||width>12000||height>12000||(long)width*height>40000000L||Math.Max((double)width/height,(double)height/width)>20d)throw new InvalidDataException("Recipe images must be 40 megapixels or fewer with reasonable dimensions.");}

    private static bool KitchenWebpDimensions(byte[] bytes,out int width,out int height){width=0;height=0;if(bytes==null||bytes.Length<30||Encoding.ASCII.GetString(bytes,0,4)!="RIFF"||Encoding.ASCII.GetString(bytes,8,4)!="WEBP")return false;string chunk=Encoding.ASCII.GetString(bytes,12,4);if(chunk=="VP8X"&&bytes.Length>=30){width=1+bytes[24]+(bytes[25]<<8)+(bytes[26]<<16);height=1+bytes[27]+(bytes[28]<<8)+(bytes[29]<<16);return true;}if(chunk=="VP8 "&&bytes.Length>=30&&bytes[23]==0x9d&&bytes[24]==0x01&&bytes[25]==0x2a){width=(bytes[26]|bytes[27]<<8)&0x3fff;height=(bytes[28]|bytes[29]<<8)&0x3fff;return true;}if(chunk=="VP8L"&&bytes.Length>=25&&bytes[20]==0x2f){width=1+(bytes[21]|((bytes[22]&0x3f)<<8));height=1+((bytes[22]>>6)|(bytes[23]<<2)|((bytes[24]&0x0f)<<10));return true;}return false;}

    private object KitchenMediaDelete(string slug,Dictionary<string,object> payload){string workspace=SafeLongId(GetString(payload,"workspace_long_id")),recipe=SafeLongId(GetString(payload,"recipe_long_id")),longId=SafeLongId(GetString(payload,"long_id"));using(SqliteDb db=Open()){int changed=db.Execute("UPDATE local_plugin_attachments SET status='deleted',version=version+1,updated_at=? WHERE slug=? AND long_id=? AND workspace_long_id=? AND transaction_long_id=? AND status='active'",Now(),slug,longId,workspace,recipe);if(changed!=1)throw new InvalidOperationException("The recipe image is unavailable.");}ProtectDatabaseFile();return new Dictionary<string,object>{{"deleted",true}};}

    private object KitchenDeleteRecord(string slug,Dictionary<string,object> payload){string type=GetString(payload,"record_type"),longId=SafeLongId(GetString(payload,"long_id")),workspace=SafeLongId(GetString(payload,"workspace_long_id"));if(!KitchenRecordTypes.Contains(type)||longId=="")throw new InvalidDataException("The Kitchen record is unavailable.");using(SqliteDb db=Open()){Dictionary<string,string> current=db.QueryOne("SELECT version FROM local_plugin_records WHERE slug=? AND record_type=? AND long_id=? AND workspace_long_id=? AND status!='deleted' LIMIT 1",slug,type,longId,workspace);if(current==null)throw new InvalidDataException("The Kitchen record is unavailable.");int version=GetInt(payload,"version");if(version>0&&version!=ToInt(current["version"]))throw new InvalidOperationException("This Kitchen record changed in another window. Reopen it and try again.");string now=Now();db.Exec("BEGIN IMMEDIATE");try{db.Execute("UPDATE local_plugin_records SET status='deleted',version=version+1,updated_at=? WHERE slug=? AND record_type=? AND long_id=? AND workspace_long_id=?",now,slug,type,longId,workspace);if(type=="workspaces"){db.Execute("UPDATE local_plugin_records SET status='deleted',version=version+1,updated_at=? WHERE slug=? AND workspace_long_id=? AND status='active'",now,slug,longId);db.Execute("UPDATE local_plugin_attachments SET status='deleted',version=version+1,updated_at=? WHERE slug=? AND workspace_long_id=? AND status='active'",now,slug,longId);}else if(type=="recipes"){db.Execute("UPDATE local_plugin_attachments SET status='deleted',version=version+1,updated_at=? WHERE slug=? AND workspace_long_id=? AND transaction_long_id=? AND status='active'",now,slug,workspace,longId);db.Execute("UPDATE local_plugin_records SET status='deleted',version=version+1,updated_at=? WHERE slug=? AND workspace_long_id=? AND record_type IN('favorites','plans') AND status='active' AND json_extract(data_json,'$.recipe_long_id')=?",now,slug,workspace,longId);}else if(type=="profiles")db.Execute("UPDATE local_plugin_records SET status='deleted',version=version+1,updated_at=? WHERE slug=? AND workspace_long_id=? AND record_type='favorites' AND status='active' AND json_extract(data_json,'$.profile_long_id')=?",now,slug,workspace,longId);db.Exec("COMMIT");}catch{db.Exec("ROLLBACK");throw;}}ProtectDatabaseFile();return new Dictionary<string,object>{{"deleted",true}};}

    private object KitchenExport(string slug){Dictionary<string,object> bootstrap=(Dictionary<string,object>)KitchenBootstrap(slug);return new Dictionary<string,object>{{"format","racinage-kitchen-planner"},{"format_version",1},{"exported_at",Now()},{"records",bootstrap["records"]}};}

    private object[] KitchenValidateBackup(string slug,Dictionary<string,object> backup){
      if(backup==null||GetString(backup,"format")!="racinage-kitchen-planner"||ToInt(backup.ContainsKey("format_version")?backup["format_version"]:null)!=1)throw new InvalidDataException("This is not a supported Racinage Kitchen Planner backup.");
      object recordsValue;if(!backup.TryGetValue("records",out recordsValue)||!(recordsValue is object[]))throw new InvalidDataException("The Kitchen backup has no records.");object[] records=(object[])recordsValue;if(records.Length>20000)throw new InvalidDataException("The Kitchen backup contains too many records.");
      HashSet<string> keys=new HashSet<string>(StringComparer.Ordinal),workspaces=new HashSet<string>(StringComparer.Ordinal),scopedKeys=new HashSet<string>(StringComparer.Ordinal);
      foreach(object value in records){Dictionary<string,object> record=value as Dictionary<string,object>;if(record==null)throw new InvalidDataException("The Kitchen backup contains an invalid record.");string type=GetString(record,"record_type"),longId=SafeLongId(GetString(record,"long_id")),workspace=SafeLongId(GetString(record,"workspace_long_id"));object rawData;if(!KitchenRecordTypes.Contains(type)||longId==""||!record.TryGetValue("data",out rawData)||!(rawData is Dictionary<string,object>)||!keys.Add(type+"|"+longId))throw new InvalidDataException("The Kitchen backup contains an invalid or duplicate record.");Dictionary<string,object> data=(Dictionary<string,object>)rawData;string encoded=json.Serialize(data);if(encoded.Length>250000)throw new InvalidDataException("A Kitchen backup record is larger than 250 KB.");if(type=="workspaces"){if(workspace!=""||GetString(data,"name").Trim()=="")throw new InvalidDataException("The Kitchen backup contains an invalid workspace.");workspaces.Add(longId);}else{if(workspace=="")throw new InvalidDataException("A Kitchen backup record has no workspace.");scopedKeys.Add(workspace+"|"+type+"|"+longId);}
        if(type=="recipes"){string status=GetString(data,"status");if(GetString(data,"title").Trim()==""||!new[]{"pending","active","duplicate","archived"}.Contains(status)||GetDouble(data,"servings")<=0||GetArray(data,"ingredients").Length>300||GetArray(data,"steps").Length>300)throw new InvalidDataException("The Kitchen backup contains an invalid recipe.");KitchenValidateOptionalHttpsUrl(data,"source_url");}
        if(type=="ingredients")KitchenValidateOptionalHttpsUrl(data,"shopping_url");
        if(type=="stock_movements"&&(!ValidDate(GetString(data,"movement_date"))||GetDouble(data,"quantity_delta")==0))throw new InvalidDataException("The Kitchen backup contains an invalid stock movement.");
        if(type=="cooking_logs"&&(!ValidDate(GetString(data,"cooked_date"))||GetDouble(data,"servings")<=0))throw new InvalidDataException("The Kitchen backup contains an invalid cooking record.");
        if((type=="plans"||type=="reminders")&&!ValidDate(GetString(data,"date_value")))throw new InvalidDataException("The Kitchen backup contains an invalid plan or reminder.");
      }
      using(SqliteDb db=Open())foreach(object value in records){Dictionary<string,object> record=(Dictionary<string,object>)value;string type=GetString(record,"record_type"),workspace=SafeLongId(GetString(record,"workspace_long_id"));if(type=="workspaces")continue;if(!workspaces.Contains(workspace)&&db.QueryOne("SELECT long_id FROM local_plugin_records WHERE slug=? AND record_type='workspaces' AND long_id=? AND status='active' LIMIT 1",slug,workspace)==null)throw new InvalidDataException("A Kitchen backup record references an unavailable workspace.");Dictionary<string,object> data=GetObject(record,"data");if(type=="stock_movements")KitchenRequireBackupReference(db,slug,scopedKeys,workspace,"ingredients",GetString(data,"ingredient_long_id"));if(type=="cooking_logs"||type=="plans")KitchenRequireBackupReference(db,slug,scopedKeys,workspace,"recipes",GetString(data,"recipe_long_id"));if(type=="favorites"){KitchenRequireBackupReference(db,slug,scopedKeys,workspace,"profiles",GetString(data,"profile_long_id"));KitchenRequireBackupReference(db,slug,scopedKeys,workspace,"recipes",GetString(data,"recipe_long_id"));}}
      return records;
    }

    private static void KitchenRequireBackupReference(SqliteDb db,string slug,HashSet<string> imported,string workspace,string type,string longId){longId=SafeLongId(longId);if(longId==""||(!imported.Contains(workspace+"|"+type+"|"+longId)&&db.QueryOne("SELECT long_id FROM local_plugin_records WHERE slug=? AND record_type=? AND long_id=? AND workspace_long_id=? AND status='active' LIMIT 1",slug,type,longId,workspace)==null))throw new InvalidDataException("A Kitchen backup record contains an unavailable reference.");}

    private object KitchenImportPreview(string slug,Dictionary<string,object> payload){string source=GetString(payload,"json_text");if(source==""||source.Length>10*1024*1024)throw new InvalidDataException("Choose a Kitchen JSON backup of 10 MB or fewer.");Dictionary<string,object> backup;try{backup=json.DeserializeObject(source) as Dictionary<string,object>;}catch{throw new InvalidDataException("The Kitchen backup is not valid JSON.");}object[] records=KitchenValidateBackup(slug,backup);using(SqliteDb db=Open())db.Execute("INSERT INTO local_plugin_settings(slug,setting_key,setting_value,updated_at)VALUES(?,'import_preview',?,?) ON CONFLICT(slug,setting_key) DO UPDATE SET setting_value=excluded.setting_value,updated_at=excluded.updated_at",slug,source,Now());ProtectDatabaseFile();return new Dictionary<string,object>{{"count",records.Length},{"ready",true}};}

    private object KitchenImportExecute(string slug){string source;using(SqliteDb db=Open()){Dictionary<string,string> preview=db.QueryOne("SELECT setting_value FROM local_plugin_settings WHERE slug=? AND setting_key='import_preview' LIMIT 1",slug);if(preview==null)throw new InvalidOperationException("Preview a Kitchen backup before restoring it.");source=preview["setting_value"];}Dictionary<string,object> backup=json.DeserializeObject(source) as Dictionary<string,object>;object[] records=KitchenValidateBackup(slug,backup);int imported=0,skipped=0;using(SqliteDb db=Open()){db.Exec("BEGIN IMMEDIATE");try{foreach(object value in records){Dictionary<string,object> record=(Dictionary<string,object>)value;string type=GetString(record,"record_type"),longId=SafeLongId(GetString(record,"long_id")),workspace=SafeLongId(GetString(record,"workspace_long_id"));Dictionary<string,object> data=GetObject(record,"data");if(db.QueryOne("SELECT long_id FROM local_plugin_records WHERE slug=? AND record_type=? AND long_id=? LIMIT 1",slug,type,longId)!=null){skipped++;continue;}string now=Now();db.Execute("INSERT INTO local_plugin_records(slug,record_type,long_id,workspace_long_id,data_json,version,status,created_at,updated_at)VALUES(?,?,?,?,?,1,'active',?,?)",slug,type,longId,workspace,json.Serialize(data),now,now);imported++;}db.Execute("DELETE FROM local_plugin_settings WHERE slug=? AND setting_key='import_preview'",slug);db.Exec("COMMIT");}catch{db.Exec("ROLLBACK");throw;}}ProtectDatabaseFile();return new Dictionary<string,object>{{"imported",imported},{"skipped",skipped}};}

    private object KitchenCalendarList(Dictionary<string,object> payload){DateTime start,end;string rawStart=GetString(payload,"start"),rawEnd=GetString(payload,"end");if(!DateTime.TryParseExact(rawStart,"yyyy-MM-dd",CultureInfo.InvariantCulture,DateTimeStyles.None,out start))start=DateTime.Today;if(!DateTime.TryParseExact(rawEnd,"yyyy-MM-dd",CultureInfo.InvariantCulture,DateTimeStyles.None,out end))end=start.AddDays(90);if(end<=start||end>start.AddYears(2))throw new InvalidDataException("Choose a Calendar range of up to two years.");return new Dictionary<string,object>{{"entries",CalendarEntries(start,end).Where(item=>item.ContainsKey("source_id")&&item["source_id"]=="plugin.kitchen-planner").ToList()}};}

    private object KitchenSafeFetch(Dictionary<string,object> payload){
      string raw=GetString(payload,"url").Trim();Uri uri;if(!Uri.TryCreate(raw,UriKind.Absolute,out uri)||uri.Scheme!=Uri.UriSchemeHttps||uri.UserInfo!=""||uri.Port!=443)throw new InvalidDataException("Enter a public HTTPS source URL using the standard secure port.");
      IPAddress address=KitchenResolvePublicAddress(uri.DnsSafeHost);Dictionary<string,object> fetched=KitchenPinnedHttpsGet(uri,address,2*1024*1024);string text=GetString(fetched,"text");
      return new Dictionary<string,object>{{"url",uri.AbsoluteUri},{"content_type",GetString(fetched,"content_type")},{"text",text},{"sha256",HashText(text)}};
    }

    private object KitchenOpenSourceUrl(Dictionary<string,object> payload){string raw=GetString(payload,"url").Trim();Uri uri;if(!Uri.TryCreate(raw,UriKind.Absolute,out uri)||uri.Scheme!=Uri.UriSchemeHttps||uri.UserInfo!=""||uri.Port!=443)throw new InvalidDataException("Only public HTTPS Kitchen source links can be opened.");KitchenResolvePublicAddress(uri.DnsSafeHost);Process.Start(new ProcessStartInfo(uri.AbsoluteUri){UseShellExecute=true});return new Dictionary<string,object>{{"opened",true}};}

    private static IPAddress KitchenResolvePublicAddress(string host){
      if(String.IsNullOrWhiteSpace(host)||host.Equals("localhost",StringComparison.OrdinalIgnoreCase)||host.EndsWith(".localhost",StringComparison.OrdinalIgnoreCase))throw new InvalidDataException("Local and private network source URLs are not allowed.");IPAddress literal;if(IPAddress.TryParse(host,out literal)){if(!KitchenPublicAddress(literal))throw new InvalidDataException("Local and private network source URLs are not allowed.");return literal;}
      IPAddress[] addresses;try{addresses=Dns.GetHostAddresses(host);}catch{throw new InvalidDataException("The source hostname could not be resolved.");}if(addresses.Length==0||addresses.Any(address=>!KitchenPublicAddress(address)))throw new InvalidDataException("The source hostname did not resolve only to public addresses.");return addresses.First(address=>address.AddressFamily==AddressFamily.InterNetworkV6||address.AddressFamily==AddressFamily.InterNetwork);
    }

    private static bool KitchenPublicAddress(IPAddress address){
      if(address==null)return false;if(address.IsIPv4MappedToIPv6)address=address.MapToIPv4();if(IPAddress.IsLoopback(address))return false;byte[] b=address.GetAddressBytes();
      if(address.AddressFamily==AddressFamily.InterNetwork){if(b[0]==0||b[0]==10||b[0]==127||(b[0]==100&&b[1]>=64&&b[1]<=127)||(b[0]==169&&b[1]==254)||(b[0]==172&&b[1]>=16&&b[1]<=31)||(b[0]==192&&b[1]==168)||(b[0]==192&&b[1]==0&&b[2]<=2)||(b[0]==198&&(b[1]==18||b[1]==19))||(b[0]==198&&b[1]==51&&b[2]==100)||(b[0]==203&&b[1]==0&&b[2]==113)||b[0]>=224)return false;return true;}
      if(address.AddressFamily!=AddressFamily.InterNetworkV6||address.IsIPv6LinkLocal||address.IsIPv6SiteLocal||address.IsIPv6Multicast||address.Equals(IPAddress.IPv6None)||address.Equals(IPAddress.IPv6Any)||(b[0]&0xfe)==0xfc)return false;
      if(b[0]==0x20&&b[1]==0x01&&b[2]==0x0d&&b[3]==0xb8)return false;return true;
    }

    private static Dictionary<string,object> KitchenPinnedHttpsGet(Uri uri,IPAddress address,int maxBytes){
      using(TcpClient client=new TcpClient(address.AddressFamily)){
        IAsyncResult pending=client.BeginConnect(address,443,null,null);if(!pending.AsyncWaitHandle.WaitOne(15000)){client.Close();throw new InvalidDataException("The source connection timed out.");}client.EndConnect(pending);client.ReceiveTimeout=15000;client.SendTimeout=15000;
        using(SslStream stream=new SslStream(client.GetStream(),false)){stream.ReadTimeout=15000;stream.WriteTimeout=15000;stream.AuthenticateAsClient(uri.DnsSafeHost,null,SslProtocols.Tls12,true);string path=String.IsNullOrEmpty(uri.PathAndQuery)?"/":uri.PathAndQuery;string request="GET "+path+" HTTP/1.1\r\nHost: "+uri.IdnHost+"\r\nUser-Agent: RacinageFreeKitchen/"+PortablePaths.Version+"\r\nAccept: text/html,text/plain,application/json,application/ld+json\r\nAccept-Encoding: identity\r\nConnection: close\r\n\r\n";byte[] requestBytes=Encoding.ASCII.GetBytes(request);stream.Write(requestBytes,0,requestBytes.Length);stream.Flush();
          using(MemoryStream raw=new MemoryStream()){byte[] chunk=new byte[8192];int read;while((read=stream.Read(chunk,0,chunk.Length))>0){if(raw.Length+read>maxBytes+128*1024)throw new InvalidDataException("The source is larger than 2 MB.");raw.Write(chunk,0,read);}byte[] response=raw.ToArray();int split=KitchenHeaderEnd(response);if(split<0||split>64*1024)throw new InvalidDataException("The source returned invalid HTTP headers.");string headerText=Encoding.ASCII.GetString(response,0,split);string[] lines=headerText.Split(new[]{"\r\n"},StringSplitOptions.None);string[] status=lines[0].Split(' ');int code;if(status.Length<2||!Int32.TryParse(status[1],out code)||code<200||code>=300)throw new InvalidDataException("The source request was rejected. Redirects must be reviewed and submitted explicitly.");Dictionary<string,string> headers=new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);foreach(string line in lines.Skip(1)){int colon=line.IndexOf(':');if(colon>0)headers[line.Substring(0,colon).Trim()]=line.Substring(colon+1).Trim();}string rawType=headers.ContainsKey("Content-Type")?headers["Content-Type"]:"",contentType=rawType.Split(';')[0].Trim().ToLowerInvariant();if(!new[]{"text/html","text/plain","application/json","application/ld+json"}.Contains(contentType))throw new InvalidDataException("Portable safe fetch supports public text, HTML, and JSON sources only.");if(headers.ContainsKey("Content-Encoding")&&!String.Equals(headers["Content-Encoding"],"identity",StringComparison.OrdinalIgnoreCase))throw new InvalidDataException("Compressed source responses are not accepted.");byte[] body=response.Skip(split+4).ToArray();if(headers.ContainsKey("Transfer-Encoding")&&headers["Transfer-Encoding"].IndexOf("chunked",StringComparison.OrdinalIgnoreCase)>=0)body=KitchenDecodeChunked(body,maxBytes);if(body.Length>maxBytes)throw new InvalidDataException("The source is larger than 2 MB.");return new Dictionary<string,object>{{"content_type",contentType},{"text",Encoding.UTF8.GetString(body)}};}
        }
      }
    }

    private static int KitchenHeaderEnd(byte[] bytes){for(int i=0;i+3<bytes.Length;i++)if(bytes[i]==13&&bytes[i+1]==10&&bytes[i+2]==13&&bytes[i+3]==10)return i;return -1;}

    private static byte[] KitchenDecodeChunked(byte[] input,int maxBytes){using(MemoryStream output=new MemoryStream()){int position=0;while(true){int lineEnd=-1;for(int i=position;i+1<input.Length;i++)if(input[i]==13&&input[i+1]==10){lineEnd=i;break;}if(lineEnd<0)throw new InvalidDataException("The source returned invalid chunked data.");string sizeText=Encoding.ASCII.GetString(input,position,lineEnd-position).Split(';')[0].Trim();int size;if(!Int32.TryParse(sizeText,NumberStyles.HexNumber,CultureInfo.InvariantCulture,out size)||size<0)throw new InvalidDataException("The source returned invalid chunked data.");position=lineEnd+2;if(size==0)break;if(position+size+2>input.Length||output.Length+size>maxBytes)throw new InvalidDataException("The source is larger than 2 MB.");output.Write(input,position,size);position+=size;if(input[position]!=13||input[position+1]!=10)throw new InvalidDataException("The source returned invalid chunked data.");position+=2;}return output.ToArray();}}

    private object KitchenLocalAiStatus(){return new Dictionary<string,object>{{"hosted_credits",false},{"loopback_only",true},{"configured",false},{"providers",new[]{"Ollama","LM Studio","custom localhost"}},{"message","Configure a loopback vision-capable provider in the local AI settings. Without vision, imports remain Pending when OCR or text evidence is insufficient."}};}
    private object NameGenBootstrap(string slug){
      using(SqliteDb db=Open()){List<Dictionary<string,object> > records=new List<Dictionary<string,object> >();foreach(Dictionary<string,string> row in db.Query("SELECT record_type,long_id,workspace_long_id,data_json,version,status,created_at,updated_at FROM local_plugin_records WHERE slug=? AND status!='deleted' ORDER BY created_at,long_id",slug)){Dictionary<string,object> item=new Dictionary<string,object>();foreach(KeyValuePair<string,string> pair in row)item[pair.Key]=pair.Value;item["version"]=ToInt(row["version"]);item["data"]=json.DeserializeObject(row["data_json"]);item.Remove("data_json");records.Add(item);}Dictionary<string,object> settings=new Dictionary<string,object>();foreach(Dictionary<string,string> row in db.Query("SELECT setting_key,setting_value FROM local_plugin_settings WHERE slug=?",slug))settings[row["setting_key"]]=row["setting_value"];return new Dictionary<string,object>{{"records",records},{"settings",settings}};}
    }
    private static readonly string[] NameGenRecordTypes={"custom_names","favorites","projects","groups","project_names","group_names","ratings","notes","avoid"};
    private object NameGenSaveRecord(string slug,Dictionary<string,object> payload){
      string type=SafeType(GetString(payload,"record_type")),workspace=SafeLongId(GetString(payload,"workspace_long_id")),requestedLongId=SafeLongId(GetString(payload,"long_id")),importLongId=SafeLongId(GetString(payload,"import_long_id"));Dictionary<string,object> values=GetObject(payload,"data");if(!NameGenRecordTypes.Contains(type))throw new InvalidDataException("Unknown NameGen record type.");if(requestedLongId!=""&&importLongId!="")throw new InvalidDataException("The NameGen record identifier is invalid.");
      if((type=="custom_names"&&GetString(values,"name").Trim()=="")||((type=="projects"||type=="groups")&&GetString(values,"title").Trim()==""))throw new InvalidDataException("A name or title is required.");
      if((type=="project_names"||type=="group_names")&&workspace=="")throw new InvalidDataException("The destination collection is unavailable.");
      if(new[]{"favorites","project_names","group_names","ratings","notes","avoid"}.Contains(type)&&GetString(values,"name_id").Trim()=="")throw new InvalidDataException("The selected name is unavailable.");
      if(type=="ratings"&&(GetLong(values,"rating")<1||GetLong(values,"rating")>5))throw new InvalidDataException("A personal rating must be between 1 and 5.");if(json.Serialize(values).Length>50000)throw new InvalidDataException("The NameGen record is too large.");
      using(SqliteDb db=Open()){
        if(type=="project_names"||type=="group_names"){string collectionType=type=="project_names"?"projects":"groups";if(db.QueryOne("SELECT 1 available FROM local_plugin_records WHERE slug=? AND record_type=? AND long_id=? AND status='active' LIMIT 1",slug,collectionType,workspace)==null)throw new InvalidDataException("The destination collection is unavailable.");}
        string now=Now(),encoded=json.Serialize(values);
        if(requestedLongId!=""){if(db.Execute("UPDATE local_plugin_records SET workspace_long_id=?,data_json=?,version=version+1,updated_at=? WHERE slug=? AND record_type=? AND long_id=? AND status='active'",workspace,encoded,now,slug,type,requestedLongId)<1)throw new InvalidDataException("The NameGen record is unavailable.");ProtectDatabaseFile();return new Dictionary<string,object>{{"long_id",requestedLongId}};}
        if(ToInt(db.Scalar("SELECT COUNT(*) FROM local_plugin_records WHERE slug=? AND record_type=? AND status='active'",slug,type))>=5000)throw new InvalidOperationException("The local NameGen record limit was reached.");
        if(new[]{"favorites","project_names","group_names","ratings","notes","avoid"}.Contains(type)){string nameId=GetString(values,"name_id");Dictionary<string,string> duplicate=db.QueryOne("SELECT long_id FROM local_plugin_records WHERE slug=? AND record_type=? AND workspace_long_id=? AND status='active' AND json_extract(data_json,'$.name_id')=? LIMIT 1",slug,type,workspace,nameId);if(duplicate!=null)return new Dictionary<string,object>{{"long_id",duplicate["long_id"]}};}
        string longId=importLongId!=""?importLongId:NewLongId(type);Dictionary<string,string> collision=db.QueryOne("SELECT record_type FROM local_plugin_records WHERE slug=? AND long_id=? LIMIT 1",slug,longId);if(collision!=null){if(collision["record_type"]==type){db.Execute("UPDATE local_plugin_records SET workspace_long_id=?,data_json=?,status='active',version=version+1,updated_at=? WHERE slug=? AND record_type=? AND long_id=?",workspace,encoded,now,slug,type,longId);ProtectDatabaseFile();return new Dictionary<string,object>{{"long_id",longId}};}longId=NewLongId(type);}
        db.Execute("INSERT INTO local_plugin_records(slug,record_type,long_id,workspace_long_id,data_json,version,status,created_at,updated_at)VALUES(?,?,?,?,?,1,'active',?,?)",slug,type,longId,workspace,encoded,now,now);ProtectDatabaseFile();return new Dictionary<string,object>{{"long_id",longId}};
      }
    }
    private object NameGenDeleteRecord(string slug,Dictionary<string,object> payload){string longId=SafeLongId(GetString(payload,"long_id"));using(SqliteDb db=Open()){Dictionary<string,string> row=db.QueryOne("SELECT record_type FROM local_plugin_records WHERE slug=? AND long_id=? AND status='active' LIMIT 1",slug,longId);db.Execute("UPDATE local_plugin_records SET status='deleted',version=version+1,updated_at=? WHERE slug=? AND long_id=? AND status='active'",Now(),slug,longId);if(row!=null&&(row["record_type"]=="projects"||row["record_type"]=="groups"))db.Execute("UPDATE local_plugin_records SET status='deleted',version=version+1,updated_at=? WHERE slug=? AND workspace_long_id=? AND status='active'",Now(),slug,longId);}ProtectDatabaseFile();return new Dictionary<string,object>{{"deleted",true}};}
    private object NameGenSaveSetting(string slug,Dictionary<string,object> payload){string key=SafeType(GetString(payload,"key")),value=GetString(payload,"value");if(key==""||value.Length>500)throw new InvalidDataException("The NameGen setting is invalid.");using(SqliteDb db=Open())db.Execute("INSERT OR REPLACE INTO local_plugin_settings(slug,setting_key,setting_value,updated_at)VALUES(?,?,?,?)",slug,key,value,Now());ProtectDatabaseFile();return new Dictionary<string,object>{{"saved",true}};}
    private object NameGenExport(string slug){return NameGenBootstrap(slug);}
    private object NameGenImport(string slug,Dictionary<string,object> payload){
      Dictionary<string,object> incoming=GetObject(payload,"data");object raw;object[] records=incoming.TryGetValue("records",out raw)?raw as object[]:null;int inserted=0;Dictionary<string,string> idMap=new Dictionary<string,string>();
      if(records!=null)foreach(bool relationsOnly in new[]{false,true})foreach(object value in records.Take(5000)){Dictionary<string,object> row=value as Dictionary<string,object>;if(row==null)continue;string type=SafeType(GetString(row,"record_type"));bool relation=type=="project_names"||type=="group_names";if(relation!=relationsOnly||!NameGenRecordTypes.Contains(type))continue;string oldId=SafeLongId(GetString(row,"long_id")),oldWorkspace=SafeLongId(GetString(row,"workspace_long_id")),workspace=idMap.ContainsKey(oldWorkspace)?idMap[oldWorkspace]:oldWorkspace;Dictionary<string,object> values=GetObject(row,"data");Dictionary<string,object> saved=NameGenSaveRecord(slug,new Dictionary<string,object>{{"record_type",type},{"workspace_long_id",workspace},{"import_long_id",oldId},{"data",values}}) as Dictionary<string,object>;if(oldId!=""&&saved!=null)idMap[oldId]=Convert.ToString(saved["long_id"],CultureInfo.InvariantCulture);inserted++;}
      Dictionary<string,object> settings=GetObject(incoming,"settings");foreach(KeyValuePair<string,object> setting in settings.Take(30))NameGenSaveSetting(slug,new Dictionary<string,object>{{"key",setting.Key},{"value",Convert.ToString(setting.Value,CultureInfo.InvariantCulture)}});
      return new Dictionary<string,object>{{"inserted",inserted}};
    }

    private object FinanceBootstrap(string slug) {
      using(SqliteDb db=Open()){
        List<Dictionary<string,object> > records=new List<Dictionary<string,object> >();
        foreach(Dictionary<string,string> row in db.Query("SELECT record_type,long_id,workspace_long_id,data_json,version,status,created_at,updated_at FROM local_plugin_records WHERE slug=? AND status!='deleted' ORDER BY created_at,long_id",slug)){
          Dictionary<string,object> item=new Dictionary<string,object>();foreach(KeyValuePair<string,string> pair in row)item[pair.Key]=pair.Value;item["version"]=ToInt(row["version"]);item["data"]=json.DeserializeObject(row["data_json"]);item.Remove("data_json");records.Add(item);
        }
        List<Dictionary<string,object> > attachments=new List<Dictionary<string,object> >();
        foreach(Dictionary<string,string> row in db.Query("SELECT long_id,workspace_long_id,transaction_long_id,original_name,mime_type,file_size,version,status,created_at FROM local_plugin_attachments WHERE slug=? AND status!='deleted' ORDER BY created_at",slug)){Dictionary<string,object> item=new Dictionary<string,object>();foreach(KeyValuePair<string,string> pair in row)item[pair.Key]=pair.Value;item["file_size"]=ToLong(row["file_size"]);item["version"]=ToInt(row["version"]);attachments.Add(item);}
        Dictionary<string,string> user=db.QueryOne("SELECT display_currency FROM users WHERE id=1 LIMIT 1");
        List<Dictionary<string,object> > currencies=new List<Dictionary<string,object> >();foreach(Dictionary<string,string> row in db.Query("SELECT code,name,rate FROM local_currency_rates ORDER BY CASE code WHEN 'USD' THEN 0 ELSE 1 END,code"))currencies.Add(new Dictionary<string,object>{{"code",row["code"]},{"name",row["name"]},{"rate",ToDouble(row["rate"])}});
        return new Dictionary<string,object>{{"records",records},{"attachments",attachments},{"currencies",currencies},{"display_currency",user==null?"USD":user["display_currency"]},{"quotas",FinanceQuotas()}};
      }
    }

    private static Dictionary<string,object> FinanceQuotas() {
      return new Dictionary<string,object>{{"workspaces",25},{"accounts",8},{"transactions",2500},{"categories",100},{"tags",200},{"recurring_rules",100},{"budgets",5},{"goals",5},{"debts",5},{"investment_accounts",1},{"investments",25},{"scenarios",1},{"circles",3},{"circle_members",25},{"circle_entries",1000},{"attachments_per_transaction",20}};
    }

    private object FinanceSave(string slug,Dictionary<string,object> payload) {
      using(SqliteDb db=Open()){db.Exec("BEGIN IMMEDIATE");try{Dictionary<string,object> result=FinanceSaveRecord(db,slug,payload);db.Exec("COMMIT");ProtectDatabaseFile();return result;}catch{db.Exec("ROLLBACK");throw;}}
    }

    private object FinanceBatchSave(string slug,Dictionary<string,object> payload) {
      object raw;if(!payload.TryGetValue("records",out raw))throw new InvalidOperationException("No finance records were supplied.");
      object[] rows=raw as object[];if(rows==null||rows.Length>5000)throw new InvalidOperationException("The finance batch is invalid or too large.");
      int added=0,duplicates=0,invalid=0,quota=0;string sampleWorkspace="";
      using(SqliteDb db=Open()){db.Exec("BEGIN IMMEDIATE");try{
        List<Dictionary<string,object> > transfers=new List<Dictionary<string,object> >();
        foreach(object value in rows){Dictionary<string,object> record=value as Dictionary<string,object>;if(record==null){invalid++;continue;}try{Dictionary<string,object> result=FinanceSaveRecord(db,slug,record);if(Convert.ToBoolean(result["duplicate"],CultureInfo.InvariantCulture))duplicates++;else added++;if(Convert.ToString(record.ContainsKey("record_type")?record["record_type"]:"",CultureInfo.InvariantCulture)=="workspaces"&&sampleWorkspace=="")sampleWorkspace=Convert.ToString(result["long_id"],CultureInfo.InvariantCulture);Dictionary<string,object> rowData=GetObject(record,"data");if(rowData.ContainsKey("transfer_group"))transfers.Add(record);}catch(InvalidDataException){invalid++;}catch(InvalidOperationException exception){if(exception.Message.IndexOf("limit",StringComparison.OrdinalIgnoreCase)>=0||exception.Message.IndexOf("allows",StringComparison.OrdinalIgnoreCase)>=0)quota++;else throw;}}
        if(transfers.Count>0)ValidateTransferBatch(transfers);
        if(payload.ContainsKey("sample")&&ToBool(payload["sample"])&&sampleWorkspace!="")SeedFinanceSample(db,slug,sampleWorkspace);
        db.Exec("COMMIT");
      }catch{db.Exec("ROLLBACK");throw;}}
      ProtectDatabaseFile();return new Dictionary<string,object>{{"added",added},{"duplicates",duplicates},{"invalid",invalid},{"quota",quota},{"workspace_long_id",sampleWorkspace}};
    }

    private Dictionary<string,object> FinanceSaveRecord(SqliteDb db,string slug,Dictionary<string,object> payload) {
      string type=SafeType(GetString(payload,"record_type")),longId=SafeLongId(GetString(payload,"long_id")),workspace=SafeLongId(GetString(payload,"workspace_long_id"));Dictionary<string,object> values=GetObject(payload,"data");
      if(!FinanceRecordTypes.Contains(type))throw new InvalidDataException("Unknown finance record type.");
      string encoded=json.Serialize(values);if(encoded.Length<2||encoded.Length>65536)throw new InvalidDataException("A finance record is invalid or too large.");
      bool editing=longId!="";Dictionary<string,string> current=editing?db.QueryOne("SELECT version,status FROM local_plugin_records WHERE slug=? AND record_type=? AND long_id=? LIMIT 1",slug,type,longId):null;
      if(editing&&current==null)throw new InvalidOperationException("The finance record no longer exists.");
      if(editing&&ToInt(current["version"])!=GetInt(payload,"version"))throw new InvalidOperationException("This finance record changed in another window. Reopen it and try again.");
      if(type=="workspaces")workspace="";else if(workspace==""||db.QueryOne("SELECT long_id FROM local_plugin_records WHERE slug=? AND record_type='workspaces' AND long_id=? AND status='active' LIMIT 1",slug,workspace)==null)throw new InvalidDataException("The finance workspace is unavailable.");
      ValidateFinanceData(db,slug,type,workspace,values,editing?longId:"");
      if(!editing)EnforceFinanceQuota(db,slug,type,workspace,values);
      if(type=="transactions"&&values.ContainsKey("fingerprint")&&GetString(values,"fingerprint")!=""){
        Dictionary<string,string> duplicate=db.QueryOne("SELECT long_id FROM local_plugin_records WHERE slug=? AND record_type='transactions' AND workspace_long_id=? AND status='active' AND json_extract(data_json,'$.fingerprint')=? AND long_id!=? LIMIT 1",slug,workspace,GetString(values,"fingerprint"),longId);
        if(duplicate!=null)return new Dictionary<string,object>{{"long_id",duplicate["long_id"]},{"duplicate",true}};
      }
      string now=Now();if(!editing){longId=NewLongId(type);db.Execute("INSERT INTO local_plugin_records(slug,record_type,long_id,workspace_long_id,data_json,version,status,created_at,updated_at)VALUES(?,?,?,?,?,1,'active',?,?)",slug,type,longId,workspace,encoded,now,now);}
      else db.Execute("UPDATE local_plugin_records SET data_json=?,version=version+1,status='active',updated_at=? WHERE slug=? AND record_type=? AND long_id=?",encoded,now,slug,type,longId);
      return new Dictionary<string,object>{{"long_id",longId},{"version",editing?GetInt(payload,"version")+1:1},{"duplicate",false}};
    }

    private static readonly HashSet<string> FinanceRecordTypes=new HashSet<string>(StringComparer.Ordinal){"workspaces","accounts","transactions","categories","tags","recurring_rules","budgets","goals","debts","debt_payments","investments","scenarios","circles","circle_members","circle_entries"};
    private static string SafeType(string value){return String.IsNullOrEmpty(value)?"":value.Replace("-","_");}
    private static string SafeLongId(string value){if(String.IsNullOrEmpty(value))return "";if(value.Length>80)throw new InvalidDataException("A finance identifier is too long.");foreach(char c in value)if(!Char.IsLetterOrDigit(c)&&c!='_'&&c!='-')throw new InvalidDataException("A finance identifier is invalid.");return value;}
    private static string NewLongId(string type){return "local_"+type.TrimEnd('s')+"_"+Guid.NewGuid().ToString("N");}

    private void EnforceFinanceQuota(SqliteDb db,string slug,string type,string workspace,Dictionary<string,object> values) {
      int limit=-1;if(type=="workspaces")limit=25;else if(type=="accounts")limit=8;else if(type=="transactions")limit=2500;else if(type=="categories")limit=100;else if(type=="tags")limit=200;else if(type=="recurring_rules")limit=100;else if(type=="budgets"||type=="goals"||type=="debts")limit=5;else if(type=="investments")limit=25;else if(type=="scenarios")limit=1;else if(type=="circles")limit=3;else if(type=="circle_entries")limit=1000;
      string countSql=type=="workspaces"?"SELECT COUNT(*) FROM local_plugin_records WHERE slug=? AND record_type=? AND status='active'":"SELECT COUNT(*) FROM local_plugin_records WHERE slug=? AND record_type=? AND workspace_long_id=? AND status='active'";int count=type=="workspaces"?ToInt(db.Scalar(countSql,slug,type)):ToInt(db.Scalar(countSql,slug,type,workspace));
      if(limit>=0&&count>=limit)throw new InvalidOperationException("The Lite finance "+type.Replace('_',' ')+" limit has been reached.");
      if(type=="accounts"&&GetString(values,"account_type")=="investment"&&ToInt(db.Scalar("SELECT COUNT(*) FROM local_plugin_records WHERE slug=? AND record_type='accounts' AND workspace_long_id=? AND status='active' AND json_extract(data_json,'$.account_type')='investment'",slug,workspace))>=1)throw new InvalidOperationException("Lite allows one investment account per workspace.");
      if(type=="circle_members"){
        string circle=GetString(values,"circle");if(ToInt(db.Scalar("SELECT COUNT(*) FROM local_plugin_records WHERE slug=? AND record_type='circle_members' AND workspace_long_id=? AND status='active' AND json_extract(data_json,'$.circle')=?",slug,workspace,circle))>=25)throw new InvalidOperationException("A Lite circle allows 25 people.");
      }
    }

    private void ValidateFinanceData(SqliteDb db,string slug,string type,string workspace,Dictionary<string,object> values,string longId) {
      if(new[]{"workspaces","accounts","categories","tags","recurring_rules","budgets","goals","debts","investments","scenarios","circles","circle_members"}.Contains(type)){string name=GetString(values,"name").Trim();if(name==""||name.Length>190)throw new InvalidDataException("A finance name is required.");}
      if(type=="workspaces"){ValidateCurrency(db,values);if(!new[]{"personal","family","group"}.Contains(GetString(values,"workspace_kind")))throw new InvalidDataException("Choose a valid workspace type.");}
      if(type=="categories"){string categoryType=GetString(values,"category_type"),name=GetString(values,"name").Trim();if(!new[]{"income","expense","transfer"}.Contains(categoryType))throw new InvalidDataException("The category type is invalid.");if(db.QueryOne("SELECT long_id FROM local_plugin_records WHERE slug=? AND record_type='categories' AND workspace_long_id=? AND status='active' AND lower(json_extract(data_json,'$.name'))=lower(?) AND json_extract(data_json,'$.category_type')=? AND long_id!=? LIMIT 1",slug,workspace,name,categoryType,longId)!=null)throw new InvalidDataException("A category with this name and type already exists.");string parent=GetString(values,"parent");if(parent!=""){if(parent==longId)throw new InvalidDataException("A category cannot be its own parent.");RequireReference(db,slug,workspace,"categories",parent);}}
      if(type=="tags"){string name=GetString(values,"name").Trim();if(name.Length>80)throw new InvalidDataException("The tag name is too long.");if(db.QueryOne("SELECT long_id FROM local_plugin_records WHERE slug=? AND record_type='tags' AND workspace_long_id=? AND status='active' AND lower(json_extract(data_json,'$.name'))=lower(?) AND long_id!=? LIMIT 1",slug,workspace,name,longId)!=null)throw new InvalidDataException("A tag with this name already exists.");}
      if(type=="accounts"){
        string accountType=GetString(values,"account_type");if(!new[]{"cash","checking","savings","mobile_money","credit_card","loan","investment","other_asset","other_liability"}.Contains(accountType))throw new InvalidDataException("The account type is invalid.");ValidateCurrency(db,values);ValidateSnapshot(values,"opening_cents","opening_usd_cents");
        if(accountType=="investment"&&ToInt(db.Scalar("SELECT COUNT(*) FROM local_plugin_records WHERE slug=? AND record_type='accounts' AND workspace_long_id=? AND status='active' AND json_extract(data_json,'$.account_type')='investment' AND long_id!=?",slug,workspace,longId))>=1)throw new InvalidDataException("Lite allows one investment account per workspace.");
      }
      if(type=="transactions"||type=="recurring_rules"){
        string transactionType=GetString(values,"transaction_type");if(type=="transactions"&&!new[]{"income","expense","transfer_in","transfer_out"}.Contains(transactionType))throw new InvalidDataException("The transaction type is invalid.");if(type=="recurring_rules"&&!new[]{"income","expense"}.Contains(transactionType))throw new InvalidDataException("The recurring type is invalid.");
        long native=GetLong(values,"amount_cents"),usd=GetLong(values,"amount_usd_cents");if(native<=0||usd<=0)throw new InvalidDataException("The finance amount must be positive.");ValidateCurrency(db,values);ValidateSnapshot(values,"amount_cents","amount_usd_cents");
        string account=SafeLongId(GetString(values,"account"));if(db.QueryOne("SELECT long_id FROM local_plugin_records WHERE slug=? AND record_type='accounts' AND long_id=? AND workspace_long_id=? AND status='active' LIMIT 1",slug,account,workspace)==null)throw new InvalidDataException("The selected account is unavailable.");
        string date=GetString(values,type=="transactions"?"transaction_date":"next_date");if(!ValidDate(date))throw new InvalidDataException("Choose a valid calendar date.");
        if(type=="recurring_rules"&&!new[]{"weekly","monthly","quarterly","yearly"}.Contains(GetString(values,"frequency")))throw new InvalidDataException("The recurring frequency is invalid.");
        if(type=="recurring_rules"){string recurringKind=GetString(values,"recurring_kind");if(!new[]{"income","bill","subscription"}.Contains(recurringKind)||(transactionType=="income")!=(recurringKind=="income"))throw new InvalidDataException("The recurring kind does not match the transaction direction.");string category=GetString(values,"category");if(category!="")RequireReference(db,slug,workspace,"categories",category);}
        if(type=="transactions"&&transactionType.StartsWith("transfer_",StringComparison.Ordinal)){RequireReference(db,slug,workspace,"accounts",GetString(values,"destination_account"));if(GetString(values,"destination_account")==account||GetString(values,"transfer_group")=="")throw new InvalidDataException("A transfer needs two accounts and one group.");}
        if(type=="transactions"&&values.ContainsKey("splits")){object[] splits=values["splits"] as object[];if(splits!=null&&splits.Length>0){long total=0;foreach(object splitValue in splits){Dictionary<string,object> split=splitValue as Dictionary<string,object>;if(split==null||GetString(split,"category").Trim()==""||GetLong(split,"amount_cents")<=0)throw new InvalidDataException("A split transaction row is invalid.");RequireReference(db,slug,workspace,"categories",GetString(split,"category"));total+=GetLong(split,"amount_cents");}if(total!=native)throw new InvalidDataException("Split amounts must equal the transaction amount.");}}
      }
      if(type=="budgets"){ValidateCurrency(db,values);ValidateSnapshot(values,"planned_cents","planned_usd_cents");if(!ValidDate(GetString(values,"start_date")))throw new InvalidDataException("Choose a valid budget date.");string category=GetString(values,"category");if(category!="")RequireReference(db,slug,workspace,"categories",category);}
      if(type=="goals"){ValidateCurrency(db,values);ValidateSnapshot(values,"target_cents","target_usd_cents");ValidateSnapshot(values,"current_cents","current_usd_cents");string date=GetString(values,"target_date");if(date!=""&&!ValidDate(date))throw new InvalidDataException("Choose a valid goal date.");}
      if(type=="debts"){ValidateCurrency(db,values);ValidateSnapshot(values,"balance_cents","balance_usd_cents");ValidateSnapshot(values,"original_cents","original_usd_cents");if(GetLong(values,"apr_bps")<0)throw new InvalidDataException("APR cannot be negative.");string date=GetString(values,"next_due_date");if(date!=""&&!ValidDate(date))throw new InvalidDataException("Choose a valid debt due date.");}
      if(type=="debt_payments"){if(GetLong(values,"amount_cents")<=0||GetLong(values,"principal_cents")<=0)throw new InvalidDataException("The debt payment is invalid.");if(!ValidDate(GetString(values,"payment_date")))throw new InvalidDataException("Choose a valid payment date.");RequireReference(db,slug,workspace,"debts",GetString(values,"debt"));}
      if(type=="investments"){ValidateCurrency(db,values);ValidateSnapshot(values,"cost_basis_cents","cost_basis_usd_cents");ValidateSnapshot(values,"current_value_cents","current_value_usd_cents");RequireReference(db,slug,workspace,"accounts",GetString(values,"account"));Dictionary<string,string> investmentAccount=db.QueryOne("SELECT long_id FROM local_plugin_records WHERE slug=? AND record_type='accounts' AND long_id=? AND status='active' AND json_extract(data_json,'$.account_type')='investment' LIMIT 1",slug,GetString(values,"account"));if(investmentAccount==null)throw new InvalidDataException("Choose an investment account.");string date=GetString(values,"valuation_date");if(date!=""&&!ValidDate(date))throw new InvalidDataException("Choose a valid valuation date.");}
      if(type=="scenarios"){ValidateCurrency(db,values);ValidateSnapshot(values,"monthly_adjustment_cents","monthly_adjustment_usd_cents",true);int months=GetInt(values,"months");if(months<1||months>120)throw new InvalidDataException("Forecast months must be between 1 and 120.");}
      if(type=="circles"){ValidateCurrency(db,values);Dictionary<string,string> owner=db.QueryOne("SELECT data_json FROM local_plugin_records WHERE slug=? AND record_type='workspaces' AND long_id=? AND status='active' LIMIT 1",slug,workspace);Dictionary<string,object> workspaceData=owner==null?null:json.DeserializeObject(owner["data_json"]) as Dictionary<string,object>;if(workspaceData==null||GetString(workspaceData,"workspace_kind")=="personal")throw new InvalidDataException("Change this workspace to Family or Group before creating a circle.");}
      if(type=="circle_members")RequireReference(db,slug,workspace,"circles",GetString(values,"circle"));
      if(type=="circle_entries"){if(GetLong(values,"amount_cents")<=0)throw new InvalidDataException("The circle amount must be positive.");ValidateCurrency(db,values);ValidateSnapshot(values,"amount_cents","amount_usd_cents");RequireReference(db,slug,workspace,"circles",GetString(values,"circle"));RequireReference(db,slug,workspace,"circle_members",GetString(values,"member"));Dictionary<string,string> member=db.QueryOne("SELECT long_id FROM local_plugin_records WHERE slug=? AND record_type='circle_members' AND long_id=? AND status='active' AND json_extract(data_json,'$.circle')=? LIMIT 1",slug,GetString(values,"member"),GetString(values,"circle"));if(member==null)throw new InvalidDataException("The selected person is not in that circle.");if(!new[]{"contribution","loan","repayment","withdrawal"}.Contains(GetString(values,"entry_type")))throw new InvalidDataException("The circle entry type is invalid.");if(!ValidDate(GetString(values,"entry_date")))throw new InvalidDataException("Choose a valid circle date.");}
    }

    private static void ValidateTransferBatch(List<Dictionary<string,object> > rows) {
      Dictionary<string,List<Dictionary<string,object> > > groups=new Dictionary<string,List<Dictionary<string,object> > >();
      foreach(Dictionary<string,object> row in rows){Dictionary<string,object> values=GetObject(row,"data");string group=GetString(values,"transfer_group");if(!groups.ContainsKey(group))groups[group]=new List<Dictionary<string,object> >();groups[group].Add(values);}
      foreach(List<Dictionary<string,object> > group in groups.Values){if(group.Count!=2||!group.Any(row=>GetString(row,"transaction_type")=="transfer_out")||!group.Any(row=>GetString(row,"transaction_type")=="transfer_in")||Math.Abs(GetLong(group[0],"amount_usd_cents")-GetLong(group[1],"amount_usd_cents"))>1)throw new InvalidOperationException("A local transfer must contain one balanced outflow and inflow.");}
    }

    private static void RequireReference(SqliteDb db,string slug,string workspace,string type,string longId){longId=SafeLongId(longId);if(db.QueryOne("SELECT long_id FROM local_plugin_records WHERE slug=? AND record_type=? AND long_id=? AND workspace_long_id=? AND status='active' LIMIT 1",slug,type,longId,workspace)==null)throw new InvalidDataException("A linked finance record is unavailable.");}
    private static bool ValidDate(string value){DateTime date;return value!=null&&value.Length==10&&DateTime.TryParseExact(value,"yyyy-MM-dd",CultureInfo.InvariantCulture,DateTimeStyles.None,out date);}
    private static void ValidateSnapshot(Dictionary<string,object> values,string nativeField,string usdField,bool signed=false){long native=GetLong(values,nativeField),usd=GetLong(values,usdField);double rate=GetDouble(values,"fx_rate");if(rate<=0||(!signed&&(native<0||usd<0))||Math.Abs(Math.Round(native/rate)-usd)>1)throw new InvalidDataException("The finance currency snapshot is invalid.");}
    private static void ValidateCurrency(SqliteDb db,Dictionary<string,object> values){string code=GetString(values,"native_currency_code").ToUpperInvariant();if(code.Length!=3||db.QueryOne("SELECT code FROM local_currency_rates WHERE code=? AND rate>0 LIMIT 1",code)==null)throw new InvalidDataException("Choose an active local currency.");}

    private object FinanceDelete(string slug,Dictionary<string,object> payload) {
      string type=SafeType(GetString(payload,"record_type")),longId=SafeLongId(GetString(payload,"long_id"));if(!FinanceRecordTypes.Contains(type))throw new InvalidOperationException("Unknown finance record type.");
      bool archived=type=="categories"||type=="tags";using(SqliteDb db=Open()){Dictionary<string,string> row=db.QueryOne("SELECT version,data_json FROM local_plugin_records WHERE slug=? AND record_type=? AND long_id=? AND status='active' LIMIT 1",slug,type,longId);if(row==null)throw new InvalidOperationException("The finance record was already deleted.");if(ToInt(row["version"])!=GetInt(payload,"version"))throw new InvalidOperationException("This finance record changed. Reopen it and try again.");string now=Now();db.Exec("BEGIN IMMEDIATE");try{if(archived){Dictionary<string,object> values=(json.DeserializeObject(row["data_json"]) as Dictionary<string,object>)??new Dictionary<string,object>();values["archived"]=true;db.Execute("UPDATE local_plugin_records SET data_json=?,version=version+1,updated_at=? WHERE slug=? AND record_type=? AND long_id=?",json.Serialize(values),now,slug,type,longId);}else db.Execute("UPDATE local_plugin_records SET status='deleted',version=version+1,updated_at=? WHERE slug=? AND record_type=? AND long_id=?",now,slug,type,longId);if(type=="workspaces"){db.Execute("UPDATE local_plugin_records SET status='deleted',version=version+1,updated_at=? WHERE slug=? AND workspace_long_id=? AND status='active'",now,slug,longId);db.Execute("UPDATE local_plugin_attachments SET status='deleted',version=version+1,updated_at=? WHERE slug=? AND workspace_long_id=? AND status='active'",now,slug,longId);}db.Exec("COMMIT");}catch{db.Exec("ROLLBACK");throw;}}ProtectDatabaseFile();return new Dictionary<string,object>{{archived?"archived":"deleted",true}};
    }

    private object FinanceSettings(Dictionary<string,object> payload) {
      string display=GetString(payload,"display_currency").ToUpperInvariant();object raw;object[] rates=payload.TryGetValue("currencies",out raw)?raw as object[]:null;
      using(SqliteDb db=Open()){if(rates!=null)foreach(object value in rates){Dictionary<string,object> rate=value as Dictionary<string,object>;if(rate==null)continue;string code=GetString(rate,"code").ToUpperInvariant(),name=GetString(rate,"name").Trim();double amount=GetDouble(rate,"rate");if(code.Length!=3||name==""||amount<=0||amount>1000000000)throw new InvalidDataException("A local currency rate is invalid.");db.Execute("INSERT OR REPLACE INTO local_currency_rates(code,name,rate,updated_at)VALUES(?,?,?,?)",code,name,amount,Now());}if(db.QueryOne("SELECT code FROM local_currency_rates WHERE code=? LIMIT 1",display)==null)throw new InvalidDataException("The display currency is unavailable.");db.Execute("UPDATE users SET display_currency=?,updated_at=? WHERE id=1",display,Now());}ProtectDatabaseFile();return new Dictionary<string,object>{{"saved",true}};
    }

    private object FinanceAttachmentUpload(string slug,Dictionary<string,object> payload) {
      // ponytail: base64 is bounded at 25 MB; switch to a streamed native picker only if memory profiling warrants it.
      string workspace=SafeLongId(GetString(payload,"workspace_long_id")),transaction=SafeLongId(GetString(payload,"transaction_long_id")),name=Path.GetFileName(GetString(payload,"original_name"));if(name==""||name.Length>255)throw new InvalidDataException("The attachment name is invalid.");
      byte[] content;try{content=Convert.FromBase64String(GetString(payload,"content_base64"));}catch{throw new InvalidDataException("The attachment content is invalid.");}if(content.Length<4||content.Length>25*1024*1024)throw new InvalidDataException("Attachments must be no larger than 25 MB.");
      string mime=DetectFinanceMime(content);if(mime=="")throw new InvalidDataException("Only private JPG, PNG, WebP, and PDF attachments are supported.");
      using(SqliteDb db=Open()){RequireReference(db,slug,workspace,"transactions",transaction);if(ToInt(db.Scalar("SELECT COUNT(*) FROM local_plugin_attachments WHERE slug=? AND transaction_long_id=? AND status='active'",slug,transaction))>=20)throw new InvalidOperationException("A transaction allows 20 attachments.");string longId="local_attachment_"+Guid.NewGuid().ToString("N"),extension=mime=="image/jpeg"?".jpg":mime=="image/png"?".png":mime=="image/webp"?".webp":".pdf",relative=Path.Combine("plugins",slug,"attachments",longId+extension),path=Path.GetFullPath(Path.Combine(PortablePaths.MediaDir,relative));if(!path.StartsWith(Path.GetFullPath(PortablePaths.MediaDir)+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase))throw new InvalidDataException("The attachment path is invalid.");Directory.CreateDirectory(Path.GetDirectoryName(path));File.WriteAllBytes(path,content);string now=Now();db.Execute("INSERT INTO local_plugin_attachments(slug,long_id,workspace_long_id,transaction_long_id,relative_path,original_name,mime_type,file_size,version,status,created_at,updated_at)VALUES(?,?,?,?,?,?,?,?,1,'active',?,?)",slug,longId,workspace,transaction,relative,name,mime,content.Length,now,now);ProtectDatabaseFile();return new Dictionary<string,object>{{"long_id",longId}};}
    }

    private object FinanceAttachmentGet(string slug,Dictionary<string,object> payload) {
      string longId=SafeLongId(GetString(payload,"long_id"));using(SqliteDb db=Open()){Dictionary<string,string> row=db.QueryOne("SELECT relative_path,original_name,mime_type,file_size FROM local_plugin_attachments WHERE slug=? AND long_id=? AND status='active' LIMIT 1",slug,longId);if(row==null)throw new InvalidOperationException("The attachment is unavailable.");string path=Path.GetFullPath(Path.Combine(PortablePaths.MediaDir,row["relative_path"]));if(!path.StartsWith(Path.GetFullPath(PortablePaths.MediaDir)+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase)||!File.Exists(path))throw new InvalidOperationException("The private attachment file is missing.");return new Dictionary<string,object>{{"original_name",row["original_name"]},{"mime_type",row["mime_type"]},{"content_base64",Convert.ToBase64String(File.ReadAllBytes(path))}};}
    }

    private object FinanceAttachmentDelete(string slug,Dictionary<string,object> payload) {
      string longId=SafeLongId(GetString(payload,"long_id"));using(SqliteDb db=Open())db.Execute("UPDATE local_plugin_attachments SET status='deleted',version=version+1,updated_at=? WHERE slug=? AND long_id=? AND status='active'",Now(),slug,longId);ProtectDatabaseFile();return new Dictionary<string,object>{{"deleted",true}};
    }

    private static string DetectFinanceMime(byte[] bytes){if(bytes.Length>=4&&bytes[0]==0xff&&bytes[1]==0xd8&&bytes[2]==0xff)return "image/jpeg";if(bytes.Length>=8&&bytes[0]==0x89&&bytes[1]==0x50&&bytes[2]==0x4e&&bytes[3]==0x47)return "image/png";if(bytes.Length>=12&&Encoding.ASCII.GetString(bytes,0,4)=="RIFF"&&Encoding.ASCII.GetString(bytes,8,4)=="WEBP")return "image/webp";if(bytes.Length>=5&&Encoding.ASCII.GetString(bytes,0,5)=="%PDF-")return "application/pdf";return "";}

    private void SeedFinanceSample(SqliteDb db,string slug,string workspace) {
      string account=NewLongId("accounts"),category=NewLongId("categories"),tag=NewLongId("tags"),now=Now();Dictionary<string,object> accountData=new Dictionary<string,object>{{"name","Everyday checking"},{"account_type","checking"},{"native_currency_code","USD"},{"opening","2500.00"},{"opening_cents",250000},{"opening_usd_cents",250000},{"fx_rate",1}};
      db.Execute("INSERT INTO local_plugin_records(slug,record_type,long_id,workspace_long_id,data_json,version,status,created_at,updated_at)VALUES(?,?,?,?,?,1,'active',?,?)",slug,"accounts",account,workspace,json.Serialize(accountData),now,now);
      db.Execute("INSERT INTO local_plugin_records(slug,record_type,long_id,workspace_long_id,data_json,version,status,created_at,updated_at)VALUES(?,?,?,?,?,1,'active',?,?)",slug,"categories",category,workspace,json.Serialize(new Dictionary<string,object>{{"name","Food"},{"category_type","expense"},{"parent",""}}),now,now);
      db.Execute("INSERT INTO local_plugin_records(slug,record_type,long_id,workspace_long_id,data_json,version,status,created_at,updated_at)VALUES(?,?,?,?,?,1,'active',?,?)",slug,"tags",tag,workspace,json.Serialize(new Dictionary<string,object>{{"name","Sample"},{"normalized_name","sample"}}),now,now);
      foreach(Dictionary<string,object> values in new[]{new Dictionary<string,object>{{"transaction_type","income"},{"account",account},{"value","3200.00"},{"amount_cents",320000},{"amount_usd_cents",320000},{"fx_rate",1},{"native_currency_code","USD"},{"transaction_date",todayUtc()},{"payee","Salary"},{"tags","Sample"},{"state","cleared"},{"note","Removable sample data"},{"splits",new object[0]}},new Dictionary<string,object>{{"transaction_type","expense"},{"account",account},{"value","190.00"},{"amount_cents",19000},{"amount_usd_cents",19000},{"fx_rate",1},{"native_currency_code","USD"},{"transaction_date",todayUtc()},{"payee","Groceries"},{"tags","Sample"},{"state","cleared"},{"note","Removable sample data"},{"splits",new[]{new Dictionary<string,object>{{"category",category},{"category_name","Food"},{"amount_cents",19000},{"note",""}}}}}})db.Execute("INSERT INTO local_plugin_records(slug,record_type,long_id,workspace_long_id,data_json,version,status,created_at,updated_at)VALUES(?,?,?,?,?,1,'active',?,?)",slug,"transactions",NewLongId("transactions"),workspace,json.Serialize(values),now,now);
    }
    private static string todayUtc(){return DateTime.UtcNow.ToString("yyyy-MM-dd",CultureInfo.InvariantCulture);}

    private static Dictionary<string,object> GetObject(Dictionary<string,object> values,string key){object value;if(!values.TryGetValue(key,out value)||!(value is Dictionary<string,object>))return new Dictionary<string,object>();return (Dictionary<string,object>)value;}
    private static object[] GetArray(Dictionary<string,object> values,string key){object value;if(values==null||!values.TryGetValue(key,out value)||value==null)return new object[0];object[] array=value as object[];if(array!=null)return array;ArrayList list=value as ArrayList;return list==null?new object[0]:list.ToArray();}
    private static string GetString(Dictionary<string,object> values,string key){object value;return values.TryGetValue(key,out value)&&value!=null?Convert.ToString(value,CultureInfo.InvariantCulture):"";}
    private static int GetInt(Dictionary<string,object> values,string key){return (int)Math.Max(Int32.MinValue,Math.Min(Int32.MaxValue,GetLong(values,key)));}
    private static long GetLong(Dictionary<string,object> values,string key){object value;if(!values.TryGetValue(key,out value)||value==null)return 0;long parsed;return Int64.TryParse(Convert.ToString(value,CultureInfo.InvariantCulture),NumberStyles.Any,CultureInfo.InvariantCulture,out parsed)?parsed:(long)ToDouble(value);}
    private static double GetDouble(Dictionary<string,object> values,string key){object value;return values.TryGetValue(key,out value)?ToDouble(value):0;}
    private static double ToDouble(object value){double parsed;return Double.TryParse(Convert.ToString(value,CultureInfo.InvariantCulture),NumberStyles.Any,CultureInfo.InvariantCulture,out parsed)?parsed:0;}
    private static long ToLong(object value){long parsed;return Int64.TryParse(Convert.ToString(value,CultureInfo.InvariantCulture),out parsed)?parsed:0;}
    private static bool ToBool(object value){bool parsed;return value!=null&&(Boolean.TryParse(Convert.ToString(value,CultureInfo.InvariantCulture),out parsed)?parsed:Convert.ToString(value,CultureInfo.InvariantCulture)=="1");}

    internal bool HasUser() {
      using (SqliteDb db = Open()) {
        return ToInt(db.Scalar("SELECT COUNT(*) FROM users")) > 0;
      }
    }

    internal void CreateAccount(string displayName, string username, string password, string familyName) {
      using (SqliteDb db = Open()) {
        if (ToInt(db.Scalar("SELECT COUNT(*) FROM users")) > 0) throw new InvalidOperationException("The local Free account already exists.");
        string now = Now();
        db.Exec("BEGIN IMMEDIATE");
        try {
          db.Execute("INSERT INTO users (id, username, display_name, password_hash, created_at, updated_at) VALUES (1, ?, ?, ?, ?, ?)", username, displayName, PasswordHasher.Hash(password), now, now);
          db.Execute("INSERT INTO families (id, name, location, story, created_at, updated_at) VALUES (1, ?, '', '', ?, ?)", familyName, now, now);
          RecordChange(db, "users", "1", "upsert", HashText(username + "|" + displayName));
          RecordChange(db, "families", "1", "upsert", HashText(familyName));
          db.Exec("COMMIT");
        } catch {
          db.Exec("ROLLBACK");
          throw;
        }
      }
      ProtectDatabaseFile();
    }

    internal bool ValidateLogin(string username, string password) {
      using (SqliteDb db = Open()) {
        Dictionary<string, string> row = db.QueryOne("SELECT password_hash FROM users WHERE username = ? LIMIT 1", username);
        return row != null && PasswordHasher.Verify(password, row["password_hash"]);
      }
    }

    internal Dictionary<string, string> GetFamily() {
      using (SqliteDb db = Open()) {
        Dictionary<string, string> row = db.QueryOne("SELECT name, location, story FROM families WHERE id = 1 LIMIT 1");
        if (row == null) {
          row = new Dictionary<string, string>();
          row["name"] = "My Family";
          row["location"] = "";
          row["story"] = "";
        }
        return row;
      }
    }

    internal List<Dictionary<string,string> > GetCurrencyRates(){using(SqliteDb db=Open())return db.Query("SELECT code,name,printf('%.8g',rate) rate FROM local_currency_rates ORDER BY CASE code WHEN 'USD' THEN 0 ELSE 1 END,code");}
    internal string GetDisplayCurrency(){using(SqliteDb db=Open()){object value=db.Scalar("SELECT display_currency FROM users WHERE id=1 LIMIT 1");return value==null?"USD":Convert.ToString(value,CultureInfo.InvariantCulture);}}
    internal void SaveCurrencySettings(string display,string lines){
      display=(display??"USD").Trim().ToUpperInvariant();Dictionary<string,Dictionary<string,object> > parsed=new Dictionary<string,Dictionary<string,object> >(StringComparer.OrdinalIgnoreCase);
      foreach(string line in (lines??"").Split(new[]{"\r\n","\n"},StringSplitOptions.RemoveEmptyEntries)){string[] parts=line.Split('|');if(parts.Length!=3)throw new InvalidOperationException("Each currency line must contain CODE | Name | rate.");string code=parts[0].Trim().ToUpperInvariant(),name=parts[1].Trim();double rate;if(code.Length!=3||name==""||!Double.TryParse(parts[2].Trim(),NumberStyles.Any,CultureInfo.InvariantCulture,out rate)||rate<=0||rate>1000000000)throw new InvalidOperationException("A currency line is invalid.");parsed[code]=new Dictionary<string,object>{{"code",code},{"name",name},{"rate",rate}};}
      parsed["USD"]=new Dictionary<string,object>{{"code","USD"},{"name","United States Dollar"},{"rate",1d}};if(!parsed.ContainsKey(display))throw new InvalidOperationException("Add the selected display currency to the rates list first.");
      using(SqliteDb db=Open()){db.Exec("BEGIN IMMEDIATE");try{db.Exec("DELETE FROM local_currency_rates WHERE code!='USD'");foreach(Dictionary<string,object> rate in parsed.Values)db.Execute("INSERT OR REPLACE INTO local_currency_rates(code,name,rate,updated_at)VALUES(?,?,?,?)",rate["code"],rate["name"],rate["rate"],Now());db.Execute("UPDATE users SET display_currency=?,updated_at=? WHERE id=1",display,Now());db.Exec("COMMIT");}catch{db.Exec("ROLLBACK");throw;}}ProtectDatabaseFile();
    }

    internal void SaveFamily(string name, string location, string story) {
      if (name == "") name = "My Family";
      using (SqliteDb db = Open()) {
        string now = Now();
        db.Execute("UPDATE families SET name = ?, location = ?, story = ?, updated_at = ? WHERE id = 1", name, location, story, now);
        RecordChange(db, "families", "1", "upsert", HashText(name + "|" + location + "|" + story));
      }
      ProtectDatabaseFile();
    }

    internal List<Dictionary<string, string> > GetPeople() {
      using (SqliteDb db = Open()) {
        return db.Query("SELECT id, full_name, relationship, birth_date, place, notes FROM people WHERE deleted_at IS NULL ORDER BY full_name COLLATE NOCASE, id");
      }
    }

    internal void AddPerson(string fullName, string relationship, string birthDate, string place, string notes) {
      if (fullName == "") return;
      using (SqliteDb db = Open()) {
        string now = Now();
        db.Execute("INSERT INTO people (full_name, relationship, birth_date, place, notes, created_at, updated_at) VALUES (?, ?, ?, ?, ?, ?, ?)", fullName, relationship, birthDate, place, notes, now, now);
        string id = Convert.ToString(db.Scalar("SELECT last_insert_rowid()"), CultureInfo.InvariantCulture);
        RecordChange(db, "people", id, "insert", HashText(fullName + "|" + relationship + "|" + birthDate + "|" + place + "|" + notes));
      }
      ProtectDatabaseFile();
    }

    internal void UpdatePerson(int id, string fullName, string relationship, string birthDate, string place, string notes) {
      if (id <= 0 || fullName == "") throw new InvalidOperationException("Choose a valid local person and full name.");
      using (SqliteDb db = Open()) {
        string now = Now();
        int changed = db.Execute(
          "UPDATE people SET full_name = ?, relationship = ?, birth_date = ?, place = ?, notes = ?, updated_at = ? WHERE id = ? AND deleted_at IS NULL",
          fullName, relationship, birthDate, place, notes, now, id);
        if (changed != 1) throw new InvalidOperationException("The selected local person was not found.");
        RecordChange(db, "people", id.ToString(CultureInfo.InvariantCulture), "upsert", HashText(fullName + "|" + relationship + "|" + birthDate + "|" + place + "|" + notes));
      }
      ProtectDatabaseFile();
    }

    internal void DeletePerson(int id) {
      if (id <= 0) return;
      using (SqliteDb db = Open()) {
        string now = Now();
        db.Execute("UPDATE people SET deleted_at = ?, updated_at = ? WHERE id = ?", now, now, id);
        RecordChange(db, "people", id.ToString(CultureInfo.InvariantCulture), "delete", HashText(id.ToString(CultureInfo.InvariantCulture) + "|delete|" + now));
      }
      ProtectDatabaseFile();
    }

    internal int PendingChangeCount() {
      using (SqliteDb db = Open()) {
        return ToInt(db.Scalar("SELECT COUNT(*) FROM sync_changes"));
      }
    }

    internal void SavePluginInstall(PortablePluginInfo plugin, string entrypoint) {
      using (SqliteDb db = Open()) {
        string now = Now();
        string[] known=KnownPluginOperations(plugin.slug),requested=plugin.local!=null&&plugin.local.operations!=null?plugin.local.operations:new string[0];string operations=String.Join(",",requested.Where(operation=>known.Contains(operation)).Distinct());if(plugin.slug=="finance-manager"&&operations=="")operations=String.Join(",",known);
        string shareActions=LocalShareContract.SerializeValidated(plugin.slug,plugin.share_actions);
        db.Execute("INSERT OR REPLACE INTO plugin_installs (slug,name,version,checksum_sha256,entrypoint,status,installed_at,updated_at,bridge_operations,share_actions_json) VALUES (?,?,?,?,?,'enabled',COALESCE((SELECT installed_at FROM plugin_installs WHERE slug=?),?),?,?,?)", plugin.slug, plugin.name, plugin.version, plugin.checksum_sha256, entrypoint, plugin.slug, now, now,operations,shareActions);
      }
      ProtectDatabaseFile();
    }

    internal void RefreshInstalledShareContracts(List<PortablePluginInfo> plugins) {
      if (plugins == null || plugins.Count == 0) return;
      bool changed = false;
      using (SqliteDb db = Open()) {
        foreach (PortablePluginInfo plugin in plugins) {
          if (plugin == null || !PluginCatalogClient.ValidSlug(plugin.slug) || String.IsNullOrWhiteSpace(plugin.checksum_sha256)) continue;
          string contract = LocalShareContract.SerializeValidated(plugin.slug, plugin.share_actions);
          int count = db.Execute("UPDATE plugin_installs SET share_actions_json=?,updated_at=? WHERE slug=? AND version=? AND checksum_sha256=? AND share_actions_json!=?", contract, Now(), plugin.slug, plugin.version, plugin.checksum_sha256, contract);
          changed = changed || count > 0;
        }
      }
      if (changed) ProtectDatabaseFile();
    }

    internal List<Dictionary<string, string> > GetInstalledPlugins() {
      using (SqliteDb db = Open()) return db.Query("SELECT slug,name,version,checksum_sha256,entrypoint,status,installed_at FROM plugin_installs WHERE status IN('enabled','hidden') ORDER BY name COLLATE NOCASE");
    }

    internal void SetPluginStatus(string slug,string status) {
      if (!PluginCatalogClient.ValidSlug(slug)||!new[]{"enabled","hidden"}.Contains(status)) return;
      using (SqliteDb db = Open()) db.Execute("UPDATE plugin_installs SET status=?,updated_at=? WHERE slug=?",status,Now(),slug);
    }
    internal void UninstallPlugin(string slug,bool deleteData){if(!PluginCatalogClient.ValidSlug(slug)||slug=="finance-manager")return;using(SqliteDb db=Open()){db.Exec("BEGIN IMMEDIATE");try{db.Execute("UPDATE plugin_installs SET status='uninstalled',updated_at=? WHERE slug=?",Now(),slug);if(deleteData){db.Execute("DELETE FROM local_plugin_records WHERE slug=?",slug);db.Execute("DELETE FROM local_plugin_settings WHERE slug=?",slug);db.Execute("DELETE FROM local_plugin_attachments WHERE slug=?",slug);}db.Exec("COMMIT");}catch{db.Exec("ROLLBACK");throw;}}ProtectDatabaseFile();}
    internal string PluginName(string slug){using(SqliteDb db=Open()){Dictionary<string,string> row=db.QueryOne("SELECT name FROM plugin_installs WHERE slug=? LIMIT 1",slug);return row==null?CultureInfo.InvariantCulture.TextInfo.ToTitleCase(slug.Replace('-',' ')):row["name"];}}

    internal string PluginEntrypoint(string slug) {
      if (!PluginCatalogClient.ValidSlug(slug)) return "";
      using (SqliteDb db = Open()) {
        Dictionary<string, string> row = db.QueryOne("SELECT entrypoint FROM plugin_installs WHERE slug=? AND status='enabled' LIMIT 1", slug);
        if (row == null) return "";
        string root = Path.GetFullPath(Path.Combine(PortablePaths.PluginsDir, slug));
        string path = Path.GetFullPath(Path.Combine(root, row["entrypoint"]));
        return path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ? path : "";
      }
    }

    internal string IssueSession() {
      return WriteProtectedToken("session.token", RandomToken(32));
    }

    internal bool IsSession(string token) {
      if (token == null || token == "") return false;
      string current = ReadProtectedToken("session.token");
      return current != "" && FixedEquals(token, current);
    }

    internal void ClearSession() {
      try { File.Delete(Path.Combine(PortablePaths.TokensDir, "session.token")); } catch { }
    }

    internal bool CheckCsrf(string token) {
      return token != null && token != "" && FixedEquals(token, CsrfToken);
    }

    private SqliteDb Open() {
      return new SqliteDb(dbPath);
    }

    private void RecordChange(SqliteDb db, string table, string primaryKey, string operation, string rowHash) {
      db.Execute("INSERT INTO sync_changes (table_name, primary_key, operation, changed_at, row_hash, origin_device) VALUES (?, ?, ?, ?, ?, ?)", table, primaryKey, operation, Now(), rowHash, DeviceId);
    }

    private void ProtectDatabaseFile() {
      try {
        if (File.Exists(dbPath)) {
          File.Encrypt(dbPath);
          protectionNote = "Windows user-profile encryption enabled";
        }
      } catch (Exception error) {
        protectionNote = "Windows file encryption unavailable: " + error.Message;
        Program.Log("Database protection warning: " + error.Message);
      }
    }

    private string GetOrCreateProtectedToken(string fileName) {
      string existing = ReadProtectedToken(fileName);
      if (existing != "") return existing;
      return WriteProtectedToken(fileName, RandomToken(32));
    }

    private string ReadProtectedToken(string fileName) {
      string path = Path.Combine(PortablePaths.TokensDir, fileName);
      try {
        if (!File.Exists(path)) return "";
        byte[] encrypted = File.ReadAllBytes(path);
        byte[] raw = ProtectedData.Unprotect(encrypted, TokenEntropy, DataProtectionScope.CurrentUser);
        return Encoding.UTF8.GetString(raw);
      } catch {
        return "";
      }
    }

    private string WriteProtectedToken(string fileName, string value) {
      string path = Path.Combine(PortablePaths.TokensDir, fileName);
      byte[] raw = Encoding.UTF8.GetBytes(value);
      byte[] encrypted = ProtectedData.Protect(raw, TokenEntropy, DataProtectionScope.CurrentUser);
      File.WriteAllBytes(path, encrypted);
      return value;
    }

    private static int ToInt(object value) {
      if (value == null) return 0;
      int parsed;
      return int.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture), out parsed) ? parsed : 0;
    }

    private static string Now() {
      return DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);
    }

    private static string RandomToken(int bytes) {
      byte[] data = new byte[bytes];
      using (RNGCryptoServiceProvider rng = new RNGCryptoServiceProvider()) {
        rng.GetBytes(data);
      }
      return BitConverter.ToString(data).Replace("-", "").ToLowerInvariant();
    }

    private static string ComputeDeviceId() {
      string machineGuid = "";
      try {
        using (RegistryKey key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Cryptography")) {
          if (key != null) machineGuid = Convert.ToString(key.GetValue("MachineGuid"), CultureInfo.InvariantCulture);
        }
      } catch {
      }
      return HashText(Environment.MachineName + "|" + Environment.UserName + "|" + machineGuid).Substring(0, 32);
    }

    private static string HashText(string text) {
      using (SHA256 sha = SHA256.Create()) {
        return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(text ?? ""))).Replace("-", "").ToLowerInvariant();
      }
    }

    private static bool FixedEquals(string a, string b) {
      if (a == null || b == null || a.Length != b.Length) return false;
      int diff = 0;
      for (int i = 0; i < a.Length; i++) diff |= a[i] ^ b[i];
      return diff == 0;
    }
  }

  internal static class PasswordHasher {
    internal static string Hash(string password) {
      byte[] salt = new byte[16];
      using (RNGCryptoServiceProvider rng = new RNGCryptoServiceProvider()) {
        rng.GetBytes(salt);
      }
      const int iterations = 120000;
      using (Rfc2898DeriveBytes pbkdf2 = new Rfc2898DeriveBytes(password, salt, iterations)) {
        byte[] hash = pbkdf2.GetBytes(32);
        return iterations.ToString(CultureInfo.InvariantCulture) + ":" + Convert.ToBase64String(salt) + ":" + Convert.ToBase64String(hash);
      }
    }

    internal static bool Verify(string password, string encoded) {
      try {
        string[] parts = encoded.Split(':');
        if (parts.Length != 3) return false;
        int iterations = int.Parse(parts[0], CultureInfo.InvariantCulture);
        byte[] salt = Convert.FromBase64String(parts[1]);
        byte[] expected = Convert.FromBase64String(parts[2]);
        using (Rfc2898DeriveBytes pbkdf2 = new Rfc2898DeriveBytes(password, salt, iterations)) {
          byte[] actual = pbkdf2.GetBytes(expected.Length);
          if (actual.Length != expected.Length) return false;
          int diff = 0;
          for (int i = 0; i < actual.Length; i++) diff |= actual[i] ^ expected[i];
          return diff == 0;
        }
      } catch {
        return false;
      }
    }
  }

  internal sealed class SqliteDb : IDisposable {
    private const int SQLITE_OK = 0;
    private const int SQLITE_ROW = 100;
    private const int SQLITE_DONE = 101;
    private const int SQLITE_OPEN_READWRITE = 0x00000002;
    private const int SQLITE_OPEN_CREATE = 0x00000004;
    private const int SQLITE_OPEN_FULLMUTEX = 0x00010000;
    private static readonly IntPtr SQLITE_TRANSIENT = new IntPtr(-1);
    private IntPtr db;

    internal SqliteDb(string path) {
      Directory.CreateDirectory(Path.GetDirectoryName(path));
      int rc = sqlite3_open_v2(ToUtf8(path), out db, SQLITE_OPEN_READWRITE | SQLITE_OPEN_CREATE | SQLITE_OPEN_FULLMUTEX, IntPtr.Zero);
      if (rc != SQLITE_OK) throw new InvalidOperationException("Could not open local SQLite database.");
      Exec("PRAGMA busy_timeout=5000");
      Exec("PRAGMA synchronous=NORMAL");
      Exec("PRAGMA foreign_keys=ON");
      Exec("PRAGMA temp_store=MEMORY");
      Exec("PRAGMA cache_size=-16384");
    }

    internal void Exec(string sql) {
      IntPtr error;
      int rc = sqlite3_exec(db, ToUtf8(sql), IntPtr.Zero, IntPtr.Zero, out error);
      if (rc != SQLITE_OK) {
        string message = error == IntPtr.Zero ? "SQLite error." : Marshal.PtrToStringAnsi(error);
        if (error != IntPtr.Zero) sqlite3_free(error);
        throw new InvalidOperationException(message);
      }
    }

    internal int Execute(string sql, params object[] args) {
      IntPtr stmt = Prepare(sql);
      try {
        Bind(stmt, args);
        int rc = sqlite3_step(stmt);
        if (rc != SQLITE_DONE) throw new InvalidOperationException("SQLite statement failed: " + ErrorMessage());
        return sqlite3_changes(db);
      } finally {
        sqlite3_finalize(stmt);
      }
    }

    internal object Scalar(string sql, params object[] args) {
      IntPtr stmt = Prepare(sql);
      try {
        Bind(stmt, args);
        int rc = sqlite3_step(stmt);
        if (rc == SQLITE_ROW) return ColumnText(stmt, 0);
        if (rc == SQLITE_DONE) return null;
        throw new InvalidOperationException("SQLite query failed: " + ErrorMessage());
      } finally {
        sqlite3_finalize(stmt);
      }
    }

    internal Dictionary<string, string> QueryOne(string sql, params object[] args) {
      List<Dictionary<string, string> > rows = Query(sql, args);
      return rows.Count == 0 ? null : rows[0];
    }

    internal List<Dictionary<string, string> > Query(string sql, params object[] args) {
      List<Dictionary<string, string> > rows = new List<Dictionary<string, string> >();
      IntPtr stmt = Prepare(sql);
      try {
        Bind(stmt, args);
        int columnCount = sqlite3_column_count(stmt);
        while (true) {
          int rc = sqlite3_step(stmt);
          if (rc == SQLITE_DONE) break;
          if (rc != SQLITE_ROW) throw new InvalidOperationException("SQLite query failed: " + ErrorMessage());
          Dictionary<string, string> row = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
          for (int i = 0; i < columnCount; i++) {
            row[ColumnName(stmt, i)] = ColumnText(stmt, i);
          }
          rows.Add(row);
        }
        return rows;
      } finally {
        sqlite3_finalize(stmt);
      }
    }

    private IntPtr Prepare(string sql) {
      IntPtr stmt;
      int rc = sqlite3_prepare_v2(db, ToUtf8(sql), -1, out stmt, IntPtr.Zero);
      if (rc != SQLITE_OK) throw new InvalidOperationException("SQLite prepare failed: " + ErrorMessage());
      return stmt;
    }

    private void Bind(IntPtr stmt, object[] args) {
      for (int i = 0; i < args.Length; i++) {
        object value = args[i];
        int index = i + 1;
        int rc;
        if (value == null) {
          rc = sqlite3_bind_null(stmt, index);
        } else if (value is int) {
          rc = sqlite3_bind_int64(stmt, index, Convert.ToInt64(value, CultureInfo.InvariantCulture));
        } else if (value is long) {
          rc = sqlite3_bind_int64(stmt, index, Convert.ToInt64(value, CultureInfo.InvariantCulture));
        } else {
          byte[] bytes = Encoding.UTF8.GetBytes(Convert.ToString(value, CultureInfo.InvariantCulture));
          rc = sqlite3_bind_text(stmt, index, bytes, bytes.Length, SQLITE_TRANSIENT);
        }
        if (rc != SQLITE_OK) throw new InvalidOperationException("SQLite bind failed: " + ErrorMessage());
      }
    }

    private string ErrorMessage() {
      IntPtr ptr = sqlite3_errmsg(db);
      return ptr == IntPtr.Zero ? "unknown error" : Marshal.PtrToStringAnsi(ptr);
    }

    private static string ColumnName(IntPtr stmt, int column) {
      IntPtr ptr = sqlite3_column_name(stmt, column);
      return ptr == IntPtr.Zero ? "column" + column.ToString(CultureInfo.InvariantCulture) : Marshal.PtrToStringAnsi(ptr);
    }

    private static string ColumnText(IntPtr stmt, int column) {
      IntPtr ptr = sqlite3_column_text(stmt, column);
      if (ptr == IntPtr.Zero) return "";
      int len = sqlite3_column_bytes(stmt, column);
      byte[] bytes = new byte[len];
      Marshal.Copy(ptr, bytes, 0, len);
      return Encoding.UTF8.GetString(bytes);
    }

    private static byte[] ToUtf8(string text) {
      return Encoding.UTF8.GetBytes(text + "\0");
    }

    public void Dispose() {
      if (db != IntPtr.Zero) {
        sqlite3_close(db);
        db = IntPtr.Zero;
      }
    }

    [DllImport("e_sqlite3", CallingConvention = CallingConvention.Cdecl)]
    private static extern int sqlite3_open_v2(byte[] filename, out IntPtr db, int flags, IntPtr vfs);
    [DllImport("e_sqlite3", CallingConvention = CallingConvention.Cdecl)]
    private static extern int sqlite3_close(IntPtr db);
    [DllImport("e_sqlite3", CallingConvention = CallingConvention.Cdecl)]
    private static extern int sqlite3_exec(IntPtr db, byte[] sql, IntPtr callback, IntPtr firstArg, out IntPtr errmsg);
    [DllImport("e_sqlite3", CallingConvention = CallingConvention.Cdecl)]
    private static extern void sqlite3_free(IntPtr ptr);
    [DllImport("e_sqlite3", CallingConvention = CallingConvention.Cdecl)]
    private static extern int sqlite3_prepare_v2(IntPtr db, byte[] sql, int bytes, out IntPtr stmt, IntPtr tail);
    [DllImport("e_sqlite3", CallingConvention = CallingConvention.Cdecl)]
    private static extern int sqlite3_step(IntPtr stmt);
    [DllImport("e_sqlite3", CallingConvention = CallingConvention.Cdecl)]
    private static extern int sqlite3_finalize(IntPtr stmt);
    [DllImport("e_sqlite3", CallingConvention = CallingConvention.Cdecl)]
    private static extern int sqlite3_bind_null(IntPtr stmt, int index);
    [DllImport("e_sqlite3", CallingConvention = CallingConvention.Cdecl)]
    private static extern int sqlite3_bind_int64(IntPtr stmt, int index, long value);
    [DllImport("e_sqlite3", CallingConvention = CallingConvention.Cdecl)]
    private static extern int sqlite3_bind_text(IntPtr stmt, int index, byte[] value, int bytes, IntPtr destructor);
    [DllImport("e_sqlite3", CallingConvention = CallingConvention.Cdecl)]
    private static extern int sqlite3_column_count(IntPtr stmt);
    [DllImport("e_sqlite3", CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr sqlite3_column_name(IntPtr stmt, int column);
    [DllImport("e_sqlite3", CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr sqlite3_column_text(IntPtr stmt, int column);
    [DllImport("e_sqlite3", CallingConvention = CallingConvention.Cdecl)]
    private static extern int sqlite3_column_bytes(IntPtr stmt, int column);
    [DllImport("e_sqlite3", CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr sqlite3_errmsg(IntPtr db);
    [DllImport("e_sqlite3", CallingConvention = CallingConvention.Cdecl)]
    private static extern int sqlite3_changes(IntPtr db);
  }
}
