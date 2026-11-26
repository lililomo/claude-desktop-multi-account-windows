// Claude Desktop multi-account launcher for Windows (unofficial, not affiliated with Anthropic).
//
// Starts another Claude Desktop instance with its own profile, so a second (third, ...) account can run
// side by side with the normal app:
//   - Own profile folder (Electron --user-data-dir): separate sign-in, sessions and settings.
//   - Own taskbar group: every window of the profile gets its own AppUserModelID, so it does not merge
//     with the main Claude button, plus a recolored copy of the installed Claude icon (made locally, the
//     Claude logo is not shipped with this program).
//   - Google sign-in that lands in the right window: Windows always hands claude:// links to the main
//     instance. When this profile's log shows that it opened the browser for sign-in, the launcher watches
//     for the short-lived process carrying the claude://login/ link and forwards the link to this profile.
//     The main instance still receives the original link and ignores it (it did not start that sign-in).
//   - Start menu + desktop shortcut on first run (Windows needs it to show the taskbar button name).
//
// The profile name comes from the exe name ("-" and "_" read as spaces): "Claude-Account-2.exe" ->
// "Account 2", "Claude-Work.exe" -> "Work". Copy/rename the exe to get more accounts.
// Data: %USERPROFILE%\Claude-Accounts\<profile>\ (not AppData: the Microsoft Store build of Claude
// redirects AppData writes into its private package folder).
//
// After starting Claude it keeps running quietly while that profile is open, then exits ~15 s after it closes.
//
// Arguments:
//   (none)                  start the profile; other arguments are passed on to Claude
//   --install-shortcuts     (re)create the start menu and desktop shortcuts for this exe
//
// Build: build.ps1 (C# 5, csc of the .NET Framework 4 that ships with Windows 10/11).
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

[assembly: AssemblyTitle("Claude multi-account launcher")]
[assembly: AssemblyDescription("Runs another Claude Desktop account side by side on Windows (unofficial)")]
[assembly: AssemblyProduct("claude-desktop-multi-account-windows")]
[assembly: AssemblyCopyright("MIT License")]
[assembly: AssemblyVersion("1.0.0.0")]
[assembly: AssemblyFileVersion("1.0.0.0")]

static class Program
{
    const string PackageFamily = "Claude_pzs8sxrjxfjjc";
    const string LoginLinkPrefix = "claude://login/";
    const string LoginStartedMarker = "Using system browser for: /login/";
    const int LoginWindowMs = 10 * 60 * 1000;
    static readonly int[] Palette = { 214 /*blue*/, 140 /*green*/, 275 /*purple*/, 180 /*teal*/, 330 /*pink*/, 45 /*gold*/ };

    static readonly string ExePath = Process.GetCurrentProcess().MainModule.FileName;
    static readonly string ExeDir = Path.GetDirectoryName(ExePath);
    static readonly string LocalAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
    static readonly string Profile = ProfileFromExeName(ExePath);
    static readonly string DisplayName = "Claude (" + Profile + ")";
    static readonly string AppId = "ClaudeMultiAccount." + AppIdPart(Profile);
    static readonly string Root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Claude-Accounts", SafeFolderName(Profile));
    static readonly string DataDir = Path.Combine(Root, "data");
    static readonly string LogPath = Path.Combine(DataDir, @"logs\main.log");
    static readonly int Hue = HueFor(Profile);
    static readonly string IconFile = Path.Combine(Root, "icon-" + Hue + ".ico");

    static string Launcher;
    static string iconPath;   // icon used for shortcuts and the taskbar relaunch entry
    static int iconIndex;

    [STAThread]
    static int Main(string[] args)
    {
        try { SetProcessDPIAware(); } catch { }
        bool onlyShortcuts = args.Length > 0 && args[0] == "--install-shortcuts";

        Launcher = FindLauncher();
        if (Launcher == null && !onlyShortcuts)
        {
            MessageBox(IntPtr.Zero,
                "Claude Desktop is not installed on this computer.\n\n" +
                "Install it from https://claude.ai/download first, then start " + DisplayName + " again.",
                DisplayName, 0x30 /*MB_ICONWARNING*/);
            return 1;
        }

        try { Directory.CreateDirectory(Root); } catch { }
        if (EnsureIcon()) { iconPath = IconFile; iconIndex = 0; }
        else { iconPath = ExePath; iconIndex = 0; }

        try { InstallShortcuts(onlyShortcuts); } catch { }
        if (onlyShortcuts) return 0;

        LaunchProfile(args);

        bool firstWatcher;
        using (var mutex = new Mutex(true, @"Local\ClaudeMultiAccount-" + Fnv(DataDir.ToLowerInvariant()).ToString("x8"), out firstWatcher))
        {
            if (!firstWatcher) return 0;   // a watcher for this profile is already running
            Watch();
        }
        return 0;
    }

