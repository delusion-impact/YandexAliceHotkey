using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Automation;
using System.Windows.Automation.Text;

namespace YandexAliceHotkey
{
    /// <summary>
    /// Глобальный хоткей "grave/ё" (VK_OEM_3) для Яндекс Браузера.
    /// Сценарий: пользователь выделяет текст на странице и нажает ` -
    /// программно открывается контекстное меню (Shift+F10) и активируется
    /// пункт "Озвучить Алисой AI" через UI Automation.
    /// Клавиша НЕ перехватывается: без выделенного текста на странице
    /// (или при фокусе в адресной строке/полях ввода) символ печатается как обычно.
    /// </summary>
    internal static class Program
    {
        private const int WH_KEYBOARD_LL = 13;
        private const int VK_OEM_3 = 0xC0;   // ` (раскладка US) / ё (раскладка RU)
        private const int VK_SHIFT = 0x10;
        private const int VK_F10 = 0x79;
        private const int VK_ESCAPE = 0x1B;

        private const uint WM_KEYDOWN = 0x0100;
        private const uint WM_KEYUP = 0x0101;
        private const uint WM_SYSKEYDOWN = 0x0104;
        private const uint WM_SYSKEYUP = 0x0105;
        private const uint WM_QUIT = 0x0012;

        private const uint KEYEVENTF_KEYUP = 0x0002;
        private const uint PM_REMOVE = 0x0001;
        private const uint QS_ALLINPUT = 0x04FF;

        private static readonly UIntPtr NO_EXTRA = UIntPtr.Zero;

        // Класс главного окна Яндекс Браузера и имя его процесса.
        private const string WINDOW_CLASS_PREFIX = "Chrome_Yandex_WidgetWin_1";
        private const string BROWSER_PROCESS = "browser";

        // Имена пунктов контекстного меню (локализация ru).
        private const string ITEM_SPEAK = "Озвучить Алисой";
        private const string ITEM_STOP = "Остановить воспроизведение";

        private const int DEBOUNCE_MS = 1200;          // защита от автоповтора клавиши
        private const int MENU_TIMEOUT_MS = 3500;      // общее время ожидания меню
        private const int MENU_ITEM_GRACE_MS = 1300;   // доп. ожидание пункта после открытия меню

        private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

