// PDF Viewer.exe - a borderless window around app\index.html, drawn by the WebView2
// runtime that Windows 11 already has. The page does all the viewing; this file only
// provides the window: moving, resizing, full screen, always on top, opening a file.
//
// Built by build\build.ps1 with the .NET Framework compiler that ships with Windows
// (C# 5: no string interpolation, no ?. operator).

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using Timer = System.Windows.Forms.Timer;

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
    string file = null;
    foreach (string arg in args) {
      if (arg.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase) && File.Exists(arg)) {
        file = Path.GetFullPath(arg);
        break;
      }
    }
    // Page 1 starts rendering before anything else, alongside the window and the browser engine.
    Preview preview = file == null ? null : Preview.Start(file);
    Application.Run(new ViewerForm(file, preview));
  }
}

// Until the page shows the PDF, the window shows the pages in view as Windows' own PDF engine draws
// them, placed exactly where the page will show them; or the PDF's name, until that's ready or if it
// can't. The window is on screen at once, while the browser engine takes another half second to start.
class Cover : Control {
  Preview preview;

  public Cover(string text) {
    Text = text;
    Dock = DockStyle.Fill;
    ForeColor = Color.FromArgb(0x9b, 0x9d, 0xa4);
    Font = new Font("Segoe UI", 11f);
    SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint, true);
  }

  public void ShowPreview(Preview p) {
    preview = p;
    Invalidate();
  }

  protected override void OnPaint(PaintEventArgs e) {
    e.Graphics.Clear(BackColor);
    if (preview != null) {
      // Pixel for pixel, the quickest: opaque pages, copied rather than blended. Should the window be
      // resized in the moment before the page takes over, they stay where they were.
      e.Graphics.CompositingMode = CompositingMode.SourceCopy;
      for (int i = 0; i < preview.Images.Count; i++) e.Graphics.DrawImageUnscaled(preview.Images[i], preview.Spots[i].Location);
      return;
    }
    TextRenderer.DrawText(e.Graphics, Text, Font, ClientRectangle, ForeColor,
      TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis);
  }

  protected override void Dispose(bool disposing) {
    if (disposing && preview != null) preview.Dispose();
    base.Dispose(disposing);
  }
}

// The pages in view when the PDF opens, drawn by Windows' own PDF engine (Windows.Data.Pdf): ready
// about 0.1 s after the launch, in time for the window's first appearance, where the browser engine
// and pdf.js show them at 0.6 s. They render on their own thread, from the very start; the window
// says where they go once it exists, and only what's in view is rendered.
class Preview : IDisposable {
  const double Gap = 8; // between pages in continuous mode, as in index.html
  readonly string file;
  readonly ManualResetEvent placed = new ManualResetEvent(false);
  Size client;           // the window's inside, device pixels
  int dpi;
  string fit, mode;      // the page's view settings: "width" or "page", "continuous" or "single"
  Action<Preview> done;  // called on the preview's thread once it's ready, or has failed

  public readonly List<Bitmap> Images = new List<Bitmap>();       // the pages in view, or the parts in view
  public readonly List<Rectangle> Spots = new List<Rectangle>();  // where each goes in the window
  public volatile bool Finished;

  public static Preview Start(string file) {
    Preview p = new Preview(file);
    Thread t = new Thread(p.Work); // MTA: Windows' engine is called from here, waiting on its results
    t.IsBackground = true;
    t.Start();
    return p;
  }

  Preview(string file) { this.file = file; }

  // From the window, once it exists. view: "fit,mode" as the page last reported it.
  public void Place(Size client, int dpi, string view, Action<Preview> done) {
    string[] v = view.Split(',');
    this.client = client;
    this.dpi = dpi;
    fit = v[0];
    mode = v[1];
    this.done = done;
    placed.Set();
  }

  void Work() {
    try {
      Render();
    } catch {
      // A password, a broken file: the window shows the name, and the page says what's wrong.
      foreach (Bitmap b in Images) b.Dispose();
      Images.Clear();
      Spots.Clear();
    }
    Finished = true;
    if (placed.WaitOne(0) && done != null) done(this);
  }

