using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

const uint ProcessVmRead = 0x0010;
const uint ProcessQueryInformation = 0x0400;
const ulong WorldChrManOffset = 0x3D65F88;
const ulong PlayersOffset = 0x10EF8;
const ulong ChrInsModulesOffset = 0x190;
const ulong ChrModulesPhysicsOffset = 0x68;
const ulong ChrPhysicsLocalPositionOffset = 0x70;
const ulong ChrInsChrCtrlOffset = 0x58;
const ulong ChrCtrlModelMatrixOffset = 0x230;
const ulong ModelMatrixTranslationOffset = 0x30;
const ulong FieldAreaGameRendOffset = 0x20;
const ulong GameRendPlayerCameraOffset = 0x20;
const ulong GameRendDebugCameraOffset = 0xD0;
const ulong CameraMatrixOffset = 0x10;
const ulong CameraPositionOffset = 0x40;
const ulong CameraRenderDistanceOffset = 0x5C;

var process = Process.GetProcessesByName("eldenring").FirstOrDefault()
    ?? throw new InvalidOperationException("eldenring.exe is not running.");
var module = process.MainModule ?? throw new InvalidOperationException("Main module unavailable.");
var handle = OpenProcess(ProcessVmRead | ProcessQueryInformation, false, process.Id);
if (handle == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());

