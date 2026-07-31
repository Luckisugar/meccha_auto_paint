using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace MecchaCamouflage.Controller;

/// <summary>
/// Performs the same --direct inject sequence as runtime-injector.exe, but
/// inside the already-running host process. Smart App Control often blocks
/// launching the separate injector EXE while still allowing the host and
/// LoadLibrary of the bridge DLL.
/// </summary>
public static class InProcessDirectInjector
{
    private const int RemoteLoadTimeoutMs = 15_000;
    private const int RemoteStartTimeoutMs = 15_000;
    private const int ModuleLookupAttempts = 20;
    private const int ModuleLookupRetryMs = 25;
    private const uint BridgeStartListening = 2;

    public static InjectorResult Inject(BridgeInstance instance, BridgeStartBlockV2 startBlock)
    {
        try
        {
            return InjectCore(instance, startBlock);
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            return Fail(
                (uint)instance.Target.ProcessId,
                startBlock,
                (uint)(ex is Win32Exception w ? w.NativeErrorCode : 0),
                0,
                "in_process_inject_exception:" + ex.GetType().Name + ":" + ex.Message);
        }
    }

    private static InjectorResult InjectCore(BridgeInstance instance, BridgeStartBlockV2 startBlock)
    {
        var pid = (uint)instance.Target.ProcessId;
        var bridgePath = Path.GetFullPath(instance.BridgePath);
        if (!File.Exists(bridgePath))
            return Fail(pid, startBlock, 2, 0, "bridge_file_missing");

        var expectedHash = Convert.FromHexString(instance.ExpectedBridgeHash);
        var actualHash = SHA256.HashData(File.ReadAllBytes(bridgePath));
        if (!expectedHash.AsSpan().SequenceEqual(actualHash))
            return Fail(pid, startBlock, 23 /* ERROR_CRC */, 0, "bridge_hash_mismatch");

        var access =
            Native.PROCESS_CREATE_THREAD |
            Native.PROCESS_QUERY_LIMITED_INFORMATION |
            Native.PROCESS_VM_OPERATION |
            Native.PROCESS_VM_WRITE |
            Native.PROCESS_VM_READ;
        var process = Native.OpenProcess(access, false, pid);
        if (process == IntPtr.Zero)
            return Fail(pid, startBlock, (uint)Marshal.GetLastWin32Error(), 0, "open_process_failed");

        try
        {
            if (!ValidateTargetArchitecture(process, out var archError, out var archDetail))
                return Fail(pid, startBlock, archError, 0, archDetail);

            if (!QueryCreationFileTime(process, out var actualCreation, out var queryError))
                return Fail(pid, startBlock, queryError, 0, "target_creation_time_query_failed");
            if (actualCreation != (ulong)instance.Target.CreationTimeUtcFileTime)
                return Fail(pid, startBlock, 87, 0, "target_identity_mismatch");

            if (!QueryProcessImagePath(process, out var actualExe, out queryError))
                return Fail(pid, startBlock, queryError, 0, "target_image_path_query_failed");
            if (!SamePath(actualExe, instance.Target.ExecutablePath))
                return Fail(pid, startBlock, 87, 0, "target_identity_mismatch");

            var pathBytes = Encoding.Unicode.GetBytes(bridgePath + "\0");
            var remotePath = Native.VirtualAllocEx(process, IntPtr.Zero, (UIntPtr)pathBytes.Length, Native.MEM_COMMIT | Native.MEM_RESERVE, Native.PAGE_READWRITE);
            if (remotePath == IntPtr.Zero)
                return Fail(pid, startBlock, (uint)Marshal.GetLastWin32Error(), 0, "remote_path_alloc_failed");

            try
            {
                if (!WriteRemote(process, remotePath, pathBytes, out var writeError))
                    return Fail(pid, startBlock, writeError, 0, "remote_path_write_failed");

                var loadLibrary = ResolveRemoteLoadLibraryW(pid, out var resolveError);
                if (loadLibrary == IntPtr.Zero)
                    return Fail(pid, startBlock, resolveError, 0, "remote_loadlibrary_resolution_failed");

                var loadThread = Native.CreateRemoteThread(process, IntPtr.Zero, UIntPtr.Zero, loadLibrary, remotePath, 0, out _);
                if (loadThread == IntPtr.Zero)
                    return Fail(pid, startBlock, (uint)Marshal.GetLastWin32Error(), 0, "remote_load_thread_failed");

                try
                {
                    if (!WaitRemoteThread(loadThread, RemoteLoadTimeoutMs, out var loadExit, out var waitError))
                        return Fail(pid, startBlock, waitError, 0, "remote_load_thread_not_finished", indeterminate: true);
                    if (loadExit == 0)
                        return Fail(pid, startBlock, 1114 /* ERROR_DLL_INIT_FAILED */, 0, "remote_loadlibrary_failed");
                }
                finally
                {
                    Native.CloseHandle(loadThread);
                }
            }
            finally
            {
                // Path buffer no longer needed after LoadLibraryW returns.
                Native.VirtualFreeEx(process, remotePath, UIntPtr.Zero, Native.MEM_RELEASE);
            }

            var remoteBase = FindRemoteModuleBase(pid, bridgePath, out var modError);
            if (remoteBase == 0)
                return Fail(pid, startBlock, modError, 0, "exact_bridge_module_not_found");

            var startRva = LocalExportRva(bridgePath, "BridgeStartV2", out var exportError);
            if (startRva == 0)
                return Fail(pid, startBlock, exportError, 0, "bridge_start_export_not_found");

            var startBlockBytes = startBlock.Serialize();
            var remoteBlock = Native.VirtualAllocEx(process, IntPtr.Zero, (UIntPtr)startBlockBytes.Length, Native.MEM_COMMIT | Native.MEM_RESERVE, Native.PAGE_READWRITE);
            if (remoteBlock == IntPtr.Zero)
                return Fail(pid, startBlock, (uint)Marshal.GetLastWin32Error(), 0, "remote_start_block_alloc_failed");

            try
            {
                if (!WriteRemote(process, remoteBlock, startBlockBytes, out var writeError))
                    return Fail(pid, startBlock, writeError, 0, "remote_start_block_write_failed");

                var remoteStart = remoteBase + startRva;
                var startThread = Native.CreateRemoteThread(
                    process,
                    IntPtr.Zero,
                    UIntPtr.Zero,
                    new IntPtr(unchecked((long)remoteStart)),
                    remoteBlock,
                    0,
                    out _);
                if (startThread == IntPtr.Zero)
                    return Fail(pid, startBlock, (uint)Marshal.GetLastWin32Error(), 0, "remote_start_thread_failed");

                uint startExit;
                try
                {
                    if (!WaitRemoteThread(startThread, RemoteStartTimeoutMs, out startExit, out var waitError))
                        return Fail(pid, startBlock, waitError, 0, "remote_start_thread_not_finished", indeterminate: true);
                }
                finally
                {
                    Native.CloseHandle(startThread);
                }

                var returned = new byte[BridgeStartBlockV2.Size];
                if (!Native.ReadProcessMemory(process, remoteBlock, returned, (UIntPtr)returned.Length, out var read) ||
                    read != (UIntPtr)returned.Length)
                {
                    return Fail(pid, startBlock, (uint)Marshal.GetLastWin32Error(), 0, "remote_start_result_read_failed");
                }

                if (!BridgeStartBlockV2.TryDeserialize(returned, out var returnedBlock, out _))
                    return Fail(pid, startBlock, 13 /* ERROR_INVALID_DATA */, 0, "remote_start_result_invalid");

                if (!ReturnedMatchesInput(startBlock, returnedBlock))
                    return Fail(pid, startBlock, 13, returnedBlock.WinsockError, "remote_start_result_invalid");

                if (returnedBlock.ResultState != BridgeStartResultStateV2.Listening ||
                    returnedBlock.BoundPort is 0 or > 65535 ||
                    startExit != 0)
                {
                    return new InjectorResult(
                        false,
                        "failed",
                        BridgeProtocolV2.Version,
                        (int)pid,
                        returnedBlock.InstanceId,
                        Convert.ToHexString(returnedBlock.ExpectedBridgeHash).ToLowerInvariant(),
                        Convert.ToHexString(returnedBlock.ExpectedRuntimeBundleHash).ToLowerInvariant(),
                        (int)returnedBlock.BoundPort,
                        (int)returnedBlock.Win32Error,
                        (int)returnedBlock.WinsockError,
                        "bridge_start_failed");
                }

                return new InjectorResult(
                    true,
                    "listening",
                    BridgeProtocolV2.Version,
                    (int)pid,
                    returnedBlock.InstanceId,
                    Convert.ToHexString(returnedBlock.ExpectedBridgeHash).ToLowerInvariant(),
                    Convert.ToHexString(returnedBlock.ExpectedRuntimeBundleHash).ToLowerInvariant(),
                    (int)returnedBlock.BoundPort,
                    (int)returnedBlock.Win32Error,
                    (int)returnedBlock.WinsockError,
                    "bridge_listening");
            }
            finally
            {
                Native.VirtualFreeEx(process, remoteBlock, UIntPtr.Zero, Native.MEM_RELEASE);
            }
        }
        finally
        {
            Native.CloseHandle(process);
        }
    }

