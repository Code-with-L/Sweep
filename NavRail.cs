namespace Sweep;

/// <summary>One selectable entry in the left navigation rail.</summary>
internal sealed record NavItem(string Key, string Label, string Group, string Description);

/// <summary>
/// The left navigation rail.
///
/// Sweep used to be a single window with every feature behind a menu, which is workable but
/// makes the tool set undiscoverable. This groups the modules the way they relate to each
/// other - things that remove programs, then things that maintain the machine - and carries
/// a short description for each so the Description Panel has something honest to show before
/// a module is even opened.
///
/// Every entry that is a stub returns false from <see cref="IsAvailable"/>, so the rail never
/// advertises a module that cannot open.
/// </summary>
internal sealed class NavRail : Panel
{
    private const int RailWidth = 210;

    private readonly Dictionary<string, Button> _buttons = new(StringComparer.Ordinal);
    private readonly TextBox _search;
    private readonly FlowLayoutPanel _list;
    private readonly Dictionary<string, NavItem> _items = new(StringComparer.Ordinal);

    public event EventHandler<string>? ModuleRequested;

    public NavRail()
    {
        Width = RailWidth;
        Dock = DockStyle.Left;
        BackColor = Theme.Current.Surface;
        Padding = new Padding(0, 8, 0, 8);

        foreach (NavItem item in AllItems().Concat(HistoryItem()))
        {
            _items[item.Key] = item;
        }

        _search = new TextBox
        {
            Dock = DockStyle.Top,
            Height = 30,
            Margin = new Padding(10, 0, 10, 8),
            PlaceholderText = "Search",
            BorderStyle = BorderStyle.FixedSingle,
        };
        Theme.StyleTextBox(_search, Theme.Current);
        _search.TextChanged += (_, _) => ApplyFilter(_search.Text);

        _list = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            AutoScroll = true,
            BackColor = Theme.Current.Surface,
            Margin = Padding.Empty,
            Padding = Padding.Empty,
        };

        // Without this the rail grows a horizontal scrollbar the moment a label is a pixel
        // wider than the buttons.
        _list.HorizontalScroll.Enabled = false;

        _list.Controls.Add(new Label
        {
            Text = "Uninstaller",
            AutoSize = false,
            Height = 26,
            Width = RailWidth - 4,
            TextAlign = ContentAlignment.MiddleLeft,
            ForeColor = Theme.Current.TextDim,
            // See MakeLabel in MainForm: AutoSize measures short of what TextRenderer draws.
            Padding = new Padding(14, 0, 4, 0),
            Font = new Font(Font, FontStyle.Bold),
        });

        foreach (NavItem item in AllItems().Where(i => i.Group == "Uninstaller"))
        {
            _list.Controls.Add(MakeButton(item));
        }

        _list.Controls.Add(new Label
        {
            Text = "Tools",
            AutoSize = false,
            Height = 30,
            Width = RailWidth - 4,
            TextAlign = ContentAlignment.MiddleLeft,
            ForeColor = Theme.Current.TextDim,
            Padding = new Padding(14, 0, 4, 0),
            Font = new Font(Font, FontStyle.Bold),
        });

        foreach (NavItem item in AllItems().Concat(HistoryItem()).Where(i => i.Group == "Tools"))
        {
            _list.Controls.Add(MakeButton(item));
        }

        // Added in this order so the search box takes the top strip and the list fills the rest:
        // WinForms docks the most recently added control first.
        Controls.Add(_list);
        Controls.Add(_search);

