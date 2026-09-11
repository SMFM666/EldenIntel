using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Linq;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Threading;

namespace EnemyIntelReader;

internal sealed class GameCameraControlService : IDisposable
{
    private const string ProcessName = "eldenring";
    private readonly NativeGameControlBridge _nativeControls = new();
    private readonly FreecamModBridge _freecamMod = new();
    private readonly PracticeToolBridge _practiceControls = new();

    public bool IsGameRunning => FindGameProcess() != null;

    public string TogglePause() => _nativeControls.TogglePause();

    public string ToggleHud() => _nativeControls.ToggleHud();

    public string ToggleHigherLods() => _nativeControls.ToggleHigherLods();

    public string ToggleAntiAliasing() => _nativeControls.ToggleAntiAliasing();

    public string ToggleMotionBlur() => _nativeControls.ToggleMotionBlur();

    public string ToggleFreecam() => _freecamMod.ToggleFreecam();

    public bool GetFreecamAutoPauseState() => _freecamMod.GetAutoPauseState();

    public string SetFreecamAutoPause(bool enabled) => _freecamMod.SetAutoPause(enabled);

    public string ToggleDayCycle() => _freecamMod.ToggleDayCycle();
    public string PlayGesture(int gestureId) => _freecamMod.PlayGesture(gestureId);
    public string ToggleActiveView() => _freecamMod.ToggleActiveView();
    public bool IsStandaloneFreecamEnabled() => _freecamMod.IsStandaloneFreecamEnabled();
    public string ApplyPlayerEffect(int effectId) => _freecamMod.ApplyCharacterEffect(effectId, 0);
    public string ApplyCharacterEffect(int effectId, ulong targetAddress) => _freecamMod.ApplyCharacterEffect(effectId, targetAddress);
    public string ApplyNinjaSet() => _freecamMod.ApplyNinjaSet();
    public string ApplyConfessorSet() => _freecamMod.ApplyConfessorSet();
    public string PossessCharacter(ulong targetAddress) => _freecamMod.SendPossessionCommand(1, targetAddress);
    public string ReleasePossession() => _freecamMod.SendPossessionCommand(2, 0);
    public bool IsPossessionActive() => _freecamMod.IsPossessionActive();
    public int ResolveBuddyTriggerEffect(int buddyParamId) => _practiceControls.ResolveBuddyTriggerEffect(buddyParamId);

    public string ToggleInputBlock() => _nativeControls.ToggleInputBlock();

    public bool IsPlayerInputBlocked => _nativeControls.IsPlayerInputBlocked;

    public string EnsurePlayerInputEnabled() => _nativeControls.EnsurePlayerInputEnabled();

    public string EnsurePlayerInputBlocked() => _nativeControls.EnsurePlayerInputBlocked();

    public string ResetFov() => _freecamMod.ResetCameraState();

    public string StepFrame() => _freecamMod.StepFrame();

    public string SetGameSpeed(double speed) => _freecamMod.SetGameSpeed(speed);

    public string SaveCameraState(int slot) => _freecamMod.SaveCameraState(slot);

    public string PlayCameraStates(int[] slots) => _freecamMod.PlayCameraStates(slots);

    public string ReloadFreecamConfig() => _freecamMod.ReloadConfig();

    public string SuspendFreecamForTrack() => _freecamMod.SuspendForTrack();

    public string ResumeFreecamAfterTrack() => _freecamMod.ResumeAfterTrack();

    public string BeginSynchronizedActionCamera(bool absolutePosition = false) =>
        _freecamMod.BeginSynchronizedActionCamera(absolutePosition);

    public void SetActionCameraInputPolicy(
        bool keyboardControlsCamera,
        bool mouseControlsCamera,
        bool controllerControlsPlayer) =>
        _freecamMod.SetActionCameraInputPolicy(
            keyboardControlsCamera,
            mouseControlsCamera,
            controllerControlsPlayer);

    public void WriteSynchronizedActionCamera(
        Vector3 right,
        Vector3 up,
        Vector3 forward,
        Vector3 position,
        float fov) =>
        _freecamMod.WriteSynchronizedActionCamera(right, up, forward, position, fov);

    public void EndSynchronizedActionCamera() => _freecamMod.EndSynchronizedActionCamera();

    public void StartActionCameraInput() => _nativeControls.StartActionCameraInput();

    public void StopActionCameraInput() => _nativeControls.StopActionCameraInput();

    public (int X, int Y, float Wheel) TakeActionCameraInput() => _nativeControls.TakeActionCameraInput();

    public string TogglePracticeFlag(string key) =>
        string.Equals(key, "DROP RATE X10", StringComparison.OrdinalIgnoreCase)
            ? _freecamMod.ToggleDropRateBoost()
            : _practiceControls.ToggleFlag(key);

    public bool? GetPracticeFlagState(string key) =>
        string.Equals(key, "DROP RATE X10", StringComparison.OrdinalIgnoreCase)
            ? _freecamMod.GetDropRateBoostState()
            : _practiceControls.GetFlagState(key);

    public void Dispose()
    {
        _nativeControls.Dispose();
        _freecamMod.Dispose();
        _practiceControls.Dispose();
    }

    private static Process? FindGameProcess()
    {
        return Process.GetProcessesByName(ProcessName)
            .OrderByDescending(process => process.MainWindowHandle != IntPtr.Zero)
            .FirstOrDefault();
    }

}

internal sealed class FreecamModBridge : IDisposable
{
    private const string ProcessName = "eldenring";
    private const string FreecamFolder = @"native\EnemyIntelFreecam";
    private const string FreecamDll = "EnemyIntelFreecam.dll";
    private const string ExternalFreecamDll = "Freecam.dll";
    private const ushort VkF1 = 0x70;
    private const ushort VkF2 = 0x71;
    private const ushort VkF11 = 0x7A;
    private const ushort VkF5 = 0x74;
    private const ushort VkF4 = 0x73;
    private const ushort VkF6 = 0x75;
    private const ushort VkF7 = 0x76;
    private const ushort VkP = 0x50;
    private const ushort VkControl = 0x11;
    private const ushort VkV = 0x56;
    private const ushort VkR = 0x52;
    private const string EnemyControlMappingName = @"Local\EldenIntelEnemyControl";
    private const int EnemyControlMappingSize = 48;
    private const uint EnemyControlMagic = 0x45494354;
    private const ushort VkDelete = 0x2E;
    private const uint InputKeyboard = 1;
    private const uint KeyEventKeyUp = 0x0002;
    private const int ForegroundDelayMilliseconds = 35;
    private const int KeyHoldMilliseconds = 50;
    private const uint WmKeyDown = 0x0100;
    private const uint WmKeyUp = 0x0101;
    private const int SwRestore = 9;
    private const uint MouseEventWheel = 0x0800;
    private const string ActionCameraSyncName = @"Local\EnemyIntelActionCameraSyncV1";
    private const int ActionCameraSyncSize = 100;
    private const int ActionCameraSyncMagic = 0x41434945;
    private const int ActionCameraSyncVersion = 3;
    private const string DayCycleControlName = @"Local\EnemyIntelDayCycleControlV1";
    private const int DayCycleControlSize = 24;
    private const int DayCycleControlMagic = 0x43594144;
    private const int DayCycleControlVersion = 1;
    private const int FilmingDayCycleSpeed = 75000;
    private const string AnimationLabName = @"Local\EldenIntelAnimationLabV1";
    private const int AnimationLabSize = 24;
    private const int AnimationLabMagic = 0x42414C45;
    private const int AnimationLabVersion = 1;
    private const string ActiveViewName = @"Local\EldenIntelActiveViewV1";
    private const int ActiveViewSize = 16;
    private const int ActiveViewMagic = 0x57495645;
    private const int ActiveViewVersion = 1;
    private const string EffectControlName = @"Local\EldenIntelEffectControlV2";
    private const int EffectControlSize = 32;
    private const int EffectControlMagic = 0x58464645;
    private const int EffectControlVersion = 2;
    private const string DropRateControlName = @"Local\EldenIntelDropRateControlV1";
    private const int DropRateControlSize = 32;
    private const int DropRateControlMagic = 0x50524445;
    private const int DropRateControlVersion = 1;
    private readonly object _actionCameraSyncGate = new();
    private readonly object _dayCycleControlGate = new();
    private readonly object _animationLabGate = new();
    private readonly object _effectControlGate = new();
    private readonly object _dropRateControlGate = new();
    private MemoryMappedFile? _actionCameraSyncMapping;
    private MemoryMappedViewAccessor? _actionCameraSyncView;
    private MemoryMappedFile? _dayCycleControlMapping;
    private MemoryMappedViewAccessor? _dayCycleControlView;
    private MemoryMappedFile? _animationLabMapping;
    private MemoryMappedViewAccessor? _animationLabView;
    private MemoryMappedFile? _activeViewMapping;
    private MemoryMappedViewAccessor? _activeViewView;
    private MemoryMappedFile? _effectControlMapping;
    private MemoryMappedViewAccessor? _effectControlView;
    private int _effectRequestSequence;
    private MemoryMappedFile? _dropRateControlMapping;
    private MemoryMappedViewAccessor? _dropRateControlView;
    private int _dropRateRequestSequence;
    private bool _speedhackEnabled;
    private double _speedhackSpeed = 1.0;
    private bool _externalPossessionActive;
    private MemoryMappedFile? _enemyControlMapping;
    private MemoryMappedViewAccessor? _enemyControlView;
    private int _enemyControlRequestSequence;

    private string FreecamDllPath => Path.Combine(AppContext.BaseDirectory, FreecamFolder, FreecamDll);

    public string ToggleFreecam()
    {
        try
        {
            Process process = EnsureInjected();
            // F1 belongs to the proven Enemy Control module. EldenIntel's
            // freecam is deliberately moved to F11 so both native modules can
            // coexist without toggling possession and freecam together.
            SendGameKey(process, VkF11);
            return "FREECAM TOGGLED";
        }
        catch (Exception exception)
        {
            return $"FREECAM FAILED: {ShortMessage(exception)}";
        }
    }

    public bool GetAutoPauseState()
    {
        try
        {
            Process? process = FindGameProcess();
            string? configPath = process is null ? null : GetLoadedFreecamConfigPath(process);
            configPath ??= Path.Combine(AppContext.BaseDirectory, FreecamFolder, "Freecam", "config.ini");
            return ReadFreecamBoolean(configPath, "freeze_game");
        }
        catch { return false; }
    }

