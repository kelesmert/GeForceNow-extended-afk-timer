using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;

internal static class Program
{
    private const int PollIntervalMs = 10_000;
    private const int CountdownRefreshMs = 1_000;
    private const int RestoreDelayMs = 500;
    private const int NudgePixels = 50;
    private static readonly TimeSpan MaxRuntime = TimeSpan.FromHours(1);

    private static readonly CancellationTokenSource Shutdown = new CancellationTokenSource();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool MoveWindow(IntPtr hWnd, int x, int y, int width, int height, bool repaint);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetWindowRect(IntPtr hWnd, out Rect rect);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    private static int Main(string[] args)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            Console.Error.WriteLine("This application only runs on Windows because it uses Win32 user32.dll APIs.");
            return 1;
        }

        if (args.Length > 0 && string.Equals(args[0], "--list-windows", StringComparison.OrdinalIgnoreCase))
        {
            ListVisibleWindows();
            return 0;
        }

        Console.CancelKeyPress += (_, args) =>
        {
            args.Cancel = true;
            Shutdown.Cancel();
        };

        Stopwatch runtime = Stopwatch.StartNew();

        WriteCountdown(runtime, allowRedirectedOutput: true);
        Console.WriteLine("GFN window watcher is running. Press CTRL + C to exit.");
        Console.WriteLine($"The watcher will stop automatically after {MaxRuntime.TotalMinutes:0} minutes.");

        while (!Shutdown.IsCancellationRequested)
        {
            WriteCountdown(runtime, allowRedirectedOutput: false);

            if (runtime.Elapsed >= MaxRuntime)
            {
                WriteCountdown(runtime, allowRedirectedOutput: false);
                Console.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] Maximum runtime reached. Stopping the program.");
                break;
            }

            WindowMatch? match = FindGfnWindow();

            if (match is null)
            {
                Console.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] GFN window was not found.");
            }
            else if (TryNudgeWindow(match.Value, out string message))
            {
                Console.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}");
            }
            else
            {
                Console.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] Operation failed: {message}");
            }

            TimeSpan remainingRuntime = MaxRuntime - runtime.Elapsed;
            TimeSpan nextDelay = remainingRuntime < TimeSpan.FromMilliseconds(PollIntervalMs)
                ? remainingRuntime
                : TimeSpan.FromMilliseconds(PollIntervalMs);

            if (WaitForNextPoll(runtime, nextDelay))
            {
                break;
            }
        }

        Console.WriteLine("Program stopped.");
        return 0;
    }

    private static bool WaitForNextPoll(Stopwatch runtime, TimeSpan delay)
    {
        Stopwatch wait = Stopwatch.StartNew();

        while (wait.Elapsed < delay)
        {
            TimeSpan remainingDelay = delay - wait.Elapsed;
            TimeSpan refreshDelay = TimeSpan.FromMilliseconds(CountdownRefreshMs);
            TimeSpan nextDelay = remainingDelay < refreshDelay ? remainingDelay : refreshDelay;

            if (nextDelay <= TimeSpan.Zero)
            {
                break;
            }

            if (Shutdown.Token.WaitHandle.WaitOne(nextDelay))
            {
                return true;
            }

            WriteCountdown(runtime, allowRedirectedOutput: false);
        }

        return false;
    }

    private static void WriteCountdown(Stopwatch runtime, bool allowRedirectedOutput)
    {
        string line = $"Remaining time: {FormatRemainingRuntime(runtime)}";

        try
        {
            Console.Title = $"GFN watcher - {line}";
        }
        catch (InvalidOperationException)
        {
        }

        if (Console.IsOutputRedirected)
        {
            if (allowRedirectedOutput)
            {
                Console.WriteLine(line);
            }

            return;
        }

        try
        {
            int cursorLeft = Console.CursorLeft;
            int cursorTop = Console.CursorTop;
            int width = Math.Max(Console.WindowWidth - 1, line.Length);

            Console.SetCursorPosition(0, 0);
            Console.Write(line.PadRight(width));
            Console.SetCursorPosition(cursorLeft, cursorTop == 0 ? 1 : cursorTop);
        }
        catch (ArgumentOutOfRangeException)
        {
        }
        catch (System.IO.IOException)
        {
        }
        catch (InvalidOperationException)
        {
        }
    }

    private static string FormatRemainingRuntime(Stopwatch runtime)
    {
        TimeSpan remaining = MaxRuntime - runtime.Elapsed;

        if (remaining < TimeSpan.Zero)
        {
            remaining = TimeSpan.Zero;
        }

        return remaining.ToString(@"hh\:mm\:ss");
    }

    private static WindowMatch? FindGfnWindow()
    {
        foreach (Process process in Process.GetProcesses())
        {
            using (process)
            {
                try
                {
                    IntPtr handle = process.MainWindowHandle;

                    if (handle == IntPtr.Zero || !IsWindowVisible(handle))
                    {
                        continue;
                    }

                    string title = process.MainWindowTitle;
                    string processName = process.ProcessName;

                    if (IsTargetWindow(processName, title))
                    {
                        return new WindowMatch(handle, process.Id, processName, title);
                    }
                }
                catch (InvalidOperationException)
                {
                    // The process may have exited; move on to the next process.
                }
                catch (System.ComponentModel.Win32Exception)
                {
                    // Skip processes that cannot be inspected because of access restrictions.
                }
                catch (NotSupportedException)
                {
                    // Skip remote or otherwise unsupported process entries.
                }
            }
        }

        return null;
    }

    private static void ListVisibleWindows()
    {
        Console.WriteLine("Visible windows:");
        Console.WriteLine("PID\tProcess\t\tMatch\tTitle");

        foreach (Process process in Process.GetProcesses())
        {
            using (process)
            {
                try
                {
                    IntPtr handle = process.MainWindowHandle;

                    if (handle == IntPtr.Zero || !IsWindowVisible(handle))
                    {
                        continue;
                    }

                    string title = process.MainWindowTitle;
                    string processName = process.ProcessName;
                    string matched = IsTargetWindow(processName, title) ? "yes" : "-";

                    Console.WriteLine($"{process.Id}\t{SafeForLog(processName)}\t\t{matched}\t{SafeForLog(title)}");
                }
                catch (InvalidOperationException)
                {
                }
                catch (System.ComponentModel.Win32Exception)
                {
                }
                catch (NotSupportedException)
                {
                }
            }
        }
    }

    private static bool IsTargetWindow(string processName, string title)
    {
        string normalizedProcess = Normalize(processName);

        if (normalizedProcess.IndexOf("geforcenow", StringComparison.Ordinal) >= 0
            || normalizedProcess.IndexOf("nvidiageforcenow", StringComparison.Ordinal) >= 0
            || normalizedProcess == "gfn")
        {
            return true;
        }

        if (IsIgnoredHostWindow(normalizedProcess))
        {
            return false;
        }

        return IsGfnTitle(title);
    }

    private static bool IsIgnoredHostWindow(string normalizedProcess)
    {
        return normalizedProcess == "mintty"
            || normalizedProcess == "cmd"
            || normalizedProcess == "conhost"
            || normalizedProcess == "powershell"
            || normalizedProcess == "pwsh"
            || normalizedProcess == "windowsterminal"
            || normalizedProcess == "wt"
            || normalizedProcess == "code"
            || normalizedProcess == "devenv";
    }

    private static bool IsGfnTitle(string title)
    {
        if (title.IndexOf("GeForce NOW", StringComparison.OrdinalIgnoreCase) >= 0
            || title.IndexOf("NVIDIA GeForce NOW", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            return true;
        }

        string normalizedTitle = Normalize(title);

        return normalizedTitle == "geforcenow"
            || normalizedTitle == "nvidiageforcenow";
    }

    private static bool TryNudgeWindow(WindowMatch match, out string message)
    {
        if (!GetWindowRect(match.Handle, out Rect rect))
        {
            message = $"Could not read the window bounds. Win32 error code: {Marshal.GetLastWin32Error()}";
            return false;
        }

        int width = rect.Right - rect.Left;
        int height = rect.Bottom - rect.Top;

        if (width <= 0 || height <= 0)
        {
            message = $"Invalid window size: {width}x{height}";
            return false;
        }

        int targetX = rect.Left >= NudgePixels ? rect.Left - NudgePixels : rect.Left + NudgePixels;

        if (!MoveWindow(match.Handle, targetX, rect.Top, width, height, true))
        {
            message = $"Could not move the window. Win32 error code: {Marshal.GetLastWin32Error()}";
            return false;
        }

        bool shutdownRequested = Shutdown.Token.WaitHandle.WaitOne(RestoreDelayMs);

        if (!MoveWindow(match.Handle, rect.Left, rect.Top, width, height, true))
        {
            message = $"Could not restore the window position. Win32 error code: {Marshal.GetLastWin32Error()}";
            return false;
        }

        if (shutdownRequested)
        {
            message = "Shutdown was requested; the window was restored.";
            return true;
        }

        message = $"GFN window was nudged. Process: {SafeForLog(match.ProcessName)} ({match.ProcessId}), Title: \"{SafeForLog(match.Title)}\"";
        return true;
    }

    private static string Normalize(string value)
    {
        char[] buffer = new char[value.Length];
        int index = 0;

        foreach (char character in value)
        {
            if (char.IsLetterOrDigit(character))
            {
                buffer[index] = char.ToLowerInvariant(character);
                index++;
            }
        }

        return new string(buffer, 0, index);
    }

    private static string SafeForLog(string value)
    {
        char[] buffer = new char[value.Length];

        for (int i = 0; i < value.Length; i++)
        {
            buffer[i] = char.IsControl(value[i]) ? ' ' : value[i];
        }

        return new string(buffer);
    }

    private readonly struct WindowMatch
    {
        public WindowMatch(IntPtr handle, int processId, string processName, string title)
        {
            Handle = handle;
            ProcessId = processId;
            ProcessName = processName;
            Title = title;
        }

        public IntPtr Handle { get; }
        public int ProcessId { get; }
        public string ProcessName { get; }
        public string Title { get; }
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct Rect
    {
        public readonly int Left;
        public readonly int Top;
        public readonly int Right;
        public readonly int Bottom;
    }
}
