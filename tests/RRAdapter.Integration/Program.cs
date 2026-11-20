using Factograph.Data.Adapters;
using System.Xml.Linq;

if (args.Length == 2 && args[0] == "stage3-all")
{
    Stage3Runner.RunAll(int.Parse(args[1]));
    return;
}

if (args.Length > 0 && args[0].StartsWith("stage3-", StringComparison.Ordinal))
{
    Stage3Scenarios.Run(args);
    return;
}

const string ontology = "http://fogid.net/o/";
const string rdf = "http://www.w3.org/1999/02/22-rdf-syntax-ns#";

bool crossVersion = args.Length == 2 && (args[0] == "seed-legacy" || args[0] == "verify-legacy");
if (args.Length != 0 && !crossVersion) throw new ArgumentException("Expected seed-legacy/verify-legacy and a temp database path");
string db = crossVersion ? Path.GetFullPath(args[1]) :
    Path.Combine(Path.GetTempPath(), "factoria-rradapter-integration", Guid.NewGuid().ToString("N"));
string safeRoot = Path.Combine(Path.GetTempPath(), "factoria-rradapter-integration") + Path.DirectorySeparatorChar;
if (!db.StartsWith(safeRoot, StringComparison.Ordinal)) throw new ArgumentException("Test storage must be under its dedicated temporary directory");
Directory.CreateDirectory(db);
string connection = "rr:" + db + Path.DirectorySeparatorChar;

static void Check(bool condition, string name)
{
    if (!condition) throw new Exception("FAIL: " + name);
    Console.WriteLine("PASS: " + name);
}

XElement Item(string id, string name, string description, string? target = null) =>
    new(XName.Get("person", ontology),
        new XAttribute(XName.Get("about", rdf), id),
        new XElement(XName.Get("name", ontology), name),
        new XElement(XName.Get("description", ontology), description),
        target is null ? null : new XElement(XName.Get("knows", ontology), new XAttribute(XName.Get("resource", rdf), target)));

bool HasResult(IEnumerable<XElement> records, string id) =>
    records.Any(r => (string?)r.Attribute("id") == id);

RRAdapter Open() { var adapter = new RRAdapter(null); adapter.Init(connection); return adapter; }

