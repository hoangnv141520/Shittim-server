namespace Schale.Data.GameModel;

// Stored with ContentInfo so existing account databases need no schema migration.
public class FriendDataInfo
{
    public HashSet<long> FriendAccountIds { get; set; } = [];
    public HashSet<long> SentRequestAccountIds { get; set; } = [];
    public HashSet<long> ReceivedRequestAccountIds { get; set; } = [];
    public HashSet<long> BlockedAccountIds { get; set; } = [];
}
