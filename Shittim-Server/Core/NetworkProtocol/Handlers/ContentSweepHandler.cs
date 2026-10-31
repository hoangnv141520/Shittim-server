using Microsoft.EntityFrameworkCore;
using AutoMapper;
using BlueArchiveAPI.Services;
using Schale.Data;
using Schale.Data.GameModel;
using Schale.Data.ModelMapping;
using Schale.MX.NetworkProtocol;
using Schale.MX.GameLogic.DBModel;
using Schale.MX.GameLogic.Parcel;
using Schale.MX.Core.Math;
using Schale.FlatData;
using Schale.Excel;
using Shittim_Server.Core;
using Shittim_Server.Services;
using Shittim_Server.Managers;

namespace Shittim_Server.Core.NetworkProtocol.Handlers;

public class ContentSweepHandler : ProtocolHandlerBase
{
    private readonly ISessionKeyService _sessionService;
    private readonly ExcelTableService _excelService;
    private readonly ParcelHandler _parcelHandler;
    private readonly CampaignManager _campaignManager;
    private readonly MissionService _missionService;
    private readonly IMapper _mapper;

    public ContentSweepHandler(
        IProtocolHandlerRegistry registry,
        ISessionKeyService sessionService,
        ExcelTableService excelService,
        ParcelHandler parcelHandler,
        CampaignManager campaignManager,
        MissionService missionService,
        IMapper mapper) : base(registry)
    {
        _sessionService = sessionService;
        _excelService = excelService;
        _parcelHandler = parcelHandler;
        _campaignManager = campaignManager;
        _missionService = missionService;
        _mapper = mapper;
    }

    [ProtocolHandler(Protocol.ContentSweep_Request)]
    public async Task<ContentSweepResponse> Request(
        SchaleDataContext db,
        ContentSweepRequest request,
        ContentSweepResponse response)
    {
        var account = await _sessionService.GetAuthenticatedUser(db, request.SessionKey);

        if (request.Count is <= 0 or > 10_000)
            throw new WebAPIException(WebAPIErrorCode.InvalidSweepRequest, "Invalid sweep count");

        return request.Content switch
        {
            ContentType.WeekDungeon => await SweepWeekDungeon(db, account, request, response),
            ContentType.SchoolDungeon => await SweepSchoolDungeon(db, account, request, response),
            ContentType.EventContentMainStage or ContentType.EventContentSubStage
                or ContentType.EventContentMainGroundStage or ContentType.EventContentStoryStage
                or ContentType.EventContent => await SweepEvent(db, account, request, response),
            _ => await SweepCampaign(db, account, request, response)
        };
    }

