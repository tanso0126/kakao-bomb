// 카톡 자폭 메시지 (Windows)
// 채팅창이 앞에 오면 창 옆에 트레이가 자동으로 뜸. 💣 자폭(또는 Ctrl+Alt+D)으로 장전/해제 토글
// → 해제할 때까지 그 채팅창에서 보내는 내 메시지마다 설정한 시간(기본 0.5초) 뒤 "모두에게서 삭제".
// 빌드: powershell -ExecutionPolicy Bypass -File build.ps1
// 실행: kakao-bomb.exe          (--debug: 삭제 과정을 %LOCALAPPDATA%\kakao-bomb\debug.log 에 기록)
//       kakao-bomb.exe --dump   (삭제 없이 카톡 창 구조만 dump.txt 로 저장)
//
// macOS 판과 다른 점: Windows 카톡은 메시지 목록도 우클릭 메뉴도 커스텀 컨트롤(EVA)이라 UI Automation에 내용이 안 나옴
//  - 전송 감지는 입력창(RichEdit)이 Enter/클릭 직후 비워지는 것으로 판단
//  - 말풍선/메뉴 줄 위치는 창 내용을 받아와(PrintWindow) 픽셀로 찾음
//  - 조작은 카톡 창에 메시지를 직접 보냄 (말풍선 우클릭 → 메뉴에서 ↑/→/Enter 키).
//    실제 커서/키보드는 안 건드리고, 채팅창이 가려져 있어도 됨

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Media;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Automation;
using System.Windows.Forms;
using Microsoft.Win32;

static class Config
{
    public const double DefaultDelay = 0.5;
    public const double MaxDelay = 10;
    public static readonly double[] DelayPresets = { 0, 0.3, 0.5, 1, 2, 3, 5 };
    public const string MainWindowTitle = "카카오톡";
    public const string KakaoProcess = "KakaoTalk";
    public const int PollMs = 50;
    public const int SendKeyWindowMs = 700;          // Enter/클릭 후 이 시간 안에 입력창이 비면 "보냄"
    public const string ListClass = "EVA_VH_ListControl";
    public const string EditClass = "RICHEDIT";
    public const string MenuClass = "EVA_Menu";
    public static readonly string[] Placeholders = { "메시지 입력", "메시지를 입력하세요" };  // 빈 입력창 안내 문구
    public static readonly string[] EveryoneMarks = { "모두에게", "모든 대화" };
    public static readonly string[] MeOnlyMarks = { "나에게서만", "나에게만", "이 기기", "내 기기" };
    public static readonly string[] ConfirmTitles = { "삭제", "확인" };
    public const string RegKey = @"Software\kakao-bomb";
    public const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    public const string AppName = "kakao-bomb";
}

static class Log
{
    public static bool On;
    static readonly object gate = new object();
    public static readonly string Dir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), Config.AppName);

    public static void Write(string s)
    {
        if (!On) return;
        lock (gate)
        {
            try
            {
                Directory.CreateDirectory(Dir);
                File.AppendAllText(Path.Combine(Dir, "debug.log"),
                    DateTime.Now.ToString("HH:mm:ss.fff") + " " + s + Environment.NewLine, Encoding.UTF8);
            }
            catch { }
        }
    }
}

// MARK: - Win32

static class N
{
    public delegate bool EnumProc(IntPtr h, IntPtr l);
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L, T, R, B; }
    [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X, Y; }

    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc p, IntPtr l);
    [DllImport("user32.dll")] public static extern bool EnumChildWindows(IntPtr h, EnumProc p, IntPtr l);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll")] public static extern bool IsWindow(IntPtr h);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr h);
    [DllImport("user32.dll")] public static extern bool IsChild(IntPtr parent, IntPtr h);
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("dwmapi.dll")] static extern int DwmGetWindowAttribute(IntPtr h, int attr, out RECT r, int size);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern short GetAsyncKeyState(int vk);
    [DllImport("user32.dll")] public static extern bool RegisterHotKey(IntPtr h, int id, uint mods, uint vk);
    [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern IntPtr SendMessageTimeout(IntPtr h, uint msg, IntPtr w, IntPtr l, uint flags, uint ms, out IntPtr res);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern IntPtr SendMessageTimeout(IntPtr h, uint msg, IntPtr w, StringBuilder l, uint flags, uint ms, out IntPtr res);

    public const int VK_RETURN = 0x0D, VK_LBUTTON = 0x01, VK_ESCAPE = 0x1B;

    public static string Class(IntPtr h) { var s = new StringBuilder(256); GetClassName(h, s, 256); return s.ToString(); }
    public static string Title(IntPtr h) { var s = new StringBuilder(512); GetWindowText(h, s, 512); return s.ToString(); }
    public static uint Pid(IntPtr h) { uint pid; GetWindowThreadProcessId(h, out pid); return pid; }

    public static Rectangle Rect(IntPtr h)
    {
        RECT r;
        return GetWindowRect(h, out r) ? Rectangle.FromLTRB(r.L, r.T, r.R, r.B) : Rectangle.Empty;
    }

    // 보이는 테두리 기준 (Win10/11 표준 창은 GetWindowRect에 투명 여백이 포함됨)
    public static Rectangle FrameRect(IntPtr h)
    {
        RECT r;
        if (DwmGetWindowAttribute(h, 9, out r, Marshal.SizeOf(typeof(RECT))) == 0 && r.R > r.L)
            return Rectangle.FromLTRB(r.L, r.T, r.R, r.B);
        return Rect(h);
    }

    /// 실패(-1)와 빈 문자열(0)을 구분. 카톡이 잠깐 멈춰도 우리 UI가 같이 멈추지 않게 타임아웃.
    public static int TextLength(IntPtr h)
    {
        IntPtr res;
        return SendMessageTimeout(h, 0x0E, IntPtr.Zero, IntPtr.Zero, 2, 100, out res) == IntPtr.Zero ? -1 : res.ToInt32();
    }

    public static string Text(IntPtr h, int len)
    {
        var sb = new StringBuilder(len + 2);
        IntPtr res;
        SendMessageTimeout(h, 0x0D, (IntPtr)(len + 1), sb, 2, 100, out res);
        return sb.ToString();
    }

    public static List<IntPtr> Children(IntPtr h)
    {
        var o = new List<IntPtr>();
        EnumChildWindows(h, (c, l) => { o.Add(c); return true; }, IntPtr.Zero);
        return o;
    }

    public static List<IntPtr> TopWindows(uint pid)
    {
        var o = new List<IntPtr>();
        EnumWindows((h, l) => { if (IsWindowVisible(h) && Pid(h) == pid) o.Add(h); return true; }, IntPtr.Zero);
        return o;
    }

    [DllImport("user32.dll")] static extern bool PostMessage(IntPtr h, uint msg, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] static extern bool ScreenToClient(IntPtr h, ref POINT p);
    [DllImport("user32.dll")] static extern bool PrintWindow(IntPtr h, IntPtr hdc, uint flags);

    /// 실제 마우스를 안 움직이고 그 창에 클릭 메시지만 보냄 (화면 좌표 기준)
    public static void Click(IntPtr h, Point screen, bool right)
    {
        var p = new POINT { X = screen.X, Y = screen.Y };
        ScreenToClient(h, ref p);
        IntPtr l = (IntPtr)((p.Y << 16) | (p.X & 0xFFFF));
        PostMessage(h, 0x0200, IntPtr.Zero, l);                                  // WM_MOUSEMOVE
        Thread.Sleep(10);
        PostMessage(h, right ? 0x0204u : 0x0201u, (IntPtr)(right ? 2 : 1), l);   // WM_?BUTTONDOWN
        PostMessage(h, right ? 0x0205u : 0x0202u, IntPtr.Zero, l);               // WM_?BUTTONUP
    }

    public static void Key(IntPtr h, int vk)
    {
        PostMessage(h, 0x0100, (IntPtr)vk, (IntPtr)1);
        PostMessage(h, 0x0101, (IntPtr)vk, (IntPtr)unchecked((int)0xC0000001));
    }

    /// 창 내용을 그 창에서 직접 받아옴 (다른 창에 가려져 있어도 됨)
    public static Bitmap Capture(IntPtr h)
    {
        var r = Rect(h);
        var bmp = new Bitmap(Math.Max(1, r.Width), Math.Max(1, r.Height));
        using (var g = Graphics.FromImage(bmp))
        {
            IntPtr dc = g.GetHdc();
            try { PrintWindow(h, dc, 2); } finally { g.ReleaseHdc(dc); }
        }
        return bmp;
    }

}

