using SpiceViewer;
using Spice.Wire;

// Errors are logged by AudioOutput (it never throws to the caller); watch the [Error] lines.
Log.Sink = (level, message) => Console.WriteLine($"[{level}] {message}");
AudioOutput.SelfTest();
using (var audio = new AudioOutput())
{
    for (int i = 0; i < 3; i++)
    {
        audio.Start(2, 48000);
        audio.SetVolume(0.5f); audio.SetMute(true); audio.Write(new short[1920]);
        Thread.Sleep(50); audio.Stop();
    }
    audio.Stop(); audio.Dispose(); audio.Stop();
}
Console.WriteLine("WASAPI tone, Start/Write/volume/mute/Stop/restart completed.");
