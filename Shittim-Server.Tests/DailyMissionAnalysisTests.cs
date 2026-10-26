using AutoMapper;
using BlueArchiveAPI.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Schale.Data;
using Schale.Data.GameModel;
using Schale.Data.ModelMapping;
using Schale.FlatData;
using Schale.MappingProfiles;
using Schale.MX.NetworkProtocol;
using Shittim_Server.Core.NetworkProtocol.Handlers;
using Shittim_Server.Services;
using Xunit;

namespace Shittim_Server.Tests;

public class DailyMissionTests
{
    private readonly IMapper _mapper = BuildMapper();
    private readonly ExcelTableService _excel = ExcelDumps.Service();

    private static IMapper BuildMapper()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAutoMapper(cfg => { }, typeof(GameModelsMappingProfile).Assembly);
        return services.BuildServiceProvider().GetRequiredService<IMapper>();
    }

    private static SchaleDataContext NewContext()
    {
        var path = Path.Combine(Path.GetTempPath(), $"shittim-dailymission-{Guid.NewGuid():N}.sqlite3");
        var context = new SchaleDataContext(
            new DbContextOptionsBuilder<SchaleDataContext>().UseSqlite($"Data Source={path}").Options);

        context.Database.EnsureCreated();
        return context;
    }

    private static AccountDBServer NewAccount(SchaleDataContext db)
    {
        var account = new AccountDBServer { ServerId = 1, Nickname = "Sensei" };
        db.Accounts.Add(account);
        db.Currencies.Add(new AccountCurrencyDBServer(account.ServerId));
        db.SaveChanges();
        return account;
    }

    [ExcelDumpFact]
    public void DailyFixedProgressIsUpdatedOnDailyMissionFulfill()
    {
        using var db = NewContext();
        var account = NewAccount(db);
        var missionService = new MissionService(_excel, _mapper);

        // DailyFixed mission 1701: CompleteConditionType = Reset_DailyMissionFulfill, count = 8
        var updated = missionService.UpdateMissionProgress(db, account, MissionCompleteConditionType.Reset_DailyMissionFulfill, 1);

        var row = db.MissionProgresses.Local.FirstOrDefault(m => m.MissionUniqueId == 1701);
        Assert.NotNull(row);
        Assert.Equal(1, row.ProgressParameters![0]);
        Assert.False(row.Complete);

        // Advance to 8
        updated = missionService.UpdateMissionProgress(db, account, MissionCompleteConditionType.Reset_DailyMissionFulfill, 7);
        Assert.True(row.Complete);
    }

    [ExcelDumpFact]
    public void ResetCompleteMissionTracksAggregatedProgressUnderKeyZero()
    {
        using var db = NewContext();
        var account = NewAccount(db);
        var missionService = new MissionService(_excel, _mapper);

        // Mission 1514 requires 5 missions from list [1502, 1503, ... 1513, etc.]
        // Ticking with candidate mission 1513
        var updated = missionService.UpdateMissionProgress(db, account, MissionCompleteConditionType.Reset_CompleteMission, 1, 1513);
        var row = db.MissionProgresses.Local.FirstOrDefault(m => m.MissionUniqueId == 1514);
        Assert.NotNull(row);
        Assert.Equal(1, row.ProgressParameters![0]);
        Assert.False(row.Complete);

        // Ticking with another candidate mission 1502
        updated = missionService.UpdateMissionProgress(db, account, MissionCompleteConditionType.Reset_CompleteMission, 1, 1502);
        Assert.Equal(2, row.ProgressParameters[0]);
        Assert.False(row.Complete);

        // Ticking 3 more
        missionService.UpdateMissionProgress(db, account, MissionCompleteConditionType.Reset_CompleteMission, 1, 1503);
        missionService.UpdateMissionProgress(db, account, MissionCompleteConditionType.Reset_CompleteMission, 1, 1504);
        missionService.UpdateMissionProgress(db, account, MissionCompleteConditionType.Reset_CompleteMission, 1, 1505);

        Assert.Equal(5, row.ProgressParameters[0]);
        Assert.True(row.Complete);
    }

    [ExcelDumpFact]
    public void DailySuddenMissionProgressIsTracked()
    {
        using var db = NewContext();
        var account = NewAccount(db);
        var missionService = new MissionService(_excel, _mapper);

        // Mission 1601 / 1301: Reset_CraftCount (count 1)
        var updated = missionService.UpdateMissionProgress(db, account, MissionCompleteConditionType.Reset_CraftCount, 1);

        var row = db.MissionProgresses.Local.FirstOrDefault(m => m.MissionUniqueId == 1601);
        Assert.NotNull(row);
        Assert.True(row.Complete);
    }

    [ExcelDumpFact]
    public async Task DailyTabClaimsFixedAndSuddenMissionsAndAwardsTheirCurrencies()
    {
        using var db = NewContext();
        var account = NewAccount(db);
        var missions = _excel.GetTable<MissionExcelT>().Where(x => x.Id == 1701 || x.Id == 1601).ToList();
        Assert.Equal(2, missions.Count);
        foreach (var mission in missions)
            AddComplete(db, account, mission);
        db.SaveChanges();

        var response = await Handler().MultipleReward(db,
            new MissionMultipleRewardRequest { SessionKey = Key(account), MissionCategory = MissionCategory.Daily },
            new MissionMultipleRewardResponse());

        Assert.Equal(new long[] { 1601, 1701 }, response.AddedHistoryDBs!.Select(x => x.MissionUniqueId).Order());
        Assert.NotNull(response.ParcelResultDB);
        AssertCurrencyRewards(db, missions);
        db.ChangeTracker.Clear();
        Assert.Empty(db.MissionProgresses.Where(x => x.MissionUniqueId == 1701 || x.MissionUniqueId == 1601));
    }

    [ExcelDumpFact]
    public async Task ClaimingFiveDailyMissionsCompletesMetaMissionAndItsRewardCanBeClaimed()
    {
        using var db = NewContext();
        var account = NewAccount(db);
        var meta = _excel.GetTable<MissionExcelT>().Single(x => x.Id == 1514);
        var candidates = _excel.GetTable<MissionExcelT>()
            .Where(x => meta.CompleteConditionParameter.Contains(x.Id))
            .Take((int)meta.CompleteConditionCount).ToList();
        Assert.Equal(meta.CompleteConditionCount, candidates.Count);
        foreach (var mission in candidates)
            AddComplete(db, account, mission);
        db.SaveChanges();

        var response = await Handler().MultipleReward(db,
            new MissionMultipleRewardRequest { SessionKey = Key(account), MissionCategory = MissionCategory.Daily },
            new MissionMultipleRewardResponse());

        Assert.Contains(response.MissionProgressDBs!, x => x.MissionUniqueId == meta.Id && x.Complete);
        db.ChangeTracker.Clear();
        Assert.True(db.MissionProgresses.Single(x => x.MissionUniqueId == meta.Id).Complete);
        var beforeReward = db.Currencies.Single().CurrencyDict.ToDictionary();

        var reward = await Handler().Reward(db,
            new MissionRewardRequest { SessionKey = Key(account), MissionUniqueId = meta.Id },
            new MissionRewardResponse());

        Assert.NotNull(reward.ParcelResultDB);
        Assert.Equal(meta.Id, reward.AddedHistoryDB!.MissionUniqueId);
        AssertCurrencyRewards(db, [meta], beforeReward);
    }

    [ExcelDumpFact]
    public void ClaimedDailyDoesNotReappearUntilItsClaimWindowExpires()
    {
        using var db = NewContext();
        var account = NewAccount(db);
        var service = new MissionService(_excel, _mapper);
        var history = new MissionHistoryDBServer
        {
            AccountServerId = account.ServerId,
            MissionUniqueId = 1701,
            CompleteTime = account.GameSettings.ServerDateTime()
        };
        db.MissionHistories.Add(history);

        // Pending histories also matter: claim-all updates meta missions before its final save.
        Assert.DoesNotContain(service.UpdateMissionProgress(db, account,
            MissionCompleteConditionType.Reset_DailyMissionFulfill), x => x.MissionUniqueId == 1701);
        db.SaveChanges();
        db.ChangeTracker.Clear();
        Assert.DoesNotContain(service.UpdateMissionProgress(db, account,
            MissionCompleteConditionType.Reset_DailyMissionFulfill), x => x.MissionUniqueId == 1701);

        db.MissionHistories.Single(x => x.MissionUniqueId == 1701).CompleteTime = history.CompleteTime.AddDays(-1);
        db.SaveChanges();
        Assert.Contains(service.UpdateMissionProgress(db, account,
            MissionCompleteConditionType.Reset_DailyMissionFulfill), x => x.MissionUniqueId == 1701);
    }

    [ExcelDumpFact]
    public async Task ExistingClaimHistoryRepairsSplitMetaAndMissingFixedDailyWithoutAwardingOnList()
    {
        using var db = NewContext();
        var account = NewAccount(db);
        SeedLegacyMetaClaims(db, account, 8);
        var before = db.Currencies.Single().CurrencyDict.ToDictionary();

        var listed = await Handler().List(db, new MissionListRequest { SessionKey = Key(account) }, new MissionListResponse());
        Assert.Contains(listed.ProgressDBs!, x => x.MissionUniqueId == 1514 && x.Complete);
        Assert.Contains(listed.ProgressDBs!, x => x.MissionUniqueId == 1701 && x.Complete);
        var repaired = db.MissionProgresses.Single(x => x.MissionUniqueId == 1514);
        Assert.Equal(8, repaired.ProgressParameters![0]);
        Assert.Single(repaired.ProgressParameters);
        Assert.Equal(before.OrderBy(x => x.Key), db.Currencies.Single().CurrencyDict.OrderBy(x => x.Key));

        // Repeated reads are idempotent and never claim or grant the newly repaired rewards.
        await Handler().Sync(db, new MissionSyncRequest { SessionKey = Key(account) }, new MissionSyncResponse());
        Assert.Equal(8, db.MissionHistories.Count());
        Assert.Equal(before.OrderBy(x => x.Key), db.Currencies.Single().CurrencyDict.OrderBy(x => x.Key));
        await Handler().Reward(db, new MissionRewardRequest { SessionKey = Key(account), MissionUniqueId = 1514 }, new MissionRewardResponse());
        await Handler().Reward(db, new MissionRewardRequest { SessionKey = Key(account), MissionUniqueId = 1701 }, new MissionRewardResponse());
        var afterReward = db.Currencies.Single().CurrencyDict.ToDictionary();
        await Handler().List(db, new MissionListRequest { SessionKey = Key(account) }, new MissionListResponse());
        Assert.Empty(db.MissionProgresses.Where(x => x.MissionUniqueId == 1514 || x.MissionUniqueId == 1701));
        Assert.Equal(afterReward.OrderBy(x => x.Key), db.Currencies.Single().CurrencyDict.OrderBy(x => x.Key));
        Assert.Equal(10, db.MissionHistories.Count());
    }

    [ExcelDumpFact]
    public async Task DirectRewardRepairsAnExistingSplitCounterBeforeCheckingCompletion()
    {
        using var db = NewContext();
        var account = NewAccount(db);
        SeedLegacyMetaClaims(db, account, 5);
        var before = db.Currencies.Single().CurrencyDict.ToDictionary();
        var response = await Handler().Reward(db,
            new MissionRewardRequest { SessionKey = Key(account), MissionUniqueId = 1514 }, new MissionRewardResponse());
        Assert.Equal(1514, response.AddedHistoryDB!.MissionUniqueId);
        AssertCurrencyRewards(db, [_excel.GetTable<MissionExcelT>().Single(x => x.Id == 1514)], before);
        // A legacy bug may have left a second complete row after the original claim.
        AddComplete(db, account, _excel.GetTable<MissionExcelT>().Single(x => x.Id == 1514));
        db.SaveChanges();
        var alreadyAwarded = db.Currencies.Single().CurrencyDict.ToDictionary();
        await Assert.ThrowsAsync<WebAPIException>(() => Handler().Reward(db,
            new MissionRewardRequest { SessionKey = Key(account), MissionUniqueId = 1514 }, new MissionRewardResponse()));
        Assert.Single(db.MissionHistories.Where(x => x.MissionUniqueId == 1514));
        Assert.Equal(alreadyAwarded.OrderBy(x => x.Key), db.Currencies.Single().CurrencyDict.OrderBy(x => x.Key));
    }

    [ExcelDumpFact]
    public async Task ExpiredClaimsDoNotCompleteMetaOrInferUnrecordedGameplay()
    {
        using var db = NewContext();
        var account = NewAccount(db);
        SeedLegacyMetaClaims(db, account, 5);
        db.MissionHistories.First().CompleteTime = account.GameSettings.ServerDateTime().AddDays(-1);
        db.SaveChanges();
        await Handler().Sync(db, new MissionSyncRequest { SessionKey = Key(account) }, new MissionSyncResponse());
        var meta = db.MissionProgresses.Single(x => x.MissionUniqueId == 1514);
        Assert.False(meta.Complete);
        Assert.Equal(4, meta.ProgressParameters![0]);
        Assert.False(db.MissionProgresses.Single(x => x.MissionUniqueId == 1701).Complete);
        // Claims establish only claim-count counters, never the player's individual action tasks.
        Assert.Empty(db.MissionProgresses.Where(x => x.MissionUniqueId == 1601 || x.MissionUniqueId == 1301));
    }

    private void SeedLegacyMetaClaims(SchaleDataContext db, AccountDBServer account, int count)
    {
        var meta = _excel.GetTable<MissionExcelT>().Single(x => x.Id == 1514);
        var candidates = meta.CompleteConditionParameter.Take(count).ToList();
        Assert.Equal(count, candidates.Count);
        foreach (var id in candidates)
            db.MissionHistories.Add(new MissionHistoryDBServer
            {
                AccountServerId = account.ServerId, MissionUniqueId = id,
                CompleteTime = account.GameSettings.ServerDateTime()
            });
        db.MissionProgresses.Add(new MissionProgressDBServer
        {
            AccountServerId = account.ServerId, MissionUniqueId = 1514,
            StartTime = account.GameSettings.ServerDateTime(), Complete = false,
            ProgressParameters = candidates.ToDictionary(id => id, _ => 1L)
        });
        db.SaveChanges();
        db.ChangeTracker.Clear();
    }

    private static void AddComplete(SchaleDataContext db, AccountDBServer account, MissionExcelT mission) =>
        db.MissionProgresses.Add(new MissionProgressDBServer
        {
            AccountServerId = account.ServerId,
            MissionUniqueId = mission.Id,
            StartTime = account.GameSettings.ServerDateTime(),
            ProgressParameters = new Dictionary<long, long> { [0] = mission.CompleteConditionCount },
            Complete = true
        });

    private static void AssertCurrencyRewards(SchaleDataContext db, IEnumerable<MissionExcelT> missions,
        Dictionary<CurrencyTypes, long>? before = null)
    {
        var currencies = db.Currencies.Single().CurrencyDict;
        var expected = new Dictionary<CurrencyTypes, long>();
        foreach (var mission in missions)
            for (var i = 0; i < mission.MissionRewardParcelType.Count; i++)
                if (mission.MissionRewardParcelType[i] == ParcelType.Currency)
                {
                    var currency = (CurrencyTypes)mission.MissionRewardParcelId[i];
                    expected[currency] = expected.GetValueOrDefault(currency) + mission.MissionRewardAmount[i];
                }
        Assert.NotEmpty(expected);
        foreach (var (currency, amount) in expected)
            Assert.Equal((before?.GetValueOrDefault(currency) ?? 0) + amount, currencies[currency]);
    }

    private MissionHandler Handler() => new(null!, new FixedSessionService(), _mapper, _excel,
        new ParcelHandler(_excel, _mapper), new MissionService(_excel, _mapper));

    private static SessionKey Key(AccountDBServer account) => new() { AccountServerId = account.ServerId, MxToken = "test" };

    private sealed class FixedSessionService : ISessionKeyService
    {
        public Task<AccountDBServer> GetAuthenticatedUser(SchaleDataContext context, SessionKey? sessionKey) =>
            Task.FromResult(context.Accounts.Single(x => x.ServerId == sessionKey!.AccountServerId));
        public Task<SessionKey?> GenerateSession(long publisherAccountId, string? customToken = null) => throw new NotSupportedException();
        public bool ValidateRequest(RequestPacket request) => true;
        public void RevokeSession(long userId) { }
        public int PurgeExpiredSessions(TimeSpan maxInactivity) => 0;
    }
}
