using System;
using System.Diagnostics;
using System.Threading;

namespace EnemyIntelReader;

internal sealed record ReaderStatus(string Title, string Detail, string Pill);

internal sealed class LiveEnemyReaderService : IDisposable
{
    // The intel UI is software-rendered for reliable OBS capture. A 30 Hz
    // telemetry feed keeps its bars responsive without forcing WPF to process
    // redundant 60 Hz cross-thread updates.
    private const int ActiveTargetPollMilliseconds = 33;
    private const int NoTargetPollMilliseconds = 80;
    private const int InvalidVitalsPollMilliseconds = 50;
    private const int FullTargetRefreshMilliseconds = 300;
    private const string ProcessName = "eldenring";
    private const string WorldChrManPattern =
        "48 8B 05 ?? ?? ?? ?? 48 85 C0 74 0F 48 39 88";
    private const string GameDataManPattern =
        "48 8B 05 ?? ?? ?? ?? 48 85 C0 74 05 48 8B 40 58 C3 C3";

    private readonly GameItemCatalog? _itemCatalog;
    private readonly CharaInitLoadoutCatalog? _charaInitLoadouts;
    private readonly EnemyLoadoutCatalog? _enemyLoadouts;
    private readonly EnemyLoadoutCatalog? _enemyVisualLoadouts;
    private readonly bool _vitalsOnly;
    private readonly CancellationTokenSource _shutdown = new();
    private Thread? _thread;
    private CharacterInfo? _lastFullTarget;
    private long _lastFullTargetTicks;
    private long _targetOverrideAddress;
    private bool _targetWasPublished;
    private ulong _clearCountCharacterAddress;
    private int _cachedClearCount = -1;
    private long _lastClearCountTicks;

    public LiveEnemyReaderService(
        GameItemCatalog itemCatalog,
        CharaInitLoadoutCatalog charaInitLoadouts,
        EnemyLoadoutCatalog enemyLoadouts,
        EnemyLoadoutCatalog enemyVisualLoadouts)
    {
        _itemCatalog = itemCatalog;
        _charaInitLoadouts = charaInitLoadouts;
        _enemyLoadouts = enemyLoadouts;
        _enemyVisualLoadouts = enemyVisualLoadouts;
    }

    public LiveEnemyReaderService()
    {
        _vitalsOnly = true;
    }

    public event Action<ReaderStatus>? StatusChanged;
    public event Action? NoTarget;
    public event Action<CharacterInfo>? TargetFound;

    public void Start()
    {
        if (_thread != null)
        {
            return;
        }

        _thread = new Thread(ReaderLoop)
        {
            IsBackground = true,
            Name = "EnemyIntelReaderWpf"
        };
        _thread.Start();
    }

    public void Stop()
    {
        _shutdown.Cancel();
    }

    public void SetTargetOverride(ulong? address) =>
        Interlocked.Exchange(ref _targetOverrideAddress, unchecked((long)(address ?? 0)));

    public void Dispose()
    {
        Stop();
        _shutdown.Dispose();
    }

    private void ReaderLoop()
    {
        while (!_shutdown.IsCancellationRequested)
        {
            if (Process.GetProcessesByName(ProcessName).Length == 0)
            {
                PublishStatus("WAITING FOR ELDEN RING", "Lock onto an enemy", "V2  |  WPF");
                Sleep(1000);
                continue;
            }

            try
            {
                PublishStatus("ELDEN RING DETECTED", "Connecting to lock-on hook", "V2  |  LINKING");

                using MemoryReader memory = new(ProcessName);
                ulong lockOnAccessor = MainForm.ResolveLockOnAccessor(memory);
                ulong worldChrMan = MainForm.ResolveGlobalPointerOrZero(memory, WorldChrManPattern);
                ulong gameDataMan = MainForm.ResolveGlobalPointerOrZero(memory, GameDataManPattern);

                using LockOnHook hook = LockOnHook.Install(memory, lockOnAccessor);
                PublishStatus("NO TARGET", "Reader active. Lock onto an enemy.", "V2  |  LINKED");

                MonitorTargets(memory, hook, worldChrMan, gameDataMan);
            }
            catch (Exception exception)
            {
                if (_shutdown.IsCancellationRequested)
                {
                    return;
                }

                PublishStatus("READER PAUSED", exception.Message, "V2  |  PAUSED");
                Sleep(1500);
            }
        }
    }

