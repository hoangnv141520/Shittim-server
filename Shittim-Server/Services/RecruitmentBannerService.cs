using Schale.FlatData;
using Schale.MX.Data;
using Schale.MX.GameLogic.DBModel;

namespace Shittim_Server.Services;

internal static class RecruitmentBannerService
{
    internal static List<BannerDB> Build(IEnumerable<ShopRecruitExcelT> recruits, DateTime now,
        string baseUrl, string artDirectory)
    {
        var banners = new List<BannerDB>();
        foreach (var recruit in recruits)
        {
            // A portrait identifies a single pickup only. Normal, selection and multi-pickup
            // banners must keep their own artwork.
            if (recruit.CategoryType == ShopCategoryType.NormalGacha || recruit.IsSelectRecruit
                || recruit.InfoCharacterId?.Count != 1 || recruit.LinkedRobbyBannerId <= 0)
                continue;
            if (!DateTime.TryParse(recruit.SalePeriodFrom, out var from)
                || !DateTime.TryParse(recruit.SalePeriodTo, out var to) || now < from || now > to)
                continue;
            var fileName = $"recruitment-{recruit.InfoCharacterId[0]}.png";
            if (!File.Exists(Path.Combine(artDirectory, fileName)))
                continue;
            banners.Add(new BannerDB
            {
                BannerOrder = recruit.DisplayOrder,
                StartDate = from,
                EndDate = to,
                Url = baseUrl.TrimEnd('/') + "/",
                FileName = fileName,
                LinkedLobbyBannerId = checked((int)recruit.LinkedRobbyBannerId),
                BannerType = EventContentType.Gacha,
                BannerDisplayType = BannerDisplayType.Gacha
            });
        }
        return banners;
    }
}
