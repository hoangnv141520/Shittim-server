using Schale.FlatData;

namespace Shittim_Server.Services;

internal static class EventShopCatalog
{
    // Both EventContent_Shop* and Shop_BuyMerchandise are used by the client.
    // Keep their product definitions and replay calendar handling identical.
    internal static ShopExcelT Product(EventContentShopExcelT row) => new()
    {
        Id = row.Id,
        LocalizeEtcId = row.LocalizeEtcId,
        CategoryType = row.CategoryType,
        IsLegacy = row.IsLegacy,
        GoodsId = row.GoodsId,
        DisplayOrder = row.DisplayOrder,
        PurchaseCooltimeMin = row.PurchaseCooltimeMin,
        PurchaseCountLimit = row.PurchaseCountLimit,
        PurchaseCountResetType = row.PurchaseCountResetType,
        BuyReportEventName = row.BuyReportEventName,
        RestrictBuyWhenInventoryFull = row.RestrictBuyWhenInventoryFull,
        SalePeriodFrom = "",
        SalePeriodTo = ""
    };
}
