using System.Diagnostics;
using System.Runtime.InteropServices;

namespace MecchaCamouflage.WebHost;

internal sealed class EspMemoryReader : IDisposable
{
    private const uint ProcessVmRead = 0x0010;
    private const uint ProcessQueryInformation = 0x0400;
    private const uint SupportedTimeDateStamp = 0x99410699;
    private const uint SupportedImageSize = 0x0AB00000;
    private const int SkeletonBoneCount = 28;
    private static readonly int?[] WorldPattern =
    [
        0x44, 0x38, 0x2D, null, null, null, null, 0x48, 0x8B, 0x1D, null, null, null, null,
        0x74, null, 0x48, 0x85, 0xDB, 0x74, null, 0x48, 0x8B, 0xCB, 0xE8, null, null, null, null
    ];

    private Process? process;
    private IntPtr handle;
    private long worldPointerAddress;
    private string lastError = "";
    private DateTimeOffset nextAttachAttempt = DateTimeOffset.MinValue;

    public string LastError => lastError;
    public IntPtr GameWindow
    {
        get
        {
            try
            {
                process?.Refresh();
                return process?.MainWindowHandle ?? IntPtr.Zero;
            }
            catch
            {
                return IntPtr.Zero;
            }
        }
    }

    public EspFrame? ReadFrame(string processName)
    {
        if (!EnsureAttached(processName))
            return null;

        var world = ReadPointer(worldPointerAddress);
        if (!ValidPointer(world))
        {
            lastError = "The active game world is not available yet.";
            return null;
        }

        var gameInstance = ReadPointer(world + 0x228);
        var localPlayers = ReadArray(gameInstance + 0x38);
        if (!ValidArray(localPlayers, 1, 8))
            return Fail("The local player list is unavailable.");

        var localPlayer = ReadPointer(localPlayers.Data);
        var controller = ReadPointer(localPlayer + 0x30);
        var localPawn = ReadPointer(controller + 0x350);
        if (!ValidPointer(localPawn))
            localPawn = ReadPointer(controller + 0x2E8);
        var cameraManager = ReadPointer(controller + 0x360);
        var gameState = ReadPointer(world + 0x1B0);
        if (!ValidPointer(controller) || !ValidPointer(cameraManager) || !ValidPointer(gameState))
            return Fail("The current map is still exposing incomplete player data.");

        if (!TryRead(cameraManager + 0x1540, out ViewInfo view) ||
            !Finite(view.Location) || !Finite(view.Rotation) ||
            !float.IsFinite(view.Fov) || view.Fov is < 20 or > 170)
            return Fail("The current camera data is unavailable.");

        var playerArray = ReadArray(gameState + 0x2C0);
        if (!ValidArray(playerArray, 1, 128))
            return Fail("The current player list is unavailable.");

        var localLocation = ReadPawnLocation(localPawn);
        var players = new List<EspPlayer>(playerArray.Count);
        for (var index = 0; index < playerArray.Count; index++)
        {
            var playerState = ReadPointer(playerArray.Data + index * 8L);
            if (!ValidPointer(playerState))
                continue;
            var pawn = ReadPointer(playerState + 0x320);
            if (!ValidPointer(pawn) || pawn == localPawn)
                continue;
            var location = ReadPawnLocation(pawn);
            if (!Finite(location) || IsZero(location))
                continue;

            var name = ReadFString(playerState + 0x340);
            if (string.IsNullOrWhiteSpace(name))
                name = "Player";
            var bones = ReadBones(ReadPointer(pawn + 0x418));
            var distance = Finite(localLocation) && !IsZero(localLocation)
                ? Distance(localLocation, location) / 100.0
                : Distance(view.Location, location) / 100.0;
            players.Add(new EspPlayer(name, location, distance, bones));
        }

        lastError = "";
        return new EspFrame(view, players);
    }

