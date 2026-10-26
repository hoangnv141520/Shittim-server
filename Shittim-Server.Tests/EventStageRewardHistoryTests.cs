using AutoMapper;
using BlueArchiveAPI.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Schale.Data;
using Schale.Data.GameModel;
using Schale.MappingProfiles;
using Schale.MX.NetworkProtocol;
using Shittim_Server.Core.NetworkProtocol.Handlers;
using Xunit;

namespace Shittim_Server.Tests;

public class EventStageRewardHistoryTests
{
    [Theory]
    [InlineData(836, new long[] { 83601, 83602 })]
    [InlineData(10836, new long[] { 1083601 })]
    [InlineData(999, new long[0])]
    public async Task ReopeningAnEventReturnsOnlyItsPersistedStageRewardClaims(long eventId, long[] expected)
    {
        using var db = NewContext();
        var account = new AccountDBServer { ServerId = 1, Nickname = "Sensei" };
        account.ContentInfo.ReceivedEventStageTotalRewards = new()
        {
            [836] = [83601, 83602],
            [10836] = [1083601]
        };
        var other = new AccountDBServer { ServerId = 2, Nickname = "Other" };
        other.ContentInfo.ReceivedEventStageTotalRewards = new() { [836] = [99999] };
        db.Accounts.AddRange(account, other);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var response = await Handler().AdventureList(db,
            new EventContentAdventureListRequest
            {
                SessionKey = new SessionKey { AccountServerId = 1, MxToken = "test" },
                EventContentId = eventId
            }, new EventContentAdventureListResponse());

        Assert.Equal(expected, response.AlreadyReceiveRewardId);
        Assert.NotNull(response.StageHistoryDBs);
        Assert.NotNull(response.StrategyObjecthistoryDBs);
    }

    [Fact]
    public async Task AnOlderAccountWithoutStageRewardHistoryReturnsAnEmptyList()
    {
        using var db = NewContext();
        var account = new AccountDBServer { ServerId = 1, Nickname = "Sensei" };
        account.ContentInfo.ReceivedEventStageTotalRewards = null!;
        db.Accounts.Add(account);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var response = await Handler().AdventureList(db,
            new EventContentAdventureListRequest
            {
                SessionKey = new SessionKey { AccountServerId = 1, MxToken = "test" },
                EventContentId = 836
            }, new EventContentAdventureListResponse());

        Assert.NotNull(response.AlreadyReceiveRewardId);
        Assert.Empty(response.AlreadyReceiveRewardId);
    }

    private static SchaleDataContext NewContext()
    {
        var db = new SchaleDataContext(new DbContextOptionsBuilder<SchaleDataContext>()
            .UseSqlite("Data Source=:memory:").Options);
        db.Database.OpenConnection();
        db.Database.EnsureCreated();
        return db;
    }

    private static EventContentCampaignHandler Handler()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAutoMapper(cfg => { }, typeof(GameModelsMappingProfile).Assembly);
        var mapper = services.BuildServiceProvider().GetRequiredService<IMapper>();
        return new EventContentCampaignHandler(null!, new FixedSessionService(), null!, mapper);
    }

    private sealed class FixedSessionService : ISessionKeyService
    {
        public Task<AccountDBServer> GetAuthenticatedUser(SchaleDataContext context, SessionKey? sessionKey) =>
            context.Accounts.SingleAsync(x => x.ServerId == sessionKey!.AccountServerId);
        public Task<SessionKey?> GenerateSession(long publisherAccountId, string? customToken = null) => throw new NotSupportedException();
        public bool ValidateRequest(RequestPacket request) => true;
        public void RevokeSession(long userId) { }
        public int PurgeExpiredSessions(TimeSpan maxInactivity) => 0;
    }
}
