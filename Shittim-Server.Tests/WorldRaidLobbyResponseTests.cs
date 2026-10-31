using AutoMapper;
using BlueArchiveAPI.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Newtonsoft.Json;
using Schale.Data;
using Schale.Data.GameModel;
using Schale.FlatData;
using Schale.MappingProfiles;
using Schale.MX.NetworkProtocol;
using Shittim_Server.Core.NetworkProtocol.Handlers;
using Shittim_Server.Services;
using Xunit;

namespace Shittim_Server.Tests;

public class WorldRaidLobbyResponseTests
{
    [ExcelDumpFact]
    public async Task ClassicStoryRaidLobbyAlwaysCarriesBossGroupsWhenBossesExist()
    {
        using var db = new SchaleDataContext(new DbContextOptionsBuilder<SchaleDataContext>().UseSqlite("Data Source=:memory:").Options);
        db.Database.OpenConnection();
        db.Database.EnsureCreated();
        db.Accounts.Add(new AccountDBServer { ServerId = 1, Nickname = "Raid lobby regression", Level = 50 });
        await db.SaveChangesAsync();
        var services = new ServiceCollection(); services.AddLogging();
        services.AddAutoMapper(_ => { }, typeof(GameModelsMappingProfile).Assembly);
        using var provider = services.BuildServiceProvider();
        var mapper = provider.GetRequiredService<IMapper>();
        var excel = ExcelDumps.Service();
        var manager = new WorldRaidManager(excel, null!, mapper);
        var handler = new WorldRaidHandler(null!, new Sessions(), manager, mapper, excel, null!);
        var response = await handler.Lobby(db, new() { SeasonId = 821, ContentType = ContentType.WorldRaid }, new());
        Assert.NotEmpty(response.LocalBossDBs!);
        Assert.NotNull(response.BossGroups);
        var json = JsonConvert.SerializeObject(response, new JsonSerializerSettings { NullValueHandling = NullValueHandling.Ignore, DefaultValueHandling = DefaultValueHandling.Ignore });
        Assert.Contains("\"BossGroups\":", json);
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