// MARK: - Chat model

static class Kakao
{
    static readonly Dictionary<uint, bool> cache = new Dictionary<uint, bool>();

    public static bool Owns(IntPtr h)
    {
        uint pid = N.Pid(h);
        if (pid == 0) return false;
        lock (cache)
        {
            bool hit;
            if (cache.TryGetValue(pid, out hit)) return hit;
            try { hit = string.Equals(Process.GetProcessById((int)pid).ProcessName, Config.KakaoProcess, StringComparison.OrdinalIgnoreCase); }
            catch { hit = false; }
            cache[pid] = hit;
            return hit;
        }
    }
}

/// 채팅창 = 카톡의 최상위 창 중 메시지 목록(EVA_VH_ListControl*)과 입력창(RICHEDIT*)을 둘 다 가진 창.
/// 메인 창("카카오톡")은 검색창이 일반 Edit라 걸러짐.
sealed class Chat
{
    public IntPtr Window, List, Edit;

    public static Chat From(IntPtr top)
    {
        if (top == IntPtr.Zero || !N.IsWindowVisible(top) || !Kakao.Owns(top)) return null;
        if (N.Title(top) == Config.MainWindowTitle) return null;
        var c = new Chat { Window = top };
        foreach (var k in N.Children(top))
        {
            string cls = N.Class(k);
            if (c.List == IntPtr.Zero && cls.StartsWith(Config.ListClass, StringComparison.Ordinal)) c.List = k;
            else if (c.Edit == IntPtr.Zero && cls.StartsWith(Config.EditClass, StringComparison.OrdinalIgnoreCase)) c.Edit = k;
        }
        return c.List != IntPtr.Zero && c.Edit != IntPtr.Zero ? c : null;
    }
}

/// 장전된 채팅창 하나. 입력창에 글이 있다가 Enter/클릭 직후 비워지면 "보냄"으로 보고 그 글을 돌려줌.
sealed class Armed
{
    public readonly Chat Chat;
    string prev;
    string lastText = "";

    public Armed(Chat chat)
    {
        Chat = chat;
        prev = Read() ?? "";
        lastText = prev;
    }

    /// 입력창 내용. 비어 있을 때 카톡이 넣어 두는 안내 문구("메시지 입력")는 빈 것으로 침. 읽기 실패는 null.
    string Read()
    {
        int len = N.TextLength(Chat.Edit);
        if (len < 0) return null;
        string s = len == 0 ? "" : N.Text(Chat.Edit, len);
        return Array.IndexOf(Config.Placeholders, s.Trim()) >= 0 ? "" : s;
    }

    public string Poll(bool sendKey, long enterAge, long clickAge)
    {
        string cur = Read();
        if (cur == null) return null;
        if (Log.On && cur.Length != prev.Length)
            Log.Write("입력창 " + prev.Length + "→" + cur.Length + " (Enter " + enterAge + "ms 전, 클릭 " + clickAge + "ms 전)");
        bool sent = prev.Length > 0 && cur.Length == 0 && sendKey && lastText.Trim().Length > 0;
        prev = cur;
        if (cur.Length > 0) lastText = cur;
        return sent ? lastText : null;
    }
}

// MARK: - UI Automation helpers

static class Ax
{
    static readonly TreeWalker walker = TreeWalker.RawViewWalker;

    public static AutomationElement From(IntPtr h)
    {
        try { return AutomationElement.FromHandle(h); } catch { return null; }
    }

    public static string Name(AutomationElement e)
    {
        try { return e.Current.Name ?? ""; } catch { return ""; }
    }

    public static Rectangle Bounds(AutomationElement e)
    {
        try
        {
            var r = e.Current.BoundingRectangle;
            if (r.IsEmpty || double.IsInfinity(r.Width) || r.Width < 1 || r.Height < 1) return Rectangle.Empty;
            return new Rectangle((int)r.X, (int)r.Y, (int)r.Width, (int)r.Height);
        }
        catch { return Rectangle.Empty; }
    }

    public static List<AutomationElement> Kids(AutomationElement e, int max)
    {
        var o = new List<AutomationElement>();
        try
        {
            for (var k = walker.GetFirstChild(e); k != null && o.Count < max; k = walker.GetNextSibling(k)) o.Add(k);
        }
        catch { }
        return o;
    }

    /// 뒤에서부터 max개 (채팅 기록은 수천 개라 끝에서만 봄)
    public static List<AutomationElement> LastKids(AutomationElement e, int max)
    {
        var o = new List<AutomationElement>();
        try
        {
            for (var k = walker.GetLastChild(e); k != null && o.Count < max; k = walker.GetPreviousSibling(k)) o.Add(k);
        }
        catch { }
        return o;
    }