    private async Task<ContentSweepResponse> SweepEvent(
        SchaleDataContext db,
        AccountDBServer account,
        ContentSweepRequest request,
        ContentSweepResponse response)
    {
        if (request.Count is <= 0 or > 10_000)
            throw new WebAPIException(WebAPIErrorCode.InvalidSweepRequest, "Invalid event sweep count");

        var stage = _excelService.GetTable<EventContentStageExcelT>()
            .FirstOrDefault(x => x.Id == request.StageId);
        if (stage == null || (request.EventContentId != 0 && request.EventContentId != stage.EventContentId))
            throw new WebAPIException(WebAPIErrorCode.InvalidSweepRequest, "Event stage does not match the sweep request");

        // ParcelHandler participates in this transaction: AP, rewards, exp and
        // history must all commit together, including when a reward fails.
        await using var transaction = db.Database.CurrentTransaction == null
            ? await db.Database.BeginTransactionAsync() : null;
        var history = await db.CampaignStageHistories.FirstOrDefaultAsync(x =>
            x.AccountServerId == account.ServerId && x.StageUniqueId == stage.Id);
        if (history == null || !history.IsClearedEver
            || !history.Star1Flag || !history.Star2Flag || !history.Star3Flag)
            throw new WebAPIException(WebAPIErrorCode.ContentSweepNotOpened, "Event sweep requires a three-star clear");

        var cost = checked((long)stage.StageEnterCostAmount * request.Count);
        var currencies = await db.Currencies.FirstOrDefaultAsync(x => x.AccountServerId == account.ServerId);
        if (currencies != null) ActionPointRegeneration.Apply(currencies, account, _excelService);
        var available = stage.StageEnterCostType switch
        {
            ParcelType.Currency => (await db.Currencies.FirstOrDefaultAsync(x => x.AccountServerId == account.ServerId))?
                .CurrencyDict.GetValueOrDefault((CurrencyTypes)stage.StageEnterCostId) ?? 0,
            ParcelType.Item => await db.Items.Where(x => x.AccountServerId == account.ServerId
                && x.UniqueId == stage.StageEnterCostId).SumAsync(x => x.StackCount),
            _ => 0
        };
        if (cost < 0 || available < cost)
            throw new WebAPIException(WebAPIErrorCode.InvalidSweepRequest, "Not enough event stage entrance currency");

        await _parcelHandler.BuildParcel(db, account,
            new ParcelResult(stage.StageEnterCostType, stage.StageEnterCostId, cost), isConsume: true);

        var rewards = _excelService.GetTable<EventContentStageRewardExcelT>()
            .GetAllRewardsByGroupId(stage.EventContentStageRewardId).ToList();
        var gacha = _excelService.GetTable<GachaElementExcelT>();
        var clearParcels = new List<List<ParcelInfo>>();
        var allRewards = new List<ParcelResult>();
        for (var i = 0; i < request.Count; i++)
        {
            // Roll each run separately; first-clear, permanent and three-star
            // rewards must never be paid again by a sweep.
            var drops = EventContentCampaignManager.RolledDrops(rewards).GenerateGachaGroup(gacha);
            clearParcels.Add(ToParcelInfos(drops));
            allRewards.AddRange(drops);
        }
        allRewards.Add(new ParcelResult(ParcelType.AccountExp, 0, cost));
        var result = await _parcelHandler.BuildParcel(db, account, allRewards);

        var now = account.GameSettings.ServerDateTime();
        CampaignDailyPlays.Record(history, now, request.Count);
        await db.SaveChangesAsync();
        if (transaction != null) await transaction.CommitAsync();

        response.ClearParcels = clearParcels;
        response.BonusParcels = [];
        response.EventContentBonusParcels = [];
        response.ParcelResult = result.ParcelResult;
        response.CampaignStageHistoryDB = history.ToMap(_mapper);
        return response;
    }

    private async Task<ContentSweepResponse> SweepCampaign(
        SchaleDataContext db,
        AccountDBServer account,
        ContentSweepRequest request,
        ContentSweepResponse response)
    {
        var campaignStageExcels = _excelService.GetTable<CampaignStageExcelT>();
        var campaignStageRewardExcels = _excelService.GetTable<CampaignStageRewardExcelT>();
        var campaignChapterExcels = _excelService.GetTable<CampaignChapterExcelT>();
        var gachaElementExcels = _excelService.GetTable<GachaElementExcelT>();

        var stageExcel = campaignStageExcels.GetCampaignStageId(request.StageId);
        await using var transaction = db.Database.CurrentTransaction == null
            ? await db.Database.BeginTransactionAsync() : null;
        await CampaignDailyPlays.CheckHardLimit(db, account, stageExcel, request.Count, _excelService);

        var apCost = checked((long)stageExcel.StageEnterCostAmount * request.Count);
        var apCostParcel = new ParcelResult(stageExcel.StageEnterCostType, stageExcel.StageEnterCostId, apCost);
        await _parcelHandler.BuildParcel(db, account, apCostParcel, isConsume: true);

        var clearParcels = new List<List<ParcelInfo>>();
        var allRewardParcels = new List<ParcelResult>();

        for (int i = 0; i < request.Count; i++)
        {
            // A sweep pays only the per-run drop table.
            // The GachaGroup rows are rolled to concrete items up front: they are IsDisplayed=false in the excel, and sent raw they show up as blank cells in the client's sweep result.
            var rewardDatas = campaignStageRewardExcels.GetAllRewardsByGroupId(stageExcel.CampaignStageRewardId);
            var sweepRewards = ConcentrateCampaignManager.RolledDrops(rewardDatas).GenerateGachaGroup(gachaElementExcels);

            clearParcels.Add(ToParcelInfos(sweepRewards));
            allRewardParcels.AddRange(sweepRewards);
        }

        var bonusParcels = new List<ParcelInfo>();

        var expAmount = stageExcel.StageEnterCostAmount * request.Count;
        allRewardParcels.Add(new ParcelResult(ParcelType.AccountExp, 0, expAmount));

        var parcelResult = await _parcelHandler.BuildParcel(db, account, allRewardParcels);

        var dateTime = account.GameSettings.ServerDateTime();
        var chapterId = campaignChapterExcels.GetChapterIdFromStageId(request.StageId);
        var historyDb = db.CampaignStageHistories.FirstOrDefault(x => x.AccountServerId == account.ServerId && x.StageUniqueId == request.StageId);

        if (historyDb == null)
        {
            historyDb = new CampaignStageHistoryDBServer(account.ServerId, request.StageId, chapterId, dateTime)
            {
                TodayPlayCount = (int)request.Count,
                LastPlay = dateTime
            };
            db.CampaignStageHistories.Add(historyDb);
        }
        else
        {
            CampaignDailyPlays.Record(historyDb, dateTime, request.Count);
            db.CampaignStageHistories.Update(historyDb);
        }

        await db.SaveChangesAsync();

        response.ClearParcels = clearParcels;
        response.BonusParcels = bonusParcels;
        if (transaction != null) await transaction.CommitAsync();
        response.ParcelResult = parcelResult.ParcelResult;
        response.CampaignStageHistoryDB = historyDb.ToMap(_mapper);

        return response;
    }

