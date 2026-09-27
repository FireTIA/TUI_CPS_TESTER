using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;

namespace TUI_CPS_TESTER
{
    internal static class Program
    {
        // ------------- WinAPI: низкоуровневый хук мыши -------------
        private const int WH_MOUSE_LL = 14;
        private const int WM_LBUTTONDOWN = 0x0201;
        private const int WM_RBUTTONDOWN = 0x0204;
        private const int WM_MBUTTONDOWN = 0x0207;

        private delegate IntPtr LowLevelMouseProc(int nCode, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelMouseProc lpfn, IntPtr hMod, uint dwThreadId);

        [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool UnhookWindowsHookEx(IntPtr hhk);

        [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

        [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern IntPtr GetModuleHandle(string lpModuleName);

        // ---- Необходимо для полноценного message loop потока хука ----
        [StructLayout(LayoutKind.Sequential)]
        private struct MSG
        {
            public IntPtr hwnd;
            public uint message;
            public IntPtr wParam;
            public IntPtr lParam;
            public uint time;
            public int pt_x;
            public int pt_y;
        }

        [DllImport("user32.dll")]
        private static extern int GetMessage(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax);

        [DllImport("user32.dll")]
        private static extern bool TranslateMessage(ref MSG lpMsg);

        [DllImport("user32.dll")]
        private static extern IntPtr DispatchMessage(ref MSG lpMsg);

        [DllImport("user32.dll")]
        private static extern bool PostThreadMessage(uint idThread, uint Msg, IntPtr wParam, IntPtr lParam);

        [DllImport("kernel32.dll")]
        private static extern uint GetCurrentThreadId();

        private const uint WM_QUIT = 0x0012;

        private static IntPtr _hookId = IntPtr.Zero;
        private static LowLevelMouseProc _proc = HookCallback;
        private static Thread _hookThread;
        private static uint _hookThreadId;
        private static readonly ManualResetEventSlim _hookReady = new ManualResetEventSlim(false);

        // ------------- Общие данные -------------
        // ConcurrentQueue — специально lock-free для записи (Enqueue),
        // чтобы обработчик хука мыши никогда не блокировался и не тормозил систему.
        private static readonly ConcurrentQueue<long> ClickTimestampsMs = new ConcurrentQueue<long>();
        private static Stopwatch _stopwatch = new Stopwatch();

        // volatile — чтобы поток хука сразу видел изменение флага из другого потока без задержек кеша
        private static volatile bool _recording = false;
        private static volatile int _buttonFilterCode = 1; // 1=left, 2=right, 3=middle, 4=any
        private static string _buttonFilter = "left";

        private static void Main()
        {
            Console.OutputEncoding = System.Text.Encoding.UTF8;
            Console.Title = "TUI CPS Tester";

            // ВАЖНО: WH_MOUSE_LL требует, чтобы поток, установивший хук,
            // имел работающий цикл обработки сообщений Windows (message loop).
            // Консольное приложение по умолчанию такого цикла не имеет — из-за этого
            // Windows не может нормально доставлять события через хук и весь ввод мыши
            // в системе начинает тормозить. Поэтому заводим отдельный поток
            // специально под хук + свой message loop, а консольное меню работает отдельно.
            _hookThread = new Thread(HookThreadProc);
            _hookThread.IsBackground = true;
            _hookThread.Name = "MouseHookThread";
            _hookThread.SetApartmentState(ApartmentState.STA);
            _hookThread.Start();

            // Ждём, пока поток хука реально установит хук, прежде чем показывать меню
            _hookReady.Wait();

            try
            {
                MainMenuLoop();
            }
            finally
            {
                // Корректно завершаем поток хука: снимаем хук и посылаем WM_QUIT в его message loop
                if (_hookId != IntPtr.Zero)
                {
                    UnhookWindowsHookEx(_hookId);
                }
                if (_hookThreadId != 0)
                {
                    PostThreadMessage(_hookThreadId, WM_QUIT, IntPtr.Zero, IntPtr.Zero);
                }
                _hookThread.Join(1000);
            }
        }

        // Этот метод целиком выполняется в отдельном потоке: устанавливает хук
        // и крутит стандартный Windows message loop, без которого LL-хуки мыши
        // работают некорректно и вызывают системные лаги курсора.
        private static void HookThreadProc()
        {
            _hookThreadId = GetCurrentThreadId();

            using (var curProcess = Process.GetCurrentProcess())
            using (var curModule = curProcess.MainModule)
            {
                _hookId = SetWindowsHookEx(WH_MOUSE_LL, _proc,
                    GetModuleHandle(curModule.ModuleName), 0);
            }

            _hookReady.Set();

            // Стандартный message loop: GetMessage блокируется до прихода сообщения,
            // TranslateMessage/DispatchMessage передают его дальше (в том числе то,
            // что нужно системе для нормальной работы хука).
            MSG msg;
            while (GetMessage(out msg, IntPtr.Zero, 0, 0) > 0)
            {
                TranslateMessage(ref msg);
                DispatchMessage(ref msg);
            }
        }

        // ВАЖНО: этот метод вызывается синхронно самой Windows для КАЖДОГО движения
        // и клика мыши в системе. Он обязан отработать за доли миллисекунды —
        // никаких lock, List.Add, консольного вывода, аллокаций и т.п. здесь быть не должно,
        // иначе вся мышь в системе начинает лагать (именно это и произошло в первой версии).
        private static IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode >= 0 && _recording)
            {
                int msg = wParam.ToInt32();
                int filter = _buttonFilterCode;

                bool isTarget =
                    (filter == 1 && msg == WM_LBUTTONDOWN) ||
                    (filter == 2 && msg == WM_RBUTTONDOWN) ||
                    (filter == 3 && msg == WM_MBUTTONDOWN) ||
                    (filter == 4 && (msg == WM_LBUTTONDOWN || msg == WM_RBUTTONDOWN || msg == WM_MBUTTONDOWN));

                if (isTarget)
                {
                    // Enqueue у ConcurrentQueue не блокирует поток — безопасно для хука.
                    ClickTimestampsMs.Enqueue(_stopwatch.ElapsedTicks);
                }
            }

            // Обязательно вызываем следующий хук в цепочке максимально быстро,
            // иначе именно это и вызывает лаги/зависания курсора в системе.
            return CallNextHookEx(_hookId, nCode, wParam, lParam);
        }

        // ------------- Меню -------------
        private static void MainMenuLoop()
        {
            while (true)
            {
                Console.Clear();
                PrintHeader();
                Console.WriteLine("Выберите пункт меню:");
                Console.WriteLine();
                Console.WriteLine("  1) Запустить CPS-тест (указать длительность)");
                Console.WriteLine("  2) Настроить кнопку для отслеживания (сейчас: " + FilterName(_buttonFilter) + ")");
                Console.WriteLine("  3) Выход");
                Console.WriteLine();
                Console.Write("> ");

                string choice = Console.ReadLine()?.Trim();

                switch (choice)
                {
                    case "1":
                        RunTestFlow();
                        break;
                    case "2":
                        ChooseButtonFilter();
                        break;
                    case "3":
                        return;
                    default:
                        Console.WriteLine("Неизвестный пункт, нажмите Enter...");
                        Console.ReadLine();
                        break;
                }
            }
        }

        private static void PrintHeader()
        {
            Console.WriteLine("==================================================");
            Console.WriteLine("   TUI CPS TESTER  —  консольный тест кликов");
            Console.WriteLine("==================================================");
            Console.WriteLine();
        }

        private static string FilterName(string f)
        {
            switch (f)
            {
                case "left": return "ЛКМ";
                case "right": return "ПКМ";
                case "middle": return "СКМ (колесо)";
                case "any": return "любая кнопка";
                default: return f;
            }
        }

        private static void ChooseButtonFilter()
        {
            Console.Clear();
            PrintHeader();
            Console.WriteLine("Какую кнопку отслеживать?");
            Console.WriteLine("  1) Левая кнопка мыши (ЛКМ)");
            Console.WriteLine("  2) Правая кнопка мыши (ПКМ)");
            Console.WriteLine("  3) Средняя кнопка (колесо)");
            Console.WriteLine("  4) Любая из кнопок");
            Console.Write("> ");

            string c = Console.ReadLine()?.Trim();
            switch (c)
            {
                case "1": _buttonFilter = "left"; _buttonFilterCode = 1; break;
                case "2": _buttonFilter = "right"; _buttonFilterCode = 2; break;
                case "3": _buttonFilter = "middle"; _buttonFilterCode = 3; break;
                case "4": _buttonFilter = "any"; _buttonFilterCode = 4; break;
                default:
                    Console.WriteLine("Оставляю без изменений.");
                    break;
            }
        }

        private static void RunTestFlow()
        {
            Console.Clear();
            PrintHeader();

            Console.Write("Введите длительность теста в секундах (например 5): ");
            string input = Console.ReadLine()?.Trim();

            if (!double.TryParse(input, out double seconds) || seconds <= 0)
            {
                Console.WriteLine("Некорректное значение. Нажмите Enter, чтобы вернуться в меню.");
                Console.ReadLine();
                return;
            }

            int totalMs = (int)(seconds * 1000);

            Console.WriteLine();
            Console.WriteLine($"Отслеживается: {FilterName(_buttonFilter)}");
            Console.WriteLine("Тест начнётся через 3 секунды. Приготовьтесь кликать (или включите автокликер)...");
            for (int i = 3; i >= 1; i--)
            {
                Console.WriteLine(i + "...");
                Thread.Sleep(1000);
            }

            // ConcurrentQueue.Clear() потокобезопасен и не требует ручного lock
            ClickTimestampsMs.Clear();

            _stopwatch.Restart();
            _recording = true;

            Console.WriteLine();
            Console.WriteLine("ИДЁТ ЗАПИСЬ КЛИКОВ...");
            Console.WriteLine("(Ctrl+C чтобы прервать досрочно — но лучше просто подождать)");
            Console.WriteLine();

            // Живой счётчик кликов раз в 100мс. Читаем Count у ConcurrentQueue —
            // это безопасно и не блокирует поток хука мыши.
            var sw = Stopwatch.StartNew();

            while (sw.ElapsedMilliseconds < totalMs)
            {
                Thread.Sleep(100);
                int count = ClickTimestampsMs.Count;

                double elapsedSec = sw.ElapsedMilliseconds / 1000.0;
                double liveCps = elapsedSec > 0 ? count / elapsedSec : 0;

                Console.Write($"\rВремя: {elapsedSec,6:0.0} с | Кликов: {count,6} | Live CPS: {liveCps,6:0.00}    ");
            }

            _recording = false;
            _stopwatch.Stop();

            Console.WriteLine();
            Console.WriteLine();
            Console.WriteLine("Тест завершён. Нажмите Enter для просмотра результатов...");
            Console.ReadLine();

            ShowResults(seconds);
        }

        private static void ShowResults(double testSeconds)
        {
            // Снимок очереди в массив — ToArray() у ConcurrentQueue потокобезопасен.
            // Клики хранились как "тики" Stopwatch (ElapsedTicks) — переводим в миллисекунды
            // через Stopwatch.Frequency, это точнее, чем считать сразу в мс внутри хука.
            long[] rawTicks = ClickTimestampsMs.ToArray();
            double ticksPerMs = Stopwatch.Frequency / 1000.0;
            List<long> clicks = rawTicks
                .Select(t => (long)(t / ticksPerMs))
                .OrderBy(t => t)
                .ToList();

            Console.Clear();
            PrintHeader();

            int totalClicks = clicks.Count;
            double cps = totalClicks / testSeconds;

            Console.WriteLine($"Длительность теста:      {testSeconds:0.###} сек");
            Console.WriteLine($"Отслеживаемая кнопка:    {FilterName(_buttonFilter)}");
            Console.WriteLine($"Всего кликов:            {totalClicks}");
            Console.WriteLine($"Средний CPS:             {cps:0.00}");
            Console.WriteLine();

            if (totalClicks < 2)
            {
                Console.WriteLine("Недостаточно кликов для анализа интервалов между ними.");
                Console.WriteLine();
                Console.WriteLine("Нажмите Enter, чтобы вернуться в меню...");
                Console.ReadLine();
                return;
            }

            // Интервалы между последовательными кликами
            var intervals = new List<long>();
            for (int i = 1; i < clicks.Count; i++)
            {
                intervals.Add(clicks[i] - clicks[i - 1]);
            }

            double avgIntervalMs = intervals.Average();
            long minIntervalMs = intervals.Min();
            long maxIntervalMs = intervals.Max();

            // Стандартное отклонение интервалов (джиттер)
            double variance = intervals.Select(x => Math.Pow(x - avgIntervalMs, 2)).Average();
            double stdDevMs = Math.Sqrt(variance);

            Console.WriteLine("---------- Анализ интервалов между кликами ----------");
            Console.WriteLine($"Средний интервал:        {avgIntervalMs:0.00} мс   (~{1000.0 / avgIntervalMs:0.00} CPS)");
            Console.WriteLine($"Минимальный интервал:    {minIntervalMs} мс");
            Console.WriteLine($"Максимальный интервал:   {maxIntervalMs} мс");
            Console.WriteLine($"Джиттер (std. откл.):    {stdDevMs:0.00} мс");
            Console.WriteLine();

            // Простая текстовая гистограмма распределения интервалов
            Console.WriteLine("---------- Гистограмма интервалов (мс) ----------");
            PrintHistogram(intervals);

            Console.WriteLine();
            Console.WriteLine("Нажмите Enter, чтобы вернуться в меню...");
            Console.ReadLine();
        }

        private static void PrintHistogram(List<long> intervals)
        {
            long min = intervals.Min();
            long max = intervals.Max();

            if (min == max)
            {
                Console.WriteLine($"Все интервалы одинаковые: {min} мс (идеально стабильный ритм)");
                return;
            }

            const int bucketCount = 10;
            double bucketSize = (max - min) / (double)bucketCount;
            if (bucketSize < 1) bucketSize = 1;

            var buckets = new int[bucketCount];
            foreach (var v in intervals)
            {
                int idx = (int)((v - min) / bucketSize);
                if (idx >= bucketCount) idx = bucketCount - 1;
                if (idx < 0) idx = 0;
                buckets[idx]++;
            }

            int maxBucketVal = buckets.Max();
            int barWidth = 40;

            for (int i = 0; i < bucketCount; i++)
            {
                double rangeStart = min + i * bucketSize;
                double rangeEnd = min + (i + 1) * bucketSize;
                int barLen = maxBucketVal > 0 ? (int)((double)buckets[i] / maxBucketVal * barWidth) : 0;
                string bar = new string('#', barLen);
                Console.WriteLine($"{rangeStart,6:0.0}-{rangeEnd,-6:0.0} | {bar} {buckets[i]}");
            }
        }
    }
}
