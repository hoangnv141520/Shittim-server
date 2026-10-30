using Schale.FlatData;
using Schale.MX.Data;
using Shittim_Server.Controllers;
using Shittim_Server.Services;
using Xunit;

namespace Shittim_Server.Tests;

public class RecruitmentBannerTests
{
    [ExcelDumpFact]
    public void CurrentFivePickupsHaveDistinctDownloadableGachaBanners()
    {
        var art = Path.Combine(AppContext.BaseDirectory, "Data", "Banner");
        var banners = RecruitmentBannerService.Build(ExcelDumps.Service().GetTable<ShopRecruitExcelT>(),
            new DateTime(2026, 10, 30, 12, 0, 0), "http://localhost:5000/banner/", art);
        Assert.Equal(new[] { 725, 726, 727, 728, 729 }, banners.Select(x => x.LinkedLobbyBannerId).Order().ToArray());
        Assert.Equal(5, banners.Select(x => x.FileName).Distinct().Count());
        Assert.All(banners, banner =>
        {
            Assert.Equal(EventContentType.Gacha, banner.BannerType);
            Assert.Equal(BannerDisplayType.Gacha, banner.BannerDisplayType);
            Assert.Equal("http://localhost:5000/banner/", banner.Url);
            var localized = Path.GetFileNameWithoutExtension(banner.FileName) + "_En.png";
            Assert.Equal(Path.Combine(art, banner.FileName!), BannerController.ResolveArt(art, localized));
            Assert.True(File.ReadAllBytes(BannerController.ResolveArt(art, localized)!).Length > 100_000);
        });
    }

    [Fact]
    public void NormalSelectionMultiPickupAndExpiredRowsDoNotBorrowASingleCharacterPortrait()
    {
        var dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllBytes(Path.Combine(dir, "recruitment-10117.png"), [1]);
            ShopRecruitExcelT Row() => new() { CategoryType = ShopCategoryType.PickupGacha,
                InfoCharacterId = [10117], LinkedRobbyBannerId = 725,
                SalePeriodFrom = "2026-10-27", SalePeriodTo = "2026-11-03" };
            var normal = Row(); normal.CategoryType = ShopCategoryType.NormalGacha;
            var selection = Row(); selection.IsSelectRecruit = true;
            var multiple = Row(); multiple.InfoCharacterId = [10117, 10118];
            var expired = Row(); expired.SalePeriodTo = "2026-10-29";
            var missing = Row(); missing.InfoCharacterId = [999999];
            Assert.Empty(RecruitmentBannerService.Build([normal, selection, multiple, expired, missing],
                new DateTime(2026, 10, 30), "http://localhost/banner", dir));
            Assert.Single(RecruitmentBannerService.Build([Row()], new DateTime(2026, 10, 30), "http://localhost/banner", dir));
            File.WriteAllBytes(Path.Combine(dir, "Event_Banner_854.png"), [1]);
            Assert.Equal(Path.Combine(dir, "Event_Banner_854.png"), BannerController.ResolveArt(dir, "Event_Banner_854.png"));
            Assert.Equal(Path.Combine(dir, "Event_Banner_854.png"), BannerController.ResolveArt(dir, "Event_Banner_854_En.png"));
            Assert.Null(BannerController.ResolveArt(dir, "Event_Banner_854_other.png"));
        }
        finally
        {
            foreach (var file in Directory.GetFiles(dir)) File.Delete(file);
            Directory.Delete(dir);
        }
    }
}
