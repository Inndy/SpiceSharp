namespace Spice.Media;

/// <summary>
/// <see cref="PcmQueue"/> counters since its last reset: samples dropped on overflow or trim; underruns; clock
/// corrections of the held timeline (frames of silence placed before all held audio, frames of held audio or of a late
/// packet dropped, and how many corrections); source gaps (frames of silence placed between packets, frames of
/// overlapping packet starts cut); and samples still queued when the queue was closed.
/// </summary>
public readonly record struct PcmQueueStatistics(long DroppedSamples, long Underruns, long SyncSilenceFrames,
    long SyncDroppedFrames, long SyncCorrections, long GapSilenceFrames, long GapTrimmedFrames, long DiscardedSamples);

/// <summary>
/// The jitter queue between the playback channel and the audio device: a ring of interleaved s16 PCM that the network
/// thread fills (<see cref="Write"/>) and the device's render thread drains (<see cref="ReadyFrames"/>,
/// <see cref="Transfer"/>). Lengths and offsets are always whole frames. All members are thread-safe; nothing allocates
/// after <see cref="Reset"/> except a one-time growth of the ring when a synchronized packet needs more room.
/// <para>
/// Unsynchronized writes (<see cref="Unsynced"/>) play as soon as possible: the queue primes 20 ms after an underrun,
/// trims queued latency above 100 ms down to 40 ms, and holds at most 200 ms. Synchronized writes carry a presentation
/// delay (clock pacing, docs/decisions/0030): the queue inserts silence or drops its oldest samples so that the packet
/// is heard that many ms from now, counting the audio the device already holds (<see cref="DeviceQueuedFrames"/>).
/// </para>
/// </summary>
public sealed class PcmQueue
{
    /// <summary>The presentation delay of a packet that plays as soon as possible.</summary>
    public const int Unsynced = int.MinValue;

    /// <summary>Ring capacity for unsynchronized playback; the oldest samples are dropped beyond this.</summary>
    public const int RingMs = 200;
    /// <summary>After an underrun, wait for this much before resuming.</summary>
    public const int PrimeMs = 20;
    /// <summary>Unsynchronized: queued latency above this is trimmed ...</summary>
    public const int TrimAboveMs = 100;
    /// <summary>... down to this.</summary>
    public const int TrimToMs = 40;
    /// <summary>Synchronized: the longest delay honoured, against a clock far off the stamps (video's clock-mode cap).</summary>
    public const int MaxDelayMs = 2000;
    /// <summary>Synchronized: the shortest delay aimed for; a packet due sooner (late) still keeps this cushion.</summary>
    public const int MinDelayMs = 40;
    /// <summary>
    /// Synchronized: the timing budget. Held audio may sit up to this far (ms) off its stamp, counting both the held
    /// timeline's uncorrected clock shift and the source gap left pending (so with audio held, the shift is corrected
    /// beyond this less <see cref="GapJitterMs"/>).
    /// </summary>
    public const int ToleranceMs = 40;
    /// <summary>
    /// Synchronized: source gaps (stamp differences between packets, after sub-ms rounding is removed) within this
    /// allowance (ms) stay pending instead of being laid out as silence or cut, so stamp jitter of alternating sign
    /// cancels. One period of QEMU's audio timer (10 ms by default), which completes the frames the server stamps: a
    /// late tick moves one stamp by less than a period and the next one back (as remembered, not measured).
    /// </summary>
    public const int GapJitterMs = 10;

    readonly Lock _lock = new();
    short[] _ring = [];
    int _head, _count, _channels, _rate, _baseLength, _primeSamples, _trimAbove, _trimTo;
    bool _active, _primed, _synced;
    int _deviceFrames;
    /// <summary>
    /// Synchronized: frames by which the last packet's stamps end past the end of the queued audio, from source gaps not
    /// yet laid out as silence (negative: stamps overlapping). Reset by unsynchronized writes.
    /// </summary>
    long _stampLead;
    /// <summary>The stream ended (<see cref="BeginDrain"/>): what is held plays out, then the device closes.</summary>
    bool _draining;
    /// <summary>The next synchronized write starts a new stream's timeline after audio still playing out.</summary>
    bool _newTimeline;
    /// <summary>Samples at the head that belong to the previous stream (<see cref="TryContinue"/>); never retimed.</summary>
    int _protectedSamples;
    /// <summary>
    /// Bumped when clock pacing is switched off and the held audio flushed; <see cref="Transfer"/> takes nothing if it
    /// changed since the render thread's <see cref="ReadyFrames"/> (<c>_readyEpoch</c>).
    /// </summary>
    int _epoch, _readyEpoch;
    long _droppedSamples, _underruns, _syncSilenceFrames, _syncDroppedFrames, _syncCorrections, _gapSilenceFrames,
        _gapTrimmedFrames, _discardedSamples;

