using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using LandBar.Presentation;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        string folder = Path.GetFullPath(args.LastOrDefault() ?? "artifacts/topmost-smoke");
        Directory.CreateDirectory(folder);
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        if (args.Contains("--fixture")) return RunFixture(app, folder);
        string communication = Path.Combine(folder, "ipc-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(communication);
        var failures = new List<string>();
        var checks = new List<object>();
        var skips = new List<object>();
        object? activationDiagnostic = null;
        Process? fixture = null;
        TopmostWindowController? controller = null;
        var surface = new Border { Width = 208, Height = 44, HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Top, Background = Brushes.Black, CornerRadius = new CornerRadius(22) };
        var window = new Window
        {
            Title = "LandBar Topmost Smoke", Left = 40, Top = 90, Width = 792, Height = 488,
            ShowActivated = false, ShowInTaskbar = false, WindowStyle = WindowStyle.None,
            AllowsTransparency = true, Background = Brushes.Transparent, Topmost = true,
            Content = surface
        };
        IntPtr handle = IntPtr.Zero, fixtureHandle = IntPtr.Zero;
        Native.Rect baseline = default;
        int sequence = 0;
        void Check(string name, bool passed, object? detail = null)
        {
            checks.Add(new { name, passed, detail });
            if (!passed) failures.Add(name);
            Console.WriteLine($"{(passed ? "PASS" : "FAIL")} {name}");
        }
        async Task Command(string action, bool inside = true)
        {
            var origin = window.PointToScreen(new Point(292, inside ? 0 : 200));
            int current = ++sequence;
            File.WriteAllText(Path.Combine(communication, "command"), $"{current}|{action}|{(int)origin.X}|{(int)origin.Y}|208|44");
            var watch = Stopwatch.StartNew();
            while (watch.ElapsedMilliseconds < 1500)
            {
                try { if (File.ReadAllText(Path.Combine(communication, "ack")) == current.ToString()) return; } catch (IOException) { }
                await Task.Delay(20);
            }
            throw new InvalidOperationException($"Fixture did not acknowledge {action}.");
        }
        async Task Settled(int delay = 350) => await Task.Delay(delay);
        window.SourceInitialized += (_, _) =>
        {
            handle = new WindowInteropHelper(window).Handle;
            controller = new TopmostWindowController(window, () => new Rect(surface.TranslatePoint(new Point(), window), surface.RenderSize));
        };
        window.Loaded += async (_, _) =>
        {
            try
            {
                await Settled();
                Native.GetWindowRect(handle, out baseline);
                Check("Four out-of-context event hooks are attached", controller!.ActiveHookCount == 4);
                var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden };
                start.ArgumentList.Add("--fixture"); start.ArgumentList.Add(communication);
                fixture = Process.Start(start)!;
                var ready = Path.Combine(communication, "fixture-" + fixture.Id);
                var watch = Stopwatch.StartNew();
                while (!File.Exists(ready) && watch.ElapsedMilliseconds < 5000) await Task.Delay(20);
                fixtureHandle = new IntPtr(long.Parse(File.ReadAllText(ready)));
                await Settled();

                int before = controller.RepairCount;
                await Command("raise", inside: false); await Settled();
                Check("Overlap with the transparent host alone does not raise the island", controller.RepairCount == before,
                    new { before, after = controller.RepairCount });

                surface.Width = 792; surface.Height = 488; window.UpdateLayout();
                controller.RequestCheck(); await Settled();
                Check("A completed expansion checks the newly visible surface", Native.IsBefore(handle, fixtureHandle) && controller.RepairCount > before);
                surface.Width = 208; surface.Height = 44; window.UpdateLayout();

                var foreground = Native.GetForegroundWindow();
                await Command("raise"); await Settled();
                Check("A topmost window moving over the pill triggers recovery", Native.IsBefore(handle, fixtureHandle));
                Check("Recovery preserves the existing foreground window", Native.GetForegroundWindow() == foreground);

                await Command("activate");
                var activation = JsonSerializer.Deserialize<ActivationAcknowledgement>(File.ReadAllText(
                    Path.Combine(communication, $"activation-{sequence}.json")))
                    ?? throw new InvalidDataException("Fixture activation acknowledgement was empty.");
                if (activation.Sequence != sequence || activation.FixtureHandle != fixtureHandle.ToInt64())
                    throw new InvalidDataException("Fixture activation acknowledgement did not match the request.");
                bool fixtureObtainedFocus = activation.ForegroundImmediatelyAfter == fixtureHandle.ToInt64();
                await Settled();
                long settledForeground = Native.GetForegroundWindow().ToInt64();
                activationDiagnostic = new { request = activation, fixtureObtainedFocus, settledForeground };
                Check(fixtureObtainedFocus ? "An activated competing window recovers below the island" :
                    "A competing window recovers below the island after the activation request",
                    Native.IsBefore(handle, fixtureHandle), new { fixtureObtainedFocus });
                if (fixtureObtainedFocus)
                {
                    Check("The external fixture retains keyboard focus", settledForeground == fixtureHandle.ToInt64(),
                        new { actual = settledForeground, expected = fixtureHandle.ToInt64() });
                }
                else if (!activation.SetForegroundWindowSucceeded)
                {
                    const string name = "The external fixture retains keyboard focus";
                    const string reason = "Windows rejected the fixture activation request; the fixture never obtained focus.";
                    skips.Add(new { name, reason, detail = activation });
                    Console.WriteLine($"SKIP {name}: {reason}");
                    Check("Rejected fixture activation preserves the actual foreground window",
                        settledForeground == activation.ForegroundImmediatelyAfter,
                        new { beforeRecovery = activation.ForegroundImmediatelyAfter, afterRecovery = settledForeground });
                }
                else
                {
                    Check("The external fixture retains keyboard focus", false,
                        new { reason = "Activation reported success without the fixture obtaining focus.", detail = activation, settledForeground });
                }

                var popup = new Window { Owner = window, Title = "Own popup", Left = 335, Top = 90, Width = 190, Height = 44,
                    Topmost = true, ShowActivated = false, ShowInTaskbar = false, WindowStyle = WindowStyle.None, Background = Brushes.DimGray };
                before = controller.RepairCount; popup.Show(); await Settled();
                Check("Owned popup stays above its owner without a repair loop", Native.IsBefore(new WindowInteropHelper(popup).Handle, handle) && controller.RepairCount == before);
                popup.Close();

                Native.SetWindowPos(handle, new IntPtr(-2), 0, 0, 0, 0, 0x0013);
                await Settled();
                Check("A removed native TOPMOST style is restored", (Native.GetWindowLongPtr(handle, -20).ToInt64() & 8) != 0);

                // Four isolated obstructions in the same foreground application all recover.
                for (int i = 0; i < 4; i++)
                {
                    await Task.Delay(2150);
                    before = controller.RepairCount;
                    await Command("raise"); await Settled();
                    Check($"Independent obstruction {i + 1} recovers after a quiet interval", Native.IsBefore(handle, fixtureHandle) && controller.RepairCount > before,
                        new { before, after = controller.RepairCount, suppressed = controller.SuppressedRepairCount, islandFirst = Native.IsBefore(handle, fixtureHandle) });
                }

                await Task.Delay(2150);
                before = controller.RepairCount;
                for (int i = 0; i < 9; i++) { await Command("raise"); await Task.Delay(150); }
                await Settled();
                Check("Continuous competing reorders receive at most three corrective writes", controller.RepairCount - before is > 0 and <= 3 && controller.SuppressedRepairCount > 0,
                    new { writes = controller.RepairCount - before, suppressed = controller.SuppressedRepairCount });
                Native.GetWindowRect(handle, out var after);
                Check("All recovery operations preserve native position and dimensions", baseline.Equals(after), new { before = baseline, after });

                before = controller.RepairCount;
                Native.SetWindowPos(handle, new IntPtr(-2), 0, 0, 0, 0, 0x0013);
                controller.Dispose();
                await Command("raise"); await Settled();
                Check("Dispose removes all hooks and cancels a queued repair", controller.ActiveHookCount == 0 && controller.RepairCount == before &&
                    (Native.GetWindowLongPtr(handle, -20).ToInt64() & 8) == 0);
            }
            catch (Exception exception) { failures.Add(exception.ToString()); }
            finally
            {
                controller?.Dispose();
                if (fixture is { HasExited: false })
                {
                    try { await Command("exit"); } catch { }
                    if (!fixture.HasExited) await Task.Run(() => fixture.WaitForExit(1500));
                }
                fixture?.Dispose();
                File.WriteAllText(Path.Combine(folder, "report.json"), JsonSerializer.Serialize(new
                {
                    assemblyVersion = typeof(TopmostWindowController).Assembly.GetName().Version?.ToString(),
                    passed = failures.Count == 0, fullyVerified = failures.Count == 0 && skips.Count == 0,
                    checks, skips, failures, activation = activationDiagnostic, controllerRepairCount = controller?.RepairCount,
                    suppressionCount = controller?.SuppressedRepairCount,
                    usesOnlyOwnedFixtures = true, readsUserWindowText = false, changesUserConfiguration = false
                }, new JsonSerializerOptions { WriteIndented = true, IncludeFields = true }));
                app.Shutdown(failures.Count == 0 ? 0 : 1);
            }
        };
        window.Show();
        return app.Run();
    }

    private static int RunFixture(Application app, string folder)
    {
        var window = new Window { Title = "LandBar External Topmost Fixture", Left = 330, Top = 300, Width = 208, Height = 44,
            Topmost = true, ShowActivated = false, ShowInTaskbar = false, WindowStyle = WindowStyle.None, Background = Brushes.DimGray };
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(20) };
        string? previous = null;
        window.Loaded += (_, _) =>
        {
            File.WriteAllText(Path.Combine(folder, "fixture-" + Environment.ProcessId), new WindowInteropHelper(window).Handle.ToInt64().ToString());
            timer.Start();
        };
        timer.Tick += (_, _) =>
        {
            try
            {
                string command = File.ReadAllText(Path.Combine(folder, "command"));
                if (command == previous) return;
                var parts = command.Split('|');
                if (parts.Length != 6) return;
                previous = command;
                var hwnd = new WindowInteropHelper(window).Handle;
                if (parts[1] == "exit") { File.WriteAllText(Path.Combine(folder, "ack"), parts[0]); timer.Stop(); app.Shutdown(); return; }
                Native.SetWindowPos(hwnd, new IntPtr(-1), int.Parse(parts[2]), int.Parse(parts[3]), int.Parse(parts[4]), int.Parse(parts[5]), 0x0010);
                if (parts[1] == "activate")
                {
                    long beforeActivation = Native.GetForegroundWindow().ToInt64();
                    bool activated = Native.SetForegroundWindow(hwnd);
                    long immediatelyAfterActivation = Native.GetForegroundWindow().ToInt64();
                    var acknowledgement = new ActivationAcknowledgement(int.Parse(parts[0]), activated,
                        beforeActivation, immediatelyAfterActivation, hwnd.ToInt64());
                    File.WriteAllText(Path.Combine(folder, $"activation-{parts[0]}.json"), JsonSerializer.Serialize(acknowledgement));
                }
                File.WriteAllText(Path.Combine(folder, "ack"), parts[0]);
            }
            catch (IOException) { }
        };
        window.Show();
        return app.Run();
    }

    private sealed record ActivationAcknowledgement(int Sequence, bool SetForegroundWindowSucceeded,
        long ForegroundBefore, long ForegroundImmediatelyAfter, long FixtureHandle);

    private static class Native
    {
        [StructLayout(LayoutKind.Sequential)] public struct Rect { public int Left, Top, Right, Bottom; }
        [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hwnd, out Rect rect);
        [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hwnd);
        [DllImport("user32.dll")] public static extern IntPtr GetWindow(IntPtr hwnd, uint command);
        [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] public static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int index);
        [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int width, int height, uint flags);
        public static bool IsBefore(IntPtr first, IntPtr second)
        {
            var seen = new HashSet<IntPtr>();
            for (var hwnd = GetWindow(second, 3); hwnd != IntPtr.Zero && seen.Add(hwnd); hwnd = GetWindow(hwnd, 3))
                if (hwnd == first) return true;
            return false;
        }
    }
}