    private async Task<ContentSweepResponse> SweepWeekDungeon(
        SchaleDataContext db,
        AccountDBServer account,
        ContentSweepRequest request,
        ContentSweepResponse response)
    {
        var stageExcel = _excelService.GetTable<WeekDungeonExcelT>().GetDungeonByStageId(request.StageId);
        var rewardExcels = _excelService.GetTable<WeekDungeonRewardExcelT>();

        var costParcels = ParcelResult.ConvertParcelResult(
            stageExcel.StageEnterCostType,
            stageExcel.StageEnterCostId,
            stageExcel.StageEnterCostAmount.Select(x => (long)x * request.Count).ToList());
        await _parcelHandler.BuildParcel(db, account, costParcels, isConsume: true);

        var clearParcels = new List<List<ParcelInfo>>();
        var allRewardParcels = new List<ParcelResult>();

        for (int i = 0; i < request.Count; i++)
        {
            var rewardDatas = rewardExcels.GetAllRewardsByGroupId(stageExcel.StageRewardId);
            var sweepRewards = GetCalcWeekDungeonProbability(rewardDatas);

            clearParcels.Add(ToParcelInfos(sweepRewards));
            allRewardParcels.AddRange(sweepRewards);
        }

        // Mirrors WeekDungeonManager.AddExperience: only the commission (FindGift/Blood)
        // dungeons grant account exp, equal to the stage enter cost.
        if (stageExcel.WeekDungeonType is WeekDungeonType.FindGift or WeekDungeonType.Blood)
            allRewardParcels.Add(new ParcelResult(
                ParcelType.AccountExp, 0, stageExcel.StageEnterCostAmount.Sum() * request.Count));

        var parcelResult = await _parcelHandler.BuildParcel(db, account, allRewardParcels);
        await db.SaveChangesAsync();

        response.ClearParcels = clearParcels;
        response.BonusParcels = [];
        response.ParcelResult = parcelResult.ParcelResult;

        return response;
    }