try
{
    var moduleBase = unchecked((ulong)module.BaseAddress.ToInt64());
    var image = Read(handle, moduleBase, module.ModuleMemorySize);
    var fieldInstruction = FindPattern(image,
        "48 8B 3D ?? ?? ?? ?? 49 8B D8 48 8B F2 4C 8B F1 48 85 FF");
    if (fieldInstruction < 0) throw new InvalidOperationException("FieldArea signature missing.");

    var displacement = BitConverter.ToInt32(image, fieldInstruction + 3);
    var fieldGlobal = moduleBase + (ulong)(fieldInstruction + 7 + displacement);
    var fieldArea = ReadUInt64(handle, fieldGlobal);
    var gameRend = ReadUInt64(handle, fieldArea + FieldAreaGameRendOffset);
    var playerCamera = ReadUInt64(handle, gameRend + GameRendPlayerCameraOffset);
    var debugCamera = ReadUInt64(handle, gameRend + GameRendDebugCameraOffset);

    var world = ReadUInt64(handle, moduleBase + WorldChrManOffset);
    var worldFinder = FindPattern(image, "48 8B FA 0F 11 41 70 48 8B 05 ?? ?? ?? ??");
    if (worldFinder >= 0)
    {
        var worldDisp = BitConverter.ToInt32(image, worldFinder + 10);
        var worldGlobal = moduleBase + (ulong)(worldFinder + 14 + worldDisp);
        Console.WriteLine($"WORLD AOB   global=0x{worldGlobal:X} offset=0x{worldGlobal-moduleBase:X} ptr=0x{ReadUInt64(handle, worldGlobal):X}");
    }
    var players = ReadUInt64(handle, world + PlayersOffset);
    var player = ReadUInt64(handle, players);
    var modules = ReadUInt64(handle, player + ChrInsModulesOffset);
    var physics = ReadUInt64(handle, modules + ChrModulesPhysicsOffset);
    var localPlayerPosition = ReadVector3(handle, physics + ChrPhysicsLocalPositionOffset);
    var chrCtrl = ReadUInt64(handle, player + ChrInsChrCtrlOffset);
    var playerPosition = ReadVector3(handle, chrCtrl + ChrCtrlModelMatrixOffset + ModelMatrixTranslationOffset);
    var cameraPosition = ReadVector3(handle, debugCamera + CameraPositionOffset);

    Console.WriteLine($"MODULE      0x{moduleBase:X}");
    Console.WriteLine($"FIELD AREA  0x{fieldArea:X}");
    Console.WriteLine($"GAME REND   0x{gameRend:X}");
    Console.WriteLine($"CAM MODE    {BitConverter.ToInt32(Read(handle, gameRend + 0xC8, 4), 0)}");
    Console.WriteLine($"WORLD       0x{world:X}");
    Console.WriteLine($"PLAYER PTR  0x{player:X}");
    Console.WriteLine($"CHR CTRL    0x{chrCtrl:X}");
    Console.WriteLine($"MODEL POS   0x{chrCtrl + ChrCtrlModelMatrixOffset + ModelMatrixTranslationOffset:X}");
    Console.WriteLine($"PHYSICS     0x{physics:X}");
    Console.WriteLine($"LOCAL POS   0x{physics + ChrPhysicsLocalPositionOffset:X}");
    Console.WriteLine($"DEBUG CAM   0x{debugCamera:X}");
    Console.WriteLine($"PLAYER CAM  0x{playerCamera:X}");
    Console.WriteLine($"PLAYER DRAW {BitConverter.ToSingle(Read(handle, playerCamera + CameraRenderDistanceOffset, 4), 0):0.###}");
    Console.WriteLine($"DEBUG DRAW  {BitConverter.ToSingle(Read(handle, debugCamera + CameraRenderDistanceOffset, 4), 0):0.###}");
    Console.WriteLine($"CAM POS     0x{debugCamera + CameraPositionOffset:X}");
    Console.WriteLine($"PLAYER      {Format(playerPosition)}");
    Console.WriteLine($"PLAYER LOCAL{Format(localPlayerPosition)}");
    Console.WriteLine($"CAMERA      {Format(cameraPosition)}");
    foreach (var label in new[] { "W_ItemSummonBuddy", "W_BuddyGenerate", "W_BuddyGenerate2" })
    {
        var needle = System.Text.Encoding.ASCII.GetBytes(label + "\0");
        var stringIndex = FindBytes(image, needle);
        if (stringIndex < 0) continue;
        var stringAddress = moduleBase + (ulong)stringIndex;
        var refs = FindRipRelativeReferences(image, moduleBase, stringAddress).Take(16).ToArray();
        Console.WriteLine($"{label} STR=0x{stringAddress:X} REFS={string.Join(',', refs.Select(v => $"0x{v:X}"))}");
    }
    Console.WriteLine($"BUDDY @18498 0x{ReadUInt64(handle, world + 0x18498):X}");
    for (ulong offset = 0x183C0; offset <= 0x18580; offset += 8)
    {
        var candidate = ReadUInt64(handle, world + offset);
        if (candidate < 0x10000 || candidate > 0x00007FFFFFFFFFFF) continue;
        try
        {
            var value20 = BitConverter.ToInt32(Read(handle, candidate + 0x20, 4), 0);
            Console.WriteLine($"BUDDY CAND +0x{offset:X} -> 0x{candidate:X} [+20]={value20}");
        }
        catch { }
    }

    var target = ResolveLockedTarget(handle, world, player, image, moduleBase);
    if (target != 0)
    {
        var cadence = Read(handle, target + 0xB4, 12);
        Console.WriteLine($"TARGET      0x{target:X} local={BitConverter.ToUInt32(Read(handle, target + 0x08, 4), 0)} param={BitConverter.ToInt32(Read(handle, target + 0x60, 4), 0)}");
        Console.WriteLine($"CADENCE     selected={BitConverter.ToInt32(cadence, 0)} override={BitConverter.ToInt32(cadence, 4)} secondary={BitConverter.ToInt32(cadence, 8)}");
    }
    else Console.WriteLine("TARGET      none");
    ReportZeroCadenceOverrides(handle, world);
    ReportCloneCandidates(handle, world, player);

    ReportPatchState(
        image,
        "VISUAL LOD",
        "0F 28 F0 F3 0F 10 87 ?? ?? ?? ?? 0F 57 C9 0F 2F C1 76 0B",
        "0F 57 F6 F3 0F 10 87 ?? ?? ?? ?? 0F 57 C9 0F 2F C1 76 0B");
    ReportPatchState(
        image,
        "FULL-RATE ANIMATION",
        "0F 28 C6 E8 ?? ?? ?? ?? 0F 28 F8 0F 28 C6 E8 ?? ?? ?? ?? F3 0F 5E F8 0F 28 CF F3 41 0F 5E 4C 24 54 41 0F 28 D1 F3 41 0F 59 54 24 58 0F 57 15 ?? ?? ?? ??",
        "0F 28 C6 E8 ?? ?? ?? ?? 0F 28 F8 0F 28 C6 E8 ?? ?? ?? ?? F3 0F 5E F8 0F 28 CF 0F 57 C9 66 0F EF C9 41 0F 28 D1 F3 41 0F 59 54 24 58 0F 57 15 ?? ?? ?? ??");

    if (args.Contains("--watch-target", StringComparer.OrdinalIgnoreCase))
    {
        if (target == 0) throw new InvalidOperationException("No locked target is available.");
        Console.WriteLine("WATCH START milliseconds, camera-player, camera-target, player-target, player-facing-dot, camera-facing-dot, selected, override, secondary");
        var started = Environment.TickCount64;
        for (var sample = 0; sample < 900 && !process.HasExited; sample++)
        {
            try
            {
                var livePlayer = ReadVector3(handle, physics + ChrPhysicsLocalPositionOffset);
                var liveCamera = ReadVector3(handle, debugCamera + CameraPositionOffset);
                var targetCtrl = ReadUInt64(handle, target + ChrInsChrCtrlOffset);
                var targetPosition = ReadVector3(handle, targetCtrl + ChrCtrlModelMatrixOffset + ModelMatrixTranslationOffset);
                var playerForward = ReadVector3(handle, chrCtrl + ChrCtrlModelMatrixOffset + 0x20);
                var toTarget = Normalize(Subtract(targetPosition, livePlayer));
                var facingDot = Dot(Normalize(playerForward), toTarget);
                var cameraForward = ReadVector3(handle, debugCamera + CameraMatrixOffset + 0x20);
                var cameraFacingDot = Dot(
                    Normalize(cameraForward),
                    Normalize(Subtract(targetPosition, liveCamera)));
                var cadence = Read(handle, target + 0xB4, 12);
                Console.WriteLine(
                    $"{Environment.TickCount64 - started,6}," +
                    $"{Distance(liveCamera, livePlayer),7:0.00}," +
                    $"{Distance(liveCamera, targetPosition),7:0.00}," +
                    $"{Distance(livePlayer, targetPosition),7:0.00}," +
                    $"{facingDot,7:0.000}," +
                    $"{cameraFacingDot,7:0.000}," +
                    $"{BitConverter.ToInt32(cadence, 0),3}," +
                    $"{BitConverter.ToInt32(cadence, 4),3}," +
                    $"{BitConverter.ToInt32(cadence, 8),3}");
            }
            catch (Win32Exception) { Console.WriteLine("TARGET BECAME UNREADABLE"); break; }
            Thread.Sleep(100);
        }
        return;
    }

    var dumpArgument = args.FirstOrDefault(value => value.StartsWith("--dump=", StringComparison.OrdinalIgnoreCase));
    if (dumpArgument is not null)
    {
        var text = dumpArgument[(dumpArgument.IndexOf('=') + 1)..];
        if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) text = text[2..];
        var dumpAddress = Convert.ToUInt64(text, 16);
        DumpPointerReferenceContext(handle, dumpAddress);
        return;
    }

    ScanObject(handle, "FieldArea", fieldArea, 0x20000, playerPosition, cameraPosition);
    ScanObject(handle, "WorldChrMan", world, 0x22000, playerPosition, cameraPosition);
    var vtables = new Dictionary<string, ulong>(StringComparer.Ordinal);
    foreach (var typeName in new[] { "WorldBackRead", "WorldAreaMap", "WorldMapTileBackReader" })
    {
        var vtable = FindVtable(image, moduleBase, typeName);
        Console.WriteLine($"{typeName,-24} VTABLE 0x{vtable:X}");
        if (vtable != 0)
        {
            vtables[typeName] = vtable;
            foreach (var reference in FindRipRelativeReferences(image, moduleBase, vtable))
                Console.WriteLine($"  XREF 0x{reference:X}");
            var entries = Read(handle, vtable, 8 * 24);
            for (var index = 0; index < 24; index++)
                Console.WriteLine($"  [{index,2}] 0x{BitConverter.ToUInt64(entries, index * 8):X}");
        }
    }
    if (!args.Contains("--quick", StringComparer.OrdinalIgnoreCase))
        ScanProcess(handle, playerPosition, cameraPosition, vtables);
}