  // Kept apart, so Windows' engine is loaded only on this thread.
  // The engine draws through a Direct3D device of this app's own: a software one (WARP), ready in
  // 15 ms, where the graphics card takes 160 ms to start.
  [MethodImpl(MethodImplOptions.NoInlining)]
  void Render() {
    Windows.Data.Pdf.PdfDocument doc = Wait(Windows.Data.Pdf.PdfDocument.LoadFromFileAsync(
      Wait(Windows.Storage.StorageFile.GetFileFromPathAsync(file))));
    IntPtr device = IntPtr.Zero, context = IntPtr.Zero, dxgi = IntPtr.Zero, renderer = IntPtr.Zero;
    try {
      int level;
      Check(D3D11CreateDevice(IntPtr.Zero, D3D_DRIVER_TYPE_WARP, IntPtr.Zero, D3D11_CREATE_DEVICE_BGRA_SUPPORT,
        IntPtr.Zero, 0, D3D11_SDK_VERSION, out device, out level, out context));
      Guid iid = IID_IDXGIDevice;
      Check(Marshal.QueryInterface(device, ref iid, out dxgi));
      Check(PdfCreateRenderer(dxgi, out renderer));
      placed.WaitOne();

      // The pages from the top of the document, laid out as layout() in index.html does: as wide as
      // the view ("width") or fitting in it ("page"), centred across; one below the other, or the
      // first one alone, centred down too ("single"). In CSS pixels, which the page counts in.
      double dpr = dpi / 96.0;
      double viewW = Math.Round(client.Width / dpr), viewH = Math.Round(client.Height / dpr);
      double top = 0;
      Rectangle window = new Rectangle(Point.Empty, client);
      for (uint i = 0; i < doc.PageCount; i++) {
        using (Windows.Data.Pdf.PdfPage page = doc.GetPage(i)) {
          Windows.Foundation.Size dips = page.Size; // 1/96 in, rotation applied
          double w = dips.Width * 0.75, h = dips.Height * 0.75; // points, as pdf.js measures pages
          double s = fit == "page" ? Math.Min(viewW / w, viewH / h) : viewW / w;
          double pageW = Math.Floor(w * s), pageH = Math.Floor(h * s);
          double x = Math.Max(0, (viewW - pageW) / 2);
          double y = mode == "single" ? Math.Max(0, (viewH - pageH) / 2) : top;
          Rectangle rect = new Rectangle((int)Math.Round(x * dpr), (int)Math.Round(y * dpr),
            (int)Math.Round(pageW * dpr), (int)Math.Round(pageH * dpr));
          Rectangle visible = Rectangle.Intersect(rect, window);
          if (visible.Width <= 0 || visible.Height <= 0) break;
          Images.Add(RenderPart(page, dips.Width / rect.Width, rect, visible, device, context, renderer));
          Spots.Add(visible);
          if (mode == "single") break;
          top = y + pageH + Gap;
        }
      }
    } finally {
      foreach (IntPtr com in new IntPtr[] { renderer, dxgi, context, device }) {
        if (com != IntPtr.Zero) Marshal.Release(com);
      }
      Marshal.FinalReleaseComObject(doc); // lets go of the file now, not whenever the garbage is collected
    }
  }