    /// <summary>Empties the queue and opens it for <paramref name="channels"/> x <paramref name="sampleRate"/>.</summary>
    public void Reset(int channels, int sampleRate)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(channels, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(sampleRate, 1000);
        lock (_lock)
        {
            int length = sampleRate * RingMs / 1000 * channels;
            if (_ring.Length != length) _ring = new short[length];
            _channels = channels;
            _rate = sampleRate;
            _baseLength = length;
            _primeSamples = sampleRate * PrimeMs / 1000 * channels;
            _trimAbove = sampleRate * TrimAboveMs / 1000 * channels;
            _trimTo = sampleRate * TrimToMs / 1000 * channels;
            _head = _count = 0;
            _deviceFrames = 0;
            _stampLead = 0;
            _droppedSamples = _underruns = _syncSilenceFrames = _syncDroppedFrames = _syncCorrections = 0;
            _gapSilenceFrames = _gapTrimmedFrames = _discardedSamples = 0;
            _primed = _synced = _draining = _newTimeline = false;
            _protectedSamples = 0;
            _readyEpoch = _epoch;
            _active = true;
        }
    }

    /// <summary>
    /// Closes the queue: later writes are ignored until the next <see cref="Reset"/>. Queued samples are discarded and
    /// counted (<see cref="PcmQueueStatistics.DiscardedSamples"/>).
    /// </summary>
    public void Close()
    {
        lock (_lock)
        {
            _discardedSamples += _count;
            _active = false;
            _draining = _newTimeline = false;
            _protectedSamples = 0;
            _head = _count = 0;
            _deviceFrames = 0;
            _stampLead = 0;
        }
    }

    /// <summary>
    /// The stream ended (PLAYBACK_STOP) while clock pacing holds its audio back: returns true if the held audio should
    /// play out before the device closes (live F1), false if the caller should close now (unsynchronized playback, as
    /// before, or a closed queue). While draining, <see cref="ReadyFrames"/> needs no prime cushion, and
    /// <see cref="TryFinishDrain"/> tells the render thread when everything has been played.
    /// </summary>
    public bool BeginDrain()
    {
        lock (_lock)
        {
            if (!_active || !_synced) return false;
            _draining = true;
            return true;
        }
    }

    /// <summary>True between <see cref="BeginDrain"/> and the end of the drain or a new stream.</summary>
    public bool Draining { get { lock (_lock) return _draining; } }

    /// <summary>
    /// The render thread, on a buffer event while draining: when the queue is empty and the device has played
    /// everything (<paramref name="devicePadding"/> is 0), closes the queue and returns true; the device may then close.
    /// Atomic with <see cref="TryContinue"/>, so a new stream either continues the drain or finds the queue closed.
    /// </summary>
    public bool TryFinishDrain(int devicePadding)
    {
        lock (_lock)
        {
            if (!_draining || _count > 0 || devicePadding > 0) return false;
            _draining = false;
            _active = false;
            _deviceFrames = 0;
            _stampLead = 0;
            return true;
        }
    }

    /// <summary>
    /// A new stream starts (PLAYBACK_START) while the previous one plays out: if the format is the same, the queue and
    /// the device carry on, the held audio keeps its schedule and the new stream's first packet starts a new timeline
    /// after it; returns true. Otherwise (not draining, the drain finished, or another format) returns false and the
    /// caller restarts the device and <see cref="Reset"/>s the queue.
    /// </summary>
    public bool TryContinue(int channels, int sampleRate)
    {
        lock (_lock)
        {
            if (!_draining || !_active || channels != _channels || sampleRate != _rate) return false;
            _draining = false;
            _newTimeline = true;
            _protectedSamples = _count;
            return true;
        }
    }

    /// <summary>Statistics since <see cref="Reset"/>. The renderer logs them when playback closes, never per packet.</summary>
    public PcmQueueStatistics Statistics
    {
        get
        {
            lock (_lock)
                return new(_droppedSamples, _underruns, _syncSilenceFrames, _syncDroppedFrames, _syncCorrections,
                    _gapSilenceFrames, _gapTrimmedFrames, _discardedSamples);
        }
    }

