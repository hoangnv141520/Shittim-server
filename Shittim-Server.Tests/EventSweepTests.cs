using AutoMapper;
using BlueArchiveAPI.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Schale.Data;
using Schale.Data.GameModel;
using Schale.FlatData;
using Schale.MappingProfiles;
using Schale.MX.NetworkProtocol;
using Shittim_Server.Core.NetworkProtocol.Handlers;
using Shittim_Server.Services;
using Xunit;

namespace Shittim_Server.Tests;

public class EventSweepTests
{
    private sealed class Fixture : IDisposable
    {
        public SchaleDataContext Db { get; }
        public EventContentStageExcelT Stage { get; }
        public ContentSweepHandler Handler { get; }
        public Fixture()
        {
            Db = new SchaleDataContext(new DbContextOptionsBuilder<SchaleDataContext>().UseSqlite("Data Source=:memory:").Options);
            Db.Database.OpenConnection();
            Db.Database.EnsureCreated();
            var excel = ExcelDumps.Service();
            // Latest quest cleared immediately before the reported 97-run sweep.
            Stage = excel.GetTable<EventContentStageExcelT>().Single(x => x.Id == 108462304);
            Assert.DoesNotContain(excel.GetTable<CampaignStageExcelT>(), x => x.Id == Stage.Id);
            Assert.Equal(ParcelType.Currency, Stage.StageEnterCostType);
            Assert.Equal((long)CurrencyTypes.ActionPoint, Stage.StageEnterCostId);
            Db.Accounts.Add(new AccountDBServer { ServerId = 1, Nickname = "Sweep regression", Level = 50 });
            var currency = new AccountCurrencyDBServer(1);
            currency.CurrencyDict[CurrencyTypes.ActionPoint] = 979;
            Db.Currencies.Add(currency);
            Db.CampaignStageHistories.Add(new CampaignStageHistoryDBServer
            {
                AccountServerId = 1, StageUniqueId = Stage.Id, IsClearedEver = true,
                Star1Flag = true, Star2Flag = true, Star3Flag = true,
                TodayPlayCount = 1, LastPlay = DateTime.Now,
                FirstClearRewardReceive = new DateTime(2026, 10, 30), StarRewardReceive = new DateTime(2026, 10, 30)
            });
            Db.SaveChanges();
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddAutoMapper(_ => { }, typeof(GameModelsMappingProfile).Assembly);
            using var provider = services.BuildServiceProvider();
            var mapper = provider.GetRequiredService<IMapper>();
            Handler = new ContentSweepHandler(null!, new Sessions(), excel, new ParcelHandler(excel, mapper), null!, null!, mapper);
        }
        public ContentSweepRequest Request(long count = 97) => new()
        {
            Content = ContentType.EventContentMainGroundStage, StageId = Stage.Id,
            EventContentId = Stage.EventContentId, Count = count
        };
        public long Ap() => Db.Currencies.Single().CurrencyDict[CurrencyTypes.ActionPoint];
        public void Dispose() => Db.Dispose();
    }

    [ExcelDumpFact]
    public async Task NinetySevenEventSweepsPayPerRunDropsAndSpendTheCorrectAp()
    {
        using var f = new Fixture();
        var response = await f.Handler.Request(f.Db, f.Request(), new());
        Assert.Equal(97, response.ClearParcels!.Count);
        Assert.All(response.ClearParcels, run => Assert.NotEmpty(run));
        Assert.DoesNotContain(response.ClearParcels.SelectMany(x => x), x => x.Key.Type == ParcelType.GachaGroup
            || (x.Key.Type == ParcelType.Currency && x.Key.Id == (long)CurrencyTypes.GemBonus));
        Assert.NotNull(response.ParcelResult);
        Assert.Equal(f.Stage.StageEnterCostAmount * 97, response.ParcelResult.BaseAccountExp);
        var itemDrops = response.ClearParcels.SelectMany(x => x).Where(x => x.Key.Type == ParcelType.Item)
            .GroupBy(x => x.Key.Id).ToDictionary(x => x.Key, x => x.Sum(p => p.Amount));
        Assert.NotEmpty(itemDrops);
        f.Db.ChangeTracker.Clear();
        Assert.Equal(979 - f.Stage.StageEnterCostAmount * 97, f.Ap());
        foreach (var (id, count) in itemDrops)
            Assert.Equal(count, f.Db.Items.Single(x => x.UniqueId == id).StackCount);
        var history = f.Db.CampaignStageHistories.Single();
        Assert.Equal(98, history.TodayPlayCount);
        Assert.Equal(new DateTime(2026, 10, 30), history.FirstClearRewardReceive);
        Assert.Equal(new DateTime(2026, 10, 30), history.StarRewardReceive);
    }

