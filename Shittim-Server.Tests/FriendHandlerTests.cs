using AutoMapper;
using System.Collections.Concurrent;
using System.Reflection;
using BlueArchiveAPI.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Schale.Data;
using Schale.Data.GameModel;
using Schale.MappingProfiles;
using Schale.FlatData;
using Schale.MX.NetworkProtocol;
using Shittim_Server.Core.NetworkProtocol.Handlers;
using Xunit;

namespace Shittim_Server.Tests;

public class FriendHandlerTests : IDisposable
{
    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private readonly DbContextOptions<SchaleDataContext> _options;
    private readonly FriendHandler _handler;
    private readonly ConstCommonExcelT _limits = new()
        { MaxFriendsCount = 30, MaxFriendsRequest = 30, MaxBlockedUserCount = 30 };

    public FriendHandlerTests()
    {
        _connection.Open();
        _options = new DbContextOptionsBuilder<SchaleDataContext>().UseSqlite(_connection).Options;
        using var db = NewContext();
        db.Database.EnsureCreated();
        db.Accounts.AddRange(
            new AccountDBServer { ServerId = 1, Nickname = "Sender" },
            new AccountDBServer { ServerId = 2, Nickname = "Recipient", RepresentCharacterServerId = 900,
                LastConnectTime = new DateTime(2026, 1, 1) });
        db.Characters.Add(new CharacterDBServer(2, 10003) { ServerId = 900 });
        db.SaveChanges();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAutoMapper(cfg => { }, typeof(GameModelsMappingProfile).Assembly);
        _handler = new FriendHandler(null!, new FixedSessionService(),
            services.BuildServiceProvider().GetRequiredService<IMapper>(), TestExcels(_limits));
    }

