using Microsoft.EntityFrameworkCore;
using BlueArchiveAPI.Services;
using Schale.Data;
using Schale.Data.GameModel;
using Schale.Excel;
using Schale.FlatData;
using Schale.MX.GameLogic.DBModel;
using Schale.MX.GameLogic.Parcel;
using Schale.MX.NetworkProtocol;
using Shittim_Server.Services;
using Shittim.Services;

namespace Shittim_Server.Managers
{
    public class EquipmentManager
    {
        private readonly ExcelTableService excelTableService;
        private readonly ParcelHandler parcelHandler;
        private readonly ConsumeHandler consumeHandler;

        private readonly List<EquipmentExcelT> equipmentExcels;
        private readonly List<EquipmentLevelExcelT> equipmentLevelExcel;
        private readonly List<CharacterGearExcelT> characterGearExcels;
        private readonly List<RecipeIngredientExcelT> recipeIngredientExcels;

        private readonly List<ExpLevelData> equipmentExpLevelDatas;

        public EquipmentManager(ExcelTableService _excelTableService, ParcelHandler _parcelHandler, ConsumeHandler _consumeHandler)
        {
            excelTableService = _excelTableService;
            parcelHandler = _parcelHandler;
            consumeHandler = _consumeHandler;

            equipmentExcels = excelTableService.GetTable<EquipmentExcelT>();
            characterGearExcels = excelTableService.GetTable<CharacterGearExcelT>();
            recipeIngredientExcels = excelTableService.GetTable<RecipeIngredientExcelT>();

            equipmentLevelExcel = excelTableService.GetTable<EquipmentLevelExcelT>();
            equipmentExpLevelDatas = new ExpLevelData().ConvertExpLevelData(equipmentLevelExcel);
        }

        public async Task<(CharacterDBServer, EquipmentDBServer, EquipmentDBServer)> EquipmentEquip(
            SchaleDataContext context, AccountDBServer account, EquipmentItemEquipRequest req)
        {
            var originalStack = context.Equipments.FirstOrDefault(x => x.ServerId == req.EquipmentServerId)
                ?? throw new WebAPIException(WebAPIErrorCode.EquipmentNotFound);

            originalStack.StackCount--;
            if (originalStack.StackCount <= 0) context.Equipments.Remove(originalStack);
            else context.Equipments.Update(originalStack);

            var newEquipment = new EquipmentDBServer()
            {
                UniqueId = originalStack.UniqueId,
                StackCount = 1,
                BoundCharacterServerId = req.CharacterServerId,
            };
            context.AddEquipment(account.ServerId, [newEquipment]);
            await context.SaveChangesAsync();

            var equippedCharacter = context.Characters.FirstOrDefault(x => x.ServerId == req.CharacterServerId)
                ?? throw new WebAPIException(WebAPIErrorCode.CharacterNotFound);
            equippedCharacter.EquipmentServerIds ??= [];

            while (equippedCharacter.EquipmentServerIds.Count < 3)
                equippedCharacter.EquipmentServerIds.Add(0);

            equippedCharacter.EquipmentServerIds[req.SlotIndex] = newEquipment.ServerId;
            context.Characters.Update(equippedCharacter);

            await context.SaveChangesAsync();

            return (equippedCharacter, originalStack, newEquipment);
        }

