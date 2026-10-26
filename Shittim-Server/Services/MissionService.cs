using AutoMapper;
using BlueArchiveAPI.Services;
using Schale.Data;
using Schale.Data.GameModel;
using Schale.Data.ModelMapping;
using Schale.Excel;
using Schale.FlatData;
using Schale.MX.GameLogic.DBModel;

namespace Shittim_Server.Services;

public class MissionService
{
    private readonly ExcelTableService _excelService;
    private readonly IMapper _mapper;

    public MissionService(ExcelTableService excelService, IMapper mapper)
    {
        _excelService = excelService;
        _mapper = mapper;
    }

    // Older builds never advanced some claim-count missions, or split their counts by candidate
    // id. Rebuild only these counters from actual rewarded missions; action missions still require
    // their normal gameplay event. This also repairs an already-running account without replaying
    // its dailies or waiting for tomorrow.
    public bool ReconcileClaimMissionProgress(SchaleDataContext context, AccountDBServer account)
    {
        var missions = _excelService.GetTable<MissionExcelT>();
        var counters = missions.Where(m =>
            (m.Category is MissionCategory.Daily or MissionCategory.DailyFixed or MissionCategory.Weekly)
            && (m.CompleteConditionType is MissionCompleteConditionType.Reset_CompleteMission
                or MissionCompleteConditionType.Reset_DailyMissionFulfill)).ToList();
        if (counters.Count == 0)
            return false;

        var now = account.GameSettings.ServerDateTime();
        var dayStart = (now.TimeOfDay < TimeSpan.FromHours(4) ? now.AddDays(-1) : now).Date.AddHours(4);
        var weekStart = dayStart.AddDays(-(((int)dayStart.DayOfWeek + 6) % 7));
        var lastClaims = context.MissionHistories
            .Where(h => h.AccountServerId == account.ServerId)
            .Select(h => new { h.MissionUniqueId, h.CompleteTime })
            .AsEnumerable()
            .Concat(context.MissionHistories.Local.Where(h => h.AccountServerId == account.ServerId)
                .Select(h => new { h.MissionUniqueId, h.CompleteTime }))
            .GroupBy(h => h.MissionUniqueId)
            .ToDictionary(g => g.Key, g => g.Max(h => h.CompleteTime));
        var dailyIds = missions.Where(m => m.Category is MissionCategory.Daily or MissionCategory.DailySudden)
            .Select(m => m.Id).ToHashSet();
        var changed = false;

        foreach (var mission in counters)
        {
            var windowStart = mission.ResetType switch
            {
                MissionResetType.Daily => dayStart,
                MissionResetType.Weekly => weekStart,
                _ => DateTime.MinValue
            };
            var progress = context.MissionProgresses.Local.FirstOrDefault(p =>
                p.AccountServerId == account.ServerId && p.MissionUniqueId == mission.Id)
                ?? context.MissionProgresses.FirstOrDefault(p =>
                    p.AccountServerId == account.ServerId && p.MissionUniqueId == mission.Id);
            // Old claim-all could recreate even an already-rewarded counter. Remove that stale
            // progress as well, so a later direct reward cannot pay the same counter twice.
            if (lastClaims.TryGetValue(mission.Id, out var claimedAt) && claimedAt >= windowStart)
            {
                if (progress != null)
                {
                    context.MissionProgresses.Remove(progress);
                    changed = true;
                }
                continue;
            }
            var count = lastClaims.Count(h => h.Value >= windowStart &&
                (mission.CompleteConditionType == MissionCompleteConditionType.Reset_DailyMissionFulfill
                    ? dailyIds.Contains(h.Key)
                    : mission.CompleteConditionParameter is { Count: > 0 }
                        && mission.CompleteConditionParameter.Contains(h.Key)));
            if (count == 0)
                continue;

            // Preserve completed but unclaimed missions even if old history is unavailable.
            if (progress?.Complete == true)
                continue;
            if (progress == null)
            {
                progress = new MissionProgressDBServer
                {
                    AccountServerId = account.ServerId,
                    MissionUniqueId = mission.Id,
                    StartTime = now,
                    ProgressParameters = new Dictionary<long, long>()
                };
                context.MissionProgresses.Add(progress);
            }

            var key = mission.CompleteConditionType == MissionCompleteConditionType.Reset_CompleteMission
                && mission.CompleteConditionParameter is { Count: 1 }
                    ? mission.CompleteConditionParameter[0] : 0L;
            progress.ProgressParameters ??= new Dictionary<long, long>();
            var repairedCount = Math.Max(progress.ProgressParameters.GetValueOrDefault(key), count);
            if (progress.ProgressParameters.GetValueOrDefault(key) != repairedCount
                || (progress.ProgressParameters.Count != 1 && key == 0))
            {
                progress.ProgressParameters = new Dictionary<long, long> { [key] = repairedCount };
                changed = true;
            }
            if (repairedCount >= mission.CompleteConditionCount)
            {
                progress.Complete = true;
                changed = true;
            }
        }
        return changed;
    }

