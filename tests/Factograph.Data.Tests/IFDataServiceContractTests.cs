using System.Xml.Linq;
using Factograph.Data;
using Xunit;

namespace Factograph.Data.Tests;

/// <summary>
/// Contract assertions exercise IFDataService, not Polar.DB internals.
/// All storage is created in a private temporary workspace.
/// </summary>
public sealed class IFDataServiceContractTests
{
    [Fact]
    public void Initial_data_and_cassette_contract_survive_reopen()
    {
        using var test = new ServiceWorkspace();
        var service = test.Open();
        Assert.Empty(service.Cassettes);
        Assert.True(service.HasWritabeFogForUser("test-owner"));
        Assert.NotNull(service.GetItemByIdBasic("alice", false));
        Assert.True(ServiceWorkspace.ContainsId(service.SearchByName("Ali"), "alice"));
        Assert.True(ServiceWorkspace.ContainsId(service.SearchByWords("ORCHARD"), "alice"));
        Assert.Contains(service.GetItemByIdBasic("bob", true).Elements("inverse"),
            x => (string?)x.Attribute("prop") == "http://fogid.net/o/knows");
        service.Close();

        service = test.Open();
        Assert.True(ServiceWorkspace.ContainsId(service.SearchByWords("ORCHARD"), "alice"));
        service.Close();
    }

    [Fact]
    public void Put_and_update_are_visible_in_Fog_indexes_and_after_reopen()
    {
        using var test = new ServiceWorkspace();
        var service = test.Open();
        var created = service.PutItem(ServiceWorkspace.OwnedItem("charlie", "Charlie", "FIRSTWORD", "bob"));
        Assert.Equal("charlie", (string?)created.Attribute(ServiceWorkspace.Rdf + "about"));
        Assert.True(ServiceWorkspace.ContainsId(service.SearchByName("Char"), "charlie"));
        Assert.True(ServiceWorkspace.ContainsId(service.SearchByWords("FIRSTWORD"), "charlie"));

        service.PutItem(ServiceWorkspace.OwnedItem("charlie", "Charlotte", "SECONDWORD", "alice"));
        Assert.True(ServiceWorkspace.ContainsId(service.SearchByWords("SECONDWORD"), "charlie"));
        Assert.False(ServiceWorkspace.ContainsId(service.SearchByWords("FIRSTWORD"), "charlie"));
        Assert.True(ServiceWorkspace.ContainsId(service.SearchByName("Charlot"), "charlie"));
        Assert.False(ServiceWorkspace.ContainsId(service.SearchByName("Charlie"), "charlie"));
        Assert.Equal("Charlotte", XElement.Load(test.FogPath).Elements()
            .Single(x => (string?)x.Attribute(ServiceWorkspace.Rdf + "about") == "charlie")
            .Element(ServiceWorkspace.O + "name")?.Value);
        service.Close();

        service = test.Open();
        Assert.True(ServiceWorkspace.ContainsId(service.SearchByWords("SECONDWORD"), "charlie"));
        Assert.False(ServiceWorkspace.ContainsId(service.SearchByWords("FIRSTWORD"), "charlie"));
        Assert.True(ServiceWorkspace.ContainsId(service.GetAll(), "charlie"));
        service.Close();
    }

    [Fact]
    public void Unsupported_substitute_does_not_modify_Fog_or_write_log()
    {
        using var test = new ServiceWorkspace();
        var service = test.Open();
        string before = File.ReadAllText(test.FogPath);
        var directive = new XElement("substitute",
            new XAttribute("owner", "test-owner"),
            new XAttribute("old-id", "alice"),
            new XAttribute("new-id", "bob"));
        XElement response = service.PutItem(directive);
        Assert.Equal("error", response.Name.LocalName);
        Assert.Equal(before, File.ReadAllText(test.FogPath));
        Assert.False(File.Exists(test.LogPath));
        Assert.True(ServiceWorkspace.ContainsId(service.SearchByName("Ali"), "alice"));
        service.Close();
    }

    [Fact]
    public void Rejected_writes_do_not_touch_source_or_index()
    {
        using var test = new ServiceWorkspace(writable: false);
        var service = test.Open();
        string before = File.ReadAllText(test.FogPath);
        Assert.False(service.HasWritabeFogForUser("test-owner"));
        XElement response = service.PutItem(ServiceWorkspace.OwnedItem("new", "New", "NEWWORD"));
        Assert.Equal("error", response.Name.LocalName);
        Assert.Equal(before, File.ReadAllText(test.FogPath));
        Assert.False(ServiceWorkspace.ContainsId(service.SearchByWords("NEWWORD"), "new"));
        Assert.False(File.Exists(test.LogPath));
        service.Close();
    }