    // ---------------------------------------------------------------- names and places

    static string ProfileFromExeName(string exe)
    {
        string name = Path.GetFileNameWithoutExtension(exe).Trim();
        if (name.StartsWith("Claude", StringComparison.OrdinalIgnoreCase)) name = name.Substring(6);
        name = name.Replace('-', ' ').Replace('_', ' ');
        while (name.Contains("  ")) name = name.Replace("  ", " ");
        name = name.Trim(' ', '.', '(', ')', '[', ']');
        return name.Length == 0 ? "Account 2" : name;
    }

    static string AppIdPart(string profile)
    {
        var sb = new StringBuilder();
        foreach (char c in profile) if (c < 128 && char.IsLetterOrDigit(c)) sb.Append(c);
        if (sb.Length == 0 || sb.Length > 60) return Fnv(profile.ToLowerInvariant()).ToString("x8");
        return sb.ToString();
    }

    static string SafeFolderName(string profile)
    {
        var sb = new StringBuilder();
        foreach (char c in profile) sb.Append(Array.IndexOf(Path.GetInvalidFileNameChars(), c) >= 0 ? '_' : c);
        return sb.ToString();
    }

    // "Account 2" -> blue, "Account 3" -> green, ...; names without a number get a color from a hash.
    static int HueFor(string profile)
    {
        int end = profile.Length;
        while (end > 0 && !char.IsDigit(profile[end - 1])) end--;
        int start = end;
        while (start > 0 && char.IsDigit(profile[start - 1])) start--;
        int n;
        if (end > start && end - start < 6 && int.TryParse(profile.Substring(start, end - start), out n))
            return Palette[((n - 2) % Palette.Length + Palette.Length) % Palette.Length];
        return Palette[Fnv(profile.ToLowerInvariant()) % (uint)Palette.Length];
    }

    static uint Fnv(string s)
    {
        uint h = 2166136261;
        foreach (char c in s) { h ^= c; h *= 16777619; }
        return h;
    }

    // The program that starts Claude Desktop (with arguments passed through).
    static string FindLauncher()
    {
        string[] candidates =
        {
            Path.Combine(LocalAppData, @"Microsoft\WindowsApps\claude-desktop.exe"),   // Microsoft Store / MSIX build
            Path.Combine(LocalAppData, @"AnthropicClaude\claude.exe"),                 // older installer
        };
        foreach (string c in candidates) if (File.Exists(c)) return c;
        return null;
    }

    // The real Claude.exe, only used as the source of the icon.
    static string FindClaudeExe()
    {
        foreach (string pkg in PackagePaths(PackageFamily))
        {
            string p = Path.Combine(pkg, @"app\Claude.exe");
            if (File.Exists(p)) return p;
        }
        string old = Path.Combine(LocalAppData, @"AnthropicClaude\claude.exe");
        if (File.Exists(old)) return old;
        foreach (Process pr in Process.GetProcessesByName("claude"))
        {
            try { string p = ProcessPath(pr.Id); if (p != null && File.Exists(p)) return p; }
            finally { pr.Dispose(); }
        }
        return null;
    }

    static List<string> PackagePaths(string family)
    {
        var result = new List<string>();
        try
        {
            uint count = 0, bufLen = 0;
            if (GetPackagesByPackageFamily(family, ref count, null, ref bufLen, IntPtr.Zero) != 122 /*ERROR_INSUFFICIENT_BUFFER*/ || count == 0)
                return result;
            var names = new IntPtr[count];
            IntPtr buf = Marshal.AllocHGlobal((int)bufLen * 2);
            try
            {
                if (GetPackagesByPackageFamily(family, ref count, names, ref bufLen, buf) != 0) return result;
                for (int i = 0; i < count; i++)
                {
                    string fullName = Marshal.PtrToStringUni(names[i]);
                    uint len = 0;
                    GetPackagePathByFullName(fullName, ref len, null);
                    if (len == 0) continue;
                    var sb = new StringBuilder((int)len);
                    if (GetPackagePathByFullName(fullName, ref len, sb) == 0) result.Add(sb.ToString());
                }
            }
            finally { Marshal.FreeHGlobal(buf); }
        }
        catch { }
        result.Reverse();   // newest version first
        return result;
    }

