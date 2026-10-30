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
/// Nothing refreshes by itself: the list is fetched on sign-in and on Refresh (energy rules).
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
    bool _busy, _loading;

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

        _loading = true;
        TerminalChoice.SelectedIndex = (int)_settings.Terminal;
        foreach (var s in _settings.Servers) Servers.Add(new ServerItem(s));
        _loading = false;
        SizeToDpi(1120, 700);
        Closed += (_, _) =>
        {
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
        ConsoleButton.Content = guest?.Vm.Type == PveGuestType.Lxc ? "Open terminal" : "Open console";
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

    async Task LoadGuestsAsync(PveClient client, PveServerConfig server)
    {
        // A token's sign-in already fetched the version (smoke test B6); a password sign-in didn't.
        string version = client.ServerVersion ?? await client.GetVersionAsync();
        var guests = await client.ListVmsAsync();
        if (SelectedServer?.Config.Id != server.Id) return;
        _guests = guests;
        SetHeading(server.DisplayName, $"PVE {version} · {client.SignedInAs}");
        ApplyFilter();
        ShowInfo($"{_guests.Count} guests.", InfoBarSeverity.Informational);
    }

    void ApplyFilter()
    {
        string filter = Filter.Text.Trim();
        var keep = SelectedGuest?.Vm;
        Guests.Clear();
        foreach (var vm in _guests)
        {
            var g = new GuestItem(vm);
            if (g.Matches(filter)) Guests.Add(g);
        }
        if (keep is not null && Guests.FirstOrDefault(g => g.Vm.Vmid == keep.Vmid) is { } again) VmList.SelectedItem = again;
        UpdateButtons();
    }

    // ---- Servers --------------------------------------------------------------------------------------------------

    // Every async event handler goes through Guard, so an exception in it is shown and logged (smoke test B1).

    void OnServerSelected(object sender, SelectionChangedEventArgs e) => Guard("Could not list the guests", ServerSelectedAsync);

    async Task ServerSelectedAsync()
    {
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

    void OnGuestSelected(object sender, SelectionChangedEventArgs e) => UpdateButtons();

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
        // A container has no SPICE display; its terminal is the console.
        MenuConsole.Visibility = guest?.Vm.Type == PveGuestType.Lxc ? Visibility.Collapsed : Visibility.Visible;
        MenuConsole.IsEnabled = MenuTerminal.IsEnabled = guest is not null && !guest.Vm.IsTemplate && !_busy;
    }

    void OnOpenConsole(object sender, RoutedEventArgs e) => Guard("Could not open the console", OpenPrimaryAsync);

    void OnOpenTerminal(object sender, RoutedEventArgs e) => Guard("Could not open the terminal", async () =>
    {
        if (SelectedGuest is { } g) await OpenTerminalAsync(g);
    });

    /// <summary>A VM opens its SPICE console in the viewer; a container opens its terminal.</summary>
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
            var d = await c.CreateTermDescriptorAsync(g.Vm);
            exited = await TerminalLauncher.LaunchAsync(d, bridge, host);
            LauncherLog.Write($"terminal: {g.Label} on {g.Vm.Node} via {host}");
            ShowInfo($"Opened the terminal of {g.Label}.");
        });
        if (exited is null) return;
        // Waits on the process handle, no polling; reports a session that failed after the hand-off.
        int? code = await exited;
        if (code is int x && x != 0)
            ShowInfo($"The terminal of {g.Label} closed: {TerminalLauncher.DescribeExit(x)} (PveTerm exit code {x}).", InfoBarSeverity.Warning);
    }

    void OnTerminalChoiceChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || TerminalChoice.SelectedIndex < 0) return;
        _settings.Terminal = (TerminalHost)TerminalChoice.SelectedIndex;
        SaveSettings();
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