finally
{
    CloseHandle(handle);
}

static void ReportCloneCandidates(IntPtr handle, ulong world, ulong player)
{
    var begin = ReadUInt64(handle, world + 0x1F1B8);
    var end = ReadUInt64(handle, world + 0x1F1C0);
    if (begin < 0x10000 || end < begin || end - begin > 800_000) return;

    for (var cursor = begin; cursor < end; cursor += 8)
    {
        var character = ReadUInt64(handle, cursor);
        if (character < 0x10000 || character == player) continue;
        var npcParam = BitConverter.ToInt32(Read(handle, character + 0x60, 4), 0);
        var characterId = BitConverter.ToUInt32(Read(handle, character + 0x188, 4), 0);
        if (npcParam != 100000010 && characterId != 0) continue;

        var bytes = Read(handle, character + 0x1C4, 8);
        Console.WriteLine($"CLONE       0x{character:X} npc={npcParam} npcId={BitConverter.ToInt32(Read(handle, character + 0x64, 4), 0)} charId={characterId} team={Read(handle, character + 0x6C, 1)[0]} think={BitConverter.ToInt32(Read(handle, character + 0x5A0, 4), 0)}");
        Console.WriteLine($"CLONE PTRS  model=0x{ReadUInt64(handle, character + 0x50):X} ctrl=0x{ReadUInt64(handle, character + 0x58):X} modules=0x{ReadUInt64(handle, character + 0x190):X}");
        Console.WriteLine($"CLONE FLAGS 1c4={bytes[0]:X2} 1c5={bytes[1]:X2} 1c6={bytes[2]:X2} 1c7={bytes[3]:X2} 1c8={bytes[4]:X2} load={BitConverter.ToUInt32(Read(handle, character + 0x1B4, 4), 0):X8}");
        Console.WriteLine($"CLONE ALPHA key={BitConverter.ToSingle(Read(handle, character + 0x238, 4), 0):0.###} tint={BitConverter.ToSingle(Read(handle, character + 0x240, 4), 0):0.###} base={BitConverter.ToSingle(Read(handle, character + 0x24C, 4), 0):0.###} modifier={BitConverter.ToSingle(Read(handle, character + 0x250, 4), 0):0.###}");
    }
}

