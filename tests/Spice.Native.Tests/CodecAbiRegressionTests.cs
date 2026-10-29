using System.Reflection;
using System.Runtime.InteropServices;
using Spice.Native;

namespace Spice.Native.Tests;

public sealed unsafe class CodecAbiRegressionTests
{
    static bool Probe(nint library, out string? error)
    {
        object?[] args = [library, null];
        bool result = (bool)typeof(NativeCodecs).GetMethod("AbiMatches", BindingFlags.Static | BindingFlags.NonPublic)!
            .Invoke(null, args)!;
        error = (string?)args[1];
        return result;
    }

    [Fact]
    public void RebuiltCodecDeclaresTheExpectedAbiAndPassesThePublicLoaderProbe()
    {
        nint library = NativeLibrary.Load("spicecodec.dll", typeof(NativeCodecs).Assembly,
            DllImportSearchPath.AssemblyDirectory | DllImportSearchPath.SafeDirectories);
        try
        {
            Assert.True(NativeLibrary.TryGetExport(library, "sc_abi_version", out nint export));
            Assert.Equal(2, ((delegate* unmanaged[Cdecl]<int>)export)());
            Assert.Equal(2, NativeCodecs.SpiceCodecAbi);
            Assert.True(Probe(library, out string? error));
            Assert.Null(error);
            Assert.Null(NativeCodecs.SpiceCodecError);
            Assert.True(NativeCodecs.TryLoad(out error), error);
            Assert.Null(error);
        }
        finally { NativeLibrary.Free(library); }
    }

    [Fact]
    public void MissingVersionExportIsRejectedAsLegacyAbiWithoutCallingAnyDecoder()
    {
        // A real module handle with no sc_abi_version export exercises legacy-DLL rejection safely.
        // The live spicecodec.dll is never replaced and its process-wide cached probe is never mutated.
        nint library = NativeLibrary.Load("kernel32.dll");
        try
        {
            Assert.False(NativeLibrary.TryGetExport(library, "sc_abi_version", out _));
            Assert.False(Probe(library, out string? error));
            Assert.Contains("interface version 1", error);
            Assert.Contains("needs 2", error);
        }
        finally { NativeLibrary.Free(library); }
    }
}
