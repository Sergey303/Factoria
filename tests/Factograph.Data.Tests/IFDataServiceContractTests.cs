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
        Assert.False(File.Exists(test.PendingPath));
        service.Close();
    }

    [Fact]
    public void Failed_Fog_save_does_not_poison_next_write_or_advance_counter()
    {
        using var workspace = new ServiceWorkspace();
        IFDataService service = workspace.Open();
        // Warm the service cache before swapping the destination path.
        service.PutItem(ServiceWorkspace.OwnedItem("warm", "Warm", "WARM"));
        string original = File.ReadAllText(workspace.FogPath);
        string originalLog = File.ReadAllText(workspace.LogPath);
        string backup = workspace.FogPath + ".backup";
        File.Move(workspace.FogPath, backup);
        Directory.CreateDirectory(workspace.FogPath);
        try
        {
            Exception? failure = Record.Exception(() =>
                service.PutItem(ServiceWorkspace.OwnedItem(
                    "failed", "ShouldNotAppear", "FAILEDWRITE")));
            Assert.True(failure is IOException or UnauthorizedAccessException,
                $"Expected an I/O failure, got {failure?.GetType().Name ?? "none"}");
            Assert.False(ServiceWorkspace.ContainsId(
                service.SearchByWords("FAILEDWRITE"), "failed"));
        }
        finally
        {
            Directory.Delete(workspace.FogPath);
            File.Move(backup, workspace.FogPath);
        }
        Assert.Equal(original, File.ReadAllText(workspace.FogPath));
        Assert.Equal(originalLog, File.ReadAllText(workspace.LogPath));
        Assert.True(File.Exists(workspace.PendingPath));

        service.PutItem(ServiceWorkspace.OwnedItem(
            "success", "Recovered", "RECOVEREDWRITE"));
        var persisted = XElement.Load(workspace.FogPath);
        Assert.DoesNotContain(persisted.Elements(),
            x => (string?)x.Attribute(ServiceWorkspace.Rdf + "about") == "failed");
        Assert.Contains(persisted.Elements(),
            x => (string?)x.Attribute(ServiceWorkspace.Rdf + "about") == "success");
        Assert.False(ServiceWorkspace.ContainsId(
            service.SearchByWords("FAILEDWRITE"), "failed"));
        Assert.False(File.Exists(workspace.PendingPath));
        service.Close();

        service = workspace.Open();
        Assert.Null(service.GetItemByIdBasic("failed", false));
        Assert.True(ServiceWorkspace.ContainsId(
            service.SearchByWords("RECOVEREDWRITE"), "success"));
        service.Close();
    }

    [Fact]
    public void Unavailable_journal_rejects_write_before_changing_Fog_and_index()
    {
        using var workspace = new ServiceWorkspace();
        IFDataService service = workspace.Open();
        string original = File.ReadAllText(workspace.FogPath);
        Directory.CreateDirectory(workspace.LogPath);
        try
        {
            Exception? error = Record.Exception(() =>
                service.PutItem(ServiceWorkspace.OwnedItem(
                    "blocked", "Blocked", "JOURNALBLOCKED")));
            Assert.True(error is IOException or UnauthorizedAccessException,
                $"Expected journal I/O failure, got {error?.GetType().Name ?? "none"}");
            Assert.Equal(original, File.ReadAllText(workspace.FogPath));
            Assert.False(ServiceWorkspace.ContainsId(
                service.SearchByWords("JOURNALBLOCKED"), "blocked"));
        }
        finally
        {
            Directory.Delete(workspace.LogPath);
        }
        service.Close();

        service = workspace.Open();
        Assert.Null(service.GetItemByIdBasic("blocked", false));
        service.Close();
    }

    [Fact]
    public void Failed_Fog_publication_does_not_consume_an_auto_generated_identifier()
    {
        using var workspace = new ServiceWorkspace();
        IFDataService service = workspace.Open();
        service.PutItem(ServiceWorkspace.OwnedItem("warm", "Warm", "WARM"));
        string backup = workspace.FogPath + ".backup";
        File.Move(workspace.FogPath, backup);
        Directory.CreateDirectory(workspace.FogPath);
        try
        {
            var candidate = new XElement(ServiceWorkspace.O + "person",
                new XAttribute("owner", "test-owner"),
                new XElement(ServiceWorkspace.O + "name", "FailedAuto"));
            Exception? error = Record.Exception(() => service.PutItem(candidate));
            Assert.True(error is IOException or UnauthorizedAccessException);
        }
        finally
        {
            Directory.Delete(workspace.FogPath);
            File.Move(backup, workspace.FogPath);
        }

        var next = new XElement(ServiceWorkspace.O + "person",
            new XAttribute("owner", "test-owner"),
            new XElement(ServiceWorkspace.O + "name", "GoodAuto"));
        XElement created = service.PutItem(next);
        Assert.Equal("test-10",
            (string?)created.Attribute(ServiceWorkspace.Rdf + "about"));
        Assert.Equal("11", (string?)XElement.Load(workspace.FogPath)
            .Attribute("counter"));
        Assert.DoesNotContain(XElement.Load(workspace.FogPath).Elements(),
            x => x.Element(ServiceWorkspace.O + "name")?.Value == "FailedAuto");
        service.Close();
    }

    [Fact]
    public void Interrupted_Fog_commit_rebuilds_index_from_authoritative_Fog_on_reopen()
    {
        using var workspace = new ServiceWorkspace();
        IFDataService service = workspace.Open();
        Assert.Null(service.GetItemByIdBasic("recovered", false));
        service.Close();

        // Simulate a crash after publishing Fog, before the adapter is updated.
        // Only Factoria-owned files are manipulated, never Polar.DB internals.
        XElement fog = XElement.Load(workspace.FogPath);
        fog.Add(ServiceWorkspace.Item("recovered", "Reborn", "PENDINGCOMMIT", "bob"));
        fog.Save(workspace.FogPath);
        File.WriteAllText(workspace.PendingPath, "pending");

        service = workspace.Open();
        Assert.True(ServiceWorkspace.ContainsId(
            service.SearchByWords("PENDINGCOMMIT"), "recovered"));
        Assert.NotNull(service.GetItemByIdBasic("recovered", false));
        Assert.Contains(service.GetItemByIdBasic("bob", true).Elements("inverse"),
            x => (string?)x.Attribute("prop") == "http://fogid.net/o/knows");
        Assert.False(File.Exists(workspace.PendingPath));
        service.Close();

        service = workspace.Open();
        Assert.True(ServiceWorkspace.ContainsId(
            service.SearchByWords("PENDINGCOMMIT"), "recovered"));
        service.Close();
    }

    [Fact]
    public void Busy_Factoria_writer_lock_blocks_mutations_before_publishing_Fog()
    {
        using var workspace = new ServiceWorkspace();
        IFDataService service = workspace.Open();
        string previous = File.ReadAllText(workspace.FogPath);
        using (var writerLease = new FileStream(workspace.WriterLockPath,
            FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
        {
            Exception? failure = Record.Exception(() =>
                service.PutItem(ServiceWorkspace.OwnedItem(
                    "busy", "Busy", "BUSYWRITER")));
            Assert.IsType<IOException>(failure);
        }

        Assert.Equal(previous, File.ReadAllText(workspace.FogPath));
        Assert.False(File.Exists(workspace.PendingPath));
        Assert.False(ServiceWorkspace.ContainsId(
            service.SearchByWords("BUSYWRITER"), "busy"));
        service.Close();
    }

    [Fact]
    public void Invalid_Fog_during_recovery_keeps_marker_and_retries_after_repair()
    {
        using var workspace = new ServiceWorkspace();
        IFDataService service = workspace.Open();
        service.Close();

        string original = File.ReadAllText(workspace.FogPath);
        File.WriteAllText(workspace.FogPath, "<not-well-formed");
        File.WriteAllText(workspace.PendingPath, "pending");

        Assert.ThrowsAny<Exception>(() => workspace.Open());
        Assert.True(File.Exists(workspace.PendingPath));

        File.WriteAllText(workspace.FogPath, original);
        service = workspace.Open();
        Assert.False(File.Exists(workspace.PendingPath));
        Assert.True(ServiceWorkspace.ContainsId(
            service.SearchByWords("ORCHARD"), "alice"));
        service.Close();
    }

    [Fact]
    public void Previously_opened_writer_must_not_publish_stale_Fog_after_another_commit()
    {
        using var workspace = new ServiceWorkspace();
        IFDataService first = workspace.Open();
        IFDataService second = workspace.Open();

        first.PutItem(ServiceWorkspace.OwnedItem(
            "first-writer", "First Writer", "FIRSTCOMMIT"));

        Exception? stale = Record.Exception(() =>
            second.PutItem(ServiceWorkspace.OwnedItem(
                "second-writer", "Second Writer", "SECONDCOMMIT")));
        Assert.IsType<InvalidOperationException>(stale);
        XElement document = XElement.Load(workspace.FogPath);
        Assert.Contains(document.Elements(),
            x => (string?)x.Attribute(ServiceWorkspace.Rdf + "about") == "first-writer");
        Assert.DoesNotContain(document.Elements(),
            x => (string?)x.Attribute(ServiceWorkspace.Rdf + "about") == "second-writer");

        second.Close();
        first.Close();
        IFDataService restarted = workspace.Open();
        restarted.PutItem(ServiceWorkspace.OwnedItem(
            "second-writer", "Second Writer", "SECONDCOMMIT"));
        Assert.Contains(XElement.Load(workspace.FogPath).Elements(),
            x => (string?)x.Attribute(ServiceWorkspace.Rdf + "about") == "first-writer");
        Assert.Contains(XElement.Load(workspace.FogPath).Elements(),
            x => (string?)x.Attribute(ServiceWorkspace.Rdf + "about") == "second-writer");
        restarted.Close();
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