  // The visible part of a page placed at rect, at the window's resolution; k: page units per pixel.
  static Bitmap RenderPart(Windows.Data.Pdf.PdfPage page, double k, Rectangle rect, Rectangle visible,
      IntPtr device, IntPtr context, IntPtr renderer) {
    IntPtr target = IntPtr.Zero, staging = IntPtr.Zero, surface = IntPtr.Zero, unknown = IntPtr.Zero;
    try {
      TextureDesc desc = new TextureDesc {
        Width = (uint)visible.Width, Height = (uint)visible.Height, MipLevels = 1, ArraySize = 1,
        Format = DXGI_FORMAT_B8G8R8A8_UNORM, SampleCount = 1, BindFlags = D3D11_BIND_RENDER_TARGET | D3D11_BIND_SHADER_RESOURCE
      };
      Check(Slot<CreateTexture2D>(device, 5)(device, ref desc, IntPtr.Zero, out target));
      desc.Usage = D3D11_USAGE_STAGING; // a copy the CPU can read
      desc.BindFlags = 0;
      desc.CpuAccessFlags = D3D11_CPU_ACCESS_READ;
      Check(Slot<CreateTexture2D>(device, 5)(device, ref desc, IntPtr.Zero, out staging));
      Guid iid = IID_IDXGISurface;
      Check(Marshal.QueryInterface(target, ref iid, out surface));

      RenderParams p = new RenderParams {
        Left = (float)((visible.Left - rect.X) * k), Top = (float)((visible.Top - rect.Y) * k),
        Right = (float)((visible.Right - rect.X) * k), Bottom = (float)((visible.Bottom - rect.Y) * k),
        Width = (uint)visible.Width, Height = (uint)visible.Height,
        R = 1, G = 1, B = 1, A = 1, IgnoreHighContrast = 1 // true colours, as pdf.js shows them
      };
      unknown = Marshal.GetIUnknownForObject(page);
      Check(Slot<RenderPageToSurface>(renderer, 3)(renderer, unknown, surface, new POINT(), ref p));

      Slot<CopyResource>(context, 47)(context, staging, target);
      Mapped m;
      Check(Slot<Map>(context, 14)(context, staging, 0, D3D11_MAP_READ, 0, out m));
      Bitmap image = new Bitmap(visible.Width, visible.Height, PixelFormat.Format32bppPArgb); // the same bytes, BGRA
      BitmapData bits = image.LockBits(new Rectangle(Point.Empty, image.Size), ImageLockMode.WriteOnly, image.PixelFormat);
      for (int y = 0; y < visible.Height; y++) {
        CopyMemory(bits.Scan0 + y * bits.Stride, m.Data + y * (int)m.RowPitch, (UIntPtr)(uint)(visible.Width * 4));
      }
      image.UnlockBits(bits);
      Slot<Unmap>(context, 15)(context, staging, 0);
      return image;
    } finally {
      foreach (IntPtr com in new IntPtr[] { unknown, surface, staging, target }) {
        if (com != IntPtr.Zero) Marshal.Release(com);
      }
    }
  }

  static T Wait<T>(Windows.Foundation.IAsyncOperation<T> op) {
    using (ManualResetEvent ready = new ManualResetEvent(false)) {
      op.Completed = delegate { ready.Set(); };
      ready.WaitOne();
    }
    return op.GetResults();
  }

  static void Check(int hr) {
    if (hr < 0) Marshal.ThrowExceptionForHR(hr);
  }

  // A method of a COM object, by its place in the object's table of methods (from the Windows SDK headers).
  static T Slot<T>(IntPtr obj, int index) where T : class {
    IntPtr method = Marshal.ReadIntPtr(Marshal.ReadIntPtr(obj), index * IntPtr.Size);
    return (T)(object)Marshal.GetDelegateForFunctionPointer(method, typeof(T));
  }

  public void Dispose() {
    foreach (Bitmap b in Images) b.Dispose();
    Images.Clear();
  }

  public static int DpiOf(Control c) {
    try { return (int)GetDpiForWindow(c.Handle); } catch { return 96; }
  }

  // ---- Direct3D 11 and Windows.Data.Pdf's native interface (d3d11.h, windows.data.pdf.interop.h) ----

  const int D3D_DRIVER_TYPE_WARP = 5, D3D11_SDK_VERSION = 7, D3D11_MAP_READ = 1;
  const uint D3D11_CREATE_DEVICE_BGRA_SUPPORT = 0x20, DXGI_FORMAT_B8G8R8A8_UNORM = 87, D3D11_USAGE_STAGING = 3;
  const uint D3D11_BIND_SHADER_RESOURCE = 0x8, D3D11_BIND_RENDER_TARGET = 0x20, D3D11_CPU_ACCESS_READ = 0x20000;
  static readonly Guid IID_IDXGIDevice = new Guid("54ec77fa-1377-44e6-8c32-88fd5f44c84c");
  static readonly Guid IID_IDXGISurface = new Guid("cafcb56c-6ac3-4889-bf47-9e23bbd260ec");

  [StructLayout(LayoutKind.Sequential)]
  struct TextureDesc {
    public uint Width, Height, MipLevels, ArraySize, Format, SampleCount, SampleQuality, Usage, BindFlags, CpuAccessFlags, MiscFlags;
  }

  [StructLayout(LayoutKind.Sequential)]
  struct Mapped { public IntPtr Data; public uint RowPitch, DepthPitch; }

