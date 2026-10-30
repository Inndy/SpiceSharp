using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Media.MediaFoundation;
using Windows.Win32.System.Com;

namespace SpiceViewer.Video;

/// <summary>
/// Media Foundation calls whose failure codes are normal results (docs/decisions/0037). CsWin32's wrappers throw on any
/// failing HRESULT, but <c>ProcessOutput</c> answers MF_E_TRANSFORM_NEED_MORE_INPUT on every frame, a missing attribute
/// answers MF_E_ATTRIBUTENOTFOUND and the end of a type list MF_E_NO_MORE_TYPES; an exception per frame would allocate.
/// These call the same vtable slots as the generated wrappers (slot = the wrapper's <c>_N</c> suffix − 1, from
/// mftransform.h and mfobjects.h) and return the HRESULT.
/// </summary>
static unsafe class Mf
{
    /// <summary>MF_VERSION: MF_SDK_VERSION 0x0002 &lt;&lt; 16 | MF_API_VERSION 0x0070 (mfapi.h).</summary>
    public const uint Version = 0x0002_0070;
    /// <summary>MFSTARTUP_LITE: no socket library (mfapi.h).</summary>
    public const uint StartupLite = 1;
    public static readonly HRESULT E_NO_MORE_TYPES = unchecked((HRESULT)(int)0xC00D36B9);
    public static readonly HRESULT E_ATTRIBUTENOTFOUND = unchecked((HRESULT)(int)0xC00D36E6);
    /// <summary>_MFT_OUTPUT_STREAM_INFO_FLAGS: the MFT allocates its output samples (D3D11 decoding does).</summary>
    public const uint OutputProvidesSamples = 0x100;
    public const uint OutputCanProvideSamples = 0x200;

    static void** Vtbl(void* p) => *(void***)p;

    public static HRESULT ProcessInput(IMFTransform* t, IMFSample* sample) =>
        ((delegate* unmanaged[Stdcall]<IMFTransform*, uint, IMFSample*, uint, HRESULT>)Vtbl(t)[24])(t, 0, sample, 0);

    public static HRESULT ProcessOutput(IMFTransform* t, MFT_OUTPUT_DATA_BUFFER* buffer, uint* status) =>
        ((delegate* unmanaged[Stdcall]<IMFTransform*, uint, uint, MFT_OUTPUT_DATA_BUFFER*, uint*, HRESULT>)Vtbl(t)[25])(t, 0, 1, buffer, status);

    public static HRESULT GetOutputAvailableType(IMFTransform* t, uint index, IMFMediaType** type) =>
        ((delegate* unmanaged[Stdcall]<IMFTransform*, uint, uint, IMFMediaType**, HRESULT>)Vtbl(t)[14])(t, 0, index, type);

    public static HRESULT ProcessMessage(IMFTransform* t, MFT_MESSAGE_TYPE message, nuint param) =>
        ((delegate* unmanaged[Stdcall]<IMFTransform*, MFT_MESSAGE_TYPE, nuint, HRESULT>)Vtbl(t)[23])(t, message, param);

    /// <summary>IMFAttributes::GetUINT32 on any attribute store (media types and activation objects derive from it).</summary>
    public static bool TryGetUInt32(void* attributes, in Guid key, out uint value)
    {
        uint v = 0;
        fixed (Guid* k = &key)
        {
            var hr = ((delegate* unmanaged[Stdcall]<void*, Guid*, uint*, HRESULT>)Vtbl(attributes)[7])(attributes, k, &v);
            value = v;
            return hr.Succeeded;
        }
    }

    public static bool TryGetUInt64(void* attributes, in Guid key, out ulong value)
    {
        ulong v = 0;
        fixed (Guid* k = &key)
        {
            var hr = ((delegate* unmanaged[Stdcall]<void*, Guid*, ulong*, HRESULT>)Vtbl(attributes)[8])(attributes, k, &v);
            value = v;
            return hr.Succeeded;
        }
    }

    public static bool TryGetGuid(void* attributes, in Guid key, out Guid value)
    {
        Guid v = default;
        fixed (Guid* k = &key)
        {
            var hr = ((delegate* unmanaged[Stdcall]<void*, Guid*, Guid*, HRESULT>)Vtbl(attributes)[10])(attributes, k, &v);
            value = v;
            return hr.Succeeded;
        }
    }

    /// <summary>IMFAttributes::GetBlob into a fixed-size struct (exactly its size), e.g. MF_MT_MINIMUM_DISPLAY_APERTURE.</summary>
    public static bool TryGetBlob<T>(void* attributes, in Guid key, out T value) where T : unmanaged
    {
        T v = default;
        uint size = 0;
        fixed (Guid* k = &key)
        {
            var hr = ((delegate* unmanaged[Stdcall]<void*, Guid*, byte*, uint, uint*, HRESULT>)Vtbl(attributes)[15])(attributes, k, (byte*)&v, (uint)sizeof(T), &size);
            value = v;
            return hr.Succeeded && size == sizeof(T);
        }
    }

    /// <summary>A friendly name for logs (control path only), or null.</summary>
    public static string? FriendlyName(IMFActivate* activate)
    {
        try
        {
            var key = PInvoke.MFT_FRIENDLY_NAME_Attribute;
            PWSTR name;
            uint length;
            activate->GetAllocatedString(&key, &name, &length);
            try { return new string(name.Value, 0, (int)length); }
            finally { PInvoke.CoTaskMemFree(name.Value); }
        }
        catch (Exception) { return null; }
    }

    /// <summary>The reference count without changing it: AddRef, then Release's result.</summary>
    public static uint RefCount(IUnknown* p)
    {
        p->AddRef();
        return p->Release();
    }

    public static void Release<T>(ref T* p) where T : unmanaged
    {
        if (p is null) return;
        ((IUnknown*)p)->Release();
        p = null;
    }

    /// <summary>Packs a width and height as MF_MT_FRAME_SIZE does: width in the high 32 bits.</summary>
    public static ulong PackSize(int width, int height) => (ulong)(uint)width << 32 | (uint)height;
}