    [Fact]
    public void Delete_is_persisted_to_Fog_and_survives_reopen()
    {
        using var test = new ServiceWorkspace();
        IFDataService service = test.Open();
        XElement deletion = new("delete",
            new XAttribute("owner", "test-owner"),
            new XAttribute(ServiceWorkspace.Rdf + "about", "alice"));
        XElement result = service.PutItem(deletion);
        Assert.Equal("delete", result.Name.LocalName);
        Assert.Null(service.GetItemByIdBasic("alice", false));
        Assert.False(ServiceWorkspace.ContainsId(service.SearchByName("Ali"), "alice"));
        Assert.False(ServiceWorkspace.ContainsId(service.SearchByWords("ORCHARD"), "alice"));
        Assert.Contains(XElement.Load(test.FogPath).Elements(),
            x => x.Name.LocalName == "delete" && (string?)x.Attribute("id") == "alice");
        service.Close();

        service = test.Open();
        Assert.Null(service.GetItemByIdBasic("alice", false));
        Assert.False(ServiceWorkspace.ContainsId(service.SearchByWords("ORCHARD"), "alice"));
        service.Close();
    }

    [Fact]
    public void Successful_write_produces_a_Fog_entry_and_a_journal_entry()
    {
        using var test = new ServiceWorkspace();
        IFDataService service = test.Open();
        var result = service.PutItem(ServiceWorkspace.OwnedItem("journal-1", "Journaled", "LOGCHECK"));
        Assert.Equal("journal-1", (string?)result.Attribute(ServiceWorkspace.Rdf + "about"));
        Assert.True(File.Exists(test.LogPath));
        string log = File.ReadAllText(test.LogPath);
        Assert.Contains("journal-1", log);
        Assert.Contains("LOGCHECK", log);
        Assert.Single(XElement.Load(test.FogPath).Elements(),
            x => (string?)x.Attribute(ServiceWorkspace.Rdf + "about") == "journal-1");
        Assert.True(ServiceWorkspace.ContainsId(service.SearchByWords("LOGCHECK"), "journal-1"));
        service.Close();
    }

    [Fact]
    public void Failed_Fog_save_does_not_poison_next_write_or_advance_counter()
    {
        using var workspace = new ServiceWorkspace();
        IFDataService service = workspace.Open();
        string original = File.ReadAllText(workspace.FogPath);
        string backup = workspace.FogPath + ".backup";
        File.Move(workspace.FogPath, backup);
        Directory.CreateDirectory(workspace.FogPath);
        try
        {
            Assert.ThrowsAny<IOException>(() =>
                service.PutItem(ServiceWorkspace.OwnedItem(
                    "failed", "ShouldNotAppear", "FAILEDWRITE")));
            Assert.False(ServiceWorkspace.ContainsId(
                service.SearchByWords("FAILEDWRITE"), "failed"));
        }
        finally
        {
            Directory.Delete(workspace.FogPath);
            File.Move(backup, workspace.FogPath);
        }
        Assert.Equal(original, File.ReadAllText(workspace.FogPath));
        Assert.False(File.Exists(workspace.LogPath));

        service.PutItem(ServiceWorkspace.OwnedItem(
            "success", "Recovered", "RECOVEREDWRITE"));
        var persisted = XElement.Load(workspace.FogPath);
        Assert.DoesNotContain(persisted.Elements(),
            x => (string?)x.Attribute(ServiceWorkspace.Rdf + "about") == "failed");
        Assert.Contains(persisted.Elements(),
            x => (string?)x.Attribute(ServiceWorkspace.Rdf + "about") == "success");
        Assert.False(ServiceWorkspace.ContainsId(
            service.SearchByWords("FAILEDWRITE"), "failed"));
        service.Close();

        service = workspace.Open();
        Assert.Null(service.GetItemByIdBasic("failed", false));
        Assert.True(ServiceWorkspace.ContainsId(
            service.SearchByWords("RECOVEREDWRITE"), "success"));
        service.Close();
    }

    [Fact]
    public void Missing_owner_is_rejected_before_persistence()
    {
        using var test = new ServiceWorkspace();
        var service = test.Open();
        string before = File.ReadAllText(test.FogPath);
        XElement result = service.PutItem(ServiceWorkspace.Item("new", "New", "NEWWORD"));
        Assert.Equal("error", result.Name.LocalName);
        Assert.Equal(before, File.ReadAllText(test.FogPath));
        Assert.False(File.Exists(test.LogPath));
        service.Close();
    }
}