    [ExcelDumpFact]
    public async Task InsufficientApOrUnclearedStageDoesNotSpendOrGrantAnything()
    {
        using var f = new Fixture();
        f.Db.Currencies.Single().CurrencyDict[CurrencyTypes.ActionPoint] = 1;
        f.Db.Entry(f.Db.Currencies.Single()).Property(x => x.CurrencyDict).IsModified = true;
        await f.Db.SaveChangesAsync();
        var error = await Assert.ThrowsAsync<WebAPIException>(() => f.Handler.Request(f.Db, f.Request(), new()));
        Assert.Equal(WebAPIErrorCode.InvalidSweepRequest, error.ErrorCode);
        f.Db.ChangeTracker.Clear();
        Assert.Equal(1, f.Ap());
        Assert.Empty(f.Db.Items);
        Assert.Equal(1, f.Db.CampaignStageHistories.Single().TodayPlayCount);
        f.Db.CampaignStageHistories.Single().Star3Flag = false;
        await f.Db.SaveChangesAsync();
        error = await Assert.ThrowsAsync<WebAPIException>(() => f.Handler.Request(f.Db, f.Request(1), new()));
        Assert.Equal(WebAPIErrorCode.ContentSweepNotOpened, error.ErrorCode);
    }

    [ExcelDumpFact]
    public async Task RewardFailureRollsBackTheApSpendAndHistory()
    {
        using var f = new Fixture();
        await f.Db.Database.ExecuteSqlRawAsync("CREATE TRIGGER fail_reward BEFORE INSERT ON Items BEGIN SELECT RAISE(ABORT, 'simulated reward failure'); END;");
        await Assert.ThrowsAnyAsync<Exception>(() => f.Handler.Request(f.Db, f.Request(), new()));
        f.Db.ChangeTracker.Clear();
        Assert.Equal(979, f.Ap());
        Assert.Empty(f.Db.Items);
        Assert.Equal(1, f.Db.CampaignStageHistories.Single().TodayPlayCount);
    }

    [ExcelDumpFact]
    public async Task InvalidCountsAndMismatchedEventAreRejectedBeforeSpending()
    {
        using var f = new Fixture();
        foreach (var count in new[] { 0L, -1L, 10_001L, long.MaxValue })
            await Assert.ThrowsAsync<WebAPIException>(() => f.Handler.Request(f.Db, f.Request(count), new()));
        var request = f.Request();
        request.EventContentId = -1;
        await Assert.ThrowsAsync<WebAPIException>(() => f.Handler.Request(f.Db, request, new()));
        request.EventContentId = 0;
        request.StageId = long.MaxValue;
        await Assert.ThrowsAsync<WebAPIException>(() => f.Handler.Request(f.Db, request, new()));
        f.Db.ChangeTracker.Clear();
        Assert.Equal(979, f.Ap());
        Assert.Empty(f.Db.Items);
    }

    private sealed class Sessions : ISessionKeyService
    {
        public Task<AccountDBServer> GetAuthenticatedUser(SchaleDataContext db, SessionKey? key) => db.Accounts.SingleAsync(x => x.ServerId == 1);
        public Task<SessionKey?> GenerateSession(long id, string? token = null) => throw new NotSupportedException();
        public bool ValidateRequest(RequestPacket request) => true;
        public void RevokeSession(long id) { }
        public int PurgeExpiredSessions(TimeSpan maxInactivity) => 0;
    }
}
