using AutoMapper;
using Microsoft.EntityFrameworkCore;
using BlueArchiveAPI.Services;
using Schale.Data;
using Schale.Data.GameModel;
using Schale.Data.ModelMapping;
using Schale.MX.GameLogic.DBModel;
using Schale.MX.NetworkProtocol;
using Schale.FlatData;
using Shittim_Server.Core;

namespace Shittim_Server.Core.NetworkProtocol.Handlers;

public class FriendHandler : ProtocolHandlerBase
{
    private readonly ISessionKeyService _sessionService;
    private readonly IMapper _mapper;
    private readonly ExcelTableService _excelService;

    public FriendHandler(
        IProtocolHandlerRegistry registry,
        ISessionKeyService sessionService,
        IMapper mapper,
        ExcelTableService excelService) : base(registry)
    {
        _sessionService = sessionService;
        _mapper = mapper;
        _excelService = excelService;
    }

    private FriendDB BuildFriend(SchaleDataContext db, AccountDBServer target)
    {
        var character = db.GetAccountCharacters(target.ServerId)
            .FirstOrDefault(x => x.ServerId == target.RepresentCharacterServerId);
        var attachment = db.GetAccountAttachments(target.ServerId).FirstOrDefault();
        return new FriendDB
        {
            AccountId = target.ServerId,
            Nickname = target.Nickname,
            Level = target.Level,
            RepresentCharacterUniqueId = character?.UniqueId ?? 0,
            RepresentCharacterCostumeId = character == null ? 0 : db.GetAccountCostumes(target.ServerId)
                .Where(x => x.BoundCharacterServerId == character.ServerId).Select(x => x.UniqueId).FirstOrDefault(),
            LastConnectTime = target.LastConnectTime,
            ComfortValue = db.GetAccountCafes(target.ServerId).FirstOrDefault(x => x.CafeId == 1)?.ProductionDB?.ComfortValue ?? 0,
            FriendCount = target.ContentInfo.Friends.FriendAccountIds.Count,
            AttachmentDB = attachment == null ? null : _mapper.Map<AccountAttachmentDB>(attachment)
        };
    }

    private FriendIdCardDB BuildIdCard(SchaleDataContext db, AccountDBServer account)
    {
        var card = account.ContentInfo.IdCard;
        card.Level = account.Level;
        card.FriendCode = AccountHandler.BuildFriendCode(account.ServerId);
        card.LastConnectTime = account.LastConnectTime;
        if (card.RepresentCharacterUniqueId == 0)
            card.RepresentCharacterUniqueId = BuildFriend(db, account).RepresentCharacterUniqueId;
        return card;
    }

    private (FriendDB[] Friends, FriendDB[] Sent, FriendDB[] Received, FriendDB[] Blocked) GetLists(
        SchaleDataContext db, AccountDBServer account)
    {
        var state = account.ContentInfo.Friends;
        FriendDB[] Profiles(HashSet<long> ids) => db.Accounts.Where(x => ids.Contains(x.ServerId))
            .ToList().Select(x => BuildFriend(db, x)).ToArray();
        return (Profiles(state.FriendAccountIds), Profiles(state.SentRequestAccountIds),
            Profiles(state.ReceivedRequestAccountIds), Profiles(state.BlockedAccountIds));
    }

    private static AccountDBServer GetTarget(SchaleDataContext db, AccountDBServer account, long targetId,
        WebAPIErrorCode selfError)
    {
        if (targetId == account.ServerId)
            throw new WebAPIException(selfError);
        return db.Accounts.FirstOrDefault(x => x.ServerId == targetId)
            ?? throw new WebAPIException(WebAPIErrorCode.FriendRequestNotFound);
    }

    private static bool IsBlocked(AccountDBServer account, AccountDBServer target) =>
        account.ContentInfo.Friends.BlockedAccountIds.Contains(target.ServerId) ||
        target.ContentInfo.Friends.BlockedAccountIds.Contains(account.ServerId);

    private static void EnsureNotBlocked(AccountDBServer account, AccountDBServer target, WebAPIErrorCode error)
    {
        if (IsBlocked(account, target))
            throw new WebAPIException(error);
    }