        public async Task<(EquipmentDBServer, ConsumeResultDB)> EquipmentLevelUp(
            SchaleDataContext context, AccountDBServer account, EquipmentItemLevelUpRequest req)
        {
            var consumeResultData = await consumeHandler.BuildConsumeResult(context, account, req.ConsumeRequestDB);
            var targetEquipment = context.Equipments.FirstOrDefault(x => x.ServerId == req.TargetServerId)
                ?? throw new WebAPIException(WebAPIErrorCode.EquipmentNotFound);
            var currentEquipmentExcel = equipmentExcels.FirstOrDefault(x => x.Id == targetEquipment.UniqueId)
                ?? throw new WebAPIException(WebAPIErrorCode.DataEntityNotFound);
            var allEquipmentCategory = equipmentExcels
                .GetEquipmentExcelByCategory(currentEquipmentExcel.EquipmentCategory).GetCharacterEquipment();
            var (resultLevel, resultExp) = MathService.CalculateLevelExpWithoutReset(targetEquipment.Level, targetEquipment.Exp, consumeResultData.AccumulatedExp, equipmentExpLevelDatas);
            var (finalLevel, finalExp) = EquipmentService.CalculateEquipmentExpLevel(currentEquipmentExcel, equipmentLevelExcel, resultLevel, resultExp);

            targetEquipment.Level = finalLevel;
            targetEquipment.Exp = finalExp;
            targetEquipment.StackCount = 1;

            context.Equipments.Update(targetEquipment);

            // Feeding costs gold, 1 per exp point: the captured official level-up burned exactly 69,304 gold alongside its fodder, and every official response carries the updated AccountCurrencyDB.
            // Clamped so a poor account cannot go negative.
            var currency = context.GetAccountCurrencies(account.ServerId).FirstOrDefault();
            if (currency != null && consumeResultData.AccumulatedExp > 0)
            {
                var goldCost = Math.Min(consumeResultData.AccumulatedExp, currency.CurrencyDict[CurrencyTypes.Gold]);
                currency.CurrencyDict[CurrencyTypes.Gold] -= goldCost;
                currency.UpdateTimeDict[CurrencyTypes.Gold] = account.GameSettings.ServerDateTime();
            }

            await context.SaveChangesAsync();

            return (targetEquipment, consumeResultData.ConsumeResult);
        }

        public async Task<(EquipmentDBServer, ParcelResultDB, ConsumeResultDB)> EquipmentTierUp(
            SchaleDataContext context, AccountDBServer account, EquipmentItemTierUpRequest req)
        {
            await using var transaction = await context.Database.BeginTransactionAsync();
            List<ParcelResult> parcelResults = [];
            var targetEquipment = context.Equipments.FirstOrDefault(x => x.AccountServerId == account.ServerId && x.ServerId == req.TargetEquipmentServerId)
                ?? throw new WebAPIException(WebAPIErrorCode.EquipmentNotFound);
            var currentEquipmentExcel = equipmentExcels.FirstOrDefault(x => x.Id == targetEquipment.UniqueId)
                ?? throw new WebAPIException(WebAPIErrorCode.DataEntityNotFound);
            AddTierUpRecipe(parcelResults, currentEquipmentExcel.RecipeId);
            var nextEquipmentExcel = equipmentExcels.FirstOrDefault(x => x.Id == currentEquipmentExcel.NextTierEquipment)
                ?? throw new WebAPIException(WebAPIErrorCode.EquipmentCannotTierUp);

            ValidateTierUpCosts(context, account, parcelResults);
            targetEquipment.UniqueId = nextEquipmentExcel.Id;
            targetEquipment.Tier = (int)nextEquipmentExcel.TierInit;
            var parcelResolver = await parcelHandler.BuildParcel(context, account, parcelResults, isConsume: true);
            await context.SaveChangesAsync();

            // Official's tier-up carries ConsumeResultDB alongside ParcelResultDB - without it the client keeps showing the spent ingredients until the next full list. The resolver's post-consume stacks are exactly the "remaining counts" official reports.
            var consumeResult = new ConsumeResultDB
            {
                UsedEquipmentServerIdAndRemainingCounts = (parcelResolver.ParcelResult.EquipmentDBs ?? [])
                    .ToDictionary(x => x.Key, x => x.Value.StackCount),
                UsedItemServerIdAndRemainingCounts = (parcelResolver.ParcelResult.ItemDBs ?? [])
                    .ToDictionary(x => x.Key, x => x.Value.StackCount),
            };

            MergeRecipeConsumption(parcelResolver.ParcelResult, consumeResult);
            await transaction.CommitAsync();
            return (targetEquipment, parcelResolver.ParcelResult, consumeResult);
        }