    // BFS. 메시지 목록 안쪽은 건너뜀.
    public static List<AutomationElement> All(AutomationElement root, int max)
    {
        var o = new List<AutomationElement>();
        var q = new Queue<AutomationElement>();
        q.Enqueue(root);
        while (q.Count > 0 && o.Count < max)
        {
            var e = q.Dequeue();
            o.Add(e);
            string cls = "";
            try { cls = e.Current.ClassName ?? ""; } catch { }
            if (cls.StartsWith(Config.ListClass, StringComparison.Ordinal)) continue;
            foreach (var k in Kids(e, 60)) q.Enqueue(k);
        }
        return o;
    }

    public static string Describe(AutomationElement e)
    {
        try
        {
            var c = e.Current;
            var pats = new List<string>();
            foreach (var p in e.GetSupportedPatterns()) pats.Add(p.ProgrammaticName.Replace("PatternIdentifiers.Pattern", ""));
            string name = c.Name ?? "";
            if (name.Length > 40) name = name.Substring(0, 40) + "…";
            return c.ControlType.ProgrammaticName.Replace("ControlType.", "") + " [" + c.ClassName + "] \"" +
                name.Replace("\r", " ").Replace("\n", " ") + "\" " + Bounds(e) + " " + string.Join(",", pats);
        }
        catch { return "(사라짐)"; }
    }

    public static void Dump(AutomationElement e, Action<string> write, int depth, int maxDepth)
    {
        write(new string(' ', depth * 2) + Describe(e));
        if (depth >= maxDepth) return;
        string cls = "";
        try { cls = e.Current.ClassName ?? ""; } catch { }
        bool list = cls.StartsWith(Config.ListClass, StringComparison.Ordinal);
        foreach (var k in list ? LastKids(e, 6) : Kids(e, 30)) Dump(k, write, depth + 1, maxDepth);
    }

    /// 좌표가 있으면 클릭 메시지 (메뉴/모달에서 Invoke는 창이 닫힐 때까지 안 돌아올 수 있음). 없으면 패턴으로.
    public static bool Activate(AutomationElement e, IntPtr window)
    {
        var r = Bounds(e);
        if (!r.IsEmpty)
        {
            N.Click(window, new Point(r.Left + r.Width / 2, r.Top + r.Height / 2), false);
            return true;
        }
        object p;
        try
        {
            if (e.TryGetCurrentPattern(InvokePattern.Pattern, out p))
            {
                var inv = (InvokePattern)p;
                var t = new Thread(() => { try { inv.Invoke(); } catch { } });
                t.IsBackground = true;
                t.Start();
                t.Join(300);
                return true;
            }
            if (e.TryGetCurrentPattern(SelectionItemPattern.Pattern, out p)) { ((SelectionItemPattern)p).Select(); return true; }
        }
        catch { }
        return false;
    }
}

// MARK: - Delete for everyone

static class Deleter
{
    public static int Scale = 100;  // DPI %
    static int S(int v) { return v * Scale / 100; }

    static string Squash(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (char c in s) if (!char.IsWhiteSpace(c)) sb.Append(c);
        return sb.ToString();
    }

    static bool HasAny(string s, string[] marks)
    {
        foreach (var m in marks) if (s.Contains(m)) return true;
        return false;
    }

    static int Diff(Color a, Color b)
    {
        return Math.Abs(a.R - b.R) + Math.Abs(a.G - b.G) + Math.Abs(a.B - b.B);
    }

    /// 메시지 목록의 화면 영역 (스크롤바 제외)
    static Rectangle ListArea(Chat chat)
    {
        var lr = N.Rect(chat.List);
        foreach (var k in N.Children(chat.List))
        {
            if (!N.IsWindowVisible(k)) continue;
            var sr = N.Rect(k);
            if (sr.Left > lr.Left + lr.Width / 2 && sr.Left < lr.Right) lr.Width = sr.Left - lr.Left;
        }
        return lr;
    }

    /// 방금 보낸 내 말풍선의 화면 좌표. 감지 시점 좌표는 못 믿으므로 (행이 밀림) 삭제 직전에 매번 새로 찾음.
    ///  1) 목록이 UIA로 읽히면: 끝에서부터 내용이 같은 항목을 찾아 그 줄 안에서만 말풍선을 찾음
    ///  2) 안 읽히면: 화면을 캡처해 맨 아래쪽의 오른쪽 정렬(= 내 것) 말풍선을 찾음
    static Point? FindBubble(Chat chat, string text)
    {
        var lr = ListArea(chat);
        if (lr.Width < S(120) || lr.Height < S(40)) return null;
        int top = Math.Max(0, lr.Height - S(400)), bottom = lr.Height - 1;  // 방금 보낸 건 맨 아래쪽에 있음
        bool haveRow = false;

        var listEl = Ax.From(chat.List);
        var items = listEl == null ? new List<AutomationElement>() : Ax.LastKids(listEl, 14);
        items.RemoveAll(e => { try { return e.Current.NativeWindowHandle != 0; } catch { return true; } });
        if (items.Count > 0)
        {
            string want = Squash(text);
            AutomationElement hit = null;
            foreach (var it in items)  // 최근 것부터
            {
                if (want.Length > 0 && Squash(Ax.Name(it)).Contains(want)) { hit = it; break; }
            }
            if (hit == null)
            {
                Log.Write("내용 일치 없음: len" + want.Length + ", 최근 항목 " + items.Count + "개");
                return null;
            }
            var row = Rectangle.Intersect(Ax.Bounds(hit), lr);
            if (row.Height < 4) { Log.Write("일치 항목이 화면 밖"); return null; }
            if (row.Width < lr.Width * 9 / 10)   // 항목 영역이 곧 말풍선
                return new Point(row.Left + row.Width / 2, row.Top + row.Height / 2);
            top = row.Top - lr.Top;
            bottom = row.Bottom - lr.Top - 1;
            haveRow = true;
        }

        using (var bmp = N.Capture(chat.List))
        {

            // 배경색 = 맨 왼쪽 세로줄에서 가장 흔한 색 (프로필/말풍선이 안 닿는 여백)
            var counts = new Dictionary<int, int>();
            int bgArgb = 0, best = 0;
            for (int y = 0; y < lr.Height; y += 2)
            {
                int c = bmp.GetPixel(S(3), y).ToArgb(), n;
                counts.TryGetValue(c, out n);
                counts[c] = ++n;
                if (n > best) { best = n; bgArgb = c; }
            }
            Color bg = Color.FromArgb(bgArgb);

            // 오른쪽 가장자리 근처에 배경 아닌 덩어리가 있으면 내 말풍선 (남의 것은 왼쪽 정렬)
            int[] probes = { lr.Width - S(30), lr.Width - S(48), lr.Width - S(66) };
            for (int y = bottom; y >= top; y--)
            {
                foreach (int x in probes)
                {
                    if (Diff(bmp.GetPixel(x, y), bg) <= 30) continue;
                    int y2 = y;
                    while (y2 > top && Diff(bmp.GetPixel(x, y2 - 1), bg) > 30) y2--;
                    if (y - y2 + 1 >= S(14))
                        return new Point(lr.Left + x, lr.Top + (y + y2) / 2);
                }
            }
        }
        if (haveRow) return new Point(lr.Right - S(50), lr.Top + (top + bottom) / 2);
        Log.Write("오른쪽 정렬 말풍선 못 찾음 (UIA 항목 없음, 픽셀 탐색 실패)");
        return null;
    }

