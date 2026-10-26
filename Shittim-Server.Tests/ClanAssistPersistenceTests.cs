using BlueArchiveAPI.Services;
using Microsoft.EntityFrameworkCore;
using Schale.Data;
using Schale.Data.GameModel;
using Schale.FlatData;
using Schale.MX.GameLogic.DBModel;
using Schale.MX.NetworkProtocol;
using Shittim_Server.Core.NetworkProtocol.Handlers;
using Xunit;

namespace Shittim_Server.Tests;

public class ClanAssistPersistenceTests
{
    [Fact]
    public async Task AssignmentSurvivesReloadAndIsReturnedByBothLists()
    {
        using var db = Context();
        var key = Seed(db);
        var character = db.Characters.Single();
        var handler = Handler();
        var result = await handler.SetAssist(db, Request(key, character.ServerId), new());
        db.ChangeTracker.Clear();

        var ownList = await handler.MyAssistList(db, new() { SessionKey = key }, new());
        var login = await handler.Login(db, new() { SessionKey = key }, new());
        Assert.Equal(character.ServerId, Assert.Single(ownList.ClanAssistSlotDBs!).CharacterDBId);
        Assert.Equal(result.ClanAssistSlotDB!.DeployDate, Assert.Single(login.ClanAssistSlotDBs!).DeployDate);
    }

    [Fact]
    public async Task RetryingAnAssignmentPreservesDeploymentTimeAndClearingRemovesIt()
    {
        using var db = Context();
        var key = Seed(db);
        var characterId = db.Characters.Single().ServerId;
        var handler = Handler();
        await handler.SetAssist(db, Request(key, characterId), new());
        var account = db.Accounts.Single();
        var deployed = new DateTime(2026, 10, 20, 4, 0, 0);
        account.ContentInfo.ClanAssistSlots.Single().DeployDate = deployed;
        db.Entry(account).Property(x => x.ContentInfo).IsModified = true;
        db.SaveChanges();

        var retry = await handler.SetAssist(db, Request(key, characterId), new());
        Assert.Equal(deployed, retry.ClanAssistSlotDB!.DeployDate);
        Assert.Single(account.ContentInfo.ClanAssistSlots);

        await handler.SetAssist(db, Request(key, 0), new());
        db.ChangeTracker.Clear();
        Assert.Empty((await handler.MyAssistList(db, new() { SessionKey = key }, new())).ClanAssistSlotDBs!);
    }

    [Fact]
    public async Task AnotherAccountsCharacterCannotBeDeployedAndDuplicateSlotIsRejected()
    {
        using var db = Context();
        var key = Seed(db);
        db.Accounts.Add(new AccountDBServer { ServerId = 2, Nickname = "Other" });
        var foreign = new CharacterDBServer(2, 10001);
        db.Characters.Add(foreign);
        db.SaveChanges();
        await Assert.ThrowsAsync<WebAPIException>(() => Handler().SetAssist(db, Request(key, foreign.ServerId), new()));
        var character = db.Characters.Single(x => x.AccountServerId == 1);
        await Handler().SetAssist(db, Request(key, character.ServerId), new());
        var duplicate = Request(key, character.ServerId);
        duplicate.SlotNumber = 2;
        await Assert.ThrowsAsync<WebAPIException>(() => Handler().SetAssist(db, duplicate, new()));
        Assert.Single(db.Accounts.Single(x => x.ServerId == 1).ContentInfo.ClanAssistSlots);
    }

    [Fact]
    public void OlderAccountJsonDefaultsToAnEmptyAssistList()
    {
        var info = System.Text.Json.JsonSerializer.Deserialize<ContentInfoDB>("{}");
        Assert.Empty(info!.ClanAssistSlots);
    }

    private static ClanSetAssistRequest Request(SessionKey key, long characterId) => new()
    {
        SessionKey = key, EchelonType = EchelonType.Raid, SlotNumber = 1, CharacterDBId = characterId
    };

    private static ClanHandler Handler() => new(null!, new Session(), null!, null!, null!, null!);

    private static SchaleDataContext Context()
    {
        var db = new SchaleDataContext(new DbContextOptionsBuilder<SchaleDataContext>()
            .UseSqlite("Data Source=:memory:").Options);
        db.Database.OpenConnection();
        db.Database.EnsureCreated();
        return db;
    }

    private static SessionKey Seed(SchaleDataContext db)
    {
        db.Accounts.Add(new AccountDBServer { ServerId = 1, Nickname = "Sensei" });
        db.Characters.Add(new CharacterDBServer(1, 10000));
        db.SaveChanges();
        return new SessionKey { AccountServerId = 1, MxToken = "test" };
    }

    private sealed class Session : ISessionKeyService
    {
        public Task<AccountDBServer> GetAuthenticatedUser(SchaleDataContext context, SessionKey? key) =>
            context.Accounts.SingleAsync(x => x.ServerId == key!.AccountServerId);
        public Task<SessionKey?> GenerateSession(long id, string? customToken = null) => throw new NotSupportedException();
        public bool ValidateRequest(RequestPacket request) => true;
        public void RevokeSession(long id) { }
        public int PurgeExpiredSessions(TimeSpan maxInactivity) => 0;
    }
}