  [StructLayout(LayoutKind.Sequential)]
  struct POINT { public int X, Y; }

  [StructLayout(LayoutKind.Sequential)]
  struct RenderParams {                    // PDF_RENDER_PARAMS
    public float Left, Top, Right, Bottom; // the part of the page, in its own units (1/96 in)
    public uint Width, Height;             // the image's size
    public float R, G, B, A;               // background
    public byte IgnoreHighContrast;
  }

  // ID3D11Device method 5, IPdfRendererNative method 3, ID3D11DeviceContext methods 47, 14 and 15.
  [UnmanagedFunctionPointer(CallingConvention.StdCall)]
  delegate int CreateTexture2D(IntPtr self, ref TextureDesc desc, IntPtr initialData, out IntPtr texture);
  [UnmanagedFunctionPointer(CallingConvention.StdCall)]
  delegate int RenderPageToSurface(IntPtr self, IntPtr page, IntPtr surface, POINT offset, ref RenderParams p);
  [UnmanagedFunctionPointer(CallingConvention.StdCall)]
  delegate void CopyResource(IntPtr self, IntPtr destination, IntPtr source);
  [UnmanagedFunctionPointer(CallingConvention.StdCall)]
  delegate int Map(IntPtr self, IntPtr resource, uint subresource, int type, uint flags, out Mapped mapped);
  [UnmanagedFunctionPointer(CallingConvention.StdCall)]
  delegate void Unmap(IntPtr self, IntPtr resource, uint subresource);

  [DllImport("d3d11.dll")]
  static extern int D3D11CreateDevice(IntPtr adapter, int driverType, IntPtr software, uint flags,
    IntPtr featureLevels, uint featureLevelCount, uint sdkVersion, out IntPtr device, out int featureLevel, out IntPtr context);
  [DllImport("Windows.Data.Pdf.dll")] static extern int PdfCreateRenderer(IntPtr dxgiDevice, out IntPtr renderer);
  [DllImport("kernel32.dll", EntryPoint = "RtlMoveMemory")] static extern void CopyMemory(IntPtr to, IntPtr from, UIntPtr count);
  [DllImport("user32.dll")] static extern uint GetDpiForWindow(IntPtr hWnd);
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

  // The page's view settings, "fit,mode", which the page keeps and reports: the window needs them
  // before the page exists, to place its preview of the first pages where the page will show them.
  static string ViewPath { get { return Path.Combine(Program.DataDir, "view.txt"); } }
  const string DefaultView = "width,continuous";

  public static string LoadView() {
    try { return ValidView(File.ReadAllText(ViewPath).Trim()) ?? DefaultView; } catch { return DefaultView; }
  }

  public static void SaveView(string view) {
    if (ValidView(view) == null || view == LoadView()) return;
    try {
      Directory.CreateDirectory(Program.DataDir);
      File.WriteAllText(ViewPath, view);
    } catch { }
  }

  static string ValidView(string view) {
    string[] v = view.Split(',');
    return v.Length == 2 && (v[0] == "width" || v[0] == "page") && (v[1] == "continuous" || v[1] == "single") ? view : null;
  }
}

class ViewerForm : Form {
  WebView2 web;               // made with the window, under the cover
  Cover cover;                // until the page shows the PDF
  readonly Preview preview;   // the first pages, for the cover
  bool shown, revealed;       // the window is shown, then uncloaked
  readonly string startFile;
  bool onTop;                 // always on top (not Form.TopMost: setting that activates the window)
  bool fullScreen;
  bool wasMaximized;
  Rectangle restoreBounds;    // where to go back to after full screen