        public async Task<(List<EquipmentDBServer>, GearDBServer?, ConsumeResultDB, ParcelResultDB)> EquipmentBatchGrowth(
            SchaleDataContext context, AccountDBServer account, EquipmentBatchGrowthRequest req)
        {
            await using var transaction = await context.Database.BeginTransactionAsync();
            List<EquipmentDBServer> equipmentDBs = [];
            GearDBServer gearDB = null;
            ConsumeResultDB consumeResultDB = new ConsumeResultDatas().ConsumeResult;
            ParcelResultDB parcelResultDB = new();

            List<ParcelResult> parcelResults = [];
            if (req.EquipmentBatchGrowthRequestDBs.Count != 0)
            {
                if (req.EquipmentBatchGrowthRequestDBs.Select(x => x.TargetServerId).Distinct().Count()
                    != req.EquipmentBatchGrowthRequestDBs.Count)
                    throw new WebAPIException(WebAPIErrorCode.EquipmentBatchGrowthNotValid);
                foreach (var batchGrowthDB in req.EquipmentBatchGrowthRequestDBs)
                {
                    var targetEquipment = context.Equipments.FirstOrDefault(x => x.AccountServerId == account.ServerId && x.ServerId == batchGrowthDB.TargetServerId)
                        ?? throw new WebAPIException(WebAPIErrorCode.EquipmentNotFound);
                    var currentEquipmentExcel = equipmentExcels.FirstOrDefault(x => x.Id == targetEquipment.UniqueId)
                        ?? throw new WebAPIException(WebAPIErrorCode.DataEntityNotFound);
                    var allEquipmentCategory = equipmentExcels.GetEquipmentExcelByCategory(currentEquipmentExcel.EquipmentCategory).GetCharacterEquipment();

                    var finalTier = targetEquipment.Tier;
                    var newEquipmentExcel = currentEquipmentExcel;
                    if (batchGrowthDB.AfterTier > targetEquipment.Tier)
                    {
                        // Each recipe belongs to the source tier, as in single-tier growth.
                        while ((long)newEquipmentExcel.TierInit < batchGrowthDB.AfterTier)
                        {
                            var next = allEquipmentCategory.FirstOrDefault(x => x.Id == newEquipmentExcel.NextTierEquipment);
                            if (next == null || next.TierInit <= newEquipmentExcel.TierInit
                                || (long)next.TierInit > batchGrowthDB.AfterTier)
                                throw new WebAPIException(WebAPIErrorCode.EquipmentBatchGrowthNotValid);
                            AddTierUpRecipe(parcelResults, newEquipmentExcel.RecipeId);
                            newEquipmentExcel = next;
                        }
                        targetEquipment.UniqueId = newEquipmentExcel.Id;
                        finalTier = (int)newEquipmentExcel.TierInit;
                    }
                    targetEquipment.Tier = finalTier;

                    var consumeResultData = await consumeHandler.BuildConsumeResult(context, account, batchGrowthDB.ConsumeRequestDBs, consumeResultDB, parcelResultDB);
                    var (resultLevel, resultExp) = MathService.CalculateLevelExpWithoutReset(targetEquipment.Level, targetEquipment.Exp, consumeResultData.AccumulatedExp, equipmentExpLevelDatas);
                    var (finalLevel, finalExp) = EquipmentService.CalculateEquipmentExpLevel(newEquipmentExcel, equipmentLevelExcel, resultLevel, resultExp);

                    targetEquipment.Level = finalLevel;
                    targetEquipment.Exp = finalExp;

                    consumeResultDB = consumeResultData.ConsumeResult;
                    parcelResultDB = consumeResultData.ParcelResult;

                    context.Equipments.Update(targetEquipment);
                    equipmentDBs.Add(targetEquipment);
                }
            }

            if (req.GearTierUpRequestDB != null)
            {
                var targetGear = context.Gears.FirstOrDefault(x => x.AccountServerId == account.ServerId && x.ServerId == req.GearTierUpRequestDB.TargetServerId)
                    ?? throw new WebAPIException(WebAPIErrorCode.CharacterGearNotFound);
                var targetCharacter = context.Characters.FirstOrDefault(x => x.ServerId == targetGear.BoundCharacterServerId)
                    ?? throw new WebAPIException(WebAPIErrorCode.CharacterNotFound);
                var characterGears = characterGearExcels.GetCharacterGearExcelByCharacterId(targetCharacter.UniqueId);
                var characterGear = characterGears.FirstOrDefault(x => x.Tier == targetGear.Tier + 1)
                    ?? throw new WebAPIException(WebAPIErrorCode.CharacterGearCannotTierUp);
                AddTierUpRecipe(parcelResults, characterGear.RecipeId);

                targetGear.UniqueId = characterGear.Id;
                targetGear.Tier = (int)characterGear.Tier;
                context.Gears.Update(targetGear);

                gearDB = targetGear;
            }

            ValidateTierUpCosts(context, account, parcelResults);
            var parcelResolver = await parcelHandler.BuildParcel(context, account, parcelResults, parcelResultDB, true);
            await context.SaveChangesAsync();
            MergeRecipeConsumption(parcelResolver.ParcelResult, consumeResultDB);

            await transaction.CommitAsync();
            return (equipmentDBs, gearDB, consumeResultDB, parcelResultDB);
        }