static (float X, float Y, float Z) Subtract((float X, float Y, float Z) left, (float X, float Y, float Z) right) =>
    (left.X - right.X, left.Y - right.Y, left.Z - right.Z);

static (float X, float Y, float Z) Normalize((float X, float Y, float Z) value)
{
    var length = MathF.Sqrt(value.X * value.X + value.Y * value.Y + value.Z * value.Z);
    return length > 0.0001f ? (value.X / length, value.Y / length, value.Z / length) : (0f, 0f, 0f);
}

static float Dot((float X, float Y, float Z) left, (float X, float Y, float Z) right) =>
    left.X * right.X + left.Y * right.Y + left.Z * right.Z;

static void ReportPatchState(byte[] image, string name, string originalPattern, string patchedPattern)
{
    var original = FindPattern(image, originalPattern);
    var patched = FindPattern(image, patchedPattern);
    Console.WriteLine($"{name,-20} {(patched >= 0 ? "ON" : original >= 0 ? "OFF" : "UNKNOWN")}");
}

static ulong ResolveLockedTarget(IntPtr handle, ulong world, ulong resolvedPlayer, byte[] image, ulong moduleBase)
{
    try
    {
        var patchedAccessor = FindPattern(image, "E9 ?? ?? ?? ?? 90 90 90 90 90 90 49 8B CE E8");
        if (patchedAccessor >= 0)
        {
            var jump = BitConverter.ToInt32(image, patchedAccessor + 1);
            var cave = moduleBase + (ulong)(patchedAccessor + 5 + jump);
            var caveBytes = Read(handle, cave, 7);
            if (caveBytes[0] == 0x48 && caveBytes[1] == 0x89 && caveBytes[2] == 0x05)
            {
                var storageDisplacement = BitConverter.ToInt32(caveBytes, 3);
                var storage = cave + 7UL + unchecked((ulong)(long)storageDisplacement);
                var hookedTarget = ReadUInt64(handle, storage);
                if (hookedTarget >= 0x10000) return hookedTarget;
            }
        }

        var playerIns = ReadUInt64(handle, world + 0x1E508);
        if (playerIns < 0x10000) playerIns = resolvedPlayer;
        if (playerIns < 0x10000) return 0;
        var packedHandle = ReadUInt64(handle, playerIns + 0x6B0);
        var localId = unchecked((uint)packedHandle);
        if (localId == 0 && resolvedPlayer >= 0x10000)
        {
            playerIns = resolvedPlayer;
            packedHandle = ReadUInt64(handle, playerIns + 0x6B0);
            localId = unchecked((uint)packedHandle);
        }
        if (localId == 0) return 0;
        var area = BitConverter.ToInt32(Read(handle, playerIns + 0x6B4, 4), 0);
        foreach (var chrSetOffset in new ulong[] { 0x17420, 0x17438 })
        {
            var chrSet = ReadUInt64(handle, world + chrSetOffset);
            if (chrSet < 0x10000) continue;
            var count = BitConverter.ToInt32(Read(handle, chrSet + 0x20, 4), 0);
            if (count < 0 || count > 4096) continue;
            for (var index = 0; index <= count; index++)
            {
                try
                {
                    var entry = chrSet + 0x78 + (ulong)(index * 0x10);
                    var candidateHandle = ReadUInt64(handle, entry);
                    if (unchecked((uint)candidateHandle) != localId) continue;
                    var candidateArea = BitConverter.ToInt32(Read(handle, entry + 0x04, 4), 0);
                    if (area != 0 && candidateArea != area) continue;
                    var candidate = ReadUInt64(handle, entry + 0x08);
                    if (candidate >= 0x10000 &&
                        BitConverter.ToUInt32(Read(handle, candidate + 0x08, 4), 0) == localId &&
                        BitConverter.ToInt32(Read(handle, candidate + 0x60, 4), 0) > 0)
                        return candidate;
                }
                catch (Win32Exception) { }
            }
        }
    }
    catch (Win32Exception) { }
    return 0;
}