    private static void ClearRequests(AccountDBServer account, AccountDBServer target)
    {
        account.ContentInfo.Friends.SentRequestAccountIds.Remove(target.ServerId);
        account.ContentInfo.Friends.ReceivedRequestAccountIds.Remove(target.ServerId);
        target.ContentInfo.Friends.SentRequestAccountIds.Remove(account.ServerId);
        target.ContentInfo.Friends.ReceivedRequestAccountIds.Remove(account.ServerId);
    }

    private static void Befriend(AccountDBServer account, AccountDBServer target)
    {
        ClearRequests(account, target);
        account.ContentInfo.Friends.FriendAccountIds.Add(target.ServerId);
        target.ContentInfo.Friends.FriendAccountIds.Add(account.ServerId);
    }

    private static async Task SavePair(SchaleDataContext db, AccountDBServer account, AccountDBServer target)
    {
        db.Entry(account).Property(x => x.ContentInfo).IsModified = true;
        db.Entry(target).Property(x => x.ContentInfo).IsModified = true;
        await db.SaveChangesAsync();
    }

    private void EnsureFriendCapacity(AccountDBServer account, AccountDBServer target, bool accepting)
    {
        var limit = _excelService.GetTable<ConstCommonExcelT>().First().MaxFriendsCount;
        if (account.ContentInfo.Friends.FriendAccountIds.Count >= limit)
            throw new WebAPIException(accepting ? WebAPIErrorCode.FriendCannotAcceptMaxFriendCountReached
                : WebAPIErrorCode.FriendCannotRequestMaxFriendCountReached);
        if (target.ContentInfo.Friends.FriendAccountIds.Count >= limit)
            throw new WebAPIException(WebAPIErrorCode.FriendOpponentMaxFriendCountReached);
    }

    [ProtocolHandler(Protocol.Friend_Check)]
    public async Task<FriendCheckResponse> Check(
        SchaleDataContext db,
        FriendCheckRequest request,
        FriendCheckResponse response)
    {
        var account = await _sessionService.GetAuthenticatedUser(db, request.SessionKey);

        if (account.ContentInfo.Friends.ReceivedRequestAccountIds.Count > 0)
            response.ServerNotification |= ServerNotificationFlag.HasFriendRequest;
        return response;
    }

    [ProtocolHandler(Protocol.Friend_List)]
    public async Task<FriendListResponse> List(
        SchaleDataContext db,
        FriendListRequest request,
        FriendListResponse response)
    {
        var account = await _sessionService.GetAuthenticatedUser(db, request.SessionKey);

        var lists = GetLists(db, account);
        response.FriendDBs = lists.Friends;
        response.SentRequestFriendDBs = lists.Sent;
        response.ReceivedRequestFriendDBs = lists.Received;
        response.BlockedUserDBs = lists.Blocked;
        response.FriendIdCardDB = BuildIdCard(db, account);
        response.IdCardBackgroundDBs = db.GetAccountIdCardBackgrounds(account.ServerId).ToMapList(_mapper).ToArray();

        return response;
    }

    [ProtocolHandler(Protocol.Friend_ListByIds)]
    public async Task<FriendListByIdsResponse> ListByIds(
        SchaleDataContext db,
        FriendListByIdsRequest request,
        FriendListByIdsResponse response)
    {
        var account = await _sessionService.GetAuthenticatedUser(db, request.SessionKey);

        response.ListResult = [];

        if (request.TargetAccountIds != null && request.TargetAccountIds.Length > 0)
        {
            var targetAccounts = await db.Accounts
                .Where(a => request.TargetAccountIds.Contains(a.ServerId))
                .ToListAsync();

            response.ListResult = targetAccounts.Select(target => BuildFriend(db, target)).ToArray();
        }

        return response;
    }

