// PDF Viewer.exe - a borderless window around app\index.html, drawn by the WebView2
// runtime that Windows 11 already has. The page does all the viewing; this file only
// provides the window: moving, resizing, full screen, always on top, opening a file.
//
// Built by build\build.ps1 with the .NET Framework compiler that ships with Windows
// (C# 5: no string interpolation, no ?. operator).

using System;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

[assembly: AssemblyTitle("PDF Viewer")]
[assembly: AssemblyProduct("PDF Viewer")]
[assembly: AssemblyDescription("Borderless, minimal PDF viewer")]
// The version attributes come from package.json, in a file build.ps1 generates.

static class Program {
  public static readonly string AppDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "app");
  public static readonly string DataDir = Path.Combine(
    Environment.GetEnvironmentVariable("LOCALAPPDATA") ?? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
    "PDF Viewer");

  [STAThread]
  static void Main(string[] args) {
    // The WebView2 libraries sit in app\ beside the page, not next to the exe.
    // UnsafeLoadFrom: plain LoadFrom refuses files that still carry the
    // "downloaded from the internet" mark, as everything unzipped from a mail or a share does.
    AppDomain.CurrentDomain.AssemblyResolve += delegate(object sender, ResolveEventArgs e) {
      string file = Path.Combine(AppDir, new AssemblyName(e.Name).Name + ".dll");
      return File.Exists(file) ? Assembly.UnsafeLoadFrom(file) : null;
    };
    Application.EnableVisualStyles();
    Application.SetCompatibleTextRenderingDefault(false);
    Run(args);
  }

  // Kept apart from Main so the WebView2 types load only once AssemblyResolve is in place.
  [MethodImpl(MethodImplOptions.NoInlining)]
  static void Run(string[] args) {
    // The browser engine takes the longest to start, so it starts first, while the window is made.
    Task<CoreWebView2Environment> engine = StartEngine();
    string file = null;
    foreach (string arg in args) {
      if (arg.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase) && File.Exists(arg)) {
        file = Path.GetFullPath(arg);
        break;
      }
    }
    Application.Run(new ViewerForm(file, engine));
  }

  static async Task<CoreWebView2Environment> StartEngine() {
    CoreWebView2Environment.SetLoaderDllFolderPath(AppDir);
    CoreWebView2EnvironmentOptions options = new CoreWebView2EnvironmentOptions();
    options.EnableTrackingPrevention = false; // only the app's own page is ever shown: nothing to track
    return await CoreWebView2Environment.CreateAsync(null, Path.Combine(DataDir, "WebView2"), options);
  }
}

// Window position and "always on top", remembered between runs.
static class Settings {
  static string FilePath { get { return Path.Combine(Program.DataDir, "window.txt"); } }

  public static void Load(out Rectangle bounds, out bool onTop) {
    bounds = Rectangle.Empty;
    onTop = false;
    try {
      string[] v = File.ReadAllText(FilePath).Trim().Split(',');
      bounds = new Rectangle(int.Parse(v[0]), int.Parse(v[1]), int.Parse(v[2]), int.Parse(v[3]));
      onTop = v[4] == "1";
    } catch { }
  }

  public static void Save(Rectangle b, bool onTop) {
    try {
      Directory.CreateDirectory(Program.DataDir);
      File.WriteAllText(FilePath, string.Join(",", b.X, b.Y, b.Width, b.Height, onTop ? 1 : 0));
    } catch { }
  }
}

class ViewerForm : Form {
  readonly WebView2 web = new WebView2();
  readonly Task<CoreWebView2Environment> engine;
  readonly string startFile;
  bool onTop;                 // always on top (not Form.TopMost: setting that activates the window)
  bool fullScreen;
  bool wasMaximized;
  Rectangle restoreBounds;    // where to go back to after full screen

  public ViewerForm(string file, Task<CoreWebView2Environment> engine) {
    startFile = file;
    this.engine = engine;
    Text = "PDF Viewer";
    FormBorderStyle = FormBorderStyle.None;
    StartPosition = FormStartPosition.Manual;
    BackColor = Color.FromArgb(0x1e, 0x1f, 0x22);
    MinimumSize = new Size(200, 150);
    try { Icon = new Icon(Path.Combine(Program.AppDir, "icon.ico")); } catch { }

    Rectangle saved;
    Settings.Load(out saved, out onTop);
    Bounds = InitialBounds(saved);

    web.DefaultBackgroundColor = BackColor;
    web.Dock = DockStyle.Fill;
    Controls.Add(web);

    Load += async delegate { await StartWebView(); };
    Activated += delegate { web.Focus(); };
    FormClosing += delegate {
      Rectangle b = fullScreen ? restoreBounds : WindowState == FormWindowState.Normal ? Bounds : RestoreBounds;
      Settings.Save(b, onTop);
    };
  }