        private void AddTierUpRecipe(List<ParcelResult> costs, long recipeId)
        {
            if (recipeId == 0 || !recipeIngredientExcels.Any(x => x.Id == recipeId))
                throw new WebAPIException(WebAPIErrorCode.EquipmentCannotTierUp, "Tier-up recipe not found");
            EquipmentService.CreateRecipes(costs, recipeIngredientExcels, recipeId);
        }

        private static void MergeRecipeConsumption(ParcelResultDB parcels, ConsumeResultDB consume)
        {
            consume.UsedEquipmentServerIdAndRemainingCounts ??= [];
            consume.UsedItemServerIdAndRemainingCounts ??= [];
            consume.RemovedEquipmentServerIds ??= [];
            consume.RemovedItemServerIds ??= [];
            foreach (var (id, equipment) in parcels.EquipmentDBs ?? [])
                consume.UsedEquipmentServerIdAndRemainingCounts[id] = equipment.StackCount;
            foreach (var (id, item) in parcels.ItemDBs ?? [])
                consume.UsedItemServerIdAndRemainingCounts[id] = item.StackCount;
            foreach (var id in parcels.RemovedEquipmentIds ?? [])
            {
                consume.UsedEquipmentServerIdAndRemainingCounts[id] = 0;
                if (!consume.RemovedEquipmentServerIds.Contains(id)) consume.RemovedEquipmentServerIds.Add(id);
            }
            foreach (var id in parcels.RemovedItemIds ?? [])
            {
                consume.UsedItemServerIdAndRemainingCounts[id] = 0;
                if (!consume.RemovedItemServerIds.Contains(id)) consume.RemovedItemServerIds.Add(id);
            }
        }

        private static void ValidateTierUpCosts(SchaleDataContext context, AccountDBServer account, List<ParcelResult> costs)
        {
            foreach (var cost in costs.Aggregate())
            {
                if (cost.Amount <= 0)
                    throw new WebAPIException(WebAPIErrorCode.EquipmentCannotTierUp);
                long held = cost.Type switch
                {
                    ParcelType.Equipment => context.Equipments.Where(x => x.AccountServerId == account.ServerId
                        && x.UniqueId == cost.Id && x.BoundCharacterServerId == 0).Select(x => x.StackCount).FirstOrDefault(),
                    ParcelType.Item => context.Items.Where(x => x.AccountServerId == account.ServerId
                        && x.UniqueId == cost.Id).Select(x => x.StackCount).FirstOrDefault(),
                    ParcelType.Currency => CurrencyBalance(context, account.ServerId, (CurrencyTypes)cost.Id),
                    _ => throw new WebAPIException(WebAPIErrorCode.EquipmentCannotTierUp)
                };
                if (held < cost.Amount)
                    throw new WebAPIException(WebAPIErrorCode.EquipmentCannotTierUp, "Insufficient tier-up materials or currency");
            }
        }

        private static long CurrencyBalance(SchaleDataContext context, long accountId, CurrencyTypes type)
        {
            var currency = context.Currencies.FirstOrDefault(x => x.AccountServerId == accountId);
            if (currency == null) return 0;
            return type == CurrencyTypes.Gem
                ? currency.CurrencyDict.GetValueOrDefault(CurrencyTypes.GemPaid) + currency.CurrencyDict.GetValueOrDefault(CurrencyTypes.GemBonus)
                : currency.CurrencyDict.GetValueOrDefault(type);
        }
    }
}