    private void MonitorTargets(
        MemoryReader memory,
        LockOnHook hook,
        ulong worldChrMan,
        ulong gameDataMan)
    {
        while (!_shutdown.IsCancellationRequested)
        {
            if (memory.Process.HasExited)
            {
                PublishStatus("WAITING FOR ELDEN RING", "Elden Ring closed", "V2  |  WPF");
                return;
            }

            try
            {
                ulong characterAddress = memory.ReadUInt64(hook.TargetStorageAddress);
                ulong lockOnManager = memory.ReadUInt64(hook.ManagerStorageAddress);
                TargetHandleInfo targetHandle = MainForm.ReadPlayerTargetHandle(memory, worldChrMan);
                ulong managerHandle = targetHandle.PackedHandle;
                ulong overrideAddress = unchecked((ulong)Interlocked.Read(ref _targetOverrideAddress));
                bool usingTargetOverride = memory.IsLikelyPointer(overrideAddress);

                if (usingTargetOverride)
                {
                    characterAddress = overrideAddress;
                    managerHandle = 0;
                    targetHandle = new TargetHandleInfo(0, 0, 0, "action-camera override");
                }

                if (!usingTargetOverride &&
                    managerHandle == 0 &&
                    memory.IsLikelyPointer(lockOnManager))
                {
                    try
                    {
                        managerHandle = memory.ReadUInt64(lockOnManager + 0x6B0);
                        targetHandle = new TargetHandleInfo(
                            managerHandle,
                            unchecked((uint)(managerHandle & 0xFFFFFFFF)),
                            unchecked((int)(managerHandle >> 32)),
                            "lock-on manager");
                    }
                    catch
                    {
                        // The manager can be briefly unreadable while target state changes.
                    }
                }

                if (!memory.IsLikelyPointer(characterAddress) && managerHandle != 0)
                {
                    characterAddress = MainForm.FindWorldChrManCharacter(
                        memory,
                        worldChrMan,
                        targetHandle.LocalId,
                        targetHandle.Area);
                }

                if (!memory.IsLikelyPointer(characterAddress))
                {
                    _lastFullTarget = null;
                    _lastFullTargetTicks = 0;
                    PublishNoTarget();
                    Sleep(NoTargetPollMilliseconds);
                    continue;
                }

                ulong packedHandle = managerHandle != 0
                    ? managerHandle
                    : memory.ReadUInt64(characterAddress + 0x08);

                uint targetLocalId = targetHandle.LocalId != 0
                    ? targetHandle.LocalId
                    : (uint)(packedHandle & 0xFFFFFFFF);

                CharacterInfo vitals = MainForm.ReadCharacterVitals(memory, characterAddress);

                if (targetLocalId != 0 && targetLocalId != vitals.LocalId)
                {
                    ulong resolvedAddress = MainForm.FindWorldChrManCharacter(
                        memory,
                        worldChrMan,
                        targetLocalId,
                        targetHandle.Area);
                    if (memory.IsLikelyPointer(resolvedAddress))
                    {
                        characterAddress = resolvedAddress;
                        vitals = MainForm.ReadCharacterVitals(memory, characterAddress);
                    }
                }

                if (!MainForm.HasReadableTargetVitals(vitals))
                {
                    _lastFullTarget = null;
                    _lastFullTargetTicks = 0;
                    PublishNoTarget();
                    Sleep(InvalidVitalsPollMilliseconds);
                    continue;
                }

                CharacterInfo target;
                if (_vitalsOnly)
                {
                    vitals.ClearCount = ReadCachedClearCount(memory, gameDataMan, characterAddress);
                    target = vitals;
                }
                else
                {
                    target = BuildTargetSnapshot(memory, vitals, characterAddress, worldChrMan, gameDataMan);
                }
                _targetWasPublished = true;
                TargetFound?.Invoke(target);
            }
            catch (MemoryReadException)
            {
                _lastFullTarget = null;
                _lastFullTargetTicks = 0;
                PublishNoTarget();
            }

            Sleep(ActiveTargetPollMilliseconds);
        }
    }

