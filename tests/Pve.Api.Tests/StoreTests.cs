using Pve.Term.Tests;

namespace Pve.Api.Tests;

/// <summary>Real Credential Manager round trips under a test-only prefix; every entry is removed afterwards.</summary>
public sealed class CredentialStoreTests : IDisposable
{
    readonly CredentialStore _store = new("SpiceViewer.Test-" + Guid.NewGuid().ToString("N")[..12]);

    public void Dispose() => _store.DeleteAll();

    static readonly Uri Api = new("https://pve.example:8006/");

    [Fact]
    public void WriteReadOverwriteDelete()
    {
        string target = _store.TargetFor(Api, PveAuthKind.Password, "alice@pve");
        Assert.Equal($"{_store.Prefix}/pve.example:8006/password/alice@pve", target);
        Assert.Null(_store.Read(target));

        _store.Write(target, "alice@pve", "first p\u00e4ssword \U0001F511");
        Assert.Equal("first p\u00e4ssword \U0001F511", _store.Read(target));
        _store.Write(target, "alice@pve", "second");
        Assert.Equal("second", _store.Read(target));

        Assert.True(_store.Delete(target));
        Assert.Null(_store.Read(target));
        Assert.False(_store.Delete(target));
    }

    [Fact]
    public void TokensAndPasswordsAreSeparateEntriesPerServer()
    {
        var token = new PveServerConfig { Url = "https://PVE.example", Auth = PveAuthKind.Token, TokenId = "alice@pve!viewer" };
        var password = new PveServerConfig { Url = "pve.example:8006", Auth = PveAuthKind.Password, User = "alice", Realm = "pve" };
        var other = new PveServerConfig { Url = "pve2.example", Auth = PveAuthKind.Password, User = "alice", Realm = "pve" };
        Assert.Equal($"{_store.Prefix}/pve.example:8006/token/alice@pve!viewer", _store.TargetFor(token));
        _store.Write(_store.TargetFor(token), token.Account, "token-secret");
        _store.Write(_store.TargetFor(password), password.Account, "password");
        _store.Write(_store.TargetFor(other), other.Account, "other");
        Assert.Equal("token-secret", _store.Read(_store.TargetFor(token)));
        Assert.Equal("password", _store.Read(_store.TargetFor(password)));
        Assert.Equal(3, _store.List().Count);
        Assert.All(_store.List(), t => Assert.StartsWith(_store.Prefix + "/", t));

        Assert.Equal(3, _store.DeleteAll());
        Assert.Empty(_store.List());
    }

    [Theory]
    [InlineData("alice@pve", "alice@pve")]
    [InlineData("Alice@pve", "%0041lice@pve")]
    [InlineData("alice@pve!Viewer", "alice@pve!%0056iewer")]
    [InlineData("a%0041@pve", "a%00250041@pve")] // '%' is escaped, so no plain spelling can imitate an escape
    [InlineData("jörg@ldap", "j%00f6rg@ldap")]
    public void AccountsAreSpelledWithoutCaseVariants(string account, string encoded)
    {
        Assert.Equal(encoded, CredentialStore.EncodeAccount(account));
        Assert.DoesNotContain(encoded, char.IsUpper);
    }

    [Fact]
    public void MoveFollowsAnEditAndNeverDeletesItsOwnDestination()
    {
        var url = new Uri("https://pve.example:8006/");
        string alice = _store.TargetFor(url, PveAuthKind.Password, "alice@pve");
        string capital = _store.TargetFor(url, PveAuthKind.Password, "Alice@pve");
        string other = _store.TargetFor(new Uri("https://pve2.example:8006/"), PveAuthKind.Password, "alice@pve");
        _store.Write(alice, "alice@pve", "secret-a");

        Assert.True(_store.Move(alice, capital, "Alice@pve")); // a case-only edit is a real move now
        Assert.Null(_store.Read(alice));
        Assert.Equal("secret-a", _store.Read(capital));

        Assert.True(_store.Move(capital, other, "alice@pve"));
        Assert.Equal("secret-a", _store.Read(other));
        Assert.False(_store.Move(other, other.ToUpperInvariant().Replace(_store.Prefix.ToUpperInvariant(), _store.Prefix, StringComparison.Ordinal), "x"));
        Assert.Equal("secret-a", _store.Read(other)); // a target Windows sees as the same entry is left alone
        Assert.False(_store.Move(alice, capital, "Alice@pve")); // nothing saved: nothing moves
    }

