using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Moonlace.Core.Models;
using Moonlace.Core.Session;

namespace Moonlace.Core.Tests;

/// <summary>
/// Session lifecycle tools: launch-scoped sessions, reconnecting to previous
/// ones, the touched-file listing, retention pruning, and migration of the
/// pre-2.3 flat item-directory layout.
/// </summary>
public sealed class SessionToolsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "moonlace-session-tools-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    private SessionService Create() => new(NullLogger<SessionService>.Instance, _root);

    private static EquipmentItem Item(uint rowId, string name) => new()
    {
        RowId = rowId, Name = name, Slot = EquipSlot.Body,
        ModelId = 100, SecondaryId = 0, Variant = 1,
    };

    [Fact]
    public void LaunchWithoutEditsLeavesNothingOnDisk()
    {
        var service = Create();
        service.ActivateForItem(Item(1, "Coat"));

        Assert.False(Directory.Exists(_root) && Directory.GetDirectories(_root).Length > 0);
        Assert.Empty(service.ListPreviousSessions());
        Assert.Empty(service.GetTouchedAssets());
    }

    [Fact]
    public void StartNewSessionClearsTheWorktree()
    {
        var service = Create();
        service.ActivateForItem(Item(1, "Coat"));
        service.StoreAsset("chara/equipment/e0100/model/c0101e0100_top.mdl", SessionAssetKind.Model, [1, 2, 3]);
        var firstId = service.CurrentSessionId;

        service.StartNewSession();

        Assert.NotEqual(firstId, service.CurrentSessionId);
        Assert.False(service.IsDirty);
        Assert.Empty(service.Entries);
        Assert.Null(service.TryReadAsset("chara/equipment/e0100/model/c0101e0100_top.mdl"));

        var previous = Assert.Single(service.ListPreviousSessions());
        Assert.Equal(firstId, previous.Id);
        Assert.Equal(1, previous.FileCount);
        Assert.Equal("Coat", Assert.Single(previous.ItemNames));
        Assert.Equal(3, previous.TotalBytes);
    }

    [Fact]
    public void ConnectToPreviousSessionRestoresItsEdits()
    {
        var service = Create();
        service.ActivateForItem(Item(1, "Coat"));
        service.StoreAsset("chara/equipment/e0100/model/c0101e0100_top.mdl", SessionAssetKind.Model, [4, 5]);
        var firstId = service.CurrentSessionId;

        service.StartNewSession();
        var changes = 0;
        service.SessionChanged += () => changes++;
        service.ConnectToSession(firstId);

        Assert.Equal(firstId, service.CurrentSessionId);
        Assert.Equal(1, changes);
        Assert.True(service.IsDirty);
        Assert.Equal([4, 5], service.TryReadAsset("chara/equipment/e0100/model/c0101e0100_top.mdl"));
        Assert.Empty(service.ListPreviousSessions());
    }

    [Fact]
    public void ConnectingToAMissingSessionThrows()
    {
        var service = Create();
        Assert.Throws<InvalidOperationException>(() => service.ConnectToSession("20200101-000000"));
    }

    [Fact]
    public void TouchedAssetsAggregateAcrossItems()
    {
        var service = Create();
        service.ActivateForItem(Item(1, "Coat"));
        service.StoreAsset("chara/equipment/e0100/model/c0101e0100_top.mdl", SessionAssetKind.Model, [1]);
        service.ActivateForItem(Item(2, "Boots"));
        service.StoreAsset("chara/equipment/e0200/material/v0001/mt_c0101e0200_sho_a.mtrl", SessionAssetKind.Material, [2]);

        var touched = service.GetTouchedAssets();

        Assert.Equal(2, touched.Count);
        Assert.Equal("Boots", touched[0].ItemName);
        Assert.Equal(SessionAssetKind.Material, touched[0].Kind);
        Assert.Equal("Coat", touched[1].ItemName);
        Assert.Equal("chara/equipment/e0100/model/c0101e0100_top.mdl", touched[1].GamePath);
    }

    [Fact]
    public void NewLaunchesSeePreviousLaunchesSessions()
    {
        var first = Create();
        first.ActivateForItem(Item(1, "Coat"));
        first.StoreAsset("chara/equipment/e0100/model/c0101e0100_top.mdl", SessionAssetKind.Model, [9]);
        var firstId = first.CurrentSessionId;

        // A later launch: fresh service over the same root starts a new session.
        var second = Create();
        Assert.NotEqual(firstId, second.CurrentSessionId);
        var previous = Assert.Single(second.ListPreviousSessions());
        Assert.Equal(firstId, previous.Id);

        second.ActivateForItem(Item(1, "Coat"));
        second.ConnectToSession(firstId);
        Assert.Equal([9], second.TryReadAsset("chara/equipment/e0100/model/c0101e0100_top.mdl"));
    }

    [Fact]
    public void LegacyFlatLayoutMigratesIntoAConnectableSession()
    {
        // Pre-2.3 layout: item directories directly under the sessions root.
        var legacyDir = Path.Combine(_root, "item-7");
        Directory.CreateDirectory(legacyDir);
        var manifest = new SessionManifest
        {
            ItemRowId = 7,
            ItemName = "Old Coat",
            Entries = [new SessionEntry("chara/equipment/e0007/model/c0101e0007_top.mdl", SessionAssetKind.Model, "old.mdl", 1)],
        };
        File.WriteAllText(Path.Combine(legacyDir, "manifest.json"), JsonSerializer.Serialize(manifest));
        File.WriteAllBytes(Path.Combine(legacyDir, "old.mdl"), [7, 7]);

        var service = Create();

        Assert.False(Directory.Exists(legacyDir));
        var migrated = Assert.Single(service.ListPreviousSessions());
        Assert.Equal("Old Coat", Assert.Single(migrated.ItemNames));

        service.ActivateForItem(Item(7, "Old Coat"));
        service.ConnectToSession(migrated.Id);
        Assert.Equal([7, 7], service.TryReadAsset("chara/equipment/e0007/model/c0101e0007_top.mdl"));
    }

    [Fact]
    public void PruneDeletesOnlyExpiredSessions()
    {
        var service = Create();
        service.ActivateForItem(Item(1, "Coat"));
        service.StoreAsset("a/path.mdl", SessionAssetKind.Model, [1]);
        var oldId = service.CurrentSessionId;
        service.StartNewSession();
        service.StoreAsset("a/path.mdl", SessionAssetKind.Model, [2]);
        var recentId = service.CurrentSessionId;
        service.StartNewSession();

        // Backdate the first session's metadata past a one-week retention.
        var metaFile = Path.Combine(_root, oldId, "session.json");
        var stale = new SessionMetadata
        {
            CreatedAtUtc = DateTime.UtcNow.AddDays(-30),
            LastUsedAtUtc = DateTime.UtcNow.AddDays(-8),
        };
        File.WriteAllText(metaFile, JsonSerializer.Serialize(stale));

        var removed = service.PruneExpiredSessions(TimeSpan.FromDays(7));

        Assert.Equal(1, removed);
        Assert.False(Directory.Exists(Path.Combine(_root, oldId)));
        var kept = Assert.Single(service.ListPreviousSessions());
        Assert.Equal(recentId, kept.Id);
    }

    [Fact]
    public void PruneNeverDeletesTheCurrentSession()
    {
        var service = Create();
        service.ActivateForItem(Item(1, "Coat"));
        service.StoreAsset("a/path.mdl", SessionAssetKind.Model, [1]);
        var currentId = service.CurrentSessionId;

        var removed = service.PruneExpiredSessions(TimeSpan.Zero);

        Assert.Equal(0, removed);
        Assert.True(Directory.Exists(Path.Combine(_root, currentId)));
    }

    [Fact]
    public void DiscardingTheOnlyItemRemovesTheSessionEntirely()
    {
        var service = Create();
        service.ActivateForItem(Item(1, "Coat"));
        service.StoreAsset("a/path.mdl", SessionAssetKind.Model, [1]);
        var id = service.CurrentSessionId;

        service.DiscardActiveSession();

        Assert.False(Directory.Exists(Path.Combine(_root, id)));
        Assert.Empty(service.GetTouchedAssets());
    }
}
