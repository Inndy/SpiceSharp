namespace Spice.Session;

/// <summary>Whether the guest has a display to show (docs/decisions/0035).</summary>
public enum GuestDisplayState
{
    /// <summary>Not every channel is linked yet, and the display channel has sent neither a DISPLAY_MARK nor a primary surface.</summary>
    Pending,
    /// <summary>There is a primary surface.</summary>
    On,
    /// <summary>
    /// The server has sent its initial state without a primary surface, or destroyed the primary surface without a new
    /// one: the guest's display is off (powered down or blanked). Keys sent to the guest usually wake it.
    /// </summary>
    Off,
    /// <summary>
    /// Every channel is linked but the display channel has sent nothing about a display yet. A guest whose display was
    /// already off when the session started stays here: spice-server then sends no DISPLAY_MARK (finding L7). Usually
    /// the display is off; keys wake it.
    /// </summary>
    Waiting,
}
