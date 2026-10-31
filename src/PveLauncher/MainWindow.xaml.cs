using System.Collections.ObjectModel;
using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Pve.Api;
using Windows.Graphics;

namespace PveLauncher;

/// <summary>
/// The launcher window (docs/decisions/0038): saved servers on the left, the selected server's guests on the right.
/// Everything that talks to Proxmox, Credential Manager or another process is in Pve.Api; this class only shows it.
/// The list is fetched on sign-in and on Refresh, and by itself every few seconds only while the window is shown
/// (<see cref="RefreshScheduler"/>; energy rules).
/// </summary>
public sealed partial class MainWindow : Window, ISignInPrompt
{
    readonly string _home;
    readonly LauncherLocation _location;
    readonly LauncherSettings _settings;
    readonly CredentialStore _store = new();
    readonly Dictionary<string, PveClient> _clients = [];
    readonly DispatcherQueue _ui;
    IReadOnlyList<PveVm> _guests = [];
    bool _busy, _loading, _closed;
    readonly RefreshScheduler _autoRefresh;
    /// <summary>The last automatic refresh's error, shown once until the server answers again.</summary>
    string? _autoRefreshError;
    /// <summary>Counts list requests, manual and automatic; only the latest one started may update the list, so an
    /// older answer that arrives late never overwrites a newer one (review R3).</summary>
    int _listGeneration;
    const string AutoRefreshFailed = "Could not refresh the guests";

    /// <summary>Whether the account may start a guest (<c>VM.PowerMgmt</c> on <c>/vms/VMID</c>), by server and VMID,
    /// as the server answered; a guest missing here is unknown, and Start is offered.</summary>
    readonly Dictionary<(string Server, int Vmid), bool> _canStart = [];
    readonly HashSet<(string Server, int Vmid)> _askingCanStart = [];
    /// <summary>Guests whose start task is being followed, so Start isn't offered twice.</summary>
    readonly HashSet<(string Server, int Vmid)> _starting = [];
    /// <summary>How long a start task is followed (qm start waits up to max(30 s, 1 s per GiB) for QEMU).</summary>
    static readonly TimeSpan StartTaskLimit = TimeSpan.FromMinutes(2);
    static readonly TimeSpan StartTaskPoll = TimeSpan.FromSeconds(1);

    /// <summary>The terminal selector's items, in order (MainWindow.xaml).</summary>
    static readonly TerminalHost[] TerminalChoices =
        [TerminalHost.Auto, TerminalHost.WindowsTerminal, TerminalHost.WindowsTerminalPreview, TerminalHost.Mintty, TerminalHost.Default,
         TerminalHost.Conhost, TerminalHost.Custom];

    /// <summary>The VNC viewer selector's items, in order (MainWindow.xaml, docs/decisions/0043).</summary>
    static readonly Pve.Term.VncViewerKind[] VncChoices =
        [Pve.Term.VncViewerKind.Auto, Pve.Term.VncViewerKind.TigerVnc, Pve.Term.VncViewerKind.UltraVnc, Pve.Term.VncViewerKind.TightVnc,
         Pve.Term.VncViewerKind.RealVnc, Pve.Term.VncViewerKind.None, Pve.Term.VncViewerKind.Custom];

    public ObservableCollection<ServerItem> Servers { get; } = [];
    public ObservableCollection<GuestItem> Guests { get; } = [];