    [ProtocolHandler(Protocol.Friend_SendFriendRequest)]
    public async Task<FriendSendFriendRequestResponse> SendFriendRequest(
        SchaleDataContext db,
        FriendSendFriendRequestRequest request,
        FriendSendFriendRequestResponse response)
    {
        var account = await _sessionService.GetAuthenticatedUser(db, request.SessionKey);

        var target = GetTarget(db, account, request.TargetAccountId, WebAPIErrorCode.FriendRequestTargetIsYourself);
        EnsureNotBlocked(account, target, WebAPIErrorCode.FriendBlockUserCannotSendRequest);
        var friends = account.ContentInfo.Friends;
        if (friends.FriendAccountIds.Contains(target.ServerId))
            throw new WebAPIException(WebAPIErrorCode.FriendAlreadyFriend);
        EnsureFriendCapacity(account, target, false);

        // Repeated sends leave one pending request. Receiving the reverse request still
        // requires Accept; merely sending must not silently approve an incoming request.
        if (target.ContentInfo.IdCard.AutoAcceptFriendRequest)
            Befriend(account, target);
        else
        {
            var requestLimit = _excelService.GetTable<ConstCommonExcelT>().First().MaxFriendsRequest;
            if (!friends.SentRequestAccountIds.Contains(target.ServerId) && friends.SentRequestAccountIds.Count >= requestLimit)
                throw new WebAPIException(WebAPIErrorCode.FriendMaxSentRequestReached);
            if (!target.ContentInfo.Friends.ReceivedRequestAccountIds.Contains(account.ServerId) &&
                target.ContentInfo.Friends.ReceivedRequestAccountIds.Count >= requestLimit)
                throw new WebAPIException(WebAPIErrorCode.FriendMaxReceivedRequestReached);
            friends.SentRequestAccountIds.Add(target.ServerId);
            target.ContentInfo.Friends.ReceivedRequestAccountIds.Add(account.ServerId);
        }
        await SavePair(db, account, target);
        var lists = GetLists(db, account);
        response.FriendDBs = lists.Friends;
        response.SentRequestFriendDBs = lists.Sent;
        response.ReceivedRequestFriendDBs = lists.Received;
        response.BlockedUserDBs = lists.Blocked;
        return response;
    }

    [ProtocolHandler(Protocol.Friend_GetIdCard)]
    public async Task<FriendGetIdCardResponse> GetIdCard(
        SchaleDataContext db,
        FriendGetIdCardRequest request,
        FriendGetIdCardResponse response)
    {
        var account = await _sessionService.GetAuthenticatedUser(db, request.SessionKey);

        response.FriendIdCardDB = BuildIdCard(db, account);

        return response;
    }

    [ProtocolHandler(Protocol.Friend_SetIdCard)]
    public async Task<FriendSetIdCardResponse> SetIdCard(
        SchaleDataContext db,
        FriendSetIdCardRequest request,
        FriendSetIdCardResponse response)
    {
        var account = await _sessionService.GetAuthenticatedUser(db, request.SessionKey);

        var card = account.ContentInfo.IdCard;
        card.Comment = request.Comment;
        card.RepresentCharacterUniqueId = request.RepresentCharacterUniqueId;
        card.EmblemId = request.EmblemId;
        card.SearchPermission = request.SearchPermission;
        card.AutoAcceptFriendRequest = request.AutoAcceptFriendRequest;
        card.ShowAccountLevel = request.ShowAccountLevel;
        card.ShowFriendCode = request.ShowFriendCode;
        card.ShowRaidRanking = request.ShowRaidRanking;
        card.ShowArenaRanking = request.ShowArenaRanking;
        card.ShowEliminateRaidRanking = request.ShowEliminateRaidRanking;
        card.ShowMultiFloorRaidClearedDifficulty = request.ShowMultiFloorRaidClearedDifficulty;
        card.CardBackgroundId = request.BackgroundId;

        db.Entry(account).Property(x => x.ContentInfo).IsModified = true;
        await db.SaveChangesAsync();

        return response;
    }

    [ProtocolHandler(Protocol.Friend_Search)]
    public async Task<FriendSearchResponse> Search(
        SchaleDataContext db,
        FriendSearchRequest request,
        FriendSearchResponse response)
    {
        var account = await _sessionService.GetAuthenticatedUser(db, request.SessionKey);

        // friend codes are derived from the server id rather than stored, so re-derive per account; there are never enough accounts here for the scan to hurt
        var code = request.FriendCode?.ToUpperInvariant();
        var match = db.Accounts.AsEnumerable().FirstOrDefault(a => a.ServerId != account.ServerId && AccountHandler.BuildFriendCode(a.ServerId) == code);

        if (match == null)
        {
            response.SearchResult = [];
            return response;
        }

        response.SearchResult = IsBlocked(account, match) ? [] : [BuildFriend(db, match)];

        return response;
    }