static void ReportZeroCadenceOverrides(IntPtr handle, ulong world)
{
    try
    {
        var begin = ReadUInt64(handle, world + 0x1F1B8);
        var end = ReadUInt64(handle, world + 0x1F1C0);
        if (begin < 0x10000 || end < begin || end - begin > 800000) return;
        var matches = new List<string>();
        for (var cursor = begin; cursor < end; cursor += 8)
        {
            ulong character;
            try { character = ReadUInt64(handle, cursor); }
            catch (Win32Exception) { continue; }
            if (character < 0x10000) continue;
            try
            {
                if (BitConverter.ToInt32(Read(handle, character + 0xB8, 4), 0) != 0) continue;
                var local = BitConverter.ToUInt32(Read(handle, character + 0x08, 4), 0);
                var param = BitConverter.ToInt32(Read(handle, character + 0x60, 4), 0);
                matches.Add($"0x{character:X}:local={local}:param={param}");
            }
            catch (Win32Exception) { }
        }
        Console.WriteLine($"OVERRIDE=0  {matches.Count} {string.Join(' ', matches.Take(12))}");
    }
    catch (Win32Exception) { }
}

static IEnumerable<ulong> FindRipRelativeReferences(byte[] image, ulong moduleBase, ulong target)
{
    for (var index = 0; index <= image.Length - 7; index++)
    {
        // lea r64,[rip+disp32] and mov r64,[rip+disp32], including REX variants.
        if (image[index] is < 0x48 or > 0x4F ||
            (image[index + 1] != 0x8D && image[index + 1] != 0x8B) ||
            (image[index + 2] & 0xC7) != 0x05)
            continue;
        var displacement = BitConverter.ToInt32(image, index + 3);
        var resolved = moduleBase + (ulong)(index + 7 + displacement);
        if (resolved == target) yield return moduleBase + (ulong)index;
    }
}

