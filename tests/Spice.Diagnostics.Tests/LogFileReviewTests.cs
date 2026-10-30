using SpiceViewer;

namespace Spice.Diagnostics.Tests;

public sealed class LogFileReviewTests
{
    [Fact]
    public void AFailedAppendReportsTheErrorInsteadOfClaimingSuccess()
    {
        string directory = Directory.CreateTempSubdirectory("spice-log-review-").FullName;
        string path = Path.Combine(directory, "viewer.log");
        try
        {
            using var log = LogFile.Open(path, 1 << 20);
            using var blocker = new FileStream(path, FileMode.Open, FileAccess.ReadWrite,
                FileShare.ReadWrite | FileShare.Delete);
            // A different handle's byte-range lock makes this append fail synchronously with a real I/O error.
            blocker.Lock(0, long.MaxValue);
            Exception? error;
            try
            {
                error = Record.Exception(() => log.WriteLine("must not silently disappear"));
                Assert.Equal(0, blocker.Length); // Establish actual data loss, not just an exception-policy difference.
            }
            finally { blocker.Unlock(0, long.MaxValue); }
            log.WriteLine("after unlock");
            blocker.Position = 0;
            using var reader = new StreamReader(blocker, leaveOpen: true);
            Assert.Equal("after unlock\r\n", reader.ReadToEnd());
            Assert.IsType<IOException>(error);
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public void ADisposedLogCannotPassItsClosedHandleToWriteFile()
    {
        string directory = Directory.CreateTempSubdirectory("spice-log-lifetime-").FullName;
        try
        {
            using var log = LogFile.Open(Path.Combine(directory, "viewer.log"), 1 << 20);
            log.Dispose();
            Assert.Throws<ObjectDisposedException>(() => log.WriteLine("after dispose"));
        }
        finally { Directory.Delete(directory, true); }
    }
}