    private async Task<ContentSweepResponse> SweepSchoolDungeon(
        SchaleDataContext db,
        AccountDBServer account,
        ContentSweepRequest request,
        ContentSweepResponse response)
    {
        var stageExcel = _excelService.GetTable<SchoolDungeonStageExcelT>().GetDungeonByStageId(request.StageId);
        var rewardExcels = _excelService.GetTable<SchoolDungeonRewardExcelT>();

        var costParcels = ParcelResult.ConvertParcelResult(
            stageExcel.StageEnterCostType,
            stageExcel.StageEnterCostId,
            stageExcel.StageEnterCostAmount.Select(x => x * request.Count).ToList());
        await _parcelHandler.BuildParcel(db, account, costParcels, isConsume: true);

        var clearParcels = new List<List<ParcelInfo>>();
        var allRewardParcels = new List<ParcelResult>();

        for (int i = 0; i < request.Count; i++)
        {
            var rewardDatas = rewardExcels.GetAllRewardsByGroupId(stageExcel.StageRewardId);
            var sweepRewards = GetCalcSchoolDungeonProbability(rewardDatas);

            clearParcels.Add(ToParcelInfos(sweepRewards));
            allRewardParcels.AddRange(sweepRewards);
        }

        // Mirrors SchoolDungeonManager: account exp equals the AP portion of the enter cost.
        var expParcels = ParcelResult.ConvertParcelResult(
            stageExcel.StageEnterCostType,
            stageExcel.StageEnterCostId,
            stageExcel.StageEnterCostAmount.Select(x => x * request.Count).ToList());
        allRewardParcels.Add(expParcels.ConvertToAccountExp());

        var parcelResult = await _parcelHandler.BuildParcel(db, account, allRewardParcels);
        await db.SaveChangesAsync();

        response.ClearParcels = clearParcels;
        response.BonusParcels = [];
        response.ParcelResult = parcelResult.ParcelResult;

        return response;
    }

    private static List<ParcelInfo> ToParcelInfos(List<ParcelResult> parcels)
        => parcels.Select(r => new ParcelInfo
        {
            Key = new ParcelKeyPair { Type = r.Type, Id = r.Id },
            Amount = r.Amount,
            Multiplier = BasisPoint.One,
            Probability = BasisPoint.One
        }).ToList();

    private static List<ParcelResult> GetCalcWeekDungeonProbability(IEnumerable<WeekDungeonRewardExcelT> rewardExcels)
    {
        var result = new List<ParcelResult>();
        foreach (var rewardExcel in rewardExcels)
        {
            if (!GenerateProbability(rewardExcel.RewardParcelProbability)) continue;
            result.Add(new ParcelResult(
                rewardExcel.RewardParcelType,
                rewardExcel.RewardParcelId,
                rewardExcel.RewardParcelAmount));
        }
        return result;
    }

    private static List<ParcelResult> GetCalcSchoolDungeonProbability(IEnumerable<SchoolDungeonRewardExcelT> rewardExcels)
    {
        var result = new List<ParcelResult>();
        foreach (var rewardExcel in rewardExcels)
        {
            if (!GenerateProbability(rewardExcel.RewardParcelProbability)) continue;
            result.Add(new ParcelResult(
                rewardExcel.RewardParcelType,
                rewardExcel.RewardParcelId,
                rewardExcel.RewardParcelAmount));
        }
        return result;
    }

    [ProtocolHandler(Protocol.ContentSweep_MultiSweep)]
    public async Task<ContentSweepMultiSweepResponse> MultiSweep(
        SchaleDataContext db,
        ContentSweepMultiSweepRequest request,
        ContentSweepMultiSweepResponse response)
    {
        var account = await _sessionService.GetAuthenticatedUser(db, request.SessionKey);

        var clearParcels = new List<List<ParcelInfo>>();
        var bonusParcels = new List<ParcelInfo>();
        var campaignStageHistoryDbs = new List<CampaignStageHistoryDB>();
        var aggregatedParcelForDisplay = new List<ParcelInfo>();
        var aggregatedParcelForMission = new List<ParcelInfo>();

        var parameters = request.MultiSweepParameters?.ToList() ?? [];
        long totalSweepCount = 0;

        foreach (var parameter in parameters)
        {
            if (parameter.SweepCount <= 0)
                continue;

            totalSweepCount += parameter.SweepCount;

            var singleRequest = new ContentSweepRequest
            {
                SessionKey = request.SessionKey,
                Content = parameter.ContentType,
                EventContentId = parameter.EventContentId ?? 0,
                StageId = parameter.StageId,
                Count = parameter.SweepCount
            };

            var singleResponse = await Request(db, singleRequest, new ContentSweepResponse());

            if (singleResponse.ClearParcels != null)
                clearParcels.AddRange(singleResponse.ClearParcels);

            if (singleResponse.BonusParcels != null)
                bonusParcels.AddRange(singleResponse.BonusParcels);

            if (singleResponse.CampaignStageHistoryDB != null)
                campaignStageHistoryDbs.Add(singleResponse.CampaignStageHistoryDB);

            if (singleResponse.ParcelResult?.DisplaySequence != null)
                aggregatedParcelForDisplay.AddRange(singleResponse.ParcelResult.DisplaySequence);

            if (singleResponse.ParcelResult?.ParcelForMission != null)
                aggregatedParcelForMission.AddRange(singleResponse.ParcelResult.ParcelForMission);
        }

        var currency = db.GetAccountCurrencies(account.ServerId).FirstOrDefaultMapTo(_mapper);
        response.ClearParcels = clearParcels;
        response.BonusParcels = bonusParcels;
        response.CampaignStageHistoryDBs = campaignStageHistoryDbs;
        response.ParcelResult = new ParcelResultDB
        {
            AccountDB = account.ToMap(_mapper),
            AccountCurrencyDB = currency,
            DisplaySequence = aggregatedParcelForDisplay,
            ParcelForMission = aggregatedParcelForMission
        };

        var missionProgresses = totalSweepCount > 0
            ? _missionService.UpdateMissionProgress(
                db,
                account,
                MissionCompleteConditionType.Achieve_ClearCampaignStageCount,
                totalSweepCount)
            : [];

        if (missionProgresses.Count > 0)
            response.MissionProgressDBs = missionProgresses;

        response.ServerNotification = (account.UnReadMailCount.GetValueOrDefault() > 0)
            ? ServerNotificationFlag.HasUnreadMail
            : ServerNotificationFlag.None;

        await db.SaveChangesAsync();

        return response;
    }