static void ScanProcess(
    IntPtr handle,
    (float X, float Y, float Z) player,
    (float X, float Y, float Z) camera,
    IReadOnlyDictionary<string, ulong> vtables)
{
    const uint MemCommit = 0x1000;
    const uint PageGuard = 0x100;
    const uint PageNoAccess = 0x01;
    const ulong MaximumAddress = 0x00007FFFFFFFFFFF;
    const int MaximumRegionBytes = 64 * 1024 * 1024;
    var address = 0UL;
    var playerMatches = new List<ulong>();
    var cameraMatches = new List<ulong>();
    var objectMatches = vtables.ToDictionary(pair => pair.Key, _ => new List<ulong>(), StringComparer.Ordinal);
    long scanned = 0;

    while (address < MaximumAddress &&
           VirtualQueryEx(handle, new IntPtr(unchecked((long)address)), out var info, (IntPtr)Marshal.SizeOf<Mbi>()) != 0)
    {
        var regionSize = unchecked((ulong)info.RegionSize.ToInt64());
        if (regionSize == 0) break;
        var regionBase = unchecked((ulong)info.BaseAddress.ToInt64());
        var readable = info.State == MemCommit &&
                       (info.Protect & (PageGuard | PageNoAccess)) == 0 &&
                       regionSize <= MaximumRegionBytes;
        if (readable)
        {
            try
            {
                var bytes = Read(handle, regionBase, checked((int)regionSize));
                scanned += bytes.Length;
                for (var offset = 0; offset <= bytes.Length - 12; offset += 4)
                {
                    var value = (
                        BitConverter.ToSingle(bytes, offset),
                        BitConverter.ToSingle(bytes, offset + 4),
                        BitConverter.ToSingle(bytes, offset + 8));
                    if (!float.IsFinite(value.Item1) || !float.IsFinite(value.Item2) || !float.IsFinite(value.Item3))
                        continue;
                    if (Distance(value, player) <= 0.02f && playerMatches.Count < 250)
                        playerMatches.Add(regionBase + (ulong)offset);
                    if (Distance(value, camera) <= 0.02f && cameraMatches.Count < 250)
                        cameraMatches.Add(regionBase + (ulong)offset);
                }
                for (var offset = 0; offset <= bytes.Length - 8; offset += 8)
                {
                    var pointer = BitConverter.ToUInt64(bytes, offset);
                    foreach (var pair in vtables)
                    {
                        var matches = objectMatches[pair.Key];
                        if (pointer == pair.Value && matches.Count < 100)
                            matches.Add(regionBase + (ulong)offset);
                    }
                }
            }
            catch (Win32Exception)
            {
                // Regions can change protection while the game is running.
            }
        }
        var next = regionBase + regionSize;
        if (next <= address) break;
        address = next;
    }

    Console.WriteLine($"PROCESS SCANNED {scanned / 1048576.0:0.0} MB");
    Console.WriteLine($"PLAYER VECTOR MATCHES ({playerMatches.Count}):");
    foreach (var match in playerMatches)
    {
        if (VirtualQueryEx(handle, new IntPtr(unchecked((long)match)), out var region, (IntPtr)Marshal.SizeOf<Mbi>()) != 0)
        {
            Console.WriteLine(
                $"  0x{match:X} region=0x{region.BaseAddress.ToInt64():X} " +
                $"allocation=0x{region.AllocationBase.ToInt64():X} size=0x{region.RegionSize.ToInt64():X} " +
                $"type=0x{region.Type:X} protect=0x{region.Protect:X}");
        }
        else Console.WriteLine($"  0x{match:X}");
    }
    Console.WriteLine($"CAMERA VECTOR MATCHES ({cameraMatches.Count}):");
    foreach (var match in cameraMatches) Console.WriteLine($"  0x{match:X}");
    foreach (var pair in objectMatches)
    {
        Console.WriteLine($"{pair.Key} OBJECTS ({pair.Value.Count}):");
        foreach (var match in pair.Value)
        {
            Console.WriteLine($"  0x{match:X}");
            InspectObjectGraph(handle, pair.Key, match, player, camera);
        }
    }

    FindObjectPointerReferences(handle, objectMatches);
}

static void FindObjectPointerReferences(
    IntPtr handle,
    IReadOnlyDictionary<string, List<ulong>> objects)
{
    const uint MemCommit = 0x1000;
    const uint PageGuard = 0x100;
    const uint PageNoAccess = 0x01;
    const ulong MaximumAddress = 0x00007FFFFFFFFFFF;
    const int MaximumRegionBytes = 64 * 1024 * 1024;
    var targets = objects
        .SelectMany(pair => pair.Value.Select(value => (pair.Key, Value: value)))
        .Where(target => target.Value >= 0x10000 && target.Value <= MaximumAddress)
        .ToArray();
    if (targets.Length == 0) return;

    var results = targets.ToDictionary(target => target, _ => new List<ulong>());
    var address = 0UL;
    while (address < MaximumAddress &&
           VirtualQueryEx(handle, new IntPtr(unchecked((long)address)), out var info, (IntPtr)Marshal.SizeOf<Mbi>()) != 0)
    {
        var regionSize = unchecked((ulong)info.RegionSize.ToInt64());
        if (regionSize == 0) break;
        var regionBase = unchecked((ulong)info.BaseAddress.ToInt64());
        var readable = info.State == MemCommit &&
                       (info.Protect & (PageGuard | PageNoAccess)) == 0 &&
                       regionSize <= MaximumRegionBytes;
        if (readable)
        {
            try
            {
                var bytes = Read(handle, regionBase, checked((int)regionSize));
                for (var offset = 0; offset <= bytes.Length - 8; offset += 8)
                {
                    var value = BitConverter.ToUInt64(bytes, offset);
                    foreach (var target in targets)
                    {
                        var matches = results[target];
                        if (value == target.Value && matches.Count < 100)
                            matches.Add(regionBase + (ulong)offset);
                    }
                }
            }
            catch (Win32Exception) { }
        }
        var next = regionBase + regionSize;
        if (next <= address) break;
        address = next;
    }

    foreach (var result in results)
    {
        Console.WriteLine($"{result.Key.Key} 0x{result.Key.Value:X} POINTER REFS ({result.Value.Count}):");
        foreach (var reference in result.Value)
        {
            Console.WriteLine($"  0x{reference:X}");
            DumpPointerReferenceContext(handle, reference);
        }
    }
}

