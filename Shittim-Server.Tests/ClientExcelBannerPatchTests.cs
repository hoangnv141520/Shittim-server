using Schale.FlatData;
using Shittim_Server.Services;
using Xunit;

namespace Shittim_Server.Tests;

public class ClientExcelBannerPatchTests
{
    private static ShopRecruitExcelT Pickup(long id, string? art, params long[] characters) => new()
    {
        Id = id,
        CategoryType = ShopCategoryType.PickupGacha,
        GachaBannerPath = art,
        InfoCharacterId = characters.ToList()
    };

    private static ShopRecruitExcelT Normal() => new()
    {
        Id = 90050736,
        CategoryType = ShopCategoryType.NormalGacha,
        GachaBannerPath = "uis/00_images/banner/Event_Banner_50001"
    };

    [Fact]
    public void SharedIdPrefixDoesNotMakeNormalRecruitmentArtAPickupMatch()
    {
        var target = Pickup(90050737, null, 10117);
        Assert.Null(ClientExcelBannerPatchService.PickRelatedArt(target, [Normal()]));
    }

    [Fact]
    public void MatchingFeaturedCharactersWinOverAnUnrelatedNearbyId()
    {
        var target = Pickup(90050737, null, 10117);
        Assert.Equal("hikari", ClientExcelBannerPatchService.PickRelatedArt(target,
            [Normal(), Pickup(90050738, "nozomi", 10118), Pickup(812345, "hikari", 10117)]));
    }

    [Fact]
    public void APartialCharacterOverlapDoesNotMatchAMultiPickup()
    {
        var target = Pickup(1, null, 10117, 10118);
        Assert.Null(ClientExcelBannerPatchService.PickRelatedArt(target, [Pickup(2, "hikari", 10117)]));
        Assert.Equal("both", ClientExcelBannerPatchService.PickRelatedArt(target,
            [Pickup(2, "hikari", 10117), Pickup(3, "both", 10118, 10117)]));
    }

    [Fact]
    public void AmbiguousMatchingArtIsNotSelectedByDatabaseOrder()
    {
        var target = Pickup(1, null, 10117);
        Assert.Null(ClientExcelBannerPatchService.PickRelatedArt(target,
            [Pickup(2, "first", 10117), Pickup(3, "second", 10117)]));
    }

    [Fact]
    public void AnExplicitBannerLinkCanMatchRowsWithoutFeaturedCharacters()
    {
        var target = Pickup(1, null);
        target.LinkedRobbyBannerId = 725;
        var donor = Pickup(2, "linked");
        donor.LinkedRobbyBannerId = 725;
        Assert.Equal("linked", ClientExcelBannerPatchService.PickRelatedArt(target, [donor]));
        donor.LinkedRobbyBannerId = 726;
        Assert.Null(ClientExcelBannerPatchService.PickRelatedArt(target, [donor]));
    }

    [Fact]
    public void APreviousNormalArtPatchIsReplacedUsingThePristinePickup()
    {
        var original = Pickup(90050737, null, 10117);
        var current = Pickup(original.Id, Normal().GachaBannerPath, 10117);
        Assert.Equal("hikari", ClientExcelBannerPatchService.ReplacementArt(current, original,
            [original, Normal(), Pickup(812345, "hikari", 10117)]));
    }

    [Fact]
    public void AnUnmatchedPollutedPickupIsRestoredWithoutPollutingTheNormalBanner()
    {
        var original = Pickup(90050737, null, 10117);
        var current = Pickup(original.Id, Normal().GachaBannerPath, 10117);
        Assert.Equal("", ClientExcelBannerPatchService.ReplacementArt(current, original, [original, Normal()]));
        Assert.Null(ClientExcelBannerPatchService.ReplacementArt(Normal(), Normal(), [original, Normal()]));
        current.GachaBannerPath = "";
        Assert.Null(ClientExcelBannerPatchService.ReplacementArt(current, original, [original, Normal()]));
    }

    [Fact]
    public void ExistingNonGenericArtIsPreserved()
    {
        var original = Pickup(1, null, 10117);
        var current = Pickup(1, "custom-hikari", 10117);
        Assert.Null(ClientExcelBannerPatchService.ReplacementArt(current, original,
            [original, Normal(), Pickup(2, "hikari", 10117)]));
    }

    [Fact]
    public void ASelectionBannerDoesNotDonateArtToASinglePickup()
    {
        var donor = Pickup(2, "select", 10117);
        donor.IsSelectRecruit = true;
        Assert.Null(ClientExcelBannerPatchService.PickRelatedArt(Pickup(1, null, 10117), [donor]));
    }
}