  public ViewerForm(string file, Preview preview) {
    startFile = file;
    this.preview = preview;
    Text = file == null ? "PDF Viewer" : Path.GetFileName(file) + " – PDF Viewer";
    FormBorderStyle = FormBorderStyle.None;
    StartPosition = FormStartPosition.Manual;
    BackColor = Color.FromArgb(0x1e, 0x1f, 0x22);
    MinimumSize = new Size(200, 150);
    try { Icon = new Icon(Path.Combine(Program.AppDir, "icon.ico")); } catch { }

    Rectangle saved;
    Settings.Load(out saved, out onTop);
    Bounds = InitialBounds(saved);

    cover = new Cover(file == null ? "PDF Viewer" : Path.GetFileName(file));
    cover.MouseDown += delegate { StartNonClientDrag(HTCAPTION); };
    Controls.Add(cover);

    Load += async delegate { await StartWebView(); }; // just before the window shows
    Shown += delegate {
      shown = true;
      if (preview == null || preview.Finished) {
        Reveal();
        return;
      }
      // The pages are due any moment: the window waits a little for them, so as to appear with them
      // in it rather than flash the name first. A slow page is shown when it comes, over the name.
      Timer wait = new Timer { Interval = PreviewWaitMs };
      wait.Tick += delegate { wait.Dispose(); Reveal(); };
      wait.Start();
    };
    Activated += delegate { if (web != null) web.Focus(); };
    FormClosing += delegate {
      Rectangle b = fullScreen ? restoreBounds : WindowState == FormWindowState.Normal ? Bounds : RestoreBounds;
      Settings.Save(b, onTop);
    };
  }

  protected override void OnHandleCreated(EventArgs e) {
    base.OnHandleCreated(e);
    // Hidden from view until painted: a new window otherwise flashes white for a frame.
    SetCloak(true);
    // Windows 11 leaves a window without a title bar square and shadowless unless asked.
    SetCorners(true);
    MARGINS m = new MARGINS { Left = 1, Right = 1, Top = 1, Bottom = 1 };
    DwmExtendFrameIntoClientArea(Handle, ref m);
    // Now the window's screen is known, so is where the pages go. All of the window is its inside
    // (see WM_NCCALCSIZE), though ClientSize doesn't know it yet.
    if (preview != null && !RecreatingHandle) preview.Place(Size, Preview.DpiOf(this), Settings.LoadView(), PreviewReady);
  }

  const int PreviewWaitMs = 100;

  // On the preview's thread.
  void PreviewReady(Preview p) {
    try {
      BeginInvoke((Action)delegate {
        if (cover != null && p.Images.Count > 0) {
          cover.ShowPreview(p);
          if (revealed) cover.Update();
        }
        if (shown) Reveal();
      });
    } catch { } // the window is already gone
  }

  // Uncloak the window, once it shows what it should.
  void Reveal() {
    if (revealed) return;
    revealed = true;
    if (cover != null) cover.Update(); // painted before the window is uncloaked
    else Update();
    DwmFlush(); // and on screen: uncloaked straight away, its first frame would still be white
    SetCloak(false);
  }

  void SetCloak(bool on) {
    int cloak = on ? 1 : 0;
    DwmSetWindowAttribute(Handle, DWMWA_CLOAK, ref cloak, sizeof(int));
  }

  // The page says when the PDF is on screen; or it couldn't open it, and has something to say.
  void Uncover() {
    if (cover == null) return;
    Controls.Remove(cover);
    cover.Dispose();
    cover = null;
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

  // Kept apart, so the engine's libraries load only here, once the window and its cover exist.
  async Task StartWebView() {
    web = new WebView2();
    web.DefaultBackgroundColor = BackColor;
    web.Dock = DockStyle.Fill;
    Controls.Add(web); // under the cover
    try {
      CoreWebView2Environment.SetLoaderDllFolderPath(Program.AppDir);
      CoreWebView2EnvironmentOptions options = new CoreWebView2EnvironmentOptions();
      options.EnableTrackingPrevention = false; // only the app's own page is ever shown: nothing to track
      CoreWebView2Environment env = await CoreWebView2Environment.CreateAsync(null, Path.Combine(Program.DataDir, "WebView2"), options);
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
      // Should the page never say it shows the PDF, the cover still goes.
      Timer fallback = new Timer { Interval = 3000 };
      fallback.Tick += delegate { fallback.Dispose(); Uncover(); };
      fallback.Start();
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
      case "uncover": action = Uncover; break;
      case "view": action = delegate { Settings.SaveView(arg); }; break;
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
    if (web != null && web.CoreWebView2 != null) web.CoreWebView2.PostWebMessageAsJson(json);
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
  const int DWMWA_CLOAK = 13, DWMWA_WINDOW_CORNER_PREFERENCE = 33, DWMWCP_DONOTROUND = 1, DWMWCP_ROUND = 2;
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
  [DllImport("dwmapi.dll")] static extern int DwmFlush();
}