    [ProtocolHandler(Protocol.Friend_GetFriendDetailedInfo)]
    public async Task<FriendGetFriendDetailedInfoResponse> GetFriendDetailedInfo(
        SchaleDataContext db,
        FriendGetFriendDetailedInfoRequest request,
        FriendGetFriendDetailedInfoResponse response)
    {
        var account = await _sessionService.GetAuthenticatedUser(db, request.SessionKey);

        var target = db.GetAccount(request.FriendAccountId);
        EnsureNotBlocked(account, target, WebAPIErrorCode.FriendBlockUserCannotOpenProfile);
        var profile = BuildFriend(db, target);
        var attachment = db.GetAccountAttachments(target.ServerId).FirstOrDefault();

        response.Nickname = target.Nickname;
        response.Level = target.Level;
        response.ClanName = "Schale Network";
        response.Comment = target.Comment;
        response.FriendCount = target.ContentInfo.Friends.FriendAccountIds.Count;
        response.FriendCode = AccountHandler.BuildFriendCode(target.ServerId);
        response.RepresentCharacterUniqueId = profile.RepresentCharacterUniqueId;
        response.RepresentCharacterCostumeId = profile.RepresentCharacterCostumeId;
        response.CharacterCount = db.GetAccountCharacters(target.ServerId).Count();
        response.AttachmentDB = attachment != null ? _mapper.Map<AccountAttachmentDB>(attachment) : null;
        response.AssistCharacterDBs = [];

        response.DetailedAccountInfoDB = new DetailedAccountInfoDB
        {
            AccountId = target.ServerId,
            Nickname = target.Nickname,
            Level = target.Level,
            ClanName = "Schale Network",
            Comment = target.Comment,
            FriendCode = response.FriendCode,
            RepresentCharacterUniqueId = profile.RepresentCharacterUniqueId,
            CharacterCount = response.CharacterCount,
            AssistCharacterDBs = []
        };

        return response;
    }

    // Both accounts are saved together; no mutation returns a fabricated empty snapshot.

    [ProtocolHandler(Protocol.Friend_Remove)]
    public async Task<FriendRemoveResponse> Remove(
        SchaleDataContext db,
        FriendRemoveRequest request,
        FriendRemoveResponse response)
    {
        var account = await _sessionService.GetAuthenticatedUser(db, request.SessionKey);

        var target = GetTarget(db, account, request.TargetAccountId, WebAPIErrorCode.FriendRequestTargetIsYourself);
        if (!account.ContentInfo.Friends.FriendAccountIds.Contains(target.ServerId))
            throw new WebAPIException(WebAPIErrorCode.FriendUserIsNotFriend);
        account.ContentInfo.Friends.FriendAccountIds.Remove(target.ServerId);
        target.ContentInfo.Friends.FriendAccountIds.Remove(account.ServerId);
        await SavePair(db, account, target);
        var lists = GetLists(db, account);
        response.FriendDBs = lists.Friends;
        response.SentRequestFriendDBs = lists.Sent;
        response.ReceivedRequestFriendDBs = lists.Received;
        response.BlockedUserDBs = lists.Blocked;

        return response;
    }

    [ProtocolHandler(Protocol.Friend_AcceptFriendRequest)]
    public async Task<FriendAcceptFriendRequestResponse> AcceptFriendRequest(
        SchaleDataContext db,
        FriendAcceptFriendRequestRequest request,
        FriendAcceptFriendRequestResponse response)
    {
        var account = await _sessionService.GetAuthenticatedUser(db, request.SessionKey);

        var target = GetTarget(db, account, request.TargetAccountId, WebAPIErrorCode.FriendRequestTargetIsYourself);
        EnsureNotBlocked(account, target, WebAPIErrorCode.FriendBlockUserCannotSendRequest);
        if (!account.ContentInfo.Friends.ReceivedRequestAccountIds.Contains(target.ServerId))
            throw new WebAPIException(WebAPIErrorCode.FriendRequestNotFound);
        EnsureFriendCapacity(account, target, true);
        Befriend(account, target);
        await SavePair(db, account, target);
        var lists = GetLists(db, account);
        response.FriendDBs = lists.Friends;
        response.SentRequestFriendDBs = lists.Sent;
        response.ReceivedRequestFriendDBs = lists.Received;
        response.BlockedUserDBs = lists.Blocked;

        return response;
    }