    private static bool ReturnedMatchesInput(BridgeStartBlockV2 input, BridgeStartBlockV2 returned) =>
        returned.ExpectedPid == input.ExpectedPid &&
        returned.InstanceId == input.InstanceId &&
        input.ConnectionToken.AsSpan().SequenceEqual(returned.ConnectionToken) &&
        input.ExpectedBridgeHash.AsSpan().SequenceEqual(returned.ExpectedBridgeHash) &&
        input.ExpectedRuntimeBundleHash.AsSpan().SequenceEqual(returned.ExpectedRuntimeBundleHash) &&
        returned.ProtocolVersion == input.ProtocolVersion;

    private static InjectorResult Fail(
        uint pid,
        BridgeStartBlockV2? block,
        uint win32,
        uint winsock,
        string detail,
        bool indeterminate = false) =>
        new(
            false,
            indeterminate ? "indeterminate_timeout" : "failed",
            BridgeProtocolV2.Version,
            pid == 0 ? null : (int)pid,
            block?.InstanceId,
            block is null ? null : Convert.ToHexString(block.ExpectedBridgeHash).ToLowerInvariant(),
            block is null ? null : Convert.ToHexString(block.ExpectedRuntimeBundleHash).ToLowerInvariant(),
            null,
            (int)win32,
            (int)winsock,
            detail);

