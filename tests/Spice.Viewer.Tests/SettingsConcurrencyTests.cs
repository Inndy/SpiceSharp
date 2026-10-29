using System.Diagnostics;

namespace SpiceViewer;

public sealed class SettingsConcurrencyTests
{
    [Fact]
    public async Task ConcurrentProcessesPreserveBothVmUpdates()
    {
        string id = Guid.NewGuid().ToString("N"), directory = Path.Combine(Path.GetTempPath(), "SpiceSharp-settings-test-" + id);
        using var enteredA = new EventWaitHandle(false, EventResetMode.ManualReset, id + "-A");
        using var enteredB = new EventWaitHandle(false, EventResetMode.ManualReset, id + "-B");
        using var releaseA = new EventWaitHandle(false, EventResetMode.ManualReset, id + "-release");
        using var readyB = new EventWaitHandle(false, EventResetMode.ManualReset, id + "-ready");
        Process Start(string vm)
        {
            var info = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet")
            { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (string arg in new[] { Path.Combine(AppContext.BaseDirectory, "Spice.Viewer.Tests.dll"), "--settings-probe", directory, id, vm }) info.ArgumentList.Add(arg);
            return Process.Start(info)!;
        }
        Process? a = null, b = null;
        try
        {
            a = Start("A"); Assert.True(enteredA.WaitOne(10000), "VM A did not enter Update callback.");
            b = Start("B");
            Assert.True(readyB.WaitOne(10000), "VM B did not reach Update.");
            // Old code lets B read and save the stale file before A saves. A correct cross-process
            // transaction blocks B's callback, so release A after this bounded observation instead.
            enteredB.WaitOne(1500); releaseA.Set();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            await Task.WhenAll(a.WaitForExitAsync(timeout.Token), b.WaitForExitAsync(timeout.Token));
            Assert.True(a.ExitCode == 0, await a.StandardError.ReadToEndAsync());
            Assert.True(b.ExitCode == 0, await b.StandardError.ReadToEndAsync());
            var store = new SettingsStore(directory); Assert.Equal(SettingsLoadResult.Loaded, store.Load());
            Assert.Equal(new[] { "A", "B" }, store.Current.Vms.Keys.Order().ToArray());
            Assert.Equal(640, store.Current.Vms["A"].Right); Assert.Equal(800, store.Current.Vms["B"].Right);
        }
        finally
        {
            releaseA.Set();
            foreach (var p in new[] { a, b }) if (p is not null) { if (!p.HasExited) { p.Kill(true); p.WaitForExit(); } p.Dispose(); }
            if (Directory.Exists(directory)) Directory.Delete(directory, true); // exact per-test GUID directory only
        }
    }

}

public static class SettingsProbe
{
    public static int Main(string[] args)
    {
        if (args.Length != 4 || args[0] != "--settings-probe") return 2;
        try
        {
            string directory = args[1], id = args[2], vm = args[3];
            using var entered = EventWaitHandle.OpenExisting(id + "-" + vm);
            using var release = EventWaitHandle.OpenExisting(id + "-release");
            var store = new SettingsStore(directory);
            if (vm == "B") { using var ready = EventWaitHandle.OpenExisting(id + "-ready"); ready.Set(); }
            bool saved = store.Update(settings =>
            {
                entered.Set(); if (vm == "A" && !release.WaitOne(10000)) throw new TimeoutException("Parent did not release VM A.");
                settings.Vms[vm] = new VmWindowState { Right = vm == "A" ? 640 : 800, Bottom = 480, Dpi = 96, LastUsed = DateTime.UtcNow };
            });
            Console.WriteLine($"saved={saved}"); return saved ? 0 : 1;
        }
        catch (Exception e) { Console.Error.WriteLine(e); return 1; }
    }
}