    /// <summary>Samples queued (all channels).</summary>
    public int Count { get { lock (_lock) return _count; } }

    /// <summary>
    /// Frames the device holds that it hasn't played yet (its buffer padding plus its stream latency), as the render
    /// thread last saw them. Synchronized writes count them as already scheduled. The render thread sets it with
    /// <see cref="Transfer"/> whenever it takes audio from the queue; set it directly only when it takes none.
    /// </summary>
    public int DeviceQueuedFrames
    {
        get => Volatile.Read(ref _deviceFrames);
        set => Volatile.Write(ref _deviceFrames, Math.Max(0, value));
    }

    /// <summary>
    /// Queues interleaved PCM. Never blocks beyond a short copy. <paramref name="delayMs"/> is when the first sample
    /// should be heard, in ms from now, or <see cref="Unsynced"/> to play it as soon as possible. <paramref name="gapMs"/>
    /// is how far its timestamp lies past the end of the previous packet's (stamp plus length; negative if they overlap).
    /// Returns the clock correction made for a synchronized write: frames of silence placed before all held audio
    /// (positive) or of audio dropped (negative).
    /// <para>
    /// Synchronized, the queue keeps the held timeline: the source gaps not yet laid out (see <c>_stampLead</c>). The
    /// error of this packet against its delay, less those gaps, is the held timeline being off the guest clock (its
    /// shift). The gaps are laid out as silence between packets once they exceed <see cref="GapJitterMs"/>, so they
    /// are never mistaken for a clock change. When the shift could put any held packet (each with its own pending lead
    /// within <see cref="GapJitterMs"/>) more than <see cref="ToleranceMs"/> off its stamp, it moves all held audio
    /// (silence before it, or its oldest samples dropped), as a new clock base moves queued video frames; after a
    /// correction every held sample is within <see cref="GapJitterMs"/> of its stamp.
    /// </para>
    /// </summary>
    public int Write(ReadOnlySpan<short> interleavedPcm, int delayMs = Unsynced, int gapMs = 0)
    {
        lock (_lock)
        {
            if (!_active || interleavedPcm.IsEmpty) return 0;
            int n = interleavedPcm.Length - interleavedPcm.Length % _channels;
            interleavedPcm = interleavedPcm[..n];
            if (delayMs == Unsynced)
            {
                if (_synced)
                {
                    // Clock pacing was switched off: drop what it held and prime again, so playback settles at the depth
                    // of a fresh stream instead of the 40 ms the trim would leave (live F2). A fill that chose its
                    // frames before this must not take the new, unprimed audio (review E5).
                    DropOldest(_count);
                    _primed = false;
                    _epoch++;
                }
                _synced = false;
                _newTimeline = false;
                _stampLead = 0;
                if (n > _baseLength)
                {
                    _droppedSamples += n - _baseLength;
                    interleavedPcm = interleavedPcm.Slice(n - _baseLength, _baseLength);
                    n = _baseLength;
                }
                DropOldest(_count + n - _baseLength);
                Append(interleavedPcm);
                return 0;
            }

            _synced = true;
            long tolerance = (long)ToleranceMs * _rate / 1000;
            long jitter = (long)GapJitterMs * _rate / 1000;
            long maxFrames = (long)(MaxDelayMs + RingMs) * _rate / 1000;
            // When the packet's first sample should be heard, and when it would be if appended now (frames from now).
            long target = (long)Math.Clamp(delayMs, MinDelayMs, MaxDelayMs) * _rate / 1000;
            long queued = (long)_count / _channels + Volatile.Read(ref _deviceFrames);
            // The held timeline: how far this packet's stamp lies past the end of the queued audio's stamps, made of
            // source gaps not yet laid out as silence. What remains of the error is the held timeline being off the
            // guest clock (a new clock base, drift, an underrun, entering clock pacing): it moves all held audio.
            // Logical time is unbounded here (a 5 s source pause is 5 s of lead); only the samples inserted or removed
            // below are bounded by the ring (review C1).
            _stampLead += (long)gapMs * _rate / 1000;
            bool fresh = _newTimeline || _protectedSamples > 0;
            if (fresh)
            {
                // A stream that follows one still playing out: nothing ties its stamps to the previous stream's held
                // audio, so while any of that is held each packet is placed at its own deadline after the held audio
                // (right after it if that is already past) and no correction moves the previous stream (review E2).
                _newTimeline = false;
                _stampLead = Math.Max(0, target - queued);
            }
            long shift = fresh ? 0 : target - queued - _stampLead;
            // Every held sample is off its stamp by the uncorrected shift plus its own pending lead, which is within
            // ±GapJitterMs (each packet's, not just this one's: review D1). Correcting the shift once it exceeds
            // ToleranceMs − GapJitterMs keeps all of them within ToleranceMs. With nothing held, only this packet's own
            // lead counts. A correction removes the whole shift, leaving each sample its pending lead.
            long pending = Math.Abs(_stampLead) > jitter ? 0 : _stampLead;
            long error = _count > 0 ? shift : shift + pending;
            long limit = _count > 0 ? tolerance - jitter : tolerance;
            int before = 0, correction = 0;
            if (error > limit && shift > 0)
            {
                // Early: silence before everything held, so the held audio and the packet move together.
                before = (int)Math.Min(shift, maxFrames) * _channels;
            }
            else if (error < -limit && shift < 0)
            {
                // Late: drop the oldest held audio. Audio already in the device stays. If nothing is held, give up
                // part of the pending source gap, then cut the start of this packet.
                long late = -shift;
                int fromQueue = (int)Math.Min(late, _count / _channels) * _channels;
                Discard(fromQueue);
                late -= fromQueue / _channels;
                if (late > 0 && _stampLead > 0)
                {
                    long gap = Math.Min(late, _stampLead);
                    _stampLead -= gap;
                    late -= gap;
                }
                int fromPacket = (int)Math.Min(late, n / _channels) * _channels;
                interleavedPcm = interleavedPcm[fromPacket..];
                n = interleavedPcm.Length;
                correction = -((fromQueue + fromPacket) / _channels);
                _syncDroppedFrames -= correction;
                _syncCorrections++;
                queued -= fromQueue / _channels;
            }
            // Lay out the source gap exactly, in full, once it exceeds the stamps' jitter allowance: as silence between
            // the held audio and the packet; an overlap cuts the start of the packet. A lead within the allowance stays
            // pending, so jitter's alternating signs cancel. Laying out all of it keeps every held packet's own phase
            // within ±GapJitterMs, which the correction gate relies on (review E1); an uncorrected shift (within the
            // gate) then applies to this packet as to the rest of the held timeline.
            long betweenFrames = 0;
            if (_stampLead > jitter)
            {
                betweenFrames = _stampLead;
                _stampLead = 0;
            }
            else if (_stampLead < -jitter)
            {
                int overlap = (int)Math.Min(-_stampLead, n / _channels) * _channels;
                interleavedPcm = interleavedPcm[overlap..];
                n = interleavedPcm.Length;
                _stampLead += overlap / _channels;
                _gapTrimmedFrames += overlap / _channels;
            }
            int between = (int)Math.Min(betweenFrames, maxFrames) * _channels;
            EnsureCapacity(_count + before + between + n);
            if (n > _ring.Length)
            {
                _droppedSamples += n - _ring.Length;
                interleavedPcm = interleavedPcm[(n - _ring.Length)..];
                n = _ring.Length;
            }
            // Bounded by the ring only when the growth was capped (a packet longer than RingMs on top of MaxDelayMs).
            int room = Math.Max(0, _ring.Length - n - _count);
            before = Math.Min(before, room);
            between = Math.Min(between, room - before);
            if (before > 0)
            {
                PrependSilence(before);
                correction = before / _channels;
                _syncSilenceFrames += correction;
                _syncCorrections++;
            }
            if (between > 0)
            {
                AppendSilence(between);
                _gapSilenceFrames += between / _channels;
            }
            DropOldest(_count + n - _ring.Length);
            Append(interleavedPcm);
            return correction;
        }
    }

