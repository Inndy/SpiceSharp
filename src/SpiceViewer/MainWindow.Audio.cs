using Spice.Session;

namespace SpiceViewer;

sealed partial class MainWindow
{
    /// <summary>Wires playback events to the audio output (opened on start, closed on stop).</summary>
    partial void AttachAudioCore(SpiceSession s);

    void AttachAudio(SpiceSession s) => AttachAudioCore(s);
}