        [StructLayout(LayoutKind.Sequential)]
        private struct KBDLLHOOKSTRUCT
        {
            public uint vkCode;
            public uint scanCode;
            public uint flags;
            public uint time;
            public IntPtr dwExtraInfo;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT
        {
            public int x;
            public int y;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MSG
        {
            public IntPtr hwnd;
            public uint message;
            public IntPtr wParam;
            public IntPtr lParam;
            public uint time;
            public POINT pt;
        }

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool UnhookWindowsHookEx(IntPtr hhk);

        [DllImport("user32.dll")]
        private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

        [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern IntPtr GetModuleHandle(string lpModuleName);

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

        [DllImport("user32.dll")]
        private static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetCursorPos(int X, int Y);

        [DllImport("user32.dll")]
        private static extern void mouse_event(uint dwFlags, int dx, int dy, int dwData, UIntPtr dwExtraInfo);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetProcessDPIAware();

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool PeekMessage(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax, uint wRemoveMsg);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool TranslateMessage(ref MSG lpMsg);

        [DllImport("user32.dll")]
        private static extern IntPtr DispatchMessage(ref MSG lpMsg);

        [DllImport("user32.dll")]
        private static extern uint MsgWaitForMultipleObjects(uint nCount, IntPtr[] pHandles, bool bWaitAll, uint dwMilliseconds, uint dwWakeMask);

        private static IntPtr s_hook;
        private static LowLevelKeyboardProc s_hookProc;
        private static int s_keyDown;             // 1, пока физически зажата клавиша
        private static int s_busy;                // 1, пока идёт обработка
        private static long s_lastTriggerTicks;   // UtcNow.Ticks последнего запуска
        private static EventWaitHandle s_stopEvent;
        private static readonly object s_logLock = new object();
        private static string s_logPath;

        [STAThread]
        private static void Main(string[] args)
        {
            bool watch = false;
            for (int i = 0; i < args.Length; i++)
            {
                if (string.Equals(args[i], "--stop", StringComparison.OrdinalIgnoreCase))
                {
                    try
                    {
                        using (EventWaitHandle ev = EventWaitHandle.OpenExisting("YandexAliceHotkey.Stop"))
                        {
                            ev.Set();
                        }
                    }
                    catch { /* экземпляр не запущен */ }
                    return;
                }
                if (string.Equals(args[i], "--watch", StringComparison.OrdinalIgnoreCase))
                {
                    watch = true;
                }
            }

            bool createdNew;
            using (Mutex instance = new Mutex(false, "YandexAliceHotkey.Instance", out createdNew))
            {
                if (!createdNew) return; // уже запущен
                bool owned = false;
                try
                {
                    if (!instance.WaitOne(0)) return;
                    owned = true;
                    Run(watch);
                }
                finally
                {
                    if (owned)
                    {
                        try { instance.ReleaseMutex(); }
                        catch { }
                    }
                }
            }
        }

        private static void Run(bool watch)
        {
            try { SetProcessDPIAware(); }
            catch { }

            s_logPath = Path.Combine(Path.GetTempPath(), "yandex_alice_hotkey.log");
            s_stopEvent = new EventWaitHandle(false, EventResetMode.AutoReset, "YandexAliceHotkey.Stop");
            s_hookProc = HookProc;

            if (watch)
            {
                WatchLoop();
                return;
            }

            if (!InstallHook()) return;
            MessageLoop(false);
            RemoveHook();
        }

        private static bool InstallHook()
        {
            if (s_hook != IntPtr.Zero) return true;
            Interlocked.Exchange(ref s_keyDown, 0); // на случай, если клавиша зажата в момент установки
            s_hook = SetWindowsHookEx(WH_KEYBOARD_LL, s_hookProc, GetModuleHandle(null), 0);
            if (s_hook == IntPtr.Zero)
            {
                Log("ERROR: SetWindowsHookEx failed, err=" + Marshal.GetLastWin32Error());
                return false;
            }
            Log("hook installed; key VK_OEM_3 (` / ё) armed for " + WINDOW_CLASS_PREFIX);
            return true;
        }

        private static void RemoveHook()
        {
            if (s_hook == IntPtr.Zero) return;
            UnhookWindowsHookEx(s_hook);
            s_hook = IntPtr.Zero;
            Log("hook removed");
        }

        // ------------------------------------------------------------------
        // Режим --watch: ждём появления процесса browser, работаем, пока
        // браузер открыт, и возвращаемся в ожидание после его закрытия.
        // ------------------------------------------------------------------
        private static void WatchLoop()
        {
            Log("watch mode: waiting for browser process '" + BROWSER_PROCESS + "'");
            while (true)
            {
                if (s_stopEvent.WaitOne(500))
                {
                    Log("stop signal received");
                    RemoveHook();
                    return;
                }

                if (!BrowserRunning()) continue;

                Log("browser process detected");
                if (!InstallHook())
                {
                    return; // не удалось повесить хук - нет смысла ждать дальше
                }

                bool keepWatching = MessageLoop(true);
                RemoveHook();
                if (!keepWatching) return; // --stop или WM_QUIT

                Log("watch mode: browser closed, waiting again");
            }
        }

        private static bool BrowserRunning()
        {
            try { return Process.GetProcessesByName(BROWSER_PROCESS).Length > 0; }
            catch { return false; }
        }

        // ------------------------------------------------------------------
        // Цикл сообщений: возвращает false при --stop/WM_QUIT,
        // true - если watchBrowserExit и процесс browser исчез.
        // ------------------------------------------------------------------
        private static bool MessageLoop(bool watchBrowserExit)
        {
            IntPtr stopHandle = s_stopEvent.SafeWaitHandle.DangerousGetHandle();
            IntPtr[] handles = new IntPtr[] { stopHandle };
            DateTime lastProcCheck = DateTime.MinValue;

            while (true)
            {
                uint r = MsgWaitForMultipleObjects(1, handles, false, 1000, QS_ALLINPUT);
                if (r == 0) { Log("stop signal received"); return false; }
                if (r == 0xFFFFFFFF)
                {
                    Log("ERROR: MsgWaitForMultipleObjects failed, err=" + Marshal.GetLastWin32Error());
                    return false;
                }

                MSG msg;
                while (PeekMessage(out msg, IntPtr.Zero, 0, 0, PM_REMOVE))
                {
                    if (msg.message == WM_QUIT) return false;
                    TranslateMessage(ref msg);
                    DispatchMessage(ref msg);
                }

                if (watchBrowserExit && (DateTime.UtcNow - lastProcCheck).TotalSeconds >= 1.0)
                {
                    lastProcCheck = DateTime.UtcNow;
                    if (!BrowserRunning()) { Log("browser process exited"); return true; }
                }
            }
        }

        // ------------------------------------------------------------------
        // Low-level keyboard hook: только отслеживает нажатие, ничего не глотает.
        // ------------------------------------------------------------------
        private static IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam)
        {
            try
            {
                if (nCode >= 0)
                {
                    long m = wParam.ToInt64();
                    bool isDown = (m == WM_KEYDOWN || m == WM_SYSKEYDOWN);
                    bool isUp = (m == WM_KEYUP || m == WM_SYSKEYUP);
                    if (isDown || isUp)
                    {
                        KBDLLHOOKSTRUCT data = (KBDLLHOOKSTRUCT)Marshal.PtrToStructure(lParam, typeof(KBDLLHOOKSTRUCT));
                        if (data.vkCode == VK_OEM_3)
                        {
                            if (isUp)
                            {
                                Interlocked.Exchange(ref s_keyDown, 0);
                            }
                            else if (Interlocked.CompareExchange(ref s_keyDown, 1, 0) == 0)
                            {
                                // Только первое физическое нажатие (автоповтор игнорируется).
                                IntPtr fg = GetForegroundWindow();
                                string fgClass = "?";
                                try
                                {
                                    StringBuilder sb = new StringBuilder(256);
                                    if (GetClassName(fg, sb, sb.Capacity) != 0) fgClass = sb.ToString();
                                }
                                catch { }
                                bool debounce = InDebounce();
                                bool yandex = IsYandexWindow(fg);
                                int busy = s_busy;
                                Log("hook: VK_OEM_3 down, fg='" + fgClass + "' debounce=" + debounce +
                                    " yandex=" + yandex + " busy=" + busy);
                                if (!debounce && yandex && busy == 0 &&
                                    Interlocked.CompareExchange(ref s_busy, 1, 0) == 0)
                                {
                                    Thread worker = new Thread(DecideWorker);
                                    worker.IsBackground = true;
                                    worker.SetApartmentState(ApartmentState.STA);
                                    worker.Start();
                                }
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Log("hook error: " + ex.Message);
            }
            return CallNextHookEx(s_hook, nCode, wParam, lParam);
        }

        private static bool InDebounce()
        {
            long last = Interlocked.Read(ref s_lastTriggerTicks);
            if (last == 0) return false;
            return (DateTime.UtcNow.Ticks - last) < DEBOUNCE_MS * TimeSpan.TicksPerMillisecond;
        }

        private static void DecideWorker()
        {
            try { Decide(); }
            catch (Exception ex) { Log("decide error: " + ex.Message); }
            finally { Interlocked.Exchange(ref s_busy, 0); }
        }

        // ------------------------------------------------------------------
        // Решение: есть ли выделенный текст на странице Яндекс Браузера.
        // ------------------------------------------------------------------
        private static void Decide()
        {
            IntPtr fg = GetForegroundWindow();
            if (!IsYandexWindow(fg))
            {
                Log("decide: foreground window changed, ignore");
                return;
            }

            uint pid;
            GetWindowThreadProcessId(fg, out pid);
            try
            {
                Process p = Process.GetProcessById((int)pid);
                if (!string.Equals(p.ProcessName, BROWSER_PROCESS, StringComparison.OrdinalIgnoreCase))
                {
                    Log("decide: process '" + p.ProcessName + "' is not the browser, ignore");
                    return;
                }
            }
            catch (Exception ex)
            {
                Log("decide: process lookup failed: " + ex.Message);
                return;
            }

            // Chromium поднимает дерево доступности не сразу: первый вызов может
            // упасть с E_FAIL ("Нераспознанная ошибка"). Повторяем несколько раз.
            AutomationElement focused = null;
            string focusError = null;
            for (int attempt = 0; attempt < 3 && focused == null; attempt++)
            {
                try { focused = AutomationElement.FocusedElement; }
                catch (Exception ex)
                {
                    focusError = ex.Message;
                    Thread.Sleep(150);
                }
            }
            if (focused == null)
            {
                Log("decide: FocusedElement failed: " + focusError);
                return;
            }

            string preview;
            AutomationElement owner;
            if (TryGetSelection(focused, out preview))
            {
                owner = focused;
                Log("decide: selection found '" + preview + "' -> opening context menu");
            }
            else
            {
                // Фокус часто не на том документе, где лежит выделение: пользователь
                // выделяет текст внутри iframe, а активен внешний документ (или
                // наоборот). Проверяем все документы окна браузера.
                owner = null;
                AutomationElementCollection docs = null;
                try
                {
                    docs = AutomationElement.FromHandle(fg).FindAll(TreeScope.Descendants,
                        new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Document));
                }
                catch (Exception ex) { Log("decide: document scan failed: " + ex.Message); }

                if (docs != null)
                {
                    for (int i = 0; i < docs.Count; i++)
                    {
                        if (TryGetSelection(docs[i], out preview))
                        {
                            owner = docs[i];
                            Log("decide: selection found in doc[" + i + "] '" + preview + "'");
                            break;
                        }
                    }
                }

                if (owner == null)
                {
                    Log("decide: " + NoSelectionReason(focused) + ", pass through");
                    return;
                }
            }

            Trigger(fg, owner);
        }

        // ------------------------------------------------------------------
        // Выделенный текст элемента (только Document с TextPattern).
        // ------------------------------------------------------------------
        private static bool TryGetSelection(AutomationElement element, out string preview)
        {
            preview = string.Empty;
            if (element == null) return false;

            ControlType ct;
            try { ct = element.Current.ControlType; }
            catch { return false; }

            if (!ControlType.Document.Equals(ct)) return false;

            object pattern;
            if (!element.TryGetCurrentPattern(TextPattern.Pattern, out pattern)) return false;

            try
            {
                TextPattern tp = (TextPattern)pattern;
                TextPatternRange[] ranges = tp.GetSelection();
                for (int i = 0; i < ranges.Length; i++)
                {
                    string text = ranges[i].GetText(80);
                    if (!string.IsNullOrEmpty(text))
                    {
                        preview = text.Replace('\r', ' ').Replace('\n', ' ').Trim();
                        if (preview.Length > 60) preview = preview.Substring(0, 60) + "...";
                        return true;
                    }
                }
            }
            catch (Exception ex) { Log("decide: GetSelection failed: " + ex.Message); }

            return false;
        }

        // Причина, по которой выделение не найдено - для лога.
        private static string NoSelectionReason(AutomationElement focused)
        {
            try
            {
                ControlType ct = focused.Current.ControlType;
                if (ControlType.Document.Equals(ct)) return "selection is empty";
                return "focus is " + ct.ProgrammaticName + " (not page content)";
            }
            catch { return "selection is empty"; }
        }

        // ------------------------------------------------------------------
        // Открыть контекстное меню и активировать пункт озвучки.
        // ------------------------------------------------------------------
        private static void Trigger(IntPtr browserWindow, AutomationElement owner)
        {
            Interlocked.Exchange(ref s_lastTriggerTicks, DateTime.UtcNow.Ticks);

            // Shift+F10 открывает контекстное меню для текущего выделения.
            EnsureForeground(browserWindow);
            FocusOwner(owner);
            SendContextKeys();

            AutomationElement window;
            try { window = AutomationElement.FromHandle(browserWindow); }
            catch (Exception ex) { Log("trigger: FromHandle failed: " + ex.Message); return; }

            DateTime start = DateTime.UtcNow;
            DateTime lastSend = start;
            int sends = 1;
            bool sawMenu = false;
            DateTime firstMenuSeen = DateTime.MinValue;

            while ((DateTime.UtcNow - start).TotalMilliseconds < MENU_TIMEOUT_MS)
            {
                Thread.Sleep(70);

                AutomationElement speak = null;
                AutomationElement stop = null;
                int count = 0;
                try
                {
                    AutomationElementCollection items = window.FindAll(
                        TreeScope.Descendants,
                        new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.MenuItem));
                    count = items.Count;
                    for (int i = 0; i < items.Count; i++)
                    {
                        string name;
                        try { name = items[i].Current.Name; }
                        catch { continue; }
                        if (name.IndexOf(ITEM_SPEAK, StringComparison.Ordinal) >= 0) speak = items[i];
                        else if (name.IndexOf(ITEM_STOP, StringComparison.Ordinal) >= 0) stop = items[i];
                    }
                }
                catch (Exception ex)
                {
                    Log("trigger: menu poll failed: " + ex.Message);
                    continue;
                }

                if (count > 0 && !sawMenu)
                {
                    sawMenu = true;
                    firstMenuSeen = DateTime.UtcNow;
                }

                AutomationElement target = (speak != null) ? speak : stop;
                if (target != null)
                {
                    bool enabled = false;
                    try { enabled = target.Current.IsEnabled; }
                    catch { }
                    if (enabled)
                    {
                        string name = "";
                        try { name = target.Current.Name; }
                        catch { }
                        Log("trigger: invoking '" + name + "'");
                        if (InvokeItem(target)) return;
                        Log("trigger: invoke failed");
                    }
                }

                // Меню не появилось - повторяем Shift+F10 (надёжность доставки).
                if (!sawMenu && sends < 3 &&
                    (DateTime.UtcNow - lastSend).TotalMilliseconds > 900)
                {
                    EnsureForeground(browserWindow);
                    FocusOwner(owner);
                    SendContextKeys();
                    sends++;
                    lastSend = DateTime.UtcNow;
                    Log("trigger: menu not seen yet, Shift+F10 resend #" + sends);
                }

                if (sawMenu && (DateTime.UtcNow - firstMenuSeen).TotalMilliseconds > MENU_ITEM_GRACE_MS)
                {
                    Log("trigger: menu opened (" + count + " items) but the target item is missing, closing menu");
                    SendEscape();
                    return;
                }
            }

            if (sawMenu)
            {
                Log("trigger: timeout, closing menu");
                SendEscape();
            }
            else
            {
                Log("trigger: context menu did not open (" + sends + " sends)");
            }
        }

        // Последовательность Shift+F10 с короткими паузами - так надёжнее,
        // чем мгновенная отправка всех клавиш одним блоком.
        private static void SendContextKeys()
        {
            keybd_event((byte)VK_SHIFT, 0, 0, NO_EXTRA);
            Thread.Sleep(15);
            keybd_event((byte)VK_F10, 0, 0, NO_EXTRA);
            Thread.Sleep(15);
            keybd_event((byte)VK_F10, 0, KEYEVENTF_KEYUP, NO_EXTRA);
            Thread.Sleep(15);
            keybd_event((byte)VK_SHIFT, 0, KEYEVENTF_KEYUP, NO_EXTRA);
        }

        private static void EnsureForeground(IntPtr hwnd)
        {
            try
            {
                if (GetForegroundWindow() != hwnd)
                {
                    SetForegroundWindow(hwnd);
                    Thread.Sleep(50);
                }
            }
            catch { }
        }

        // Перевод фокуса в документ, где реально лежит выделение: иначе Shift+F10
        // откроет меню для постороннего элемента (например, для внешнего
        // документа, пока выделение внутри iframe).
        private static void FocusOwner(AutomationElement owner)
        {
            if (owner == null) return;
            try
            {
                if (!owner.Current.HasKeyboardFocus)
                {
                    owner.SetFocus();
                    Thread.Sleep(120);
                }
            }
            catch (Exception ex) { Log("trigger: SetFocus failed: " + ex.Message); }
        }

        private static bool InvokeItem(AutomationElement item)
        {
            object pattern;
            try
            {
                if (item.TryGetCurrentPattern(InvokePattern.Pattern, out pattern))
                {
                    ((InvokePattern)pattern).Invoke();
                    return true;
                }
            }
            catch (Exception ex)
            {
                Log("trigger: InvokePattern error: " + ex.Message);
            }

            // Запасной вариант: настоящий клик по центру пункта меню.
            try
            {
                System.Windows.Rect rect = item.Current.BoundingRectangle;
                if (rect.Width <= 0 || rect.Height <= 0) return false;
                int x = (int)(rect.X + rect.Width / 2);
                int y = (int)(rect.Y + rect.Height / 2);
                SetCursorPos(x, y);
                Thread.Sleep(60);
                mouse_event(0x0002, 0, 0, 0, NO_EXTRA); // left down
                mouse_event(0x0004, 0, 0, 0, NO_EXTRA); // left up
                return true;
            }
            catch (Exception ex)
            {
                Log("trigger: click fallback failed: " + ex.Message);
                return false;
            }
        }

        private static void SendEscape()
        {
            keybd_event((byte)VK_ESCAPE, 0, 0, NO_EXTRA);
            keybd_event((byte)VK_ESCAPE, 0, KEYEVENTF_KEYUP, NO_EXTRA);
        }

        private static bool IsYandexBrowserForeground()
        {
            return IsYandexWindow(GetForegroundWindow());
        }

        private static bool IsYandexWindow(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero) return false;
            StringBuilder sb = new StringBuilder(256);
            if (GetClassName(hwnd, sb, sb.Capacity) == 0) return false;
            return sb.ToString().StartsWith(WINDOW_CLASS_PREFIX, StringComparison.Ordinal);
        }

        private static void Log(string message)
        {
            try
            {
                lock (s_logLock)
                {
                    File.AppendAllText(s_logPath,
                        DateTime.Now.ToString("HH:mm:ss.fff") + " " + message + Environment.NewLine);
                }
            }
            catch { }
        }
    }
}