    public string SetAutoPause(bool enabled)
    {
        try
        {
            Process process = EnsureInjected();
            var configPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                Path.Combine(AppContext.BaseDirectory, FreecamFolder, "Freecam", "config.ini")
            };
            var loadedConfigPath = GetLoadedFreecamConfigPath(process);
            if (!string.IsNullOrWhiteSpace(loadedConfigPath)) configPaths.Add(loadedConfigPath);

            foreach (var configPath in configPaths.Where(File.Exists))
            {
                SetFreecamBoolean(configPath, "freeze_game", enabled);
                SetFreecamBoolean(configPath, "freeze_entities", enabled);
                SetFreecamBoolean(configPath, "freeze_player", enabled);
            }

            SendGameKey(process, VkF5);
            Thread.Sleep(80);
            if (IsStandaloneFreecamEnabled()) SendGameKey(process, VkP);
            return enabled ? "FREECAM AUTO PAUSE ON" : "FREECAM AUTO PAUSE OFF";
        }
        catch (Exception exception)
        {
            return $"AUTO PAUSE FAILED: {ShortMessage(exception)}";
        }
    }

    private static bool ReadFreecamBoolean(string path, string key)
    {
        if (!File.Exists(path)) return false;
        var prefix = key + " =";
        var line = File.ReadLines(path).FirstOrDefault(value =>
            value.TrimStart().StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
        return line is not null && line.Split('=', 2)[1].Trim().Equals("true", StringComparison.OrdinalIgnoreCase);
    }

    private static void SetFreecamBoolean(string path, string key, bool enabled)
    {
        var lines = File.ReadAllLines(path);
        var prefix = key + " =";
        for (var index = 0; index < lines.Length; index++)
        {
            if (!lines[index].TrimStart().StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
            lines[index] = $"{key} = {enabled.ToString().ToLowerInvariant()}";
            File.WriteAllLines(path, lines);
            return;
        }
        throw new InvalidDataException($"Freecam setting '{key}' is missing from {path}.");
    }

    public string ApplyPlayerEffect(int effectId) => ApplyCharacterEffect(effectId, 0);

    public string ApplyNinjaSet()
        => ApplyOutfitPreset(-3, "NINJA SET");

    public string ApplyConfessorSet()
        => ApplyOutfitPreset(-4, "CONFESSOR SET");

    private string ApplyOutfitPreset(int effectId, string label)
    {
        var status = ApplyCharacterEffect(effectId, 0);
        var effectLabel = $"PLAYER EFFECT {effectId}";
        return status switch
        {
            var value when value == $"{effectLabel} APPLIED" => $"{label} APPLIED",
            var value when value == $"{effectLabel} FAILED (-10)" => $"{label} FAILED — TRANSMOGRIFY IS NOT LOADED",
            var value when value == $"{effectLabel} FAILED (-11)" => $"{label} FAILED — GAME FUNCTION NOT FOUND",
            var value when value == $"{effectLabel} FAILED (-12)" => $"{label} FAILED — TRANSMOGRIFY REJECTED AN OUTFIT PART",
            var value when value == $"{effectLabel} FAILED (-13)" => $"{label} FAILED — TRANSMOGRIFY CALL CRASHED SAFELY",
            _ => status.Replace(effectLabel, label, StringComparison.OrdinalIgnoreCase)
        };
    }

    public string ApplyCharacterEffect(int effectId, ulong targetAddress)
    {
        System.Diagnostics.Debug.WriteLine($"[EldenIntel] ApplyCharacterEffect called: effectId={effectId}, targetAddress=0x{targetAddress:X}");
        try
        {
            _ = EnsureInjected();
            lock (_effectControlGate)
            {
                for (var attempt = 0; attempt < 40 && _effectControlView is null; attempt++)
                {
                    try
                    {
                        _effectControlMapping ??= MemoryMappedFile.OpenExisting(
                            EffectControlName, MemoryMappedFileRights.ReadWrite);
                        _effectControlView ??= _effectControlMapping.CreateViewAccessor(
                            0, EffectControlSize, MemoryMappedFileAccess.ReadWrite);
                    }
                    catch (FileNotFoundException) { Thread.Sleep(25); }
                }

                var view = _effectControlView
                    ?? throw new InvalidOperationException("GAME EFFECT CONTROL CHANNEL IS NOT READY");
                if (view.ReadInt32(0) != EffectControlMagic || view.ReadInt32(4) != EffectControlVersion)
                    throw new InvalidOperationException("GAME EFFECT CONTROL CHANNEL VERSION MISMATCH");

                var sequence = Interlocked.Increment(ref _effectRequestSequence);
                if (sequence == 0) sequence = Interlocked.Increment(ref _effectRequestSequence);
                view.Write(12, effectId);
                view.Write(20, 0);
                view.Write(24, targetAddress);
                Thread.MemoryBarrier();
                view.Write(8, sequence);
                view.Flush();
                System.Diagnostics.Debug.WriteLine($"[EldenIntel] Wrote to shared memory: effectId={effectId}, sequence={sequence}, target=0x{targetAddress:X}");

                for (var attempt = 0; attempt < 120; attempt++)
                {
                    if (view.ReadInt32(16) == sequence)
                    {
                        var result = view.ReadInt32(20);
                        var owner = targetAddress == 0 ? "PLAYER" : "CHARACTER";
                        var msg = result == 1
                            ? $"{owner} EFFECT {effectId} APPLIED"
                            : $"{owner} EFFECT {effectId} FAILED ({result})";
                        System.Diagnostics.Debug.WriteLine($"[EldenIntel] Native responded: {msg}");
                        return msg;
                    }
                    Thread.Sleep(10);
                }
                System.Diagnostics.Debug.WriteLine($"[EldenIntel] Timeout waiting for native response");
                return $"CHARACTER EFFECT {effectId} PENDING — GAME HOOK DID NOT ACKNOWLEDGE";
            }
        }
        catch (Exception exception)
        {
            var msg = $"PLAYER EFFECT FAILED: {ShortMessage(exception)}";
            System.Diagnostics.Debug.WriteLine($"[EldenIntel] Exception: {msg}");
            return msg;
        }
    }

    public string ToggleDropRateBoost()
    {
        try
        {
            _ = EnsureInjected();
            lock (_dropRateControlGate)
            {
                OpenDropRateControl();
                var view = _dropRateControlView!;
                var enable = view.ReadInt32(28) == 0;
                var sequence = Interlocked.Increment(ref _dropRateRequestSequence);
                if (sequence == 0) sequence = Interlocked.Increment(ref _dropRateRequestSequence);
                view.Write(12, enable ? 1 : 2);
                view.Write(20, 0);
                view.Write(24, 0);
                Thread.MemoryBarrier();
                view.Write(8, sequence);
                view.Flush();

                for (var attempt = 0; attempt < 200; attempt++)
                {
                    if (view.ReadInt32(16) == sequence)
                    {
                        var result = view.ReadInt32(20);
                        var changed = view.ReadInt32(24);
                        if (result != 1) return $"DROP RATE X10 FAILED ({result})";
                        return enable
                            ? $"DROP RATE X10 ON ({changed})"
                            : "DROP RATE X10 OFF";
                    }
                    Thread.Sleep(5);
                }
                return "DROP RATE X10 PENDING — GAME HOOK DID NOT ACKNOWLEDGE";
            }
        }
        catch (Exception exception)
        {
            return $"DROP RATE X10 FAILED: {ShortMessage(exception)}";
        }
    }

    public bool? GetDropRateBoostState()
    {
        try
        {
            _ = EnsureInjected();
            lock (_dropRateControlGate)
            {
                OpenDropRateControl();
                return _dropRateControlView!.ReadInt32(28) != 0;
            }
        }
        catch
        {
            return null;
        }
    }

    private void OpenDropRateControl()
    {
        for (var attempt = 0; attempt < 40 && _dropRateControlView is null; attempt++)
        {
            try
            {
                _dropRateControlMapping ??= MemoryMappedFile.OpenExisting(
                    DropRateControlName, MemoryMappedFileRights.ReadWrite);
                _dropRateControlView ??= _dropRateControlMapping.CreateViewAccessor(
                    0, DropRateControlSize, MemoryMappedFileAccess.ReadWrite);
            }
            catch (FileNotFoundException) { Thread.Sleep(25); }
        }
        var view = _dropRateControlView
            ?? throw new InvalidOperationException("DROP RATE CONTROL CHANNEL IS NOT READY");
        if (view.ReadInt32(0) != DropRateControlMagic || view.ReadInt32(4) != DropRateControlVersion)
            throw new InvalidOperationException("DROP RATE CONTROL CHANNEL VERSION MISMATCH");
    }

    public string SendPossessionCommand(int command, ulong targetAddress)
    {
        try
        {
            Process process = EnsureInjected();
            if (!IsModuleLoaded(process, "elden_ring_enemy_control.dll"))
                throw new InvalidOperationException("ENEMY CONTROL MODULE IS NOT LOADED");

            _enemyControlMapping ??= MemoryMappedFile.OpenExisting(
                EnemyControlMappingName, MemoryMappedFileRights.ReadWrite);
            _enemyControlView ??= _enemyControlMapping.CreateViewAccessor(
                0, EnemyControlMappingSize, MemoryMappedFileAccess.ReadWrite);
            if (_enemyControlView.ReadUInt32(0) != EnemyControlMagic)
                throw new InvalidOperationException("ENEMY CONTROL CHANNEL IS NOT READY");

            int request = Interlocked.Increment(ref _enemyControlRequestSequence);
            _enemyControlView.Write(16, command);
            _enemyControlView.Write(24, targetAddress);
            _enemyControlView.Write(8, request);
            _enemyControlView.Flush();

            for (int attempt = 0; attempt < 200; attempt++)
            {
                if (_enemyControlView.ReadUInt32(12) == unchecked((uint)request))
                {
                    int result = _enemyControlView.ReadInt32(20);
                    if (result <= 0)
                        throw new InvalidOperationException($"ENEMY CONTROL REJECTED ({result})");
                    _externalPossessionActive = _enemyControlView.ReadUInt32(40) != 0;
                    return command == 1 ? "CONTROL LINK ACTIVE" : "CONTROL RELEASED";
                }
                Thread.Sleep(10);
            }
            throw new TimeoutException("ENEMY CONTROL DID NOT ACKNOWLEDGE THE REQUEST");
        }
        catch (Exception exception)
        {
            return $"CONTROL FAILED: {ShortMessage(exception)}";
        }
    }

    public bool IsPossessionActive()
    {
        return _externalPossessionActive;
    }

    private static bool IsModuleLoaded(Process process, string fileName)
    {
        try
        {
            return process.Modules.OfType<ProcessModule>().Any(module =>
                string.Equals(Path.GetFileName(module.FileName), fileName,
                    StringComparison.OrdinalIgnoreCase));
        }
        catch { return false; }
    }

    public string ToggleDayCycle()
    {
        try
        {
            _ = EnsureInjected();
            lock (_dayCycleControlGate)
            {
                for (var attempt = 0; attempt < 30 && _dayCycleControlView is null; attempt++)
                {
                    try
                    {
                        _dayCycleControlMapping ??= MemoryMappedFile.OpenExisting(
                            DayCycleControlName,
                            MemoryMappedFileRights.ReadWrite);
                        _dayCycleControlView ??= _dayCycleControlMapping.CreateViewAccessor(
                            0,
                            DayCycleControlSize,
                            MemoryMappedFileAccess.ReadWrite);
                    }
                    catch (FileNotFoundException)
                    {
                        Thread.Sleep(25);
                    }
                }

                var view = _dayCycleControlView
                    ?? throw new InvalidOperationException("DAY CYCLE CONTROL CHANNEL IS NOT READY");
                if (view.ReadInt32(0) != DayCycleControlMagic ||
                    view.ReadInt32(4) != DayCycleControlVersion)
                    throw new InvalidOperationException("DAY CYCLE CONTROL CHANNEL VERSION MISMATCH");

                var requested = view.ReadInt32(16) == 0 ? 1 : 0;
                view.Write(20, FilmingDayCycleSpeed);
                view.Write(12, requested);
                Thread.MemoryBarrier();
                view.Write(8, view.ReadInt32(8) + 1);

                for (var attempt = 0; attempt < 30; attempt++)
                {
                    if (view.ReadInt32(16) == requested)
                        return requested != 0 ? "DAY CYCLE ON" : "DAY CYCLE OFF";
                    Thread.Sleep(10);
                }
                throw new TimeoutException("DAY CYCLE DID NOT ACKNOWLEDGE THE REQUEST");
            }
        }
        catch (Exception exception)
        {
            return $"DAY CYCLE FAILED: {ShortMessage(exception)}";
        }
    }

    public string PlayGesture(int gestureId)
    {
        lock (_animationLabGate)
        {
            try
            {
                _ = EnsureInjected();
                _animationLabMapping ??= MemoryMappedFile.OpenExisting(AnimationLabName, MemoryMappedFileRights.ReadWrite);
                _animationLabView ??= _animationLabMapping.CreateViewAccessor(0, AnimationLabSize, MemoryMappedFileAccess.ReadWrite);
                var view = _animationLabView;
                if (view.ReadInt32(0) != AnimationLabMagic || view.ReadInt32(4) != AnimationLabVersion)
                    throw new InvalidOperationException("ANIMATION LAB CHANNEL VERSION MISMATCH");
                int sequence = unchecked(view.ReadInt32(8) + 1);
                view.Write(12, gestureId);
                view.Write(8, sequence);
                view.Flush();
                for (int i = 0; i < 60; i++)
                {
                    if (view.ReadInt32(16) == sequence)
                    {
                        return view.ReadInt32(20) switch
                        {
                            1 => $"GESTURE {gestureId} PLAYED",
                            -1 => "GESTURE FUNCTION UNAVAILABLE FOR THIS GAME BUILD",
                            -2 => "GESTURE CALL WAS SAFELY REJECTED",
                            _ => "GESTURE REQUEST FAILED"
                        };
                    }
                    Thread.Sleep(10);
                }
                throw new TimeoutException("ANIMATION LAB DID NOT ACKNOWLEDGE THE REQUEST");
            }
            catch (Exception exception) { return $"GESTURE FAILED: {ShortMessage(exception)}"; }
        }
    }

    public string ToggleActiveView()
    {
        try
        {
            _ = EnsureInjected();
            _activeViewMapping ??= MemoryMappedFile.OpenExisting(ActiveViewName, MemoryMappedFileRights.ReadWrite);
            _activeViewView ??= _activeViewMapping.CreateViewAccessor(0, ActiveViewSize, MemoryMappedFileAccess.ReadWrite);
            var view = _activeViewView;
            if (view.ReadInt32(0) != ActiveViewMagic || view.ReadInt32(4) != ActiveViewVersion)
                throw new InvalidOperationException("ACTIVE VIEW CHANNEL VERSION MISMATCH");
            int requested = view.ReadInt32(12) == 0 ? 1 : 0;
            view.Write(8, requested); view.Flush();
            for (int i = 0; i < 60; i++)
            {
                if (view.ReadInt32(12) == requested) return requested != 0 ? "ACTIVE VIEW ON" : "ACTIVE VIEW OFF";
                Thread.Sleep(10);
            }
            throw new TimeoutException("ACTIVE VIEW DID NOT ACKNOWLEDGE THE REQUEST");
        }
        catch (Exception exception) { return $"ACTIVE VIEW FAILED: {ShortMessage(exception)}"; }
    }

    public bool IsStandaloneFreecamEnabled()
    {
        try
        {
            _activeViewMapping ??= MemoryMappedFile.OpenExisting(ActiveViewName, MemoryMappedFileRights.ReadWrite);
            _activeViewView ??= _activeViewMapping.CreateViewAccessor(0, ActiveViewSize, MemoryMappedFileAccess.ReadWrite);
            var view = _activeViewView;
            return view.ReadInt32(0) == ActiveViewMagic &&
                   view.ReadInt32(4) == ActiveViewVersion &&
                   view.ReadInt32(12) != 0;
        }
        catch { return false; }
    }

    public string ResetCameraState()
    {
        try
        {
            Process process = EnsureInjected();
            SendGameKey(process, VkR);
            return "CAMERA RESET";
        }
        catch (Exception exception)
        {
            return $"RESET FAILED: {ShortMessage(exception)}";
        }
    }

    public string StepFrame()
    {
        try
        {
            Process process = EnsureInjected();
            EnsureStepFrameKey();
            SendGameKey(process, VkF5);
            Thread.Sleep(100);
            SendGameKey(process, VkF6);
            return "FRAME STEPPED";
        }
        catch (Exception exception)
        {
            return $"STEP FAILED: {ShortMessage(exception)}";
        }
    }

    private void EnsureStepFrameKey()
    {
        string configPath = Path.Combine(AppContext.BaseDirectory, FreecamFolder, "Freecam", "config.ini");
        if (!File.Exists(configPath)) throw new FileNotFoundException("Freecam config missing.", configPath);
        string[] lines = File.ReadAllLines(configPath);
        bool changed = false;
        for (int index = 0; index < lines.Length; index++)
        {
            if (!lines[index].TrimStart().StartsWith("step_frames", StringComparison.OrdinalIgnoreCase)) continue;
            if (!string.Equals(lines[index].Trim(), "step_frames = F6", StringComparison.OrdinalIgnoreCase))
            {
                lines[index] = "step_frames = F6";
                changed = true;
            }
            break;
        }
        if (changed) File.WriteAllLines(configPath, lines);
    }

    public string SetGameSpeed(double speed)
    {
        try
        {
            speed = Math.Clamp(Math.Round(speed / 0.05) * 0.05, 0.05, 2.0);
            Process process = EnsureInjected();
            if (!_speedhackEnabled)
            {
                SendGameKey(process, VkF7);
                _speedhackEnabled = true;
            }

            ResetSpeedhack(process);
            int steps = (int)Math.Round((speed - 1.0) / 0.05);
            if (steps != 0) AdjustSpeedhack(process, Math.Sign(steps), Math.Abs(steps));
            _speedhackSpeed = speed;
            return $"GAME SPEED {_speedhackSpeed:0.00}×";
        }
        catch (Exception exception)
        {
            return $"GAME SPEED FAILED: {ShortMessage(exception)}";
        }
    }

    public string ReloadConfig()
    {
        try
        {
            Process process = EnsureInjected();
            SendGameKey(process, VkF5);
            return "FREECAM CONFIG RELOADED";
        }
        catch (Exception exception)
        {
            return $"CONFIG RELOAD FAILED: {ShortMessage(exception)}";
        }
    }

    public string SuspendForTrack()
    {
        try
        {
            Process process = EnsureInjected();
            SendGameKey(process, VkDelete);
            for (var index = 0; index < 40; index++)
            {
                process.Refresh();
                if (GetLoadedFreecamModule(process) is null) return "FREECAM HOOK SUSPENDED";
                Thread.Sleep(50);
            }
            return "FREECAM SUSPEND FAILED: CAMERA HOOK STAYED LOADED";
        }
        catch (Exception exception)
        {
            return $"FREECAM SUSPEND FAILED: {ShortMessage(exception)}";
        }
    }

    public string ResumeAfterTrack()
    {
        try
        {
            Process process = EnsureInjected();
            SendGameKey(process, VkF1);
            return "FREECAM RESTORED";
        }
        catch (Exception exception)
        {
            return $"FREECAM RESTORE FAILED: {ShortMessage(exception)}";
        }
    }

    public string BeginSynchronizedActionCamera(bool absolutePosition = false)
    {
        try
        {
            _ = EnsureInjected();

            lock (_actionCameraSyncGate)
            {
                for (var attempt = 0; attempt < 40 && _actionCameraSyncMapping is null; attempt++)
                {
                    try
                    {
                        _actionCameraSyncMapping = MemoryMappedFile.OpenExisting(
                            ActionCameraSyncName,
                            MemoryMappedFileRights.ReadWrite);
                    }
                    catch (FileNotFoundException)
                    {
                        Thread.Sleep(25);
                    }
                }
                if (_actionCameraSyncMapping is null)
                    return "ACTION CAMERA SYNC FAILED: COMPATIBLE NATIVE CHANNEL IS NOT READY";
                _actionCameraSyncView ??= _actionCameraSyncMapping.CreateViewAccessor(
                    0,
                    ActionCameraSyncSize,
                    MemoryMappedFileAccess.ReadWrite);
                _actionCameraSyncView.Write(0, ActionCameraSyncMagic);
                _actionCameraSyncView.Write(4, ActionCameraSyncVersion);
                _actionCameraSyncView.Write(8, 0);
                _actionCameraSyncView.Write(12, 0);
                _actionCameraSyncView.Write(96, absolutePosition ? 1 : 0);
                WriteActionCameraInputPolicyLocked();
            }
            return "ACTION CAMERA GAME-FRAME SYNC READY";
        }
        catch (Exception exception)
        {
            return $"ACTION CAMERA SYNC FAILED: {ShortMessage(exception)}";
        }
    }

    private bool _actionKeyboardControlsCamera = true;
    private bool _actionMouseControlsCamera = true;
    private bool _actionControllerControlsPlayer = true;

    public void SetActionCameraInputPolicy(
        bool keyboardControlsCamera,
        bool mouseControlsCamera,
        bool controllerControlsPlayer)
    {
        lock (_actionCameraSyncGate)
        {
            _actionKeyboardControlsCamera = keyboardControlsCamera;
            _actionMouseControlsCamera = mouseControlsCamera;
            _actionControllerControlsPlayer = controllerControlsPlayer;
            WriteActionCameraInputPolicyLocked();
        }
    }

    private void WriteActionCameraInputPolicyLocked()
    {
        if (_actionCameraSyncView is null) return;
        _actionCameraSyncView.Write(84, _actionKeyboardControlsCamera ? 1 : 0);
        _actionCameraSyncView.Write(88, _actionMouseControlsCamera ? 1 : 0);
        _actionCameraSyncView.Write(92, _actionControllerControlsPlayer ? 1 : 0);
    }

    public void WriteSynchronizedActionCamera(
        Vector3 right,
        Vector3 up,
        Vector3 forward,
        Vector3 position,
        float fov)
    {
        lock (_actionCameraSyncGate)
        {
            var view = _actionCameraSyncView
                ?? throw new InvalidOperationException("ACTION CAMERA SYNC IS NOT READY");
            var sequence = view.ReadInt32(12);
            var oddSequence = (sequence & ~1) + 1;
            view.Write(12, oddSequence);
            Thread.MemoryBarrier();
            WriteVector(view, 16, right, 0f);
            WriteVector(view, 32, up, 0f);
            WriteVector(view, 48, forward, 0f);
            WriteVector(view, 64, position, 1f);
            view.Write(80, fov);
            Thread.MemoryBarrier();
            view.Write(12, oddSequence + 1);
            view.Write(8, 1);
        }
    }

    public void EndSynchronizedActionCamera()
    {
        lock (_actionCameraSyncGate)
        {
            _actionCameraSyncView?.Write(8, 0);
        }
    }

    public void Dispose()
    {
        lock (_actionCameraSyncGate)
        {
            _actionCameraSyncView?.Write(8, 0);
            _actionCameraSyncView?.Dispose();
            _actionCameraSyncMapping?.Dispose();
            _actionCameraSyncView = null;
            _actionCameraSyncMapping = null;
        }
        lock (_dayCycleControlGate)
        {
            _dayCycleControlView?.Dispose();
            _dayCycleControlMapping?.Dispose();
            _dayCycleControlView = null;
            _dayCycleControlMapping = null;
        }
        lock (_animationLabGate)
        {
            _animationLabView?.Dispose();
            _animationLabMapping?.Dispose();
            _animationLabView = null;
            _animationLabMapping = null;
        }
        _activeViewView?.Dispose();
        _activeViewMapping?.Dispose();
        _activeViewView = null;
        _activeViewMapping = null;
        lock (_effectControlGate)
        {
            _effectControlView?.Dispose();
            _effectControlMapping?.Dispose();
            _effectControlView = null;
            _effectControlMapping = null;
        }
        lock (_dropRateControlGate)
        {
            _dropRateControlView?.Dispose();
            _dropRateControlMapping?.Dispose();
            _dropRateControlView = null;
            _dropRateControlMapping = null;
        }
        _enemyControlView?.Dispose();
        _enemyControlMapping?.Dispose();
        _enemyControlView = null;
        _enemyControlMapping = null;
    }

    private static void WriteVector(
        MemoryMappedViewAccessor view,
        long offset,
        Vector3 vector,
        float w)
    {
        view.Write(offset, vector.X);
        view.Write(offset + 4, vector.Y);
        view.Write(offset + 8, vector.Z);
        view.Write(offset + 12, w);
    }

    public string SaveCameraState(int slot)
    {
        if (slot is < 0 or > 9) return "CAMERA POINT FAILED: INVALID SLOT";
        try
        {
            Process process = EnsureInjected();
            SendChord(process, VkControl, (ushort)(0x30 + slot));
            return $"CAMERA POINT {slot + 1} SAVED";
        }
        catch (Exception exception)
        {
            return $"CAMERA POINT FAILED: {ShortMessage(exception)}";
        }
    }

    public string PlayCameraStates(int[] slots)
    {
        if (slots.Length < 2) return "ADD AT LEAST TWO CAMERA POINTS";
        try
        {
            Process process = EnsureInjected();
            BringGameToFront(process);
            Thread.Sleep(ForegroundDelayMilliseconds);
            foreach (int slot in slots)
            {
                ushort key = (ushort)(0x30 + slot);
                PostKey(process.MainWindowHandle, key, true);
                TrySendKeyboardInput(key, 0);
                Thread.Sleep(45);
            }
            foreach (int slot in Enumerable.Reverse(slots))
            {
                ushort key = (ushort)(0x30 + slot);
                PostKey(process.MainWindowHandle, key, false);
                TrySendKeyboardInput(key, KeyEventKeyUp);
            }
            return $"CAMERA SEQUENCE PLAYING — {slots.Length} POINTS";
        }
        catch (Exception exception)
        {
            return $"CAMERA SEQUENCE FAILED: {ShortMessage(exception)}";
        }
    }

    private static void SendChord(Process process, ushort modifier, ushort key)
    {
        BringGameToFront(process);
        Thread.Sleep(ForegroundDelayMilliseconds);
        PostKey(process.MainWindowHandle, modifier, true);
        TrySendKeyboardInput(modifier, 0);
        Thread.Sleep(35);
        try { SendGameKey(process, key); }
        finally
        {
            PostKey(process.MainWindowHandle, modifier, false);
            TrySendKeyboardInput(modifier, KeyEventKeyUp);
        }
    }

    private static void ResetSpeedhack(Process process)
    {
        BringGameToFront(process);
        Thread.Sleep(ForegroundDelayMilliseconds);
        PostKey(process.MainWindowHandle, VkControl, true);
        TrySendKeyboardInput(VkControl, 0);
        Thread.Sleep(30);
        SendGameKey(process, VkV);
        PostKey(process.MainWindowHandle, VkControl, false);
        TrySendKeyboardInput(VkControl, KeyEventKeyUp);
        Thread.Sleep(50);
    }

    private static void AdjustSpeedhack(Process process, int direction, int count)
    {
        PostKey(process.MainWindowHandle, VkV, true);
        TrySendKeyboardInput(VkV, 0);
        Thread.Sleep(30);
        try
        {
            int wheelDelta = direction * 120;
            for (int index = 0; index < count; index++)
            {
                mouse_event(MouseEventWheel, 0, 0, unchecked((uint)wheelDelta), UIntPtr.Zero);
                Thread.Sleep(8);
            }
        }
        finally
        {
            PostKey(process.MainWindowHandle, VkV, false);
            TrySendKeyboardInput(VkV, KeyEventKeyUp);
        }
    }

    private Process EnsureInjected()
    {
        if (!File.Exists(FreecamDllPath))
        {
            throw new FileNotFoundException("Bundled freecam DLL missing.", FreecamDllPath);
        }

        Process process = FindGameProcess() ?? throw new InvalidOperationException("ELDEN RING NOT RUNNING");
        if (GetLoadedFreecamModule(process) is not null)
        {
            return process;
        }

        if (!IsBundledFreecamLoaded(process))
        {
            InjectDll(process, FreecamDllPath);
            for (int i = 0; i < 20 && !IsBundledFreecamLoaded(process); i++)
            {
                process.Refresh();
                Thread.Sleep(100);
            }

            Thread.Sleep(250);

            if (!IsBundledFreecamLoaded(process))
            {
                throw new InvalidOperationException(GetNativeFreecamLoadFailure());
            }
        }

        return process;
    }

    private static bool IsBundledFreecamLoaded(Process process)
    {
        return string.Equals(GetLoadedFreecamModule(process), FreecamDll, StringComparison.OrdinalIgnoreCase);
    }

    private static string? GetLoadedFreecamModule(Process process)
    {
        try
        {
            return process.Modules
                .OfType<ProcessModule>()
                .Select(module => Path.GetFileName(module.FileName))
                .FirstOrDefault(moduleName =>
                    string.Equals(moduleName, FreecamDll, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(moduleName, ExternalFreecamDll, StringComparison.OrdinalIgnoreCase));
        }
        catch
        {
            return null;
        }
    }

    private static string? GetLoadedFreecamConfigPath(Process process)
    {
        try
        {
            var modulePath = process.Modules
                .OfType<ProcessModule>()
                .Select(module => module.FileName)
                .FirstOrDefault(path =>
                    string.Equals(Path.GetFileName(path), FreecamDll, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(Path.GetFileName(path), ExternalFreecamDll, StringComparison.OrdinalIgnoreCase));
            return string.IsNullOrWhiteSpace(modulePath)
                ? null
                : Path.Combine(Path.GetDirectoryName(modulePath)!, "Freecam", "config.ini");
        }
        catch { return null; }
    }

    private string GetNativeFreecamLoadFailure()
    {
        string logPath = Path.Combine(AppContext.BaseDirectory, FreecamFolder, "Freecam", "log.txt");
        if (File.Exists(logPath))
        {
            string logText = File.ReadAllText(logPath);
            if (logText.Contains("Failed to find UpdateCameraMatrixFunc", StringComparison.OrdinalIgnoreCase))
            {
                return "CAMERA HOOK ALREADY CLAIMED; RESTART ELDEN RING WITHOUT THE OLD FREECAM DLL";
            }

            string? lastError = logText
                .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
                .LastOrDefault(line => line.Contains("[ERROR]", StringComparison.OrdinalIgnoreCase));
            if (!string.IsNullOrWhiteSpace(lastError))
            {
                return lastError.Replace("[ERROR]", string.Empty, StringComparison.OrdinalIgnoreCase).Trim();
            }
        }

        return "BUNDLED FREECAM DID NOT STAY LOADED";
    }

    private static Process? FindGameProcess()
    {
        return Process.GetProcessesByName(ProcessName)
            .OrderByDescending(process => process.MainWindowHandle != IntPtr.Zero)
            .FirstOrDefault();
    }

    private static void SendGameKey(Process process, ushort virtualKey)
    {
        if (process.MainWindowHandle == IntPtr.Zero)
        {
            throw new InvalidOperationException("GAME WINDOW NOT READY");
        }

        BringGameToFront(process);

        Thread.Sleep(ForegroundDelayMilliseconds);
        bool keyDownPosted = false;
        try
        {
            PostKey(process.MainWindowHandle, virtualKey, keyDown: true);
            keyDownPosted = true;
            TrySendKeyboardInput(virtualKey, 0);
            Thread.Sleep(KeyHoldMilliseconds);
        }
        finally
        {
            if (keyDownPosted)
            {
                PostKey(process.MainWindowHandle, virtualKey, keyDown: false);
            }

            TrySendKeyboardInput(virtualKey, KeyEventKeyUp);
        }
    }

    private static void BringGameToFront(Process process)
    {
        IntPtr windowHandle = process.MainWindowHandle;
        if (windowHandle == IntPtr.Zero)
        {
            throw new InvalidOperationException("GAME WINDOW NOT READY");
        }

        ShowWindow(windowHandle, SwRestore);

        uint foregroundThreadId = GetWindowThreadProcessId(GetForegroundWindow(), out _);
        uint gameThreadId = GetWindowThreadProcessId(windowHandle, out _);
        uint currentThreadId = GetCurrentThreadId();

        bool attachedForeground = false;
        bool attachedGame = false;
        try
        {
            if (foregroundThreadId != 0 && foregroundThreadId != currentThreadId)
            {
                attachedForeground = AttachThreadInput(currentThreadId, foregroundThreadId, true);
            }

            if (gameThreadId != 0 && gameThreadId != currentThreadId)
            {
                attachedGame = AttachThreadInput(currentThreadId, gameThreadId, true);
            }

            BringWindowToTop(windowHandle);
            SetForegroundWindow(windowHandle);
        }
        finally
        {
            if (attachedGame)
            {
                AttachThreadInput(currentThreadId, gameThreadId, false);
            }

            if (attachedForeground)
            {
                AttachThreadInput(currentThreadId, foregroundThreadId, false);
            }
        }
    }

    private static void TrySendKeyboardInput(ushort virtualKey, uint flags)
    {
        INPUT[] inputs = [KeyboardInput(virtualKey, flags)];

        uint sent = SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
        _ = sent;
    }

    private static void PostKey(IntPtr windowHandle, ushort virtualKey, bool keyDown)
    {
        uint scanCode = MapVirtualKey(virtualKey, 0);
        nint lParam = keyDown
            ? 1 | ((nint)scanCode << 16)
            : 1 | ((nint)scanCode << 16) | (1 << 30) | unchecked((nint)0x80000000);

        if (!PostMessage(windowHandle, keyDown ? WmKeyDown : WmKeyUp, virtualKey, lParam))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not post FreecamMod hotkey.");
        }
    }

    private static INPUT KeyboardInput(ushort virtualKey, uint flags)
    {
        return new INPUT
        {
            type = InputKeyboard,
            Anonymous = new InputUnion
            {
                ki = new KEYBDINPUT
                {
                    wVk = virtualKey,
                    wScan = 0,
                    dwFlags = flags,
                    time = 0,
                    dwExtraInfo = UIntPtr.Zero
                }
            }
        };
    }

    private static void InjectDll(Process process, string dllPath)
    {
        IntPtr processHandle = OpenProcess(
            ProcessCreateThread |
            ProcessQueryInformation |
            ProcessVirtualMemoryOperation |
            ProcessVirtualMemoryWrite |
            ProcessVirtualMemoryRead,
            false,
            process.Id);
        if (processHandle == IntPtr.Zero)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not open Elden Ring process.");
        }

        IntPtr remoteString = IntPtr.Zero;
        bool remoteThreadCompleted = false;
        try
        {
            IntPtr loadLibrary = GetProcAddress(GetModuleHandle("kernel32.dll"), "LoadLibraryW");
            if (loadLibrary == IntPtr.Zero)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not resolve LoadLibraryW.");
            }

            byte[] pathBytes = System.Text.Encoding.Unicode.GetBytes(dllPath + '\0');
            remoteString = VirtualAllocEx(
                processHandle,
                IntPtr.Zero,
                (UIntPtr)pathBytes.Length,
                MemCommit | MemReserve,
                PageReadWrite);
            if (remoteString == IntPtr.Zero)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not allocate FreecamMod path.");
            }

            if (!WriteProcessMemory(processHandle, remoteString, pathBytes, pathBytes.Length, out IntPtr written) ||
                written.ToInt64() != pathBytes.Length)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not write FreecamMod path.");
            }

            IntPtr thread = CreateRemoteThread(
                processHandle,
                IntPtr.Zero,
                0,
                loadLibrary,
                remoteString,
                0,
                IntPtr.Zero);
            if (thread == IntPtr.Zero)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not start FreecamMod loader thread.");
            }

            try
            {
                uint waitResult = WaitForSingleObject(thread, DllLoadTimeoutMilliseconds);
                if (waitResult != WaitObject0)
                {
                    throw new TimeoutException("Timed out waiting for FreecamMod to load.");
                }

                remoteThreadCompleted = true;
                if (!GetExitCodeThread(thread, out uint exitCode) || exitCode == 0)
                {
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "FreecamMod loader did not return a module.");
                }
            }
            finally
            {
                CloseHandle(thread);
            }
        }
        finally
        {
            if (remoteString != IntPtr.Zero && remoteThreadCompleted)
            {
                VirtualFreeEx(processHandle, remoteString, UIntPtr.Zero, MemRelease);
            }

            CloseHandle(processHandle);
        }
    }

    private static string ShortMessage(Exception exception)
    {
        string message = exception.Message;
        return message.Length <= 42 ? message : message[..42];
    }

    private const uint ProcessCreateThread = 0x0002;
    private const uint ProcessQueryInformation = 0x0400;
    private const uint ProcessVirtualMemoryOperation = 0x0008;
    private const uint ProcessVirtualMemoryRead = 0x0010;
    private const uint ProcessVirtualMemoryWrite = 0x0020;
    private const uint MemCommit = 0x1000;
    private const uint MemReserve = 0x2000;
    private const uint MemRelease = 0x8000;
    private const uint PageReadWrite = 0x04;
    private const uint WaitObject0 = 0x00000000;
    private const uint DllLoadTimeoutMilliseconds = 10000;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint inputCount, INPUT[] inputs, int size);

    [DllImport("user32.dll")]
    private static extern void mouse_event(uint flags, uint dx, uint dy, uint data, UIntPtr extraInfo);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr windowHandle);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool BringWindowToTop(IntPtr windowHandle);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(IntPtr windowHandle, int commandShow);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetWindowThreadProcessId(IntPtr windowHandle, out uint processId);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool attach);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostMessage(IntPtr windowHandle, uint message, UIntPtr wParam, nint lParam);

    [DllImport("user32.dll")]
    private static extern uint MapVirtualKey(uint code, uint mapType);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint desiredAccess, bool inheritHandle, int processId);

    [DllImport("kernel32.dll", CharSet = CharSet.Ansi, ExactSpelling = true, SetLastError = true)]
    private static extern IntPtr GetProcAddress(IntPtr moduleHandle, string procName);

    [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern IntPtr GetModuleHandle(string moduleName);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr VirtualAllocEx(
        IntPtr processHandle,
        IntPtr address,
        UIntPtr size,
        uint allocationType,
        uint protection);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool VirtualFreeEx(IntPtr processHandle, IntPtr address, UIntPtr size, uint freeType);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WriteProcessMemory(
        IntPtr processHandle,
        IntPtr baseAddress,
        byte[] buffer,
        int size,
        out IntPtr numberOfBytesWritten);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateRemoteThread(
        IntPtr processHandle,
        IntPtr threadAttributes,
        uint stackSize,
        IntPtr startAddress,
        IntPtr parameter,
        uint creationFlags,
        IntPtr threadId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetExitCodeThread(IntPtr thread, out uint exitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT
    {
        public uint type;
        public InputUnion Anonymous;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)]
        public KEYBDINPUT ki;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT
    {
        public ushort wVk;
        public ushort wScan;
        public uint dwFlags;
        public uint time;
        public UIntPtr dwExtraInfo;
    }
}

internal sealed class NativeGameControlBridge : IDisposable
{
    private const string ProcessName = "eldenring";
    private const string FieldAreaPattern =
        "48 8B 3D ?? ?? ?? ?? 49 8B D8 48 8B F2 4C 8B F1 48 85 FF";
    private const string GameDataManPattern =
        "48 8B 05 ?? ?? ?? ?? 48 85 C0 74 05 48 8B 40 58 C3 C3";
    private const string WindowPattern =
        "48 8B 0D ?? ?? ?? ?? 48 85 C9 74 ?? 48 83 C1 ?? 48 8D 45";
    private const string RealGamePausePatchPattern =
        "C1 E8 12 A8 01 74 3E 48 8B 0D ?? ?? ?? ?? C6 05 ?? ?? ?? ?? 01 48 85 C9 74 05";
    private const string RealGamePausePatchPausedPattern =
        "B0 01 90 A8 01 74 3E 48 8B 0D ?? ?? ?? ?? C6 05 ?? ?? ?? ?? 01 48 85 C9 74 05";
    private const string FallbackGamePausePatchPattern =
        "0F 84 ?? ?? ?? ?? C6 ?? ?? ?? ?? ?? 00 ?? 8D ?? ?? ?? ?? ?? ?? 89 ?? ?? 89 ?? ?? ?? 8B ?? ?? ?? ?? ?? ?? 85 ?? 75";
    private const string FallbackGamePausePatchPausedPattern =
        "0F 85 ?? ?? ?? ?? C6 ?? ?? ?? ?? ?? 00 ?? 8D ?? ?? ?? ?? ?? ?? 89 ?? ?? 89 ?? ?? ?? 8B ?? ?? ?? ?? ?? ?? 85 ?? 75";
    private const string HigherLodsPattern =
        "0F 28 F0 F3 0F 10 87 ?? ?? ?? ?? 0F 57 C9 0F 2F C1 76 0B";
    private const string HigherLodsPatchedPattern =
        "0F 57 F6 F3 0F 10 87 ?? ?? ?? ?? 0F 57 C9 0F 2F C1 76 0B";
    private const string FullRateAnimationsPattern =
        "E8 ?? ?? ?? ?? 0F 28 ?? 0F 28 ?? E8 ?? ?? ?? ?? F3 0F ?? ?? 0F 28 ?? F3 41 0F 5E 4C 24 54";
    private const string FullRateAnimationsPatchedPattern =
        "E8 ?? ?? ?? ?? 0F 28 ?? 0F 28 ?? E8 ?? ?? ?? ?? F3 0F ?? ?? 0F 28 ?? 0F 57 C9 66 0F EF C9";
    private const string FullRateAnimationsCurrentPattern =
        "0F 28 C6 E8 ?? ?? ?? ?? 0F 28 F8 0F 28 C6 E8 ?? ?? ?? ?? F3 0F 5E F8 0F 28 CF F3 41 0F 5E 4C 24 54 41 0F 28 D1 F3 41 0F 59 54 24 58 0F 57 15 ?? ?? ?? ??";
    private const string FullRateAnimationsCurrentPatchedPattern =
        "0F 28 C6 E8 ?? ?? ?? ?? 0F 28 F8 0F 28 C6 E8 ?? ?? ?? ?? F3 0F 5E F8 0F 28 CF 0F 57 C9 66 0F EF C9 41 0F 28 D1 F3 41 0F 59 54 24 58 0F 57 15 ?? ?? ?? ??";
    private const string FullRateAnimationsCurrentLegacyPatchedPattern =
        "0F 28 C6 E8 ?? ?? ?? ?? 0F 28 F8 0F 28 C6 E8 ?? ?? ?? ?? F3 0F 5E F8 0F 28 CF 0F 57 C9 90 90 90 90 41 0F 28 D1 F3 41 0F 59 54 24 58 0F 57 15 ?? ?? ?? ??";

    private const ulong GameDataManOptionDataOffset = 0x58;
    private const ulong OptionDataHudOffset = 0x09;
    private const ulong WindowAntiAliasingOffset = 0xFC;
    private const ulong WindowMotionBlurOffset = 0x108;
    private const ulong FieldAreaGameRendOffset = 0x20;
    private const ulong GameRendPlayerCameraOffset = 0x20;
    private const ulong GameRendFreeCameraModeOffset = 0xC8;
    private const ulong GameRendDebugCameraOffset = 0xD0;
    private const ulong CameraMatrixOffset = 0x10;
    private const int FreecamModeDisabled = 0;
    private const int FreecamModeEnabledUpdating = 2;
    private const int FreecamModeFixed = 3;
    private const byte PauseJumpIfEqual = 0x84;
    private const byte PauseJumpIfNotEqual = 0x85;
    private const byte HudOff = 0;
    private const byte HudOn = 1;
    private const int CameraPollMilliseconds = 16;
    private const float DefaultSpeed = 10.0f;
    private const float SprintMultiplier = 2.5f;
    private const float MouseSensitivity = 1.0f;
    private const float RollSpeed = 0.5f;
    private const float FovSpeed = 0.7f;
    private const float MinFov = 0.000087f;
    private const float MaxFov = 2.7f;
    private const float PitchLimit = 1.55f;
    private const float VelocityFadeSpeed = 14.0f;
    private const float ZoomFadeSpeed = 16.0f;
    private const float MaxZoomStep = 0.05f;
    private const bool HideHudWithFreecam = true;
    private const bool FreezeGameWithFreecam = true;
    private static readonly byte[] HigherLodsOriginalBytes = [0x0F, 0x28, 0xF0];
    private static readonly byte[] HigherLodsPatchBytes = [0x0F, 0x57, 0xF6];
    private static readonly byte[] FullRateAnimationsOriginalBytes = [0xF3, 0x41, 0x0F, 0x5E, 0x4C, 0x24, 0x54];
    private static readonly byte[] FullRateAnimationsPatchBytes = [0x0F, 0x57, 0xC9, 0x66, 0x0F, 0xEF, 0xC9];
    private static readonly byte[] FullRateAnimationsLegacyPatchBytes = [0x0F, 0x57, 0xC9, 0x90, 0x90, 0x90, 0x90];
    private static readonly byte[] RealPauseOriginalBytes = [0xC1, 0xE8, 0x12];
    private static readonly byte[] RealPausePatchBytes = [0xB0, 0x01, 0x90];

    private byte _hudRestoreValue = HudOn;
    private readonly object _freecamLock = new();
    private readonly LowLevelMouseWheelCapture _mouseWheel = new();
    private Thread? _freecamThread;
    private bool _freecamEnabled;
    // Base freecam owns WASD/Space/Shift by default. Attached action camera
    // temporarily releases this so a controller can continue driving the
    // player, then restores the caller's previous state when it exits.
    private bool _blockPlayerInput = true;
    private bool _freecamFreezeEnabled = FreezeGameWithFreecam;
    private bool _freecamAppliedPause;
    private bool _freecamAppliedHud;
    private bool _higherLodsEnabled;
    private bool _antiAliasingDisabled;
    private bool _motionBlurDisabled;
    private byte? _antiAliasingRestoreValue;
    private byte? _motionBlurRestoreValue;
    private Vector3 _position;
    private Vector3 _velocity;
    private float _yaw;
    private float _pitch;
    private float _roll;
    private float _rollVelocity;
    private float _fov = 1.0f;
    private float _initialFov = 1.0f;
    private float _speed = DefaultSpeed;
    private float _zoomVelocity;

    public bool IsFreecamEnabled
    {
        get
        {
            lock (_freecamLock)
            {
                return _freecamEnabled;
            }
        }
    }

    public bool IsPlayerInputBlocked
    {
        get { lock (_freecamLock) return _blockPlayerInput; }
    }

    public string EnsurePlayerInputEnabled()
    {
        lock (_freecamLock)
        {
            if (!_blockPlayerInput) return "PLAYER INPUT READY";
            _blockPlayerInput = false;
            return "PLAYER INPUT UNBLOCKED FOR ACTION CAM";
        }
    }

    public string EnsurePlayerInputBlocked()
    {
        lock (_freecamLock)
        {
            if (_blockPlayerInput) return "PLAYER INPUT BLOCKED";
            _blockPlayerInput = true;
            if (!_freecamEnabled) return "PLAYER INPUT BLOCK ARMED";

            try
            {
                using MemoryReader memory = new(ProcessName);
                CameraPointers pointers = ResolveCameraPointers(memory);
                memory.WriteBytes(
                    pointers.GameRend + GameRendFreeCameraModeOffset,
                    BitConverter.GetBytes(FreecamModeEnabledUpdating));
                return "PLAYER INPUT BLOCKED";
            }
            catch (Exception exception)
            {
                return $"PLAYER INPUT BLOCK FAILED: {ShortMessage(exception)}";
            }
        }
    }

    public void StartActionCameraInput()
    {
        _mouseWheel.Start();
        _mouseWheel.ResetDeltas();
    }

    public void StopActionCameraInput() => _mouseWheel.Stop();

    public (int X, int Y, float Wheel) TakeActionCameraInput()
    {
        var mouse = _mouseWheel.TakeMouseDelta();
        return (mouse.X, mouse.Y, _mouseWheel.TakeScrollDelta());
    }

    public string TogglePause()
    {
        try
        {
            using MemoryReader memory = new(ProcessName);
            PausePatch patch = ResolvePausePatch(memory);

            if (patch.IsRealEnginePause)
            {
                byte[] currentBytes = memory.ReadBytes(patch.Address, RealPausePatchBytes.Length);
                if (currentBytes.SequenceEqual(RealPauseOriginalBytes))
                {
                    memory.WriteExecutableBytes(patch.Address, RealPausePatchBytes);
                    return "REAL PAUSE ON";
                }

                if (currentBytes.SequenceEqual(RealPausePatchBytes))
                {
                    memory.WriteExecutableBytes(patch.Address, RealPauseOriginalBytes);
                    return "REAL PAUSE OFF";
                }

                return $"REAL PAUSE BYTES {Convert.ToHexString(currentBytes)}";
            }

            byte current = memory.ReadBytes(patch.Address, 1)[0];
            if (current == PauseJumpIfEqual)
            {
                WritePauseByte(memory, patch.Address, PauseJumpIfNotEqual);
                return "GAME PAUSED";
            }

            if (current == PauseJumpIfNotEqual)
            {
                WritePauseByte(memory, patch.Address, PauseJumpIfEqual);
                return "GAME UNPAUSED";
            }

            return $"PAUSE BYTE {current:X2}";
        }
        catch (Exception exception)
        {
            return $"PAUSE FAILED: {ShortMessage(exception)}";
        }
    }

    private static bool IsGamePaused(MemoryReader memory)
    {
        PausePatch patch = ResolvePausePatch(memory);
        if (patch.IsRealEnginePause)
        {
            return memory.ReadBytes(patch.Address, RealPausePatchBytes.Length).SequenceEqual(RealPausePatchBytes);
        }

        return memory.ReadBytes(patch.Address, 1)[0] == PauseJumpIfNotEqual;
    }

    private static bool SetGamePaused(MemoryReader memory, bool paused)
    {
        PausePatch patch = ResolvePausePatch(memory);
        if (patch.IsRealEnginePause)
        {
            byte[] desired = paused ? RealPausePatchBytes : RealPauseOriginalBytes;
            byte[] currentBytes = memory.ReadBytes(patch.Address, desired.Length);
            if (currentBytes.SequenceEqual(desired))
            {
                return false;
            }

            memory.WriteExecutableBytes(patch.Address, desired);
            return true;
        }

        byte desiredByte = paused ? PauseJumpIfNotEqual : PauseJumpIfEqual;
        byte current = memory.ReadBytes(patch.Address, 1)[0];
        if (current == desiredByte)
        {
            return false;
        }

        WritePauseByte(memory, patch.Address, desiredByte);
        return true;
    }

    private static void WritePauseByte(MemoryReader memory, ulong patchAddress, byte value)
    {
        memory.WriteExecutableBytes(patchAddress, [value]);
    }

    private static PausePatch ResolvePausePatch(MemoryReader memory)
    {
        try
        {
            return new PausePatch(memory.ScanModule(RealGamePausePatchPattern), IsRealEnginePause: true);
        }
        catch
        {
            try
            {
                return new PausePatch(memory.ScanModule(RealGamePausePatchPausedPattern), IsRealEnginePause: true);
            }
            catch
            {
                try
                {
                    return new PausePatch(memory.ScanModule(FallbackGamePausePatchPattern) + 1, IsRealEnginePause: false);
                }
                catch
                {
                    return new PausePatch(memory.ScanModule(FallbackGamePausePatchPausedPattern) + 1, IsRealEnginePause: false);
                }
            }
        }
    }

    public string ToggleHud()
    {
        try
        {
            using MemoryReader memory = new(ProcessName);
            ulong hudAddress = ResolveHudAddress(memory);
            byte current = memory.ReadBytes(hudAddress, 1)[0];
            if (current == HudOff)
            {
                memory.WriteBytes(hudAddress, [_hudRestoreValue == HudOff ? HudOn : _hudRestoreValue]);
                return "HUD VISIBLE";
            }

            _hudRestoreValue = current;
            memory.WriteBytes(hudAddress, [HudOff]);
            return "HUD HIDDEN";
        }
        catch (Exception exception)
        {
            return $"HUD FAILED: {ShortMessage(exception)}";
        }
    }

    private static ulong ResolveHudAddress(MemoryReader memory)
    {
        ulong gameDataMan = MainForm.ResolveGlobalPointerOrZero(memory, GameDataManPattern);
        if (!memory.IsLikelyPointer(gameDataMan))
        {
            throw new InvalidOperationException("HUD data missing.");
        }

        ulong optionData = memory.ReadUInt64(gameDataMan + GameDataManOptionDataOffset);
        if (!memory.IsLikelyPointer(optionData))
        {
            throw new InvalidOperationException("HUD option missing.");
        }

        return optionData + OptionDataHudOffset;
    }

    public string ToggleHigherLods()
    {
        try
        {
            using MemoryReader memory = new(ProcessName);
            ulong lodAddress = ResolveHigherLodsAddress(memory);
            ulong animationAddress = ResolveFullRateAnimationsAddress(memory);
            byte[] currentLod = memory.ReadBytes(lodAddress, HigherLodsOriginalBytes.Length);
            byte[] currentAnimation = memory.ReadBytes(animationAddress, FullRateAnimationsOriginalBytes.Length);

            bool lodEnabled = currentLod.SequenceEqual(HigherLodsPatchBytes);
            bool animationEnabled = currentAnimation.SequenceEqual(FullRateAnimationsPatchBytes) ||
                                    currentAnimation.SequenceEqual(FullRateAnimationsLegacyPatchBytes);
            bool lodKnown = lodEnabled || currentLod.SequenceEqual(HigherLodsOriginalBytes);
            bool animationKnown = animationEnabled || currentAnimation.SequenceEqual(FullRateAnimationsOriginalBytes);

            if (!lodKnown || !animationKnown)
                return $"LOD BYTES {Convert.ToHexString(currentLod)} / ANIM {Convert.ToHexString(currentAnimation)}";

            if (lodEnabled && animationEnabled)
            {
                memory.WriteExecutableBytes(lodAddress, HigherLodsOriginalBytes);
                memory.WriteExecutableBytes(animationAddress, FullRateAnimationsOriginalBytes);
                _higherLodsEnabled = false;
                return "CAMERA DETAIL OFF";
            }

            // Complete either half of an older/partial patch instead of toggling
            // the already-active half back off.
            if (!lodEnabled) memory.WriteExecutableBytes(lodAddress, HigherLodsPatchBytes);
            if (!animationEnabled) memory.WriteExecutableBytes(animationAddress, FullRateAnimationsPatchBytes);
            _higherLodsEnabled = true;
            return "CAMERA DETAIL ON — FULL-RATE EDGE ANIMATIONS";
        }
        catch (Exception exception)
        {
            return $"LOD FAILED: {ShortMessage(exception)}";
        }
    }

    private static ulong ResolveHigherLodsAddress(MemoryReader memory)
    {
        try
        {
            return memory.ScanModule(HigherLodsPattern);
        }
        catch
        {
            return memory.ScanModule(HigherLodsPatchedPattern);
        }
    }

    private static ulong ResolveFullRateAnimationsAddress(MemoryReader memory)
    {
        try
        {
            return memory.ScanModule(FullRateAnimationsCurrentPattern) + 26;
        }
        catch
        {
            try
            {
                return memory.ScanModule(FullRateAnimationsCurrentPatchedPattern) + 26;
            }
            catch
            {
                try
                {
                    return memory.ScanModule(FullRateAnimationsCurrentLegacyPatchedPattern) + 26;
                }
                catch
                {
                    try
                    {
                        return memory.ScanModule(FullRateAnimationsPattern) + 23;
                    }
                    catch
                    {
                        return memory.ScanModule(FullRateAnimationsPatchedPattern) + 23;
                    }
                }
            }
        }
    }

    public string ToggleAntiAliasing()
    {
        return ToggleWindowByteOption(
            "AA DISABLE",
            WindowAntiAliasingOffset,
            ref _antiAliasingDisabled,
            ref _antiAliasingRestoreValue);
    }

    public string ToggleMotionBlur()
    {
        return ToggleWindowByteOption(
            "BLUR DISABLE",
            WindowMotionBlurOffset,
            ref _motionBlurDisabled,
            ref _motionBlurRestoreValue);
    }

    private static string ToggleWindowByteOption(
        string label,
        ulong offset,
        ref bool disabled,
        ref byte? restoreValue)
    {
        try
        {
            using MemoryReader memory = new(ProcessName);
            ulong address = ResolveWindowOptionAddress(memory, offset);
            byte current = memory.ReadBytes(address, 1)[0];
            if (!disabled)
            {
                restoreValue = current;
                if (current != 0)
                {
                    memory.WriteBytes(address, [0]);
                }

                disabled = true;
                return $"{label} ON";
            }

            byte value = restoreValue.HasValue && restoreValue.Value > 0
                ? restoreValue.Value
                : (byte)3;
            memory.WriteBytes(address, [value]);
            restoreValue = null;
            disabled = false;
            return $"{label} OFF";
        }
        catch (Exception exception)
        {
            return $"{label} FAILED: {ShortMessage(exception)}";
        }
    }

    private static ulong ResolveWindowOptionAddress(MemoryReader memory, ulong offset)
    {
        ulong window = MainForm.ResolveGlobalPointerOrZero(memory, WindowPattern);
        if (!memory.IsLikelyPointer(window))
        {
            throw new InvalidOperationException("Window options missing.");
        }

        return window + offset;
    }

    private void ApplyFreecamHud(MemoryReader memory, bool enabled)
    {
        ulong hudAddress = ResolveHudAddress(memory);
        byte current = memory.ReadBytes(hudAddress, 1)[0];
        if (enabled)
        {
            if (current == HudOff)
            {
                _freecamAppliedHud = false;
                return;
            }

            _hudRestoreValue = current;
            _freecamAppliedHud = true;
            memory.WriteBytes(hudAddress, [HudOff]);
            return;
        }

        if (_freecamAppliedHud)
        {
            memory.WriteBytes(hudAddress, [_hudRestoreValue == HudOff ? HudOn : _hudRestoreValue]);
            _freecamAppliedHud = false;
        }
    }

    public string ToggleFreecam()
    {
        lock (_freecamLock)
        {
            return _freecamEnabled ? DisableFreecamLocked() : EnableFreecamLocked();
        }
    }

    public string ToggleInputBlock()
    {
        lock (_freecamLock)
        {
            _blockPlayerInput = !_blockPlayerInput;

            try
            {
                using MemoryReader memory = new(ProcessName);
                CameraPointers pointers = ResolveCameraPointers(memory);
                int currentMode = BitConverter.ToInt32(
                    memory.ReadBytes(pointers.GameRend + GameRendFreeCameraModeOffset, sizeof(int)),
                    0);
                if (currentMode == FreecamModeDisabled && !_freecamEnabled)
                {
                    return _blockPlayerInput ? "BLOCK INPUT ARMED" : "BLOCK INPUT OFF";
                }

                memory.WriteBytes(
                    pointers.GameRend + GameRendFreeCameraModeOffset,
                    BitConverter.GetBytes(_blockPlayerInput ? FreecamModeEnabledUpdating : FreecamModeFixed));
                return _blockPlayerInput ? "PLAYER INPUT BLOCKED" : "PLAYER INPUT UNBLOCKED";
            }
            catch (Exception exception)
            {
                return $"BLOCK FAILED: {ShortMessage(exception)}";
            }
        }
    }

    public string ResetFov()
    {
        lock (_freecamLock)
        {
            if (!_freecamEnabled)
            {
                return "FREECAM NOT ACTIVE";
            }

            _fov = _initialFov;
            return "FOV RESET";
        }
    }

    public void Dispose()
    {
        lock (_freecamLock)
        {
            if (_freecamEnabled)
            {
                DisableFreecamLocked();
            }

            RestoreHigherLodsIfNeeded();
            RestoreWindowByteOptionIfNeeded(
                WindowAntiAliasingOffset,
                ref _antiAliasingDisabled,
                ref _antiAliasingRestoreValue);
            RestoreWindowByteOptionIfNeeded(
                WindowMotionBlurOffset,
                ref _motionBlurDisabled,
                ref _motionBlurRestoreValue);
        }

        _mouseWheel.Dispose();
    }

    private static void RestoreWindowByteOptionIfNeeded(
        ulong offset,
        ref bool disabled,
        ref byte? restoreValue)
    {
        if (!disabled)
        {
            return;
        }

        try
        {
            using MemoryReader memory = new(ProcessName);
            byte value = restoreValue.HasValue && restoreValue.Value > 0
                ? restoreValue.Value
                : (byte)3;
            memory.WriteBytes(ResolveWindowOptionAddress(memory, offset), [value]);
        }
        catch
        {
            // Elden Ring may have closed before the app did.
        }

        restoreValue = null;
        disabled = false;
    }

    private void RestoreHigherLodsIfNeeded()
    {
        if (!_higherLodsEnabled)
        {
            return;
        }

        try
        {
            using MemoryReader memory = new(ProcessName);
            ulong lodAddress = ResolveHigherLodsAddress(memory);
            if (memory.ReadBytes(lodAddress, HigherLodsPatchBytes.Length).SequenceEqual(HigherLodsPatchBytes))
            {
                memory.WriteExecutableBytes(lodAddress, HigherLodsOriginalBytes);
            }

            ulong animationAddress = ResolveFullRateAnimationsAddress(memory);
            byte[] animationBytes = memory.ReadBytes(animationAddress, FullRateAnimationsPatchBytes.Length);
            if (animationBytes.SequenceEqual(FullRateAnimationsPatchBytes) ||
                animationBytes.SequenceEqual(FullRateAnimationsLegacyPatchBytes))
            {
                memory.WriteExecutableBytes(animationAddress, FullRateAnimationsOriginalBytes);
            }
        }
        catch
        {
            // Elden Ring may have closed before the app did.
        }

        _higherLodsEnabled = false;
    }

    private string EnableFreecamLocked()
    {
        try
        {
            using MemoryReader memory = new(ProcessName);
            CameraPointers pointers = ResolveCameraPointers(memory);
            CameraState player = ReadCamera(memory, pointers.PlayerCamera);
            _freecamAppliedPause = false;
            _freecamAppliedHud = false;
            WriteCamera(memory, pointers.DebugCamera, player);
            ApplyFreecamHud(memory, enabled: true);
            _freecamAppliedPause = FreezeGameWithFreecam &&
                !IsGamePaused(memory) &&
                SetGamePaused(memory, paused: true);
            memory.WriteBytes(
                pointers.GameRend + GameRendFreeCameraModeOffset,
                BitConverter.GetBytes(_blockPlayerInput ? FreecamModeEnabledUpdating : FreecamModeFixed));

            _position = player.Position;
            _fov = player.Fov;
            _initialFov = player.Fov;
            _speed = DefaultSpeed;
            _velocity = Vector3.Zero;
            _zoomVelocity = 0;
            _rollVelocity = 0;
            _freecamFreezeEnabled = FreezeGameWithFreecam;
            (_yaw, _pitch, _roll) = ToEuler(player.Right, player.Up, player.Forward);
            _mouseWheel.Start();
            _mouseWheel.ResetDeltas();
            _freecamEnabled = true;
            _freecamThread = new Thread(FreecamLoop)
            {
                IsBackground = true,
                Name = "EnemyIntelNativeFreecam"
            };
            _freecamThread.Start();
            return "FREECAM ENABLED";
        }
        catch (Exception exception)
        {
            _freecamEnabled = false;
            _mouseWheel.Stop();
            TryRestoreFreecamSideEffects();
            return $"FREECAM FAILED: {ShortMessage(exception)}";
        }
    }

    private string DisableFreecamLocked()
    {
        _freecamEnabled = false;
        _mouseWheel.Stop();
        try
        {
            using MemoryReader memory = new(ProcessName);
            CameraPointers pointers = ResolveCameraPointers(memory);
            memory.WriteBytes(
                pointers.GameRend + GameRendFreeCameraModeOffset,
                BitConverter.GetBytes(FreecamModeDisabled));
            ApplyFreecamHud(memory, enabled: false);
            if (_freecamAppliedPause)
            {
                SetGamePaused(memory, paused: false);
                _freecamAppliedPause = false;
            }
        }
        catch
        {
            // The game may have closed while freecam was active.
        }

        return "FREECAM DISABLED";
    }

    private void FreecamLoop()
    {
        try
        {
            using MemoryReader memory = new(ProcessName);
            CameraPointers pointers = ResolveCameraPointers(memory);
            long lastTicks = Environment.TickCount64;

            while (_freecamEnabled && !memory.Process.HasExited)
            {
                long now = Environment.TickCount64;
                float dt = Math.Clamp((now - lastTicks) / 1000.0f, 0.001f, 0.08f);
                lastTicks = now;

                if (!IsGameForeground(memory.Process))
                {
                    _mouseWheel.ResetDeltas();
                    _velocity = Vector3.Zero;
                    _zoomVelocity = 0;
                    _rollVelocity = 0;
                    Thread.Sleep(CameraPollMilliseconds);
                    continue;
                }

                if (IsKeyJustPressed(VkR) && !IsKeyDown(VkControl))
                {
                    ResetCameraState(memory, pointers);
                }

                if (IsKeyJustPressed(VkP))
                {
                    ToggleFreecamFreeze(memory);
                }

                UpdateFreecamState(dt);
                CameraState state = BuildCameraState();
                WriteCamera(memory, pointers.DebugCamera, state);

                Thread.Sleep(CameraPollMilliseconds);
            }
        }
        catch
        {
            _mouseWheel.Stop();
            TryRestoreFreecamSideEffects();
            lock (_freecamLock)
            {
                _freecamEnabled = false;
            }
        }
    }

    private void TryRestoreFreecamSideEffects()
    {
        try
        {
            using MemoryReader memory = new(ProcessName);
            ApplyFreecamHud(memory, enabled: false);
            if (_freecamAppliedPause)
            {
                SetGamePaused(memory, paused: false);
                _freecamAppliedPause = false;
            }
        }
        catch
        {
            _freecamAppliedHud = false;
            _freecamAppliedPause = false;
        }
    }

    private void UpdateFreecamState(float dt)
    {
        var mouseDelta = _mouseWheel.TakeMouseDelta();
        float scrollDelta = _mouseWheel.TakeScrollDelta();
        bool ctrlDown = IsKeyDown(VkControl);
        bool shiftDown = IsKeyDown(VkShift);
        bool independentYawGesture = ctrlDown && shiftDown;
        float zoomFactor = ComputeZoomFactor(_fov);

        float rollSin = MathF.Sin(_roll);
        float rollCos = MathF.Cos(_roll);
        float rotatedMouseX = mouseDelta.X * rollCos + mouseDelta.Y * rollSin;
        float rotatedMouseY = -mouseDelta.X * rollSin + mouseDelta.Y * rollCos;
        float sensitivity = MouseSensitivity * zoomFactor * 0.001f;

        _yaw += rotatedMouseX * sensitivity;
        if (Math.Abs(scrollDelta) > 0.001f && independentYawGesture)
        {
            _yaw += scrollDelta * 0.055f;
        }
        _pitch = Math.Clamp(_pitch + rotatedMouseY * sensitivity, -PitchLimit, PitchLimit);

        float rollInput = (IsKeyDown(VkQ) ? 1f : 0f) - (IsKeyDown(VkE) ? 1f : 0f);
        _rollVelocity += rollInput;
        _roll += _rollVelocity * RollSpeed * dt;
        _rollVelocity = 0;

        CameraBasis basis = BuildBasis(_yaw, _pitch, _roll);
        Vector3 movement = Vector3.Zero;
        movement.Z += (IsKeyDown(VkW) ? 1f : 0f) - (IsKeyDown(VkS) ? 1f : 0f);
        movement.X += (IsKeyDown(VkD) ? 1f : 0f) - (IsKeyDown(VkA) ? 1f : 0f);
        movement.Y += independentYawGesture
            ? 0f
            : (IsKeyDown(VkSpace) ? 1f : 0f) - (shiftDown ? 1f : 0f);
        _velocity += movement;

        Vector3 cameraVelocity =
            basis.Right * _velocity.X +
            Vector3.UnitY * _velocity.Y +
            basis.Forward * _velocity.Z;
        if (cameraVelocity.LengthSquared() > 1.0f)
        {
            cameraVelocity = Vector3.Normalize(cameraVelocity);
        }

        float cameraSpeed = _speed * (IsKeyDown(VkLButton) ? SprintMultiplier : 1.0f) * dt;
        _position += cameraVelocity * cameraSpeed;

        if (Math.Abs(scrollDelta) > 0.001f && !ctrlDown && !IsKeyDown(VkV))
        {
            _speed = Math.Max(_speed + scrollDelta * (0.05f * _speed + 0.01f), 0);
        }

        _zoomVelocity +=
            (IsKeyDown(VkOemMinus) ? 1f : 0f) -
            (IsKeyDown(VkOemPlus) ? 1f : 0f) -
            (ctrlDown && !shiftDown ? scrollDelta : 0f);
        float zoom = _zoomVelocity * FovSpeed * zoomFactor * dt;
        _fov = Math.Clamp(_fov + Math.Clamp(zoom, -MaxZoomStep, MaxZoomStep), MinFov, MaxFov);

        _velocity *= FastEase(VelocityFadeSpeed * dt);
        if (_velocity.LengthSquared() < 0.001f)
        {
            _velocity = Vector3.Zero;
        }

        _zoomVelocity *= 1.0f / (1.0f + ZoomFadeSpeed * dt);
        if (Math.Abs(_zoomVelocity) < 0.001f)
        {
            _zoomVelocity = 0;
        }
    }

    private void ResetCameraState(MemoryReader memory, CameraPointers pointers)
    {
        CameraState player = ReadCamera(memory, pointers.PlayerCamera);
        WriteCamera(memory, pointers.DebugCamera, player);
        _position = player.Position;
        _fov = player.Fov;
        _initialFov = player.Fov;
        _speed = DefaultSpeed;
        _velocity = Vector3.Zero;
        _zoomVelocity = 0;
        _rollVelocity = 0;
        (_yaw, _pitch, _roll) = ToEuler(player.Right, player.Up, player.Forward);
    }

    private void ToggleFreecamFreeze(MemoryReader memory)
    {
        _freecamFreezeEnabled = !_freecamFreezeEnabled;
        if (_freecamFreezeEnabled)
        {
            _freecamAppliedPause = !IsGamePaused(memory) && SetGamePaused(memory, paused: true);
            return;
        }

        if (_freecamAppliedPause)
        {
            SetGamePaused(memory, paused: false);
            _freecamAppliedPause = false;
        }
    }

    private CameraState BuildCameraState()
    {
        CameraBasis basis = BuildBasis(_yaw, _pitch, _roll);
        return new CameraState(basis.Right, basis.Up, basis.Forward, _position, _fov);
    }

    private static CameraPointers ResolveCameraPointers(MemoryReader memory)
    {
        ulong fieldArea = MainForm.ResolveGlobalPointerOrZero(memory, FieldAreaPattern);
        if (!memory.IsLikelyPointer(fieldArea))
        {
            throw new InvalidOperationException("FieldArea missing.");
        }

        ulong gameRend = memory.ReadUInt64(fieldArea + FieldAreaGameRendOffset);
        ulong playerCamera = memory.ReadUInt64(gameRend + GameRendPlayerCameraOffset);
        ulong debugCamera = memory.ReadUInt64(gameRend + GameRendDebugCameraOffset);
        if (!memory.IsLikelyPointer(gameRend) ||
            !memory.IsLikelyPointer(playerCamera) ||
            !memory.IsLikelyPointer(debugCamera))
        {
            throw new InvalidOperationException("Camera pointers missing.");
        }

        return new CameraPointers(gameRend, playerCamera, debugCamera);
    }

    private static CameraState ReadCamera(MemoryReader memory, ulong camera)
    {
        byte[] bytes = memory.ReadBytes(camera + CameraMatrixOffset, 0x44);
        Vector3 right = ReadVector3(bytes, 0x00);
        Vector3 up = ReadVector3(bytes, 0x10);
        Vector3 forward = ReadVector3(bytes, 0x20);
        Vector3 position = ReadVector3(bytes, 0x30);
        float fov = BitConverter.ToSingle(bytes, 0x40);
        return new CameraState(right, up, forward, position, fov);
    }

    private static void WriteCamera(MemoryReader memory, ulong camera, CameraState state)
    {
        byte[] bytes = new byte[0x44];
        WriteVector4(bytes, 0x00, state.Right, 0);
        WriteVector4(bytes, 0x10, state.Up, 0);
        WriteVector4(bytes, 0x20, state.Forward, 0);
        WriteVector4(bytes, 0x30, state.Position, 1);
        BitConverter.GetBytes(state.Fov).CopyTo(bytes, 0x40);
        memory.WriteBytes(camera + CameraMatrixOffset, bytes);
    }

    private static Vector3 ReadVector3(byte[] bytes, int offset)
    {
        return new Vector3(
            BitConverter.ToSingle(bytes, offset),
            BitConverter.ToSingle(bytes, offset + 4),
            BitConverter.ToSingle(bytes, offset + 8));
    }

    private static void WriteVector4(byte[] bytes, int offset, Vector3 vector, float w)
    {
        BitConverter.GetBytes(vector.X).CopyTo(bytes, offset);
        BitConverter.GetBytes(vector.Y).CopyTo(bytes, offset + 4);
        BitConverter.GetBytes(vector.Z).CopyTo(bytes, offset + 8);
        BitConverter.GetBytes(w).CopyTo(bytes, offset + 12);
    }

    private static (float Yaw, float Pitch, float Roll) ToEuler(Vector3 right, Vector3 up, Vector3 forward)
    {
        return (
            MathF.Atan2(forward.X, forward.Z),
            MathF.Asin(Math.Clamp(-forward.Y, -1.0f, 1.0f)),
            MathF.Atan2(-right.Y, up.Y));
    }

    private static CameraBasis BuildBasis(float yaw, float pitch, float roll)
    {
        float sinYaw = MathF.Sin(yaw);
        float cosYaw = MathF.Cos(yaw);
        float sinPitch = MathF.Sin(pitch);
        float cosPitch = MathF.Cos(pitch);
        float sinRoll = MathF.Sin(roll);
        float cosRoll = MathF.Cos(roll);

        Vector3 forward = new(sinYaw * cosPitch, -sinPitch, cosYaw * cosPitch);
        Vector3 right = new(cosYaw, 0, -sinYaw);
        Vector3 up = Vector3.Cross(forward, right);
        Vector3 rolledRight = right * cosRoll + up * sinRoll;
        Vector3 rolledUp = up * cosRoll - right * sinRoll;
        return new CameraBasis(rolledRight, rolledUp, forward);
    }

    private static bool IsKeyDown(int virtualKey)
    {
        return (GetAsyncKeyState(virtualKey) & 0x8000) != 0;
    }

    private static bool IsKeyJustPressed(int virtualKey)
    {
        return (GetAsyncKeyState(virtualKey) & 0x0001) != 0;
    }

    private static bool IsGameForeground(Process process)
    {
        return process.MainWindowHandle != IntPtr.Zero &&
               GetForegroundWindow() == process.MainWindowHandle;
    }

    private static float ComputeZoomFactor(float fov)
    {
        const float minFov = 0.00001f;
        const float maxFov = 3.14f;
        float t = Math.Clamp((fov - minFov) / (maxFov - minFov), 0, 1);
        return 1.0f - ((1.0f - t) * (1.0f - t));
    }

    private static float FastEase(float value)
    {
        return 1.0f / (1.0f + value);
    }

    private static string ShortMessage(Exception exception)
    {
        string message = exception.Message;
        return message.Length <= 42 ? message : message[..42];
    }

    private const int VkLButton = 0x01;
    private const int VkShift = 0x10;
    private const int VkControl = 0x11;
    private const int VkSpace = 0x20;
    private const int VkA = 0x41;
    private const int VkD = 0x44;
    private const int VkE = 0x45;
    private const int VkP = 0x50;
    private const int VkQ = 0x51;
    private const int VkR = 0x52;
    private const int VkS = 0x53;
    private const int VkV = 0x56;
    private const int VkW = 0x57;
    private const int VkOemPlus = 0xBB;
    private const int VkOemMinus = 0xBD;

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int virtualKey);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    private readonly record struct CameraPointers(ulong GameRend, ulong PlayerCamera, ulong DebugCamera);

    private readonly record struct PausePatch(ulong Address, bool IsRealEnginePause);

    private readonly record struct CameraState(
        Vector3 Right,
        Vector3 Up,
        Vector3 Forward,
        Vector3 Position,
        float Fov);

    private readonly record struct CameraBasis(Vector3 Right, Vector3 Up, Vector3 Forward);
}