    public MainWindow()
    {
        InitializeComponent();
        _ui = DispatcherQueue.GetForCurrentThread();
        SystemBackdrop = new MicaBackdrop();
        ExtendsContentIntoTitleBar = false;
        // The system caption follows Windows' app mode (dark or light), as the content does; by default it stays light
        // (smoke test B2).
        AppWindow.TitleBar.PreferredTheme = TitleBarTheme.UseDefaultAppMode;

        _home = LauncherPaths.FindHome(AppContext.BaseDirectory, File.Exists);
        _location = LauncherPaths.Resolve(_home);
        LauncherLog.Open(_location.LogDir);
        LauncherLog.Write($"start: home {_home}, settings in {_location.SettingsDir}{(_location.FellBack ? " (the portable folder can't be written)" : "")}");
        _settings = LauncherSettings.Load(_location.SettingsDir);
        if (_settings.LoadProblem is { } problem) LauncherLog.Write("settings: " + problem);
        _autoRefresh = new RefreshScheduler(TimeSpan.FromSeconds(_settings.RefreshSeconds), AutoRefreshAsync, a => _ui.TryEnqueue(() => a()))
        {
            Error = e => ShowUnexpected("Could not refresh the guests", e),
        };
        LauncherLog.Write(_autoRefresh.Interval > TimeSpan.Zero
            ? $"refresh: every {_autoRefresh.Interval.TotalSeconds:0} s while the window is shown"
            : "refresh: automatic refresh is off (refreshSeconds 0)");

        _loading = true;
        ShowTerminalDetection(WindowsTerminalLocator.Scan());
        TerminalChoice.SelectedIndex = Math.Max(0, Array.IndexOf(TerminalChoices, _settings.Terminal));
        ShowVncDetection();
        VncChoice.SelectedIndex = Math.Max(0, Array.IndexOf(VncChoices, _settings.VncViewer));
        foreach (var s in _settings.Servers) Servers.Add(new ServerItem(s));
        _loading = false;
        SizeToDpi(1120, 700);
        // Whether the window can be seen: shown and not minimized. Any of these may be the one that reports a change.
        AppWindow.Changed += (_, _) => UpdateVisible();
        Activated += (_, _) => UpdateVisible();
        VisibilityChanged += (_, _) => UpdateVisible();
        Closed += (_, _) =>
        {
            _closed = true;
            _autoRefresh.Dispose();
            foreach (var c in _clients.Values) c.Dispose();
            LauncherLog.Write("exit");
        };
        // Dialogs need the XamlRoot, which exists once the content is loaded; selecting a server may sign in.
        Root.Loaded += (_, _) =>
        {
            if (Servers.FirstOrDefault(s => s.Config.Id == _settings.LastServerId) is { } last) ServerList.SelectedItem = last;
            else if (Servers.Count == 0) ShowInfo("Add a Proxmox VE server to begin.", InfoBarSeverity.Informational);
        };
    }

    [LibraryImport("user32.dll")]
    private static partial uint GetDpiForWindow(nint hwnd);

    /// <summary>The automatic refresh runs only while the window is shown and not minimized (energy rules).</summary>
    void UpdateVisible()
    {
        if (_closed) return;
        _autoRefresh.Visible = AppWindow.IsVisible && AppWindow.Presenter is not OverlappedPresenter { State: OverlappedPresenterState.Minimized };
    }

    void SizeToDpi(int width, int height)
    {
        uint dpi = GetDpiForWindow(WinRT.Interop.WindowNative.GetWindowHandle(this));
        double scale = dpi == 0 ? 1 : dpi / 96.0;
        AppWindow.Resize(new SizeInt32((int)(width * scale), (int)(height * scale)));
    }

    ServerItem? SelectedServer => ServerList.SelectedItem as ServerItem;
    GuestItem? SelectedGuest => VmList.SelectedItem as GuestItem;

    // ---- Status line ----------------------------------------------------------------------------------------------

    void ShowInfo(string message, InfoBarSeverity severity = InfoBarSeverity.Success)
    {
        Notice.Severity = severity;
        Notice.Title = "";
        Notice.Message = message;
        Notice.IsOpen = true;
    }

    void ShowError(string what, string message)
    {
        LauncherLog.Write($"error: {what}: {message}");
        Notice.Severity = InfoBarSeverity.Error;
        Notice.Title = what;
        Notice.Message = message;
        Notice.IsOpen = true;
    }

    bool _reporting;

    /// <summary>
    /// A failure the code didn't expect (a bug): shown and logged with its stack trace, never dropped silently (smoke
    /// test B1). Exception messages carry no secrets (see <see cref="App.Describe"/>). It never throws and never
    /// re-enters itself, so a failing status bar or log can't start a cascade of reports (review R2); the log append is
    /// best effort and can't stop the message from showing. A fatal failure ends the process instead (review R3).
    /// </summary>
    public void ShowUnexpected(string what, Exception e)
    {
        if (App.IsFatal(e)) App.Fatal(what, e);
        if (_reporting) { LauncherLog.Write($"unexpected while reporting: {what}: {App.Describe(e)}"); return; }
        _reporting = true;
        try
        {
            Notice.Severity = InfoBarSeverity.Error;
            Notice.Title = what;
            Notice.Message = $"The launcher hit an unexpected error ({e.GetType().Name}: {e.Message}). launcher.log has the details.";
            Notice.IsOpen = true;
        }
        catch (Exception x) when (!App.IsFatal(x))
        {
            LauncherLog.Write($"unexpected: the status bar could not show the next error: {App.Describe(x)}");
        }
        finally { _reporting = false; }
        LauncherLog.Write($"unexpected: {what}: {App.Describe(e)}");
    }

    /// <summary>Runs an event handler's work. Exceptions from <c>async void</c> handlers would otherwise leave no trace
    /// (smoke test B1), so whatever escapes is shown and logged. Fatal ones go on to the application's handler, which
    /// ends the process (review R3).</summary>
    async void Guard(string what, Func<Task> work)
    {
        try { await work(); }
        catch (Exception e) when (!App.IsFatal(e)) { ShowUnexpected(what, e); }
    }

    void SetHeading(string name, string? detail = null)
    {
        HeadingName.Text = name;
        HeadingDetail.Text = detail ?? "";
        // One line, trimmed to fit (smoke test B4); the whole text is in the tooltip.
        ToolTipService.SetToolTip(Heading, detail is null ? null : $"{name} · {detail}");
    }

    void UpdateButtons()
    {
        var server = SelectedServer;
        var guest = SelectedGuest;
        bool signedIn = server is not null && _clients.TryGetValue(server.Config.Id, out var c) && c.IsSignedIn;
        AddButton.IsEnabled = !_busy;
        ServerList.IsEnabled = !_busy; // one server's answer must not land in another's list
        EditButton.IsEnabled = RemoveButton.IsEnabled = ForgetButton.IsEnabled = server is not null && !_busy;
        SignOutButton.IsEnabled = signedIn && !_busy;
        RefreshButton.IsEnabled = server is not null && !_busy;
        ConsoleButton.IsEnabled = guest is not null && !guest.Vm.IsTemplate && !_busy;
        TerminalButton.IsEnabled = guest is not null && !guest.Vm.IsTemplate && !_busy;
        VncButton.IsEnabled = guest is not null && !guest.Vm.IsTemplate && guest.Vm.Type == PveGuestType.Qemu && !_busy;
        ConsoleButton.Content = guest?.Vm.Type == PveGuestType.Lxc ? "Open terminal" : "Open console";
        StartButton.Visibility = guest is not null && OffersStart(guest) ? Visibility.Visible : Visibility.Collapsed;
        StartButton.IsEnabled = !_busy;
    }

    /// <summary>Start is offered for a stopped guest that isn't a template, isn't already being started, and that the
    /// account may start, as far as the server has said.</summary>
    bool OffersStart(GuestItem g)
    {
        if (g.Vm.IsTemplate || g.Vm.Status != "stopped" || SelectedServer is not { } server) return false;
        var key = (server.Config.Id, g.Vm.Vmid);
        return !_starting.Contains(key) && !(_canStart.TryGetValue(key, out bool may) && !may);
    }

    // ---- Running an API action ------------------------------------------------------------------------------------

    PveClient ClientFor(PveServerConfig server)
    {
        if (_clients.TryGetValue(server.Id, out var c)) return c;
        c = new PveClient(PveClient.NormalizeApiUrl(server.Url), server.CaPem) { Log = LauncherLog.Write };
        _clients[server.Id] = c;
        return c;
    }

    void DropClient(string serverId)
    {
        if (_clients.Remove(serverId, out var c)) c.Dispose();
        // The next sign-in may be another account.
        foreach (var key in _canStart.Keys.Where(k => k.Server == serverId).ToArray()) _canStart.Remove(key);
    }

    /// <summary>Runs <paramref name="action"/> signed in: signs in first if needed, and once more if the session
    /// turned out to be expired or refused. Errors go to the status line.</summary>
    async Task<bool> RunAsync(string what, Func<PveClient, Task> action)
    {
        if (_busy || SelectedServer is not { } item) return false;
        var server = item.Config;
        _busy = true;
        UpdateButtons();
        try
        {
            var client = ClientFor(server);
            if (!client.IsSignedIn && !await SignInAsync(client, server)) return false;
            try { await action(client); }
            catch (PveAuthException e)
            {
                LauncherLog.Write($"auth: {e.Message}; signing in again");
                if (!await SignInAsync(client, server)) return false;
                await action(client);
            }
            return true;
        }
        catch (PveApiException e) { ShowError(what, e.Message); }
        catch (CredentialStoreException e) { ShowError(what, "Credential Manager: " + e.Message); }
        catch (Exception e) when (!App.IsFatal(e)) { ShowUnexpected(what, e); }
        finally
        {
            _busy = false;
            UpdateButtons();
        }
        return false;
    }

    async Task<bool> SignInAsync(PveClient client, PveServerConfig server)
    {
        bool ok = await PveSignIn.SignInAsync(client, server, _store, this);
        if (!ok) ShowInfo($"Not signed in to {server.DisplayName}.", InfoBarSeverity.Informational);
        else LauncherLog.Write($"signed in to {client.ApiUrl.Authority} as {client.SignedInAs}");
        return ok;
    }

    async Task LoadGuestsAsync(PveClient client, PveServerConfig server, bool announce = true)
    {
        // A token's sign-in already fetched the version (smoke test B6); a password sign-in didn't.
        string version = client.ServerVersion ?? await client.GetVersionAsync();
        int generation = ++_listGeneration;
        var guests = await client.ListVmsAsync();
        if (SelectedServer?.Config.Id != server.Id || generation != _listGeneration) return;
        _guests = guests;
        _autoRefreshError = null;
        SetHeading(server.DisplayName, $"PVE {version} · {client.SignedInAs}");
        ApplyFilter();
        if (announce) ShowInfo($"{_guests.Count} guests.", InfoBarSeverity.Informational);
        _autoRefresh.Armed = true;
        _autoRefresh.Fetched();
    }

    /// <summary>
    /// The automatic refresh: one <c>GET /cluster/resources?type=vm</c>, without the log lines of a manual one. It
    /// never asks for a password: an ended session stops it until the next sign-in, and an error is shown once until
    /// the server answers again. The list is touched only when something changed.
    /// </summary>
    async Task AutoRefreshAsync()
    {
        if (_busy || SelectedServer is not { } item || !_clients.TryGetValue(item.Config.Id, out var c) || !c.IsSignedIn) return;
        IReadOnlyList<PveVm> guests;
        int generation = ++_listGeneration;
        try { guests = await c.ListVmsAsync(log: false); }
        catch (PveAuthException e)
        {
            _autoRefresh.Armed = false;
            LauncherLog.Write($"refresh: {e.Message}; the automatic refresh waits for the next sign-in");
            if (SelectedServer?.Config.Id == item.Config.Id)
                ShowInfo("The session ended. Refresh (F5) signs in again.", InfoBarSeverity.Warning);
            return;
        }
        catch (PveApiException e)
        {
            if (e.Message == _autoRefreshError) return;
            _autoRefreshError = e.Message;
            ShowError(AutoRefreshFailed, e.Message);
            return;
        }
        catch (ObjectDisposedException) { return; } // signed out meanwhile
        if (SelectedServer?.Config.Id != item.Config.Id || _clients.GetValueOrDefault(item.Config.Id) != c) return;
        // A manual Refresh started while this one ran answers for a later moment; this older answer is dropped
        // (review R3).
        if (generation != _listGeneration) return;
        if (_autoRefreshError is not null)
        {
            _autoRefreshError = null;
            LauncherLog.Write("refresh: the server answers again");
            if (Notice.Title == AutoRefreshFailed) Notice.IsOpen = false;
        }
        if (guests.SequenceEqual(_guests)) return;
        _guests = guests;
        ApplyFilter();
    }

    /// <summary>
    /// Brings the shown rows in line with <see cref="_guests"/> and the filter, in place: unchanged rows stay (with
    /// their selection and the scroll position), changed ones are replaced, others inserted or removed. Both lists are
    /// sorted by VMID.
    /// </summary>
    void ApplyFilter()
    {
        string filter = Filter.Text.Trim();
        var selected = SelectedGuest?.Vm.Vmid;
        int i = 0;
        foreach (var vm in _guests)
        {
            var g = new GuestItem(vm);
            if (!g.Matches(filter)) continue;
            while (i < Guests.Count && Guests[i].Vm.Vmid < vm.Vmid) Guests.RemoveAt(i);
            if (i < Guests.Count && Guests[i].Vm.Vmid == vm.Vmid)
            {
                if (Guests[i].Vm != vm) Guests[i] = g;
            }
            else Guests.Insert(i, g);
            i++;
        }
        while (Guests.Count > i) Guests.RemoveAt(Guests.Count - 1);
        if (selected is int vmid && SelectedGuest?.Vm.Vmid != vmid && Guests.FirstOrDefault(g => g.Vm.Vmid == vmid) is { } again)
            VmList.SelectedItem = again;
        UpdateButtons();
    }

    // ---- Servers --------------------------------------------------------------------------------------------------

    // Every async event handler goes through Guard, so an exception in it is shown and logged (smoke test B1).

    void OnServerSelected(object sender, SelectionChangedEventArgs e) => Guard("Could not list the guests", ServerSelectedAsync);

    async Task ServerSelectedAsync()
    {
        _autoRefresh.Armed = false;
        Guests.Clear();
        _guests = [];
        SetHeading(SelectedServer?.Name ?? "Guests");
        UpdateButtons();
        if (SelectedServer is not { } item || _loading) return;
        if (_settings.LastServerId != item.Config.Id)
        {
            _settings.LastServerId = item.Config.Id;
            SaveSettings();
        }
        await RunAsync("Could not list the guests", c => LoadGuestsAsync(c, item.Config));
    }

    void OnServerDoubleTapped(object sender, DoubleTappedRoutedEventArgs e) => Guard("Could not edit the server", EditSelectedServerAsync);

    void OnRefresh(object sender, RoutedEventArgs e) => Guard("Could not list the guests", async () =>
    {
        if (SelectedServer is { } item) await RunAsync("Could not list the guests", c => LoadGuestsAsync(c, item.Config));
    });

    void OnAddServer(object sender, RoutedEventArgs e) => Guard("Could not add the server", AddServerAsync);

    async Task AddServerAsync()
    {
        if (_busy) return;
        var server = await Dialogs.EditServerAsync(Root.XamlRoot, null);
        if (server is null) return;
        _settings.Servers.Add(server);
        SaveSettings();
        Notice.IsOpen = false; // "Add a Proxmox VE server to begin." is done
        var item = new ServerItem(server);
        Servers.Add(item);
        ServerList.SelectedItem = item;
    }

    void OnEditServer(object sender, RoutedEventArgs e) => Guard("Could not edit the server", EditSelectedServerAsync);

    async Task EditSelectedServerAsync()
    {
        if (_busy || SelectedServer is not { } item) return;
        var old = item.Config;
        var edited = await Dialogs.EditServerAsync(Root.XamlRoot, old);
        if (edited is null) return;
        // A changed address or account makes the saved secret's target name stale: move it along.
        // A different sign-in kind makes the old secret useless (its target differs by kind, so nothing aliases).
        string oldTarget = _store.TargetFor(old), newTarget = _store.TargetFor(edited);
        try
        {
            if (edited.Auth == old.Auth) _store.Move(oldTarget, newTarget, edited.Account);
            else _store.Delete(oldTarget);
        }
        catch (CredentialStoreException ex) { ShowError("Could not move the saved credential", ex.Message); }
        int i = _settings.Servers.FindIndex(s => s.Id == old.Id);
        if (i >= 0) _settings.Servers[i] = edited;
        SaveSettings();
        DropClient(old.Id);
        int pos = Servers.IndexOf(item);
        var replacement = new ServerItem(edited);
        Servers[pos] = replacement;
        ServerList.SelectedItem = replacement;
    }

    void OnRemoveServer(object sender, RoutedEventArgs e) => Guard("Could not remove the server", RemoveServerAsync);

    async Task RemoveServerAsync()
    {
        if (_busy || SelectedServer is not { } item) return;
        bool hasSecret = TryReadSecret(_store.TargetFor(item.Config)) is not null;
        if (!await Dialogs.ConfirmAsync(Root.XamlRoot, $"Remove {item.Name}?",
                hasSecret ? "The server and its saved credential in Credential Manager are deleted." : "The server is removed from the list.",
                "Remove"))
            return;
        if (hasSecret) TryDeleteSecret(_store.TargetFor(item.Config));
        DropClient(item.Config.Id);
        _settings.Servers.RemoveAll(s => s.Id == item.Config.Id);
        if (_settings.LastServerId == item.Config.Id) _settings.LastServerId = null;
        SaveSettings();
        Servers.Remove(item);
        ShowInfo($"Removed {item.Name}.", InfoBarSeverity.Informational);
    }

    void OnSignOut(object sender, RoutedEventArgs e)
    {
        if (_busy || SelectedServer is not { } item) return;
        DropClient(item.Config.Id);
        _autoRefresh.Armed = false;
        Guests.Clear();
        _guests = [];
        SetHeading(item.Name);
        LauncherLog.Write($"signed out of {item.Name}");
        ShowInfo($"Signed out of {item.Name}. Refresh signs in again.", InfoBarSeverity.Informational);
        UpdateButtons();
    }

    void OnForget(object sender, RoutedEventArgs e) => Guard("Could not forget the saved credentials", ForgetAsync);

    async Task ForgetAsync()
    {
        if (_busy || SelectedServer is not { } item) return;
        string target = _store.TargetFor(item.Config);
        bool has = TryReadSecret(target) is not null;
        int all;
        try { all = _store.List().Count; }
        catch (CredentialStoreException ex) { ShowError("Credential Manager", ex.Message); return; }
        string kind = item.Config.Auth == PveAuthKind.Token ? "token secret" : "password";
        var choice = await Dialogs.ForgetAsync(Root.XamlRoot,
            has ? $"Delete the saved {kind} for {item.Config.Account} on {item.Name} from Windows Credential Manager?"
                : $"No {kind} is saved for {item.Config.Account} on {item.Name}.",
            has, all);
        try
        {
            if (choice == ContentDialogResult.Primary && has)
            {
                _store.Delete(target);
                ShowInfo($"Deleted the saved {kind} for {item.Name}.", InfoBarSeverity.Informational);
            }
            else if (choice == ContentDialogResult.Secondary)
            {
                int n = _store.DeleteAll();
                ShowInfo($"Deleted {n} saved credential{(n == 1 ? "" : "s")}.", InfoBarSeverity.Informational);
            }
            else return;
            LauncherLog.Write("credentials: deleted saved secrets on request");
        }
        catch (CredentialStoreException ex) { ShowError("Credential Manager", ex.Message); }
    }

    string? TryReadSecret(string target)
    {
        try { return _store.Read(target); }
        catch (CredentialStoreException) { return null; }
    }

    void TryDeleteSecret(string target)
    {
        try { _store.Delete(target); }
        catch (CredentialStoreException ex) { ShowError("Could not delete the saved credential", ex.Message); }
    }

    void SaveSettings()
    {
        try { _settings.Save(_location.SettingsDir); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            ShowError("Could not save the server list", e.Message);
        }
    }

    // ---- Guests ---------------------------------------------------------------------------------------------------

    void OnFilterChanged(object sender, TextChangedEventArgs e) => ApplyFilter();

    void OnGuestSelected(object sender, SelectionChangedEventArgs e)
    {
        UpdateButtons();
        if (SelectedGuest is { Vm: { IsTemplate: false, Status: "stopped" } } g) Guard("Could not check the Start privilege", () => LookUpStartPrivilegeAsync(g));
    }

    /// <summary>
    /// Asks the server once per guest and session whether the account may start it (<c>GET /access/permissions</c>
    /// for <c>/vms/VMID</c>), when a stopped guest is selected: a click, not a timer. Start stays offered until the
    /// answer says no; an answer that can't be had leaves it offered, and the server decides when it is clicked.
    /// </summary>
    async Task LookUpStartPrivilegeAsync(GuestItem g)
    {
        if (SelectedServer is not { } item || !_clients.TryGetValue(item.Config.Id, out var c) || !c.IsSignedIn) return;
        var key = (item.Config.Id, g.Vm.Vmid);
        if (_canStart.ContainsKey(key) || !_askingCanStart.Add(key)) return;
        try
        {
            bool? may;
            try { may = await c.HasPrivilegeAsync($"/vms/{g.Vm.Vmid}", "VM.PowerMgmt"); }
            catch (PveApiException) { may = null; } // an ended session: unknown until the next sign-in
            catch (ObjectDisposedException) { return; } // signed out meanwhile
            if (may is not bool known || _clients.GetValueOrDefault(item.Config.Id) != c) return;
            _canStart[key] = known;
            if (!known) LauncherLog.Write($"start: {c.SignedInAs} lacks VM.PowerMgmt on /vms/{g.Vm.Vmid}; Start is not offered");
        }
        finally { _askingCanStart.Remove(key); }
        UpdateButtons();
    }

    void OnStart(object sender, RoutedEventArgs e) => Guard("Could not start the guest", async () =>
    {
        if (SelectedGuest is { } g) await StartGuestAsync(g);
    });

    /// <summary>
    /// Starts a stopped guest (<c>POST .../status/start</c>) and follows its task until it ends, at most
    /// <see cref="StartTaskLimit"/>: a failed start shows the task's error. The window stays usable meanwhile.
    /// </summary>
    async Task StartGuestAsync(GuestItem g)
    {
        if (SelectedServer is not { } item || !OffersStart(g)) return;
        var vm = g.Vm;
        string label = g.Label;
        var key = (item.Config.Id, vm.Vmid);
        string? upid = null;
        PveClient? client = null;
        if (!await RunAsync($"Could not start {label}", async c =>
        {
            upid = await c.StartGuestAsync(vm);
            client = c;
        })) return;
        LauncherLog.Write($"start: {label} on {vm.Node}: {client!.Scrub(upid!)}");
        ShowInfo($"Starting {label}…", InfoBarSeverity.Informational);
        _starting.Add(key);
        UpdateButtons();
        PveTask? task = null;
        try { task = await client!.WaitForTaskAsync(vm.Node, upid!, StartTaskPoll, StartTaskLimit); }
        catch (PveApiException ex) { LauncherLog.Write($"start: {label}: the task's status can't be read ({ex.Message})"); }
        catch (ObjectDisposedException) { } // signed out meanwhile
        finally
        {
            _starting.Remove(key);
            UpdateButtons();
        }
        if (task is { Failed: true })
        {
            ShowError($"Could not start {label}", task.Error);
            // The list is fetched again either way: a failed start may still have changed the guest (review R5).
            if (SelectedServer?.Config.Id == item.Config.Id) _autoRefresh.FollowUp(() => true);
            return;
        }
        LauncherLog.Write($"start: {label}: {(task is null ? "task status unknown" : task.Running ? "still running" : task.ExitStatus)}");
        if (SelectedServer?.Config.Id != item.Config.Id) return;
        ShowInfo(task is { Running: false } ? $"Started {label}." : $"{label} is starting.", InfoBarSeverity.Success);
        // Refreshes now, then every 2 s until the guest is no longer listed as stopped, for at most 30 s: an
        // HA-managed guest's task only asks the HA manager, which starts it a little later.
        _autoRefresh.FollowUp(() => _guests.FirstOrDefault(v => v.Vmid == vm.Vmid) is not { Status: "stopped" });
    }

    void OnGuestDoubleTapped(object sender, DoubleTappedRoutedEventArgs e) => Guard("Could not open the console", OpenPrimaryAsync);

    void OnGuestKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != Windows.System.VirtualKey.Enter) return;
        e.Handled = true;
        Guard("Could not open the console", OpenPrimaryAsync);
    }

