namespace Spice.Session;

/// <summary>How video-stream frames are timed against the guest's multimedia clock (docs/decisions/0025).</summary>
public enum VideoPacing
{
    /// <summary>
    /// Default. Frames keep their spacing on the guest timeline, held at most 50 ms after arrival; frames more than 80 ms
    /// behind the guest clock are dropped before decode. Adds no fixed delay.
    /// </summary>
    Spacing,
    /// <summary>
    /// spice-gtk's pacing: each frame waits until the guest clock reaches its timestamp; frames already behind it are
    /// dropped. Video then runs as far behind as the server's clock lead (about 400 ms), and behind the sound.
    /// </summary>
    Clock,
    /// <summary>Every frame is drawn on arrival: no queue and no drops.</summary>
    Off,
}