    static string ProcessPath(int pid)
    {
        IntPtr h = OpenProcess(0x1000 /*PROCESS_QUERY_LIMITED_INFORMATION*/, false, pid);
        if (h == IntPtr.Zero) return null;
        try
        {
            var sb = new StringBuilder(1024);
            int size = sb.Capacity;
            return QueryFullProcessImageName(h, 0, sb, ref size) ? sb.ToString() : null;
        }
        finally { CloseHandle(h); }
    }

    // ---------------------------------------------------------------- icon (recolored from the installed app)

    static bool EnsureIcon()
    {
        if (File.Exists(IconFile)) return true;
        try
        {
            string source = FindClaudeExe();
            if (source == null) return false;
            var frames = new List<KeyValuePair<int, byte[]>>();
            foreach (int size in new[] { 16, 20, 24, 32, 40, 48, 64, 256 })
            {
                var handles = new IntPtr[1];
                var ids = new uint[1];
                if (PrivateExtractIcons(source, 0, size, size, handles, ids, 1, 0) != 1 || handles[0] == IntPtr.Zero) continue;
                try
                {
                    using (Bitmap bmp = BitmapFromIcon(handles[0], size))
                    {
                        if (bmp == null) continue;
                        Recolor(bmp, Hue);
                        using (var ms = new MemoryStream())
                        {
                            bmp.Save(ms, ImageFormat.Png);
                            frames.Add(new KeyValuePair<int, byte[]>(size, ms.ToArray()));
                        }
                    }
                }
                finally { DestroyIcon(handles[0]); }
            }
            if (frames.Count == 0) return false;
            WriteIco(IconFile, frames);
            return true;
        }
        catch { return false; }
    }

    static Bitmap BitmapFromIcon(IntPtr hicon, int size)
    {
        ICONINFO info;
        if (!GetIconInfo(hicon, out info)) return null;
        IntPtr dc = GetDC(IntPtr.Zero);
        try
        {
            if (info.hbmColor == IntPtr.Zero) return null;
            var px = new int[size * size];
            var bih = NewHeader(size);
            if (GetDIBits(dc, info.hbmColor, 0, (uint)size, px, ref bih, 0) == 0) return null;

            bool hasAlpha = false;
            foreach (int p in px) if ((p & unchecked((int)0xFF000000)) != 0) { hasAlpha = true; break; }
            if (!hasAlpha && info.hbmMask != IntPtr.Zero)
            {
                var mask = new int[size * size];
                var mih = NewHeader(size);
                GetDIBits(dc, info.hbmMask, 0, (uint)size, mask, ref mih, 0);
                for (int i = 0; i < px.Length; i++)
                    px[i] = (mask[i] & 0xFFFFFF) != 0 ? 0 : (px[i] | unchecked((int)0xFF000000));
            }

            var bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb);
            BitmapData data = bmp.LockBits(new Rectangle(0, 0, size, size), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
            Marshal.Copy(px, 0, data.Scan0, px.Length);
            bmp.UnlockBits(data);
            return bmp;
        }
        finally
        {
            ReleaseDC(IntPtr.Zero, dc);
            if (info.hbmColor != IntPtr.Zero) DeleteObject(info.hbmColor);
            if (info.hbmMask != IntPtr.Zero) DeleteObject(info.hbmMask);
        }
    }

    static BITMAPINFOHEADER NewHeader(int size)
    {
        return new BITMAPINFOHEADER { biSize = 40, biWidth = size, biHeight = -size /*top-down*/, biPlanes = 1, biBitCount = 32 };
    }

