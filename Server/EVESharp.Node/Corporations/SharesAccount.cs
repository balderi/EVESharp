using EVESharp.Database;
using EVESharp.Database.Extensions;
using EVESharp.EVE.Corporations;

namespace EVESharp.Node.Corporations;

public class SharesAccount : ISharesAccount
{
    public DbLock Lock    { get; init; }
    public int    OwnerID { get; set; }

    public uint GetSharesForCorporation (int corporationID)
    {
        return Lock.Creator.CrpSharesGet (OwnerID, corporationID);
    }

    public void UpdateSharesForCorporation (int corporationID, uint newSharesCount)
    {
        Lock.Creator.CrpSharesSet (OwnerID, corporationID, newSharesCount);
    }

    public SharesAccount (int ownerID, IDatabase Database)
    {
        OwnerID = ownerID;
        Lock    = Database.GetLock (this.GenerateLockName ());
    }

    private string GenerateLockName ()
    {
        return $"shares_{OwnerID}";
    }
    
    public void Dispose ()
    {
        Lock.Dispose ();
    }
}