using System.Diagnostics;
using System.Reflection;
using System.Xml.Linq;
using Factograph.Data;

/// <summary>
/// Real process-termination tests for Factoria-owned commit boundaries.
/// Never opens or modifies Polar.DB's physical files directly.
/// </summary>
internal static class Stage4FactoriaCrash
{
    private const string RootName = "factoria-stage4-crash";
    private const string Owner = "stage4-owner";
    private const string RecordId = "crashed-record";
    private const string Word = "KILLEDPROCESSWORD";
    private static readonly XNamespace O = "http://fogid.net/o/";
    private static readonly XNamespace Rdf = "http://www.w3.org/1999/02/22-rdf-syntax-ns#";
    private static readonly string[] Stages = { "after-fog", "after-index", "after-journal" };
    private static readonly string Dll = Assembly.GetExecutingAssembly().Location;

    private static string SafeRoot => Path.Combine(Path.GetTempPath(), RootName) + Path.DirectorySeparatorChar;

    private static string Validate(string path)
    {
        string canonical = Path.GetFullPath(path);
        if (!canonical.StartsWith(SafeRoot, StringComparison.Ordinal) ||
            canonical == SafeRoot.TrimEnd(Path.DirectorySeparatorChar))
            throw new ArgumentException("Stage 4 accepts only its isolated test directories");
        return canonical;
    }

    private static void Check(bool condition, string meaning)
    {
        if (!condition) throw new Exception("FAIL " + meaning);
        Console.WriteLine("PASS " + meaning);
    }

    private static string Fog(string path) => Path.Combine(path, "source.fog");
    private static string Pending(string path) => Path.Combine(path, ".fog-index-pending");
    private static string Log(string path) => Path.Combine(path, "logfile_put.txt");

    private static void Setup(string path)
    {
        Directory.CreateDirectory(path);
        string db = Path.Combine(path, "db");
        Directory.CreateDirectory(db);
        new XElement(Rdf + "RDF",
            new XAttribute(XNamespace.Xmlns + "rdf", Rdf.NamespaceName),
            new XAttribute("owner", Owner),
            new XAttribute("prefix", "stage4-"),
            new XAttribute("counter", "1"),
            new XElement(O + "person",
                new XAttribute(Rdf + "about", "target"),
                new XElement(O + "name", "Target"))
            ).Save(Fog(path));
        new XElement("config",
            new XElement("database", new XAttribute("connectionstring",
                "rr:" + db + Path.DirectorySeparatorChar)),
            new XElement("LoadFog", new XAttribute("write", "yes"), Fog(path)))
            .Save(Path.Combine(path, "config.xml"));
        new XElement("Ontology").Save(Path.Combine(path, "ontology.xml"));
    }

    private static FDataService Open(string path) => new(
        path + Path.DirectorySeparatorChar,
        Path.Combine(path, "ontology.xml"), null);

    public static void RunChild(string rawPath, string stage)
    {
        if (!Stages.Contains(stage)) throw new ArgumentException("Unknown crash stage");
        string path = Validate(rawPath);
        var service = Open(path);
        service.CommitCheckpointForTests = actual =>
        {
            if (actual != stage) return;
            Console.WriteLine("STAGE4_READY " + stage);
            Console.Out.Flush();
            Thread.Sleep(Timeout.Infinite);
        };

        service.PutItem(new XElement(O + "person",
            new XAttribute("owner", Owner),
            new XAttribute(Rdf + "about", RecordId),
            new XElement(O + "name", "Killed Writer"),
            new XElement(O + "description", Word),
            new XElement(O + "knows", new XAttribute(Rdf + "resource", "target"))));
        // Reaching this line means no checkpoint was executed.
        throw new Exception("Crash checkpoint was not reached");
    }

    private static void CrashAt(string path, string stage)
    {
        var info = new ProcessStartInfo("dotnet")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        info.ArgumentList.Add(Dll);
        info.ArgumentList.Add("stage4-child");
        info.ArgumentList.Add(path);
        info.ArgumentList.Add(stage);
        info.Environment["DOTNET_ROLL_FORWARD"] = "Major";
        using Process child = Process.Start(info) ?? throw new Exception("Child process did not start");
        try
        {
            bool reached = false;
            for (int i = 0; i < 70; i++)
            {
                Task<string?> line = child.StandardOutput.ReadLineAsync();
                if (!line.Wait(TimeSpan.FromSeconds(15)))
                    throw new TimeoutException("Timed out waiting for stage " + stage);
                if (line.Result is null) break;
                if (line.Result == "STAGE4_READY " + stage)
                {
                    reached = true;
                    break;
                }
            }
            if (!reached)
                throw new Exception("Child exited before checkpoint " + stage +
                    "; stderr: " + child.StandardError.ReadToEnd());
            child.Kill(entireProcessTree: true);
            Check(child.WaitForExit(10000), stage + " child was killed");
        }
        finally
        {
            if (!child.HasExited)
            {
                child.Kill(entireProcessTree: true);
                child.WaitForExit();
            }
        }
    }

    private static void Verify(string path, string stage)
    {
        Check(File.Exists(Pending(path)), stage + " unfinished commit marker exists");
        XElement fog = XElement.Load(Fog(path));
        Check(fog.Elements().Count(x =>
            (string?)x.Attribute(Rdf + "about") == RecordId) == 1,
            stage + " authoritative Fog contains one committed record");

        // Reopening through FDataService must rebuild its derived state.
        var service = Open(path);
        Check(service.GetItemByIdBasic(RecordId, false) is not null,
            stage + " record recovered through IFDataService");
        Check(service.SearchByWords(Word)
            .Any(x => (string?)x.Attribute("id") == RecordId),
            stage + " full-text search recovered");
        Check(service.GetItemByIdBasic("target", true)?.Elements("inverse")
            .Any(x => (string?)x.Attribute("prop") == O + "knows") == true,
            stage + " inverse relation recovered");
        Check(!File.Exists(Pending(path)),
            stage + " recovery intent cleared after successful rebuild");
        service.Close();

        bool journalHasEntry = File.Exists(Log(path)) &&
            File.ReadAllText(Log(path)).Contains(RecordId, StringComparison.Ordinal);
        Check(journalHasEntry == (stage == "after-journal"),
            stage + " diagnostic journal reflects checkpoint");

        service = Open(path);
        Check(service.GetItemByIdBasic(RecordId, false) is not null,
            stage + " recovered data survives second reopen");
        service.Close();
    }

    public static void RunAll()
    {
        Directory.CreateDirectory(SafeRoot);
        foreach (string stage in Stages)
        {
            string path = Validate(Path.Combine(SafeRoot, Guid.NewGuid().ToString("N")));
            try
            {
                Setup(path);
                CrashAt(path, stage);
                Verify(path, stage);
            }
            finally
            {
                if (Directory.Exists(path))
                    Directory.Delete(path, recursive: true);
            }
        }
        Console.WriteLine("STAGE4_ALL_PASSED");
    }
}