static void DumpPointerReferenceContext(IntPtr handle, ulong reference)
{
    const ulong before = 0x100;
    var start = reference >= before ? reference - before : 0;
    try
    {
        var bytes = Read(handle, start, 0x180);
        for (var offset = 0; offset <= bytes.Length - 8; offset += 8)
        {
            var address = start + (ulong)offset;
            var value = BitConverter.ToUInt64(bytes, offset);
            Console.WriteLine($"    {(address == reference ? '>' : ' ')} 0x{address:X}: 0x{value:X}");
        }
    }
    catch (Win32Exception)
    {
        Console.WriteLine("    <context unreadable>");
    }
}

static void InspectObjectGraph(
    IntPtr handle,
    string name,
    ulong objectAddress,
    (float X, float Y, float Z) player,
    (float X, float Y, float Z) camera)
{
    byte[] root;
    try { root = Read(handle, objectAddress, 0x800); }
    catch (Win32Exception) { return; }

    ReportVectorMatches($"{name} root", objectAddress, root, player, camera);
    var visited = new HashSet<ulong>();
    var reported = 0;
    for (var offset = 0; offset <= root.Length - 8 && reported < 80; offset += 8)
    {
        var pointer = BitConverter.ToUInt64(root, offset);
        if (pointer < 0x10000 || pointer > 0x00007FFFFFFFFFFF || !visited.Add(pointer)) continue;
        try
        {
            var child = Read(handle, pointer, 0x800);
            if (ReportVectorMatches(
                    $"{name} +0x{offset:X} -> 0x{pointer:X}",
                    pointer,
                    child,
                    player,
                    camera))
                reported++;
        }
        catch (Win32Exception)
        {
            // Not every aligned value is a readable pointer.
        }
    }
}

static bool ReportVectorMatches(
    string label,
    ulong address,
    byte[] bytes,
    (float X, float Y, float Z) player,
    (float X, float Y, float Z) camera)
{
    var found = false;
    for (var offset = 0; offset <= bytes.Length - 12; offset += 4)
    {
        var value = (
            BitConverter.ToSingle(bytes, offset),
            BitConverter.ToSingle(bytes, offset + 4),
            BitConverter.ToSingle(bytes, offset + 8));
        var playerDistance = Distance(value, player);
        var cameraDistance = Distance(value, camera);
        if (!float.IsFinite(playerDistance) || !float.IsFinite(cameraDistance)) continue;
        if (playerDistance > 0.5f && cameraDistance > 0.5f) continue;
        Console.WriteLine(
            $"    {label} +0x{offset:X} [0x{address + (ulong)offset:X}] " +
            $"{Format(value)} {(playerDistance <= cameraDistance ? "PLAYER" : "CAMERA")}");
        found = true;
    }
    return found;
}

static ulong FindVtable(byte[] image, ulong moduleBase, string className)
{
    var decorated = System.Text.Encoding.ASCII.GetBytes($".?AV{className}@CS@@\0");
    var nameOffset = FindBytes(image, decorated);
    if (nameOffset < 16) return 0;
    var typeDescriptorRva = nameOffset - 16;
    var encodedRva = BitConverter.GetBytes(typeDescriptorRva);
    var searchAt = 0;
    while (searchAt < image.Length)
    {
        var found = FindBytes(image, encodedRva, searchAt);
        if (found < 12) break;
        var colRva = found - 12;
        if (BitConverter.ToUInt32(image, colRva) == 1 &&
            colRva + 24 <= image.Length &&
            BitConverter.ToInt32(image, colRva + 20) == colRva)
        {
            var colAddressBytes = BitConverter.GetBytes(moduleBase + (ulong)colRva);
            var locatorPointer = FindBytes(image, colAddressBytes);
            if (locatorPointer >= 0) return moduleBase + (ulong)locatorPointer + 8;
        }
        searchAt = found + 1;
    }
    return 0;
}

