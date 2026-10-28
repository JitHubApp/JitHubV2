using Microsoft.Win32.SafeHandles;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

[assembly: DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory | DllImportSearchPath.SafeDirectories)]

namespace MarkdownRenderer.Mermaid;

public enum MermaidNativeRuntimeStatus
{
    Available,
    NotInstalled,
    IncompatibleArchitecture,
    MissingExport,
    IncompatibleAbi,
    LoadFailure,
}

/// <summary>A version of the fixed-width C ABI used to exchange MMIR buffers.</summary>
public readonly record struct MermaidNativeAbiVersion(ushort Major, ushort Minor);

/// <summary>Read-only availability information for the optional native Mermaid engine.</summary>
public sealed record MermaidNativeRuntimeInfo(
    MermaidNativeRuntimeStatus Status,
    MermaidNativeAbiVersion AbiVersion,
    string Message)
{
    public bool IsAvailable => Status == MermaidNativeRuntimeStatus.Available;
}

/// <summary>Safely probes the optional native engine without executing parser or layout work.</summary>
public static class MermaidNativeRuntime
{
    public static MermaidNativeRuntimeInfo Probe() => ProbeCore(null);

    internal static unsafe MermaidNativeRuntimeInfo ProbeCore(string? explicitLibraryPath)
    {
        nint library = 0;
        try
        {
            if (explicitLibraryPath is not null && File.Exists(explicitLibraryPath) &&
                TryReadMachine(explicitLibraryPath, out Architecture architecture) &&
                architecture != RuntimeInformation.ProcessArchitecture)
            {
                return new MermaidNativeRuntimeInfo(
                    MermaidNativeRuntimeStatus.IncompatibleArchitecture,
                    default,
                    $"The native Mermaid engine targets {architecture}, but the process targets {RuntimeInformation.ProcessArchitecture}.");
            }

            const DllImportSearchPath searchPath = DllImportSearchPath.AssemblyDirectory | DllImportSearchPath.SafeDirectories;
            bool loaded = explicitLibraryPath is null
                ? NativeLibrary.TryLoad(NativeMethods.LibraryName, typeof(MermaidNativeRuntime).Assembly, searchPath, out library)
                : NativeLibrary.TryLoad(explicitLibraryPath, out library);
            if (!loaded)
            {
                return new MermaidNativeRuntimeInfo(
                    explicitLibraryPath is not null && File.Exists(explicitLibraryPath)
                        ? MermaidNativeRuntimeStatus.LoadFailure
                        : MermaidNativeRuntimeStatus.NotInstalled,
                    default,
                    explicitLibraryPath is not null && File.Exists(explicitLibraryPath)
                        ? "The selected native Mermaid library is present but cannot be loaded."
                        : "The optional native Mermaid engine is not installed for this process architecture.");
            }

            foreach (string export in NativeMethods.RequiredExports)
            {
                if (!NativeLibrary.TryGetExport(library, export, out _))
                {
                    return new MermaidNativeRuntimeInfo(
                        MermaidNativeRuntimeStatus.MissingExport,
                        default,
                        $"The native Mermaid engine does not export {export}.");
                }
            }

            NativeLibrary.TryGetExport(library, "mmir_get_abi_version", out nint versionAddress);
            var getVersion = (delegate* unmanaged[Cdecl]<uint>)versionAddress;
            uint packedVersion = getVersion();
            var version = new MermaidNativeAbiVersion((ushort)(packedVersion >> 16), (ushort)(packedVersion & 0xffff));
            if (version.Major != NativeMethods.AbiMajor || version.Minor < NativeMethods.AbiMinor)
            {
                return new MermaidNativeRuntimeInfo(
                    MermaidNativeRuntimeStatus.IncompatibleAbi,
                    version,
                    $"Native Mermaid ABI {version.Major}.{version.Minor} is incompatible with required ABI {NativeMethods.AbiMajor}.{NativeMethods.AbiMinor}.");
            }

            return new MermaidNativeRuntimeInfo(
                MermaidNativeRuntimeStatus.Available,
                version,
                "The native Mermaid engine is available.");
        }
        catch (BadImageFormatException)
        {
            return new MermaidNativeRuntimeInfo(
                MermaidNativeRuntimeStatus.IncompatibleArchitecture,
                default,
                "The native Mermaid engine does not match the process architecture.");
        }
        catch (Exception exception) when (exception is DllNotFoundException or EntryPointNotFoundException or FileLoadException)
        {
            return new MermaidNativeRuntimeInfo(
                MermaidNativeRuntimeStatus.LoadFailure,
                default,
                "The optional native Mermaid engine could not be loaded safely.");
        }
        finally
        {
            if (library != 0)
            {
                NativeLibrary.Free(library);
            }
        }
    }

    private static bool TryReadMachine(string path, out Architecture architecture)
    {
        architecture = default;
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
            Span<byte> dosHeader = stackalloc byte[64];
            if (stream.Read(dosHeader) != dosHeader.Length || dosHeader[0] != (byte)'M' || dosHeader[1] != (byte)'Z')
            {
                return false;
            }

            int peOffset = BitConverter.ToInt32(dosHeader[0x3c..]);
            if (peOffset < 64 || peOffset > stream.Length - 6)
            {
                return false;
            }

            stream.Position = peOffset;
            Span<byte> signature = stackalloc byte[6];
            if (stream.Read(signature) != signature.Length ||
                signature[0] != (byte)'P' || signature[1] != (byte)'E' || signature[2] != 0 || signature[3] != 0)
            {
                return false;
            }

            Architecture? detected = BitConverter.ToUInt16(signature[4..]) switch
            {
                0x014c => Architecture.X86,
                0x8664 => Architecture.X64,
                0xaa64 => Architecture.Arm64,
                _ => null,
            };
            if (detected is null)
            {
                return false;
            }

            architecture = detected.Value;
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return false;
        }
    }
}

internal sealed class MermaidEngineHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    internal MermaidEngineHandle(nint value)
        : base(ownsHandle: true) => SetHandle(value);

    protected override bool ReleaseHandle()
    {
        try
        {
            return NativeMethods.EngineRelease(handle) == NativeStatus.Success;
        }
        catch (Exception exception) when (exception is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        {
            return false;
        }
    }
}

internal sealed class MermaidCancellationHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    internal MermaidCancellationHandle(nint value)
        : base(ownsHandle: true) => SetHandle(value);

    internal void Request()
    {
        if (IsInvalid || IsClosed)
        {
            return;
        }

        try
        {
            _ = NativeMethods.CancellationRequest(handle);
        }
        catch (Exception exception) when (exception is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        {
            // Cancellation is best-effort during native teardown. No exception crosses the callback boundary.
        }
    }

    protected override bool ReleaseHandle()
    {
        try
        {
            return NativeMethods.CancellationRelease(handle) == NativeStatus.Success;
        }
        catch (Exception exception) when (exception is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        {
            return false;
        }
    }
}

internal sealed class MermaidBufferHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    internal MermaidBufferHandle(nint value)
        : base(ownsHandle: true) => SetHandle(value);

    protected override bool ReleaseHandle()
    {
        try
        {
            return NativeMethods.BufferRelease(handle) == NativeStatus.Success;
        }
        catch (Exception exception) when (exception is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        {
            return false;
        }
    }
}