    /// <summary>
    /// Frames ready to play; 0 while (re)priming after an underrun. Unsynchronized, it trims excess latency; synchronized,
    /// the queue holds what the schedule needs.
    /// </summary>
    public int ReadyFrames()
    {
        lock (_lock)
        {
            if (!_primed)
            {
                // A stream that ended gets no more audio: whatever is left plays without waiting for a cushion.
                if (_count < _primeSamples && !(_draining && _count > 0)) return 0;
                _primed = true;
            }
            if (!_synced && _count > _trimAbove)
            {
                // The queue grew (network burst after a stall, or synchronized playback ended); cut back so latency
                // stays low.
                DropOldest(_count - _trimTo);
            }
            _readyEpoch = _epoch;
            return _count / _channels;
        }
    }

    /// <summary>
    /// Removes up to dst.Length samples into <paramref name="dst"/>, zero-filling what the queue no longer holds (a
    /// synchronized write may drop samples between <see cref="ReadyFrames"/> and this call). On starvation, re-primes so
    /// playback resumes with a small cushion.
    /// </summary>
    public void Dequeue(Span<short> dst, bool starving)
    {
        lock (_lock) DequeueCore(dst, starving);
    }

    /// <summary>
    /// The render thread's handoff to the device: <see cref="Dequeue"/>, and under the same lock records that the device
    /// now holds <paramref name="deviceFrames"/> (its padding before this write, plus everything this write gives it,
    /// plus its stream latency). A synchronized <see cref="Write"/> therefore sees each frame either in the queue or in
    /// the device, never in both or neither (review A3). If clock pacing was switched off since the render thread's
    /// <see cref="ReadyFrames"/>, its choice is stale: nothing is taken and <paramref name="dst"/> is silence, so the new,
    /// unprimed audio waits for its prime (review E5). Returns the samples taken; the rest of dst is zero.
    /// </summary>
    public int Transfer(Span<short> dst, bool starving, int deviceFrames)
    {
        lock (_lock)
        {
            int taken;
            if (_readyEpoch != _epoch)
            {
                dst.Clear();
                taken = 0;
            }
            else taken = DequeueCore(dst, starving);
            _deviceFrames = Math.Max(0, deviceFrames);
            return taken;
        }
    }

