using System.Xml.Linq;
using Factograph.Data.Adapters;

internal static class Stage3Scenarios
{
    private const string Ontology = "http://fogid.net/o/";
    private const string Rdf = "http://www.w3.org/1999/02/22-rdf-syntax-ns#";
    private const string Root = "factoria-rradapter-integration";

    private static string Id(int i) => $"record-{i:D6}";
    private static string Name(int i, string prefix) => $"{prefix} Person {i:D6}";

    private static XElement Item(int i, string prefix, string word) =>
        new(XName.Get("person", Ontology),
            new XAttribute(XName.Get("about", Rdf), Id(i)),
            new XElement(XName.Get("name", Ontology), Name(i, prefix)),
            new XElement(XName.Get("description", Ontology), word),
            i == 0 ? null : new XElement(XName.Get("knows", Ontology),
                new XAttribute(XName.Get("resource", Rdf), Id(0))));

    private static void Check(bool actual, string label)
    {
        if (!actual) throw new Exception("FAIL " + label);
        Console.WriteLine("PASS " + label);
    }

    private static RRAdapter Open(string path)
    {
        var adapter = new RRAdapter(null);
        adapter.Init("rr:" + path + Path.DirectorySeparatorChar);
        return adapter;
    }

    public static void Run(string[] args)
    {
        if (args.Length != 3 || !int.TryParse(args[2], out int count) || count is < 100 or > 50000)
            throw new ArgumentException("stage3-seed|verify|crash-write|crash-verify path recordCount(100..50000)");
        string path = Path.GetFullPath(args[1]);
        string root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), Root)) + Path.DirectorySeparatorChar;
        if (!path.StartsWith(root, StringComparison.Ordinal) || path == root)
            throw new ArgumentException("Refusing to open a database outside the dedicated test directory");
        Directory.CreateDirectory(path);
        Console.WriteLine($"STAGE3 mode={args[0]} count={count} path={path}");
        switch (args[0])
        {
            case "stage3-seed": Seed(path, count); break;
            case "stage3-verify": Verify(path, count); break;
            case "stage3-crash-write": CrashWrite(path, count); break;
            case "stage3-crash-verify": CrashVerify(path, count); break;
            default: throw new ArgumentException("Unknown stage3 mode");
        }
    }

    private static void Seed(string path, int count)
    {
        var a = Open(path);
        a.StartFillDb(_ => { });
        a.LoadXFlow(Enumerable.Range(0, count).Select(i => Item(i, "Archive", "BULKSEEDWORD")),
            new Dictionary<string, string>());
        a.FinishFillDb(_ => { });
        Check(a.GetRecord(Id(count - 1)) is not null, "bulk seed last primary key");
        a.Close();
        Console.WriteLine("STAGE3_SEEDED");
    }

    private static void Verify(string path, int count)
    {
        var a = Open(path);
        Check(a.GetRecord(Id(count - 1)) is not null, "bulk reopen last primary key");
        Check(a.SearchByName(Name(count - 1, "Archive")).Any(), "bulk reopen name index");
        Check(a.SearchByWords("BULKSEEDWORD").Count() == count, "bulk reopen word count");
        Check(a.GetInverseRecords(Id(0)).Count() == count - 1, "bulk inverse link count");
        int updates = 0;
        for (int i = 1; i < count; i += 10)
        {
            a.PutItem(Item(i, "Updated", "BULKUPDATEDWORD"));
            updates++;
        }
        Check(a.SearchByWords("BULKUPDATEDWORD").Count() == updates, "bulk updated word count");
        Check(a.SearchByWords("BULKSEEDWORD").Count() == count - updates, "bulk stale word exclusion");
        a.Close();
        a = Open(path);
        Check(a.SearchByWords("BULKUPDATEDWORD").Count() == updates, "bulk updated words survive reopen");
        Check(a.SearchByWords("BULKSEEDWORD").Count() == count - updates, "bulk stale words remain excluded");
        Check(a.SearchByName(Name(1, "Updated")).Any(), "bulk updated name survives reopen");
        Check(!a.SearchByName(Name(1, "Archive")).Any(), "bulk stale name excluded");
        Check(a.GetInverseRecords(Id(0)).Count() == count - 1, "bulk inverse links survive reopen");
        a.Close();
        Console.WriteLine("STAGE3_BULK_PASSED");
    }

    private static void CrashWrite(string path, int count)
    {
        var a = Open(path);
        for (int i = 1; i < count && i <= 100; i++)
            a.PutItem(Item(i, "Crash", "CRASHDURABLEWORD"));
        Console.WriteLine("STAGE3_CRASH_READY");
        Console.Out.Flush();
        Thread.Sleep(Timeout.Infinite); // supervisor must send SIGKILL, not a clean Close
    }

    private static void CrashVerify(string path, int count)
    {
        var a = Open(path);
        int changed = Math.Min(count - 1, 100);
        Check(a.SearchByWords("CRASHDURABLEWORD").Count() == changed, "unclean stop durable word count");
        Check(a.SearchByName(Name(1, "Crash")).Any(), "unclean stop durable name");
        Check(!a.SearchByWords("BULKSEEDWORD").Any(r => (string?)r.Attribute("id") == Id(1)),
            "unclean stop excludes stale word");
        Check(a.GetInverseRecords(Id(0)).Count() == count - 1, "unclean stop inverse links");
        a.Close();
        a = Open(path);
        Check(a.SearchByWords("CRASHDURABLEWORD").Count() == changed, "second reopen after crash");
        a.Close();
        Console.WriteLine("STAGE3_CRASH_RECOVERY_PASSED");
    }
}