    [Fact]
    public void TargetsOutsideThePrefixAreRefused()
    {
        Assert.Throws<ArgumentException>(() => _store.Read("SpiceViewer.PVE/pve.example:8006/password/root@pam"));
        Assert.Throws<ArgumentException>(() => _store.Delete("someone-elses-credential"));
        Assert.Throws<ArgumentException>(() => new CredentialStore("bad*prefix"));
    }

    [Fact]
    public void EmptyAndOversizedSecretsAreRefused()
    {
        string target = _store.TargetFor(Api, PveAuthKind.Token, "a@pve!t");
        Assert.Throws<CredentialStoreException>(() => _store.Write(target, "a@pve!t", ""));
        Assert.Throws<CredentialStoreException>(() => _store.Write(target, "a@pve!t", new string('x', CredentialStore.MaxSecretChars + 1)));
        _store.Write(target, "a@pve!t", new string('x', CredentialStore.MaxSecretChars));
        Assert.Equal(CredentialStore.MaxSecretChars, _store.Read(target)!.Length);
    }
}

public sealed class SettingsTests : IDisposable
{
    readonly string _dir = Path.Combine(Path.GetTempPath(), "pveapi-settings-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    [Fact]
    public void ServersRoundTripWithoutSecrets()
    {
        var s = new LauncherSettings { Terminal = TerminalHost.WindowsTerminal };
        s.Servers.Add(new PveServerConfig { Name = "lab", Url = "https://pve.example:8006", Auth = PveAuthKind.Token, TokenId = "a@pve!t", CaPem = TestPki.Shared.CaPem });
        s.Servers.Add(new PveServerConfig { Url = "pve2.example", User = "alice", Realm = "pve" });
        s.LastServerId = s.Servers[1].Id;
        s.Save(_dir);

        string json = File.ReadAllText(Path.Combine(_dir, LauncherSettings.FileName));
        Assert.Contains("\"auth\": \"token\"", json);
        Assert.Contains("\"terminal\": \"wt\"", json);
        Assert.DoesNotContain("secret", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("password\":", json);
        Assert.Empty(Directory.GetFiles(_dir, "*.tmp"));

        var back = LauncherSettings.Load(_dir);
        Assert.Null(back.LoadProblem);
        Assert.Equal(TerminalHost.WindowsTerminal, back.Terminal);
        Assert.Equal(s.LastServerId, back.LastServerId);
        Assert.Equal(2, back.Servers.Count);
        var lab = back.Servers[0];
        Assert.Equal(("lab", "https://pve.example:8006", PveAuthKind.Token, "a@pve!t"), (lab.Name, lab.Url, lab.Auth, lab.TokenId));
        Assert.Equal(TestPki.Shared.CaPem, lab.CaPem);
        Assert.Null(lab.Problem());
        var two = back.Servers[1];
        Assert.Equal((PveAuthKind.Password, "alice", "pve", "alice@pve"), (two.Auth, two.User, two.Realm, two.Account));
        Assert.Equal("pve2.example", two.DisplayName);
    }

    [Theory]
    [InlineData("\"terminal\": \"auto\",", TerminalHost.Auto, "auto")]
    [InlineData("\"terminal\": \"default\",", TerminalHost.Default, "default")] // chosen before "auto" existed: kept
    [InlineData("\"terminal\": \"wt\",", TerminalHost.WindowsTerminal, "wt")]
    [InlineData("\"terminal\": \"wt-preview\",", TerminalHost.WindowsTerminalPreview, "wt-preview")]
    [InlineData("\"terminal\": \"conhost\",", TerminalHost.Conhost, "conhost")]
    [InlineData("", TerminalHost.Auto, "auto")]
    [InlineData("\"terminal\": null,", TerminalHost.Auto, "auto")]
    [InlineData("\"terminal\": \"Default\",", TerminalHost.Auto, "auto")]
    public void TheTerminalChoiceRoundTrips(string field, TerminalHost expected, string saved)
    {
        Directory.CreateDirectory(_dir);
        string path = Path.Combine(_dir, LauncherSettings.FileName);
        File.WriteAllText(path, $$"""{ "version": 1, {{field}} "servers": [] }""");
        var s = LauncherSettings.Load(_dir);
        Assert.Null(s.LoadProblem);
        Assert.Equal(expected, s.Terminal);
        s.Save(_dir);
        Assert.Contains($"\"terminal\": \"{saved}\"", File.ReadAllText(path));
        Assert.Equal(expected, LauncherSettings.Load(_dir).Terminal);
    }

    [Fact]
    public void NoFileMeansAutomatic() => Assert.Equal(TerminalHost.Auto, LauncherSettings.Load(_dir).Terminal);

    [Fact]
    public void ABrokenFileIsSetAsideAtTheNextSave()
    {
        Directory.CreateDirectory(_dir);
        string path = Path.Combine(_dir, LauncherSettings.FileName);
        File.WriteAllText(path, "{ not json");
        var s = LauncherSettings.Load(_dir);
        Assert.NotNull(s.LoadProblem);
        Assert.Empty(s.Servers);
        s.Servers.Add(new PveServerConfig { Url = "pve.example", User = "u" });
        s.Save(_dir);
        Assert.Equal("{ not json", File.ReadAllText(path + ".bad"));
        Assert.Single(LauncherSettings.Load(_dir).Servers);
    }

    [Fact]
    public void CommentsTrailingCommasAndUnknownValuesAreTolerated()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, LauncherSettings.FileName), """
            // hand-edited
            { "version": 1, "terminal": "kitty", "future": true,
              "servers": [ { "url": "pve.example", "user": "u", "auth": "???" }, { "name": "no url" }, ], }
            """);
        var s = LauncherSettings.Load(_dir);
        Assert.Null(s.LoadProblem);
        Assert.Equal(TerminalHost.Auto, s.Terminal);
        var only = Assert.Single(s.Servers);
        Assert.Equal(PveAuthKind.Password, only.Auth);
        Assert.Equal("pam", only.Realm);
    }

    [Fact]
    public void PathsFollowTheViewersRules()
    {
        string local = @"C:\Users\u\AppData\Local", roaming = @"C:\Users\u\AppData\Roaming";
        var installed = LauncherPaths.Resolve(@"C:\Users\u\AppData\Local\Programs\SpiceViewer\", local, roaming, _ => true);
        Assert.Equal((true, @"C:\Users\u\AppData\Roaming\SpiceViewer", @"C:\Users\u\AppData\Local\SpiceViewer"),
            (installed.Installed, installed.SettingsDir, installed.LogDir));
        var portable = LauncherPaths.Resolve(@"D:\tools\SpiceViewer", local, roaming, _ => true);
        Assert.Equal((false, @"D:\tools\SpiceViewer\data", false), (portable.Installed, portable.SettingsDir, portable.FellBack));
        var readOnly = LauncherPaths.Resolve(@"C:\Program Files\SpiceViewer", local, roaming, _ => false);
        Assert.Equal((false, @"C:\Users\u\AppData\Roaming\SpiceViewer", true), (readOnly.Installed, readOnly.SettingsDir, readOnly.FellBack));
    }

    [Fact]
    public void HomeIsWhereTheViewerIs()
    {
        var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { @"D:\sv\SpiceViewer.exe", @"E:\flat\SpiceViewer.exe" };
        Assert.Equal(@"D:\sv", LauncherPaths.FindHome(@"D:\sv\PveLauncher\", files.Contains));
        Assert.Equal(@"E:\flat", LauncherPaths.FindHome(@"E:\flat", files.Contains));
        Assert.Equal(@"F:\dev\bin", LauncherPaths.FindHome(@"F:\dev\bin\", files.Contains));
    }

    [Fact]
    public void ServerProblemsAreNamed()
    {
        Assert.NotNull(new PveServerConfig { Url = "", User = "u" }.Problem());
        Assert.NotNull(new PveServerConfig { Url = "pve.example", User = "" }.Problem());
        Assert.NotNull(new PveServerConfig { Url = "pve.example", User = "u", Realm = "" }.Problem());
        Assert.NotNull(new PveServerConfig { Url = "pve.example", Auth = PveAuthKind.Token, TokenId = "u@pam" }.Problem());
        Assert.Null(new PveServerConfig { Url = "pve.example", User = "u" }.Problem());
    }
}