    /// <summary>
    /// A right-click (or long press) on a row selects it before the list's menu opens. The handler sits on the row's
    /// template, not on the list: a handler on the list never ran (presumably its ContextFlyout takes the event
    /// first), and the menu opened for no row (found in the smoke test re-check). The row is read through
    /// DependencyObject.GetValue, which needs no projected type beyond what the sender is declared as (0038, AOT).
    /// The keyboard's menu key opens on the focused row, which a single-selection list has already selected.
    /// </summary>
    void OnGuestRowContextRequested(UIElement sender, ContextRequestedEventArgs e)
    {
        if (sender.GetValue(FrameworkElement.DataContextProperty) is GuestItem guest) VmList.SelectedItem = guest;
    }

    void OnGuestMenuOpening(object sender, object e)
    {
        var guest = SelectedGuest;
        // A container has no SPICE or VNC display; its terminal is the console.
        var vmOnly = guest?.Vm.Type == PveGuestType.Lxc ? Visibility.Collapsed : Visibility.Visible;
        MenuConsole.Visibility = MenuSpice.Visibility = MenuVnc.Visibility = vmOnly;
        MenuConsole.IsEnabled = MenuSpice.IsEnabled = MenuVnc.IsEnabled = MenuTerminal.IsEnabled = guest is not null && !guest.Vm.IsTemplate && !_busy;
        MenuStart.Visibility = guest is not null && OffersStart(guest) ? Visibility.Visible : Visibility.Collapsed;
        MenuStart.IsEnabled = !_busy;
    }