    public List<MissionProgressDB> UpdateMissionProgress(
        SchaleDataContext context,
        AccountDBServer account,
        MissionCompleteConditionType conditionType,
        long amount = 1,
        long? parameter = null)
    {
        var missionExcels = _excelService.GetTable<MissionExcelT>();
        var battlePassMissionExcels = _excelService.GetTable<BattlePassMissionExcelT>();
        var updatedMissions = new List<MissionProgressDB>();

        var relevantMissions = missionExcels
            .Where(m => m.CompleteConditionType == conditionType)
            .Where(m => m.Category == MissionCategory.Daily ||
                       m.Category == MissionCategory.DailySudden ||
                       m.Category == MissionCategory.DailyFixed ||
                       m.Category == MissionCategory.Weekly ||
                       m.Category == MissionCategory.Achievement ||
                       // Challenge is the per-stage "clear it in N turns" mission. Official sends these in MissionProgressDBs like any other (30150 on the campaign clear), and Mission_List serves them from the same storage. Every Reset_CompleteCampaignStageMinimumTurn row is Challenge.
                       m.Category == MissionCategory.Challenge)
            .Select(m => new { m.Id, m.CompleteConditionParameter, m.CompleteConditionCount, m.ResetType, IsBattlePass = false })
            .ToList();

        // BattlePass missions share the MissionProgresses storage (BattlePass_MissionList serves them from it), but they must never reach the returned list: handlers attach it to client responses as MissionProgressDBs,
        // and the client's mission screen cannot resolve a BattlePassMissionExcel id (2000001+) against MissionExcel - unresolvable ids break the screen.
        // Progress is still persisted for both kinds below.
        var relevantBpMissions = battlePassMissionExcels
            .Where(m => m.CompleteConditionType == conditionType)
            .Select(m => new { m.Id, m.CompleteConditionParameter, m.CompleteConditionCount, m.ResetType, IsBattlePass = true })
            .ToList();

        relevantMissions.AddRange(relevantBpMissions);

        var lowerIsBetter = IsLowerBetter(conditionType);
        var now = account.GameSettings.ServerDateTime();
        var dayStart = (now.TimeOfDay < TimeSpan.FromHours(4) ? now.AddDays(-1) : now).Date.AddHours(4);
        var weekStart = dayStart.AddDays(-(((int)dayStart.DayOfWeek + 6) % 7));
        var missionIds = relevantMissions.Where(m => !m.IsBattlePass).Select(m => m.Id).ToList();
        // BuildParcel can save deleted progress rows before the reward handler advances meta missions.
        // Include both saved and pending claims so that advancing another daily cannot recreate a
        // mission already rewarded in this window (especially DailyFixed 1701 in claim-all).
        var lastClaims = context.MissionHistories
            .Where(h => h.AccountServerId == account.ServerId && missionIds.Contains(h.MissionUniqueId))
            .Select(h => new { h.MissionUniqueId, h.CompleteTime })
            .AsEnumerable()
            .Concat(context.MissionHistories.Local
                .Where(h => h.AccountServerId == account.ServerId && missionIds.Contains(h.MissionUniqueId))
                .Select(h => new { h.MissionUniqueId, h.CompleteTime }))
            .GroupBy(h => h.MissionUniqueId)
            .ToDictionary(g => g.Key, g => g.Max(h => h.CompleteTime));

        foreach (var mission in relevantMissions)
        {
            if (!mission.IsBattlePass && lastClaims.TryGetValue(mission.Id, out var lastClaim)
                && (mission.ResetType switch
                {
                    MissionResetType.Daily => lastClaim >= dayStart,
                    MissionResetType.Weekly => lastClaim >= weekStart,
                    _ => true
                }))
                continue;

            // A mission that names its subject only moves for that subject.
            // Letting a caller with no parameter tick every parameterised row of the condition type means one campaign clear satisfies all 259 "clear stage X" missions at once.
            var declared = mission.CompleteConditionParameter;
            var hasDeclared = declared is { Count: > 0 };
            if (hasDeclared && (!parameter.HasValue || !declared!.Contains(parameter.Value)))
                continue;

            // When a mission requires completing N missions from a list of candidate mission IDs (e.g. Reset_CompleteMission),
            // parameter.Value is the specific mission just completed. The count is accumulated in bucket 0L rather than under parameter.Value,
            // because no single candidate mission alone can or will reach the total required count.
            var isMultiParameterCount = hasDeclared && declared!.Count > 1 &&
                conditionType is MissionCompleteConditionType.Reset_CompleteMission;

            var key = isMultiParameterCount ? 0L : (hasDeclared ? parameter!.Value : 0L);

            var existingMission = context.MissionProgresses.Local
                .FirstOrDefault(m => m.AccountServerId == account.ServerId && m.MissionUniqueId == mission.Id)
                ?? context.MissionProgresses
                    .FirstOrDefault(m => m.AccountServerId == account.ServerId && m.MissionUniqueId == mission.Id);

            if (existingMission != null && existingMission.Complete)
                continue;

            if (existingMission == null)
            {
                existingMission = new MissionProgressDBServer
                {
                    AccountServerId = account.ServerId,
                    MissionUniqueId = mission.Id,
                    StartTime = account.GameSettings.ServerDateTime(),
                    ProgressParameters = new Dictionary<long, long>(),
                    Complete = false
                };
                context.MissionProgresses.Add(existingMission);
            }

            existingMission.ProgressParameters ??= new Dictionary<long, long>();
            var current = existingMission.ProgressParameters.GetValueOrDefault(key);

            if (lowerIsBetter)
            {
                // Here `amount` is a result to beat, not a tally: the turn count the stage was cleared in. Keeping the best run is what makes the mission completable at all - accumulating would walk the number away from the target on every replay.
                existingMission.ProgressParameters[key] = current == 0 ? amount : Math.Min(current, amount);
                existingMission.Complete = existingMission.ProgressParameters[key] <= mission.CompleteConditionCount;
            }
            else
            {
                existingMission.ProgressParameters[key] = current + amount;
                existingMission.Complete = existingMission.ProgressParameters[key] >= mission.CompleteConditionCount;
            }

            if (!mission.IsBattlePass)
                updatedMissions.Add(existingMission.ToMap(_mapper));
        }

        return updatedMissions;
    }

    // Conditions whose progress is a personal best rather than a running total - the target is a ceiling to get under, so a smaller number is further along.
    // Official's capture shows the per-stage turn mission sitting at {"1161101": 5} against a count of 4 and NOT complete.
    private static bool IsLowerBetter(MissionCompleteConditionType conditionType)
        => conditionType is MissionCompleteConditionType.Reset_CompleteCampaignStageMinimumTurn
            or MissionCompleteConditionType.Reset_EventCompleteCampaignStageMinimumTurn;
}
