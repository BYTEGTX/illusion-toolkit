using Illusion.Formats.ResourceFormats;

namespace Illusion.Assets.Cars;

/// <summary>
/// What KIND of car the game takes a car for: its row in <c>vehicles.tbl</c> (tables.sds).
///
/// <para>
/// Two columns of that row are flags, and they are where a police car differs from its civil twin - nothing in the
/// car's own archive does. Measured on the shipped table: smith_200_p_pha 0x80001000 / 0x30000 against
/// smith_200_pha 0x80200800 / 0x7, ulver_newyorker_p the same against ulver_newyorker; the tuning data of each
/// pair differs only in the siren's sound, the PREFAB only in the beacon's light. With the police flags a car has
/// no radio and the keys that change the station switch its siren (seen in the game on a cloned hearse made an
/// ambulance: beacon bones, siren fields and the siren's sample were all in place and the radio still played -
/// until the row took the flags). Which of the two columns, and which bit, does it is not known: both are taken.
/// </para>
/// <para>
/// A cloned car has a row of its own (car_clone copies its source's). This gives a row the two flag columns of
/// another car's; id, name, title, class and price stay. It is any row the table has - a stock car's too: that
/// changes the game's own car for the single player (Mafia II Online keeps its own table for stock cars). The table of the working copy of tables.sds is written -
/// build tables.sds for the single player; a car streamed to Mafia II Online carries the row inside its own
/// archive (car_export_m2o), so export it again.
/// </para>
/// </summary>
public static class CarKind
{
    private const int IdColumn = 0;
    private const int FlagsColumn = 1;
    private const int NameColumn = 2;
    private const int ReportColumn = 8;
    private const int MoreFlagsColumn = 9;

    /// <summary>A car's row: its id, name, the two flag columns and what the police radio calls it.</summary>
    public sealed record Row(int Id, string Name, uint Flags, uint MoreFlags, string CopReport);

    /// <summary>The row of a car, by its model or archive name (the case does not matter).</summary>
    public static string? Read(string vehiclesTable, string car, out Row? row)
    {
        row = null;
        if (Open(vehiclesTable, out GameTable? table) is { } refused) return refused;
        int index = Find(table!, car);
        if (index < 0) return $"vehicles.tbl has no car named '{car}' - a car that was not cloned by the toolkit has no row of its own";
        return AsRow(table!, index, out row);
    }

    /// <summary>Gives <paramref name="car"/>'s row the two flag columns of <paramref name="like"/>'s. Nothing is
    /// written when they are the same already.</summary>
    public static string? Like(string vehiclesTable, string car, string like, out Row? before, out Row? after, out bool changed)
    {
        before = after = null;
        changed = false;
        if (Open(vehiclesTable, out GameTable? table) is { } refused) return refused;
        int index = Find(table!, car);
        if (index < 0) return $"vehicles.tbl has no car named '{car}' - a car that was not cloned by the toolkit has no row of its own";
        int source = Find(table!, like);
        if (source < 0) return $"vehicles.tbl has no car named '{like}'";
        if (AsRow(table!, index, out before) is { } unread) return unread;
        if (AsRow(table!, source, out Row? pattern) is { } unreadToo) return unreadToo;
        if (before!.Flags == pattern!.Flags && before.MoreFlags == pattern.MoreFlags)
        {
            after = before;
            return null;
        }
        table!.SetCell(index, FlagsColumn, pattern.Flags);
        table.SetCell(index, MoreFlagsColumn, pattern.MoreFlags);
        if (AsRow(table, index, out after) is { } unwritten) return unwritten;
        AtomicFile.WriteAllBytes(vehiclesTable, table.ToBytes());
        changed = true;
        return null;
    }

    private static string? Open(string path, out GameTable? table)
    {
        table = null;
        if (!File.Exists(path)) return "tables.sds has no vehicles.tbl";
        table = GameTable.Load(path);
        if (table.ColumnCount <= MoreFlagsColumn) return "vehicles.tbl is not laid out as the game's (ten columns)";
        return null;
    }

    private static int Find(GameTable table, string car)
    {
        string wanted = Path.GetFileNameWithoutExtension(car.Trim());
        for (int r = 0; r < table.RowCount; r++)
        {
            if (table.Cell(r, NameColumn) is string name && name.Equals(wanted, StringComparison.OrdinalIgnoreCase)) return r;
        }
        return -1;
    }

    private static string? AsRow(GameTable table, int index, out Row? row)
    {
        row = null;
        if (table.Cell(index, FlagsColumn) is not uint flags || table.Cell(index, MoreFlagsColumn) is not uint more
            || table.Cell(index, IdColumn) is not int id)
        {
            return "vehicles.tbl is not laid out as the game's (id, flags ... flags)";
        }
        row = new Row(id, (string)table.Cell(index, NameColumn), flags, more, table.Cell(index, ReportColumn) as string ?? "");
        return null;
    }
}