    static List<IntPtr> NewWindows(uint pid, HashSet<IntPtr> before)
    {
        var o = N.TopWindows(pid);
        o.RemoveAll(h => before.Contains(h) || N.Class(h).StartsWith("tooltips", StringComparison.OrdinalIgnoreCase));
        return o;
    }

    /// 메뉴 창에서 글자가 있는 줄들의 세로 중심 (창 기준 좌표)
    static List<int> TextRows(IntPtr menu)
    {
        var rows = new List<int>();
        var r = N.Rect(menu);
        if (r.Width < S(40) || r.Height < S(16)) return rows;
        using (var bmp = N.Capture(menu))
        {
            int start = -1;
            for (int y = S(3); y <= r.Height - S(3); y++)
            {
                bool ink = false;
                if (y < r.Height - S(3))
                {
                    Color bg = bmp.GetPixel(r.Width - S(4), y);
                    for (int x = S(8); x < r.Width - S(8) && !ink; x++) ink = Diff(bmp.GetPixel(x, y), bg) > 90;
                }
                if (ink) { if (start < 0) start = y; }
                else if (start >= 0) { if (y - start >= S(6)) rows.Add((start + y - 1) / 2); start = -1; }
            }
        }
        return rows;
    }

    /// 조건이 될 때까지 짧게 반복 확인 (고정 대기 대신: 되는 즉시 다음 단계로)
    static bool Until(int ms, Func<bool> ok)
    {
        var sw = Stopwatch.StartNew();
        do
        {
            if (ok()) return true;
            Thread.Sleep(10);
        } while (sw.ElapsedMilliseconds < ms);
        return false;
    }

    static IntPtr WaitMenu(uint pid, HashSet<IntPtr> known, int ms)
    {
        var sw = Stopwatch.StartNew();
        do
        {
            Thread.Sleep(10);
            foreach (var h in NewWindows(pid, known))
                if (N.Class(h).StartsWith(Config.MenuClass, StringComparison.Ordinal) && N.Rect(h).Height >= S(16)) return h;
        } while (sw.ElapsedMilliseconds < ms);
        return IntPtr.Zero;
    }

    /// 말풍선 우클릭 → 메뉴에서 ↑ 키로 "삭제 >" 줄까지 이동 → → 키로 하위 메뉴. 열린 메뉴는 opened에 쌓임.
    /// 메뉴는 실제 마우스 위치를 따라가서 마우스 메시지로는 불안정함. 키 메시지는 안정적.
    static IntPtr OpenDeleteSubmenu(Chat chat, Point bubble, HashSet<IntPtr> known, List<IntPtr> opened)
    {
        uint pid = N.Pid(chat.Window);
        N.Click(chat.List, bubble, true);
        var menu = WaitMenu(pid, known, 700);
        if (menu == IntPtr.Zero) { Log.Write("우클릭 " + bubble + " 메뉴 안 뜸"); return IntPtr.Zero; }
        opened.Add(menu);
        known.Add(menu);
        Point? arrow = null;
        if (!Until(400, () => (arrow = ArrowRow(menu)) != null)) { Log.Write("메뉴에서 '삭제 >' 줄 못 찾음"); return IntPtr.Zero; }
        int arrowY = arrow.Value.Y - N.Rect(menu).Top;
        bool on = false;
        for (int i = 0; i < 14 && !on; i++)   // 삭제는 아래쪽에 있어서 ↑가 빠름
        {
            int was = LitRow(menu);
            N.Key(menu, 0x26);
            int now = was;
            Until(150, () => (now = LitRow(menu)) != was);
            on = Math.Abs(now - arrowY) <= S(8);
        }
        if (!on) { Log.Write("↑ 키로 '삭제 >' 줄에 못 감"); return IntPtr.Zero; }
        N.Key(menu, 0x27);
        var sub = WaitMenu(pid, known, 600);
        if (sub == IntPtr.Zero) { Log.Write("→ 키로 하위 메뉴 안 뜸"); return IntPtr.Zero; }
        opened.Add(sub);
        known.Add(sub);
        Until(300, () => TextRows(sub).Count == 2);  // 다 그려질 때까지
        return sub;
    }

    /// 하위 메뉴 두 줄 중 첫 줄(모두에게서 삭제)만 강조됐는지. 강조색은 부모 메뉴의 "삭제 >" 줄과 같아야 함.
    static bool FirstRowLit(IntPtr menu, IntPtr sub, List<int> rows)
    {
        if (!N.IsWindow(menu) || !N.IsWindow(sub)) return false;
        int litY = LitRow(menu);
        if (litY < 0) return false;
        Color lit;
        using (var bmp = N.Capture(menu)) lit = bmp.GetPixel(bmp.Width - S(8), litY);
        using (var bmp = N.Capture(sub))
        {
            int x = bmp.Width - S(12);
            return Diff(bmp.GetPixel(x, rows[0]), lit) <= 12 && Diff(bmp.GetPixel(x, rows[1]), lit) > 12;
        }
    }

    /// 메뉴에서 강조(선택)된 줄의 세로 중심 (창 기준). 없으면 -1.
    static int LitRow(IntPtr menu)
    {
        if (!N.IsWindow(menu)) return -1;
        using (var bmp = N.Capture(menu))
        {
            int x = bmp.Width - S(8), start = -1;
            Color bg = bmp.GetPixel(x, S(4));
            for (int y = S(4); y < bmp.Height - S(3); y++)
            {
                bool lit = Diff(bmp.GetPixel(x, y), bg) > 12;
                if (lit && start < 0) start = y;
                if (!lit && start >= 0) { if (y - start >= S(10)) return (start + y) / 2; start = -1; }
            }
            return start >= 0 ? (start + bmp.Height) / 2 : -1;
        }
    }