    [ProtocolHandler(Protocol.Friend_DeclineFriendRequest)]
    public async Task<FriendDeclineFriendRequestResponse> DeclineFriendRequest(
        SchaleDataContext db,
        FriendDeclineFriendRequestRequest request,
        FriendDeclineFriendRequestResponse response)
    {
        var account = await _sessionService.GetAuthenticatedUser(db, request.SessionKey);

        var target = GetTarget(db, account, request.TargetAccountId, WebAPIErrorCode.FriendRequestTargetIsYourself);
        if (!account.ContentInfo.Friends.ReceivedRequestAccountIds.Remove(target.ServerId))
            throw new WebAPIException(WebAPIErrorCode.FriendRequestNotFound);
        target.ContentInfo.Friends.SentRequestAccountIds.Remove(account.ServerId);
        await SavePair(db, account, target);
        var lists = GetLists(db, account);
        response.FriendDBs = lists.Friends;
        response.SentRequestFriendDBs = lists.Sent;
        response.ReceivedRequestFriendDBs = lists.Received;
        response.BlockedUserDBs = lists.Blocked;

        return response;
    }

    [ProtocolHandler(Protocol.Friend_CancelFriendRequest)]
    public async Task<FriendCancelFriendRequestResponse> CancelFriendRequest(
        SchaleDataContext db,
        FriendCancelFriendRequestRequest request,
        FriendCancelFriendRequestResponse response)
    {
        var account = await _sessionService.GetAuthenticatedUser(db, request.SessionKey);

        var target = GetTarget(db, account, request.TargetAccountId, WebAPIErrorCode.FriendRequestTargetIsYourself);
        if (!account.ContentInfo.Friends.SentRequestAccountIds.Remove(target.ServerId))
            throw new WebAPIException(WebAPIErrorCode.FriendRequestNotFound);
        target.ContentInfo.Friends.ReceivedRequestAccountIds.Remove(account.ServerId);
        await SavePair(db, account, target);
        var lists = GetLists(db, account);
        response.FriendDBs = lists.Friends;
        response.SentRequestFriendDBs = lists.Sent;
        response.ReceivedRequestFriendDBs = lists.Received;
        response.BlockedUserDBs = lists.Blocked;

        return response;
    }

    [ProtocolHandler(Protocol.Friend_Block)]
    public async Task<FriendBlockResponse> Block(
        SchaleDataContext db,
        FriendBlockRequest request,
        FriendBlockResponse response)
    {
        var account = await _sessionService.GetAuthenticatedUser(db, request.SessionKey);

        var target = GetTarget(db, account, request.TargetAccountId, WebAPIErrorCode.FriendBlockTargetIsYourself);
        if (account.ContentInfo.Friends.BlockedAccountIds.Contains(target.ServerId))
            throw new WebAPIException(WebAPIErrorCode.FriendBlockTargetIsAlreadyBlocked);
        if (account.ContentInfo.Friends.BlockedAccountIds.Count >=
            _excelService.GetTable<ConstCommonExcelT>().First().MaxBlockedUserCount)
            throw new WebAPIException(WebAPIErrorCode.FriendBlockTargetIsExceedMaxCount);
        account.ContentInfo.Friends.BlockedAccountIds.Add(target.ServerId);
        account.ContentInfo.Friends.FriendAccountIds.Remove(target.ServerId);
        target.ContentInfo.Friends.FriendAccountIds.Remove(account.ServerId);
        ClearRequests(account, target);
        await SavePair(db, account, target);
        var lists = GetLists(db, account);
        response.FriendDBs = lists.Friends;
        response.SentRequestFriendDBs = lists.Sent;
        response.ReceivedRequestFriendDBs = lists.Received;
        response.BlockedUserDBs = lists.Blocked;

        return response;
    }

    [ProtocolHandler(Protocol.Friend_Unblock)]
    public async Task<FriendUnblockResponse> Unblock(
        SchaleDataContext db,
        FriendUnblockRequest request,
        FriendUnblockResponse response)
    {
        var account = await _sessionService.GetAuthenticatedUser(db, request.SessionKey);

        var target = GetTarget(db, account, request.TargetAccountId, WebAPIErrorCode.FriendBlockTargetIsYourself);
        account.ContentInfo.Friends.BlockedAccountIds.Remove(target.ServerId);
        await SavePair(db, account, target);
        var lists = GetLists(db, account);
        response.FriendDBs = lists.Friends;
        response.SentRequestFriendDBs = lists.Sent;
        response.ReceivedRequestFriendDBs = lists.Received;
        response.BlockedUserDBs = lists.Blocked;

        return response;
    }
}
