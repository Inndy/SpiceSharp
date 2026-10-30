using System.Text;
using Pve.Term;

namespace Pve.Api.Tests;

/// <summary>
/// Entry point of the test assembly when run as a program (the test host does not use it):
/// <list type="bullet">
/// <item><c>--fake-viewer ARGS...</c>: stands in for <c>SpiceViewer.exe</c>; prints its arguments and everything it
/// read from standard input, so a test can check what the launcher handed over and how.</item>
/// <item><c>--fake-bridge console --pipe NAME --nonce HEX</c>: stands in for <c>PveTerm.exe</c>; collects the
/// descriptor over the hand-off pipe, writes its fields to the file named by <see cref="ReportVariable"/> and exits
/// with code 7.</item>
/// </list>
/// </summary>
public static class Helper
{
    public static int Main(string[] args)
    {
        if (args.Length >= 1 && args[0] == "--fake-viewer")
        {
            var stdin = Console.OpenStandardInput();
            using var reader = new StreamReader(stdin, new UTF8Encoding(false));
            string text = reader.ReadToEnd();
            var stdout = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { NewLine = "\n" };
            stdout.WriteLine("ARGS:" + string.Join("|", args[1..]));
            stdout.WriteLine("CMDLINE:" + Environment.CommandLine);
            stdout.Write("STDIN:" + text);
            stdout.Flush();
            return 0;
        }
        if (args is ["--fake-bridge", "console", "--pipe", var pipe, "--nonce", var nonce])
        {
            try
            {
                var d = TermDescriptor.Parse(HandoffClient.Receive(pipe, nonce, TimeSpan.FromSeconds(20)));
                File.WriteAllText(Environment.GetEnvironmentVariable(ReportVariable)!,
                    $"target={d.Target} node={d.Node} vmid={d.Vmid} title={d.Title} token={d.ApiToken} cookie={d.AuthCookie} csrf={d.CsrfToken} ca={(d.CaPem is null ? "none" : "pinned")}");
                return 7;
            }
            catch (Exception e)
            {
                Console.Error.WriteLine("error=" + e.Message);
                return 9;
            }
        }
        Console.Error.WriteLine("usage: --fake-viewer ARGS... | --fake-bridge console --pipe NAME --nonce HEX");
        return 2;
    }

    public const string ReportVariable = "PVEAPI_TEST_REPORT";

    public static string Exe => Path.Combine(AppContext.BaseDirectory, "Pve.Api.Tests.exe");
}