try
{
    Console.WriteLine("TEST DATA ROOT=" + db);
    if (args.Length == 2 && args[0] == "verify-legacy")
    {
        var legacy = Open();
        Check(legacy.GetRecord("alice") is not null, "legacy 2.1.0 primary key reopen");
        Check(HasResult(legacy.SearchByName("Ali"), "alice"), "legacy 2.1.0 name index reopen");
        Check(HasResult(legacy.SearchByWords("ORCHARD"), "alice"), "legacy 2.1.0 word index reopen");
        Check(legacy.GetInverseRecords("bob").Any(), "legacy 2.1.0 inverse index reopen");
        legacy.PutItem(Item("alice", "Alicia North", "Blueberry project", "bob"));
        Check(HasResult(legacy.SearchByName("Alic"), "alice"), "legacy 2.1.0 update with 3.1.1");
        legacy.Close();
        legacy = Open();
        Check(HasResult(legacy.SearchByName("Alic"), "alice"), "legacy 2.1.0 updated name survives reopen");
        Check(HasResult(legacy.SearchByWords("BLUEBERRY"), "alice"), "legacy 2.1.0 updated words survive reopen");
        Check(!HasResult(legacy.SearchByWords("ORCHARD"), "alice"), "legacy 2.1.0 old words excluded");
        Check(legacy.GetInverseRecords("bob").Any(), "legacy 2.1.0 inverse links survive update");
        legacy.Close();
        Console.WriteLine("LEGACY 2.1.0 COMPATIBILITY PASSED");
        return;
    }
    var adapter = Open();
    adapter.StartFillDb(_ => { });
    adapter.LoadXFlow(new[] {
        Item("alice", "Alice North", "Orchard project", "bob"),
        Item("bob", "Bob West", "Museum collection")
    }, new Dictionary<string, string>());
    adapter.FinishFillDb(_ => { });
    if (args.Length == 2 && args[0] == "seed-legacy")
    {
        adapter.Close();
        Console.WriteLine("LEGACY 2.1.0 DATABASE SEEDED");
        return;
    }
    Check(adapter.GetRecord("alice") is not null, "load by primary key");
    Check(HasResult(adapter.SearchByName("Ali"), "alice"), "name prefix search");
    Check(HasResult(adapter.SearchByWords("ORCHARD"), "alice"), "word search");
    Check(adapter.GetInverseRecords("bob").Any(), "inverse links");
    Check(adapter.GetItemByIdBasic("bob", true)?.Elements("inverse").Any() == true, "inverse XML");
    adapter.Close();

    adapter = Open();
    Check(adapter.GetRecord("alice") is not null, "reopen primary key");
    Check(HasResult(adapter.SearchByName("Ali"), "alice"), "reopen name prefix");
    Check(HasResult(adapter.SearchByWords("ORCHARD"), "alice"), "reopen word index");
    Check(adapter.GetInverseRecords("bob").Any(), "reopen inverse index");
    var oldAlice = adapter.GetItemByIdBasic("alice", false)?.ToString();
    bool substituteRejected = false;
    try
    {
        adapter.PutItem(new XElement(XName.Get("substitute", "http://fogid.net/o/"),
            new XAttribute("old-id", "alice"),
            new XAttribute("new-id", "bob")));
    }
    catch (NotSupportedException)
    {
        substituteRejected = true;
    }
    Check(substituteRejected, "substitute explicitly rejected instead of silently ignored");
    bool canonicalSubstituteRejected = false;
    try
    {
        adapter.PutItem(new XElement(XName.Get("substitute", "http://fogid.net/u/"),
            new XAttribute(XName.Get("about", rdf), "alice"),
            new XElement(XName.Get("newid", "http://fogid.net/u/"),
                new XAttribute(XName.Get("resource", rdf), "bob"))));
    }
    catch (NotSupportedException)
    {
        canonicalSubstituteRejected = true;
    }
    Check(canonicalSubstituteRejected, "canonical substitute explicitly rejected");
    Check(adapter.GetItemByIdBasic("alice", false)?.ToString() == oldAlice,
        "rejected substitute does not change existing resource");
    Check(HasResult(adapter.SearchByName("Ali"), "alice"),
        "rejected substitute preserves name index");
    Check(adapter.GetInverseRecords("bob").Any(),
        "rejected substitute preserves inverse index");
    adapter.PutItem(Item("alice", "Alicia North", "Blueberry project", "bob"));
    Check(HasResult(adapter.SearchByName("Alic"), "alice"), "updated name search");
    Check(!HasResult(adapter.SearchByName("Alice"), "alice"), "old name removed from search");
    Check(HasResult(adapter.SearchByWords("BLUEBERRY"), "alice"), "updated word search");
    Check(!HasResult(adapter.SearchByWords("ORCHARD"), "alice"), "old word removed from search");
    adapter.Close();

    adapter = Open();
    Check(HasResult(adapter.SearchByName("Alic"), "alice"), "second reopen updated name");
    Check(HasResult(adapter.SearchByWords("BLUEBERRY"), "alice"), "second reopen updated word");
    adapter.PutItem(new XElement("delete", new XAttribute(XName.Get("about", rdf), "alice")));
    Check(adapter.GetItemByIdBasic("alice", false) is null, "tombstone hides deleted record");
    Check(!HasResult(adapter.SearchByName("Alic"), "alice"), "tombstone hides from name index");
    Check(!adapter.GetInverseRecords("bob").Any(), "tombstone removes inverse index");
    adapter.Close();

    adapter = Open();
    Check(adapter.GetItemByIdBasic("alice", false) is null, "reopen deleted record");
    Check(!HasResult(adapter.SearchByWords("BLUEBERRY"), "alice"), "reopen deleted word index");
    adapter.Close();

    // Full Fog rebuild supplies resolved substitution mappings. RRAdapter must
    // rewrite incoming rdf:resource links before constructing inverse indexes.
    adapter = Open();
    adapter.StartFillDb(_ => { });
    adapter.LoadXFlow(new[] {
        Item("alice", "Alice", "Linked to old identity", "old-bob"),
        Item("bob", "Bob", "Current target")
    }, new Dictionary<string, string> {
        ["old-bob"] = "bob",
        ["deleted-id"] = null!
    });
    adapter.FinishFillDb(_ => { });
    Check(adapter.GetInverseRecords("bob").Any(),
        "substitute rebuild redirects inverse links to replacement");
    Check(!adapter.GetInverseRecords("old-bob").Any(),
        "substitute rebuild removes obsolete inverse links");
    adapter.Close();
    adapter = Open();
    Check(adapter.GetInverseRecords("bob").Any(),
        "substitute redirect survives reopen");
    Check(!adapter.GetInverseRecords("old-bob").Any(),
        "obsolete inverse link stays absent after reopen");
    adapter.Close();
    Console.WriteLine("ALL INTEGRATION CHECKS PASSED");
}
finally
{
    // This test creates its own unique temporary directory and never touches configured production cassettes.
    if (!crossVersion) Directory.Delete(db, recursive: true);
}