internal sealed class LowLevelMouseWheelCapture : IDisposable
{
    private const int HookMouseLowLevel = 14;
    private const int HookAction = 0;
    private const int WindowMessageQuit = 0x0012;
    private const int WindowMessageInput = 0x00FF;
    private const int WindowMessageMouseWheel = 0x020A;
    private const int WheelDelta = 120;
    private const int RawInputDeviceMouseUsagePage = 0x01;
    private const int RawInputDeviceMouseUsage = 0x02;
    private const int RawInputInputSink = 0x00000100;
    private const int RawInputCommandInput = 0x10000003;
    private const int RawInputTypeMouse = 0;
    private const string WindowClassName = "EnemyIntelFreecamRawMouseWindow";

    private readonly object _sync = new();
    private Thread? _thread;
    private LowLevelMouseProc? _proc;
    private WndProc? _windowProc;
    private readonly string _windowClassName = $"{WindowClassName}_{Guid.NewGuid():N}";
    private IntPtr _hook;
    private IntPtr _windowHandle;
    private uint _threadId;
    private POINT _mouseDelta;
    private float _scrollDelta;

    public void Start()
    {
        lock (_sync)
        {
            if (_thread != null)
            {
                return;
            }

            _proc = HookCallback;
            _thread = new Thread(MessageLoop)
            {
                IsBackground = true,
                Name = "EnemyIntelFreecamMouseWheel"
            };
            _thread.Start();
        }
    }

