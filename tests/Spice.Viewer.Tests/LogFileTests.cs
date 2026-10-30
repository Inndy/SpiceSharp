namespace SpiceViewer;

/// <summary>
/// viewer.log shared by two viewers (finding L15, docs/decisions/0040). Two handles in one process have the same
/// problem as two processes: with FileMode.Append each keeps its own position and the later writer overwrites the other.
/// </summary>
public sealed class LogFileTests : IDisposable
{
    readonly string _dir = Path.Combine(Path.GetTempPath(), "SpiceSharp-log-test-" + Guid.NewGuid().ToString("N"));
    string File_ => Path.Combine(_dir, "viewer.log");

    public LogFileTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch (IOException) { } }

    /// <summary>Reads the log while viewers still have it open, as a log viewer would.</summary>
    string[] ReadShared()
    {
        using var r = new StreamReader(new FileStream(File_, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete));
        return r.ReadToEnd().Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
    }

    [Fact]
    public void TwoWritersAlternatingKeepEveryLine()
    {
        using (var a = LogFile.Open(File_, 1 << 20))
        using (var b = LogFile.Open(File_, 1 << 20))
        {
            for (int i = 0; i < 50; i++)
            {
                a.WriteLine($"A {i} {new string('a', i)}");
                b.WriteLine($"B {i}");
            }
        }
        var lines = ReadShared();
        Assert.Equal(100, lines.Length);
        for (int i = 0; i < 50; i++)
        {
            Assert.Equal($"A {i} {new string('a', i)}", lines[2 * i]);
            Assert.Equal($"B {i}", lines[2 * i + 1]);
        }
    }

    [Fact]
    public async Task ConcurrentWritersNeverSpliceLines()
    {
        const int perWriter = 2000;
        using (var a = LogFile.Open(File_, 1 << 20))
        using (var b = LogFile.Open(File_, 1 << 20))
        {
            var ta = Task.Run(() => { for (int i = 0; i < perWriter; i++) a.WriteLine($"A {i:D5} {new string('a', 40)} é"); });
            var tb = Task.Run(() => { for (int i = 0; i < perWriter; i++) b.WriteLine($"B {i:D5} {new string('b', 70)}"); });
            await Task.WhenAll(ta, tb);
        }
        var lines = ReadShared();
        Assert.Equal(2 * perWriter, lines.Length);
        Assert.Equal(Enumerable.Range(0, perWriter).Select(i => $"A {i:D5} {new string('a', 40)} é"), lines.Where(l => l.StartsWith('A')));
        Assert.Equal(Enumerable.Range(0, perWriter).Select(i => $"B {i:D5} {new string('b', 70)}"), lines.Where(l => l.StartsWith('B')));
    }

    [Fact]
    public void AppendsToAnExistingLogAndEmptiesAnOversizedOneInPlace()
    {
        File.WriteAllText(File_, "earlier run\r\n");
        using (var a = LogFile.Open(File_, 1 << 20)) a.WriteLine("this run");
        Assert.Equal(["earlier run", "this run"], ReadShared());

        // Another viewer still has the file open when a new one finds it too big: both go on at the new end.
        using var other = LogFile.Open(File_, 1 << 20);
        using (var big = new FileStream(File_, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete))
            big.Write(new byte[2000]);
        using var next = LogFile.Open(File_, 1000);
        next.WriteLine("new");
        other.WriteLine("other");
        Assert.Equal(["new", "other"], ReadShared());
    }
}