  protected override void OnHandleCreated(EventArgs e) {
    base.OnHandleCreated(e);
    // Windows 11 leaves a window without a title bar square and shadowless unless asked.
    SetCorners(true);
    MARGINS m = new MARGINS { Left = 1, Right = 1, Top = 1, Bottom = 1 };
    DwmExtendFrameIntoClientArea(Handle, ref m);
  }

  void SetCorners(bool round) {
    int preference = round ? DWMWCP_ROUND : DWMWCP_DONOTROUND;
    DwmSetWindowAttribute(Handle, DWMWA_WINDOW_CORNER_PREFERENCE, ref preference, sizeof(int));
  }

  // ---- Where the window opens ----

  static Rectangle InitialBounds(Rectangle saved) {
    Rectangle other;
    if (OtherViewerBounds(out other)) {
      other.Offset(30, 30); // another viewer is open: cascade from it
      return other;
    }
    if (saved.Width > 0 && IsOnScreen(saved)) return saved;
    Rectangle area = Screen.PrimaryScreen.WorkingArea;
    int h = area.Height * 85 / 100, w = h * 72 / 100;
    return new Rectangle(area.X + (area.Width - w) / 2, area.Y + (area.Height - h) / 2, w, h);
  }

  // At least a 100px square of the window must land on some screen.
  static bool IsOnScreen(Rectangle b) {
    foreach (Screen s in Screen.AllScreens) {
      Rectangle i = Rectangle.Intersect(b, s.WorkingArea);
      if (i.Width >= 100 && i.Height >= 100) return true;
    }
    return false;
  }

  // A visible, windowed viewer started from this same exe.
  static bool OtherViewerBounds(out Rectangle bounds) {
    bounds = Rectangle.Empty;
    Process me = Process.GetCurrentProcess();
    string myPath = Application.ExecutablePath;
    foreach (Process p in Process.GetProcessesByName(me.ProcessName)) {
      try {
        IntPtr h = p.MainWindowHandle;
        RECT r;
        if (p.Id == me.Id || h == IntPtr.Zero || !IsWindowVisible(h) || IsIconic(h) || IsZoomed(h)) continue;
        if (!string.Equals(p.MainModule.FileName, myPath, StringComparison.OrdinalIgnoreCase)) continue;
        if (!GetWindowRect(h, out r)) continue;
        Rectangle b = Rectangle.FromLTRB(r.Left, r.Top, r.Right, r.Bottom);
        if (Screen.FromRectangle(b).Bounds == b) continue; // in full screen
        bounds = b;
        return true;
      } catch { }
    }
    return false;
  }

  // Honour a launcher that asked for the window to open without taking focus.
  protected override bool ShowWithoutActivation {
    get {
      STARTUPINFO si;
      GetStartupInfo(out si);
      return (si.dwFlags & STARTF_USESHOWWINDOW) != 0 &&
        (si.wShowWindow == SW_SHOWNOACTIVATE || si.wShowWindow == SW_SHOWMINNOACTIVE || si.wShowWindow == SW_SHOWNA);
    }
  }

  // ---- WebView2 ----