    // Gives every clearly colored pixel the new hue; whites, greys and transparency stay as they are.
    static void Recolor(Bitmap bmp, int hue)
    {
        var rect = new Rectangle(0, 0, bmp.Width, bmp.Height);
        BitmapData data = bmp.LockBits(rect, ImageLockMode.ReadWrite, PixelFormat.Format32bppArgb);
        var px = new int[bmp.Width * bmp.Height];
        Marshal.Copy(data.Scan0, px, 0, px.Length);
        double h = hue / 360.0;
        for (int i = 0; i < px.Length; i++)
        {
            int a = (px[i] >> 24) & 255;
            if (a == 0) continue;
            double r = ((px[i] >> 16) & 255) / 255.0, g = ((px[i] >> 8) & 255) / 255.0, b = (px[i] & 255) / 255.0;
            double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b));
            double l = (max + min) / 2, d = max - min;
            if (d == 0) continue;
            double s = l > 0.5 ? d / (2 - max - min) : d / (max + min);
            if (s < 0.12) continue;
            double q = l < 0.5 ? l * (1 + s) : l + s - l * s, p = 2 * l - q;
            int nr = (int)Math.Round(HueToRgb(p, q, h + 1 / 3.0) * 255);
            int ng = (int)Math.Round(HueToRgb(p, q, h) * 255);
            int nb = (int)Math.Round(HueToRgb(p, q, h - 1 / 3.0) * 255);
            px[i] = (a << 24) | (nr << 16) | (ng << 8) | nb;
        }
        Marshal.Copy(px, 0, data.Scan0, px.Length);
        bmp.UnlockBits(data);
    }

    static double HueToRgb(double p, double q, double t)
    {
        if (t < 0) t += 1;
        if (t > 1) t -= 1;
        if (t < 1 / 6.0) return p + (q - p) * 6 * t;
        if (t < 1 / 2.0) return q;
        if (t < 2 / 3.0) return p + (q - p) * (2 / 3.0 - t) * 6;
        return p;
    }

    static void WriteIco(string path, List<KeyValuePair<int, byte[]>> frames)
    {
        string tmp = path + ".tmp";
        using (var w = new BinaryWriter(new FileStream(tmp, FileMode.Create)))
        {
            w.Write((short)0); w.Write((short)1); w.Write((short)frames.Count);
            int offset = 6 + 16 * frames.Count;
            foreach (var f in frames)
            {
                byte dim = (byte)(f.Key >= 256 ? 0 : f.Key);
                w.Write(dim); w.Write(dim); w.Write((byte)0); w.Write((byte)0);
                w.Write((short)1); w.Write((short)32);
                w.Write(f.Value.Length); w.Write(offset);
                offset += f.Value.Length;
            }
            foreach (var f in frames) w.Write(f.Value);
        }
        if (File.Exists(path)) File.Delete(path);
        File.Move(tmp, path);
    }

    // ---------------------------------------------------------------- start

    static void LaunchProfile(string[] extra)
    {
        var sb = new StringBuilder("--user-data-dir=\"" + DataDir + "\"");
        foreach (string a in extra) sb.Append(' ').Append(Quote(a));
        StartLauncher(sb.ToString());
    }

    static void StartLauncher(string arguments)
    {
        var psi = new ProcessStartInfo(Launcher, arguments);
        psi.UseShellExecute = true;
        psi.WorkingDirectory = ExeDir;
        Process.Start(psi);
    }

    static string Quote(string a)
    {
        if (a.Length > 0 && a.IndexOfAny(new[] { ' ', '\t', '"' }) < 0) return a;
        return "\"" + a.Replace("\"", "\\\"") + "\"";
    }

    // ---------------------------------------------------------------- window watcher

    static readonly HashSet<int> profilePids = new HashSet<int>();
    static readonly HashSet<int> otherPids = new HashSet<int>();
    static readonly Dictionary<IntPtr, long> tagged = new Dictionary<IntPtr, long>();
    static readonly Stopwatch clock = Stopwatch.StartNew();
    static IntPtr bigIcon, smallIcon;
    static EnumWindowsProc enumProc;   // kept alive for the GC

    static void Watch()
    {
        bigIcon = LoadWindowIcon(GetSystemMetrics(11), GetSystemMetrics(12));
        smallIcon = LoadWindowIcon(GetSystemMetrics(49), GetSystemMetrics(50));
        enumProc = OnWindow;

        var linkThread = new Thread(LinkLoop);
        linkThread.IsBackground = true;
        linkThread.Start();

        long lastSeen = -1;
        while (true)
        {
            RefreshPids();
            long now = clock.ElapsedMilliseconds;
            if (profilePids.Count > 0) lastSeen = now;
            else if (lastSeen < 0 ? now > 120000 : now - lastSeen > 15000) break;

            if (profilePids.Count > 0) EnumWindows(enumProc, IntPtr.Zero);

            var dead = new List<IntPtr>();
            foreach (IntPtr h in tagged.Keys) if (!IsWindow(h)) dead.Add(h);
            foreach (IntPtr h in dead) tagged.Remove(h);

            CheckLogForSignIn();
            Thread.Sleep(lastSeen < 0 || now - lastSeen < 1000 ? 250 : 500);
        }
    }

    static IntPtr LoadWindowIcon(int cx, int cy)
    {
        if (iconPath == IconFile)
        {
            IntPtr h = LoadImage(IntPtr.Zero, IconFile, 1 /*IMAGE_ICON*/, cx, cy, 0x10 /*LR_LOADFROMFILE*/);
            if (h != IntPtr.Zero) return h;
        }
        var icons = new IntPtr[1];
        var ids = new uint[1];
        try { if (PrivateExtractIcons(ExePath, 0, cx, cy, icons, ids, 1, 0) == 1) return icons[0]; } catch { }
        return IntPtr.Zero;
    }

    static void RefreshPids()
    {
        var alive = new HashSet<int>();
        foreach (Process p in Process.GetProcessesByName("claude")) { alive.Add(p.Id); p.Dispose(); }
        profilePids.IntersectWith(alive);
        otherPids.IntersectWith(alive);
        foreach (int id in alive)
        {
            if (profilePids.Contains(id) || otherPids.Contains(id)) continue;
            string cl = GetCommandLine(id);
            if (cl == null) continue;
            bool mine = cl.IndexOf(DataDir, StringComparison.OrdinalIgnoreCase) >= 0
                     && cl.IndexOf("--type=", StringComparison.Ordinal) < 0;
            (mine ? profilePids : otherPids).Add(id);
        }
    }

    static bool OnWindow(IntPtr h, IntPtr lParam)
    {
        uint pid;
        GetWindowThreadProcessId(h, out pid);
        if (!profilePids.Contains((int)pid)) return true;

        long now = clock.ElapsedMilliseconds;
        long last;
        bool known = tagged.TryGetValue(h, out last);
        if (known && now - last < 2000) return true;   // re-check every 2 s
        tagged[h] = now;

        if (!known || GetWindowAppId(h) != AppId) SetWindowAppProps(h);

        if (bigIcon != IntPtr.Zero && IsWindowVisible(h))
        {
            IntPtr cur;
            SendMessageTimeout(h, 0x7F /*WM_GETICON*/, (IntPtr)1 /*ICON_BIG*/, IntPtr.Zero, 2 /*SMTO_ABORTIFHUNG*/, 200, out cur);
            if (cur != bigIcon)
            {
                IntPtr r;
                SendMessageTimeout(h, 0x80 /*WM_SETICON*/, (IntPtr)1, bigIcon, 2, 200, out r);
                SendMessageTimeout(h, 0x80, IntPtr.Zero /*ICON_SMALL*/, smallIcon, 2, 200, out r);
            }
        }
        return true;
    }

    // ---------------------------------------------------------------- Google sign-in forwarding

    // Reads what was added to this profile's log. When it shows that the profile opened the browser for
    // sign-in, link catching is switched on for 10 minutes (no fast polling the rest of the time).
    static long logPos = -1;
    static string logCarry = "";
    static long armedUntil;

    static void CheckLogForSignIn()
    {
        try
        {
            var fi = new FileInfo(LogPath);
            if (!fi.Exists) { logPos = 0; return; }
            long len = fi.Length;
            if (logPos < 0) { logPos = len; return; }          // ignore what was logged before we started
            if (len < logPos) { logPos = 0; logCarry = ""; }   // log was rotated
            if (len == logPos) return;

            using (var fs = new FileStream(LogPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            {
                long start = Math.Max(logPos, len - (1 << 20));
                fs.Seek(start, SeekOrigin.Begin);
                var buf = new byte[len - start];
                int n = 0;
                while (n < buf.Length) { int r = fs.Read(buf, n, buf.Length - n); if (r <= 0) break; n += r; }
                logPos = start + n;
                string text = logCarry + Encoding.UTF8.GetString(buf, 0, n);
                if (text.IndexOf(LoginStartedMarker, StringComparison.Ordinal) >= 0)
                {
                    Interlocked.Exchange(ref armedUntil, clock.ElapsedMilliseconds + LoginWindowMs);
                    logCarry = "";
                }
                else logCarry = text.Length > 200 ? text.Substring(text.Length - 200) : text;
            }
        }
        catch { }
    }

    // The process that carries the claude:// link lives for only ~0.2 s, so while armed we look every 40 ms.
    static void LinkLoop()
    {
        HashSet<int> seen = null;
        string lastLink = null;
        while (true)
        {
            if (clock.ElapsedMilliseconds >= Interlocked.Read(ref armedUntil))
            {
                seen = null;
                Thread.Sleep(250);
                continue;
            }

            HashSet<int> alive = ClaudeProcessIds();
            if (seen == null) { seen = alive; Thread.Sleep(40); continue; }   // processes already running carry no link
            seen.IntersectWith(alive);
            foreach (int id in alive)
            {
                if (seen.Contains(id)) continue;
                string cl = GetCommandLine(id);
                if (cl == null) continue;   // try again on the next round while the process exists
                seen.Add(id);
                string link = ExtractSignInLink(cl);
                if (link == null || link == lastLink) continue;
                lastLink = link;
                try { StartLauncher("--user-data-dir=\"" + DataDir + "\" -- \"" + link + "\""); } catch { }
            }
            Thread.Sleep(40);
        }
    }

    static HashSet<int> ClaudeProcessIds()
    {
        var ids = new HashSet<int>();
        foreach (Process p in Process.GetProcesses())
        {
            string n = p.ProcessName;
            if (string.Equals(n, "claude", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(n, "claude-desktop", StringComparison.OrdinalIgnoreCase)) ids.Add(p.Id);
            p.Dispose();
        }
        return ids;
    }

    static string ExtractSignInLink(string commandLine)
    {
        int i = commandLine.IndexOf(LoginLinkPrefix, StringComparison.OrdinalIgnoreCase);
        if (i < 0) return null;
        // Links already forwarded to a profile (by this or another launcher) carry --user-data-dir.
        if (commandLine.IndexOf("--user-data-dir", StringComparison.OrdinalIgnoreCase) >= 0) return null;
        int end = i;
        while (end < commandLine.Length && commandLine[end] != '"' && !char.IsWhiteSpace(commandLine[end])) end++;
        return commandLine.Substring(i, end - i);
    }

    // ---------------------------------------------------------------- AppUserModelID

    static readonly Guid FmtidAppUserModel = new Guid("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3");
    const uint PidRelaunchCommand = 2, PidRelaunchIcon = 3, PidRelaunchName = 4, PidId = 5;

    static void SetWindowAppProps(IntPtr h)
    {
        IPropertyStore ps = null;
        try
        {
            Guid iid = typeof(IPropertyStore).GUID;
            if (SHGetPropertyStoreForWindow(h, ref iid, out ps) != 0 || ps == null) return;
            SetString(ps, PidId, AppId);
            SetString(ps, PidRelaunchCommand, "\"" + ExePath + "\"");
            SetString(ps, PidRelaunchName, DisplayName);
            SetString(ps, PidRelaunchIcon, iconPath + "," + iconIndex);
            ps.Commit();
        }
        catch { }
        finally { if (ps != null) Marshal.ReleaseComObject(ps); }
    }

    static string GetWindowAppId(IntPtr h)
    {
        IPropertyStore ps = null;
        try
        {
            Guid iid = typeof(IPropertyStore).GUID;
            if (SHGetPropertyStoreForWindow(h, ref iid, out ps) != 0 || ps == null) return null;
            return GetString(ps, PidId);
        }
        catch { return null; }
        finally { if (ps != null) Marshal.ReleaseComObject(ps); }
    }

    static void SetString(IPropertyStore ps, uint pid, string value)
    {
        var key = new PROPERTYKEY { fmtid = FmtidAppUserModel, pid = pid };
        var pv = new PROPVARIANT { vt = 31 /*VT_LPWSTR*/, p = Marshal.StringToCoTaskMemUni(value) };
        try { ps.SetValue(ref key, ref pv); }
        finally { PropVariantClear(ref pv); }
    }

    static string GetString(IPropertyStore ps, uint pid)
    {
        var key = new PROPERTYKEY { fmtid = FmtidAppUserModel, pid = pid };
        PROPVARIANT pv;
        if (ps.GetValue(ref key, out pv) != 0) return null;
        try { return pv.vt == 31 ? Marshal.PtrToStringUni(pv.p) : null; }
        finally { PropVariantClear(ref pv); }
    }

    // ---------------------------------------------------------------- shortcuts

    // force=false: create the start menu shortcut if it is missing (and the desktop one, on first run only),
    // and repair shortcuts whose exe or icon no longer exists. Healthy shortcuts are left alone.
    static void InstallShortcuts(bool force)
    {
        string fileName = SafeFolderName(DisplayName) + ".lnk";
        string startMenu = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), fileName);
        string desktop = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), fileName);
        bool firstRun = !File.Exists(startMenu);
        bool changed = false;

        foreach (string lnk in new[] { startMenu, desktop })
        {
            bool make;
            if (force) make = true;
            else if (!File.Exists(lnk)) make = lnk == startMenu || firstRun;
            else make = IsBroken(lnk);
            if (!make) continue;
            WriteShortcut(lnk);
            changed = true;
        }
        if (changed) SHChangeNotify(0x08000000 /*SHCNE_ASSOCCHANGED*/, 0, IntPtr.Zero, IntPtr.Zero);
    }

    static void WriteShortcut(string lnk)
    {
        var link = (IShellLinkW)new ShellLink();
        try
        {
            link.SetPath(ExePath);
            link.SetWorkingDirectory(ExeDir);
            link.SetIconLocation(iconPath, iconIndex);
            link.SetDescription("Claude Desktop with a separate account (" + Profile + ")");
            var ps = (IPropertyStore)link;
            SetString(ps, PidId, AppId);
            ps.Commit();
            ((IPersistFile)link).Save(lnk, true);
        }
        finally { Marshal.ReleaseComObject(link); }
    }

    static bool IsBroken(string lnk)
    {
        object link = null;
        try
        {
            link = new ShellLink();
            ((IPersistFile)link).Load(lnk, 0 /*STGM_READ*/);
            var target = new StringBuilder(1024);
            ((IShellLinkW)link).GetPath(target, target.Capacity, IntPtr.Zero, 0);
            var icon = new StringBuilder(1024);
            int index;
            ((IShellLinkW)link).GetIconLocation(icon, icon.Capacity, out index);
            if (target.Length > 0 && !File.Exists(target.ToString())) return true;
            if (icon.Length > 0 && !File.Exists(icon.ToString())) return true;
            return false;
        }
        catch { return false; }
        finally { if (link != null) Marshal.ReleaseComObject(link); }
    }

    // ---------------------------------------------------------------- other processes' command lines

    static string GetCommandLine(int pid)
    {
        IntPtr h = OpenProcess(0x1000 /*PROCESS_QUERY_LIMITED_INFORMATION*/, false, pid);
        if (h == IntPtr.Zero) return null;
        try
        {
            int len;
            NtQueryInformationProcess(h, 60 /*ProcessCommandLineInformation*/, IntPtr.Zero, 0, out len);
            if (len <= 0) return null;
            IntPtr buf = Marshal.AllocHGlobal(len);
            try
            {
                if (NtQueryInformationProcess(h, 60, buf, len, out len) != 0) return null;
                int bytes = (ushort)Marshal.ReadInt16(buf);
                return Marshal.PtrToStringUni(Marshal.ReadIntPtr(buf, IntPtr.Size), bytes / 2);
            }
            finally { Marshal.FreeHGlobal(buf); }
        }
        finally { CloseHandle(h); }
    }

    // ---------------------------------------------------------------- interop

    delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")] static extern bool SetProcessDPIAware();
    [DllImport("user32.dll")] static extern bool EnumWindows(EnumWindowsProc cb, IntPtr lParam);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] static extern bool IsWindow(IntPtr h);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] static extern int GetSystemMetrics(int i);
    [DllImport("user32.dll")] static extern IntPtr GetDC(IntPtr h);
    [DllImport("user32.dll")] static extern int ReleaseDC(IntPtr h, IntPtr dc);
    [DllImport("user32.dll")] static extern bool GetIconInfo(IntPtr icon, out ICONINFO info);
    [DllImport("user32.dll")] static extern bool DestroyIcon(IntPtr icon);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern int MessageBox(IntPtr h, string text, string caption, uint type);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern IntPtr LoadImage(IntPtr hinst, string name, uint type, int cx, int cy, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern uint PrivateExtractIcons(string file, int index, int cx, int cy, [Out] IntPtr[] icons, [Out] uint[] ids, uint count, uint flags);
    [DllImport("user32.dll")]
    static extern IntPtr SendMessageTimeout(IntPtr h, uint msg, IntPtr w, IntPtr l, uint flags, uint timeout, out IntPtr result);
    [DllImport("gdi32.dll")]
    static extern int GetDIBits(IntPtr dc, IntPtr bmp, uint start, uint lines, [Out] int[] bits, ref BITMAPINFOHEADER bi, uint usage);
    [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr obj);
    [DllImport("shell32.dll")]
    static extern int SHGetPropertyStoreForWindow(IntPtr h, ref Guid iid, [MarshalAs(UnmanagedType.Interface)] out IPropertyStore ps);
    [DllImport("shell32.dll")] static extern void SHChangeNotify(int e, uint f, IntPtr a, IntPtr b);
    [DllImport("ole32.dll")] static extern int PropVariantClear(ref PROPVARIANT pv);
    [DllImport("kernel32.dll")] static extern IntPtr OpenProcess(int access, bool inherit, int pid);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    static extern bool QueryFullProcessImageName(IntPtr h, int flags, StringBuilder name, ref int size);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    static extern int GetPackagesByPackageFamily(string family, ref uint count, [Out] IntPtr[] fullNames, ref uint bufferLength, IntPtr buffer);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    static extern int GetPackagePathByFullName(string fullName, ref uint pathLength, StringBuilder path);
    [DllImport("ntdll.dll")] static extern int NtQueryInformationProcess(IntPtr h, int cls, IntPtr buf, int len, out int ret);

    [StructLayout(LayoutKind.Sequential)]
    struct ICONINFO { public bool fIcon; public int xHotspot; public int yHotspot; public IntPtr hbmMask; public IntPtr hbmColor; }

    [StructLayout(LayoutKind.Sequential)]
    struct BITMAPINFOHEADER
    {
        public int biSize; public int biWidth; public int biHeight; public short biPlanes; public short biBitCount;
        public int biCompression; public int biSizeImage; public int biXPelsPerMeter; public int biYPelsPerMeter;
        public int biClrUsed; public int biClrImportant;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    struct PROPERTYKEY { public Guid fmtid; public uint pid; }

    [StructLayout(LayoutKind.Explicit, Size = 24)]
    struct PROPVARIANT { [FieldOffset(0)] public ushort vt; [FieldOffset(8)] public IntPtr p; }

    [ComImport, Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IPropertyStore
    {
        [PreserveSig] int GetCount(out uint count);
        [PreserveSig] int GetAt(uint index, out PROPERTYKEY key);
        [PreserveSig] int GetValue(ref PROPERTYKEY key, out PROPVARIANT pv);
        [PreserveSig] int SetValue(ref PROPERTYKEY key, ref PROPVARIANT pv);
        [PreserveSig] int Commit();
    }

    [ComImport, Guid("00021401-0000-0000-C000-000000000046")]
    class ShellLink { }

    [ComImport, Guid("000214F9-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IShellLinkW
    {
        void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder file, int cch, IntPtr fd, uint flags);
        void GetIDList(out IntPtr pidl);
        void SetIDList(IntPtr pidl);
        void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder name, int cch);
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string name);
        void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder dir, int cch);
        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string dir);
        void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder args, int cch);
        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string args);
        void GetHotkey(out short hotkey);
        void SetHotkey(short hotkey);
        void GetShowCmd(out int cmd);
        void SetShowCmd(int cmd);
        void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder path, int cch, out int index);
        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string path, int index);
        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string rel, uint reserved);
        void Resolve(IntPtr hwnd, uint flags);
        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string file);
    }

    [ComImport, Guid("0000010b-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IPersistFile
    {
        void GetClassID(out Guid clsid);
        [PreserveSig] int IsDirty();
        void Load([MarshalAs(UnmanagedType.LPWStr)] string file, uint mode);
        void Save([MarshalAs(UnmanagedType.LPWStr)] string file, bool remember);
        void SaveCompleted([MarshalAs(UnmanagedType.LPWStr)] string file);
        void GetCurFile([MarshalAs(UnmanagedType.LPWStr)] out string file);
    }
}