    static bool MenusClosed(List<IntPtr> opened)
    {
        foreach (var m in opened) if (N.IsWindow(m) && N.IsWindowVisible(m)) return false;
        return true;
    }

    /// 첫 줄(모두에게서 삭제)이 선택된 게 확인될 때에만 Enter. 메뉴가 닫히면 성공.
    static bool PickFirstRow(List<IntPtr> opened, IntPtr sub, List<int> rows)
    {
        foreach (var k in new[] { sub, opened[0] })
        {
            if (MenusClosed(opened)) return false;
            if (!Until(200, () => FirstRowLit(opened[0], sub, rows))) { Log.Write("첫 줄이 선택돼 있지 않음 → 안 누름"); return false; }
            N.Key(k, N.VK_RETURN);
            if (Until(300, () => MenusClosed(opened))) return true;
            Log.Write("Enter→[" + N.Class(k) + "] 보냈지만 메뉴가 안 닫힘");
        }
        return false;
    }

    static void CloseMenus(List<IntPtr> menus)
    {
        for (int i = menus.Count - 1; i >= 0; i--)
        {
            if (N.IsWindow(menus[i])) { N.Key(menus[i], N.VK_ESCAPE); Thread.Sleep(80); }
        }
    }

    enum Step { Nothing, Pressed, Cancelled }

    /// 확인창 하나 처리. "모두에게서" 선택지가 있으면 고르고 "삭제"/"확인"을 누름.
    /// "나에게서만" 쪽만 보이면 (남의 메시지를 잘못 집었거나 5분 지남) 취소.
    static Step HandleDialog(IntPtr dlg)
    {
        var root = Ax.From(dlg);
        if (root == null) return Step.Nothing;
        var nodes = Ax.All(root, 200);
        AutomationElement everyone = null, confirm = null;
        bool meOnly = false;
        foreach (var e in nodes)
        {
            string name = Ax.Name(e).Trim();
            if (name.Length == 0 || name.Length > 30) continue;
            if (HasAny(name, Config.MeOnlyMarks)) { meOnly = true; continue; }
            if (everyone == null && HasAny(name, Config.EveryoneMarks)) everyone = e;
            if (Array.IndexOf(Config.ConfirmTitles, name) >= 0)
            {
                bool button = false;
                try { button = e.Current.ControlType == ControlType.Button; } catch { }
                if (confirm == null || button) confirm = e;
            }
        }
        if (meOnly && everyone == null)
        {
            Log.Write("'모두에게서' 선택지가 없음 → 취소");
            N.Key(dlg, N.VK_ESCAPE);
            return Step.Cancelled;
        }
        bool pressed = false;
        if (everyone != null)
        {
            Log.Write("선택: " + Ax.Describe(everyone));
            pressed = Ax.Activate(everyone, dlg);
            Thread.Sleep(80);
        }
        if (confirm != null)
        {
            Log.Write("확인 버튼: " + Ax.Describe(confirm));
            pressed |= Ax.Activate(confirm, dlg);
        }
        return pressed ? Step.Pressed : Step.Nothing;
    }

    static void DumpWindows(string title, List<IntPtr> wins)
    {
        if (!Log.On) return;
        Log.Write("--- " + title + " ---");
        foreach (var h in wins)
        {
            Log.Write(string.Format("{0:X} [{1}] {2}", h.ToInt64(), N.Class(h), N.Rect(h)));
            try
            {
                var wr = N.Rect(h);
                if (wr.Width > 0 && wr.Height > 0)
                    using (var bmp = N.Capture(h)) bmp.Save(Path.Combine(Log.Dir, "shot-" + N.Class(h).Replace('#', '_') + ".png"));
            }
            catch { }
            var root = Ax.From(h);
            if (root != null) Ax.Dump(root, Log.Write, 1, 5);
        }
    }

    public static bool DeleteForEveryone(Chat chat, string text)
    {
        try { return Run(chat, text); }
        catch (Exception ex) { Log.Write("예외: " + ex); return false; }
    }

    static bool Run(Chat chat, string text)
    {
        if (!N.IsWindow(chat.Window) || N.IsIconic(chat.Window)) { Log.Write("채팅창이 닫혔거나 최소화됨"); return false; }
        uint pid = N.Pid(chat.Window);
        var before = new HashSet<IntPtr>(N.TopWindows(pid));

        // 1) 말풍선 우클릭 → 메뉴 "삭제 >" → 하위 메뉴 첫 줄 "모두에게서 삭제"
        //    하위 메뉴가 두 줄(모두에게서/나에게서만)이 아니면 아무것도 안 누르고 닫음
        bool clicked = false;
        for (int attempt = 1; attempt <= 4 && !clicked; attempt++)
        {
            if (attempt > 1) Thread.Sleep(150);
            var pt = FindBubble(chat, text);
            if (pt == null) { Log.Write("[" + attempt + "] 보낸 말풍선 못 찾음, 재시도"); continue; }

            var known = new HashSet<IntPtr>(before);
            var opened = new List<IntPtr>();
            var sub = OpenDeleteSubmenu(chat, pt.Value, known, opened);
            var rows = sub == IntPtr.Zero ? new List<int>() : TextRows(sub);
            if (rows.Count != 2)
            {
                Log.Write("[" + attempt + "] 삭제 하위 메뉴가 두 줄이 아님 (" + rows.Count + "줄)");
                DumpWindows("메뉴", opened);
                CloseMenus(opened);
                continue;
            }
            clicked = PickFirstRow(opened, sub, rows);
            Log.Write("[" + attempt + "] '모두에게서 삭제' " + (clicked ? "누름" : "못 누름"));
            if (!clicked) { DumpWindows("메뉴", opened); CloseMenus(opened); }
            foreach (var m in opened) before.Add(m);
        }
        if (!clicked) return false;

        // 2) 확인창 (여러 번 떠도 처리)
        var start = Stopwatch.StartNew();
        long lastPress = -1;
        bool dumped = false;
        while (start.ElapsedMilliseconds < 3000)
        {
            Thread.Sleep(30);
            var wins = NewWindows(pid, before);
            Step step = Step.Nothing;
            foreach (var w in wins)
            {
                step = HandleDialog(w);
                if (step != Step.Nothing) break;
            }
            if (step == Step.Cancelled) return false;
            if (step == Step.Pressed) { lastPress = start.ElapsedMilliseconds; Thread.Sleep(200); continue; }
            if (lastPress < 0 && wins.Count == 0 && start.ElapsedMilliseconds > 250) return true;  // 이 버전은 확인창 없이 바로 삭제됨
            if (lastPress >= 0 && start.ElapsedMilliseconds - lastPress > 600) return true;  // 더 누를 확인창 없음
            if (lastPress < 0 && start.ElapsedMilliseconds > 500 && !dumped) { dumped = true; DumpWindows("확인창 후보", wins); }
            if (lastPress < 0 && start.ElapsedMilliseconds > 1500)
            {
                if (wins.Count == 0) { Log.Write("확인창 없이 끝남 (바로 삭제된 것으로 간주)"); return true; }
                Log.Write("확인창을 읽을 수 없음 → 취소");
                foreach (var w in wins) N.Key(w, N.VK_ESCAPE);
                return false;
            }
        }
        Log.Write("시간 초과: 확인창이 계속 남아 있음");
        return false;
    }

