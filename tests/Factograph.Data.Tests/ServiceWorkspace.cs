using System.Xml.Linq;
using Factograph.Data;

namespace Factograph.Data.Tests;

/// <summary>Owns all test resources; no application cassettes or global DB paths.</summary>
internal sealed class ServiceWorkspace : IDisposable
{
    private const string Ontology = "http://fogid.net/o/";
    internal static readonly XNamespace O = Ontology;
    internal static readonly XNamespace Rdf = "http://www.w3.org/1999/02/22-rdf-syntax-ns#";
    private readonly string _path;
    internal readonly string FogPath;
    internal readonly string LogPath;

    internal ServiceWorkspace(bool writable = true)
    {
        _path = Path.Combine(Path.GetTempPath(), "factoria-contract-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_path);
        string db = Path.Combine(_path, "storage");
        Directory.CreateDirectory(db);
        FogPath = Path.Combine(_path, "source.fog");
        LogPath = Path.Combine(_path, "logfile_put.txt");
        new XElement(Rdf + "RDF",
            new XAttribute(XNamespace.Xmlns + "rdf", Rdf.NamespaceName),
            new XAttribute("owner", "test-owner"),
            new XAttribute("prefix", "test-"),
            new XAttribute("counter", "10"),
            Item("alice", "Alice", "ORCHARD", "bob"),
            Item("bob", "Bob", "RIVER")).Save(FogPath);
        new XElement("config",
            new XElement("database", new XAttribute("connectionstring",
                "rr:" + db + Path.DirectorySeparatorChar)),
            new XElement("LoadFog", new XAttribute("write", writable ? "yes" : "no"), FogPath)
            ).Save(Path.Combine(_path, "config.xml"));
        new XElement("Ontology").Save(Path.Combine(_path, "ontology.xml"));
    }

    internal IFDataService Open() => new FDataService(
        _path + Path.DirectorySeparatorChar,
        Path.Combine(_path, "ontology.xml"), dictionary: null);

    internal static XElement Item(string id, string name, string description, string? link = null) =>
        new(O + "person",
            new XAttribute(Rdf + "about", id),
            new XElement(O + "name", name),
            new XElement(O + "description", description),
            link == null ? null : new XElement(O + "knows", new XAttribute(Rdf + "resource", link)));

    internal static XElement OwnedItem(string id, string name, string description, string? link = null)
    {
        var item = Item(id, name, description, link);
        item.Add(new XAttribute("owner", "test-owner"));
        return item;
    }

    internal static bool ContainsId(IEnumerable<XElement> records, string id) =>
        records.Any(record => (string?)record.Attribute("id") == id);

    public void Dispose()
    {
        if (Directory.Exists(_path))
            Directory.Delete(_path, recursive: true);
    }
}
