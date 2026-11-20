using System.Diagnostics;
using System.Reflection;

internal static class Stage3Runner
{
    private static string Dll => Assembly.GetExecutingAssembly().Location;

    private static void Child(string mode, string database, int count)
    {
        using var p = NewChild(mode, database, count);
        string stdout = p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        Console.WriteLine(stdout);
        if (p.ExitCode != 0 || !stdout.Contains(mode == "stage3-seed" ? "STAGE3_SEEDED" :
                mode == "stage3-verify" ? "STAGE3_BULK_PASSED" : "STAGE3_CRASH_RECOVERY_PASSED"))
            throw new Exception($"Stage3 subprocess {mode} failed, exit={p.ExitCode}");
    }

    private static Process NewChild(string mode, string database, int count)
    {
        var info = new ProcessStartInfo("dotnet")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true
        };
        info.ArgumentList.Add(Dll);
        info.ArgumentList.Add(mode);
        info.ArgumentList.Add(database);
        info.ArgumentList.Add(count.ToString());
        info.Environment["DOTNET_ROLL_FORWARD"] = "Major";
        return Process.Start(info) ?? throw new Exception("Failed to launch .NET child");
    }

    private static void CrashChild(string database, int count)
    {
        using var child = NewChild("stage3-crash-write", database, count);
        try
        {
            bool ready = false;
            for (int i = 0; i < 80; i++)
            {
                var reading = child.StandardOutput.ReadLineAsync();
                if (!reading.Wait(TimeSpan.FromSeconds(30)))
                    throw new TimeoutException("Timed out waiting for crash-ready marker");
                string? line = reading.Result;
                if (line == null) break;
                Console.WriteLine(line);
                if (line == "STAGE3_CRASH_READY") { ready = true; break; }
            }
            if (!ready) throw new Exception("Child exited before committing crash data");
            child.Kill();
            if (!child.WaitForExit(10000)) throw new TimeoutException("Child not terminated");
            Console.WriteLine("PASS forcibly terminated process without RRAdapter.Close()");
        }
        finally
        {
            if (!child.HasExited) { child.Kill(); child.WaitForExit(); }
        }
    }

    public static void RunAll(int count)
    {
        if (count is < 100 or > 50000) throw new ArgumentOutOfRangeException(nameof(count));
        string root = Path.Combine(Path.GetTempPath(), "factoria-rradapter-integration");
        string db = Path.Combine(root, "stage3-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(db);
        try
        {
            Child("stage3-seed", db, count);
            Child("stage3-verify", db, count);
            CrashChild(db, count);
            Child("stage3-crash-verify", db, count);
            Console.WriteLine("STAGE3_ALL_PASSED");
        }
        finally
        {
            Directory.Delete(db, recursive: true);
        }
    }
}
