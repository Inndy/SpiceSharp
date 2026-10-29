namespace Spice.Session;

/// <summary>
/// How video-stream frames, and in <see cref="Clock"/> also audio, are timed against the guest's multimedia clock
/// (docs/decisions/0025, 0030).
/// </summary>
public enum VideoPacing
{
    /// <summary>
    /// Frames keep their spacing on the guest timeline, held at most 50 ms after arrival; frames more than 80 ms behind
    /// the guest clock are dropped before decode. Adds no fixed delay. Audio plays on arrival.
    /// </summary>
    Spacing,
    /// <summary>
    /// spice-gtk's pacing: each frame waits until the guest clock reaches its timestamp; frames already behind it are
    /// dropped. Audio is held the same way (<see cref="SpiceSession.AudioData"/> carries each packet's delay), so sound
    /// and video line up, both as far behind as the server's clock lead (about 400 ms).
    /// </summary>
    Clock,
    /// <summary>Default. Every frame is drawn on arrival: no queue and no drops. Audio plays on arrival.</summary>
    Off,
}