    private bool EnsureAttached(string processName)
    {
        if (process is { HasExited: false } && handle != IntPtr.Zero && worldPointerAddress != 0)
            return true;
        if (DateTimeOffset.UtcNow < nextAttachAttempt)
            return false;

        DisposeProcess();
        var name = Path.GetFileNameWithoutExtension(processName);
        process = Process.GetProcessesByName(name).OrderBy(candidate => candidate.Id).FirstOrDefault();
        if (process is null)
        {
            lastError = $"Waiting for {processName}.";
            nextAttachAttempt = DateTimeOffset.UtcNow.AddSeconds(1);
            return false;
        }

        handle = OpenProcess(ProcessVmRead | ProcessQueryInformation, false, process.Id);
        if (handle == IntPtr.Zero)
        {
            lastError = "The game process could not be opened for the ESP overlay.";
            nextAttachAttempt = DateTimeOffset.UtcNow.AddSeconds(5);
            DisposeProcess();
            return false;
        }

        try
        {
            var module = process.MainModule;
            if (module is null)
                return AttachFailed("The game module could not be inspected.");
            var baseAddress = module.BaseAddress.ToInt64();
            var peOffset = Read<uint>(baseAddress + 0x3C);
            var timestamp = Read<uint>(baseAddress + peOffset + 0x08);
            var imageSize = Read<uint>(baseAddress + peOffset + 0x50);
            if (timestamp != SupportedTimeDateStamp || imageSize != SupportedImageSize)
                return AttachFailed("ESP is disabled for this game build because its player offsets have not been verified.");

            var match = FindPattern(baseAddress, module.ModuleMemorySize, WorldPattern);
            if (match == 0)
                return AttachFailed("The game world signature was not found.");
            var instruction = match + 7;
            var relative = Read<int>(instruction + 3);
            worldPointerAddress = instruction + 7 + relative;
            if (!ValidPointer(worldPointerAddress) || !ValidPointer(ReadPointer(worldPointerAddress)))
                return AttachFailed("The game world pointer was not valid.");
            nextAttachAttempt = DateTimeOffset.MinValue;
            return true;
        }
        catch (Exception exception)
        {
            return AttachFailed("ESP initialization failed: " + exception.Message);
        }
    }

    private long FindPattern(long start, int size, IReadOnlyList<int?> pattern)
    {
        const int chunkSize = 4 * 1024 * 1024;
        var overlap = pattern.Count - 1;
        var offset = 0;
        while (offset < size)
        {
            var requested = Math.Min(chunkSize, size - offset);
            var bytes = ReadBytes(start + offset, requested);
            if (bytes.Length == 0)
            {
                offset += requested;
                continue;
            }
            for (var index = 0; index <= bytes.Length - pattern.Count; index++)
            {
                var matches = true;
                for (var part = 0; part < pattern.Count; part++)
                {
                    if (pattern[part] is int expected && bytes[index + part] != expected)
                    {
                        matches = false;
                        break;
                    }
                }
                if (matches)
                    return start + offset + index;
            }
            offset += Math.Max(1, requested - overlap);
        }
        return 0;
    }

    private Vec3 ReadPawnLocation(long pawn)
    {
        if (!ValidPointer(pawn))
            return default;
        var root = ReadPointer(pawn + 0x1B8);
        return ValidPointer(root) ? Read<Vec3>(root + 0x200) : default;
    }

    private IReadOnlyList<Vec3> ReadBones(long mesh)
    {
        if (!ValidPointer(mesh))
            return [];
        var transforms = ReadArray(mesh + 0x9B8);
        if (!ValidArray(transforms, SkeletonBoneCount, 256))
            transforms = ReadArray(mesh + 0x5F0);
        if (!ValidArray(transforms, SkeletonBoneCount, 256))
            return [];

        var component = ReadTransform(mesh + 0x1E0);
        if (!component.Valid)
            return [];
        var bones = new List<Vec3>(SkeletonBoneCount);
        for (var index = 0; index < SkeletonBoneCount; index++)
        {
            var local = Read<Vec3>(transforms.Data + index * 0x60L + 0x20);
            var world = Transform(component, local);
            if (!Finite(world))
                return [];
            bones.Add(world);
        }
        return bones;
    }

    private Transform ReadTransform(long address)
    {
        var bytes = ReadBytes(address, 0x60);
        if (bytes.Length != 0x60)
            return default;
        var rotation = MemoryMarshal.Read<Quat>(bytes.AsSpan(0x00));
        var translation = MemoryMarshal.Read<Vec3>(bytes.AsSpan(0x20));
        var scale = MemoryMarshal.Read<Vec3>(bytes.AsSpan(0x40));
        var valid = Finite(rotation) && Finite(translation) && Finite(scale) &&
                    Math.Abs(rotation.X) <= 2 && Math.Abs(rotation.Y) <= 2 &&
                    Math.Abs(rotation.Z) <= 2 && Math.Abs(rotation.W) <= 2;
        return new Transform(rotation, translation, scale, valid);
    }

    private static Vec3 Transform(Transform transform, Vec3 position)
    {
        var scaled = new Vec3(
            position.X * transform.Scale.X,
            position.Y * transform.Scale.Y,
            position.Z * transform.Scale.Z);
        var q = transform.Rotation;
        var uv = Cross(new Vec3(q.X, q.Y, q.Z), scaled);
        var uuv = Cross(new Vec3(q.X, q.Y, q.Z), uv);
        return scaled + uv * (2.0 * q.W) + uuv * 2.0 + transform.Translation;
    }

    private string ReadFString(long address)
    {
        var value = Read<NativeString>(address);
        if (!ValidPointer(value.Data) || value.Count is < 1 or > 128 || value.Maximum < value.Count)
            return "";
        var bytes = ReadBytes(value.Data, value.Count * 2);
        if (bytes.Length == 0)
            return "";
        return System.Text.Encoding.Unicode.GetString(bytes).TrimEnd('\0');
    }