  async Task StartWebView() {
    try {
      CoreWebView2Environment env = await engine;
      await web.EnsureCoreWebView2Async(env);
    } catch (Exception e) {
      MessageBox.Show("PDF Viewer needs the Microsoft Edge WebView2 Runtime, which comes with Windows 11.\n\n" + e.Message,
        "PDF Viewer", MessageBoxButtons.OK, MessageBoxIcon.Error);
      Close();
      return;
    }

    CoreWebView2 core = web.CoreWebView2;
    CoreWebView2Settings s = core.Settings;
    s.AreDefaultContextMenusEnabled = false;   // the page has its own menu
    s.AreBrowserAcceleratorKeysEnabled = false; // no reload, zoom, find, print…
    s.IsZoomControlEnabled = false;
    s.IsStatusBarEnabled = false;
    try { s.IsReputationCheckingRequired = false; } catch { } // no SmartScreen check of the app's own page: it cost 50 ms

    // The page, and the PDF to open at https://file.example/<name>. A local PDF's folder is handed to
    // the engine, which reads it straight from the disk; one on the network, the engine won't read
    // itself, so this app serves it.
    // Not .local names: Windows looks those up on the network first, which held the page back 2 seconds.
    core.SetVirtualHostNameToFolderMapping("app.example", Program.AppDir, CoreWebView2HostResourceAccessKind.Allow);
    if (startFile != null && !OnNetwork(startFile)) {
      core.SetVirtualHostNameToFolderMapping("file.example", Path.GetDirectoryName(startFile), CoreWebView2HostResourceAccessKind.Allow);
    } else if (startFile != null) {
      core.AddWebResourceRequestedFilter("https://file.example/*", CoreWebView2WebResourceContext.All);
      core.WebResourceRequested += ServeFile;
    }
    core.WebMessageReceived += OnMessage;
    core.DocumentTitleChanged += delegate { Text = core.DocumentTitle; };
    core.NewWindowRequested += delegate(object sender, CoreWebView2NewWindowRequestedEventArgs e) { e.Handled = true; };
    core.NavigationStarting += delegate(object sender, CoreWebView2NavigationStartingEventArgs e) {
      if (!e.Uri.StartsWith("https://app.example/", StringComparison.OrdinalIgnoreCase)) e.Cancel = true;
    };
    EventHandler<CoreWebView2NavigationCompletedEventArgs> ready = null;
    ready = delegate {
      core.NavigationCompleted -= ready;
      SendState();
      if (ActiveForm == this) web.Focus();
    };
    core.NavigationCompleted += ready;
    // The PDF's name goes in the address, so the page starts loading it straight away.
    core.Navigate("https://app.example/index.html" +
      (startFile == null ? "" : "?open=" + Uri.EscapeDataString(Path.GetFileName(startFile))));
  }

  // A \\server\share path or a mapped network drive.
  static bool OnNetwork(string path) {
    try {
      return new Uri(path).IsUnc || new DriveInfo(Path.GetPathRoot(path)).DriveType == DriveType.Network;
    } catch {
      return true; // when in doubt, the way that always works
    }
  }

  void ServeFile(object sender, CoreWebView2WebResourceRequestedEventArgs e) {
    CoreWebView2Environment env = web.CoreWebView2.Environment;
    try {
      Stream data = File.OpenRead(startFile);
      e.Response = env.CreateWebResourceResponse(data, 200, "OK",
        "Content-Type: application/pdf\r\nAccess-Control-Allow-Origin: https://app.example");
    } catch {
      e.Response = env.CreateWebResourceResponse(null, 404, "Not Found", "Access-Control-Allow-Origin: https://app.example");
    }
  }

  // Messages from the page are "command" or "command:argument".
  void OnMessage(object sender, CoreWebView2WebMessageReceivedEventArgs e) {
    string msg;
    try { msg = e.TryGetWebMessageAsString(); } catch { return; }
    int colon = msg.IndexOf(':');
    string cmd = colon < 0 ? msg : msg.Substring(0, colon);
    string arg = colon < 0 ? "" : msg.Substring(colon + 1);
    Action action = null;
    switch (cmd) {
      case "drag": action = delegate { StartNonClientDrag(HTCAPTION); }; break;
      case "resize": action = delegate { StartNonClientDrag(EdgeHitTest(arg)); }; break;
      case "fit": action = delegate { FitToAspect(arg); }; break;
      case "fullscreen": action = delegate { SetFullScreen(arg == "1"); }; break;
      case "topmost": action = delegate { SetAlwaysOnTop(!onTop); }; break;
      case "minimize": action = delegate { WindowState = FormWindowState.Minimized; }; break;
      case "close": action = Close; break;
    }
    // Run after the WebView2 event returns: moving and resizing start a modal loop.
    if (action != null) BeginInvoke(action);
  }

  void SendState() {
    // wheelLines: Windows' "lines per wheel notch", which sets how far one notch scrolls.
    Post("{\"type\":\"state\",\"alwaysOnTop\":" + (onTop ? "true" : "false") +
      ",\"version\":" + Json(Application.ProductVersion) +
      ",\"wheelLines\":" + SystemInformation.MouseWheelScrollLines + "}");
  }

