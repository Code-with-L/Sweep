namespace Sweep;

/// <summary>
/// Inserts group header rows into the program list, so a machine with 300 programs does not
/// present them as one undifferentiated wall.
///
/// "Recently installed" uses the install date when the registry has one, falling back to the
    /// install folder's creation time. Programs with no discoverable date go to the other group
    /// rather than being guessed into the recent one.
    ///
    /// Grouping is not just decoration. Selecting a row runs that program's uninstaller, so the
    /// mapping between grid row and program index has to be exactly right; --selftest --grouping
    /// asserts it rather than trusting the arithmetic.
    /// </summary>
internal sealed class GroupIndex
{
    internal sealed record Entry(string Label, int Count);

    private readonly Dictionary<int, Entry> _groupsByRow = new();
    private readonly List<int> _groupRows = new();
    private readonly List<InstalledApp> _ordered = new();

    public bool Enabled { get; set; }

    public int TotalRows { get; private set; }

    /// <summary>Number of programs installed within the window.</summary>
    public int RecentCount { get; private set; }

    /// <summary>True when Rebuild actually reorders the list into the two sections.</summary>
    public bool OrdersApps { get; private set; }

    /// <summary>
    /// The apps in display order: every recent program first, then the rest. Stable within
    /// each section, so whatever sort the user picked (size, company, name) is preserved.
    /// </summary>
    public IReadOnlyList<InstalledApp> Ordered => _ordered;

    /// <summary>Group header rows, keyed by the grid row they occupy.</summary>
    public IReadOnlyDictionary<int, Entry> Groups => _groupsByRow;

    /// <summary>True when the row is a group header rather than a program.</summary>
    public bool IsGroupRow(int rowIndex) => _groupsByRow.ContainsKey(rowIndex);

    /// <summary>Label for a group header row, or null for a program row.</summary>
    public string? GroupLabel(int rowIndex) => _groupsByRow.TryGetValue(rowIndex, out Entry? e) ? e.Label : null;

    public void Rebuild(IReadOnlyList<InstalledApp> apps, bool recentlyInstalledFirst, int days)
    {
        _groupsByRow.Clear();
        _groupRows.Clear();
        _ordered.Clear();
        TotalRows = 0;
        RecentCount = 0;
        OrdersApps = false;

        if (!Enabled || !recentlyInstalledFirst)
        {
            TotalRows = apps.Count;
            return;
        }

        var cutoff = DateTime.Now.AddDays(-Math.Abs(days));

        // The registry date only. Resolving the folder-creation fallback here would put
        // a filesystem probe per program on the UI thread, which is exactly what made
        // the list hang before.
        int recent = 0;
        foreach (var app in apps)
        {
            DateTime? date = app.InstallDate;
            if (date is not null && date.Value >= cutoff)
            {
                recent++;
            }
        }

        RecentCount = recent;
        OrdersApps = true;

        // Stable partition: the caller's sort order survives inside each section.
        foreach (var app in apps)
        {
            DateTime? date = app.InstallDate;
            if (date is not null && date.Value >= cutoff)
            {
                _ordered.Add(app);
            }
        }

        foreach (var app in apps)
        {
            DateTime? date = app.InstallDate;
            if (date is null || date.Value < cutoff)
            {
                _ordered.Add(app);
            }
        }

        int other = _ordered.Count - recent;

        // The second header sits *below* the recent section, not beside the first one,
        // so each program appears under the heading it belongs to.
        if (recent > 0)
        {
            _groupsByRow[0] = new Entry($"New programs ({recent})", recent);
            _groupRows.Add(0);
            TotalRows = 1 + recent;
        }

        if (other > 0)
        {
            _groupsByRow[TotalRows] = new Entry($"Other programs ({other})", other);
            _groupRows.Add(TotalRows);
            TotalRows++;
        }

        // The recent apps are already accounted for by the (1 + recent) above, so only the
        // remaining programs are added here.
        TotalRows += other;
    }

    /// <summary>The group row index at or before the given row, or -1 when grouping is off.</summary>
    public int GroupRowAt(int rowIndex)
    {
        foreach (int row in _groupRows)
        {
            if (row == rowIndex)
            {
                return row;
            }
        }

        return -1;
    }

    /// <summary>
    /// Maps a grid row onto the filtered program list, skipping the group header rows.
    /// Returns -1 for a header row.
    /// </summary>
    public int DataRowFor(int rowIndex)
    {
        int skipped = 0;
        foreach (int row in _groupRows)
        {
            if (row >= rowIndex)
            {
                break;
            }

            skipped++;
        }

        int index = rowIndex - skipped;
        return _groupRows.Contains(rowIndex) ? -1 : index;
    }

    /// <summary>Maps a filtered program index back onto its grid row.</summary>
    public int GridRowFor(int dataIndex) => dataIndex + CountGroupRowsBefore(dataIndex);

    private int CountGroupRowsBefore(int dataIndex)
    {
        int count = 0;

        for (int k = 0; k < _groupRows.Count; k++)
        {
            // The first data row that follows the k-th header is at row (_groupRows[k] - k),
            // because k headers already precede it. Once that is at or before the program
            // being placed, this header has to be counted in its offset.
            int nextDataRow = _groupRows[k] - k;
            if (nextDataRow > dataIndex)
            {
                break;
            }

            count++;
        }

        return count;
    }

    public void Clear()
    {
        _groupsByRow.Clear();
        _groupRows.Clear();
        _ordered.Clear();
        TotalRows = 0;
        RecentCount = 0;
        OrdersApps = false;
    }
}