    private NativeArray ReadArray(long address) => Read<NativeArray>(address);
    private long ReadPointer(long address) => Read<long>(address);

    private T Read<T>(long address) where T : unmanaged
    {
        var bytes = ReadBytes(address, Marshal.SizeOf<T>());
        return bytes.Length == Marshal.SizeOf<T>() ? MemoryMarshal.Read<T>(bytes) : default;
    }

    private bool TryRead<T>(long address, out T value) where T : unmanaged
    {
        var bytes = ReadBytes(address, Marshal.SizeOf<T>());
        if (bytes.Length != Marshal.SizeOf<T>())
        {
            value = default;
            return false;
        }
        value = MemoryMarshal.Read<T>(bytes);
        return true;
    }

    private byte[] ReadBytes(long address, int count)
    {
        if (handle == IntPtr.Zero || !ValidPointer(address) || count <= 0)
            return [];
        var bytes = new byte[count];
        if (!ReadProcessMemory(handle, new IntPtr(address), bytes, count, out var read) || read.ToInt64() <= 0)
            return [];
        if (read.ToInt64() == count)
            return bytes;
        return bytes[..checked((int)read.ToInt64())];
    }

    private EspFrame? Fail(string message)
    {
        lastError = message;
        return null;
    }

    private bool AttachFailed(string message)
    {
        lastError = message;
        nextAttachAttempt = DateTimeOffset.UtcNow.AddSeconds(10);
        DisposeProcess();
        return false;
    }

    private static bool ValidPointer(long pointer) => pointer is >= 0x10000 and <= 0x00007FFFFFFFFFFF;
    private static bool ValidArray(NativeArray array, int minimum, int maximum) =>
        ValidPointer(array.Data) && array.Count >= minimum && array.Count <= maximum &&
        array.Maximum >= array.Count && array.Maximum <= 4096;
    private static bool Finite(Vec3 value) => double.IsFinite(value.X) && double.IsFinite(value.Y) && double.IsFinite(value.Z);
    private static bool Finite(Rotator value) => double.IsFinite(value.Pitch) && double.IsFinite(value.Yaw) && double.IsFinite(value.Roll);
    private static bool Finite(Quat value) => double.IsFinite(value.X) && double.IsFinite(value.Y) && double.IsFinite(value.Z) && double.IsFinite(value.W);
    private static bool IsZero(Vec3 value) => value.X == 0 && value.Y == 0 && value.Z == 0;
    private static double Distance(Vec3 left, Vec3 right) =>
        Math.Sqrt(Math.Pow(left.X - right.X, 2) + Math.Pow(left.Y - right.Y, 2) + Math.Pow(left.Z - right.Z, 2));
    private static Vec3 Cross(Vec3 left, Vec3 right) => new(
        left.Y * right.Z - left.Z * right.Y,
        left.Z * right.X - left.X * right.Z,
        left.X * right.Y - left.Y * right.X);

    private void DisposeProcess()
    {
        worldPointerAddress = 0;
        if (handle != IntPtr.Zero)
        {
            CloseHandle(handle);
            handle = IntPtr.Zero;
        }
        process?.Dispose();
        process = null;
    }

    public void Dispose()
    {
        DisposeProcess();
        GC.SuppressFinalize(this);
    }

    [StructLayout(LayoutKind.Sequential)]
    internal readonly record struct Vec3(double X, double Y, double Z)
    {
        public static Vec3 operator +(Vec3 left, Vec3 right) => new(left.X + right.X, left.Y + right.Y, left.Z + right.Z);
        public static Vec3 operator *(Vec3 value, double scalar) => new(value.X * scalar, value.Y * scalar, value.Z * scalar);
    }

    [StructLayout(LayoutKind.Sequential)]
    internal readonly record struct Rotator(double Pitch, double Yaw, double Roll);

    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct Quat(double X, double Y, double Z, double W);

    [StructLayout(LayoutKind.Sequential)]
    internal readonly record struct ViewInfo(Vec3 Location, Rotator Rotation, float Fov, float DesiredFov);

    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct NativeArray(long Data, int Count, int Maximum);

    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct NativeString(long Data, int Count, int Maximum);

    private readonly record struct Transform(Quat Rotation, Vec3 Translation, Vec3 Scale, bool Valid);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint desiredAccess, bool inheritHandle, int processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool ReadProcessMemory(
        IntPtr processHandle,
        IntPtr baseAddress,
        [Out] byte[] buffer,
        int size,
        out IntPtr bytesRead);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr handle);
}

internal sealed record EspFrame(EspMemoryReader.ViewInfo View, IReadOnlyList<EspPlayer> Players);
internal sealed record EspPlayer(
    string Name,
    EspMemoryReader.Vec3 Location,
    double DistanceMeters,
    IReadOnlyList<EspMemoryReader.Vec3> Bones);