    public void Stop()
    {
        Thread? thread;
        lock (_sync)
        {
            thread = _thread;
            if (thread == null)
            {
                return;
            }

            if (_threadId != 0)
            {
                PostThreadMessage(_threadId, WindowMessageQuit, IntPtr.Zero, IntPtr.Zero);
            }
        }

        thread.Join(2000);
        lock (_sync)
        {
            // Never release native callback delegates while the message thread
            // may still dispatch them. A later Stop call can finish cleanup.
            if (thread.IsAlive)
            {
                return;
            }

            _thread = null;
            _threadId = 0;
            _proc = null;
            _windowProc = null;
            _mouseDelta = default;
            _scrollDelta = 0;
        }
    }

    public (int X, int Y) TakeMouseDelta()
    {
        lock (_sync)
        {
            POINT value = _mouseDelta;
            _mouseDelta = default;
            return (value.X, value.Y);
        }
    }

    public void ResetDeltas()
    {
        lock (_sync)
        {
            _mouseDelta = default;
            _scrollDelta = 0;
        }
    }

    public float TakeScrollDelta()
    {
        lock (_sync)
        {
            float value = _scrollDelta;
            _scrollDelta = 0;
            return value;
        }
    }

    public void Dispose() => Stop();

    private void MessageLoop()
    {
        _threadId = GetCurrentThreadId();
        _windowProc = RawInputWindowProc;
        _windowHandle = CreateRawInputWindow(_windowProc, _windowClassName);
        if (_windowHandle != IntPtr.Zero)
        {
            RegisterRawMouseInput(_windowHandle);
        }

        _hook = SetWindowsHookEx(HookMouseLowLevel, _proc!, GetModuleHandle(null), 0);
        if (_hook == IntPtr.Zero)
        {
            if (_windowHandle == IntPtr.Zero)
            {
                return;
            }
        }

        try
        {
            while (GetMessage(out MSG message, IntPtr.Zero, 0, 0) > 0)
            {
                TranslateMessage(ref message);
                DispatchMessage(ref message);
            }
        }
        finally
        {
            if (_hook != IntPtr.Zero)
            {
                UnhookWindowsHookEx(_hook);
            }

            if (_windowHandle != IntPtr.Zero)
            {
                DestroyWindow(_windowHandle);
            }

            UnregisterClass(_windowClassName, GetModuleHandle(null));

            _hook = IntPtr.Zero;
            _windowHandle = IntPtr.Zero;
        }
    }

