using BlueArchiveAPI.Services;
using Microsoft.EntityFrameworkCore;
using Schale.Data;
using Schale.Data.GameModel;
using Schale.FlatData;
using Schale.MX.NetworkProtocol;

namespace Shittim_Server.Services;

internal static class CampaignDailyPlays
{
    internal static DateTime Day(DateTime now)
        => now.Ticks < TimeSpan.FromHours(4).Ticks ? DateTime.MinValue.Date : now.AddHours(-4).Date;

    internal static bool Reset(CampaignStageHistoryDBServer history, DateTime now)
    {
        if (Day(history.LastPlay) >= Day(now)) return false;
        history.TodayPlayCount = 0;
        history.TodayPlayCountForUI = 0;
        history.TodayPurchasePlayCountHardStage = 0;
        history.LastPlay = now;
        return true;
    }

    internal static void Record(CampaignStageHistoryDBServer history, DateTime now, long count)
    {
        Reset(history, now);
        history.TodayPlayCount = checked(history.TodayPlayCount + count);
        history.LastPlay = now;
    }

    internal static async Task CheckHardLimit(SchaleDataContext db, AccountDBServer account,
        CampaignStageExcelT stage, long count, ExcelTableService excel)
    {
        if (stage.Name?.Contains("Hard", StringComparison.OrdinalIgnoreCase) != true) return;
        var history = await db.CampaignStageHistories.FirstOrDefaultAsync(x =>
            x.AccountServerId == account.ServerId && x.StageUniqueId == stage.Id);
        if (history != null) Reset(history, account.GameSettings.ServerDateTime());
        var limit = excel.GetTable<ConstCommonExcelT>().First().HardStageCount
            + (history?.TodayPurchasePlayCountHardStage ?? 0);
        if (count <= 0 || count > limit - (history?.TodayPlayCount ?? 0))
            throw new WebAPIException(WebAPIErrorCode.CampaignStagePlayLimit);
    }
}
