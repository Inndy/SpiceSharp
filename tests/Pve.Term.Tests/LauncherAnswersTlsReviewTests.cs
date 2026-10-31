using System.Diagnostics;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Pve.Term.Tests;

public sealed class LauncherAnswersTlsReviewTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task PythonLeafPinRejectsExpiredCertificates(bool historical)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var now = DateTimeOffset.UtcNow;
        using var cert = new CertificateRequest("CN=offline.example", key, HashAlgorithmName.SHA256)
            .CreateSelfSigned(historical ? new DateTimeOffset(1949, 1, 1, 0, 0, 0, TimeSpan.Zero) : now.AddDays(-1),
                historical ? new DateTimeOffset(1960, 1, 1, 0, 0, 0, TimeSpan.Zero) : now.AddDays(1));
        string pem = Path.Combine(Path.GetTempPath(), "pve-review-" + Guid.NewGuid().ToString("N") + ".pem");
        // Public throwaway certificate only; no key or credentials are written.
        File.WriteAllText(pem, cert.ExportCertificatePem());
        const string script = """
            import ast, datetime, pathlib, ssl, sys
            from unittest.mock import patch
            from types import SimpleNamespace
            root, pem, historical = pathlib.Path(sys.argv[1]), sys.argv[2], sys.argv[3] == 'True'
            tree = ast.parse((root / 'tools' / 'pve-vv.py').read_text(encoding='utf-8'))
            names = {'_der_items', '_cert_facts', '_PinnedLeafContext', '_pinned_context'}
            # Execute definitions only, excluding every top-level credential read and CLI action.
            nodes = [n for n in tree.body if isinstance(n, (ast.FunctionDef, ast.ClassDef)) and n.name in names]
            env = {'ssl': ssl}
            exec(compile(ast.Module(body=nodes, type_ignores=[]), '<offline TLS review>', 'exec'), env)
            if historical:
                try:
                    env['_pinned_context'](pem)
                except SystemExit:
                    pass
                else:
                    raise AssertionError('expired 1960 certificate was accepted as a valid pin')
            else:
                ctx, _ = env['_pinned_context'](pem)
                der = ssl.PEM_cert_to_DER_cert(pathlib.Path(pem).read_text())
                peer = SimpleNamespace(getpeercert=lambda **kw: der, close=lambda: None)
                # Advance wall time beyond expiry between context creation and a new handshake.
                real_datetime = datetime.datetime
                class Future(real_datetime):
                    @classmethod
                    def now(cls, tz=None):
                        return real_datetime.now(tz) + datetime.timedelta(days=2)
                with patch.object(datetime, 'datetime', Future), patch.object(ssl.SSLContext, 'wrap_socket', return_value=peer):
                    try:
                        ctx.wrap_socket(None, server_hostname='offline.example')
                    except ssl.SSLCertVerificationError:
                        pass
                    else:
                        raise AssertionError('new handshake accepted a pin that expired after context creation')
            """;
        try
        {
            var psi = new ProcessStartInfo("python")
            {
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true,
            };
            foreach (var arg in new[] { "-I", "-B", "-c", script,
                Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../")), pem, historical.ToString() })
                psi.ArgumentList.Add(arg);
            using var p = Process.Start(psi)!;
            var stdout = p.StandardOutput.ReadToEndAsync();
            var stderr = p.StandardError.ReadToEndAsync();
            try
            {
                await p.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
                Assert.True(p.ExitCode == 0, await stderr + await stdout);
            }
            finally { if (!p.HasExited) p.Kill(entireProcessTree: true); }
        }
        finally { File.Delete(pem); }
    }
}