    void OnOpenConsole(object sender, RoutedEventArgs e) => Guard("Could not open the console", OpenPrimaryAsync);

    void OnOpenSpice(object sender, RoutedEventArgs e) => Guard("Could not open the SPICE console", async () =>
    {
        if (SelectedGuest is { Vm.IsTemplate: false } g) await OpenSpiceAsync(g);
    });

    void OnOpenVnc(object sender, RoutedEventArgs e) => Guard("Could not open the VNC console", async () =>
    {
        if (SelectedGuest is { Vm.IsTemplate: false, Vm.Type: PveGuestType.Qemu } g) await OpenVncAsync(g);
    });

    void OnOpenTerminal(object sender, RoutedEventArgs e) => Guard("Could not open the terminal", async () =>
    {
        if (SelectedGuest is { } g) await OpenTerminalAsync(g);
    });

    /// <summary>
    /// A container opens its terminal. A VM opens what its display offers (docs/decisions/0043): SPICE in the viewer,
    /// otherwise its VNC console in the user's VNC viewer, or its serial terminal for a serial or absent display. A
    /// config the account can't read falls back to SPICE, the launcher's earlier default.
    /// </summary>
    async Task OpenPrimaryAsync()
    {
        if (SelectedGuest is not { } g) return;
        if (g.Vm.IsTemplate)
        {
            ShowInfo($"{g.Label} is a template; it has no console.", InfoBarSeverity.Warning);
            return;
        }
        if (g.Vm.Type == PveGuestType.Lxc)
        {
            await OpenTerminalAsync(g);
            return;
        }
        GuestDisplay display = GuestDisplay.Spice;
        if (!await RunAsync($"Could not read the display of {g.Label}", async c =>
        {
            try { display = await c.GetDisplayAsync(g.Vm.Node, g.Vm.Vmid); }
            catch (PveApiException e) when (e is not PveAuthException)
            {
                LauncherLog.Write($"open: the config of {g.Label} can't be read ({e.Message}); trying SPICE");
            }
        })) return;
        LauncherLog.Write($"open: {g.Label} has a {display} display");
        switch (display)
        {
            case GuestDisplay.Vnc: await OpenVncAsync(g); break;
            case GuestDisplay.Serial or GuestDisplay.None: await OpenTerminalAsync(g); break;
            default: await OpenSpiceAsync(g); break;
        }
    }