    private static bool ValidateTargetArchitecture(IntPtr process, out uint error, out string detail)
    {
        error = 0;
        detail = "";
        if (!Environment.Is64BitProcess)
        {
            error = 193; // ERROR_BAD_EXE_FORMAT
            detail = "architecture_mismatch";
            return false;
        }

        // Prefer IsWow64Process2 when available.
        var wow64 = Native.IsWow64Process(process, out var isWow64);
        if (!wow64)
        {
            error = (uint)Marshal.GetLastWin32Error();
            detail = "architecture_validation_failed";
            return false;
        }
        if (isWow64)
        {
            error = 193;
            detail = "architecture_mismatch";
            return false;
        }
        return true;
    }

    private static bool QueryCreationFileTime(IntPtr process, out ulong creation, out uint error)
    {
        creation = 0;
        error = 0;
        if (!Native.GetProcessTimes(process, out var create, out _, out _, out _))
        {
            error = (uint)Marshal.GetLastWin32Error();
            return false;
        }
        creation = unchecked((ulong)create);
        return true;
    }

    private static bool QueryProcessImagePath(IntPtr process, out string path, out uint error)
    {
        path = "";
        error = 0;
        var buffer = new StringBuilder(1024);
        var size = (uint)buffer.Capacity;
        if (!Native.QueryFullProcessImageNameW(process, 0, buffer, ref size))
        {
            error = (uint)Marshal.GetLastWin32Error();
            return false;
        }
        path = buffer.ToString(0, (int)size);
        return true;
    }

