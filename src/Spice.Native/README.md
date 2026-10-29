# Native codec bindings

Build DLLs with `bash native/build.sh x64` before running
`dotnet test tests/Spice.Native.Tests`. Import `native/NativeDlls.targets` from
executable/test projects; the target stages the three DLLs for build and publish,
including Native AOT publish. RuntimeIdentifier `win-arm64` selects ARM64;
otherwise the default is x64. `SpiceNativeArch` can explicitly override it.
Missing DLLs produce build warnings. NativeCodecs.TryLoad probes the executable's
assembly directory and Windows safe DLL search directories and reports load errors.

All native bindings use LibraryImport, cdecl, pointer/primitive arguments, and
fixed spans. Decoder calls serialize with disposal; GLZ calls also serialize with
the shared session window. Disposing a window makes its decoders return false;
the window allocation survives until the last decoder is disposed. Dispose
deterministically; finalizers provide fallback cleanup.

TryDecode rejects nonpositive/overflowing dimensions, mismatched stream dimensions,
negative/short strides, and short output spans. Valid output is top-down BGRA,
and row padding is untouched. Native QUIC/LZ/GLZ errors return false. Output bytes
may be partially updated on failure. Non-RGBA alpha is set to 255; LZ XXXA is an
alpha-only plane and preserves destination RGB. LZ palette row padding is decoded
into reusable scratch before visible pixels are copied. Scratch grows on first
use or larger images, with no managed allocation on warmed calls. JPEG uses tj3
with TJPF_BGRX and normalizes alpha to 255. Constructors can throw when native
libraries are missing or decoder allocation fails; disposed decode calls fail.

AudioDecoder takes `(ushort mode, int channels, int frequency)` and its
`Decode(payload, pcm)` returns samples per channel or -1. The payload excludes
the SPICE PLAYBACK_DATA timestamp. Raw mode reads interleaved signed 16-bit little
endian samples. Opus playback uses two channels and a supported Opus sample rate
(8000, 12000, 16000, 24000, or 48000 Hz). Upstream snd_codec.h defines a 480-sample
encode frame, which is 10 ms at 48 kHz. Decode capacity comes from the supplied
PCM span so valid longer packets also work. No packet-loss concealment is requested
for empty packets. MjpegStreamDecoder delegates each frame to the reusable JPEG
decoder. These wrappers allocate no managed storage per audio/MJPEG call.

Native safety changes and upstream provenance are recorded in native/VENDORED.md.
The deterministic mutation tests are regression tests, not exhaustive fuzzing or
a proof that the upstream codecs are free of memory-safety defects. ARM64 and
application Native AOT runtime execution require separate validation.