    private IntPtr RawInputWindowProc(IntPtr windowHandle, uint message, IntPtr wParam, IntPtr lParam)
    {
        if (message == WindowMessageInput)
        {
            HandleRawInput(lParam);
        }

        return DefWindowProc(windowHandle, message, wParam, lParam);
    }

    private void HandleRawInput(IntPtr rawInputHandle)
    {
        uint size = 0;
        uint headerSize = (uint)Marshal.SizeOf<RAWINPUTHEADER>();
        if (GetRawInputData(rawInputHandle, RawInputCommandInput, IntPtr.Zero, ref size, headerSize) != 0 ||
            size == 0)
        {
            return;
        }

        IntPtr buffer = Marshal.AllocHGlobal((int)size);
        try
        {
            uint read = GetRawInputData(rawInputHandle, RawInputCommandInput, buffer, ref size, headerSize);
            if (read == unchecked((uint)-1))
            {
                return;
            }

            RAWINPUT raw = Marshal.PtrToStructure<RAWINPUT>(buffer);
            if (raw.Header.Type != RawInputTypeMouse)
            {
                return;
            }

            lock (_sync)
            {
                _mouseDelta.X += raw.Mouse.LastX;
                _mouseDelta.Y += raw.Mouse.LastY;
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static IntPtr CreateRawInputWindow(WndProc windowProc, string windowClassName)
    {
        IntPtr instance = GetModuleHandle(null);
        WNDCLASSEX windowClass = new()
        {
            CbSize = (uint)Marshal.SizeOf<WNDCLASSEX>(),
            LpfnWndProc = windowProc,
            HInstance = instance,
            LpszClassName = windowClassName
        };

        RegisterClassEx(ref windowClass);
        return CreateWindowEx(
            0,
            windowClassName,
            windowClassName,
            0,
            0,
            0,
            0,
            0,
            IntPtr.Zero,
            IntPtr.Zero,
            instance,
            IntPtr.Zero);
    }

    private static void RegisterRawMouseInput(IntPtr windowHandle)
    {
        RAWINPUTDEVICE[] devices =
        [
            new RAWINPUTDEVICE
            {
                UsagePage = RawInputDeviceMouseUsagePage,
                Usage = RawInputDeviceMouseUsage,
                Flags = RawInputInputSink,
                Target = windowHandle
            }
        ];

        RegisterRawInputDevices(devices, (uint)devices.Length, (uint)Marshal.SizeOf<RAWINPUTDEVICE>());
    }

    private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        // Movement is sourced exclusively from WM_INPUT. Counting this legacy
        // mouse-move path as well doubles orbit deltas and introduces jitter.
        if (nCode >= HookAction && wParam == (IntPtr)WindowMessageMouseWheel)
        {
            MSLLHOOKSTRUCT data = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
            short delta = unchecked((short)((data.MouseData >> 16) & 0xFFFF));
            lock (_sync)
            {
                _scrollDelta += delta / (float)WheelDelta;
            }
        }

        return CallNextHookEx(_hook, nCode, wParam, lParam);
    }

    private delegate IntPtr LowLevelMouseProc(int nCode, IntPtr wParam, IntPtr lParam);

    private delegate IntPtr WndProc(IntPtr windowHandle, uint message, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(
        int hookId,
        LowLevelMouseProc callback,
        IntPtr moduleHandle,
        uint threadId);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWindowsHookEx(IntPtr hookHandle);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hookHandle, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern int GetMessage(out MSG message, IntPtr windowHandle, uint messageFilterMin, uint messageFilterMax);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TranslateMessage(ref MSG message);

    [DllImport("user32.dll")]
    private static extern IntPtr DispatchMessage(ref MSG message);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern ushort RegisterClassEx(ref WNDCLASSEX windowClass);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnregisterClass(string className, IntPtr instance);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowEx(
        uint extendedStyle,
        string className,
        string windowName,
        uint style,
        int x,
        int y,
        int width,
        int height,
        IntPtr parent,
        IntPtr menu,
        IntPtr instance,
        IntPtr param);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyWindow(IntPtr windowHandle);

    [DllImport("user32.dll")]
    private static extern IntPtr DefWindowProc(IntPtr windowHandle, uint message, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RegisterRawInputDevices(
        RAWINPUTDEVICE[] rawInputDevices,
        uint numberOfDevices,
        uint size);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetRawInputData(
        IntPtr rawInput,
        uint command,
        IntPtr data,
        ref uint size,
        uint sizeHeader);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostThreadMessage(uint threadId, int message, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern IntPtr GetModuleHandle(string? moduleName);

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MSLLHOOKSTRUCT
    {
        public POINT Point;
        public uint MouseData;
        public uint Flags;
        public uint Time;
        public IntPtr ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WNDCLASSEX
    {
        public uint CbSize;
        public uint Style;
        public WndProc LpfnWndProc;
        public int CbClsExtra;
        public int CbWndExtra;
        public IntPtr HInstance;
        public IntPtr HIcon;
        public IntPtr HCursor;
        public IntPtr HbrBackground;
        public string? LpszMenuName;
        public string LpszClassName;
        public IntPtr HIconSm;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RAWINPUTDEVICE
    {
        public ushort UsagePage;
        public ushort Usage;
        public uint Flags;
        public IntPtr Target;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RAWINPUTHEADER
    {
        public uint Type;
        public uint Size;
        public IntPtr Device;
        public IntPtr WParam;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RAWMOUSE
    {
        public ushort Flags;
        public uint Buttons;
        public uint RawButtons;
        public int LastX;
        public int LastY;
        public uint ExtraInformation;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RAWINPUT
    {
        public RAWINPUTHEADER Header;
        public RAWMOUSE Mouse;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG
    {
        public IntPtr WindowHandle;
        public uint Message;
        public IntPtr WParam;
        public IntPtr LParam;
        public uint Time;
        public POINT Point;
    }
}

internal sealed class PracticeToolBridge : IDisposable
{
    private const string ProcessName = "eldenring";
    private const string DropRateBoostKey = "DROP RATE X10";
    private const string SteadyPlayerKey = "STEADY PLAYER";
    private const float SteadyPlayerPoise = 1_000_000f;

    // Elden Ring 2.7.x debug/runtime bases. These moved by roughly 0x4000 in
    // 2.7.0; keeping them behind the version gate below prevents stale writes.
    private const ulong ChrDbgFlags = 0x3D6A210;
    private const string CsRegulationManagerPattern =
        "48 8B 0D ?? ?? ?? ?? 48 85 C9 74 0B 4C 8B C0 48 8B D7";
    private const ulong DamageCtrl = 0x3D6A3E8;
    // Existing flag definitions are intentionally indexed from one byte before
    // GroupMask (GEOM 07 uses the zero offset).
    private const ulong GroupMask = 0x3B37D0F;
    private const ulong HitInsHitboxOffset = 0x3D6E15C;
    private const ulong WorldChrMan = 0x3D69FF8;
    private const ulong WorldChrManDbg = 0x3D6A208;
    private const ulong DbgEventManOff = 0x3D67FF8;
    private const ulong FuncCheckGraces = 0x3D6CFC0;
    private const ulong TargetingDebugDraw = 0x3D662C9;
    private const ulong RegulationManagerParamMasterOffset = 0x18;
    private const ulong ParamEntryNameOffset = 0x18;
    private const ulong ParamEntryNameLengthOffset = 0x28;
    private const ulong ParamEntryDataRootOffset = 0x80;
    private const ulong ParamDataRootOffset = 0x80;
    private const ulong ParamRowCountOffset = 0x0A;
    private const ulong ParamRowVectorOffset = 0x40;
    private const int ParamRowEntrySize = 0x18;
    private const ulong ParamRowEntryOffsetOffset = 0x08;
    private const ulong ItemLotBasePointOffset = 0x40;
    private const int ItemLotBasePointCount = 8;
    private const int ItemLotBasePointBytes = ItemLotBasePointCount * sizeof(ushort);
    private const ushort MaxDropRatePoints = 1000;
    private const int DropRateMultiplier = 10;
    private static readonly string[] EnemyItemLotParamNames =
    [
        "ItemLotParam_enemy",
        "ItemLotParam"
    ];

    private readonly List<DropRatePatch> _dropRatePatches = [];
    private readonly List<DropRateCachedPatch> _dropRateCachedPatches = [];
    private int _dropRateCacheProcessId;
    private ulong[]? _dropRateBasePointAddresses;
    private bool _dropRateBoostEnabled;
    private bool _steadyPlayerEnabled;
    private ulong _steadyPlayerPoiseAddress;
    private byte[]? _steadyPlayerOriginalPoise;

    public int ResolveBuddyTriggerEffect(int buddyParamId)
    {
        using MemoryReader memory = new(ProcessName);
        ValidateSupportedVersion(memory);
        ParamTable buddies = ResolveParamTable(memory, "BuddyParam");
        for (int index = 0; index < buddies.Count; index++)
        {
            ulong entry = buddies.BaseAddress + ParamRowVectorOffset + (ulong)(index * ParamRowEntrySize);
            if (memory.ReadInt32(entry) != buddyParamId) continue;

            ulong row = buddies.ResolveRowAddress(memory, index);
            int effectId = memory.ReadInt32(row + 0x04);
            if (effectId <= 0)
                throw new InvalidOperationException($"BuddyParam {buddyParamId} has no summon trigger effect.");
            return effectId;
        }

        throw new InvalidOperationException($"BuddyParam {buddyParamId} was not found.");
    }

    private readonly PracticeFlagDefinition[] _flags =
    [
        StaticFlag("NO DAMAGE", ChrDbgFlags + 0x0C, 0b0000_0001),
        StaticFlag("INF STAMINA", ChrDbgFlags + 0x05, 0b0000_0001),
        MultiFlag("INF FP",
        [
            StaticFlag("NO FP", ChrDbgFlags + 0x06, 0b0000_0001),
            StaticFlag("NO ASH FP", ChrDbgFlags + 0x12, 0b0000_0001)
        ]),
        MultiFlag("INF ITEMS",
        [
            StaticFlag("NO GOODS", ChrDbgFlags + 0x04, 0b0000_0001),
            StaticFlag("NO ARROWS", ChrDbgFlags + 0x07, 0b0000_0001)
        ]),
        StaticFlag("NO DEATH", ChrDbgFlags + 0x01, 0b0000_0001),
        StaticFlag("ONE SHOT", ChrDbgFlags + 0x03, 0b0000_0001),
        StaticFlag("AI DISABLE", ChrDbgFlags + 0x10, 0b0000_0001),
        PointerFlag("NO EVENTS", 0b0000_0001, DbgEventManOff, [0x28]),
        MultiFlag("WEAPON HITBOX",
        [
            PointerFlag("WEAPON HITBOX 1", 0b0000_0001, DamageCtrl, [0xA0]),
            PointerFlag("WEAPON HITBOX 2", 0b0000_0001, DamageCtrl, [0xA1]),
            PointerFlag("WEAPON HITBOX 3", 0b0000_0001, DamageCtrl, [0xA4])
        ]),
        MultiFlag("WORLD HITBOX",
        [
            StaticFlag("HITBOX HIGH", HitInsHitboxOffset, 0b0000_0001),
            StaticFlag("HITBOX LOW", HitInsHitboxOffset + 0x01, 0b0000_0001),
            StaticFlag("HITBOX F", HitInsHitboxOffset + 0x04, 0b0000_0001),
            StaticFlag("HITBOX CHARACTER", HitInsHitboxOffset + 0x03, 0b0000_0001)
        ]),
        PointerFlag("EVENT HITBOX", 0b0000_0001, DbgEventManOff, [0x04]),
        PointerFlag("POISE VIEW", 0b0000_0001, WorldChrManDbg, [0x69]),
        StaticFlag("TARGETING VIEW", TargetingDebugDraw, 0b0000_0001),
        MultiFlag("SHOW MAP",
        [
            StaticFlag("GEOM 00", GroupMask + 0x02, 0b0000_0001),
            StaticFlag("GEOM 01", GroupMask + 0x03, 0b0000_0001),
            StaticFlag("GEOM 02", GroupMask + 0x04, 0b0000_0001),
            StaticFlag("GEOM 03", GroupMask + 0x05, 0b0000_0001),
            StaticFlag("GEOM 04", GroupMask + 0x06, 0b0000_0001),
            StaticFlag("GEOM 05", GroupMask + 0x07, 0b0000_0001),
            StaticFlag("GEOM 06", GroupMask + 0x08, 0b0000_0001),
            StaticFlag("GEOM 07", GroupMask, 0b0000_0001),
            StaticFlag("GEOM 08", GroupMask + 0x0A, 0b0000_0001),
            StaticFlag("GEOM 09", GroupMask + 0x0B, 0b0000_0001),
            StaticFlag("GEOM 10", GroupMask + 0x0C, 0b0000_0001),
            StaticFlag("GEOM 11", GroupMask + 0x0D, 0b0000_0001),
            StaticFlag("GEOM 12", GroupMask + 0x0F, 0b0000_0001),
            StaticFlag("GEOM 13", GroupMask + 0x10, 0b0000_0001),
            StaticFlag("GEOM 14", GroupMask + 0x11, 0b0000_0001),
            StaticFlag("GEOM 15", GroupMask + 0x12, 0b0000_0001)
        ]),
        StaticFlag("SHOW CHR", GroupMask + 0x0E, 0b0000_0001),
        StaticFlag("SHOW LANDMARKS", FuncCheckGraces, 0b0000_0001)
    ];

    public string ToggleFlag(string key)
    {
        if (string.Equals(key, DropRateBoostKey, StringComparison.OrdinalIgnoreCase))
        {
            return ToggleDropRateBoost();
        }
        if (string.Equals(key, SteadyPlayerKey, StringComparison.OrdinalIgnoreCase))
        {
            return ToggleSteadyPlayer();
        }

        PracticeFlagDefinition? flag = _flags.FirstOrDefault(f =>
            string.Equals(f.Key, key, StringComparison.OrdinalIgnoreCase));
        if (flag == null)
        {
            return "UNKNOWN PRACTICE FLAG";
        }

        try
        {
            using MemoryReader memory = new(ProcessName);
            ValidateSupportedVersion(memory);
            bool enabled = flag.Toggle(memory);
            return $"{flag.Key} {(enabled ? "ON" : "OFF")}";
        }
        catch (Exception exception)
        {
            return $"{flag.Key} FAILED: {ShortMessage(exception)}";
        }
    }

    public bool? GetFlagState(string key)
    {
        if (string.Equals(key, DropRateBoostKey, StringComparison.OrdinalIgnoreCase))
        {
            return _dropRateBoostEnabled;
        }
        if (string.Equals(key, SteadyPlayerKey, StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                using MemoryReader memory = new(ProcessName);
                ValidateSupportedVersion(memory);
                _ = ResolveLocalPlayerPoiseAddress(memory);
                return _steadyPlayerEnabled;
            }
            catch
            {
                return null;
            }
        }

        PracticeFlagDefinition? flag = _flags.FirstOrDefault(f =>
            string.Equals(f.Key, key, StringComparison.OrdinalIgnoreCase));
        if (flag == null)
        {
            return null;
        }

        try
        {
            using MemoryReader memory = new(ProcessName);
            ValidateSupportedVersion(memory);
            return flag.Get(memory);
        }
        catch
        {
            return null;
        }
    }

    public void Dispose()
    {
        try
        {
            using MemoryReader memory = new(ProcessName);
            if (_dropRateBoostEnabled) RestoreDropRateBoost(memory);
            if (_steadyPlayerEnabled) RestoreSteadyPlayer(memory);
        }
        catch
        {
            // Elden Ring may have closed before the app did.
        }

        _dropRateBoostEnabled = false;
        _dropRatePatches.Clear();
        _steadyPlayerEnabled = false;
        _steadyPlayerPoiseAddress = 0;
        _steadyPlayerOriginalPoise = null;
    }

    private string ToggleSteadyPlayer()
    {
        try
        {
            using MemoryReader memory = new(ProcessName);
            ValidateSupportedVersion(memory);
            if (_steadyPlayerEnabled)
            {
                RestoreSteadyPlayer(memory);
                _steadyPlayerEnabled = false;
                return "STEADY PLAYER OFF";
            }

            ulong poiseAddress = ResolveLocalPlayerPoiseAddress(memory);
            byte[] original = memory.ReadBytes(poiseAddress, sizeof(float) * 2);
            float current = BitConverter.ToSingle(original, 0);
            float maximum = BitConverter.ToSingle(original, sizeof(float));
            if (!float.IsFinite(current) || !float.IsFinite(maximum) || maximum < 0f || maximum > 100_000f)
            {
                throw new InvalidOperationException("Player poise structure invalid.");
            }

            byte[] steady = new byte[sizeof(float) * 2];
            BitConverter.GetBytes(SteadyPlayerPoise).CopyTo(steady, 0);
            BitConverter.GetBytes(SteadyPlayerPoise).CopyTo(steady, sizeof(float));
            memory.WriteBytes(poiseAddress, steady);
            _steadyPlayerPoiseAddress = poiseAddress;
            _steadyPlayerOriginalPoise = original;
            _steadyPlayerEnabled = true;
            return "STEADY PLAYER ON";
        }
        catch (Exception exception)
        {
            return $"STEADY PLAYER FAILED: {ShortMessage(exception)}";
        }
    }

    private static ulong ResolveLocalPlayerPoiseAddress(MemoryReader memory)
    {
        ulong worldChrMan = memory.ReadUInt64(memory.ModuleBase + WorldChrMan);
        if (!memory.IsLikelyPointer(worldChrMan)) throw new InvalidOperationException("World character manager missing.");
        ulong characterList = memory.ReadUInt64(worldChrMan + 0x10EF8);
        if (!memory.IsLikelyPointer(characterList)) throw new InvalidOperationException("Character list missing.");
        ulong player = memory.ReadUInt64(characterList);
        if (!memory.IsLikelyPointer(player)) throw new InvalidOperationException("Local player missing.");
        ulong characterData = memory.ReadUInt64(player + 0x190);
        if (!memory.IsLikelyPointer(characterData)) throw new InvalidOperationException("Player data missing.");
        ulong superArmor = memory.ReadUInt64(characterData + 0x40);
        if (!memory.IsLikelyPointer(superArmor)) throw new InvalidOperationException("Player poise missing.");
        return superArmor + 0x10;
    }

    private void RestoreSteadyPlayer(MemoryReader memory)
    {
        if (_steadyPlayerPoiseAddress != 0 &&
            _steadyPlayerOriginalPoise is { Length: 8 } &&
            ResolveLocalPlayerPoiseAddress(memory) == _steadyPlayerPoiseAddress)
        {
            memory.WriteBytes(_steadyPlayerPoiseAddress, _steadyPlayerOriginalPoise);
        }
        _steadyPlayerPoiseAddress = 0;
        _steadyPlayerOriginalPoise = null;
    }

    private string ToggleDropRateBoost()
    {
        try
        {
            using MemoryReader memory = new(ProcessName);
            ValidateSupportedVersion(memory);
            if (_dropRateBoostEnabled)
            {
                RestoreDropRateBoost(memory);
                _dropRateBoostEnabled = false;
                return "DROP RATE X10 OFF";
            }

            int patchCount = ApplyDropRateBoost(memory);
            _dropRateBoostEnabled = true;
            return $"DROP RATE X10 ON ({patchCount})";
        }
        catch (Exception exception)
        {
            return $"DROP RATE X10 FAILED: {ShortMessage(exception)}";
        }
    }

    private int ApplyDropRateBoost(MemoryReader memory)
    {
        _dropRatePatches.Clear();
        if (_dropRateCacheProcessId == memory.Process.Id && _dropRateCachedPatches.Count > 0)
        {
            foreach (DropRateCachedPatch patch in _dropRateCachedPatches)
            {
                memory.WriteBytes(patch.Address, patch.BoostedBytes);
                _dropRatePatches.Add(new DropRatePatch(patch.Address, patch.OriginalBytes));
            }
            return _dropRateCachedPatches.Sum(patch => patch.ChangedPoints);
        }

        _dropRateCachedPatches.Clear();
        _dropRateBasePointAddresses = null;
        _dropRateCacheProcessId = memory.Process.Id;
        int changed = 0;
        foreach (ulong basePointAddress in ResolveDropRateBasePointAddresses(memory))
        {
            byte[] originalBytes = memory.ReadBytes(basePointAddress, ItemLotBasePointBytes);
            byte[] boostedBytes = (byte[])originalBytes.Clone();
            int rowChanged = 0;
            for (int slot = 0; slot < ItemLotBasePointCount; slot++)
            {
                int offset = slot * sizeof(ushort);
                ushort original = BitConverter.ToUInt16(originalBytes, offset);
                if (original == 0 || original >= MaxDropRatePoints) continue;

                ushort boosted = (ushort)Math.Min(MaxDropRatePoints, original * DropRateMultiplier);
                if (boosted == original) continue;

                BitConverter.GetBytes(boosted).CopyTo(boostedBytes, offset);
                rowChanged++;
            }

            if (rowChanged > 0)
            {
                _dropRatePatches.Add(new DropRatePatch(basePointAddress, originalBytes));
                _dropRateCachedPatches.Add(new DropRateCachedPatch(
                    basePointAddress,
                    originalBytes,
                    boostedBytes,
                    rowChanged));
                memory.WriteBytes(basePointAddress, boostedBytes);
                changed += rowChanged;
            }
        }

        if (changed == 0)
        {
            throw new InvalidOperationException("No drop rates changed.");
        }

        return changed;
    }

    private ulong[] ResolveDropRateBasePointAddresses(MemoryReader memory)
    {
        if (_dropRateCacheProcessId == memory.Process.Id && _dropRateBasePointAddresses is { Length: > 0 })
            return _dropRateBasePointAddresses;

        foreach (string paramName in EnemyItemLotParamNames)
        {
            if (!TryResolveParamTable(memory, paramName, out ParamTable itemLots)) continue;

            int vectorBytes = checked(itemLots.Count * ParamRowEntrySize);
            byte[] vector = memory.ReadBytes(itemLots.BaseAddress + ParamRowVectorOffset, vectorBytes);
            var addresses = new ulong[itemLots.Count];
            for (int index = 0; index < itemLots.Count; index++)
            {
                int entryOffset = index * ParamRowEntrySize;
                long rowOffset = BitConverter.ToInt64(
                    vector,
                    entryOffset + (int)ParamRowEntryOffsetOffset);
                addresses[index] = unchecked((ulong)((long)itemLots.BaseAddress + rowOffset)) +
                    ItemLotBasePointOffset;
            }

            _dropRateBasePointAddresses = addresses;
            return addresses;
        }

        throw new InvalidOperationException("Enemy item-lot table not found.");
    }

    private void RestoreDropRateBoost(MemoryReader memory)
    {
        foreach (DropRatePatch patch in _dropRatePatches)
        {
            memory.WriteBytes(patch.Address, patch.OriginalBytes);
        }

        _dropRatePatches.Clear();
    }

    private static ParamTable ResolveParamTable(MemoryReader memory, string paramName)
    {
        if (TryResolveParamTable(memory, paramName, out ParamTable table))
        {
            return table;
        }

        throw new InvalidOperationException($"{paramName} not found.");
    }

    private static bool TryResolveParamTable(MemoryReader memory, string paramName, out ParamTable table)
    {
        table = default;
        ulong regulationManager = MainForm.ResolveGlobalPointerOrZero(
            memory,
            CsRegulationManagerPattern);
        if (!memory.IsLikelyPointer(regulationManager))
        {
            throw new InvalidOperationException("Regulation manager missing.");
        }

        ulong master = regulationManager + RegulationManagerParamMasterOffset;
        ulong start = memory.ReadUInt64(master);
        ulong end = memory.ReadUInt64(master + sizeof(ulong));
        if (!memory.IsLikelyPointer(start) ||
            !memory.IsLikelyPointer(end) ||
            end <= start)
        {
            throw new InvalidOperationException("Param master missing.");
        }

        ulong entryCount = (end - start) / sizeof(ulong);
        if (entryCount < 100 || entryCount > 1000)
        {
            throw new InvalidOperationException("Param table count invalid.");
        }

        for (ulong index = 0; index < entryCount; index++)
        {
            ulong entry = memory.ReadUInt64(start + index * sizeof(ulong));
            if (!memory.IsLikelyPointer(entry))
            {
                continue;
            }

            string name = ReadParamName(memory, entry);
            if (!string.Equals(name, paramName, StringComparison.Ordinal))
            {
                continue;
            }

            ulong root = memory.ReadUInt64(entry + ParamEntryDataRootOffset);
            if (!memory.IsLikelyPointer(root))
            {
                throw new InvalidOperationException($"{paramName} root missing.");
            }

            root = memory.ReadUInt64(root + ParamDataRootOffset);
            if (!memory.IsLikelyPointer(root))
            {
                throw new InvalidOperationException($"{paramName} data missing.");
            }

            ushort rowCount = memory.ReadUInt16(root + ParamRowCountOffset);
            if (rowCount == 0)
            {
                throw new InvalidOperationException($"{paramName} rows missing.");
            }

            table = new ParamTable(root, rowCount);
            return true;
        }

        return false;
    }

    private static string ReadParamName(MemoryReader memory, ulong entry)
    {
        ulong length = memory.ReadUInt64(entry + ParamEntryNameLengthOffset);
        ulong nameAddress = length <= 7
            ? entry + ParamEntryNameOffset
            : memory.ReadUInt64(entry + ParamEntryNameOffset);
        if (!memory.IsLikelyPointer(nameAddress))
        {
            return string.Empty;
        }

        int charCapacity = length is > 0 and < 90
            ? (int)length + 1
            : 90;
        byte[] bytes = memory.ReadBytes(nameAddress, charCapacity * sizeof(ushort));
        for (int offset = 0; offset + 1 < bytes.Length; offset += sizeof(ushort))
        {
            if (bytes[offset] == 0 && bytes[offset + 1] == 0)
            {
                return System.Text.Encoding.Unicode.GetString(bytes, 0, offset);
            }
        }

        return System.Text.Encoding.Unicode.GetString(bytes).TrimEnd('\0');
    }

    private static long ReadInt64(MemoryReader memory, ulong address)
    {
        return BitConverter.ToInt64(memory.ReadBytes(address, sizeof(long)), 0);
    }

    private static void ValidateSupportedVersion(MemoryReader memory)
    {
        FileVersionInfo version = FileVersionInfo.GetVersionInfo(memory.Process.MainModule?.FileName ?? string.Empty);
        bool supported = version.FileMajorPart == 2 &&
            version.FileMinorPart == 7 &&
            version.FileBuildPart is 0 or 1;
        if (!supported)
        {
            throw new NotSupportedException(
                $"Unsupported ER version {version.FileMajorPart}.{version.FileMinorPart}.{version.FileBuildPart}.");
        }
    }

    private static PracticeFlagDefinition StaticFlag(string key, ulong offset, byte mask, bool optional = false)
    {
        return new PracticeFlagDefinition(key, [new PracticeBitFlag(offset, mask, [], optional)]);
    }

    private static PracticeFlagDefinition PointerFlag(string key, byte mask, ulong offset, ulong[] chain, bool optional = false)
    {
        return new PracticeFlagDefinition(key, [new PracticeBitFlag(offset, mask, chain, optional)]);
    }

    private static PracticeFlagDefinition MultiFlag(string key, PracticeFlagDefinition[] flags)
    {
        return new PracticeFlagDefinition(
            key,
            flags.SelectMany(flag => flag.Flags).ToArray());
    }

    private static string ShortMessage(Exception exception)
    {
        string message = exception.Message;
        return message.Length <= 42 ? message : message[..42];
    }

    private sealed record PracticeFlagDefinition(string Key, PracticeBitFlag[] Flags)
    {
        public bool Get(MemoryReader memory)
        {
            bool?[] states = GetResolvedStates(memory);
            if (states.Length == 0)
            {
                throw new InvalidOperationException("No practice addresses resolved.");
            }

            return states.All(state => state == true);
        }

        public bool Toggle(MemoryReader memory)
        {
            bool?[] states = GetResolvedStates(memory);
            if (states.Length == 0)
            {
                throw new InvalidOperationException("No practice addresses resolved.");
            }

            bool allEnabled = states.All(state => state == true);
            bool newValue = !allEnabled;
            foreach (PracticeBitFlag flag in Flags)
            {
                flag.SetIfAvailable(memory, newValue);
            }

            return newValue;
        }

        private bool?[] GetResolvedStates(MemoryReader memory)
        {
            return Flags
                .Select(flag => flag.GetOrNull(memory))
                .Where(state => state.HasValue)
                .ToArray();
        }
    }

    private sealed record DropRatePatch(ulong Address, byte[] OriginalBytes);

    private sealed record DropRateCachedPatch(
        ulong Address,
        byte[] OriginalBytes,
        byte[] BoostedBytes,
        int ChangedPoints);

    private readonly record struct ParamTable(ulong BaseAddress, ushort Count)
    {
        public ulong ResolveRowAddress(MemoryReader memory, int index)
        {
            ulong entry = BaseAddress + ParamRowVectorOffset + (ulong)(index * ParamRowEntrySize);
            long offset = ReadInt64(memory, entry + ParamRowEntryOffsetOffset);
            return (ulong)((long)BaseAddress + offset);
        }
    }

    private sealed record PracticeBitFlag(ulong BaseOffset, byte Mask, ulong[] Offsets, bool Optional)
    {
        public bool? GetOrNull(MemoryReader memory)
        {
            try
            {
                return Get(memory);
            }
            catch when (Optional)
            {
                return null;
            }
        }

        public void SetIfAvailable(MemoryReader memory, bool enabled)
        {
            try
            {
                Set(memory, enabled);
            }
            catch when (Optional)
            {
                // Torrent-specific chains are absent until that object exists in the world.
            }
        }

        private bool Get(MemoryReader memory)
        {
            byte value = memory.ReadBytes(ResolveAddress(memory), 1)[0];
            return (value & Mask) == Mask;
        }

        private void Set(MemoryReader memory, bool enabled)
        {
            ulong address = ResolveAddress(memory);
            byte value = memory.ReadBytes(address, 1)[0];
            byte next = enabled
                ? (byte)(value | Mask)
                : (byte)(value & ~Mask);
            memory.WriteBytes(address, [next]);
        }

        private ulong ResolveAddress(MemoryReader memory)
        {
            ulong address = memory.ModuleBase + BaseOffset;
            foreach (ulong offset in Offsets)
            {
                address = memory.ReadUInt64(address) + offset;
            }

            return address;
        }
    }
}