    private static ExcelTableService TestExcels(ConstCommonExcelT limits)
    {
        var excels = new ExcelTableService();
        var cache = (ConcurrentDictionary<Type, object>)typeof(ExcelTableService)
            .GetField("caches", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(excels)!;
        cache[typeof(ConstCommonExcelT)] = new List<ConstCommonExcelT>
        {
            limits
        };
        return excels;
    }

    [ExcelDumpFact]
    public void ShippedFriendLimitsArePositive()
    {
        var limits = ExcelDumps.Service().GetTable<ConstCommonExcelT>().First();
        Assert.True(limits.MaxFriendsCount > 0, $"MaxFriendsCount={limits.MaxFriendsCount}");
        Assert.True(limits.MaxFriendsRequest > 0, $"MaxFriendsRequest={limits.MaxFriendsRequest}");
        Assert.True(limits.MaxBlockedUserCount > 0, $"MaxBlockedUserCount={limits.MaxBlockedUserCount}");
    }

    [Fact]
    public async Task RequestAcceptAndRemovePersistBothSidesAcrossContextReloads()
    {
        using (var db = NewContext())
        {
            var sent = await Send(db, 1, 2);
            Assert.Empty(sent.FriendDBs!);
            Assert.Equal(2, Assert.Single(sent.SentRequestFriendDBs!).AccountId);
        }
        using (var db = NewContext())
        {
            var incoming = await List(db, 2);
            Assert.Equal(1, Assert.Single(incoming.ReceivedRequestFriendDBs!).AccountId);
            var accepted = await _handler.AcceptFriendRequest(db,
                new FriendAcceptFriendRequestRequest { SessionKey = Key(2), TargetAccountId = 1 },
                new FriendAcceptFriendRequestResponse());
            Assert.Equal(1, Assert.Single(accepted.FriendDBs!).AccountId);
            Assert.Empty(accepted.ReceivedRequestFriendDBs!);
        }
        using (var db = NewContext())
        {
            var sender = await List(db, 1);
            var friend = Assert.Single(sender.FriendDBs!);
            Assert.Equal(10003, friend.RepresentCharacterUniqueId);
            Assert.Equal(0, friend.RepresentCharacterCostumeId);
            Assert.Equal(new DateTime(2026, 1, 1), friend.LastConnectTime);
            Assert.Equal(1, friend.FriendCount);
            Assert.Empty(sender.SentRequestFriendDBs!);
            await _handler.Remove(db, new FriendRemoveRequest { SessionKey = Key(1), TargetAccountId = 2 },
                new FriendRemoveResponse());
        }
        using (var db = NewContext())
        {
            Assert.Empty((await List(db, 1)).FriendDBs!);
            Assert.Empty((await List(db, 2)).FriendDBs!);
        }
    }

    [Fact]
    public async Task BlockClearsBothPendingDirectionsAndUnblockDoesNotRestoreThem()
    {
        using (var db = NewContext())
        {
            await Send(db, 1, 2);
            await Send(db, 2, 1);
            var blocked = await _handler.Block(db,
                new FriendBlockRequest { SessionKey = Key(2), TargetAccountId = 1 }, new FriendBlockResponse());
            Assert.Equal(1, Assert.Single(blocked.BlockedUserDBs!).AccountId);
            Assert.Empty(blocked.SentRequestFriendDBs!);
            Assert.Empty(blocked.ReceivedRequestFriendDBs!);
        }
        using (var db = NewContext())
        {
            var other = await List(db, 1);
            Assert.Empty(other.SentRequestFriendDBs!);
            Assert.Empty(other.ReceivedRequestFriendDBs!);
            await Assert.ThrowsAsync<WebAPIException>(() => Send(db, 1, 2));
            await Assert.ThrowsAsync<WebAPIException>(() => _handler.GetFriendDetailedInfo(db,
                new FriendGetFriendDetailedInfoRequest { SessionKey = Key(1), FriendAccountId = 2 },
                new FriendGetFriendDetailedInfoResponse()));
            await _handler.Unblock(db,
                new FriendUnblockRequest { SessionKey = Key(2), TargetAccountId = 1 }, new FriendUnblockResponse());
        }
        using (var db = NewContext())
        {
            Assert.Empty((await List(db, 2)).BlockedUserDBs!);
            Assert.Empty((await List(db, 1)).FriendDBs!);
            Assert.Single((await Send(db, 1, 2)).SentRequestFriendDBs!);
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task CancelOrDeclineRemovesBothPendingSides(bool cancel)
    {
        using (var db = NewContext())
        {
            await Send(db, 1, 2);
            if (cancel)
                await _handler.CancelFriendRequest(db,
                    new FriendCancelFriendRequestRequest { SessionKey = Key(1), TargetAccountId = 2 },
                    new FriendCancelFriendRequestResponse());
            else
                await _handler.DeclineFriendRequest(db,
                    new FriendDeclineFriendRequestRequest { SessionKey = Key(2), TargetAccountId = 1 },
                    new FriendDeclineFriendRequestResponse());
        }
        using var reload = NewContext();
        Assert.Empty((await List(reload, 1)).SentRequestFriendDBs!);
        Assert.Empty((await List(reload, 2)).ReceivedRequestFriendDBs!);
        await Assert.ThrowsAsync<WebAPIException>(() => _handler.AcceptFriendRequest(reload,
            new FriendAcceptFriendRequestRequest { SessionKey = Key(2), TargetAccountId = 1 },
            new FriendAcceptFriendRequestResponse()));
    }

    [Fact]
    public async Task AutoAcceptCreatesOneMutualFriendAndRejectsDuplicateOrSelfRequests()
    {
        using (var db = NewContext())
        {
            var target = db.Accounts.Single(x => x.ServerId == 2);
            target.ContentInfo.IdCard.AutoAcceptFriendRequest = true;
            db.Entry(target).Property(x => x.ContentInfo).IsModified = true;
            db.SaveChanges();
            var result = await Send(db, 1, 2);
            Assert.Single(result.FriendDBs!);
            Assert.Empty(result.SentRequestFriendDBs!);
            await Assert.ThrowsAsync<WebAPIException>(() => Send(db, 1, 2));
            await Assert.ThrowsAsync<WebAPIException>(() => Send(db, 1, 1));
        }
        using var reload = NewContext();
        Assert.Single((await List(reload, 2)).FriendDBs!);
    }

    [Fact]
    public async Task RepeatedPendingSendDoesNotDuplicateOrFabricateAFriendship()
    {
        using var db = NewContext();
        await Send(db, 1, 2);
        await Send(db, 1, 2);
        Assert.Single((await List(db, 1)).SentRequestFriendDBs!);
        Assert.Single((await List(db, 2)).ReceivedRequestFriendDBs!);
        Assert.Empty((await List(db, 1)).FriendDBs!);
    }

    [Fact]
    public async Task PendingRequestsSetTheCheckNotificationAndClearItAfterCancel()
    {
        using var db = NewContext();
        await Send(db, 1, 2);
        var check = await _handler.Check(db, new FriendCheckRequest { SessionKey = Key(2) }, new FriendCheckResponse());
        Assert.True(check.ServerNotification.HasFlag(ServerNotificationFlag.HasFriendRequest));
        await _handler.CancelFriendRequest(db,
            new FriendCancelFriendRequestRequest { SessionKey = Key(1), TargetAccountId = 2 },
            new FriendCancelFriendRequestResponse());
        check = await _handler.Check(db, new FriendCheckRequest { SessionKey = Key(2) }, new FriendCheckResponse());
        Assert.False(check.ServerNotification.HasFlag(ServerNotificationFlag.HasFriendRequest));
    }

    [Theory]
    [InlineData("friend")]
    [InlineData("request")]
    [InlineData("block")]
    public async Task TableLimitsRejectChangesWithoutWritingPartialRelationships(string limit)
    {
        using var db = NewContext();
        if (limit == "friend") _limits.MaxFriendsCount = 0;
        if (limit == "request") _limits.MaxFriendsRequest = 0;
        if (limit == "block") _limits.MaxBlockedUserCount = 0;
        if (limit == "block")
            await Assert.ThrowsAsync<WebAPIException>(() => _handler.Block(db,
                new FriendBlockRequest { SessionKey = Key(1), TargetAccountId = 2 }, new FriendBlockResponse()));
        else
            await Assert.ThrowsAsync<WebAPIException>(() => Send(db, 1, 2));

        using var reload = NewContext();
        var sender = await List(reload, 1);
        Assert.Empty(sender.FriendDBs!);
        Assert.Empty(sender.SentRequestFriendDBs!);
        Assert.Empty(sender.BlockedUserDBs!);
        Assert.Empty((await List(reload, 2)).ReceivedRequestFriendDBs!);
    }

    private SchaleDataContext NewContext() => new(_options);
    private static SessionKey Key(long id) => new() { AccountServerId = id, MxToken = "test" };
    private Task<FriendListResponse> List(SchaleDataContext db, long id) => _handler.List(db,
        new FriendListRequest { SessionKey = Key(id) }, new FriendListResponse());
    private Task<FriendSendFriendRequestResponse> Send(SchaleDataContext db, long from, long to) =>
        _handler.SendFriendRequest(db,
            new FriendSendFriendRequestRequest { SessionKey = Key(from), TargetAccountId = to },
            new FriendSendFriendRequestResponse());

    private class FixedSessionService : ISessionKeyService
    {
        public Task<AccountDBServer> GetAuthenticatedUser(SchaleDataContext db, SessionKey? key) =>
            db.Accounts.SingleAsync(x => x.ServerId == key!.AccountServerId);
        public Task<SessionKey?> GenerateSession(long publisherAccountId, string? customToken = null) => throw new NotSupportedException();
        public bool ValidateRequest(RequestPacket request) => true;
        public void RevokeSession(long userId) { }
        public int PurgeExpiredSessions(TimeSpan maxInactivity) => 0;
    }

    public void Dispose() => _connection.Dispose();
}