    /// 메뉴(EVA_Menu)는 UIA로 안 읽힘. 하위 메뉴 화살표(>)가 있는 유일한 줄 = "삭제". 그 줄의 화면 좌표.
    static Point? ArrowRow(IntPtr menu)
    {
        var r = N.Rect(menu);
        if (r.Width < S(60) || r.Height < S(40)) return null;
        using (var bmp = N.Capture(menu))
        {
            int w = r.Width, x0 = w * 78 / 100, x1 = w * 92 / 100, t0 = w * 60 / 100, t1 = w * 76 / 100;
            var hits = new List<int>();
            int start = -1;
            for (int y = S(3); y <= r.Height - S(3); y++)
            {
                Color bg = bmp.GetPixel(w - S(4), Math.Min(y, r.Height - 1));
                bool ink = false;
                if (y < r.Height - S(3))
                    for (int x = x0; x < x1 && !ink; x++) ink = Diff(bmp.GetPixel(x, y), bg) > 90;
                if (ink) { if (start < 0) start = y; continue; }
                if (start < 0) continue;
                int a = start, b = y - 1;
                start = -1;
                if (b - a + 1 < S(5) || b - a + 1 > S(22)) continue;
                bool text = false;   // 긴 글자가 화살표 자리까지 온 줄은 제외
                for (int yy = a; yy <= b && !text; yy++)
                    for (int x = t0; x < t1 && !text; x++) text = Diff(bmp.GetPixel(x, yy), bmp.GetPixel(w - S(4), yy)) > 90;
                if (!text) hits.Add((a + b) / 2);
            }
            if (hits.Count != 1) { Log.Write("화살표 줄 " + hits.Count + "개 (1개여야 함)"); return null; }
            return new Point(r.Left + w / 2, r.Top + hits[0]);
        }
    }

    /// --menushot: 맨 아래 내 말풍선의 메뉴와 삭제 하위 메뉴 모양만 menuN.png 로 저장하고 닫음 (삭제 안 함)
    public static string MenuShot()
    {
        Chat chat = null;
        N.EnumWindows((h, l) => { if (chat == null) chat = Chat.From(h); return true; }, IntPtr.Zero);
        if (chat == null) return "채팅창 없음";
        uint pid = N.Pid(chat.Window);
        var known = new HashSet<IntPtr>(N.TopWindows(pid));
        var pt = FindBubble(chat, "");
        if (pt == null) return "내 말풍선 못 찾음";
        var sb = new StringBuilder("우클릭 " + pt.Value);
        var opened = new List<IntPtr>();
        var sw = Stopwatch.StartNew();
        var sub = OpenDeleteSubmenu(chat, pt.Value, known, opened);
        sb.Append(" / 메뉴 " + opened.Count + "개 " + sw.ElapsedMilliseconds + "ms");
        for (int i = 0; i < opened.Count; i++)
            using (var bmp = N.Capture(opened[i])) bmp.Save(Path.Combine(Log.Dir, "menu" + i + ".png"));
        if (sub != IntPtr.Zero)
        {
            var rows = TextRows(sub);
            sb.Append(" / 하위 메뉴 글자 줄 " + rows.Count + (rows.Count == 2 ? " 첫 줄 선택됨=" + FirstRowLit(opened[0], sub, rows) : ""));
        }
        CloseMenus(opened);
        Thread.Sleep(200);
        foreach (var m in opened) sb.Append(" / 닫힘=" + !N.IsWindow(m));
        return sb.ToString();
    }

    /// --dump: 삭제 없이 카톡 창 구조만 기록
    public static string DumpAll()
    {
        var sb = new StringBuilder();
        Action<string> w = s => sb.AppendLine(s);
        N.EnumWindows((h, l) =>
        {
            if (!N.IsWindowVisible(h) || !Kakao.Owns(h)) return true;
            var chat = Chat.From(h);
            w(string.Format("== {0:X} [{1}] \"{2}\" {3} chat={4}", h.ToInt64(), N.Class(h), N.Title(h), N.Rect(h), chat != null));
            foreach (var k in N.Children(h))
                w(string.Format("   child {0:X} [{1}] vis={2} {3}", k.ToInt64(), N.Class(k), N.IsWindowVisible(k), N.Rect(k)));
            var root = Ax.From(h);
            if (root != null) Ax.Dump(root, w, 1, 5);
            if (chat != null)
            {
                var pt = FindBubble(chat, "");
                w("   입력창 글자 수=" + N.TextLength(chat.Edit) + " 맨 아래 내 말풍선(픽셀)=" + (pt == null ? "없음" : pt.Value.ToString()));
            }
            return true;
        }, IntPtr.Zero);
        if (sb.Length == 0) sb.AppendLine("카카오톡 창 없음 (실행 중이고 채팅창이 열려 있어야 함)");
        return sb.ToString();
    }
}

// MARK: - Tray (채팅창 옆 플로팅 창)

sealed class TrayButton : Button
{
    public TrayButton()
    {
        SetStyle(ControlStyles.Selectable, false);
        FlatStyle = FlatStyle.Flat;
        FlatAppearance.BorderSize = 0;
        BackColor = Color.FromArgb(70, 70, 74);
        ForeColor = Color.White;
        TabStop = false;
    }
}

sealed class App : Form
{
    readonly NotifyIcon status = new NotifyIcon();
    readonly ToolStripMenuItem delayMenu = new ToolStripMenuItem("삭제 지연");
    readonly ToolStripMenuItem loginItem = new ToolStripMenuItem("로그인 시 자동 실행");
    readonly TrayButton bomb = new TrayButton();
    readonly Label delayLabel = new Label();
    readonly System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer();
    readonly Icon idleIcon = MakeIcon(false), armedIcon = MakeIcon(true);
    readonly BlockingCollection<Action> deleteQueue = new BlockingCollection<Action>();
    readonly Stopwatch clock = Stopwatch.StartNew();

