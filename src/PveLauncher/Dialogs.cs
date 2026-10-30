using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Pve.Api;

namespace PveLauncher;

/// <summary>The launcher's dialogs, built in code: server details, the secret, the second factor and confirmations.</summary>
static class Dialogs
{
    static ContentDialog Dialog(XamlRoot root, string title, UIElement content, string primary, string close = "Cancel") => new()
    {
        XamlRoot = root,
        Title = title,
        Content = content,
        PrimaryButtonText = primary,
        CloseButtonText = close,
        DefaultButton = ContentDialogButton.Primary,
    };

    static TextBlock ErrorText() => new()
    {
        TextWrapping = TextWrapping.Wrap,
        Foreground = (Brush)Application.Current.Resources["SystemFillColorCriticalBrush"],
        Visibility = Visibility.Collapsed,
    };

    static void Show(TextBlock error, string? text)
    {
        error.Text = text ?? "";
        error.Visibility = string.IsNullOrEmpty(text) ? Visibility.Collapsed : Visibility.Visible;
    }

    static TextBlock Note(string text) => new()
    {
        Text = text,
        TextWrapping = TextWrapping.Wrap,
        Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
    };

    /// <summary>Adds a server (<paramref name="existing"/> null) or edits a copy of one; null when cancelled.</summary>
    public static async Task<PveServerConfig?> EditServerAsync(XamlRoot root, PveServerConfig? existing)
    {
        var c = existing?.Clone() ?? new PveServerConfig();
        var name = new TextBox { Header = "Name (optional)", Text = c.Name, PlaceholderText = "lab" };
        var url = new TextBox { Header = "Address", Text = c.Url, PlaceholderText = "https://pve.example:8006" };
        var auth = new RadioButtons { Header = "Sign in with" };
        auth.Items.Add("User name and password");
        auth.Items.Add("API token");
        auth.SelectedIndex = c.Auth == PveAuthKind.Token ? 1 : 0;
        var user = new TextBox { Header = "User", Text = c.User, PlaceholderText = "root", Width = 200 };
        var realm = new TextBox { Header = "Realm", Text = c.Realm, PlaceholderText = "pam", Width = 120 };
        var userRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { user, realm } };
        var token = new TextBox { Header = "Token ID", Text = c.TokenId, PlaceholderText = "user@realm!tokenname" };
        var ca = new TextBox
        {
            Header = "CA certificate to pin (PEM, optional)",
            Text = c.CaPem ?? "",
            PlaceholderText = "-----BEGIN CERTIFICATE-----\n…\nLeave empty to use the Windows certificate store.",
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            Height = 110,
            FontFamily = new FontFamily("Consolas"),
        };
        var caNote = Note("With a CA, the server must present a certificate issued by it, and only by it. Proxmox's own CA is " +
                          "/etc/pve/pve-root-ca.pem on any node. Without one, Windows decides, as a browser would.");
        var secretNote = Note("The password or token secret is asked for when you connect. If you choose to remember it, it is " +
                              "kept in Windows Credential Manager, never in the server list.");
        var error = ErrorText();
        void UpdateAuth()
        {
            bool isToken = auth.SelectedIndex == 1;
            userRow.Visibility = isToken ? Visibility.Collapsed : Visibility.Visible;
            token.Visibility = isToken ? Visibility.Visible : Visibility.Collapsed;
        }
        auth.SelectionChanged += (_, _) => UpdateAuth();
        UpdateAuth();