  void Post(string json) {
    if (web.CoreWebView2 != null) web.CoreWebView2.PostWebMessageAsJson(json);
  }

  static string Json(string s) {
    StringBuilder b = new StringBuilder("\"");
    foreach (char c in s) {
      if (c == '"' || c == '\\') b.Append('\\').Append(c);
      else if (c < ' ') b.AppendFormat("\\u{0:x4}", (int)c);
      else b.Append(c);
    }
    return b.Append('"').ToString();
  }

  // ---- Window actions ----

  // Hand the mouse to Windows' own move or resize loop (snapping included).
  void StartNonClientDrag(int hit) {
    if (fullScreen || hit == 0 || (Control.MouseButtons & MouseButtons.Left) == 0) return; // just a click
    if (hit != HTCAPTION && WindowState != FormWindowState.Normal) return;
    ReleaseCapture();
    SendMessage(Handle, WM_NCLBUTTONDOWN, (IntPtr)hit, IntPtr.Zero);
  }

  static int EdgeHitTest(string edge) {
    switch (edge) {
      case "left": return HTLEFT;
      case "right": return HTRIGHT;
      case "top": return HTTOP;
      case "bottom": return HTBOTTOM;
      case "topleft": return HTTOPLEFT;
      case "topright": return HTTOPRIGHT;
      case "bottomleft": return HTBOTTOMLEFT;
      case "bottomright": return HTBOTTOMRIGHT;
    }
    return 0;
  }

