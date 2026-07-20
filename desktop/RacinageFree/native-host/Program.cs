using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
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
    private static void Main() {
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
    internal const string Version = "0.13.3";
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

    internal static void EnsureMutableFolders() {
      foreach (string path in new[] { Root, DataDir, MediaDir, LogsDir, UpdatesDir, TokensDir, WebViewDir, PluginsDir, PluginCacheDir }) {
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

    private readonly LocalServer server;
    private readonly LocalStore store;
    private readonly WebView2 browser = new WebView2();
    private readonly StatusDotControl statusDot = new StatusDotControl();
    private readonly System.Windows.Forms.Timer statusTimer = new System.Windows.Forms.Timer();
    private Label statusText;
    private string lastError = "";

    internal RacinageWindow(LocalServer server, LocalStore store) {
      this.server = server;
      this.store = store;
      Text = PortablePaths.AppName;
      StartPosition = FormStartPosition.CenterScreen;
      MinimumSize = new Size(980, 620);
      Size = new Size(1180, 760);
      BackColor = Color.White;
      BuildChrome();
      browser.Dock = DockStyle.Fill;
      Controls.Add(browser);
      browser.BringToFront();
      FormClosing += delegate { statusTimer.Stop(); };
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
      titleBar.MouseDown += delegate {
        ReleaseCapture();
        SendMessage(Handle, 0xA1, 0x2, 0);
      };

      Label title = new Label {
        Text = "Racinage Free",
        Dock = DockStyle.Left,
        Width = 180,
        TextAlign = ContentAlignment.MiddleLeft,
        ForeColor = Color.White,
        Font = new Font("Segoe UI", 10.5f, FontStyle.Bold)
      };
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
      maximize.Click += delegate { WindowState = WindowState == FormWindowState.Maximized ? FormWindowState.Normal : FormWindowState.Maximized; };
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
        browser.CoreWebView2.Navigate(server.BaseUrl + "/");
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

  internal sealed class LocalServer {
    private readonly LocalStore store;
    private readonly PluginCatalogClient pluginCatalog = new PluginCatalogClient();
    private HttpListener listener;
    private Thread thread;
    private volatile bool running;

    internal string BaseUrl { get; private set; }
    internal bool IsRunning { get { return running; } }

    internal LocalServer(LocalStore store) {
      this.store = store;
    }

    internal void Start() {
      int port = ReservePort();
      BaseUrl = "http://127.0.0.1:" + port.ToString(CultureInfo.InvariantCulture);
      listener = new HttpListener();
      listener.Prefixes.Add(BaseUrl + "/");
      listener.Start();
      running = true;
      thread = new Thread(ListenLoop);
      thread.IsBackground = true;
      thread.Start();
    }

    internal void Stop() {
      running = false;
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
        if (path == "/health") { WriteJson(context, "{\"ok\":true,\"product\":\"Racinage Free\",\"version\":\"" + PortablePaths.Version + "\"}"); return; }
        if (path == "/upgrade") { Redirect(context, PortablePaths.PricingUrl); return; }
        if (path == "/login") { Login(context); return; }
        if (path == "/start-free") { StartFree(context); return; }
        if (path == "/family") { Family(context); return; }
        if (path == "/manage" || path == "/manage/plugins" || path == "/manage/family" || path == "/manage/settings") { Manage(context, path); return; }
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
          Redirect(context, "/family");
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
        Redirect(context, "/family");
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
        UpgradeModal();
      return Page("Family", body);
    }

    private void Manage(HttpListenerContext context, string path) {
      if (!IsAuthenticated(context)) { Redirect(context, "/login"); return; }
      string message = "";
      if (context.Request.HttpMethod == "POST") {
        Dictionary<string, string> form = ReadForm(context);
        if (!CheckCsrf(form)) { WriteHtml(context, ManagePage(path, "Your session expired. Please try again."), 400); return; }
        string action = form.ContainsKey("action") ? form["action"] : "";
        string slug = form.ContainsKey("slug") ? form["slug"] : "";
        if (action == "install_plugin") message = pluginCatalog.Install(slug, store);
        else if (action == "uninstall_plugin") { store.UninstallPlugin(slug); message = "Plugin uninstalled. Its local data was kept."; }
      }
      WriteHtml(context, ManagePage(path, message));
    }

    private string ManagePage(string path, string message) {
      string active = path.EndsWith("/plugins", StringComparison.OrdinalIgnoreCase) ? "plugins" : (path.EndsWith("/settings", StringComparison.OrdinalIgnoreCase) ? "settings" : (path.EndsWith("/family", StringComparison.OrdinalIgnoreCase) ? "family" : "account"));
      string tabs = "<nav class='manage-tabs' aria-label='Manage sections'>" + ManageTab("/manage", "Account", active == "account") + ManageTab("/manage/family", "Family", active == "family") + ManageTab("/manage/plugins", "Plugins", active == "plugins") + ManageTab("/manage/settings", "Settings", active == "settings") + "</nav>";
      string content;
      if (active == "plugins") content = PluginsPanel();
      else if (active == "family") {
        Dictionary<string, string> family = store.GetFamily();
        content = "<section class='manage-card'><div class='manage-card-head'><div><h2>Family account</h2><p>The local Free edition has one owner-managed family and no collaboration controls.</p></div><a class='button' href='/family'>Open family records</a></div><dl class='facts'><div><dt>Name</dt><dd>" + H(family["name"]) + "</dd></div><div><dt>Location</dt><dd>" + H(family["location"] == "" ? "Not set" : family["location"]) + "</dd></div></dl></section>";
      } else if (active == "settings") {
        content = "<section class='manage-card'><h2>Local settings</h2><p>Database, media, installed plugins, and device tokens stay under your Windows user profile.</p><dl class='facts'><div><dt>Edition</dt><dd>Lite Free Portable</dd></div><div><dt>Version</dt><dd>" + H(PortablePaths.Version) + "</dd></div><div><dt>Plugin updates</dt><dd>Checked only when you open the Plugins tab</dd></div></dl></section>";
      } else {
        content = "<section class='manage-grid'><article class='manage-card'><h2>Local account</h2><p>One local user owns this device's family records. Collaborative members and invitations are intentionally unavailable.</p><a class='button ghost' href='/family'>Open dashboard</a></article><article class='manage-card'><h2>Plan</h2><p>Lite Free limits apply to local features. Reviewed plugins can add Free features, while optional Pro features are purchased through the publisher's hosted Racinage page.</p><a class='button' href='" + PortablePaths.PricingUrl + "'>View Racinage plans</a></article></section>";
      }
      string body = "<section class='manage-head'><div><p class='kicker'>Manage</p><h1>Account and features</h1><p>Manage the local account using the same clear sections as the hosted app, without collaborative controls.</p></div></section>" + tabs + ErrorHtml(message) + "<div class='manage-content'>" + content + "</div>";
      return Page("Manage", body);
    }

    private string PluginsPanel() {
      List<Dictionary<string, string> > installed = store.GetInstalledPlugins();
      Dictionary<string, Dictionary<string, string> > installedBySlug = new Dictionary<string, Dictionary<string, string> >(StringComparer.OrdinalIgnoreCase);
      foreach (Dictionary<string, string> row in installed) installedBySlug[row["slug"]] = row;
      StringBuilder cards = new StringBuilder();
      try {
        List<PortablePluginInfo> plugins = pluginCatalog.GetPlugins();
        foreach (PortablePluginInfo plugin in plugins) {
          Dictionary<string, string> current;
          bool isInstalled = installedBySlug.TryGetValue(plugin.slug ?? "", out current);
          int listPriceCents = Math.Max(0, plugin.price_cents);
          int effectivePriceCents = plugin.effective_price_cents.HasValue ? Math.Min(listPriceCents, Math.Max(0, plugin.effective_price_cents.Value)) : listPriceCents;
          string currency = String.IsNullOrWhiteSpace(plugin.currency) ? "USD" : plugin.currency.ToUpperInvariant();
          string displayName = String.IsNullOrWhiteSpace(plugin.name) ? "Plugin" : plugin.name;
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
          cards.Append("<article class='plugin-card'><div class='plugin-card-top'><span class='plugin-mark'>" + H(displayName.Substring(0, 1).ToUpperInvariant()) + "</span><div><h3>" + H(displayName) + "</h3><p class='plugin-meta'>" + priceMeta + "</p></div></div><p>" + H(plugin.summary) + "</p>");
          if (plugin.local == null || !plugin.local.supported) cards.Append("<p class='notice'>Web only: " + H(plugin.local == null ? "No reviewed local runtime is available." : plugin.local.reason) + "</p>");
          else if (isInstalled) cards.Append("<div class='actions'><a class='button' href='/plugin/" + A(plugin.slug) + "'>Open</a><form method='post' action='/manage/plugins'>" + CsrfInput() + "<input type='hidden' name='action' value='uninstall_plugin'><input type='hidden' name='slug' value='" + A(plugin.slug) + "'><button class='button ghost' type='submit'>Uninstall</button></form></div>");
          else if ((plugin.download_url ?? "") != "") cards.Append("<form method='post' action='/manage/plugins'>" + CsrfInput() + "<input type='hidden' name='action' value='install_plugin'><input type='hidden' name='slug' value='" + A(plugin.slug) + "'><button class='button' type='submit'>Install</button></form>");
          if (plugin.pricing_type != "free" && plugin.price_cents > 0) cards.Append("<p><a href='" + A(plugin.purchase_url) + "'>Buy or manage Pro access on Racinage</a></p>");
          cards.Append("</article>");
        }
      } catch (Exception error) {
        Program.Log("Plugin catalog error: " + error);
        cards.Append("<p class='notice'>The online plugin library is unavailable right now. Installed plugins remain available.</p>");
      }
      return "<section class='manage-card'><div class='manage-card-head'><div><h2>Plugin library</h2><p>Only reviewed, checksum-verified, local-compatible bundles can be installed. Collaboration plugins and controls are excluded.</p></div><span class='status-pill'>Lite rules apply</span></div><div class='plugin-grid'>" + cards.ToString() + "</div></section>";
    }

    private void PortablePlugin(HttpListenerContext context, string slug) {
      if (!IsAuthenticated(context)) { Redirect(context, "/login"); return; }
      string entrypoint = store.PluginEntrypoint(slug);
      if (entrypoint == "" || !File.Exists(entrypoint)) { WriteHtml(context, Page("Plugin unavailable", "<section class='panel'><h1>Plugin unavailable</h1><p>This local plugin is missing or disabled.</p><a class='button' href='/manage/plugins'>Back to plugins</a></section>"), 404); return; }
      string source = File.ReadAllText(entrypoint, Encoding.UTF8);
      string body = "<section class='manage-head'><div><p class='kicker'>Local plugin</p><h1>" + H(slug) + "</h1><p>Runs in an isolated frame without access to family records unless a future reviewed host capability explicitly grants it.</p></div><a class='button ghost' href='/manage/plugins'>Back to plugins</a></section><iframe class='plugin-frame' sandbox='allow-scripts allow-forms' referrerpolicy='no-referrer' srcdoc='" + A(source) + "'></iframe>";
      WriteHtml(context, Page(slug, body));
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

    private static string Page(string title, string body) {
      return "<!doctype html><html><head><meta charset='utf-8'><meta name='viewport' content='width=device-width, initial-scale=1'>" +
        "<title>" + H(title) + " - Racinage Free</title><style>" + Css() + "</style></head><body>" +
        "<header><a class='brand' href='/'>Racinage Free</a><nav><a href='/'>Home</a><a href='/family'>Dashboard</a><a href='/manage'>Manage</a><a href='" + PortablePaths.PricingUrl + "'>Upgrade</a></nav></header>" +
        "<main>" + body + "</main><script>" + Js() + "</script></body></html>";
    }

    private static string ErrorHtml(string error) {
      return error == "" ? "" : "<p class='error'>" + H(error) + "</p>";
    }

    private static string UpgradeModal() {
      return "<div id='upgradeModal' class='modal' hidden><div class='modalbox'><h2>Upgrade required</h2><p>You cannot share <span id='upgradeFeature'>this record</span> while using the local Lite Free plan.</p><div class='actions'><a class='button' href='" + PortablePaths.PricingUrl + "'>Upgrade</a><button type='button' class='button ghost' onclick='hideUpgrade()'>Close</button></div></div></div>";
    }

    private static string Css() {
      return @"
@font-face{font-family:Inter;src:url('/fonts/inter/InterVariable.woff2') format('woff2');font-weight:100 900;font-style:normal;font-display:swap}
@font-face{font-family:Inter;src:url('/fonts/inter/InterVariable-Italic.woff2') format('woff2');font-weight:100 900;font-style:italic;font-display:swap}
:root{--brand:#004650;--accent:#c35900;--pale:#f5fafd;--line:#dbe5ea;--text:#3d4b4c;--muted:#6d7c7d}
*{box-sizing:border-box}body{margin:0;font-family:Inter,Segoe UI,Tahoma,sans-serif;background:#f8fbfc;color:var(--text)}a{color:#007584;text-decoration:none}header{height:58px;display:flex;align-items:center;justify-content:space-between;padding:0 28px;border-bottom:1px solid var(--line);background:#fff;position:sticky;top:0;z-index:5}.brand{font-weight:800;color:var(--brand)}nav{display:flex;gap:16px;align-items:center}nav a{font-size:14px;font-weight:600;color:var(--muted)}main{max-width:1120px;margin:0 auto;padding:34px 24px 70px}.hero{min-height:360px;display:grid;align-items:center;border-bottom:1px solid var(--line)}.hero h1,.dashhead h1,.manage-head h1{font-size:48px;line-height:1.02;margin:6px 0 16px;color:var(--brand)}.hero p,.dashhead p,.manage-head p,.note{font-size:17px;line-height:1.6;max-width:760px;color:var(--muted)}.kicker{font-size:12px!important;text-transform:uppercase;letter-spacing:.14em;color:var(--accent)!important;font-weight:800;margin:0}.actions{display:flex;align-items:center;gap:10px;flex-wrap:wrap;margin-top:20px}.actions form{display:block}.button{display:inline-flex;align-items:center;justify-content:center;min-height:42px;padding:0 18px;border-radius:8px;border:1px solid var(--brand);background:var(--brand);color:#fff;font:700 14px/1 inherit;cursor:pointer}.button.ghost{background:transparent;color:var(--brand);border-color:#9ab2b8}.grid,.manage-grid{display:grid;grid-template-columns:repeat(auto-fit,minmax(240px,1fr));gap:16px;margin-top:28px}.grid article,.panel,.manage-card{background:#fff;border:1px solid var(--line);border-radius:12px;padding:24px}.grid h2,.panel h2,.manage-card h2{margin:0 0 10px;color:var(--brand)}.grid p,.panel p,.manage-card p{line-height:1.55;color:var(--muted)}.narrow{max-width:460px;margin:30px auto}.wide{margin-top:18px}.layout{display:grid;grid-template-columns:1fr 1fr;gap:18px}.dashhead{display:flex;align-items:flex-end;justify-content:space-between;gap:20px;margin-bottom:22px}form{display:grid;gap:12px}label{display:grid;gap:6px;font-size:13px;font-weight:700;color:var(--brand)}input,textarea{width:100%;border:1px solid #cad8dd;border-radius:8px;padding:10px 12px;font:inherit;background:#fbfdfd;color:var(--text)}textarea{resize:vertical}.error{border:1px solid #efb5b5;background:#fff2f2;color:#9b2525;border-radius:8px;padding:10px 12px}.sharebar{display:flex;flex-wrap:wrap;gap:8px;margin:12px 0 18px}.share{border:1px solid #cddbe0;background:#f4faff;border-radius:8px;padding:8px 11px;cursor:pointer;font-weight:700;color:var(--brand)}.people{display:grid;grid-template-columns:repeat(auto-fit,minmax(240px,1fr));gap:12px}.people article{border:1px solid var(--line);border-radius:10px;padding:14px;background:#fbfdfd}.people strong{display:block;color:var(--brand)}.people span{display:block;color:var(--accent);font-size:12px;font-weight:800;text-transform:uppercase;margin-top:3px}.people p{margin:8px 0 0;font-size:14px}.textbtn{border:0;background:transparent;color:#b93333;padding:8px 0 0;cursor:pointer;font-weight:700}.empty{margin:0}.modal{position:fixed;inset:0;background:rgba(5,21,25,.55);display:grid;place-items:center;padding:24px;z-index:20}.modal[hidden]{display:none}.modalbox{width:min(440px,100%);background:#fff;border-radius:12px;padding:24px;border:1px solid var(--line)}.modalbox h2{margin:0 0 10px;color:var(--brand)}.panelhead,.manage-card-head{display:flex;justify-content:space-between;gap:16px;align-items:center}.panelhead h2,.panelhead p,.manage-card-head h2,.manage-card-head p{margin:0}.manage-head{margin-bottom:22px}.manage-tabs{display:flex;gap:6px;overflow:auto;padding:5px;border:1px solid var(--line);border-radius:10px;background:#fff}.manage-tabs a{min-height:42px;display:inline-flex;align-items:center;padding:0 16px;border-radius:7px;font-weight:700;color:var(--muted)}.manage-tabs a.active{color:#fff;background:var(--brand)}.manage-content{margin-top:18px}.manage-grid{margin-top:0}.facts{display:grid;grid-template-columns:repeat(auto-fit,minmax(180px,1fr));gap:10px;margin:18px 0 0}.facts div{padding:12px;border:1px solid var(--line);border-radius:9px}.facts dt{font-size:12px;color:var(--muted)}.facts dd{margin:5px 0 0;color:var(--brand);font-weight:700}.plugin-grid{display:grid;grid-template-columns:repeat(auto-fit,minmax(250px,1fr));gap:12px;margin-top:18px}.plugin-card{display:flex;flex-direction:column;min-height:260px;padding:16px;border:1px solid var(--line);border-radius:10px;background:#fbfdfd}.plugin-card-top{display:flex;gap:11px;align-items:center}.plugin-card h3{margin:0;color:var(--brand)}.plugin-card>p{flex:1}.plugin-mark{width:44px;height:44px;display:grid;place-items:center;border-radius:9px;color:#fff;background:var(--brand);font-size:20px;font-weight:800}.plugin-meta{margin:4px 0 0!important;font-size:12px}.notice{padding:10px;border-left:3px solid var(--accent);background:#fff8ef;font-size:13px}.status-pill{padding:7px 10px;border-radius:999px;color:var(--brand);background:#e9f3ef;font-size:12px;font-weight:800}.plugin-frame{width:100%;min-height:620px;border:1px solid var(--line);border-radius:12px;background:#fff}@media(max-width:760px){header{padding:0 16px}nav{gap:10px}.hero h1,.dashhead h1,.manage-head h1{font-size:36px}.layout{grid-template-columns:1fr}.dashhead,.manage-card-head{display:block}.manage-card-head .button,.manage-card-head .status-pill{margin-top:12px}}";
    }

    private static string Js() {
      return "function showUpgrade(feature){var m=document.getElementById('upgradeModal');document.getElementById('upgradeFeature').textContent=feature;m.hidden=false;}function hideUpgrade(){document.getElementById('upgradeModal').hidden=true;}document.addEventListener('keydown',function(e){if(e.key==='Escape')hideUpgrade();});document.addEventListener('click',function(e){var p=e.target.closest&&e.target.closest('input[type=date],input[type=datetime-local],input[type=time],input[type=month],input[type=year]');if(!p||p.disabled||p.readOnly||typeof p.showPicker!=='function')return;try{p.showPicker();}catch(_){}});";
    }

    private static void WriteHtml(HttpListenerContext context, string html) {
      WriteHtml(context, html, 200);
    }

    private static void WriteHtml(HttpListenerContext context, string html, int status) {
      byte[] bytes = Encoding.UTF8.GetBytes(html);
      context.Response.StatusCode = status;
      context.Response.ContentType = "text/html; charset=utf-8";
      context.Response.ContentLength64 = bytes.Length;
      context.Response.OutputStream.Write(bytes, 0, bytes.Length);
      context.Response.Close();
    }

    private static void WriteFile(HttpListenerContext context, string relativePath, string contentType) {
      string root = AppDomain.CurrentDomain.BaseDirectory;
      string path = Path.GetFullPath(Path.Combine(root, relativePath));
      if (!path.StartsWith(root, StringComparison.OrdinalIgnoreCase) || !File.Exists(path)) { context.Response.StatusCode = 404; context.Response.Close(); return; }
      byte[] bytes = File.ReadAllBytes(path);
      context.Response.ContentType = contentType;
      context.Response.ContentLength64 = bytes.Length;
      context.Response.OutputStream.Write(bytes, 0, bytes.Length);
      context.Response.Close();
    }

    private static void WriteJson(HttpListenerContext context, string json) {
      byte[] bytes = Encoding.UTF8.GetBytes(json);
      context.Response.ContentType = "application/json; charset=utf-8";
      context.Response.ContentLength64 = bytes.Length;
      context.Response.OutputStream.Write(bytes, 0, bytes.Length);
      context.Response.Close();
    }

    private static void Redirect(HttpListenerContext context, string target) {
      context.Response.StatusCode = 302;
      context.Response.RedirectLocation = target;
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
  }

  internal sealed class PortableCatalogEnvelope { public string payload_base64; public string signature; public string algorithm; public string key_id; }
  internal sealed class PortableCatalogPayload { public string expires_at; public List<PortablePluginInfo> plugins; }
  internal sealed class PortableLocalSupport { public bool supported; public string reason; public string root; public string entrypoint; }
  internal sealed class PortablePluginInfo {
    public string slug; public string name; public string summary; public string description; public string pricing_type; public int price_cents; public int? effective_price_cents; public string billing_interval; public string promotion_label; public string promotion_expires_at;
    public string currency; public string version; public string checksum_sha256; public string download_url; public string purchase_url; public PortableLocalSupport local;
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

  internal sealed class LocalStore {
    private static readonly byte[] TokenEntropy = Encoding.UTF8.GetBytes("Racinage Free local token v1");
    private readonly string dbPath = Path.Combine(PortablePaths.DataDir, "racinage-free.sqlite");
    private string deviceId;
    private string csrfToken;
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
      }
      ProtectDatabaseFile();
      GetOrCreateProtectedToken("device.token");
    }

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
        db.Execute("INSERT OR REPLACE INTO plugin_installs (slug,name,version,checksum_sha256,entrypoint,status,installed_at,updated_at) VALUES (?,?,?,?,?,'enabled',COALESCE((SELECT installed_at FROM plugin_installs WHERE slug=?),?),?)", plugin.slug, plugin.name, plugin.version, plugin.checksum_sha256, entrypoint, plugin.slug, now, now);
      }
      ProtectDatabaseFile();
    }

    internal List<Dictionary<string, string> > GetInstalledPlugins() {
      using (SqliteDb db = Open()) return db.Query("SELECT slug,name,version,checksum_sha256,entrypoint,status,installed_at FROM plugin_installs WHERE status='enabled' ORDER BY name COLLATE NOCASE");
    }

    internal void UninstallPlugin(string slug) {
      if (!PluginCatalogClient.ValidSlug(slug)) return;
      using (SqliteDb db = Open()) db.Execute("UPDATE plugin_installs SET status='uninstalled',updated_at=? WHERE slug=?", Now(), slug);
    }

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