    readonly List<Armed> armed = new List<Armed>();
    Chat current;                    // 트레이가 붙어 있는 채팅창
    IntPtr flashWindow; string flashText; long flashUntil;  // ⏳/✅/❌ 잠깐 표시
    IntPtr lastFg; Chat lastFgChat;
    long lastEnter = -10000, lastClick = -10000;
    int pending;                     // 대기/진행 중인 삭제 수
    double delay;
    readonly int scale;

    int S(int v) { return v * scale / 100; }

    protected override bool ShowWithoutActivation { get { return true; } }
    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= 0x80 | 0x08000000 | 0x8;  // TOOLWINDOW | NOACTIVATE | TOPMOST: 포커스 안 뺏음
            return cp;
        }
    }

    public App()
    {
        using (var g = Graphics.FromHwnd(IntPtr.Zero)) scale = (int)Math.Round(g.DpiX * 100 / 96);
        Deleter.Scale = scale;
        delay = LoadDelay();

        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        AutoScaleMode = AutoScaleMode.None;
        BackColor = Color.FromArgb(40, 40, 43);
        ClientSize = new Size(S(206), S(32));
        Font = new Font("Segoe UI", 9f);

        int x = S(5), y = S(4), h = S(24);
        bomb.SetBounds(x, y, S(98), h);
        bomb.Click += (s, e) => { if (current != null) Toggle(current); };
        x += S(98) + S(3);
        var dec = new TrayButton { Text = "−" };
        dec.SetBounds(x, y, S(24), h);
        dec.Click += (s, e) => SetDelay(delay - 0.1);
        x += S(24);
        delayLabel.SetBounds(x, y, S(44), h);
        delayLabel.TextAlign = ContentAlignment.MiddleCenter;
        delayLabel.ForeColor = Color.White;
        x += S(44);
        var inc = new TrayButton { Text = "+" };
        inc.SetBounds(x, y, S(24), h);
        inc.Click += (s, e) => SetDelay(delay + 0.1);
        Controls.AddRange(new Control[] { bomb, dec, delayLabel, inc });

        var menu = new ContextMenuStrip();
        menu.Items.Add(new ToolStripMenuItem("카톡 채팅창 옆 💣 버튼 또는 Ctrl+Alt+D로 장전/해제") { Enabled = false });
        menu.Items.Add(new ToolStripSeparator());
        delayMenu.DropDownItems.Add("-");  // 열릴 때 채움
        delayMenu.DropDownOpening += (s, e) => FillDelayMenu();
        menu.Items.Add(delayMenu);
        loginItem.Click += (s, e) => ToggleLoginItem();
        menu.Items.Add(loginItem);
        menu.Items.Add(new ToolStripSeparator());
        var v = Assembly.GetExecutingAssembly().GetName().Version;
        menu.Items.Add(new ToolStripMenuItem(Config.AppName + " " + v.ToString(3)) { Enabled = false });
        menu.Items.Add("종료", null, (s, e) => { status.Visible = false; Application.Exit(); });
        menu.Opening += (s, e) => loginItem.Checked = LoginItemEnabled();
        status.ContextMenuStrip = menu;
        status.Visible = true;

        var worker = new Thread(() => { foreach (var job in deleteQueue.GetConsumingEnumerable()) job(); });
        worker.IsBackground = true;
        worker.Start();

        timer.Interval = Config.PollMs;
        timer.Tick += (s, e) => Tick();
        timer.Start();
        Log.Write("시작, 배율 " + scale + "%");
        Refresh2();
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        bool ok = N.RegisterHotKey(Handle, 1, 0x2 | 0x1 | 0x4000, (uint)Keys.D);  // Ctrl+Alt+D, 반복 없음
        Log.Write("단축키 등록 " + (ok ? "성공" : "실패 (다른 프로그램이 Ctrl+Alt+D 사용 중)") + " hwnd=" + Handle.ToString("X"));
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == 0x0312) HotKey();
        base.WndProc(ref m);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        using (var pen = new Pen(Color.FromArgb(95, 95, 100)))
            e.Graphics.DrawRectangle(pen, 0, 0, ClientSize.Width - 1, ClientSize.Height - 1);
    }

    static Icon MakeIcon(bool lit)
    {
        using (var bmp = new Bitmap(32, 32))
        {
            using (var g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                using (var body = new SolidBrush(lit ? Color.FromArgb(232, 72, 32) : Color.FromArgb(45, 45, 48)))
                    g.FillEllipse(body, 3, 9, 22, 22);
                using (var rim = new Pen(Color.FromArgb(230, 230, 230), 1.5f)) g.DrawEllipse(rim, 3, 9, 22, 22);
                using (var fuse = new Pen(Color.FromArgb(170, 130, 80), 3f)) g.DrawLine(fuse, 20, 12, 26, 6);
                using (var spark = new SolidBrush(lit ? Color.Gold : Color.Orange)) g.FillEllipse(spark, 23, 1, lit ? 9 : 7, lit ? 9 : 7);
            }
            return Icon.FromHandle(bmp.GetHicon());
        }
    }

    // MARK: settings

    static double LoadDelay()
    {
        try
        {
            using (var k = Registry.CurrentUser.OpenSubKey(Config.RegKey))
            {
                double d;
                if (k != null && double.TryParse(Convert.ToString(k.GetValue("DeleteDelay")),
                        System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out d))
                    return Math.Min(Config.MaxDelay, Math.Max(0, d));
            }
        }
        catch { }
        return Config.DefaultDelay;
    }

    void SetDelay(double d)
    {
        delay = Math.Min(Config.MaxDelay, Math.Max(0, Math.Round(d * 10) / 10));
        try
        {
            using (var k = Registry.CurrentUser.CreateSubKey(Config.RegKey))
                k.SetValue("DeleteDelay", delay.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }
        catch { }
        Refresh2();
    }

    void FillDelayMenu()
    {
        delayMenu.DropDownItems.Clear();
        foreach (double d in Config.DelayPresets)
        {
            double pick = d;
            var item = new ToolStripMenuItem(d.ToString("0.0") + "초", null, (s, e) => SetDelay(pick));
            item.Checked = Math.Abs(d - delay) < 0.01;
            delayMenu.DropDownItems.Add(item);
        }
    }

    static bool LoginItemEnabled()
    {
        try { using (var k = Registry.CurrentUser.OpenSubKey(Config.RunKey)) return k != null && k.GetValue(Config.AppName) != null; }
        catch { return false; }
    }

    void ToggleLoginItem()
    {
        try
        {
            using (var k = Registry.CurrentUser.CreateSubKey(Config.RunKey))
            {
                if (k.GetValue(Config.AppName) != null) k.DeleteValue(Config.AppName, false);
                else k.SetValue(Config.AppName, "\"" + Application.ExecutablePath + "\"");
            }
        }
        catch (Exception ex)
        {
            SystemSounds.Beep.Play();
            Log.Write("자동 실행 설정 실패: " + ex.Message);
        }
    }

    // MARK: actions

    void HotKey()
    {
        var fg = N.GetForegroundWindow();
        var chat = Chat.From(fg);
        Log.Write("단축키: 앞 창 " + fg.ToString("X") + " [" + N.Class(fg) + "] 채팅창=" + (chat != null));
        if (chat == null) { SystemSounds.Beep.Play(); return; }
        Toggle(chat);
    }

    void Toggle(Chat chat)
    {
        int i = armed.FindIndex(a => a.Chat.Window == chat.Window);
        if (i >= 0) armed.RemoveAt(i);
        else armed.Add(new Armed(chat));
        Log.Write((i >= 0 ? "해제 " : "장전 ") + chat.Window.ToString("X") + " 입력창 " + chat.Edit.ToString("X"));
        Refresh2();
    }

    // MARK: loop

    void Tick()
    {
        long now = clock.ElapsedMilliseconds;
        if (N.GetAsyncKeyState(N.VK_RETURN) != 0) lastEnter = now;
        if (N.GetAsyncKeyState(N.VK_LBUTTON) != 0) lastClick = now;  // 전송 버튼 클릭
        bool sendKey = now - lastEnter < Config.SendKeyWindowMs || now - lastClick < Config.SendKeyWindowMs;

        if (armed.RemoveAll(a => !N.IsWindow(a.Chat.Window)) > 0) Refresh2();  // 닫힌 채팅창
        foreach (var a in armed.ToArray())
        {
            string text = a.Poll(sendKey, now - lastEnter, now - lastClick);
            if (text != null) Fire(a.Chat, text);
        }
        if (flashText != null && flashUntil > 0 && now > flashUntil) { flashText = null; Refresh2(); }
        FollowFocusedChat();
    }

    void FollowFocusedChat()
    {
        var fg = N.GetForegroundWindow();
        if (fg != lastFg) { lastFg = fg; lastFgChat = Chat.From(fg); }
        var chat = lastFgChat;
        if (chat == null && pending > 0 && current != null && Kakao.Owns(fg)) chat = current;  // 삭제 중 메뉴/확인창
        if (chat == null || !N.IsWindowVisible(chat.Window) || N.IsIconic(chat.Window))
        {
            current = null;
            if (Visible) Hide();
            return;
        }
        if (current == null || current.Window != chat.Window) { current = chat; Refresh2(); }
        Place(N.FrameRect(chat.Window));
        if (!Visible) Show();
    }

    /// 채팅창 위쪽 오른편에 붙임. 공간 없으면 오른쪽 → 왼쪽 → 창 안쪽.
    void Place(Rectangle win)
    {
        int w = Width, h = Height, gap = S(4);
        var candidates = new[]
        {
            new Rectangle(win.Right - w, win.Top - h - gap, w, h),
            new Rectangle(win.Right + gap, win.Top, w, h),
            new Rectangle(win.Left - w - gap, win.Top, w, h),
            new Rectangle(win.Right - w - S(8), win.Top + S(40), w, h),
        };
        var pick = candidates[candidates.Length - 1];
        foreach (var c in candidates)
        {
            if (Array.Exists(Screen.AllScreens, s => s.WorkingArea.Contains(c))) { pick = c; break; }
        }
        if (Location != pick.Location) Location = pick.Location;
    }

    void Fire(Chat chat, string text)
    {
        // 장전은 유지 (토글). 연달아 보내도 메뉴 조작이 겹치지 않게 삭제는 한 번에 하나씩.
        Log.Write("전송 감지: len" + text.Length + ", 지연 " + delay.ToString("0.0") + "초");
        pending++;
        SetFlash(chat.Window, "⏳", 0);
        long due = clock.ElapsedMilliseconds + (long)(delay * 1000);
        deleteQueue.Add(() =>
        {
            long wait = due - clock.ElapsedMilliseconds;
            if (wait > 0) Thread.Sleep((int)wait);
            bool ok = Deleter.DeleteForEveryone(chat, text);
            Log.Write(ok ? "삭제됨" : "실패");
            BeginInvoke((Action)(() =>
            {
                pending--;
                if (!ok) SystemSounds.Hand.Play();
                SetFlash(chat.Window, ok ? "✅ 삭제됨" : "❌ 실패", clock.ElapsedMilliseconds + 1500);
            }));
        });
    }

    void SetFlash(IntPtr win, string text, long until)
    {
        flashWindow = win; flashText = text; flashUntil = until;
        Refresh2();
    }

    void Refresh2()
    {
        status.Icon = armed.Count == 0 ? idleIcon : armedIcon;
        status.Text = armed.Count == 0 ? "kakao-bomb" : "kakao-bomb — 장전됨 (" + armed.Count + ")";
        delayLabel.Text = delay.ToString("0.0") + "초";
        if (current == null) return;
        if (flashText != null && flashWindow == current.Window) bomb.Text = flashText;
        else bomb.Text = armed.Exists(a => a.Chat.Window == current.Window) ? "🔥 장전됨" : "💣 자폭";
    }
}