        // Once the list grows a vertical scrollbar its client area is narrower than the rail,
        // and fixed-width buttons then overflow sideways and raise a horizontal scrollbar.
        // Re-fitting on every resize keeps the buttons exactly as wide as the space available.
        _list.SizeChanged += (_, _) => FitButtons();
        FitButtons();
    }

    private void FitButtons()
    {
        int available = _list.ClientSize.Width - 6;
        if (available <= 0)
        {
            return;
        }

        foreach (Button button in _buttons.Values)
        {
            button.Width = available;
        }

        foreach (Control child in _list.Controls)
        {
            if (child is Label heading)
            {
                heading.Width = available;
            }
        }
    }

    private Button MakeButton(NavItem item)
    {
        var button = new Button
        {
            Text = "   " + item.Label,
            Width = RailWidth - 4,
            Height = 30,
            FlatStyle = FlatStyle.Flat,
            TextAlign = ContentAlignment.MiddleLeft,
            Margin = Padding.Empty,
            Tag = item.Key,
            BackColor = Theme.Current.Surface,
            ForeColor = Theme.Current.Text,
            Cursor = Cursors.Hand,
        };

        button.FlatAppearance.BorderSize = 0;
        button.FlatAppearance.MouseOverBackColor = Theme.Current.SurfaceAlt;
        button.Click += (_, _) => ModuleRequested?.Invoke(this, item.Key);

        _buttons[item.Key] = button;
        return button;
    }

    /// <summary>Filters the rail by label, matching anywhere in the name.</summary>
    private void ApplyFilter(string query)
    {
        foreach (NavItem item in _items.Values)
        {
            if (_buttons.TryGetValue(item.Key, out Button? button))
            {
                button.Visible = string.IsNullOrWhiteSpace(query)
                    || item.Label.Contains(query.Trim(), StringComparison.CurrentCultureIgnoreCase);
            }
        }
    }

    public void Select(string key)
    {
        foreach ((string k, Button button) in _buttons)
        {
            bool active = string.Equals(k, key, StringComparison.Ordinal);
            button.BackColor = active ? Theme.Current.Selection : Theme.Current.Surface;
            button.ForeColor = active ? Theme.Current.Accent : Theme.Current.Text;
        }
    }

    public string? DescriptionFor(string key) =>
        _items.TryGetValue(key, out NavItem? item) ? item.Description : null;

    public void Restyle()
    {
        BackColor = Theme.Current.Surface;
        _list.BackColor = Theme.Current.Surface;
        Theme.StyleTextBox(_search, Theme.Current);

        foreach (Button button in _buttons.Values)
        {
            button.BackColor = Theme.Current.Surface;
            button.ForeColor = Theme.Current.Text;
            button.FlatAppearance.MouseOverBackColor = Theme.Current.SurfaceAlt;
        }
    }

    /// <summary>
    /// The full module list. Ordered the way the rail reads, not alphabetically.
    /// </summary>
    internal static IEnumerable<NavItem> AllItems() =>
    [
        new("programs", "All Programs", "Uninstaller",
            "All installed programs and components, for the current user and for every user on the machine, grouped into recently installed and the rest."),
        new("traced", "Traced Programs", "Uninstaller",
            "Programs recorded by the installation monitor, with the files and registry entries they added. Lets you remove something whose uninstaller never registered."),
        new("apps", "Windows Apps", "Uninstaller",
            "Store and packaged apps, including provisioning. Some can be reset to their shipped state instead of removed outright."),
        new("extensions", "Browser Extensions", "Uninstaller",
            "Add-ons installed into Chrome, Edge and Firefox profiles. These uninstall from the browser itself rather than through Add or Remove Programs."),
        new("hunter", "Hunter Mode", "Uninstaller",
            "Shows every running process with its executable, parent and open handles, so you can see what a stubborn program is actually doing before killing it."),
        new("autorun", "Autorun Manager", "Tools",
            "Startup programs, services and scheduled tasks. Disable, delete or add entries and keep a record of what you changed."),
        new("junk", "Junk Files Cleaner", "Tools",
            "Temporary files, caches, old logs, thumbnails and crash dumps across all drives, with a running total of what is recoverable."),
        new("wintools", "Windows Tools", "Tools",
            "The built-in Windows utilities, grouped by what they are for, each with its command line shown so you can reuse it. Add your own."),
        new("history", "History Cleaner", "Tools",
            "Clears browsing history, cookies, caches, form and download history per browser, plus Windows recent items and Office document history."),
        new("backup", "Backup Manager", "Tools",
            "Registry and file backups taken before removals, with the ability to put any of them back exactly as they were."),
    ];

    /// <summary>
    /// Modules that are not part of the rail's two groups but still open from it.
    ///
    /// History belongs with the Uninstaller group conceptually but is a record of what it did
    /// rather than a list of programs, so it sits under Tools in reading order.
    /// </summary>
    internal static IEnumerable<NavItem> HistoryItem() =>
    [
        new("uninstallhistory", "Uninstall History", "Tools",
            "Every removal this copy of Sweep has performed, grouped by date, listing what was removed and what could not be. Exportable as text."),
    ];
}