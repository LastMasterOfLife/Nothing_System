namespace NothingShell.Core.Widgets;

/// <summary>Griglia del mese con settimane che iniziano di lunedì.</summary>
public static class MonthCalendar
{
    public static readonly string[] WeekdayInitials = ["L", "M", "M", "G", "V", "S", "D"];

    /// <summary>Settimane del mese: ogni riga ha 7 celle, 0 = cella vuota.</summary>
    public static int[][] Weeks(int year, int month)
    {
        var first = new DateOnly(year, month, 1);
        var leading = ((int)first.DayOfWeek + 6) % 7; // lunedì = 0
        var days = DateTime.DaysInMonth(year, month);
        var rows = (leading + days + 6) / 7;

        var weeks = new int[rows][];
        for (var r = 0; r < rows; r++)
        {
            weeks[r] = new int[7];
            for (var c = 0; c < 7; c++)
            {
                var day = r * 7 + c - leading + 1;
                weeks[r][c] = day >= 1 && day <= days ? day : 0;
            }
        }
        return weeks;
    }
}