    async Task OpenSpiceAsync(GuestItem g)
    {
        string viewer = Path.Combine(_home, ViewerLauncher.ExeName);
        await RunAsync($"Could not open {g.Label}", async c =>
        {
            string vv = await c.GetSpiceVvTextAsync(g.Vm.Node, g.Vm.Vmid);
            await ViewerLauncher.StartAsync(viewer, vv);
            LauncherLog.Write($"open: {g.Label} on {g.Vm.Node} in the viewer");
            ShowInfo($"Opened {g.Label} in SpiceViewer.");
        });
    }

    async Task OpenTerminalAsync(GuestItem g)
    {
        if (g.Vm.IsTemplate) return;
        string bridge = Path.Combine(_home, TerminalLauncher.ExeName);
        var host = _settings.Terminal;
        Task<int?>? exited = null;
        await RunAsync($"Could not open the terminal of {g.Label}", async c =>
        {
            // Looks for the terminal at each launch, before the descriptor is requested, so a chosen one that is
            // missing (or a custom command without {pipe}) costs no ticket renewal.
            var scan = WindowsTerminalLocator.Scan();
            ShowTerminalDetection(scan);
            var target = host is TerminalHost.Mintty or TerminalHost.Custom
                ? TerminalLauncher.Resolve(host, _settings.TerminalCommand)
                : TerminalLauncher.Resolve(host, scan);
            var d = await c.CreateTermDescriptorAsync(g.Vm);
            exited = await TerminalLauncher.LaunchAsync(d, bridge, target);
            LauncherLog.Write($"terminal: {g.Label} on {g.Vm.Node} via {host}: {target}");
            ShowInfo($"Opened the terminal of {g.Label}.");
        });
        if (exited is null) return;
        // Waits on the process handle, no polling; reports a session that failed after the hand-off.
        int? code = await exited;
        if (code is int x && x != 0)
            ShowInfo($"The terminal of {g.Label} closed: {TerminalLauncher.DescribeExit(x)} (PveTerm exit code {x}).", InfoBarSeverity.Warning);
    }

