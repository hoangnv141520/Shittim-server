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

public class EventShopPurchaseTests
{
    [ExcelDumpFact]
    public async Task Event859ProductCanBeBoughtThroughTheOrdinaryShopProtocol()
    {
        var path = Path.Combine(Path.GetTempPath(), $"shittim-eventshop-{Guid.NewGuid():N}.sqlite3");
        try
        {
            using var db = new SchaleDataContext(new DbContextOptionsBuilder<SchaleDataContext>()
                .UseSqlite($"Data Source={path}").Options);
            db.Database.EnsureCreated();
            db.Accounts.Add(new AccountDBServer { ServerId = 1, Nickname = "Event shop regression" });
            db.Currencies.Add(new AccountCurrencyDBServer(1));
            var excel = ExcelDumps.Service();
            var product = Assert.Single(excel.GetTable<EventContentShopExcelT>(), x => x.Id == 8590116);
            Assert.DoesNotContain(excel.GetTable<ShopExcelT>(), x => x.Id == product.Id);
            var goods = excel.GetTable<GoodsExcelT>().Single(x => x.Id == product.GoodsId[0]);
            Assert.All(goods.ConsumeParcelType, x => Assert.Equal(ParcelType.Item, x));
            for (var i = 0; i < goods.ConsumeParcelId.Count; i++)
                db.Items.Add(new ItemDBServer { AccountServerId = 1, UniqueId = goods.ConsumeParcelId[i],
                    StackCount = goods.ConsumeParcelAmount[i] * 3 });
            await db.SaveChangesAsync();
            var cost = db.Items.Single();
            var costServerId = cost.ServerId;
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddAutoMapper(_ => { }, typeof(GameModelsMappingProfile).Assembly);
            using var provider = services.BuildServiceProvider();
            var mapper = provider.GetRequiredService<IMapper>();
            var handler = new ShopHandler(null!, new Sessions(), excel, mapper, null!, null!, new ParcelHandler(excel, mapper));
            var request = new ShopBuyMerchandiseRequest { ShopUniqueId = product.Id, GoodsId = goods.Id, PurchaseCount = 2 };
            var response = await handler.BuyMerchandise(db, request, new());
            Assert.Equal(2, response.ShopProductDB!.PurchaseCount);
            Assert.Equal(goods.ConsumeParcelAmount[0], response.ConsumeResultDB!.UsedItemServerIdAndRemainingCounts[costServerId]);
            Assert.NotNull(response.ParcelResultDB);
            db.ChangeTracker.Clear();
            Assert.Equal(goods.ConsumeParcelAmount[0], db.Items.Single(x => x.ServerId == costServerId).StackCount);
            for (var i = 0; i < goods.ParcelId.Count; i++)
            {
                Assert.Equal(ParcelType.Item, goods.ParcelType[i]);
                Assert.Equal(goods.ParcelAmount[i] * 2, db.Items.Single(x => x.UniqueId == goods.ParcelId[i]).StackCount);
            }
            request.PurchaseCount = 1;
            response = await handler.BuyMerchandise(db, request, new());
            Assert.Contains(costServerId, response.ConsumeResultDB!.RemovedItemServerIds);
            Assert.DoesNotContain(costServerId, response.ConsumeResultDB.UsedItemServerIdAndRemainingCounts.Keys);
            db.ChangeTracker.Clear();
            Assert.False(db.Items.Any(x => x.ServerId == costServerId));
            Assert.Equal(3, db.ShopPurchaseHistories.Single().PurchaseCount);
            request.GoodsId = -1;
            var error = await Assert.ThrowsAsync<WebAPIException>(() => handler.BuyMerchandise(db, request, new()));
            Assert.Equal(WebAPIErrorCode.ShopGoodsNotFound, error.ErrorCode);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            File.Delete(path);
        }
    }

    private sealed class Sessions : ISessionKeyService
    {
        public Task<AccountDBServer> GetAuthenticatedUser(SchaleDataContext context, SessionKey? key) =>
            Task.FromResult(context.Accounts.Single(x => x.ServerId == 1));
        public Task<SessionKey?> GenerateSession(long id, string? token = null) => throw new NotSupportedException();
        public bool ValidateRequest(RequestPacket request) => true;
        public void RevokeSession(long id) { }
        public int PurgeExpiredSessions(TimeSpan maxInactivity) => 0;
    }
}
