using System;
using System.Collections.Generic;
using System.Data.Common;
using EVESharp.Database;
using EVESharp.Types;
using EVESharp.Types.Collections;

namespace EVESharp.Node.Services.Insurance;

public class OldInsuranceDB : DatabaseAccessor
{
    private readonly DBRowDescriptor InsuranceDescriptor;

    public OldInsuranceDB(IDatabase db) : base(db)
    {
        InsuranceDescriptor = BuildDescriptor();
    }

    // -------------------------------------------------------------------
    // Build the descriptor once — EVE requires consistent row layout
    // -------------------------------------------------------------------
    private DBRowDescriptor BuildDescriptor()
    {
        DBRowDescriptor desc = new DBRowDescriptor();
        desc.Columns.Add(new DBRowDescriptor.Column("ownerID",   FieldType.I4));      // INTEGER
        desc.Columns.Add(new DBRowDescriptor.Column("shipID",    FieldType.I4));       // INTEGER
        desc.Columns.Add(new DBRowDescriptor.Column("fraction",  FieldType.R8));     // DOUBLE
        desc.Columns.Add(new DBRowDescriptor.Column("startDate", FieldType.FileTime)); // FILETIME (INT64)
        desc.Columns.Add(new DBRowDescriptor.Column("endDate",   FieldType.FileTime));   // FILETIME (INT64)
        return desc;
    }

    // -------------------------------------------------------------------
    // Helper: Build PyPackedRow from a reader
    // -------------------------------------------------------------------
    private PyPackedRow BuildPackedRow(DbDataReader reader)
    {
        PyPackedRow row = new PyPackedRow(InsuranceDescriptor);

        row["ownerID"]   = reader.GetInt32(0);
        row["shipID"]    = reader.GetInt32(1);
        row["fraction"]  = reader.GetDouble(2);
        row["startDate"] = reader.GetInt64(3); 
        row["endDate"]   = reader.GetInt64(4);

        return row;
    }

    // -------------------------------------------------------------------
    // Helper: Build PyPackedRow list
    // -------------------------------------------------------------------
    private PyList<PyPackedRow> BuildPackedRowList(DbDataReader reader)
    {
        PyList<PyPackedRow> list = new PyList<PyPackedRow>();

        while (reader.Read())
            list.Add(BuildPackedRow(reader));

        return list;
    }

    // ===================================================================
    //                           MAIN FUNCTIONS
    // ===================================================================

    public PyPackedRow GetContractForShip(int characterID, int shipID)
    {
        DbDataReader reader = Database.Select(
            "SELECT ownerID, shipID, fraction, startDate, endDate " +
            "FROM chrShipInsurances WHERE ownerID=@char AND shipID=@ship",
            new Dictionary<string, object>
            {
                {"@char", characterID},
                {"@ship", shipID}
            }
        );

        using (reader)
        {
            if (reader.Read())
                return BuildPackedRow(reader);
        }

        // Fallback row when no insurance exists
        PyPackedRow fallback = new PyPackedRow(InsuranceDescriptor);
        fallback["ownerID"]   = characterID;
        fallback["shipID"]    = shipID;
        fallback["fraction"]  = 0.0;
        fallback["startDate"] = 0L;
        fallback["endDate"]   = 0L;

        return fallback;
    }

    public PyList<PyPackedRow> GetContractsForShipsOnStation(int characterID, int stationID)
    {
        DbDataReader reader = Database.Select(
            "SELECT ownerID, shipID, fraction, startDate, endDate " +
            "FROM chrShipInsurances WHERE ownerID=@char",
            new Dictionary<string, object>
            {
                {"@char", characterID}
            }
        );

        using (reader)
            return BuildPackedRowList(reader);
    }

    public PyList<PyPackedRow> GetContractsForShipsOnStationIncludingCorp(
        int characterID, int corpID, int stationID)
    {
        DbDataReader reader = Database.Select(
            "SELECT ownerID, shipID, fraction, startDate, endDate " +
            "FROM chrShipInsurances WHERE ownerID=@char OR ownerID=@corp",
            new Dictionary<string, object>
            {
                {"@char", characterID},
                {"@corp", corpID}
            }
        );

        using (reader)
            return BuildPackedRowList(reader);
    }

    public bool IsShipInsured(int shipID, out int ownerID, out int count)
    {
        DbDataReader reader = Database.Select(
            "SELECT ownerID FROM chrShipInsurances WHERE shipID=@ship",
            new Dictionary<string, object>
            {
                {"@ship", shipID}
            }
        );

        List<int> owners = new List<int>();

        using (reader)
        {
            while (reader.Read())
                owners.Add(reader.GetInt32(0));
        }

        count   = owners.Count;
        ownerID = count > 0 ? owners[0] : 0;

        return count > 0;
    }

    public int InsureShip(int shipID, int ownerID, double fraction, DateTime endDate)
    {
        Database.Prepare(
            "INSERT INTO chrShipInsurances (ownerID, shipID, fraction, startDate, endDate) " +
            "VALUES (@owner, @ship, @frac, @start, @end)",
            new Dictionary<string, object>
            {
                {"@owner", ownerID},
                {"@ship", shipID},
                {"@frac", fraction},
                {"@start", DateTime.UtcNow.ToFileTimeUtc()},
                {"@end", endDate.ToFileTimeUtc()}
            }
        );

        return 1;
    }

    public void UnInsureShip(int shipID)
    {
        Database.Prepare(
            "DELETE FROM chrShipInsurances WHERE shipID=@ship",
            new Dictionary<string, object>
            {
                {"@ship", shipID}
            }
        );
    }

    public List<(int OwnerID, long StartDate, string ShipName)> GetExpiredContracts()
    {
        DbDataReader reader = Database.Select(
            "SELECT ownerID, startDate, shipID FROM chrShipInsurances WHERE endDate < @now",
            new Dictionary<string, object>
            {
                {"@now", DateTime.UtcNow.ToFileTimeUtc()}
            }
        );

        List<(int, long, string)> results = new List <(int, long, string)> ();

        using (reader)
        {
            while (reader.Read())
            {
                int  owner     = reader.GetInt32(0);
                long startDate = reader.GetInt64(1);
                int  shipID    = reader.GetInt32(2);

                results.Add((owner, startDate, "Ship"));
            }
        }

        return results;
    }
}