    /// <summary>The VM's VNC console in the user's VNC viewer, through PveTerm's bridge (docs/decisions/0043).</summary>
    async Task OpenVncAsync(GuestItem g)
    {
        if (g.Vm.IsTemplate || g.Vm.Type != PveGuestType.Qemu) return;
        string bridge = Path.Combine(_home, TerminalLauncher.ExeName);
        var viewer = _settings.VncViewer;
        var command = _settings.VncCommand.ToArray();
        Task<VncExit>? exited = null;
        await RunAsync($"Could not open the VNC console of {g.Label}", async c =>
        {
            var d = await c.CreateVncDescriptorAsync(g.Vm);
            // Status lines arrive on a thread-pool thread; "connect one yourself" carries the address to show.
            exited = await VncLauncher.LaunchAsync(d, bridge, viewer, command, line =>
            {
                LauncherLog.Write($"vnc: {g.Label}: {line}");
                if (line.StartsWith("Connect your VNC viewer to", StringComparison.Ordinal))
                    _ui.TryEnqueue(() => ShowInfo($"{g.Label}: {line}"));
            });
            LauncherLog.Write($"vnc: {g.Label} on {g.Vm.Node} via {Pve.Term.VncViewers.Id(viewer)}");
            ShowInfo($"Opening the VNC console of {g.Label}…");
        });
        if (exited is null) return;
        // Waits on the process handle, no polling; reports a session that failed after the hand-off.
        var result = await exited;
        if (result.Code != 0)
            ShowInfo($"The VNC console of {g.Label} closed: {result.LastStatus ?? VncLauncher.DescribeExit(result.Code)} (PveTerm exit code {result.Code}).",
                InfoBarSeverity.Warning);
    }

