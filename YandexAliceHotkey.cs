using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Text;
using System.Threading;
using System.Drawing;
using System.Drawing.Imaging;
using System.Windows.Automation;
using System.Windows.Automation.Text;
using Windows.Globalization;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Storage.Streams;

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
        private const int WH_MOUSE_LL = 14;
        private const int VK_OEM_3 = 0xC0;   // ` (раскладка US) / ё (раскладка RU)
        private const int VK_SHIFT = 0x10;
        private const int VK_LSHIFT = 0xA0;
        private const int VK_CONTROL = 0x11;
        private const int VK_LCONTROL = 0xA2;
        private const int VK_RCONTROL = 0xA3;
        private const int VK_F10 = 0x79;
        private const int VK_ESCAPE = 0x1B;

        private const uint WM_KEYDOWN = 0x0100;
        private const uint WM_KEYUP = 0x0101;
        private const uint WM_SYSKEYDOWN = 0x0104;
        private const uint WM_SYSKEYUP = 0x0105;
        private const uint WM_QUIT = 0x0012;

        private const uint KEYEVENTF_KEYUP = 0x0002;
        private const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
        private const uint MOUSEEVENTF_LEFTUP = 0x0004;
        private const uint MOUSEEVENTF_MOVE = 0x0001;
        private const uint MOUSEEVENTF_WHEEL = 0x0800;
        private const uint MOUSEEVENTF_RIGHTDOWN = 0x0008;
        private const uint MOUSEEVENTF_RIGHTUP = 0x0010;
        private const uint MOUSEEVENTF_VIRTUALDESK = 0x4000;
        private const uint MOUSEEVENTF_ABSOLUTE = 0x8000;
        private const uint PM_REMOVE = 0x0001;
        private const uint QS_ALLINPUT = 0x04FF;

        // Сообщения мыши для низкоуровневого хука: ручное нажатие кнопки или
        // колесо во время автоматического чтения считается остановкой цикла.
        private const uint WM_LBUTTONDOWN = 0x0201;
        private const uint WM_RBUTTONDOWN = 0x0204;
        private const uint WM_MBUTTONDOWN = 0x0207;
        private const uint WM_MOUSEWHEEL = 0x020A;
        private const uint WM_XBUTTONDOWN = 0x020B;
        private const uint LLMHF_INJECTED = 0x00000001;

        private static readonly UIntPtr NO_EXTRA = UIntPtr.Zero;

        // Класс главного окна Яндекс Браузера и имя его процесса.
        private const string WINDOW_CLASS_PREFIX = "Chrome_Yandex_WidgetWin_1";
        private const string BROWSER_PROCESS = "browser";

        // Версия сборки - для сопоставления строк лога с конкретным бинарником.
        private const string APP_VERSION = "2.8.0";
        private const int MAX_PAGE_TEXT_CHARS = 12000;

        // Имена пунктов контекстного меню (локализация ru).
        private const string ITEM_SPEAK = "Озвучить Алисой";
        private const string ITEM_STOP = "Остановить воспроизведение";

        private const int DEBOUNCE_MS = 1200;          // защита от автоповтора клавиши
        private const int MENU_TIMEOUT_MS = 3500;      // общее время ожидания меню
        private const int MENU_ITEM_GRACE_MS = 1300;   // доп. ожидание пункта после открытия меню
        private const int MENU_ATTEMPTS = 3;           // попыток открыть «правильное» меню
        private const int MENU_RETRY_DELAY_MS = 250;   // пауза между попытками

        // Ожидание отрисовки страницы: после перелистывания просмотрщик PDF
        // иногда перерисовывает следующую страницу не сразу, поэтому выделение
        // может не найти видимую страницу с первой попытки.
        private const int PAGE_SELECT_ATTEMPTS = 4;
        private const int PAGE_SELECT_RETRY_DELAY_MS = 900;
        private const int NOTIFY_TIMEOUT_MS = 6000;

        // Перелистывание. Встроенный PDF-просмотрщик не принимает
        // синтезированные клавиши (ArrowRight/PageDown/ArrowDown до него не
        // доходят), поэтому вид прокручивается колесом мыши над листом. После
        // автопрокрутки Алисы лист часто стоит на границе двух страниц:
        // колесо доводит видимый лист до полного попадания в окно.
        private const double PAGE_TOP_MAX_RATIO = 0.25;      // верх листа не ниже 25% высоты окна
        private const double PAGE_TOP_MIN_RATIO = 0.10;      // и не выше 10% (иначе лист ушёл вверх)
        private const int PAGE_ALIGN_MAX_STEPS = 40;         // макс. шагов колеса на выравнивание
        private const int PAGE_ALIGN_STEP_DELAY_MS = 110;    // пауза после шага колеса
        private const int PAGE_TURN_ATTEMPTS = 2;            // попыток перелистнуть неизменившуюся страницу
        private const int WHEEL_COARSE_DELTA = -120;         // один «щелчок» колеса вниз
        private const int WHEEL_FINE_DELTA = -40;            // мелкий шаг для точного выравнивания

        private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

        private delegate IntPtr LowLevelMouseProc(int nCode, IntPtr wParam, IntPtr lParam);

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
        private struct MSLLHOOKSTRUCT
        {
            public POINT pt;
            public uint mouseData;
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
        private struct RECT
        {
            public int left;
            public int top;
            public int right;
            public int bottom;
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
        private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelMouseProc lpfn, IntPtr hMod, uint dwThreadId);

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

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetClientRect(IntPtr hWnd, out RECT lpRect);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool ClientToScreen(IntPtr hWnd, ref POINT lpPoint);

        [DllImport("user32.dll")]
        private static extern int GetSystemMetrics(int nIndex);

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
        private static IntPtr s_mouseHook;
        private static LowLevelKeyboardProc s_hookProc;
        private static LowLevelMouseProc s_mouseHookProc;
        private static int s_keyDown;             // 1, пока физически зажата клавиша
        private static int s_ctrlDown;
        private static int s_ctrlAloneCandidate;
        private static int s_leftShiftDown;
        private static int s_leftShiftAloneCandidate;
        private static int s_autoReading;
        private static int s_autoReadStopRequest; // 1, если пользователь вмешался вручную
        private static int s_busy;                // 1, пока идёт обработка
        private static long s_lastTriggerTicks;   // UtcNow.Ticks последнего запуска
        private static EventWaitHandle s_stopEvent;
        private static readonly object s_logLock = new object();
        private static string s_logPath;
        private static bool s_profileOnly;
        private static OcrEngine s_ocrEngine;
        private static readonly object s_ocrLock = new object();
        // Высота полностью видимого листа PDF (0 - ещё не измерена). Нужна,
        // чтобы не принять обрезанный лист за выровненный.
        private static double s_pageHeightPx;

        private sealed class OcrTextLine
        {
            public string Text;
            public System.Windows.Rect Bounds;
        }

        private enum PlaybackState
        {
            Unknown,
            Playing,
            Idle
        }

        [STAThread]
        private static void Main(string[] args)
        {
            bool watch = false;
            bool readPageOnce = false;
            bool profilePageOnce = false;
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
                if (string.Equals(args[i], "--read-page-once", StringComparison.OrdinalIgnoreCase))
                {
                    readPageOnce = true;
                }
                if (string.Equals(args[i], "--profile-page-once", StringComparison.OrdinalIgnoreCase))
                {
                    readPageOnce = true;
                    profilePageOnce = true;
                }
            }

            if (readPageOnce)
            {
                try { SetProcessDPIAware(); }
                catch { }
                s_logPath = Path.Combine(Path.GetTempPath(), "yandex_alice_hotkey.log");
                s_profileOnly = profilePageOnce;
                Log("manual test: reading current visible page once");
                try { Decide(true); }
                catch (Exception ex) { Log("manual test failed: " + ex.Message); }
                return;
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
            // Низкоуровневый хук мыши нужен, чтобы во время автоматического
            // чтения считать ручное вмешательство пользователя остановкой цикла.
            s_mouseHookProc = MouseHookProc;
            s_mouseHook = SetWindowsHookEx(WH_MOUSE_LL, s_mouseHookProc, GetModuleHandle(null), 0);
            if (s_mouseHook == IntPtr.Zero)
                Log("WARN: mouse hook not installed, err=" + Marshal.GetLastWin32Error());
            Log("hook installed; key VK_OEM_3 (` / ё) armed for " + WINDOW_CLASS_PREFIX +
                " (v" + APP_VERSION + "); LShift = auto read cycle");
            return true;
        }

        private static void RemoveHook()
        {
            if (s_mouseHook != IntPtr.Zero)
            {
                UnhookWindowsHookEx(s_mouseHook);
                s_mouseHook = IntPtr.Zero;
                s_mouseHookProc = null;
            }
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
                        // Ignore keys synthesized by this app while selecting text or opening menus.
                        if ((data.flags & 0x10) != 0) return CallNextHookEx(s_hook, nCode, wParam, lParam);
                        bool isControl = data.vkCode == VK_CONTROL || data.vkCode == VK_LCONTROL || data.vkCode == VK_RCONTROL;
                        bool isLeftShift = data.vkCode == VK_LSHIFT;
                        // Любая физическая клавиша во время автоматического
                        // чтения - это ручное вмешательство: цикл останавливается.
                        // Сам левый Shift служит штатной остановкой-переключателем.
                        if (isDown && !isLeftShift) NoteManualActivity("keyboard");
                        if (isControl)
                        {
                            if (isDown && Interlocked.CompareExchange(ref s_ctrlDown, 1, 0) == 0)
                            {
                                Interlocked.Exchange(ref s_ctrlAloneCandidate, 1);
                            }
                            else if (isUp && Interlocked.Exchange(ref s_ctrlDown, 0) == 1)
                            {
                                if (Interlocked.Exchange(ref s_ctrlAloneCandidate, 0) == 1)
                                    StartTrigger(true, "Ctrl");
                            }
                        }
                        else if (isLeftShift)
                        {
                            if (isDown && Interlocked.CompareExchange(ref s_leftShiftDown, 1, 0) == 0)
                            {
                                Interlocked.Exchange(ref s_leftShiftAloneCandidate, 1);
                            }
                            else if (isUp && Interlocked.Exchange(ref s_leftShiftDown, 0) == 1)
                            {
                                if (Interlocked.Exchange(ref s_leftShiftAloneCandidate, 0) == 1)
                                    ToggleAutoReading();
                            }
                        }
                        else if (isDown)
                        {
                            if (Interlocked.CompareExchange(ref s_ctrlDown, 0, 0) == 1)
                                Interlocked.Exchange(ref s_ctrlAloneCandidate, 0);
                            if (Interlocked.CompareExchange(ref s_leftShiftDown, 0, 0) == 1)
                                Interlocked.Exchange(ref s_leftShiftAloneCandidate, 0);
                        }
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
                                bool bookMode = false;
                                int busy = s_busy;
                                Log("hook: VK_OEM_3 down, fg='" + fgClass + "' debounce=" + debounce +
                                    " bookMode=" + bookMode +
                                    " yandex=" + yandex + " busy=" + busy);
                                if (!debounce && yandex && busy == 0) StartTrigger(bookMode, "ё");
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

        // ------------------------------------------------------------------
        // Low-level mouse hook: ручное нажатие кнопки мыши или колесо во время
        // автоматического чтения останавливает цикл. Синтезированные этим
        // приложением события (выделение, контекстное меню) имеют флаг injected
        // и игнорируются.
        // ------------------------------------------------------------------
        private static IntPtr MouseHookProc(int nCode, IntPtr wParam, IntPtr lParam)
        {
            try
            {
                if (nCode >= 0 && Interlocked.CompareExchange(ref s_autoReading, 0, 0) == 1)
                {
                    MSLLHOOKSTRUCT data = (MSLLHOOKSTRUCT)Marshal.PtrToStructure(lParam, typeof(MSLLHOOKSTRUCT));
                    if ((data.flags & LLMHF_INJECTED) == 0)
                    {
                        long m = wParam.ToInt64();
                        if (m == WM_LBUTTONDOWN || m == WM_RBUTTONDOWN || m == WM_MBUTTONDOWN ||
                            m == WM_XBUTTONDOWN || m == WM_MOUSEWHEEL)
                            NoteManualActivity("mouse");
                    }
                }
            }
            catch (Exception ex)
            {
                Log("mouse hook error: " + ex.Message);
            }
            return CallNextHookEx(s_mouseHook, nCode, wParam, lParam);
        }

        // Единая точка «пользователь вмешался вручную». И окончание чтения, и
        // ручная остановка через меню выглядят одинаково (снова виден пункт
        // «Озвучить Алисой»), поэтому в автоматическом режиме любое ручное
        // действие считается остановкой всего цикла, а не переходом дальше.
        private static void NoteManualActivity(string source)
        {
            if (Interlocked.CompareExchange(ref s_autoReading, 0, 0) != 1) return;
            if (Interlocked.Exchange(ref s_autoReadStopRequest, 1) != 1)
                Log("auto read: manual activity detected (" + source + "); stopping the cycle");
        }

        private static void StartTrigger(bool bookMode, string keyName)
        {
            IntPtr fg = GetForegroundWindow();
            bool yandex = IsYandexWindow(fg);
            bool debounce = InDebounce();
            if (debounce || !yandex || Interlocked.CompareExchange(ref s_busy, 1, 0) != 0) return;
            Log("hotkey: " + keyName + " bookMode=" + bookMode);
            Thread worker = new Thread(delegate() { DecideWorker(bookMode); });
            worker.IsBackground = true;
            worker.SetApartmentState(ApartmentState.STA);
            worker.Start();
        }

        // Левый Shift запускает или останавливает последовательное чтение PDF.
        // Повторное одиночное нажатие всегда служит безопасной остановкой цикла.
        private static void ToggleAutoReading()
        {
            if (Interlocked.CompareExchange(ref s_autoReading, 0, 1) == 1)
            {
                Interlocked.Exchange(ref s_autoReadStopRequest, 1);
                Log("auto read: stop requested by Left Shift");
                return;
            }

            IntPtr browser = GetForegroundWindow();
            if (!IsYandexWindow(browser))
            {
                Log("auto read: Left Shift ignored because Yandex Browser is not foreground");
                return;
            }
            if (Interlocked.CompareExchange(ref s_busy, 1, 0) != 0)
            {
                Log("auto read: Left Shift ignored because another operation is running");
                return;
            }

            Interlocked.Exchange(ref s_autoReadStopRequest, 0);
            Interlocked.Exchange(ref s_autoReading, 1);
            s_pageHeightPx = 0;   // высота листа измеряется заново в этом сеансе
            Log("auto read: started by Left Shift");
            Thread worker = new Thread(delegate() { AutoReadWorker(browser); });
            worker.IsBackground = true;
            worker.SetApartmentState(ApartmentState.STA);
            worker.Start();
        }

        // Цикл продолжается только пока не запрошена остановка: её выставляет
        // либо повторный Left Shift, либо ручное действие пользователя.
        private static bool IsAutoReading()
        {
            if (Interlocked.CompareExchange(ref s_autoReadStopRequest, 0, 0) == 1) return false;
            return Interlocked.CompareExchange(ref s_autoReading, 0, 0) == 1;
        }

        // Полный цикл автоматического чтения:
        //   выделить видимую страницу -> запустить Алису -> дождаться конца
        //   воспроизведения (по контекстному меню) -> выровнять/перелистнуть
        //   лист колесом мыши -> подтвердить по OCR, что текст сменился ->
        //   выделить следующую -> повторить.
        // Любая аварийная остановка сопровождается понятным сообщением, чтобы
        // режим не выглядел «зависшим».
        private static void AutoReadWorker(IntPtr browserWindow)
        {
            string stopReason = null;   // null => остановка по запросу пользователя
            try
            {
                int page = 0;
                string previousSignature = null;
                System.Windows.Point? wheelPoint = null;   // точка над листом для колеса мыши
                while (IsAutoReading())
                {
                    if (GetForegroundWindow() != browserWindow || !IsYandexWindow(browserWindow))
                    {
                        stopReason = "окно Яндекс Браузера перестало быть активным";
                        break;
                    }

                    // После автопрокрутки Алисы лист может стоять на границе
                    // двух страниц (вид «завис на половине»). Выравниваем
                    // видимый лист колесом мыши: клавиши до встроенного
                    // PDF-просмотрщика не доходят.
                    if (previousSignature != null)
                        AlignVisiblePage(browserWindow, wheelPoint);

                    System.Windows.Point? contextPoint;
                    string signature;
                    if (!TrySelectPageForAutoRead(browserWindow, out contextPoint, out signature))
                    {
                        if (IsAutoReading())
                        {
                            stopReason = "не удалось надёжно выделить текст на текущей странице";
                            LogPageMarker(browserWindow, "could not select page text");
                        }
                        break;
                    }

                    // Если текст тот же, что читали в прошлый раз, просмотрщик
                    // не перешёл на следующую страницу (Алиса не прокрутила
                    // вид). Перелистываем колесом и проверяем снова.
                    if (previousSignature != null && signature == previousSignature)
                    {
                        bool turned = false;
                        for (int attempt = 1; attempt <= PAGE_TURN_ATTEMPTS && IsAutoReading(); attempt++)
                        {
                            Log("auto read: page text unchanged; turning the page by wheel (attempt " +
                                attempt + "/" + PAGE_TURN_ATTEMPTS + ")");
                            TurnPageByWheel(browserWindow, contextPoint);
                            AlignVisiblePage(browserWindow, contextPoint);
                            if (!TrySelectPageForAutoRead(browserWindow, out contextPoint, out signature))
                                break;
                            if (signature != previousSignature) { turned = true; break; }
                        }
                        if (!turned)
                        {
                            if (IsAutoReading())
                            {
                                stopReason = "текст страницы после перелистывания не изменился " +
                                    "(похоже, достигнут конец документа)";
                                LogPageMarker(browserWindow, "page text did not change");
                            }
                            break;
                        }
                    }
                    previousSignature = signature;
                    wheelPoint = contextPoint;
                    if (!IsAutoReading()) break;

                    page++;
                    Log("auto read: page " + page + " selected; starting playback");
                    if (!Trigger(browserWindow, null, false, contextPoint))
                    {
                        if (IsAutoReading())
                        {
                            stopReason = "не удалось запустить озвучку (страница с картинкой или графиком?)";
                            LogPageMarker(browserWindow, "could not start playback");
                        }
                        break;
                    }
                    if (!IsAutoReading()) break;

                    bool startedPlaying;
                    if (!WaitForPlaybackToFinish(browserWindow, contextPoint, out startedPlaying))
                    {
                        if (IsAutoReading())
                        {
                            if (startedPlaying) stopReason = "озвучка не завершилась (превышено время ожидания)";
                            else stopReason = "не удалось запустить озвучку (страница с картинкой или графиком?)";
                            if (!startedPlaying) LogPageMarker(browserWindow, "playback did not start");
                        }
                        break;
                    }
                    Log("auto read: playback finished; aligning the visible page before the next one");
                }
            }
            catch (Exception ex)
            {
                stopReason = "внутренняя ошибка: " + ex.Message;
                Log("auto read failed: " + ex.Message);
            }
            finally
            {
                Interlocked.Exchange(ref s_autoReading, 0);
                Interlocked.Exchange(ref s_autoReadStopRequest, 0);
                Interlocked.Exchange(ref s_busy, 0);
                if (stopReason != null)
                {
                    // Аварийная остановка: сообщаем пользователю причину,
                    // чтобы было видно, что цикл завершился, а не завис.
                    Log("auto read: stopped — " + stopReason);
                    NotifyUser("Авточтение остановлено", stopReason);
                }
                else
                {
                    Log("auto read: stopped by user");
                }
                Log("auto read: stopped");
            }
        }

        // Выделение текущей страницы для цикла: несколько попыток OCR с
        // паузами (просмотрщик PDF может отрисовывать страницу не мгновенно),
        // затем резервный путь через UI Automation для страниц, где OCR не
        // находит белую область (крупные изображения, нестандартный макет).
        private static bool TrySelectPageForAutoRead(IntPtr browserWindow,
            out System.Windows.Point? contextPoint, out string signature)
        {
            contextPoint = null;
            signature = null;

            for (int attempt = 1; attempt <= PAGE_SELECT_ATTEMPTS; attempt++)
            {
                System.Windows.Point? point;
                string sig;
                if (SelectCurrentPageByOcr(browserWindow, out point, out sig))
                {
                    contextPoint = point;
                    signature = sig;
                    return true;
                }
                if (!IsAutoReading()) return false;
                if (attempt < PAGE_SELECT_ATTEMPTS)
                {
                    Log("auto read: page selection attempt " + attempt + "/" + PAGE_SELECT_ATTEMPTS +
                        " failed; retrying after " + PAGE_SELECT_RETRY_DELAY_MS + " ms");
                    Thread.Sleep(PAGE_SELECT_RETRY_DELAY_MS);
                }
            }

            if (!IsAutoReading()) return false;
            Log("auto read: OCR could not select the page; falling back to UI Automation");
            try
            {
                AutomationElement focused = null;
                try { focused = AutomationElement.FocusedElement; }
                catch { }
                AutomationElement document = FindFocusedDocument(browserWindow, focused);
                System.Windows.Point? point;
                if (document != null && SelectDocumentText(document, browserWindow, out point))
                {
                    string preview;
                    signature = TryGetSelection(document, out preview) ? "uia:" + preview : null;
                    contextPoint = point;
                    return true;
                }
            }
            catch (Exception ex) { Log("auto read: UIA fallback failed: " + ex.Message); }
            return false;
        }

        private static bool InDebounce()
        {
            long last = Interlocked.Read(ref s_lastTriggerTicks);
            if (last == 0) return false;
            return (DateTime.UtcNow.Ticks - last) < DEBOUNCE_MS * TimeSpan.TicksPerMillisecond;
        }

        private static void DecideWorker(bool bookMode)
        {
            try { Decide(bookMode); }
            catch (Exception ex) { Log("decide error: " + ex.Message); }
            finally { Interlocked.Exchange(ref s_busy, 0); }
        }

        // ------------------------------------------------------------------
        // Решение: есть ли выделенный текст на странице Яндекс Браузера.
        // ------------------------------------------------------------------
        private static void Decide(bool bookMode)
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

            // Book mode does not need to inspect the current text selection.
            // Go straight to the visible page area to avoid slow PDF TextPattern calls.
            if (bookMode)
            {
                System.Windows.Point? contextPoint;
                string ignoredSignature;
                if (SelectCurrentPageByOcr(fg, out contextPoint, out ignoredSignature))
                {
                    Log("decide: book mode selected the visible page area");
                    if (s_profileOnly) Log("profile mode: skipped opening Alice menu");
                    else Trigger(fg, null, false, contextPoint);
                }
                else
                {
                    Log("book mode: OCR did not find a safe page selection; falling back to UIA");
                    AutomationElement focused = null;
                    try { focused = AutomationElement.FocusedElement; }
                    catch { }
                    AutomationElement bookDocument = FindFocusedDocument(fg, focused);
                    if (bookDocument != null && SelectDocumentText(bookDocument, fg, out contextPoint))
                    {
                        Log("decide: fallback UIA selected the visible page area");
                        if (s_profileOnly) Log("profile mode: skipped opening Alice menu");
                        else Trigger(fg, bookDocument, false, contextPoint);
                    }
                    else Log("decide: book mode could not select the visible page area");
                }
                return;
            }

            // После перезапуска Chromium иногда сначала отдаёт дерево UIA без
            // выделения. Повторно читаем фокус и документы, чтобы получить
            // свежие TextPatternRange из обновлённого дерева.
            const int selectionAttempts = 5;
            const int selectionRetryDelayMs = 180;
            AutomationElement owner = null;
            AutomationElement lastFocused = null;
            string preview = string.Empty;
            string focusError = null;
            int lastDocumentCount = -1;
            bool foundInFocused = false;

            for (int attempt = 0; attempt < selectionAttempts && owner == null; attempt++)
            {
                try { lastFocused = AutomationElement.FocusedElement; }
                catch (Exception ex)
                {
                    focusError = ex.Message;
                    lastFocused = null;
                }

                if (lastFocused != null && TryGetSelection(lastFocused, out preview))
                {
                    owner = lastFocused;
                    foundInFocused = true;
                    break;
                }

                // Выделение может находиться в iframe/внутреннем Document,
                // отличном от текущего focused element. Каждый раз перечитываем
                // браузерное UIA дерево, а не используем ранее сохранённый список.
                try
                {
                    AutomationElement root = AutomationElement.FromHandle(fg);
                    AutomationElementCollection docs = root.FindAll(TreeScope.Descendants,
                        new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Document));
                    lastDocumentCount = docs.Count;
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
                catch (Exception ex) { Log("decide: document scan failed: " + ex.Message); }

                if (owner == null && attempt + 1 < selectionAttempts)
                    Thread.Sleep(selectionRetryDelayMs);
            }

            if (owner == null)
            {
                string reason = lastFocused == null
                    ? "FocusedElement unavailable: " + (focusError ?? "no element")
                    : NoSelectionReason(lastFocused);
                Log("decide: no selection after " + selectionAttempts +
                    " fresh UIA scans (documents=" + lastDocumentCount + ", " + reason + "), pass through");

                // The built-in PDF viewer can visibly select text while exposing
                // an empty TextPattern selection. For a focused Document only,
                // use the context menu itself as the selection check: the Alice
                // action is invoked only if the selected-text menu item exists.
                if (IsDocumentElement(lastFocused))
                {
                    Log("decide: probing PDF context menu because UIA selection is unavailable");
                    Trigger(fg, lastFocused, true);
                }
                return;
            }

            if (foundInFocused)
                Log("decide: selection found '" + preview + "' -> opening context menu");

            Trigger(fg, owner, false);
        }

        private static AutomationElement FindFocusedDocument(IntPtr fg, AutomationElement focused)
        {
            AutomationElement current = focused;
            for (int depth = 0; current != null && depth < 16; depth++)
            {
                if (IsDocumentElement(current)) return current;
                try { current = TreeWalker.ControlViewWalker.GetParent(current); }
                catch { break; }
            }
            try
            {
                AutomationElement root = AutomationElement.FromHandle(fg);
                AutomationElementCollection docs = root.FindAll(TreeScope.Descendants,
                    new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Document));
                AutomationElement bestDocument = null;
                double bestArea = 0;
                for (int i = 0; i < docs.Count; i++)
                {
                    try
                    {
                        if (docs[i].Current.HasKeyboardFocus) return docs[i];
                        System.Windows.Rect bounds = docs[i].Current.BoundingRectangle;
                        if (bounds.IsEmpty || bounds.Width <= 0 || bounds.Height <= 0) continue;
                        object pattern;
                        if (!docs[i].TryGetCurrentPattern(TextPattern.Pattern, out pattern)) continue;
                        double area = bounds.Width * bounds.Height;
                        if (area > bestArea)
                        {
                            bestDocument = docs[i];
                            bestArea = area;
                        }
                    }
                    catch { }
                }
                if (bestDocument != null) Log("book mode: chose visible document by viewport area=" + bestArea);
                return bestDocument;
            }
            catch (Exception ex) { Log("book mode: document scan failed: " + ex.Message); }
            return null;
        }

        private static bool SelectCurrentPageByOcr(IntPtr browserWindow, out System.Windows.Point? contextPoint,
            out string pageSignature)
        {
            contextPoint = null;
            pageSignature = null;
            Stopwatch total = Stopwatch.StartNew();
            Stopwatch phase = Stopwatch.StartNew();
            System.Windows.Rect client;
            if (!GetClientScreenBounds(browserWindow, out client) || client.Width < 400 || client.Height < 400)
            {
                Log("book OCR: browser client viewport unavailable or too small");
                return false;
            }

            try
            {
                using (Bitmap bitmap = new Bitmap((int)Math.Round(client.Width), (int)Math.Round(client.Height), PixelFormat.Format32bppArgb))
                {
                    using (Graphics graphics = Graphics.FromImage(bitmap))
                    {
                        graphics.CopyFromScreen((int)Math.Round(client.Left), (int)Math.Round(client.Top),
                            0, 0, bitmap.Size, CopyPixelOperation.SourceCopy);
                    }
                    Log("book OCR timing: viewport capture elapsedMs=" + phase.ElapsedMilliseconds);
                    phase.Restart();

                    System.Windows.Rect pageBounds;
                    if (!TryFindWhitePageBounds(bitmap, out pageBounds))
                    {
                        Log("book OCR: could not locate the visible white PDF page");
                        return false;
                    }
                    Log("book OCR timing: page bounds=" + pageBounds + " elapsedMs=" + phase.ElapsedMilliseconds);
                    RememberPageHeight(pageBounds, client.Height);
                    phase.Restart();

                    OcrEngine engine = GetOcrEngine();
                    if (engine == null)
                    {
                        Log("book OCR: Russian OCR language is unavailable");
                        return false;
                    }
                    using (MemoryStream stream = new MemoryStream())
                    {
                        bitmap.Save(stream, ImageFormat.Png);
                        stream.Position = 0;
                        IRandomAccessStream randomAccess = stream.AsRandomAccessStream();
                        BitmapDecoder decoder = BitmapDecoder.CreateAsync(randomAccess).AsTask().GetAwaiter().GetResult();
                        SoftwareBitmap softwareBitmap = decoder.GetSoftwareBitmapAsync().AsTask().GetAwaiter().GetResult();
                        OcrResult result = engine.RecognizeAsync(softwareBitmap).AsTask().GetAwaiter().GetResult();
                        Log("book OCR timing: recognize lines=" + result.Lines.Count + " elapsedMs=" + phase.ElapsedMilliseconds);

                        List<OcrTextLine> lines = GetOcrPageLines(result, pageBounds);
                        if (lines.Count < 2)
                        {
                            Log("book OCR: too few page text lines; lines=" + lines.Count);
                            softwareBitmap.Dispose();
                            randomAccess.Dispose();
                            return false;
                        }

                        int first = 0;
                        int last = lines.Count - 1;
                        RemoveRunningHeader(lines, pageBounds, ref first);
                        if (last > first && IsPageNumberOnly(lines[last].Text))
                        {
                            Log("book OCR: skipped footer page number='" + PreviewText(lines[last].Text, 32) + "'");
                            last--;
                        }
                        if (last <= first)
                        {
                            Log("book OCR: no body text remains after header filtering");
                            softwareBitmap.Dispose();
                            randomAccess.Dispose();
                            return false;
                        }

                        OcrTextLine firstLine = lines[first];
                        OcrTextLine lastLine = lines[last];
                        pageSignature = MakePageSignature(firstLine.Text, lastLine.Text);
                        double endPadding = Math.Max(8, lastLine.Bounds.Height);
                        System.Windows.Point start = new System.Windows.Point(
                            client.Left + firstLine.Bounds.Left + Math.Min(3, Math.Max(1, firstLine.Bounds.Width * 0.02)),
                            client.Top + firstLine.Bounds.Top + firstLine.Bounds.Height / 2);
                        System.Windows.Point end = new System.Windows.Point(
                            Math.Min(client.Right - 2, client.Left + lastLine.Bounds.Right + endPadding),
                            client.Top + lastLine.Bounds.Top + lastLine.Bounds.Height / 2);
                        System.Windows.Point menuPoint = new System.Windows.Point(
                            client.Left + lastLine.Bounds.Left + lastLine.Bounds.Width / 2,
                            client.Top + lastLine.Bounds.Top + lastLine.Bounds.Height / 2);
                        if (!client.Contains(start) || !client.Contains(end) || end.Y <= start.Y)
                        {
                            Log("book OCR: refused invalid text endpoints start=" + start + " end=" + end);
                            softwareBitmap.Dispose();
                            randomAccess.Dispose();
                            return false;
                        }

                        phase.Restart();
                        bool dragged = DragSelectText(start, end, client);
                        Log("book OCR timing: drag elapsedMs=" + phase.ElapsedMilliseconds +
                            " totalElapsedMs=" + total.ElapsedMilliseconds);
                        if (dragged)
                        {
                            contextPoint = client.Contains(menuPoint) ? menuPoint : end;
                            Log("book OCR: selected text body, lines=" + lines.Count +
                                " first='" + PreviewText(firstLine.Text, 72) + "' last='" + PreviewText(lastLine.Text, 72) +
                                " start=" + start + " end=" + end + " menuPoint=" + contextPoint.Value);
                        }
                        softwareBitmap.Dispose();
                        randomAccess.Dispose();
                        return dragged;
                    }
                }
            }
            catch (Exception ex)
            {
                Log("book OCR failed: " + ex.Message);
                return false;
            }
        }

        private static string MakePageSignature(string first, string last)
        {
            string a = (first ?? string.Empty).Trim().ToLowerInvariant();
            string b = (last ?? string.Empty).Trim().ToLowerInvariant();
            return a + "|" + b;
        }

        private static bool WaitForPlaybackToFinish(IntPtr browserWindow, System.Windows.Point? contextPoint,
            out bool startedPlaying)
        {
            startedPlaying = false;
            DateTime started = DateTime.UtcNow;
            bool seenPlaying = false;
            int idleConfirmations = 0;
            while (IsAutoReading())
            {
                if (GetForegroundWindow() != browserWindow) return false;
                PlaybackState state = ReadPlaybackState(browserWindow, contextPoint);
                if (state == PlaybackState.Playing)
                {
                    if (!seenPlaying) Log("auto read: playback confirmed");
                    seenPlaying = true;
                    startedPlaying = true;
                    idleConfirmations = 0;
                }
                else if (state == PlaybackState.Idle && seenPlaying)
                {
                    idleConfirmations++;
                    if (idleConfirmations >= 2)
                    {
                        Log("auto read: playback finished");
                        return true;
                    }
                }

                // If the command did not enter playback state, do not turn the
                // page blindly. The user can inspect the log and restart with Shift.
                if (!seenPlaying && (DateTime.UtcNow - started).TotalSeconds >= 10)
                    return false;
                Thread.Sleep(seenPlaying ? 1100 : 550);
            }
            return false;
        }

        private static PlaybackState ReadPlaybackState(IntPtr browserWindow, System.Windows.Point? contextPoint)
        {
            if (!contextPoint.HasValue) return PlaybackState.Unknown;
            uint browserPid;
            GetWindowThreadProcessId(browserWindow, out browserPid);
            if (browserPid == 0) return PlaybackState.Unknown;

            EnsureForeground(browserWindow);
            SendRightClick(browserWindow, contextPoint.Value);
            try
            {
                DateTime limit = DateTime.UtcNow.AddMilliseconds(900);
                while (DateTime.UtcNow < limit)
                {
                    Thread.Sleep(90);
                    bool sawSpeak = false;
                    bool sawStop = false;
                    AutomationElementCollection items = AutomationElement.RootElement.FindAll(
                        TreeScope.Descendants,
                        new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.MenuItem));
                    for (int i = 0; i < items.Count; i++)
                    {
                        int handle = FindOwningWindowHandle(items[i]);
                        if (handle == 0) continue;
                        uint itemPid;
                        GetWindowThreadProcessId(new IntPtr(handle), out itemPid);
                        if (itemPid != browserPid) continue;
                        if (items[i].Current.IsOffscreen) continue;
                        string name = items[i].Current.Name ?? string.Empty;
                        if (name.IndexOf(ITEM_STOP, StringComparison.Ordinal) >= 0) sawStop = true;
                        if (name.IndexOf(ITEM_SPEAK, StringComparison.Ordinal) >= 0) sawSpeak = true;
                    }
                    if (sawStop) return PlaybackState.Playing;
                    if (sawSpeak) return PlaybackState.Idle;
                }
            }
            catch (Exception ex) { Log("auto read: playback menu probe failed: " + ex.Message); }
            finally { SendEscape(); }
            return PlaybackState.Unknown;
        }

        // Прокрутка вида колесом мыши. Синтезированные клавиши до встроенного
        // PDF-просмотрщика не доходят (проверено по журналу: ArrowRight,
        // PageDown, ArrowDown и Ctrl+Down не сдвигали лист), а мышь работает.
        private static void SendWheel(System.Windows.Point screenPoint, int delta)
        {
            SetCursorPos((int)Math.Round(screenPoint.X), (int)Math.Round(screenPoint.Y));
            Thread.Sleep(15);
            mouse_event(MOUSEEVENTF_WHEEL, 0, 0, delta, NO_EXTRA);
        }

        // Прокручивает вид примерно на один экран вниз (к следующей странице).
        private static void TurnPageByWheel(IntPtr browserWindow, System.Windows.Point? point)
        {
            System.Windows.Rect client;
            if (!GetClientScreenBounds(browserWindow, out client) || client.Width < 400 || client.Height < 400)
                return;
            EnsureForeground(browserWindow);
            System.Windows.Point wheelPoint;
            if (point.HasValue) wheelPoint = point.Value;
            else wheelPoint = new System.Windows.Point(client.Left + client.Width / 2.0,
                client.Top + client.Height * 0.6);

            // Один «щелчок» колеса прокручивает около 100 px; намеренно берём
            // меньше высоты листа, чтобы не перескочить страницу - недокрутка
            // безопасна, её доделает AlignVisiblePage.
            int notches = Math.Max(3, (int)Math.Ceiling(client.Height * 0.60 / 100.0));
            for (int i = 0; i < notches && IsAutoReading(); i++)
            {
                SendWheel(wheelPoint, WHEEL_COARSE_DELTA);
                Thread.Sleep(45);
            }
            Thread.Sleep(250);
        }

        private enum PageGeometry
        {
            Unreadable,   // белая область листа не найдена (напр. страница-картинка)
            Partial,      // лист найден, но не вписан в окно целиком
            Full          // лист виден целиком
        }

        private struct PageView
        {
            public PageGeometry Geometry;
            public double TopOffset;      // верх белой области листа в координатах клиента
            public double BottomOffset;   // низ белой области листа в координатах клиента
            public double ClientHeight;
        }

        // Запоминает высоту полностью видимого листа (низ листа виден и близок
        // к нижнему краю окна). По ней DetectPageView отличает выровненный лист
        // от обрезанного: если лист прокручен слишком далеко, его высота падает.
        private static void RememberPageHeight(System.Windows.Rect pageBounds, double clientHeight)
        {
            if (pageBounds.Top >= clientHeight * PAGE_TOP_MIN_RATIO &&
                pageBounds.Top <= clientHeight * PAGE_TOP_MAX_RATIO &&
                pageBounds.Bottom < clientHeight - 2 &&
                pageBounds.Bottom >= clientHeight * 0.90)
            {
                s_pageHeightPx = pageBounds.Height;
            }
        }

        // Определяет геометрию видимого листа PDF. Полностью видимым лист
        // считается, когда его верх находится у верхнего края области просмотра
        // (не ниже 25% высоты окна) и низ доходит до нижнего края окна.
        // PageGeometry.Unreadable означает, что белую область найти не удалось
        // (страница-картинка) - прокрутка не поможет, сведения попадут в маркер.
        private static PageView DetectPageView(IntPtr browserWindow)
        {
            PageView view = new PageView();
            view.Geometry = PageGeometry.Unreadable;

            System.Windows.Rect client;
            if (!GetClientScreenBounds(browserWindow, out client) || client.Width < 400 || client.Height < 400)
                return view;

            System.Windows.Rect pageBounds;
            try
            {
                using (Bitmap bitmap = new Bitmap((int)Math.Round(client.Width), (int)Math.Round(client.Height), PixelFormat.Format32bppArgb))
                using (Graphics graphics = Graphics.FromImage(bitmap))
                {
                    graphics.CopyFromScreen((int)Math.Round(client.Left), (int)Math.Round(client.Top),
                        0, 0, bitmap.Size, CopyPixelOperation.SourceCopy);
                    if (!TryFindWhitePageBounds(bitmap, out pageBounds))
                    {
                        Log("auto read: page geometry: white page not found");
                        return view;
                    }
                }
            }
            catch (Exception ex) { Log("auto read: page geometry capture failed: " + ex.Message); return view; }

            // pageBounds уже в координатах клиентской области (0,0 = левый верх окна).
            view.TopOffset = pageBounds.Top;
            view.BottomOffset = pageBounds.Bottom;
            view.ClientHeight = client.Height;

            bool topOk = pageBounds.Top >= client.Height * PAGE_TOP_MIN_RATIO &&
                         pageBounds.Top <= client.Height * PAGE_TOP_MAX_RATIO;
            // Низ листа должен доходить до нижнего края окна; допуск ~4% высоты
            // покрывает поля и погрешность распознавания белой области.
            bool bottomOk = pageBounds.Bottom >= client.Height - client.Height * 0.04;
            // Если высота полностью видимого листа уже измерена, лист не должен
            // быть ниже неё: иначе верх или низ страницы обрезан.
            bool heightOk = s_pageHeightPx <= 0 || pageBounds.Height >= s_pageHeightPx - 8;
            if (topOk && bottomOk && heightOk)
            {
                view.Geometry = PageGeometry.Full;
                return view;
            }

            view.Geometry = PageGeometry.Partial;
            return view;
        }

        // Выравнивает видимый лист колесом мыши: прокручивает вниз, пока верх
        // листа не встанет у верхнего края области просмотра, либо вверх, если
        // лист уже почти вписан и его низ приподнялся над нижним краем.
        private static bool AlignVisiblePage(IntPtr browserWindow, System.Windows.Point? point)
        {
            System.Windows.Rect client;
            if (!GetClientScreenBounds(browserWindow, out client) || client.Width < 400 || client.Height < 400)
                return false;

            EnsureForeground(browserWindow);
            System.Windows.Point wheelPoint;
            if (point.HasValue) wheelPoint = point.Value;
            else wheelPoint = new System.Windows.Point(client.Left + client.Width / 2.0,
                client.Top + client.Height * 0.6);

            for (int step = 0; step < PAGE_ALIGN_MAX_STEPS && IsAutoReading(); step++)
            {
                PageView view = DetectPageView(browserWindow);
                if (view.Geometry == PageGeometry.Full)
                {
                    // Даём прокрутке «устояться» и проверяем ещё раз, чтобы не
                    // принять кадр в середине анимации за готовое положение.
                    Thread.Sleep(250);
                    PageView confirm = DetectPageView(browserWindow);
                    if (confirm.Geometry == PageGeometry.Full)
                    {
                        if (step > 0) Log("auto read: page aligned to the window in " + step + " wheel step(s)");
                        return true;
                    }
                    view = confirm;
                }
                if (view.Geometry == PageGeometry.Unreadable) return false;
                if (step == 0)
                    Log("auto read: aligning page top=" + view.TopOffset.ToString("F0") +
                        " bottom=" + view.BottomOffset.ToString("F0") +
                        " clientHeight=" + view.ClientHeight.ToString("F0"));

                // Лист почти вписан, но низ приподнялся - прокручиваем вверх
                // мелкими шагами. Иначе лист ниже окна - прокручиваем вниз.
                bool scrollUp = view.TopOffset <= client.Height * PAGE_TOP_MAX_RATIO &&
                                view.BottomOffset >= client.Height * 0.70;
                int delta = scrollUp ? -WHEEL_FINE_DELTA : WHEEL_COARSE_DELTA;
                if (!scrollUp && view.TopOffset <= client.Height * 0.35)
                    delta = WHEEL_FINE_DELTA;   // близко к цели - не перескакиваем

                SendWheel(wheelPoint, delta);
                Thread.Sleep(PAGE_ALIGN_STEP_DELAY_MS);
            }

            PageView final = DetectPageView(browserWindow);
            if (final.Geometry == PageGeometry.Full)
            {
                Log("auto read: page aligned to the window");
                return true;
            }
            Log("auto read: could not align the page top=" + final.TopOffset.ToString("F0") +
                " bottom=" + final.BottomOffset.ToString("F0") +
                " clientHeight=" + final.ClientHeight.ToString("F0"));
            return false;
        }

        // Номер печатной страницы по видимому листу (для маркера в логе).
        // Нужен, когда страницу не удалось выделить или запустить озвучку,
        // чтобы потом улучшить алгоритм разбора таких страниц (графики, схемы).
        private static string TryDetectVisiblePageNumber(IntPtr browserWindow)
        {
            try
            {
                System.Windows.Rect client;
                if (!GetClientScreenBounds(browserWindow, out client) || client.Width < 400 || client.Height < 400)
                    return null;
                using (Bitmap bitmap = new Bitmap((int)Math.Round(client.Width), (int)Math.Round(client.Height), PixelFormat.Format32bppArgb))
                {
                    using (Graphics graphics = Graphics.FromImage(bitmap))
                    {
                        graphics.CopyFromScreen((int)Math.Round(client.Left), (int)Math.Round(client.Top),
                            0, 0, bitmap.Size, CopyPixelOperation.SourceCopy);
                    }
                    System.Windows.Rect pageBounds;
                    if (!TryFindWhitePageBounds(bitmap, out pageBounds))
                    {
                        // На страницах-картинках белой области листа может не быть.
                        // Для маркера всё равно пытаемся распознать верх/низ окна,
                        // где обычно стоит номер страницы.
                        pageBounds = new System.Windows.Rect(0, 0, bitmap.Width, bitmap.Height);
                    }
                    OcrEngine engine = GetOcrEngine();
                    if (engine == null) return null;
                    using (MemoryStream stream = new MemoryStream())
                    {
                        bitmap.Save(stream, ImageFormat.Png);
                        stream.Position = 0;
                        IRandomAccessStream randomAccess = stream.AsRandomAccessStream();
                        BitmapDecoder decoder = BitmapDecoder.CreateAsync(randomAccess).AsTask().GetAwaiter().GetResult();
                        SoftwareBitmap softwareBitmap = decoder.GetSoftwareBitmapAsync().AsTask().GetAwaiter().GetResult();
                        OcrResult result = engine.RecognizeAsync(softwareBitmap).AsTask().GetAwaiter().GetResult();
                        List<OcrTextLine> lines = GetOcrPageLines(result, pageBounds);
                        string found = DetectFolioFromHeaderOrFooter(lines, pageBounds);
                        softwareBitmap.Dispose();
                        randomAccess.Dispose();
                        return found;
                    }
                }
            }
            catch (Exception ex) { Log("auto read: page number OCR failed: " + ex.Message); return null; }
        }

        // Возвращает число страницы (1-4 цифры), если оно стоит в начале или
        // конце строки колонтитула. Числа в середине строки (годы и т. п.) не
        // принимаются, чтобы не путать их с номером страницы.
        private static string TryExtractFolio(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return null;
            string[] parts = text.Split(
                new char[] { ' ', '\t', '\u00A0', '\u2003', '|', '.', ',', ';', ':', '—', '–', '-', '/', '(', ')' },
                StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0) return null;
            if (IsPageNumberOnly(parts[0])) return parts[0];
            if (IsPageNumberOnly(parts[parts.Length - 1])) return parts[parts.Length - 1];
            return null;
        }

        // Номер страницы ищется только в верхней и нижней зонах листа, где
        // обычно стоит колонтитул; так номер не спутать с числами в тексте.
        private static string DetectFolioFromHeaderOrFooter(List<OcrTextLine> lines, System.Windows.Rect pageBounds)
        {
            if (lines == null || lines.Count == 0) return null;
            double topLimit = pageBounds.Top + pageBounds.Height * 0.10;
            double bottomLimit = pageBounds.Bottom - pageBounds.Height * 0.10;
            for (int i = 0; i < lines.Count; i++)
            {
                double center = lines[i].Bounds.Top + lines[i].Bounds.Height / 2;
                if (center <= topLimit || center >= bottomLimit)
                {
                    string folio = TryExtractFolio(lines[i].Text);
                    if (folio != null) return folio;
                }
            }
            return null;
        }

        private static void LogPageMarker(IntPtr browserWindow, string reason)
        {
            string folio = TryDetectVisiblePageNumber(browserWindow);
            Log("auto read: PAGE MARKER folio=" + (folio ?? "unknown") + " reason='" + reason + "'");
        }

        private static OcrEngine GetOcrEngine()
        {
            lock (s_ocrLock)
            {
                if (s_ocrEngine != null) return s_ocrEngine;
                try
                {
                    s_ocrEngine = OcrEngine.TryCreateFromLanguage(new Language("ru-RU"));
                    if (s_ocrEngine == null) s_ocrEngine = OcrEngine.TryCreateFromUserProfileLanguages();
                }
                catch (Exception ex) { Log("book OCR engine initialization failed: " + ex.Message); }
                return s_ocrEngine;
            }
        }

        private static bool TryFindWhitePageBounds(Bitmap bitmap, out System.Windows.Rect pageBounds)
        {
            pageBounds = System.Windows.Rect.Empty;
            const int scale = 4;
            int w = Math.Max(1, bitmap.Width / scale);
            int h = Math.Max(1, bitmap.Height / scale);
            int minY = Math.Max(0, (int)(h * 0.14)); // skip browser tabs and the PDF toolbar
            bool[] white = new bool[w * h];
            for (int y = minY; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    Color c = bitmap.GetPixel(Math.Min(bitmap.Width - 1, x * scale + scale / 2),
                        Math.Min(bitmap.Height - 1, y * scale + scale / 2));
                    white[y * w + x] = c.R >= 248 && c.G >= 248 && c.B >= 248;
                }
            }

            bool[] visited = new bool[white.Length];
            int[] queue = new int[white.Length];
            int bestCount = 0, bestLeft = 0, bestTop = 0, bestRight = 0, bestBottom = 0;
            for (int i = minY * w; i < white.Length; i++)
            {
                if (!white[i] || visited[i]) continue;
                int head = 0, tail = 0;
                queue[tail++] = i;
                visited[i] = true;
                int left = w, top = h, right = 0, bottom = 0;
                while (head < tail)
                {
                    int index = queue[head++];
                    int x = index % w, y = index / w;
                    if (x < left) left = x;
                    if (x > right) right = x;
                    if (y < top) top = y;
                    if (y > bottom) bottom = y;
                    int n;
                    if (x > 0) { n = index - 1; if (white[n] && !visited[n]) { visited[n] = true; queue[tail++] = n; } }
                    if (x + 1 < w) { n = index + 1; if (white[n] && !visited[n]) { visited[n] = true; queue[tail++] = n; } }
                    if (y > 0) { n = index - w; if (white[n] && !visited[n]) { visited[n] = true; queue[tail++] = n; } }
                    if (y + 1 < h) { n = index + w; if (white[n] && !visited[n]) { visited[n] = true; queue[tail++] = n; } }
                }
                if (tail > bestCount)
                {
                    bestCount = tail;
                    bestLeft = left; bestTop = top; bestRight = right; bestBottom = bottom;
                }
            }

            int boxWidth = bestRight - bestLeft + 1;
            int boxHeight = bestBottom - bestTop + 1;
            if (bestCount < white.Length * 0.08 || boxWidth < w * 0.35 || boxHeight < h * 0.45) return false;
            pageBounds = new System.Windows.Rect(bestLeft * scale, bestTop * scale,
                Math.Min(bitmap.Width - bestLeft * scale, boxWidth * scale),
                Math.Min(bitmap.Height - bestTop * scale, boxHeight * scale));
            return true;
        }

        private static List<OcrTextLine> GetOcrPageLines(OcrResult result, System.Windows.Rect pageBounds)
        {
            List<OcrTextLine> lines = new List<OcrTextLine>();
            foreach (OcrLine line in result.Lines)
            {
                if (line.Words == null || line.Words.Count == 0) continue;
                double left = double.MaxValue, top = double.MaxValue, right = double.MinValue, bottom = double.MinValue;
                for (int i = 0; i < line.Words.Count; i++)
                {
                    Windows.Foundation.Rect r = line.Words[i].BoundingRect;
                    left = Math.Min(left, r.X); top = Math.Min(top, r.Y);
                    right = Math.Max(right, r.X + r.Width); bottom = Math.Max(bottom, r.Y + r.Height);
                }
                System.Windows.Rect bounds = new System.Windows.Rect(left, top, right - left, bottom - top);
                if (bounds.Width < 3 || bounds.Height < 4 || bounds.Height > 100) continue;
                if (bounds.Right < pageBounds.Left || bounds.Left > pageBounds.Right ||
                    bounds.Bottom < pageBounds.Top || bounds.Top > pageBounds.Bottom) continue;
                lines.Add(new OcrTextLine { Text = line.Text, Bounds = bounds });
            }
            lines.Sort(delegate(OcrTextLine a, OcrTextLine b)
            {
                double dy = a.Bounds.Top - b.Bounds.Top;
                return Math.Abs(dy) < 6 ? a.Bounds.Left.CompareTo(b.Bounds.Left) : dy.CompareTo(0);
            });
            return lines;
        }

        private static void RemoveRunningHeader(List<OcrTextLine> lines, System.Windows.Rect pageBounds, ref int first)
        {
            if (lines.Count <= 1) return;
            string text = (lines[0].Text ?? string.Empty).Trim();
            double firstCenter = lines[0].Bounds.Top + lines[0].Bounds.Height / 2;
            bool topOfPage = firstCenter <= pageBounds.Top + pageBounds.Height * 0.10;
            if (topOfPage && (IsPageNumberAndRunningHeader(text) ||
                IsRunningHeaderWithTrailingPageNumber(text) || IsAnyHeaderWithTrailingFolio(text)))
            {
                Log("book OCR: skipped top page header='" + PreviewText(text, 90) + "'");
                first++;
                return;
            }

            // On facing book pages the folio can sit on either side of the
            // running title. OCR may return them as separate lines with the
            // same baseline; identify the whole top row by geometry, not by
            // assuming the page number comes before the title.
            int sameRowEnd = 0;
            bool hasFolio = false;
            StringBuilder topRow = new StringBuilder();
            for (int i = 0; i < lines.Count && i < 5 && (i == 0 || SameTextRow(lines[0], lines[i])); i++)
            {
                sameRowEnd = i;
                if (IsPageNumberOnly(lines[i].Text)) hasFolio = true;
                if (topRow.Length > 0) topRow.Append(" | ");
                topRow.Append(lines[i].Text);
            }
            if (hasFolio && topOfPage)
            {
                first = sameRowEnd + 1;
                Log("book OCR: skipped top row with folio='" + PreviewText(topRow.ToString(), 100) + "'");
                return;
            }
            double topInset = lines[0].Bounds.Top - pageBounds.Top;
            bool shortRightHeader = text.Length <= 60 &&
                lines[0].Bounds.Left >= pageBounds.Left + pageBounds.Width * 0.55;
            if (topOfPage && topInset <= pageBounds.Height * 0.09 && shortRightHeader)
            {
                first++;
                Log("book OCR: skipped short right-aligned top header='" + PreviewText(text, 72) +
                    "' yOffset=" + topInset.ToString("F1") + " bounds=" + lines[0].Bounds);
                return;
            }
            if (topOfPage && topInset <= pageBounds.Height * 0.035 && lines[0].Text.Trim().Length <= 100)
            {
                first++;
                Log("book OCR: skipped top-margin header by position='" + PreviewText(text, 72) +
                    "' yOffset=" + topInset.ToString("F1"));
                return;
            }
            if (topOfPage)
                Log("book OCR: top row kept; no same-row folio found text='" + PreviewText(topRow.ToString(), 96) +
                    "' yOffset=" + topInset.ToString("F1"));
            if (lines.Count > 1 && IsPageNumberOnly(text) && SameTextRow(lines[0], lines[1]) && IsRunningChapterHeader(lines[1].Text))
            {
                Log("book OCR: skipped page number and running header='" + PreviewText(text + " " + lines[1].Text, 90) + "'");
                first += 2;
                return;
            }
            if (lines.Count > 1 && IsRunningChapterHeader(text) && SameTextRow(lines[0], lines[1]) && IsPageNumberOnly(lines[1].Text))
            {
                Log("book OCR: skipped running header and page number='" + PreviewText(text + " " + lines[1].Text, 90) + "'");
                first += 2;
                return;
            }
            if (IsPageNumberOnly(text))
            {
                Log("book OCR: skipped standalone page number='" + PreviewText(text, 32) + "'");
                first++;
            }
        }

        private static bool SameTextRow(OcrTextLine a, OcrTextLine b)
        {
            double centerA = a.Bounds.Top + a.Bounds.Height / 2;
            double centerB = b.Bounds.Top + b.Bounds.Height / 2;
            return Math.Abs(centerA - centerB) <= Math.Max(12, Math.Max(a.Bounds.Height, b.Bounds.Height) * 1.5);
        }

        private static bool IsRunningHeaderWithTrailingPageNumber(string text)
        {
            string s = (text ?? string.Empty).Trim();
            int lastSpace = s.LastIndexOfAny(new char[] { ' ', '\t', '\u00A0', '\u2003' });
            return lastSpace >= 0 && IsPageNumberOnly(s.Substring(lastSpace + 1)) &&
                IsRunningChapterHeader(s.Substring(0, lastSpace).TrimEnd());
        }

        private static bool IsAnyHeaderWithTrailingFolio(string text)
        {
            string s = (text ?? string.Empty).Trim();
            int lastSpace = s.LastIndexOfAny(new char[] { ' ', '\t', '\u00A0', '\u2003' });
            if (lastSpace < 0) return false;
            string trailing = s.Substring(lastSpace + 1);
            return IsPageNumberOnly(trailing) && lastSpace > 0 &&
                s.Substring(0, lastSpace).Trim().Length > 0;
        }

        private static bool SelectDocumentText(AutomationElement document, IntPtr browserWindow, out System.Windows.Point? contextPoint)
        {
            contextPoint = null;
            Stopwatch total = Stopwatch.StartNew();
            Stopwatch phase = Stopwatch.StartNew();
            try
            {
                System.Windows.Rect client;
                if (!GetClientScreenBounds(browserWindow, out client) || client.Width < 400 || client.Height < 400)
                {
                    Log("book mode: browser client viewport unavailable or too small");
                    return false;
                }

                object pattern;
                if (!document.TryGetCurrentPattern(TextPattern.Pattern, out pattern))
                {
                    Log("book mode: visible page document has no UIA TextPattern");
                    return false;
                }

                TextPattern textPattern = (TextPattern)pattern;
                TextPatternRange[] visibleRanges = textPattern.GetVisibleRanges();
                Log("book timing: GetVisibleRanges count=" + (visibleRanges == null ? 0 : visibleRanges.Length) +
                    " elapsedMs=" + phase.ElapsedMilliseconds);
                if (visibleRanges == null || visibleRanges.Length == 0)
                {
                    Log("book mode: UIA returned no visible text ranges");
                    return false;
                }

                int firstIndex = 0;
                int lastIndex = visibleRanges.Length - 1;
                string firstText = visibleRanges[firstIndex].GetText(300).Trim();
                string lastText = visibleRanges[lastIndex].GetText(80).Trim();
                bool firstIsPageNumber = IsPageNumberOnly(firstText);
                if (IsPageNumberAndRunningHeader(firstText))
                {
                    firstIndex++;
                    Log("book mode: skipped page number and running header='" + PreviewText(firstText, 80) + "'");
                }
                else if (firstIsPageNumber && firstIndex + 1 < lastIndex)
                {
                    string nextText = visibleRanges[firstIndex + 1].GetText(180).Trim();
                    if (IsRunningChapterHeader(nextText))
                    {
                        firstIndex += 2;
                        Log("book mode: skipped page number and running header='" + PreviewText(nextText, 80) + "'");
                    }
                    else
                    {
                        firstIndex++;
                        Log("book mode: skipped standalone page number='" + firstText + "'");
                    }
                }
                else if (IsRunningChapterHeader(firstText))
                {
                    firstIndex++;
                    Log("book mode: skipped running header='" + PreviewText(firstText, 80) + "'");
                }
                if (IsPageNumberOnly(lastText) && lastIndex > firstIndex)
                {
                    lastIndex--;
                    Log("book mode: skipped trailing page number='" + lastText + "'");
                }
                if (firstIndex >= lastIndex)
                {
                    Log("book mode: not enough visible text ranges after header filtering");
                    return false;
                }

                phase.Restart();
                System.Windows.Rect[] firstRects = visibleRanges[firstIndex].GetBoundingRectangles();
                System.Windows.Rect[] lastRects = visibleRanges[lastIndex].GetBoundingRectangles();
                Log("book timing: endpoint bounds elapsedMs=" + phase.ElapsedMilliseconds);
                System.Windows.Point dragStart, dragEnd;
                if (!TryGetVisibleTextDragPoints(firstRects, lastRects, client, out dragStart, out dragEnd))
                {
                    Log("book mode: UIA endpoint ranges have no safe text bounds; ranges=" + visibleRanges.Length);
                    return false;
                }

                phase.Restart();
                if (!DragSelectText(dragStart, dragEnd, client)) return false;
                Log("book timing: mouse selection elapsedMs=" + phase.ElapsedMilliseconds);
                contextPoint = dragEnd;
                Log("book mode: dragged along visible text bounds, ranges=" + visibleRanges.Length +
                    " first='" + PreviewText(firstText, 80) + "' last='" + PreviewText(lastText, 80) +
                    "' start=" + dragStart + " end=" + dragEnd + " totalElapsedMs=" + total.ElapsedMilliseconds);
                return true;
            }
            catch (Exception ex) { Log("book mode: UIA visible-text selection failed: " + ex.Message); return false; }
        }

        private static string PreviewText(string text, int maxLength)
        {
            string preview = (text ?? string.Empty).Replace("\r", " ").Replace("\n", " ").Trim();
            if (preview.Length > maxLength) preview = preview.Substring(0, maxLength);
            return preview;
        }

        private static bool IsPageNumberOnly(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return false;
            string s = text.Trim();
            if (s.Length == 0 || s.Length > 4) return false;
            for (int i = 0; i < s.Length; i++) if (!char.IsDigit(s[i])) return false;
            return true;
        }

        private static bool IsPageNumberAndRunningHeader(string text)
        {
            string s = (text ?? string.Empty).TrimStart();
            int i = 0;
            while (i < s.Length && char.IsDigit(s[i])) i++;
            if (i == 0 || i > 4 || i >= s.Length || !char.IsWhiteSpace(s[i])) return false;
            return IsRunningChapterHeader(s.Substring(i).TrimStart());
        }

        private static bool IsRunningChapterHeader(string text)
        {
            string s = (text ?? string.Empty).TrimStart();
            return s.StartsWith("Глава", StringComparison.OrdinalIgnoreCase) ||
                s.StartsWith("Chapter", StringComparison.OrdinalIgnoreCase) ||
                s.StartsWith("Часть", StringComparison.OrdinalIgnoreCase) ||
                s.StartsWith("Part", StringComparison.OrdinalIgnoreCase);
        }

        private static bool TryGetVisibleTextPoint(System.Windows.Rect[] rectangles, System.Windows.Rect client, out System.Windows.Point point)
        {
            point = new System.Windows.Point();
            if (rectangles == null || rectangles.Length == 0) return false;
            double bestArea = 0;
            for (int i = 0; i < rectangles.Length; i++)
            {
                System.Windows.Rect r = rectangles[i];
                if (r.IsEmpty || r.Width <= 0 || r.Height <= 0) continue;
                System.Windows.Rect clipped = System.Windows.Rect.Intersect(r, client);
                double area = clipped.IsEmpty ? 0 : clipped.Width * clipped.Height;
                if (area > bestArea)
                {
                    bestArea = area;
                    point = new System.Windows.Point(clipped.Left + clipped.Width / 2, clipped.Top + clipped.Height / 2);
                }
            }
            return bestArea > 0 && client.Contains(point);
        }

        private static bool TryGetVisibleTextDragPoints(System.Windows.Rect[] firstRectangles,
            System.Windows.Rect[] lastRectangles, System.Windows.Rect client,
            out System.Windows.Point start, out System.Windows.Point end)
        {
            start = new System.Windows.Point();
            end = new System.Windows.Point();
            if (firstRectangles == null || firstRectangles.Length == 0 ||
                lastRectangles == null || lastRectangles.Length == 0) return false;

            System.Windows.Rect first = System.Windows.Rect.Empty;
            System.Windows.Rect last = System.Windows.Rect.Empty;
            for (int i = 0; i < firstRectangles.Length; i++)
            {
                System.Windows.Rect r = firstRectangles[i];
                if (r.IsEmpty || r.Width < 3 || r.Height < 4 || r.Height > 100) continue;
                System.Windows.Rect clipped = System.Windows.Rect.Intersect(r, client);
                if (clipped.IsEmpty || clipped.Width < 3 || clipped.Height < 4) continue;
                first = clipped;
                break;
            }
            for (int i = lastRectangles.Length - 1; i >= 0; i--)
            {
                System.Windows.Rect r = lastRectangles[i];
                if (r.IsEmpty || r.Width < 3 || r.Height < 4 || r.Height > 100) continue;
                System.Windows.Rect clipped = System.Windows.Rect.Intersect(r, client);
                if (clipped.IsEmpty || clipped.Width < 3 || clipped.Height < 4) continue;
                last = clipped;
                break;
            }
            if (first.IsEmpty || last.IsEmpty) return false;

            double startInset = Math.Min(3, Math.Max(1, first.Width * 0.03));
            double endInset = Math.Min(3, Math.Max(1, last.Width * 0.03));
            start = new System.Windows.Point(first.Left + startInset, first.Top + first.Height / 2);
            end = new System.Windows.Point(last.Right - endInset, last.Top + last.Height / 2);
            return client.Contains(start) && client.Contains(end) &&
                (end.Y > start.Y || (Math.Abs(end.Y - start.Y) < 2 && end.X > start.X));
        }

        private static bool GetClientScreenBounds(IntPtr hwnd, out System.Windows.Rect bounds)
        {
            bounds = System.Windows.Rect.Empty;
            RECT client;
            if (hwnd == IntPtr.Zero || !GetClientRect(hwnd, out client)) return false;
            POINT topLeft = new POINT();
            POINT bottomRight = new POINT();
            bottomRight.x = client.right;
            bottomRight.y = client.bottom;
            if (!ClientToScreen(hwnd, ref topLeft) || !ClientToScreen(hwnd, ref bottomRight)) return false;
            bounds = new System.Windows.Rect(topLeft.x, topLeft.y,
                bottomRight.x - topLeft.x, bottomRight.y - topLeft.y);
            return !bounds.IsEmpty;
        }

        private static bool IsDocumentElement(AutomationElement element)
        {
            if (element == null) return false;
            try { return ControlType.Document.Equals(element.Current.ControlType); }
            catch { return false; }
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
        private static bool Trigger(IntPtr browserWindow, AutomationElement owner, bool probeWithoutSelection,
            System.Windows.Point? contextPoint = null)
        {
            Interlocked.Exchange(ref s_lastTriggerTicks, DateTime.UtcNow.Ticks);

            uint browserPid;
            GetWindowThreadProcessId(browserWindow, out browserPid);
            if (browserPid == 0)
            {
                Log("trigger: cannot resolve browser process for foreground window");
                return false;
            }

            // Иногда Shift+F10 открывает «чужое» меню (например, меню самой
            // страницы без выделения в PDF-просмотрщике) - в нём нет пункта
            // Алисы. Поэтому не сдаёмся после первой попытки, а закрываем
            // меню и пробуем ещё раз с полным повтором (FocusOwner + Shift+F10).
            int attemptLimit = (probeWithoutSelection || contextPoint.HasValue) ? 1 : MENU_ATTEMPTS;
            int timeoutMs = probeWithoutSelection ? 2200 : MENU_TIMEOUT_MS;
            bool restartedPlayback = false;
            for (int attempt = 1; attempt <= attemptLimit; attempt++)
            {
                // Shift+F10 открывает контекстное меню для текущего выделения.
                EnsureForeground(browserWindow);
                if (!contextPoint.HasValue) FocusOwner(owner);
                if (contextPoint.HasValue) SendRightClick(browserWindow, contextPoint.Value);
                else SendContextKeys();

                DateTime start = DateTime.UtcNow;
                DateTime lastSend = start;
                int sends = 1;
                bool sawMenu = false;
                DateTime firstMenuSeen = DateTime.MinValue;
                int lastMenuCount = 0;
                string lastMenuNames = "";
                bool loggedUiADiagnostics = false;

                while ((DateTime.UtcNow - start).TotalMilliseconds < timeoutMs)
                {
                    Thread.Sleep(70);

                    AutomationElement speak = null;
                    AutomationElement stop = null;
                    int count = 0;
                    StringBuilder names = new StringBuilder();
                    try
                    {
                        // Контекстное меню Chromium в некоторых версиях Яндекс
                        // Браузера публикуется отдельным popup HWND и не является
                        // потомком главного окна в UIA-дереве. Ищем MenuItem на
                        // рабочем столе, затем оставляем только элементы окон
                        // процесса browser, чтобы не принять меню другого приложения.
                        AutomationElementCollection items = AutomationElement.RootElement.FindAll(
                            TreeScope.Descendants,
                            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.MenuItem));
                        int withHandle = 0;
                        int sameProcess = 0;
                        int visibleForProcess = 0;
                        for (int i = 0; i < items.Count; i++)
                        {
                            int nativeHandle = FindOwningWindowHandle(items[i]);
                            if (nativeHandle == 0) continue;
                            withHandle++;

                            uint itemProcessId;
                            GetWindowThreadProcessId(new IntPtr(nativeHandle), out itemProcessId);
                            if (itemProcessId != browserPid) continue;
                            sameProcess++;

                            // В дереве доступности есть и постоянных пункты панели
                            // браузера (скрытые). Считаем только видимые - иначе
                            // «меню открыто» фиксируется преждевременно.
                            bool offscreen;
                            try { offscreen = items[i].Current.IsOffscreen; }
                            catch { continue; }
                            if (offscreen) continue;
                            visibleForProcess++;

                            string name;
                            try { name = items[i].Current.Name; }
                            catch { continue; }

                            count++;
                            if (names.Length < 400)
                            {
                                if (names.Length > 0) names.Append(" | ");
                                names.Append(name);
                            }

                            if (name.IndexOf(ITEM_SPEAK, StringComparison.Ordinal) >= 0) speak = items[i];
                            else if (name.IndexOf(ITEM_STOP, StringComparison.Ordinal) >= 0) stop = items[i];
                        }
                        if (!loggedUiADiagnostics)
                        {
                            Log("trigger: UIA desktop scan items=" + items.Count +
                                " withHwnd=" + withHandle + " browserProcess=" + sameProcess +
                                " visibleBrowserItems=" + visibleForProcess);
                            loggedUiADiagnostics = true;
                        }
                    }
                    catch (Exception ex)
                    {
                        Log("trigger: menu poll failed: " + ex.Message);
                        continue;
                    }

                    if (count > 0)
                    {
                        lastMenuCount = count;
                        lastMenuNames = names.ToString();
                        if (!sawMenu)
                        {
                            sawMenu = true;
                            firstMenuSeen = DateTime.UtcNow;
                        }
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
                            if (stop != null && contextPoint.HasValue && !restartedPlayback)
                            {
                                if (InvokeItem(stop))
                                {
                                    restartedPlayback = true;
                                    Log("trigger: stopped previous playback; starting the selected page");
                                    Thread.Sleep(500);
                                    EnsureForeground(browserWindow);
                                    SendRightClick(browserWindow, contextPoint.Value);
                                    sawMenu = false;
                                    firstMenuSeen = DateTime.MinValue;
                                    lastSend = DateTime.UtcNow;
                                    sends = 1;
                                    continue;
                                }
                            }
                            else if (restartedPlayback && stop != null)
                            {
                                // Wait for the PDF menu to switch from Stop to Speak.
                                Thread.Sleep(250);
                                continue;
                            }
                            else if (InvokeItem(target))
                            {
                                // Пункт «Озвучить Алисой» действительно вызван.
                                return speak != null;
                            }
                            Log("trigger: invoke failed");
                        }
                    }

                    // Меню не появилось - повторяем Shift+F10 (надёжность доставки).
                    if (!probeWithoutSelection && !contextPoint.HasValue && !sawMenu && sends < 3 &&
                        (DateTime.UtcNow - lastSend).TotalMilliseconds > 900)
                    {
                        EnsureForeground(browserWindow);
                        if (!contextPoint.HasValue) FocusOwner(owner);
                        if (contextPoint.HasValue) SendRightClick(browserWindow, contextPoint.Value);
                        else SendContextKeys();
                        sends++;
                        lastSend = DateTime.UtcNow;
                        Log("trigger: menu not seen yet, Shift+F10 resend #" + sends);
                    }

                    if (sawMenu && (DateTime.UtcNow - firstMenuSeen).TotalMilliseconds > MENU_ITEM_GRACE_MS)
                    {
                        Log("trigger: attempt " + attempt + "/" + attemptLimit +
                            ": menu opened (" + lastMenuCount + " items) but the target item is missing: " + lastMenuNames);
                        SendEscape();
                        break;
                    }
                }

                if (attempt < attemptLimit)
                {
                    Thread.Sleep(MENU_RETRY_DELAY_MS);
                    Log("trigger: retrying with a fresh Shift+F10 (attempt " + (attempt + 1) + "/" + attemptLimit + ")");
                    continue;
                }

                if (!sawMenu)
                {
                    Log("trigger: context menu did not open (" + sends + " sends)");
                    if (probeWithoutSelection) SendEscape();
                }
                else
                {
                    Log("trigger: target item not found after " + attemptLimit + " attempts");
                }
                return false;
            }
            return false;
        }

        // UIA children such as Chromium menu items commonly have HWND=0.
        // Walk up to the containing menu/window and use that HWND to identify
        // the owning browser process.
        private static int FindOwningWindowHandle(AutomationElement element)
        {
            AutomationElement current = element;
            for (int depth = 0; current != null && depth < 12; depth++)
            {
                try
                {
                    int handle = current.Current.NativeWindowHandle;
                    if (handle != 0) return handle;
                }
                catch { }

                try { current = TreeWalker.ControlViewWalker.GetParent(current); }
                catch { return 0; }
            }
            return 0;
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

        private static void SendRightClick(IntPtr browserWindow, System.Windows.Point point)
        {
            System.Windows.Rect client;
            if (!GetClientScreenBounds(browserWindow, out client) || !client.Contains(point))
            {
                Log("trigger: refused right-click outside browser client bounds at " + point);
                return;
            }
            int x = (int)Math.Round(point.X);
            int y = (int)Math.Round(point.Y);
            SetCursorPos(x, y);
            Thread.Sleep(60);
            mouse_event(MOUSEEVENTF_RIGHTDOWN, 0, 0, 0, NO_EXTRA);
            Thread.Sleep(25);
            mouse_event(MOUSEEVENTF_RIGHTUP, 0, 0, 0, NO_EXTRA);
        }

        private static bool DragSelectText(System.Windows.Point start, System.Windows.Point end, System.Windows.Rect client)
        {
            if (!client.Contains(start) || !client.Contains(end))
            {
                Log("book mode: refused drag outside browser client bounds");
                return false;
            }
            SetCursorPos((int)Math.Round(start.X), (int)Math.Round(start.Y));
            Thread.Sleep(80);
            mouse_event(MOUSEEVENTF_LEFTDOWN, 0, 0, 0, NO_EXTRA);
            const int steps = 80;
            int virtualLeft = GetSystemMetrics(76);
            int virtualTop = GetSystemMetrics(77);
            int virtualWidth = GetSystemMetrics(78);
            int virtualHeight = GetSystemMetrics(79);
            if (virtualWidth <= 1 || virtualHeight <= 1)
            {
                mouse_event(MOUSEEVENTF_LEFTUP, 0, 0, 0, NO_EXTRA);
                Log("book mode: invalid virtual screen dimensions");
                return false;
            }
            for (int i = 1; i <= steps; i++)
            {
                double t = (double)i / steps;
                System.Windows.Point p = new System.Windows.Point(
                    start.X + (end.X - start.X) * t,
                    start.Y + (end.Y - start.Y) * t);
                if (!client.Contains(p))
                {
                    mouse_event(MOUSEEVENTF_LEFTUP, 0, 0, 0, NO_EXTRA);
                    Log("book mode: cancelled drag at a point outside browser client bounds");
                    return false;
                }
                int absoluteX = (int)Math.Round((p.X - virtualLeft) * 65535.0 / (virtualWidth - 1));
                int absoluteY = (int)Math.Round((p.Y - virtualTop) * 65535.0 / (virtualHeight - 1));
                mouse_event(MOUSEEVENTF_MOVE | MOUSEEVENTF_ABSOLUTE | MOUSEEVENTF_VIRTUALDESK,
                    absoluteX, absoluteY, 0, NO_EXTRA);
                Thread.Sleep(15);
            }
            Thread.Sleep(80);
            mouse_event(MOUSEEVENTF_LEFTUP, 0, 0, 0, NO_EXTRA);
            return true;
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

        // Всплывающее сообщение в правом нижнем углу экрана. Не забирает фокус
        // и само закрывается, поэтому не мешает работе в браузере.
        private sealed class ToastForm : System.Windows.Forms.Form
        {
            public ToastForm(string title, string message)
            {
                FormBorderStyle = System.Windows.Forms.FormBorderStyle.None;
                StartPosition = System.Windows.Forms.FormStartPosition.Manual;
                ShowInTaskbar = false;
                TopMost = true;
                BackColor = System.Drawing.Color.FromArgb(32, 32, 32);
                Size = new System.Drawing.Size(380, 92);

                System.Windows.Forms.Label titleLabel = new System.Windows.Forms.Label();
                titleLabel.Text = title;
                titleLabel.ForeColor = System.Drawing.Color.White;
                titleLabel.Font = new System.Drawing.Font("Segoe UI", 10f, System.Drawing.FontStyle.Bold);
                titleLabel.AutoSize = false;
                titleLabel.Location = new System.Drawing.Point(14, 10);
                titleLabel.Size = new System.Drawing.Size(352, 22);
                Controls.Add(titleLabel);

                System.Windows.Forms.Label messageLabel = new System.Windows.Forms.Label();
                messageLabel.Text = message;
                messageLabel.ForeColor = System.Drawing.Color.Gainsboro;
                messageLabel.Font = new System.Drawing.Font("Segoe UI", 9f);
                messageLabel.AutoSize = false;
                messageLabel.Location = new System.Drawing.Point(14, 34);
                messageLabel.Size = new System.Drawing.Size(352, 48);
                Controls.Add(messageLabel);

                System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer();
                timer.Interval = NOTIFY_TIMEOUT_MS;
                timer.Tick += delegate(object sender, EventArgs e) { timer.Stop(); Close(); };
                timer.Start();
            }

            // Не активировать окно, чтобы не сбивать активное окно браузера.
            protected override bool ShowWithoutActivation { get { return true; } }

            protected override System.Windows.Forms.CreateParams CreateParams
            {
                get
                {
                    System.Windows.Forms.CreateParams cp = base.CreateParams;
                    const int WS_EX_NOACTIVATE = 0x08000000;
                    const int WS_EX_TOOLWINDOW = 0x00000080;
                    cp.ExStyle |= WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW;
                    return cp;
                }
            }

            protected override void OnShown(EventArgs e)
            {
                base.OnShown(e);
                System.Drawing.Rectangle work = System.Windows.Forms.Screen.PrimaryScreen.WorkingArea;
                Location = new System.Drawing.Point(
                    work.Right - Width - 16, work.Bottom - Height - 16);
            }
        }

        private static void NotifyUser(string title, string message)
        {
            try
            {
                Thread thread = new Thread(delegate()
                {
                    try
                    {
                        using (ToastForm form = new ToastForm(title, message))
                        {
                            System.Windows.Forms.Application.Run(form);
                        }
                    }
                    catch (Exception ex) { Log("notify failed: " + ex.Message); }
                });
                thread.IsBackground = true;
                thread.SetApartmentState(ApartmentState.STA);
                thread.Start();
            }
            catch (Exception ex) { Log("notify failed: " + ex.Message); }
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