    private CharacterInfo BuildTargetSnapshot(
        MemoryReader memory,
        CharacterInfo vitals,
        ulong characterAddress,
        ulong worldChrMan,
        ulong gameDataMan)
    {
        long now = Environment.TickCount64;
        bool targetChanged = _lastFullTarget == null ||
                             _lastFullTarget.Address != characterAddress ||
                             _lastFullTarget.LocalId != vitals.LocalId ||
                             _lastFullTarget.ParamId != vitals.ParamId;
        bool fullRefreshDue = targetChanged ||
                              now - _lastFullTargetTicks >= FullTargetRefreshMilliseconds;

        if (fullRefreshDue)
        {
            CharacterInfo fullTarget = ReadTarget(memory, characterAddress, worldChrMan, gameDataMan);
            if (MainForm.HasReadableTargetVitals(fullTarget))
            {
                _lastFullTarget = fullTarget;
                _lastFullTargetTicks = now;
            }
        }

        return MergeVitals(vitals, _lastFullTarget, gameDataMan, memory);
    }

    private static CharacterInfo MergeVitals(
        CharacterInfo vitals,
        CharacterInfo? fullTarget,
        ulong gameDataMan,
        MemoryReader memory)
    {
        if (fullTarget == null ||
            fullTarget.Address != vitals.Address ||
            fullTarget.LocalId != vitals.LocalId ||
            fullTarget.ParamId != vitals.ParamId)
        {
            vitals.ClearCount = MainForm.ReadClearCount(memory, gameDataMan);
            return vitals;
        }

        fullTarget.CurrentHp = vitals.CurrentHp;
        fullTarget.MaxHp = vitals.MaxHp;
        fullTarget.Stagger = vitals.Stagger;
        fullTarget.ClearCount = MainForm.ReadClearCount(memory, gameDataMan);
        return fullTarget;
    }

    private CharacterInfo ReadTarget(
        MemoryReader memory,
        ulong characterAddress,
        ulong worldChrMan,
        ulong gameDataMan)
    {
        CharacterInfo target = MainForm.ReadCharacter(
            memory,
            characterAddress,
            worldChrMan,
            _itemCatalog!,
            _charaInitLoadouts!,
            _enemyLoadouts!,
            _enemyVisualLoadouts!);
        target.ClearCount = MainForm.ReadClearCount(memory, gameDataMan);
        return target;
    }

    private void PublishStatus(string title, string detail, string pill)
    {
        StatusChanged?.Invoke(new ReaderStatus(title, detail, pill));
    }

    private void PublishNoTarget()
    {
        if (!_targetWasPublished)
        {
            return;
        }

        _targetWasPublished = false;
        _clearCountCharacterAddress = 0;
        _cachedClearCount = -1;
        _lastClearCountTicks = 0;
        NoTarget?.Invoke();
    }

    private int ReadCachedClearCount(MemoryReader memory, ulong gameDataMan, ulong characterAddress)
    {
        var now = Environment.TickCount64;
        if (_clearCountCharacterAddress != characterAddress || now - _lastClearCountTicks >= 1000)
        {
            _cachedClearCount = MainForm.ReadClearCount(memory, gameDataMan);
            _clearCountCharacterAddress = characterAddress;
            _lastClearCountTicks = now;
        }

        return _cachedClearCount;
    }

    private void Sleep(int milliseconds)
    {
        _shutdown.Token.WaitHandle.WaitOne(milliseconds);
    }
}