    void OnVncChoiceChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || VncChoice.SelectedIndex < 0) return;
        _settings.VncViewer = VncChoices[VncChoice.SelectedIndex];
        SaveSettings();
    }

    /// <summary>Detection runs when the list opens, never on a timer: file checks and uninstall-key reads only.</summary>
    void OnVncChoiceOpened(object sender, object e) => ShowVncDetection();

    /// <summary>Labels "Automatic" with what it opens and each viewer with whether it is installed; the custom item
    /// shows only when launcher.json has a command (or it is chosen).</summary>
    void ShowVncDetection()
    {
        var found = Pve.Term.VncViewers.Scan();
        AutoVncItem.Content = found.Count == 0 ? "Automatic (none found)" : $"Automatic ({found[0].DisplayName})";
        Label(TigerVncItem, Pve.Term.VncViewerKind.TigerVnc, "TigerVNC (recommended)");
        Label(TightVncItem, Pve.Term.VncViewerKind.TightVnc, "TightVNC");
        Label(UltraVncItem, Pve.Term.VncViewerKind.UltraVnc, "UltraVNC");
        Label(RealVncItem, Pve.Term.VncViewerKind.RealVnc, "RealVNC Viewer");
        CustomVncItem.Visibility = _settings.VncCommand.Count > 0 || _settings.VncViewer == Pve.Term.VncViewerKind.Custom
            ? Visibility.Visible : Visibility.Collapsed;

        void Label(ComboBoxItem item, Pve.Term.VncViewerKind kind, string name)
        {
            var install = found.FirstOrDefault(f => f.Kind == kind);
            item.Content = install is null ? $"{name} (not found)" : name;
            ToolTipService.SetToolTip(item, install?.Path);
        }
    }

    void OnTerminalChoiceChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || TerminalChoice.SelectedIndex < 0) return;
        _settings.Terminal = TerminalChoices[TerminalChoice.SelectedIndex];
        SaveSettings();
    }

    /// <summary>Detection runs when the list opens, never on a timer (energy rules): a few file checks and one
    /// registry value.</summary>
    void OnTerminalChoiceOpened(object sender, object e) => ShowTerminalDetection(WindowsTerminalLocator.Scan());

    /// <summary>Labels "Automatic" with what it opens, and shows each Windows Terminal found (or the chosen one,
    /// marked when missing).</summary>
    void ShowTerminalDetection(WindowsTerminalScan scan)
    {
        var auto = scan.Preferred;
        AutoTerminalItem.Content = auto is null ? "Automatic (new console)" : $"Automatic ({auto.DisplayName})";
        ToolTipService.SetToolTip(AutoTerminalItem, auto is null
            ? "Windows Terminal was not found; a new console opens."
            : $"A new tab in {auto.Path} ({scan.PreferredReason}).");
        Label(WtTerminalItem, TerminalHost.WindowsTerminal, "Windows Terminal");
        Label(WtPreviewTerminalItem, TerminalHost.WindowsTerminalPreview, "Windows Terminal Preview");
        // mintty: shown when found (or chosen); a few file checks, as for Windows Terminal.
        var mintty = MinttyLocator.Scan();
        MinttyTerminalItem.Visibility = mintty.Count > 0 || _settings.Terminal == TerminalHost.Mintty ? Visibility.Visible : Visibility.Collapsed;
        MinttyTerminalItem.Content = mintty.Count > 0 ? $"mintty ({mintty[0].DisplayName})" : "mintty (not found)";
        ToolTipService.SetToolTip(MinttyTerminalItem, mintty.Count > 0
            ? $"A new mintty window, {mintty[0].Path}, with MSYS=enable_pcon."
            : "mintty was not found on PATH, in MSYS2 or in Git for Windows.");
        // The custom command lives in launcher.json; the item shows only when one is set (or it is chosen).
        CustomTerminalItem.Visibility = _settings.TerminalCommand.Count > 0 || _settings.Terminal == TerminalHost.Custom
            ? Visibility.Visible : Visibility.Collapsed;
        ToolTipService.SetToolTip(CustomTerminalItem, _settings.TerminalCommand.Count > 0
            ? "terminalCommand in launcher.json: " + string.Join(" ", _settings.TerminalCommand)
            : "Set terminalCommand in launcher.json.");

        // The closed box shows a copy of the selected item's text; selecting the item again refreshes it.
        if (!TerminalChoice.IsDropDownOpen) RefreshTerminalChoiceBox();

        void Label(ComboBoxItem item, TerminalHost choice, string name)
        {
            var wt = scan.For(choice);
            item.Visibility = wt is not null || _settings.Terminal == choice ? Visibility.Visible : Visibility.Collapsed;
            item.Content = wt is null ? $"{name} (not found)"
                : wt.DisplayName.StartsWith(name, StringComparison.Ordinal) ? wt.DisplayName : $"{name} ({wt.DisplayName})";
            ToolTipService.SetToolTip(item, wt is null ? $"{name} was not found." : $"A new tab in {wt.Path}.");
        }
    }

    void OnTerminalChoiceClosed(object sender, object e) => RefreshTerminalChoiceBox();

    void RefreshTerminalChoiceBox()
    {
        int index = TerminalChoice.SelectedIndex;
        if (index < 0) return;
        bool loading = _loading;
        _loading = true;
        TerminalChoice.SelectedIndex = -1;
        TerminalChoice.SelectedIndex = index;
        _loading = loading;
    }

    // ---- ISignInPrompt: Pve.Api's sign-in flow asks through these dialogs, on the UI thread ----------------------

    Task<T> OnUi<T>(Func<Task<T>> f)
    {
        if (_ui.HasThreadAccess) return f();
        var done = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_ui.TryEnqueue(async () =>
        {
            try { done.SetResult(await f()); }
            catch (Exception e) { done.SetException(e); }
        }))
            done.SetCanceled();
        return done.Task;
    }

    public Task<SecretAnswer?> AskSecretAsync(PveServerConfig server, string? problem, CancellationToken ct) =>
        OnUi(() => Dialogs.AskSecretAsync(Root.XamlRoot, server, problem));

    public Task<string?> AskSecondFactorAsync(PveServerConfig server, TfaChallenge challenge, string? problem, CancellationToken ct) =>
        OnUi(() => Dialogs.AskSecondFactorAsync(Root.XamlRoot, server, challenge, problem));
}
