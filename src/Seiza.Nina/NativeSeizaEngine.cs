using System.Reflection;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;

namespace Seiza.Nina;

internal sealed class NativeSeizaEngine : ISeizaEngine
{
    internal static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    private readonly EngineHandle handle;

    public NativeSeizaEngine(SolveOptions options)
    {
        options.Validate();
        var pointer = NativeMethods.Open(JsonSerializer.Serialize(new { options.CatalogPath, options.IndexPath }, Json), out var error);
        if (pointer == 0)
        {
            throw new InvalidOperationException($"Seiza could not open its catalogs: {ReadAndFree(error)}");
        }

        if (error != 0) NativeMethods.FreeString(error);
        handle = new(pointer);
    }

    public unsafe SolveResponse Solve(ushort[] pixels, int width, int height, SolveRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var signal = new CancelHandle(NativeMethods.CreateCancel());
        if (signal.IsInvalid) throw new InvalidOperationException("Seiza could not allocate cancellation state.");
        using var registration = cancellationToken.UnsafeRegister(static state => ((CancelHandle)state!).Cancel(), signal);
        nint result;
        fixed (ushort* pointer = pixels)
        {
            result = NativeMethods.Solve(handle, pointer, (nuint)pixels.Length, (uint)width, (uint)height,
                JsonSerializer.Serialize(request, Json), signal);
        }

        var json = ReadAndFree(result);
        cancellationToken.ThrowIfCancellationRequested();
        return JsonSerializer.Deserialize<SolveResponse>(json, Json) ?? throw new InvalidDataException("Seiza returned no solve response.");
    }

    private static string ReadAndFree(nint value)
    {
        if (value == 0) return "No native response";
        try { return Marshal.PtrToStringUTF8(value) ?? "Invalid native response"; }
        finally { NativeMethods.FreeString(value); }
    }

    public void Dispose() => handle.Dispose();
}

internal sealed class EngineHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    internal EngineHandle(nint value) : base(true) => SetHandle(value);

    protected override bool ReleaseHandle() { NativeMethods.Close(handle); return true; }
}

internal sealed class CancelHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    internal CancelHandle(nint value) : base(true) => SetHandle(value);

    internal void Cancel() => NativeMethods.SetCancel(DangerousGetHandle());

    protected override bool ReleaseHandle() { NativeMethods.FreeCancel(handle); return true; }
}

internal static class NativeMethods
{
    private const string Library = "seiza_nina_native";

    static NativeMethods()
    {
        NativeLibrary.SetDllImportResolver(typeof(NativeMethods).Assembly, Resolve);
    }

    private static nint Resolve(string name, Assembly assembly, DllImportSearchPath? searchPath)
    {
        if (name != Library) return 0;
        // Resolve relative to the plugin even inside NINA's custom AssemblyLoadContext.
        return NativeLibrary.Load(Path.Combine(Path.GetDirectoryName(assembly.Location)!, Library + ".dll"));
    }

    [DllImport(Library, EntryPoint = "seiza_nina_open", CallingConvention = CallingConvention.Cdecl)]
    internal static extern nint Open([MarshalAs(UnmanagedType.LPUTF8Str)] string options, out nint error);

    [DllImport(Library, EntryPoint = "seiza_nina_solve", CallingConvention = CallingConvention.Cdecl)]
    internal static extern unsafe nint Solve(EngineHandle engine, ushort* pixels, nuint length, uint width, uint height,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string request, CancelHandle cancel);

    [DllImport(Library, EntryPoint = "seiza_nina_close", CallingConvention = CallingConvention.Cdecl)]
    internal static extern void Close(nint engine);

    [DllImport(Library, EntryPoint = "seiza_nina_string_free", CallingConvention = CallingConvention.Cdecl)]
    internal static extern void FreeString(nint value);

    [DllImport(Library, EntryPoint = "seiza_nina_cancel_new", CallingConvention = CallingConvention.Cdecl)]
    internal static extern nint CreateCancel();

    [DllImport(Library, EntryPoint = "seiza_nina_cancel_set", CallingConvention = CallingConvention.Cdecl)]
    internal static extern void SetCancel(nint signal);

    [DllImport(Library, EntryPoint = "seiza_nina_cancel_free", CallingConvention = CallingConvention.Cdecl)]
    internal static extern void FreeCancel(nint signal);
}