static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        N.SetProcessDPIAware();
        Log.On = Array.IndexOf(args, "--debug") >= 0;

        if (Array.IndexOf(args, "--dump") >= 0)
        {
            using (var g = Graphics.FromHwnd(IntPtr.Zero)) Deleter.Scale = (int)Math.Round(g.DpiX * 100 / 96);
            Directory.CreateDirectory(Log.Dir);
            File.WriteAllText(Path.Combine(Log.Dir, "dump.txt"), Deleter.DumpAll(), Encoding.UTF8);
            return;
        }

        if (Array.IndexOf(args, "--menushot") >= 0)
        {
            using (var g = Graphics.FromHwnd(IntPtr.Zero)) Deleter.Scale = (int)Math.Round(g.DpiX * 100 / 96);
            Directory.CreateDirectory(Log.Dir);
            string res;
            try { res = Deleter.MenuShot(); } catch (Exception ex) { res = ex.ToString(); }
            File.WriteAllText(Path.Combine(Log.Dir, "menushot.txt"), res, Encoding.UTF8);
            return;
        }

        bool first;
        using (new Mutex(true, "kakao-bomb-single-instance", out first))
        {
            if (!first) return;
            Application.EnableVisualStyles();
            var app = new App();
            var handle = app.Handle;  // 창을 띄우지 않고 핸들만 만듦 (단축키 수신용)
            Application.Run();
            GC.KeepAlive(app);
        }
    }
}