static int FindBytes(byte[] bytes, byte[] needle, int start = 0)
{
    for (var index = start; index <= bytes.Length - needle.Length; index++)
    {
        var matched = true;
        for (var needleIndex = 0; needleIndex < needle.Length; needleIndex++)
        {
            if (bytes[index + needleIndex] == needle[needleIndex]) continue;
            matched = false;
            break;
        }
        if (matched) return index;
    }
    return -1;
}

static void ScanObject(
    IntPtr handle,
    string name,
    ulong address,
    int size,
    (float X, float Y, float Z) player,
    (float X, float Y, float Z) camera)
{
    var bytes = Read(handle, address, size);
    Console.WriteLine($"{name} approximate vector matches:");
    var found = 0;
    for (var offset = 0; offset <= bytes.Length - 12; offset += 4)
    {
        var value = (
            BitConverter.ToSingle(bytes, offset),
            BitConverter.ToSingle(bytes, offset + 4),
            BitConverter.ToSingle(bytes, offset + 8));
        var playerDistance = Distance(value, player);
        var cameraDistance = Distance(value, camera);
        if (!float.IsFinite(playerDistance) || !float.IsFinite(cameraDistance)) continue;
        if (playerDistance > 0.25f && cameraDistance > 0.25f) continue;
        Console.WriteLine(
            $"  +0x{offset:X5}  {Format(value)}  " +
            (playerDistance <= cameraDistance ? $"PLAYER d={playerDistance:0.000}" : $"CAMERA d={cameraDistance:0.000}"));
        if (++found >= 100) break;
    }
    if (found == 0) Console.WriteLine("  none");
}

static float Distance((float X, float Y, float Z) a, (float X, float Y, float Z) b)
{
    var x = a.X - b.X;
    var y = a.Y - b.Y;
    var z = a.Z - b.Z;
    return MathF.Sqrt(x * x + y * y + z * z);
}

static string Format((float X, float Y, float Z) value) =>
    $"({value.X:0.000}, {value.Y:0.000}, {value.Z:0.000})";

static (float X, float Y, float Z) ReadVector3(IntPtr handle, ulong address)
{
    var bytes = Read(handle, address, 12);
    return (BitConverter.ToSingle(bytes, 0), BitConverter.ToSingle(bytes, 4), BitConverter.ToSingle(bytes, 8));
}

static ulong ReadUInt64(IntPtr handle, ulong address) => BitConverter.ToUInt64(Read(handle, address, 8));

static byte[] Read(IntPtr handle, ulong address, int size)
{
    var bytes = new byte[size];
    if (!ReadProcessMemory(handle, new IntPtr(unchecked((long)address)), bytes, size, out var read) ||
        read.ToInt64() != size)
        throw new Win32Exception(Marshal.GetLastWin32Error(), $"Read failed at 0x{address:X}.");
    return bytes;
}

static int FindPattern(byte[] bytes, string pattern)
{
    var tokens = pattern.Split(' ', StringSplitOptions.RemoveEmptyEntries);
    var values = tokens.Select(token => token is "?" or "??" ? -1 : Convert.ToInt32(token, 16)).ToArray();
    for (var index = 0; index <= bytes.Length - values.Length; index++)
    {
        var matched = true;
        for (var patternIndex = 0; patternIndex < values.Length; patternIndex++)
        {
            if (values[patternIndex] < 0 || bytes[index + patternIndex] == values[patternIndex]) continue;
            matched = false;
            break;
        }
        if (matched) return index;
    }
    return -1;
}

[DllImport("kernel32.dll", SetLastError = true)]
static extern IntPtr OpenProcess(uint access, bool inheritHandle, int processId);

[DllImport("kernel32.dll", SetLastError = true)]
[return: MarshalAs(UnmanagedType.Bool)]
static extern bool ReadProcessMemory(
    IntPtr process,
    IntPtr baseAddress,
    [Out] byte[] buffer,
    int size,
    out IntPtr bytesRead);

[DllImport("kernel32.dll")]
[return: MarshalAs(UnmanagedType.Bool)]
static extern bool CloseHandle(IntPtr handle);

[DllImport("kernel32.dll", SetLastError = true)]
static extern IntPtr VirtualQueryEx(
    IntPtr process,
    IntPtr address,
    out Mbi information,
    IntPtr length);

[StructLayout(LayoutKind.Sequential)]
struct Mbi
{
    public IntPtr BaseAddress;
    public IntPtr AllocationBase;
    public uint AllocationProtect;
    public ushort PartitionId;
    public IntPtr RegionSize;
    public uint State;
    public uint Protect;
    public uint Type;
}