    private static bool SamePath(string left, string right)
    {
        try
        {
            return string.Equals(Path.GetFullPath(left), Path.GetFullPath(right), StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
        }
    }

    private static bool WriteRemote(IntPtr process, IntPtr remote, byte[] data, out uint error)
    {
        error = 0;
        if (!Native.WriteProcessMemory(process, remote, data, (UIntPtr)data.Length, out var written) || written != (UIntPtr)data.Length)
        {
            error = (uint)Marshal.GetLastWin32Error();
            return false;
        }
        return true;
    }

    private static bool WaitRemoteThread(IntPtr thread, int timeoutMs, out uint exitCode, out uint error)
    {
        exitCode = 0;
        error = 0;
        var waited = Native.WaitForSingleObject(thread, (uint)timeoutMs);
        if (waited != 0) // WAIT_OBJECT_0
        {
            error = waited == 0xFFFFFFFF ? (uint)Marshal.GetLastWin32Error() : 258; // WAIT_TIMEOUT
            return false;
        }
        if (!Native.GetExitCodeThread(thread, out exitCode))
            error = (uint)Marshal.GetLastWin32Error();
        return true;
    }

    private static IntPtr ResolveRemoteLoadLibraryW(uint pid, out uint error)
    {
        error = 0;
        var kernel32 = Native.GetModuleHandleW("kernel32.dll");
        if (kernel32 == IntPtr.Zero)
        {
            error = (uint)Marshal.GetLastWin32Error();
            return IntPtr.Zero;
        }
        var localLoad = Native.GetProcAddress(kernel32, "LoadLibraryW");
        if (localLoad == IntPtr.Zero)
        {
            error = (uint)Marshal.GetLastWin32Error();
            return IntPtr.Zero;
        }

        // Resolve which module actually owns LoadLibraryW (kernel32 or KernelBase).
        if (!Native.GetModuleHandleExW(
                Native.GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS | Native.GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT,
                localLoad,
                out var owner) ||
            owner == IntPtr.Zero)
        {
            error = (uint)Marshal.GetLastWin32Error();
            return IntPtr.Zero;
        }

        var ownerPathBuf = new StringBuilder(Native.MAX_PATH);
        if (Native.GetModuleFileNameW(owner, ownerPathBuf, Native.MAX_PATH) == 0)
        {
            error = (uint)Marshal.GetLastWin32Error();
            return IntPtr.Zero;
        }
        var ownerPath = Path.GetFullPath(ownerPathBuf.ToString());
        var rva = (ulong)(localLoad.ToInt64() - owner.ToInt64());
        if (localLoad.ToInt64() < owner.ToInt64())
        {
            error = 487; // ERROR_INVALID_ADDRESS
            return IntPtr.Zero;
        }

        var remoteBase = FindRemoteModuleBase(pid, ownerPath, out error);
        if (remoteBase == 0)
            return IntPtr.Zero;
        return new IntPtr(unchecked((long)(remoteBase + rva)));
    }

    private static ulong FindRemoteModuleBase(uint pid, string expectedPath, out uint error)
    {
        error = 0;
        expectedPath = Path.GetFullPath(expectedPath);
        for (var attempt = 0; attempt < ModuleLookupAttempts; attempt++)
        {
            var snapshot = Native.CreateToolhelp32Snapshot(Native.TH32CS_SNAPMODULE | Native.TH32CS_SNAPMODULE32, pid);
            if (snapshot == Native.INVALID_HANDLE_VALUE)
            {
                error = (uint)Marshal.GetLastWin32Error();
                Thread.Sleep(ModuleLookupRetryMs);
                continue;
            }

            try
            {
                var entry = new Native.MODULEENTRY32W { dwSize = (uint)Marshal.SizeOf<Native.MODULEENTRY32W>() };
                if (!Native.Module32FirstW(snapshot, ref entry))
                {
                    error = (uint)Marshal.GetLastWin32Error();
                }
                else
                {
                    do
                    {
                        var modulePath = entry.szExePath;
                        if (!string.IsNullOrEmpty(modulePath) && SamePath(modulePath, expectedPath))
                            return (ulong)entry.modBaseAddr.ToInt64();
                    } while (Native.Module32NextW(snapshot, ref entry));
                    error = 126; // ERROR_MOD_NOT_FOUND
                }
            }
            finally
            {
                Native.CloseHandle(snapshot);
            }
            Thread.Sleep(ModuleLookupRetryMs);
        }
        return 0;
    }

    private static ulong LocalExportRva(string dllPath, string exportName, out uint error)
    {
        error = 0;
        var local = Native.LoadLibraryExW(dllPath, IntPtr.Zero, Native.DONT_RESOLVE_DLL_REFERENCES);
        if (local == IntPtr.Zero)
        {
            error = (uint)Marshal.GetLastWin32Error();
            return 0;
        }
        try
        {
            var export = Native.GetProcAddress(local, exportName);
            if (export == IntPtr.Zero)
            {
                error = (uint)Marshal.GetLastWin32Error();
                return 0;
            }
            return (ulong)(export.ToInt64() - local.ToInt64());
        }
        finally
        {
            Native.FreeLibrary(local);
        }
    }

    private static class Native
    {
        public const int MAX_PATH = 260;
        public const uint PROCESS_CREATE_THREAD = 0x0002;
        public const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
        public const uint PROCESS_VM_OPERATION = 0x0008;
        public const uint PROCESS_VM_READ = 0x0010;
        public const uint PROCESS_VM_WRITE = 0x0020;
        public const uint MEM_COMMIT = 0x1000;
        public const uint MEM_RESERVE = 0x2000;
        public const uint MEM_RELEASE = 0x8000;
        public const uint PAGE_READWRITE = 0x04;
        public const uint TH32CS_SNAPMODULE = 0x00000008;
        public const uint TH32CS_SNAPMODULE32 = 0x00000010;
        public const uint DONT_RESOLVE_DLL_REFERENCES = 0x00000001;
        public const uint GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS = 0x00000004;
        public const uint GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT = 0x00000002;
        public static readonly IntPtr INVALID_HANDLE_VALUE = new(-1);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern IntPtr OpenProcess(uint dwDesiredAccess, bool bInheritHandle, uint dwProcessId);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool CloseHandle(IntPtr hObject);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern IntPtr VirtualAllocEx(IntPtr hProcess, IntPtr lpAddress, UIntPtr dwSize, uint flAllocationType, uint flProtect);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool VirtualFreeEx(IntPtr hProcess, IntPtr lpAddress, UIntPtr dwSize, uint dwFreeType);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool WriteProcessMemory(IntPtr hProcess, IntPtr lpBaseAddress, byte[] lpBuffer, UIntPtr nSize, out UIntPtr lpNumberOfBytesWritten);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool ReadProcessMemory(IntPtr hProcess, IntPtr lpBaseAddress, [Out] byte[] lpBuffer, UIntPtr nSize, out UIntPtr lpNumberOfBytesRead);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern IntPtr CreateRemoteThread(
            IntPtr hProcess,
            IntPtr lpThreadAttributes,
            UIntPtr dwStackSize,
            IntPtr lpStartAddress,
            IntPtr lpParameter,
            uint dwCreationFlags,
            out uint lpThreadId);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern uint WaitForSingleObject(IntPtr hHandle, uint dwMilliseconds);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool GetExitCodeThread(IntPtr hThread, out uint lpExitCode);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool GetProcessTimes(IntPtr hProcess, out long creation, out long exit, out long kernel, out long user);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        public static extern bool QueryFullProcessImageNameW(IntPtr hProcess, uint dwFlags, StringBuilder lpExeName, ref uint lpdwSize);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool IsWow64Process(IntPtr hProcess, out bool Wow64Process);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern IntPtr GetModuleHandleW(string lpModuleName);

        [DllImport("kernel32.dll", CharSet = CharSet.Ansi, SetLastError = true, ExactSpelling = true)]
        public static extern IntPtr GetProcAddress(IntPtr hModule, string procName);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool GetModuleHandleExW(uint dwFlags, IntPtr lpModuleName, out IntPtr phModule);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern uint GetModuleFileNameW(IntPtr hModule, StringBuilder lpFilename, int nSize);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern IntPtr CreateToolhelp32Snapshot(uint dwFlags, uint th32ProcessID);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern bool Module32FirstW(IntPtr hSnapshot, ref MODULEENTRY32W lpme);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern bool Module32NextW(IntPtr hSnapshot, ref MODULEENTRY32W lpme);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern IntPtr LoadLibraryExW(string lpFileName, IntPtr hFile, uint dwFlags);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool FreeLibrary(IntPtr hModule);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct MODULEENTRY32W
        {
            public uint dwSize;
            public uint th32ModuleID;
            public uint th32ProcessID;
            public uint GlblcntUsage;
            public uint ProccntUsage;
            public IntPtr modBaseAddr;
            public uint modBaseSize;
            public IntPtr hModule;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
            public string szModule;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
            public string szExePath;
        }
    }
}