  void SetAlwaysOnTop(bool on) {
    onTop = on;
    SetWindowPos(Handle, on ? HWND_TOPMOST : HWND_NOTOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
    SendState();
  }

  // Resize to the page's aspect ratio, keeping the height when it fits on screen.
  void FitToAspect(string arg) {
    double aspect;
    if (fullScreen || !double.TryParse(arg, NumberStyles.Float, CultureInfo.InvariantCulture, out aspect) || aspect <= 0) return;
    if (WindowState != FormWindowState.Normal) WindowState = FormWindowState.Normal;
    Rectangle area = Screen.FromHandle(Handle).WorkingArea;
    int h = Math.Min(Height, area.Height);
    int w = (int)Math.Round(h * aspect);
    if (w > area.Width) {
      w = area.Width;
      h = (int)Math.Round(w / aspect);
    }
    Size = new Size(w, h);
  }

  void SetFullScreen(bool on) {
    if (on == fullScreen) return;
    if (on) {
      wasMaximized = WindowState == FormWindowState.Maximized;
      restoreBounds = wasMaximized ? RestoreBounds : Bounds;
      Rectangle screen = Screen.FromHandle(Handle).Bounds;
      WindowState = FormWindowState.Normal;
      fullScreen = true;
      UpdateStyles(); // drop the frame
      SetCorners(false);
      Bounds = screen;
    } else {
      fullScreen = false;
      UpdateStyles();
      SetCorners(true);
      Bounds = restoreBounds;
      if (wasMaximized) WindowState = FormWindowState.Maximized;
      Post("{\"type\":\"left-full-screen\"}");
    }
  }

  // ---- The invisible frame ----

  // A real frame (shadow, rounded corners, snapping, resizing) whose title bar and
  // borders are given to the page: see WM_NCCALCSIZE below.
  protected override CreateParams CreateParams {
    get {
      CreateParams cp = base.CreateParams;
      cp.Style &= ~WS_POPUP;
      if (!fullScreen) cp.Style |= WS_CAPTION | WS_THICKFRAME | WS_SYSMENU | WS_MINIMIZEBOX | WS_MAXIMIZEBOX;
      if (onTop) cp.ExStyle |= WS_EX_TOPMOST;
      else cp.ExStyle &= ~WS_EX_TOPMOST;
      return cp;
    }
  }

  protected override void WndProc(ref Message m) {
    switch (m.Msg) {
      case WM_NCCALCSIZE:
        if (m.WParam == IntPtr.Zero) break;
        if (IsZoomed(Handle) && !fullScreen) {
          // A maximized window hangs its frame off the screen; keep the page inside the work area.
          NCCALCSIZE_PARAMS p = (NCCALCSIZE_PARAMS)Marshal.PtrToStructure(m.LParam, typeof(NCCALCSIZE_PARAMS));
          Rectangle area = Screen.FromRectangle(Rectangle.FromLTRB(p.rgrc0.Left, p.rgrc0.Top, p.rgrc0.Right, p.rgrc0.Bottom)).WorkingArea;
          p.rgrc0 = new RECT { Left = area.Left, Top = area.Top, Right = area.Right, Bottom = area.Bottom };
          Marshal.StructureToPtr(p, m.LParam, false);
        }
        m.Result = IntPtr.Zero; // the whole window is client area
        return;
      case WM_NCHITTEST:
        m.Result = (IntPtr)HTCLIENT;
        return;
      case WM_NCACTIVATE:
        m.LParam = (IntPtr)(-1); // don't repaint the (hidden) title bar
        break;
      case WM_DPICHANGED:
        if (fullScreen) break;
        // Moved to a screen with other scaling: take the size Windows suggests.
        RECT r = (RECT)Marshal.PtrToStructure(m.LParam, typeof(RECT));
        SetWindowPos(Handle, IntPtr.Zero, r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top, SWP_NOZORDER | SWP_NOACTIVATE);
        m.Result = IntPtr.Zero;
        return;
    }
    base.WndProc(ref m);
  }

  // ---- Win32 ----

  const int WS_POPUP = unchecked((int)0x80000000), WS_CAPTION = 0x00C00000, WS_THICKFRAME = 0x00040000;
  const int WS_SYSMENU = 0x00080000, WS_MINIMIZEBOX = 0x00020000, WS_MAXIMIZEBOX = 0x00010000;
  const int WM_NCCALCSIZE = 0x0083, WM_NCHITTEST = 0x0084, WM_NCACTIVATE = 0x0086;
  const int WM_NCLBUTTONDOWN = 0x00A1, WM_DPICHANGED = 0x02E0;
  const int HTCLIENT = 1, HTCAPTION = 2, HTLEFT = 10, HTRIGHT = 11, HTTOP = 12, HTTOPLEFT = 13;
  const int HTTOPRIGHT = 14, HTBOTTOM = 15, HTBOTTOMLEFT = 16, HTBOTTOMRIGHT = 17;
  const int WS_EX_TOPMOST = 0x00000008;
  const uint SWP_NOSIZE = 0x0001, SWP_NOMOVE = 0x0002, SWP_NOZORDER = 0x0004, SWP_NOACTIVATE = 0x0010;
  static readonly IntPtr HWND_TOPMOST = new IntPtr(-1), HWND_NOTOPMOST = new IntPtr(-2);
  const int DWMWA_WINDOW_CORNER_PREFERENCE = 33, DWMWCP_DONOTROUND = 1, DWMWCP_ROUND = 2;
  const int STARTF_USESHOWWINDOW = 0x1;
  const short SW_SHOWMINNOACTIVE = 7, SW_SHOWNA = 8, SW_SHOWNOACTIVATE = 4;

  [StructLayout(LayoutKind.Sequential)]
  struct RECT { public int Left, Top, Right, Bottom; }

  [StructLayout(LayoutKind.Sequential)]
  struct NCCALCSIZE_PARAMS { public RECT rgrc0, rgrc1, rgrc2; public IntPtr lppos; }

  [StructLayout(LayoutKind.Sequential)]
  struct MARGINS { public int Left, Right, Top, Bottom; }

  [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
  struct STARTUPINFO {
    public int cb;
    public IntPtr lpReserved, lpDesktop, lpTitle;
    public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
    public short wShowWindow, cbReserved2;
    public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
  }

  [DllImport("user32.dll")] static extern bool ReleaseCapture();
  [DllImport("user32.dll")] static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);
  [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
  [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);
  [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr hWnd);
  [DllImport("user32.dll")] static extern bool IsIconic(IntPtr hWnd);
  [DllImport("user32.dll")] static extern bool IsZoomed(IntPtr hWnd);
  [DllImport("kernel32.dll", EntryPoint = "GetStartupInfoW")] static extern void GetStartupInfo(out STARTUPINFO info);
  [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr hWnd, int attribute, ref int value, int size);
  [DllImport("dwmapi.dll")] static extern int DwmExtendFrameIntoClientArea(IntPtr hWnd, ref MARGINS margins);
}
