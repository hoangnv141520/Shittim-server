using BlueArchiveAPI.Services;
using Schale.Data.GameModel;
using Schale.FlatData;

namespace Shittim_Server.Services;

internal static class ActionPointRegeneration
{
    internal static long Cap(ExcelTableService excel, long level)
        => excel.GetTable<AccountLevelExcelT>().FirstOrDefault(x => x.Level == level)?.APAutoChargeMax ?? 0;

    internal static bool Apply(AccountCurrencyDBServer currency, DateTime now, long cap, int intervalMs, int amount)
    {
        const CurrencyTypes ap = CurrencyTypes.ActionPoint;
        if (cap <= 0 || intervalMs <= 0 || amount <= 0) return false;
        var held = currency.CurrencyDict.GetValueOrDefault(ap);
        if (!currency.UpdateTimeDict.TryGetValue(ap, out var last))
        {
            currency.UpdateTimeDict[ap] = now;
            return true;
        }
        if (now <= last) return false;
        if (held >= cap)
        {
            currency.UpdateTimeDict[ap] = now;
            return true;
        }
        var intervalTicks = checked((long)intervalMs * TimeSpan.TicksPerMillisecond);
        var periods = (now.Ticks - last.Ticks) / intervalTicks;
        if (periods == 0) return false;
        var needed = cap - held;
        var gained = periods >= (needed + amount - 1) / amount ? needed : periods * amount;
        currency.CurrencyDict[ap] = held + gained;
        currency.UpdateTimeDict[ap] = held + gained >= cap ? now : last.AddTicks(periods * intervalTicks);
        return true;
    }

    internal static bool Apply(AccountCurrencyDBServer currency, AccountDBServer account, ExcelTableService excel)
    {
        var row = excel.GetTable<CurrencyExcelT>().First(x => x.CurrencyType == CurrencyTypes.ActionPoint);
        return Apply(currency, account.GameSettings.ServerDateTime(), Cap(excel, account.Level), row.AutoChargeMsc, row.AutoChargeAmount);
    }
}