    [ProtocolHandler(Protocol.ContentSweep_SetMultiSweepPreset)]
    public async Task<ContentSweepSetMultiSweepPresetResponse> SetMultiSweepPreset(
        SchaleDataContext db,
        ContentSweepSetMultiSweepPresetRequest request,
        ContentSweepSetMultiSweepPresetResponse response)
    {
        var account = await _sessionService.GetAuthenticatedUser(db, request.SessionKey);

        var presets = account.GameSettings.MultiSweepPresetDBs ?? [];
        var current = presets.FirstOrDefault(x => x.PresetId == request.PresetId);

        if (current == null)
        {
            current = new MultiSweepPresetDB
            {
                PresetId = request.PresetId
            };
            presets.Add(current);
        }

        current.PresetName = request.PresetName;
        current.StageIds = request.StageIds?.ToList() ?? [];
        current.ParcelIds = request.ParcelIds?.ToList() ?? [];

        account.GameSettings.MultiSweepPresetDBs = presets;
        db.Accounts.Update(account);
        await db.SaveChangesAsync();

        response.MultiSweepPresetDBs = account.GameSettings.MultiSweepPresetDBs;

        return response;
    }

    [ProtocolHandler(Protocol.ContentSweep_MultiSweepPresetList)]
    public async Task<ContentSweepMultiSweepPresetListResponse> MultiSweepPresetList(
        SchaleDataContext db,
        ContentSweepMultiSweepPresetListRequest request,
        ContentSweepMultiSweepPresetListResponse response)
    {
        var account = await _sessionService.GetAuthenticatedUser(db, request.SessionKey);

        response.MultiSweepPresetDBs = account.GameSettings.MultiSweepPresetDBs ?? [];

        return response;
    }

    [ProtocolHandler(Protocol.ContentSweep_SetMultiSweepPresetName)]
    public async Task<ContentSweepSetMultiSweepPresetNameResponse> SetMultiSweepPresetName(
        SchaleDataContext db,
        ContentSweepSetMultiSweepPresetNameRequest request,
        ContentSweepSetMultiSweepPresetNameResponse response)
    {
        var account = await _sessionService.GetAuthenticatedUser(db, request.SessionKey);

        var presets = account.GameSettings.MultiSweepPresetDBs ?? [];
        var current = presets.FirstOrDefault(x => x.PresetId == request.PresetId);
        if (current != null)
        {
            current.PresetName = request.PresetName;
            account.GameSettings.MultiSweepPresetDBs = presets;
            db.Accounts.Update(account);
            await db.SaveChangesAsync();
        }

        response.MultiSweepPresetDBs = presets;

        return response;
    }

    private static bool GenerateProbability(long probability)
    {
        if (probability == 0) return true;
        return Random.Shared.Next(10000) < probability;
    }
}