        var panel = new StackPanel { Spacing = 10, MinWidth = 440, Children = { name, url, auth, userRow, token, ca, caNote, secretNote, error } };
        var dialog = Dialog(root, existing is null ? "Add a Proxmox VE server" : "Edit server",
            new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto }, "Save");
        dialog.PrimaryButtonClick += (_, e) =>
        {
            c.Name = name.Text.Trim();
            c.Url = url.Text.Trim();
            c.Auth = auth.SelectedIndex == 1 ? PveAuthKind.Token : PveAuthKind.Password;
            c.User = user.Text.Trim();
            c.Realm = realm.Text.Trim();
            c.TokenId = token.Text.Trim();
            c.CaPem = string.IsNullOrWhiteSpace(ca.Text) ? null : ca.Text.Trim().Replace("\r\n", "\n").Replace('\r', '\n') + "\n";
            string? problem = c.Problem();
            Show(error, problem);
            e.Cancel = problem is not null;
        };
        return await dialog.ShowAsync() == ContentDialogResult.Primary ? c : null;
    }

    public static async Task<SecretAnswer?> AskSecretAsync(XamlRoot root, PveServerConfig server, string? problem)
    {
        bool isToken = server.Auth == PveAuthKind.Token;
        string where;
        try { where = PveClient.NormalizeApiUrl(server.Url).Authority; }
        catch (PveApiException) { where = server.Url; }
        var box = new PasswordBox { Header = isToken ? "Token secret" : "Password" };
        var remember = new CheckBox { Content = "Remember in Windows Credential Manager" };
        var error = ErrorText();
        Show(error, problem);
        var panel = new StackPanel
        {
            Spacing = 10,
            MinWidth = 380,
            Children = { Note($"{server.Account} on {where}"), box, remember, error },
        };
        var dialog = Dialog(root, $"Sign in to {server.DisplayName}", panel, "Sign in");
        dialog.PrimaryButtonClick += (_, e) =>
        {
            if (box.Password.Length > 0) return;
            Show(error, isToken ? "Enter the token secret." : "Enter the password.");
            e.Cancel = true;
        };
        box.Loaded += (_, _) => box.Focus(FocusState.Programmatic);
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return null;
        var answer = new SecretAnswer(box.Password, remember.IsChecked == true);
        box.Password = "";
        return answer;
    }

    public static async Task<string?> AskSecondFactorAsync(XamlRoot root, PveServerConfig server, TfaChallenge challenge, string? problem)
    {
        var kind = new RadioButtons { Header = "Answer with" };
        if (challenge.Totp) kind.Items.Add("Authenticator app code (TOTP)");
        if (challenge.RecoveryKeysLeft > 0) kind.Items.Add($"Recovery key ({challenge.RecoveryKeysLeft} left)");
        kind.SelectedIndex = 0;
        var code = new TextBox { Header = "Code" };
        var error = ErrorText();
        Show(error, problem);
        var panel = new StackPanel { Spacing = 10, MinWidth = 380, Children = { Note($"{server.Account} has two-factor authentication.") } };
        if (kind.Items.Count > 1) panel.Children.Add(kind);
        panel.Children.Add(code);
        if (challenge.UnsupportedFactors is { Length: > 0 } others)
            panel.Children.Add(Note($"Also set up, but not usable here: {others}."));
        panel.Children.Add(error);
        bool Recovery() => !challenge.Totp || kind.SelectedIndex == 1;
        void UpdateKind() => code.Header = Recovery() ? "Recovery key" : "Six-digit code";
        kind.SelectionChanged += (_, _) => UpdateKind();
        UpdateKind();
        string? response = null;
        var dialog = Dialog(root, "Second factor", panel, "Verify");
        dialog.PrimaryButtonClick += (_, e) =>
        {
            try
            {
                response = Recovery() ? TfaChallenge.RecoveryResponse(code.Text) : TfaChallenge.TotpResponse(code.Text);
            }
            catch (ArgumentException ex)
            {
                Show(error, ex.Message.Split(" (Parameter", 2)[0]);
                e.Cancel = true;
            }
        };
        code.Loaded += (_, _) => code.Focus(FocusState.Programmatic);
        return await dialog.ShowAsync() == ContentDialogResult.Primary ? response : null;
    }

    public static async Task<bool> ConfirmAsync(XamlRoot root, string title, string text, string action) =>
        await Dialog(root, title, Note(text), action).ShowAsync() == ContentDialogResult.Primary;

    /// <summary>Primary: forget this server's secret; secondary: forget every secret the launcher saved.</summary>
    public static async Task<ContentDialogResult> ForgetAsync(XamlRoot root, string text, bool hasThis, int all)
    {
        var dialog = Dialog(root, "Forget saved credentials", Note(text + (all > 0 ? $"\n\nThe launcher has {all} saved in all." : "")),
            hasThis ? "Forget this one" : "");
        if (all > 0) dialog.SecondaryButtonText = $"Forget all {all}";
        if (!hasThis) dialog.DefaultButton = ContentDialogButton.Close;
        return await dialog.ShowAsync();
    }
}