    /// <summary>The device was reopened: wait for the prime cushion again.</summary>
    public void Unprime()
    {
        lock (_lock) { _primed = false; _deviceFrames = 0; }
    }

    // ---- under _lock ----

    int Wrap(int index) => index >= _ring.Length ? index - _ring.Length : index;

    int DequeueCore(Span<short> dst, bool starving)
    {
        int n = Math.Min(dst.Length, _count);
        int first = Math.Min(n, _ring.Length - _head);
        _ring.AsSpan(_head, first).CopyTo(dst);
        _ring.AsSpan(0, n - first).CopyTo(dst[first..]);
        if (n < dst.Length) dst[n..].Clear();
        _head = Wrap(_head + n);
        _count -= n;
        _protectedSamples = Math.Max(0, _protectedSamples - n);
        if (starving && _primed) { _primed = false; _underruns++; }
        return n;
    }

    /// <summary>Drops the oldest samples, counted as overflow or trim.</summary>
    void DropOldest(int samples)
    {
        if (samples <= 0) return;
        Discard(samples);
        _droppedSamples += samples;
    }

    void Discard(int samples)
    {
        if (samples <= 0) return;
        _head = Wrap(_head + samples);
        _count -= samples;
        _protectedSamples = Math.Max(0, _protectedSamples - samples);
    }

    void Append(ReadOnlySpan<short> pcm)
    {
        int tail = Wrap(_head + _count);
        int first = Math.Min(pcm.Length, _ring.Length - tail);
        pcm[..first].CopyTo(_ring.AsSpan(tail));
        pcm[first..].CopyTo(_ring);
        _count += pcm.Length;
    }

    /// <summary>Silence ahead of everything queued: the held audio plays that much later.</summary>
    void PrependSilence(int samples)
    {
        if (samples <= 0) return;
        _head -= samples;
        if (_head < 0) _head += _ring.Length;
        int first = Math.Min(samples, _ring.Length - _head);
        _ring.AsSpan(_head, first).Clear();
        _ring.AsSpan(0, samples - first).Clear();
        _count += samples;
    }

    void AppendSilence(int samples)
    {
        if (samples <= 0) return;
        int tail = Wrap(_head + _count);
        int first = Math.Min(samples, _ring.Length - tail);
        _ring.AsSpan(tail, first).Clear();
        _ring.AsSpan(0, samples - first).Clear();
        _count += samples;
    }

    /// <summary>Grows the ring (once per larger delay, never per packet) up to the synchronized maximum.</summary>
    void EnsureCapacity(int samples)
    {
        if (samples <= _ring.Length) return;
        int max = (int)((long)(MaxDelayMs + RingMs) * _rate / 1000) * _channels;
        if (_ring.Length >= max) return;
        int length = Math.Min(max, Math.Max(samples, _ring.Length * 2));
        var grown = new short[length];
        int first = Math.Min(_count, _ring.Length - _head);
        _ring.AsSpan(_head, first).CopyTo(grown);
        _ring.AsSpan(0, _count - first).CopyTo(grown.AsSpan(first));
        _ring = grown;
        _head = 0;
    }
}
