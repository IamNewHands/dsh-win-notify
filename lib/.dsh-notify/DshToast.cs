using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;
using Windows.Data.Xml.Dom;
using Windows.UI.Notifications;

// Compiled by the plugin with:
//   csc /target:winexe /platform:anycpu /win32icon:notify.ico
//       /reference:System.Runtime.dll /reference:System.Runtime.InteropServices.WindowsRuntime.dll
//       /reference:Windows.Foundation.winmd /reference:Windows.Data.winmd /reference:Windows.UI.winmd
// => legacy csc (C# 5): no string interpolation, no ?., no out-var, no expression bodies.
// => only mscorlib + the WinRT facades are referenceable: no System.Windows.Forms / System.Drawing /
//    System.Diagnostics (Process) usage is allowed here.

internal static class Native {
  [DllImport("shell32.dll", SetLastError = true)]
  internal static extern void SetCurrentProcessExplicitAppUserModelID(
    [MarshalAs(UnmanagedType.LPWStr)] string AppID);
}

// --- Minimal COM interop to create a Start Menu shortcut with an AUMID ---
// WARNING: the vtable order below must match shobjidl.h exactly; do not reorder.
[ComImport, Guid("000214F9-0000-0000-C000-000000000046"),
 InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IShellLinkW {
  void GetPath(StringBuilder pszFile, int cch, IntPtr pfd, uint fFlags);
  void GetIDList(out IntPtr ppidl);
  void SetIDList(IntPtr pidl);
  void GetDescription(StringBuilder pszName, int cch);
  void SetDescription(string pszName);
  void GetWorkingDirectory(StringBuilder pszDir, int cch);
  void SetWorkingDirectory(string pszDir);
  void GetArguments(StringBuilder pszArgs, int cch);
  void SetArguments(string pszArgs);
  void GetHotkey(out short pwHotkey);
  void SetHotkey(short wHotkey);
  void GetShowCmd(out int piShowCmd);
  void SetShowCmd(int iShowCmd);
  void GetIconLocation(StringBuilder pszIconPath, int cch, out int piIcon);
  void SetIconLocation(string pszIconPath, int iIcon);
  void SetRelativePath(string pszPathRel, uint dwReserved);
  void Resolve(IntPtr hwnd, uint fFlags);
  void SetPath(string pszFile);
}

[ComImport, Guid("0000010B-0000-0000-C000-000000000046"),
 InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IPersistFile {
  void GetClassID(out Guid pClassID);
  void IsDirty();
  void Load(string pszFileName, uint dwMode);
  void Save(string pszFileName, bool fRemember);
  void SaveCompleted(string pszFileName);
  void GetCurFile(out string ppszFileName);
}

[StructLayout(LayoutKind.Sequential)]
struct PROPERTYKEY {
  public Guid fmtid;
  public uint pid;
}

[StructLayout(LayoutKind.Explicit)]
struct PROPVARIANT {
  [FieldOffset(0)] public ushort vt;
  [FieldOffset(8)] public IntPtr pwszVal;
  public void SetString(string s) {
    vt = 31; // VT_LPWSTR
    if (pwszVal != IntPtr.Zero) Marshal.FreeCoTaskMem(pwszVal);
    pwszVal = Marshal.StringToCoTaskMemUni(s);
  }
  public void Clear() {
    if (pwszVal != IntPtr.Zero) { Marshal.FreeCoTaskMem(pwszVal); pwszVal = IntPtr.Zero; }
    vt = 0;
  }
}

[ComImport, Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"),
 InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IPropertyStore {
  void GetCount(out uint cProps);
  void GetAt(uint iProp, out PROPERTYKEY pkey);
  void GetValue(ref PROPERTYKEY key, out PROPVARIANT pv);
  void SetValue(ref PROPERTYKEY key, ref PROPVARIANT pv);
  void Commit();
}

/// <summary>
/// Timestamped log next to the helper. Untimestamped lines made the earlier
/// click investigations unreadable, so every line now carries HH:mm:ss.fff.
/// </summary>
internal static class Log {
  private static string path;
  public static void Init(string exeDir) {
    if (path != null) return;
    try { path = Path.Combine(exeDir, "dsh-toast.log"); } catch {}
  }
  public static void W(string msg) {
    try {
      string one = msg == null ? "" : msg.Replace("\r", " ").Replace("\n", " ");
      File.AppendAllText(path, DateTime.Now.ToString("HH:mm:ss.fff") + " " + one + "\r\n");
    } catch {}
  }
}

/// <summary>Raw Win32 access used by the activation path. P/Invoke only: no extra references.</summary>
internal static class Win32 {
  internal const int SW_HIDE = 0;
  internal const int SW_SHOWNORMAL = 1;
  internal const int SW_SHOW = 5;
  internal const int SW_RESTORE = 9;
  internal const int GWL_EXSTYLE = -20;
  internal const int GW_OWNER = 4;
  internal const int WS_EX_TOOLWINDOW = 0x00000080;
  internal const uint SWP_NOSIZE = 0x0001;
  internal const uint SWP_NOMOVE = 0x0002;
  internal const uint SWP_SHOWWINDOW = 0x0040;
  internal const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
  internal const uint FLASHW_ALL = 3;
  internal const uint FLASHW_TIMERNOFG = 12;

  internal delegate bool EnumProc(IntPtr hWnd, IntPtr lParam);

  [StructLayout(LayoutKind.Sequential)]
  internal struct FLASHWINFO {
    public uint cbSize;
    public IntPtr hwnd;
    public uint dwFlags;
    public uint uCount;
    public uint dwTimeout;
  }

  [DllImport("user32.dll")]
  internal static extern bool EnumWindows(EnumProc callback, IntPtr lParam);
  [DllImport("user32.dll")]
  internal static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
  [DllImport("user32.dll")]
  internal static extern bool IsWindowVisible(IntPtr hWnd);
  [DllImport("user32.dll")]
  internal static extern bool IsIconic(IntPtr hWnd);
  [DllImport("user32.dll")]
  internal static extern IntPtr GetWindow(IntPtr hWnd, uint cmd);
  [DllImport("user32.dll")]
  internal static extern bool ShowWindow(IntPtr hWnd, int cmd);
  [DllImport("user32.dll")]
  internal static extern int GetWindowLong(IntPtr hWnd, int index);
  [DllImport("user32.dll", CharSet = CharSet.Unicode)]
  internal static extern int GetWindowText(IntPtr hWnd, StringBuilder text, int maxCount);
  [DllImport("user32.dll", CharSet = CharSet.Unicode)]
  internal static extern int GetClassName(IntPtr hWnd, StringBuilder name, int maxCount);
  [DllImport("user32.dll")]
  internal static extern bool SetForegroundWindow(IntPtr hWnd);
  [DllImport("user32.dll")]
  internal static extern bool BringWindowToTop(IntPtr hWnd);
  [DllImport("user32.dll")]
  internal static extern IntPtr GetForegroundWindow();
  [DllImport("user32.dll")]
  internal static extern IntPtr SetFocus(IntPtr hWnd);
  [DllImport("user32.dll")]
  internal static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
  [DllImport("user32.dll")]
  internal static extern bool AttachThreadInput(uint attach, uint attachTo, bool fAttach);
  [DllImport("user32.dll")]
  internal static extern void SwitchToThisWindow(IntPtr hWnd, bool altTab);
  [DllImport("user32.dll")]
  internal static extern bool FlashWindowEx(ref FLASHWINFO info);
  [DllImport("kernel32.dll")]
  internal static extern uint GetCurrentThreadId();
  [DllImport("kernel32.dll", SetLastError = true)]
  internal static extern IntPtr OpenProcess(uint access, bool inheritHandle, uint pid);
  [DllImport("kernel32.dll", SetLastError = true)]
  internal static extern bool CloseHandle(IntPtr handle);
  [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
  internal static extern bool QueryFullProcessImageName(IntPtr process, uint flags, StringBuilder name, ref int size);
  [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
  internal static extern IntPtr ShellExecute(IntPtr hwnd, string verb, string file, string parameters, string directory, int showCmd);

  internal static string Title(IntPtr h) {
    try {
      StringBuilder sb = new StringBuilder(512);
      GetWindowText(h, sb, sb.Capacity);
      return sb.ToString();
    } catch { return ""; }
  }

  internal static string ClassName(IntPtr h) {
    try {
      StringBuilder sb = new StringBuilder(256);
      GetClassName(h, sb, sb.Capacity);
      return sb.ToString();
    } catch { return ""; }
  }
}

internal static class DshTarget {
  private const string SHELL_LINK_CLSID = "00021401-0000-0000-C000-000000000046";
  internal const string SHELL_EXE_NAME = "DSH Desktop.exe";

  /// <summary>Resolve the DSH Desktop shell EXE. Never returns the helper itself.</summary>
  internal static string ResolveShellExe(string hint) {
    string self = null;
    try { self = System.Reflection.Assembly.GetExecutingAssembly().Location; } catch {}
    foreach (string candidate in Candidates(hint)) {
      try {
        if (string.IsNullOrEmpty(candidate)) continue;
        if (!File.Exists(candidate)) continue;
        string full = Path.GetFullPath(candidate);
        if (!string.IsNullOrEmpty(self) && string.Equals(full, self, StringComparison.OrdinalIgnoreCase)) {
          Log.W("resolver: skipping self-reference " + full);
          continue;
        }
        return full;
      } catch {}
    }
    return null;
  }

  private static IEnumerable<string> Candidates(string hint) {
    if (!string.IsNullOrEmpty(hint)) yield return hint;

    string local = null;
    string pf = null;
    string pf86 = null;
    try { local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData); } catch {}
    try { pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles); } catch {}
    try { pf86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86); } catch {}

    if (!string.IsNullOrEmpty(local)) {
      yield return Path.Combine(local, "Programs", "DSH Desktop", SHELL_EXE_NAME);
      yield return Path.Combine(local, "Programs", "dsh-desktop", SHELL_EXE_NAME);
      yield return Path.Combine(local, "Programs", "deepseek-harness", SHELL_EXE_NAME);
      yield return Path.Combine(local, "DSH Desktop", SHELL_EXE_NAME);
    }
    if (!string.IsNullOrEmpty(pf)) {
      yield return Path.Combine(pf, "DSH Desktop", SHELL_EXE_NAME);
      yield return Path.Combine(pf, "DeepSeek Harness", SHELL_EXE_NAME);
    }
    if (!string.IsNullOrEmpty(pf86)) {
      yield return Path.Combine(pf86, "DSH Desktop", SHELL_EXE_NAME);
    }

    foreach (string lnk in ShortcutCandidates()) {
      string target = ReadShortcutTarget(lnk);
      if (!string.IsNullOrEmpty(target)) yield return target;
    }
  }

  private static IEnumerable<string> ShortcutCandidates() {
    // NOTE: only installer-owned launchers are listed. "DeepSeek Harness.lnk" is
    // written by this helper (its target is the helper), so it is deliberately absent.
    string[] names = { "DSH Desktop.lnk", "DeepSeek Harness Desktop.lnk" };
    List<string> roots = new List<string>();
    try {
      roots.Add(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Microsoft", "Windows", "Start Menu", "Programs"));
      roots.Add(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "Microsoft", "Windows", "Start Menu", "Programs"));
    } catch {}
    foreach (string root in roots) {
      foreach (string name in names) yield return Path.Combine(root, name);
    }
  }

  private static string ReadShortcutTarget(string lnkPath) {
    object shellLink = null;
    try {
      if (!File.Exists(lnkPath)) return null;
      Type type = Type.GetTypeFromCLSID(new Guid(SHELL_LINK_CLSID));
      if (type == null) return null;
      shellLink = Activator.CreateInstance(type);
      IShellLinkW link = shellLink as IShellLinkW;
      IPersistFile file = shellLink as IPersistFile;
      if (link == null || file == null) return null;
      file.Load(lnkPath, 0 /* STGM_READ */);
      StringBuilder buffer = new StringBuilder(520);
      link.GetPath(buffer, buffer.Capacity, IntPtr.Zero, 0);
      string target = buffer.ToString();
      return string.IsNullOrEmpty(target) ? null : target;
    } catch {
      return null;
    } finally {
      if (shellLink != null) { try { Marshal.ReleaseComObject(shellLink); } catch {} }
    }
  }

  internal const string PROTOCOL_SCHEME = "dsh-notify";

  /// <summary>
  /// Register the URI scheme that makes a toast click actually reach this helper.
  ///
  /// A click is NOT a shortcut launch. Verified empirically on this machine:
  ///   - foreground activation with no activator registered = silent no-op
  ///     (no process starts; the user sees "no reaction");
  ///   - a custom HKCU scheme is honoured ONLY if it already exists when the
  ///     toast is shown - a toast displayed before the scheme was registered
  ///     still fails with the shell's "no app can open this link" picker.
  /// So the scheme is (re)written on every toast, immediately BEFORE it is shown.
  ///
  /// The handler is launched by the shell as a direct result of the user's click,
  /// which is also what grants it the user-input foreground rights the DSH window
  /// needs in order to be raised.
  /// </summary>
  internal static void EnsureProtocol(string exePath) {
    try {
      using (RegistryKey root = Registry.CurrentUser.CreateSubKey(
          @"Software\Classes\" + PROTOCOL_SCHEME)) {
        root.SetValue("", "URL:" + PROTOCOL_SCHEME, RegistryValueKind.String);
        root.SetValue("URL Protocol", "", RegistryValueKind.String);
      }
      using (RegistryKey cmd = Registry.CurrentUser.CreateSubKey(
          @"Software\Classes\" + PROTOCOL_SCHEME + @"\shell\open\command")) {
        cmd.SetValue("", "\"" + exePath + "\" \"%1\"", RegistryValueKind.String);
      }
      Log.W("protocol registered: " + PROTOCOL_SCHEME + " -> " + exePath);
    } catch (Exception e) {
      Log.W("protocol registration failed: " + e.GetType().Name + ": " + e.Message);
    }
  }
}

/// <summary>
/// Toast-click activation.
///
/// Why the helper (not DSH) owns this: a toast click makes Windows launch the
/// AUMID shortcut's target as a direct result of user input, and explorer grants
/// THAT process the right to call SetForegroundWindow. The old design pointed the
/// shortcut at DSH Desktop.exe, so the right landed on a throwaway second
/// instance while the already-running primary (the process that owns the window)
/// had no foreground rights - Windows' foreground lock then swallowed the focus
/// and the user saw "no reaction". Here the clicked process is this helper, so it
/// holds the rights and focuses/restores the real DSH window itself.
///
/// The same log lines double as the click detector: if a click produces no
/// "start args=..." line, the toast never launched the helper at all.
/// </summary>
internal static class Activate {
  internal static void Run(string shellHint) {
    string shellExe = DshTarget.ResolveShellExe(shellHint);
    Log.W("ACTIVATE shellExe=" + (shellExe == null ? "<unresolved>" : shellExe));

    IntPtr hwnd = FindWindow(shellExe);
    if (hwnd == IntPtr.Zero && !string.IsNullOrEmpty(shellExe)) {
      Log.W("ACTIVATE no DSH window found; launching shell");
      string dir = null;
      try { dir = Path.GetDirectoryName(shellExe); } catch {}
      IntPtr rc = Win32.ShellExecute(IntPtr.Zero, "open", shellExe, null, dir, Win32.SW_SHOWNORMAL);
      Log.W("ACTIVATE ShellExecute rc=" + rc.ToInt64());
      for (int i = 0; i < 60; i++) {
        System.Threading.Thread.Sleep(250);
        hwnd = FindWindow(shellExe);
        if (hwnd != IntPtr.Zero) {
          Log.W("ACTIVATE window appeared after " + ((i + 1) * 250) + "ms");
          break;
        }
      }
    }

    if (hwnd == IntPtr.Zero) {
      Log.W("ACTIVATE FAILED: no DSH window to focus (unresolved=" + (shellExe == null) + ")");
      return;
    }
    Focus(hwnd);
  }

  /// <summary>Best top-level window belonging to the DSH shell, or IntPtr.Zero.</summary>
  private static IntPtr FindWindow(string shellExe) {
    List<IntPtr> handles = new List<IntPtr>();
    Win32.EnumWindows(delegate(IntPtr h, IntPtr p) { handles.Add(h); return true; }, IntPtr.Zero);

    string wantBase = null;
    try { if (!string.IsNullOrEmpty(shellExe)) wantBase = Path.GetFileName(shellExe); } catch {}

    Dictionary<uint, string> pathCache = new Dictionary<uint, string>();
    IntPtr best = IntPtr.Zero;
    int bestScore = -1;
    string bestInfo = "<none>";

    foreach (IntPtr h in handles) {
      uint pid;
      if (Win32.GetWindowThreadProcessId(h, out pid) == 0 || pid == 0) continue;

      string path;
      if (!pathCache.TryGetValue(pid, out path)) {
        path = ProcessPath(pid);
        pathCache[pid] = path;
      }

      string baseName = null;
      try { if (path != null) baseName = Path.GetFileName(path); } catch {}

      int score = -1;
      if (path != null && shellExe != null && string.Equals(path, shellExe, StringComparison.OrdinalIgnoreCase)) {
        score = 30;
      } else if (baseName != null && wantBase != null && string.Equals(baseName, wantBase, StringComparison.OrdinalIgnoreCase)) {
        score = 28;
      } else if (baseName != null && string.Equals(baseName, DshTarget.SHELL_EXE_NAME, StringComparison.OrdinalIgnoreCase)) {
        score = 26;
      } else if (baseName != null && baseName.IndexOf("DSH ", StringComparison.OrdinalIgnoreCase) >= 0 &&
                 baseName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) {
        score = 8;
      } else {
        // Last resort: an Electron main window whose title names the product.
        if (string.Equals(Win32.ClassName(h), "Chrome_WidgetWin_1", StringComparison.Ordinal) &&
            Win32.Title(h).IndexOf("DeepSeek Harness", StringComparison.OrdinalIgnoreCase) >= 0) {
          score = 6;
        }
      }
      if (score < 0) continue;

      string title = Win32.Title(h);
      bool visible = Win32.IsWindowVisible(h);
      if (title.Length > 0 && visible) score += 10;
      else if (visible) score += 4;
      if ((Win32.GetWindowLong(h, Win32.GWL_EXSTYLE) & Win32.WS_EX_TOOLWINDOW) != 0) score -= 6;
      if (Win32.GetWindow(h, Win32.GW_OWNER) != IntPtr.Zero) score -= 8;

      if (score > bestScore) {
        bestScore = score;
        best = h;
        bestInfo = "pid=" + pid + " score=" + score + " vis=" + visible +
                   " min=" + Win32.IsIconic(h) + " cls=" + Win32.ClassName(h) +
                   " title=[" + title + "] path=" + (path == null ? "<denied>" : path);
      }
    }

    Log.W("ACTIVATE scanned " + handles.Count + " windows; best " + bestInfo);
    return best;
  }

  private static string ProcessPath(uint pid) {
    IntPtr h = Win32.OpenProcess(Win32.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
    if (h == IntPtr.Zero) return null;
    try {
      int size = 1024;
      StringBuilder sb = new StringBuilder(size);
      if (Win32.QueryFullProcessImageName(h, 0, sb, ref size)) return sb.ToString();
      return null;
    } catch {
      return null;
    } finally {
      Win32.CloseHandle(h);
    }
  }

  private static void Focus(IntPtr h) {
    Log.W("ACTIVATE target hwnd=0x" + h.ToInt64().ToString("X") + " title=[" + Win32.Title(h) + "]" +
          " vis=" + Win32.IsWindowVisible(h) + " min=" + Win32.IsIconic(h));

    if (Win32.IsIconic(h)) Win32.ShowWindow(h, Win32.SW_RESTORE);
    else Win32.ShowWindow(h, Win32.SW_SHOW);
    Win32.BringWindowToTop(h);
    Win32.SetWindowPos(h, IntPtr.Zero, 0, 0, 0, 0,
      Win32.SWP_NOSIZE | Win32.SWP_NOMOVE | Win32.SWP_SHOWWINDOW);
    Win32.SetForegroundWindow(h);
    if (Win32.GetForegroundWindow() == h) { Log.W("ACTIVATE RESULT ok=direct"); return; }

    // Stage 2: share the input queue with the foreground thread and the target
    // thread, which is the classic way past the foreground lock.
    uint fgPid;
    IntPtr fg = Win32.GetForegroundWindow();
    uint fgThread = fg == IntPtr.Zero ? 0 : Win32.GetWindowThreadProcessId(fg, out fgPid);
    uint targetThread = Win32.GetWindowThreadProcessId(h, out fgPid);
    uint mine = Win32.GetCurrentThreadId();

    bool attachedTarget = false;
    bool attachedFg = false;
    if (targetThread != 0 && targetThread != mine) attachedTarget = Win32.AttachThreadInput(mine, targetThread, true);
    if (fgThread != 0 && fgThread != mine && fgThread != targetThread) attachedFg = Win32.AttachThreadInput(mine, fgThread, true);

    Win32.BringWindowToTop(h);
    Win32.SetForegroundWindow(h);
    Win32.SetFocus(h);

    if (attachedFg) Win32.AttachThreadInput(mine, fgThread, false);
    if (attachedTarget) Win32.AttachThreadInput(mine, targetThread, false);
    if (Win32.GetForegroundWindow() == h) { Log.W("ACTIVATE RESULT ok=attach"); return; }

    // Stage 3: alt-tab semantics (undocumented but present since Win95).
    Win32.SwitchToThisWindow(h, true);
    if (Win32.GetForegroundWindow() == h) { Log.W("ACTIVATE RESULT ok=switch"); return; }

    // Stage 4: at least make it visible in the taskbar.
    Win32.FLASHWINFO fi = new Win32.FLASHWINFO();
    fi.cbSize = (uint)Marshal.SizeOf(typeof(Win32.FLASHWINFO));
    fi.hwnd = h;
    fi.dwFlags = Win32.FLASHW_ALL | Win32.FLASHW_TIMERNOFG;
    fi.uCount = 0;
    fi.dwTimeout = 0;
    Win32.FlashWindowEx(ref fi);
    Log.W("ACTIVATE RESULT FAILED (flashed taskbar) fg=0x" + Win32.GetForegroundWindow().ToInt64().ToString("X"));
  }
}

internal static class Program {
  private const string APP_ID = "DeepSeekHarness.Notify";
  private const string APP_NAME = "DeepSeek Harness";
  private static readonly PROPERTYKEY PKEY_AUMID = new PROPERTYKEY {
    fmtid = new Guid("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3"), pid = 5
  };

  [STAThread]
  private static void Main(string[] args) {
    string exePath = System.Reflection.Assembly.GetExecutingAssembly().Location;
    string exeDir = null;
    try { exeDir = Path.GetDirectoryName(exePath); } catch {}
    Log.Init(exeDir);
    Log.W("start args=" + args.Length + " [" + string.Join(" ", args) + "]");

    // A toast click must never be mistaken for a new toast request: the shell
    // appends the toast's launch string to the shortcut's own arguments, so the
    // "activate" marker wins regardless of the argument count.
    //   >=2 args, no marker : toast request from the plugin (exe <title> <body>)
    //   marker or <2 args   : launched by clicking the toast -> bring DSH forward
    bool activation = args.Length < 2;
    for (int i = 0; i < args.Length; i++) {
      if (string.Equals(args[i], "activate", StringComparison.OrdinalIgnoreCase)) activation = true;
    }
    if (!activation) {
      ShowToast(exePath, exeDir, args[0], args[1]);
      return;
    }
    Activate.Run(null);
  }

  private static string EscapeXml(string s) {
    if (string.IsNullOrEmpty(s)) return "";
    return s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;")
            .Replace("\"", "&quot;").Replace("'", "&apos;");
  }

  /// <summary>
  /// AUMID identity in HKCU. Without it Windows cannot resolve this app's
  /// notification identity and shows an iconless toast. Guarded so a registry
  /// failure can never surface a crash dialog.
  /// </summary>
  private static void EnsureIdentity(string icoFile) {
    try {
      using (RegistryKey root = Registry.CurrentUser.CreateSubKey(
          @"Software\Classes\AppUserModelId\" + APP_ID)) {
        root.SetValue("DisplayName", APP_NAME, RegistryValueKind.String);
        root.SetValue("IconUri", icoFile, RegistryValueKind.String);
      }
    } catch (Exception e) {
      Log.W("AUMID registry write failed: " + e.GetType().Name + ": " + e.Message);
    }
    try {
      Native.SetCurrentProcessExplicitAppUserModelID(APP_ID);
    } catch {}
  }

  /// <summary>
  /// Create/refresh the AUMID Start Menu shortcut. The target is ALWAYS this
  /// helper: a toast click must launch the helper so that the helper - not a
  /// throwaway DSH second instance - receives the user-input foreground rights.
  /// </summary>
  private static void EnsureShortcut(string exePath, string exeDir) {
    try {
      string shortcutDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Microsoft", "Windows", "Start Menu", "Programs");
      string shortcutPath = Path.Combine(shortcutDir, APP_NAME + ".lnk");
      Log.W("shortcut path: " + shortcutPath);
      Directory.CreateDirectory(shortcutDir);

      Type slType = Type.GetTypeFromCLSID(new Guid("00021401-0000-0000-C000-000000000046"));
      if (slType == null) { Log.W("GetTypeFromCLSID returned null"); return; }

      object slObj = Activator.CreateInstance(slType);
      if (slObj == null) { Log.W("CreateInstance returned null"); return; }

      IShellLinkW sl = slObj as IShellLinkW;
      if (sl == null) { Log.W("cast to IShellLinkW failed"); return; }

      string iconFile = Path.Combine(exeDir, "notify.ico");
      sl.SetPath(exePath);
      sl.SetArguments("");
      sl.SetDescription(APP_NAME);
      sl.SetWorkingDirectory(exeDir);
      sl.SetIconLocation(iconFile, 0);
      Log.W("IShellLink fields set, target=" + exePath + " icon=" + iconFile);

      IPersistFile pf = slObj as IPersistFile;
      if (pf == null) { Log.W("cast to IPersistFile failed"); return; }
      pf.Save(shortcutPath, true);
      Log.W("IPersistFile.Save #1 OK, file exists=" + File.Exists(shortcutPath));

      IPropertyStore ps = slObj as IPropertyStore;
      if (ps == null) { Log.W("cast to IPropertyStore failed"); return; }
      PROPVARIANT v = new PROPVARIANT();
      v.SetString(APP_ID);
      PROPERTYKEY pkey = PKEY_AUMID;
      ps.SetValue(ref pkey, ref v);
      ps.Commit();
      v.Clear();
      Log.W("IPropertyStore SetValue AUMID OK");

      // The AppUserModelID only lands on disk when the link is saved AGAIN
      // after the property Commit; saving before it silently drops the
      // property (verified empirically: lnk read back vt=0). Without the AUMID
      // on the shortcut, Windows cannot resolve this app's notification
      // identity and shows an iconless toast.
      pf.Save(shortcutPath, true);
      Log.W("IPersistFile.Save #2 OK (AUMID persisted)");

      Marshal.ReleaseComObject(ps);
      Marshal.ReleaseComObject(pf);
      Marshal.ReleaseComObject(sl);
      Marshal.ReleaseComObject(slObj);
      Log.W("EnsureShortcut complete");
    } catch (Exception e) {
      Log.W("EXCEPTION: " + e.GetType().Name + ": " + e.Message);
      Log.W("stack: " + e.StackTrace);
    }
  }

  private static void ShowToast(string exePath, string exeDir, string title, string body) {
    string icoFile = Path.Combine(exeDir, "notify.ico");
    EnsureIdentity(icoFile);
    EnsureShortcut(exePath, exeDir);
    // Must happen BEFORE Show(): the notification platform only honours a URI
    // scheme that already exists when the toast is displayed.
    DshTarget.EnsureProtocol(exePath);

    // No <image> in the toast XML on purpose: appLogoOverride renders as a
    // large circle in the notification center. The whale icon instead comes
    // from the AUMID identity (Start Menu shortcut + IconUri registry), which
    // shows at the proper small size next to the title / app name. notify.ico
    // and whale-black-bg.png carry real 16..256px frames for crisp small icons.
    //
    // activationType='protocol' + a URI launch is the only channel that actually
    // survives a click for a non-packaged app: the shell hands the URI to the
    // registered handler (this helper) as a direct result of the user's click,
    // which is exactly what raising the DSH window needs. Plain foreground
    // activation registers nothing and the click silently does nothing.
    string xml =
      "<toast activationType='protocol' launch='" + DshTarget.PROTOCOL_SCHEME + ":activate'>" +
      "<visual><binding template='ToastGeneric'>" +
      "<text>" + EscapeXml(title) + "</text>" +
      "<text>" + EscapeXml(body) + "</text>" +
      "</binding></visual></toast>";

    XmlDocument doc = new XmlDocument();
    doc.LoadXml(xml);
    ToastNotification toast = new ToastNotification(doc);
    ToastNotificationManager.CreateToastNotifier(APP_ID).Show(toast);
    Log.W("toast shown");
  }
}