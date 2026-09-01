namespace EnemyIntel.Game.Legacy;

using System.Diagnostics;
using System.IO;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Text.Json;

public sealed record EnemySpawnPreset(
    string Name,
    string Model,
    int NpcParamId,
    int ThinkParamId,
    bool SuppressDeathRewards = false,
    int CharacterInitId = -1);

public enum EnemySpawnScalingMode
{
    AutoBalance,
    Cinematic,
    Authentic
}

public sealed class LegacyGameCameraService : IDisposable
{
    private enum CameraTransformOwner
    {
        InteractiveFreecam = 0,
        SubjectCamera = 1,
        CameraTrack = 2
    }

    private const string ProcessName = "eldenring";
    private const int LiveCameraUpdatesPerSecond = 240;
    private const string RealPauseOffPattern = "C1 E8 12 A8 01 74 3E 48 8B 0D ?? ?? ?? ?? C6 05 ?? ?? ?? ?? 01 48 85 C9 74 05";
    private const string RealPauseOnPattern = "B0 01 90 A8 01 74 3E 48 8B 0D ?? ?? ?? ?? C6 05 ?? ?? ?? ?? 01 48 85 C9 74 05";
    private static readonly byte[] RealPauseOffBytes = [0xC1, 0xE8, 0x12];
    private static readonly byte[] RealPauseOnBytes = [0xB0, 0x01, 0x90];
    private static readonly object RealPauseAddressGate = new();
    private static ulong _cachedRealPauseAddress;
    private static ulong _cachedRealPauseModuleBase;
    private const string WorldChrManPattern =
        "48 8B 05 ?? ?? ?? ?? 48 85 C0 74 0F 48 39 88";
    private const string GameDataManPattern =
        "48 8B 05 ?? ?? ?? ?? 48 85 C0 74 05 48 8B 40 58 C3 C3";
    private const string CsRegulationManagerPattern =
        "48 8B 0D ?? ?? ?? ?? 48 85 C9 74 0B 4C 8B C0 48 8B D7";
    private const ulong RegulationManagerParamMasterOffset = 0x18;
    private const ulong ParamEntryNameOffset = 0x18;
    private const ulong ParamEntryNameLengthOffset = 0x28;
    private const ulong ParamEntryDataRootOffset = 0x80;
    private const ulong ParamDataRootOffset = 0x80;
    private const ulong ParamRowCountOffset = 0x0A;
    private const ulong ParamRowVectorOffset = 0x40;
    private const int ParamRowEntrySize = 0x18;
    private const ulong ParamRowEntryOffsetOffset = 0x08;
    private const ulong PlayersOffset = 0x10EF8;
    private const ulong CharacterListBeginOffset = 0x1F1B8;
    private const ulong CharacterListEndOffset = 0x1F1C0;
    private const ulong DebugCharacterSpawnerOffset = 0x1E648;
    private const ulong DebugCharacterListOffset = 0x1E268;
    private const ulong DebugCharacterListEntriesOffset = 0x18;
    private const int EldenIntelCloneNpcParamId = 200000011;
    private const int EldenIntelCloneThinkParamId = 200000011;
    private const int EldenIntelCloneCharacterInitId = 26050;
    private const ulong SpawnRequestPositionOffset = 0xB0;
    private const ulong SpawnRequestNpcParamOffset = 0xF0;
    private const ulong SpawnRequestThinkParamOffset = 0xF4;
    private const ulong SpawnRequestEventEntityOffset = 0xF8;
    private const ulong SpawnRequestTalkIdOffset = 0xFC;
    private const ulong SpawnRequestModelOffset = 0x100;
    private const ulong SpawnRequestCreateFlagOffset = 0x44;
    private const ulong SpawnRequestEnemyTypeOffset = 0x178;
    private const ulong SpawnRequestCharacterInitOffset = 0x17C;
    private const ulong SpawnRequestManipulatorOffset = 0x180;
    private const float EnemySpawnForwardDistance = 7.0f;
    // The debug spawner does not terrain-snap before its first physics tick.
    // A modest settling lift prevents capsules from initializing inside mild
    // slopes while remaining low enough to avoid a visible spawn drop.
    private const float EnemySpawnVerticalClearance = 0.35f;
    private const string FieldAreaPattern = "48 8B 3D ?? ?? ?? ?? 49 8B D8 48 8B F2 4C 8B F1 48 85 FF";
    private const ulong FieldAreaGameRendOffset = 0x20;
    private const ulong GameRendDebugCameraOffset = 0xD0;
    private const ulong GameRendFreeCameraModeOffset = 0xC8;
    private const int FixedDebugCameraMode = 3;
    private const ulong CameraMatrixOffset = 0x10;
    private const ulong ChrInsChrCtrlOffset = 0x58;
    private const ulong ChrCtrlModelWidthOffset = 0x2D4;
    private const ulong ChrCtrlModelHeightOffset = 0x2D8;
    private const ulong ChrCtrlModelHeightZOffset = 0x2DC;
    private const ulong ChrInsOmissionModeOverrideOffset = 0xB8;
    private const ulong ChrCtrlPhysicsModelMatrixOffset = 0x1B0;
    private const ulong ChrCtrlModelMatrixOffset = 0x230;
    private const ulong ModelMatrixTranslationOffset = 0x30;
    private const ulong ChrInsModulesOffset = 0x190;
    private const ulong ChrModulesPhysicsOffset = 0x68;
    private const ulong ChrPhysicsOrientationOffset = 0x50;
    private const ulong ChrPhysicsInterpolatedOrientationOffset = 0x60;
    private const ulong ChrPhysicsLocalPositionOffset = 0x70;
    private const ulong ChrInsDeathRewardFlagsOffset = 0x1C6;
    private const byte ChrInsDeathRewardsHandledMask = 0x03;
    private const ulong ChrInsPlayerGameDataOffset = 0x580;
    private const ulong ChrInsSpecialEffectsOffset = 0x178;
    private const ulong SpecialEffectListHeadOffset = 0x08;
    private const ulong SpecialEffectParamIdOffset = 0x08;
    private const ulong SpecialEffectNextOffset = 0x30;
    private const int TransmogEffectMinimum = 169_000_000;
    private const int TransmogEffectMaximumExclusive = 169_500_000;
    private const ulong ChrInsLastHitByOffset = 0x180;
    private const ulong PlayerGameDataMaxHpOffset = 0x14;
    private const ulong PlayerGameDataVigorOffset = 0x3C;
    private const ulong PlayerGameDataLevelOffset = 0x68;
    private const ulong PlayerInventoryOffset = 0x5D0;
    private const int PlayerInventoryCapacity = 2688;
    private const int InventoryEntrySize = 0x18;
    private const uint TransmogEncodedGoodsMinimum = 0x40000000u + 6_900_000u;
    private const uint TransmogEncodedGoodsMaximumExclusive = 0x40000000u + 7_000_000u;
    private const ulong ChrDataHpOffset = 0x138;
    private const float SubjectAimHeight = 1.2f;
    private const float BodyOrientationResponse = 4.6f;
    // Character model matrices contain the full animation pose.  A literal
    // mount makes footsteps, hit reactions, and turning corrections dominate
    // the shot, so Body Cam inherits heading strongly but only a restrained
    // amount of animated lean.
    private const float BodyHorizonInfluence = 0.16f;
    private const float CameraFloorClearance = 0.20f;
    private const float GroundEstimateResponse = 1.25f;
    private const float GroundedVerticalSpeedThreshold = 1.25f;
    private const float MaximumSubjectFrameDisplacement = 150.0f;
    private const int SubjectReadGraceMilliseconds = 3000;
    private const int VirtualKeyControl = 0x11;
    private const int VirtualKeyAlt = 0x12;
    private const int VirtualKeyShift = 0x10;
    private const int VirtualKeySpace = 0x20;
    private const int VirtualKeyMiddleMouse = 0x04;
    private const ulong ChrInsRenderFlagsOffset = 0x1C5;
    private const byte ChrInsEnableRenderMask = 1 << 3;
    private const byte ChrInsDeathFlagMask = 1 << 7;
    private readonly global::EnemyIntelReader.GameCameraControlService _service = new();
    private readonly SemaphoreSlim _commandGate = new(1, 1);
    private bool _disposed;
    private bool _freecamAppliedRealPause;
    private readonly object _trackPlaybackGate = new();
    private CancellationTokenSource? _trackPlaybackCancellation;
    private readonly object _subjectLockGate = new();
    private CancellationTokenSource? _subjectLockCancellation;
    private Thread? _subjectLockThread;
    private Thread? _playerTargetSyncThread;
    private volatile bool _subjectLockAttached;
    private volatile bool _subjectBodyOrientationEnabled;
    private volatile bool _dialogueMotionEnabled;
    private volatile bool _movementShakeEnabled;
    private volatile bool _autoOrbitEnabled;
    private double _autoOrbitSpeedMultiplier = 1.0;
    private volatile bool _actionKeyboardControlsCamera = true;
    private volatile bool _actionMouseControlsCamera = true;
    private volatile bool _actionControllerControlsPlayer = true;
    private int _cameraTransformOwner = (int)CameraTransformOwner.InteractiveFreecam;
    private long _activeSubjectAddress;
    private readonly object _spawnScalingGate = new();
    private readonly Dictionary<ulong, ScaledSpawnProfile> _scaledSpawnsByHandle = [];
    private CancellationTokenSource? _spawnScalingCancellation;
    private Thread? _spawnScalingThread;

    public LegacyGameCameraService()
    {
        DisableFreecamFreezeFlags();
        _ = Task.Run(PrewarmRealPauseAddress);
    }

    public bool IsGameRunning => _service.IsGameRunning;
    private bool _possessionRestoreInputBlocked;
    public string PossessCharacter(ulong targetAddress)
    {
        _possessionRestoreInputBlocked = _service.IsPlayerInputBlocked;
        _service.EnsurePlayerInputEnabled();
        var status = _service.PossessCharacter(targetAddress);
        if (!status.StartsWith("CONTROL LINK ACTIVE", StringComparison.OrdinalIgnoreCase) &&
            _possessionRestoreInputBlocked) _service.EnsurePlayerInputBlocked();
        return status;
    }
    public string ReleasePossession()
    {
        var status = _service.ReleasePossession();
        if (_possessionRestoreInputBlocked) _service.EnsurePlayerInputBlocked();
        else _service.EnsurePlayerInputEnabled();
        return status;
    }
    public bool IsPossessionActive() => _service.IsPossessionActive();
    public AntiCheatStatus GetAntiCheatStatus() => AntiCheatVerifier.Check();
    public bool IsSubjectLockEnabled { get { lock (_subjectLockGate) return _subjectLockCancellation is not null; } }
    public bool IsSubjectBodyOrientationEnabled => _subjectBodyOrientationEnabled;
    public bool IsDialogueMotionEnabled => _dialogueMotionEnabled;
    public bool IsMovementShakeEnabled => _movementShakeEnabled;
    public bool IsAutoOrbitEnabled => _autoOrbitEnabled;
    public double AutoOrbitSpeedMultiplier => Volatile.Read(ref _autoOrbitSpeedMultiplier);
    public bool ActionKeyboardControlsCamera => _actionKeyboardControlsCamera;
    public bool ActionMouseControlsCamera => _actionMouseControlsCamera;
    public bool ActionKbmControlsCamera => _actionKeyboardControlsCamera && _actionMouseControlsCamera;
    public bool ActionControllerControlsPlayer => _actionControllerControlsPlayer;
    public bool IsPlayerInputBlocked => _service.IsPlayerInputBlocked;
    public static IReadOnlyList<EnemySpawnPreset> EnemySpawnPresets { get; } = LoadBossCatalog();

    private void ApplyActionCameraInputPolicy() =>
        _service.SetActionCameraInputPolicy(
            _actionKeyboardControlsCamera,
            _actionMouseControlsCamera,
            _actionControllerControlsPlayer);

    private static IReadOnlyList<EnemySpawnPreset> LoadBossCatalog()
    {
        try
        {
            var path = Path.Combine(AppContext.BaseDirectory, "assets", "Database", "boss_catalog.json");
            var catalog = JsonSerializer.Deserialize<List<EnemySpawnPreset>>(File.ReadAllText(path));
            if (catalog is { Count: > 0 })
            {
                var valid = catalog
                    .Where(entry => entry is not null &&
                                    !string.IsNullOrWhiteSpace(entry.Name) &&
                                    !string.IsNullOrWhiteSpace(entry.Model) &&
                                    entry.NpcParamId > 0 &&
                                    entry.ThinkParamId > 0)
                    .OrderBy(entry => entry.Name)
                    .ToArray();
                if (valid.Length > 0) return valid;
            }
        }
        catch { }

        return [new("Midra, Lord of Frenzied Flame", "c5051", 50510086, 50510000, true)];
    }

    public Task<string> ToggleFreecamAsync() => RunAsync(ToggleFreecamWithRealPause);
    public bool IsFreecamAutoPauseEnabled => _service.GetFreecamAutoPauseState();
    public Task<string> SetFreecamAutoPauseAsync(bool enabled) =>
        RunAsync(() => _service.SetFreecamAutoPause(enabled), requireVerifiedSession: false);
    public Task<string> ToggleDayCycleAsync() => RunAsync(_service.ToggleDayCycle);
    public Task<string> PlayGestureAsync(int gestureId) => RunAsync(() => _service.PlayGesture(gestureId));
    public Task<string> ToggleActiveViewAsync() => RunAsync(_service.ToggleActiveView);
    public Task<string> ToggleRealPauseAsync() => RunAsync(ToggleRealPause);
    public Task<bool?> GetRealPauseStateAsync() => Task.Run<bool?>(() =>
    {
        try { return IsRealPaused(); }
        catch { return null; }
    });
    public Task<bool?> GetFreecamStateAsync() => Task.Run<bool?>(() =>
    {
        try { return IsDebugCameraActive(); }
        catch { return null; }
    });
    public Task<string> ResetCameraAsync() => RunAsync(_service.ResetFov);
    public Task<string> StepFrameAsync() => RunAsync(StepRealPausedFrame);
    public Task<string> SetGameSpeedAsync(double speed) => RunAsync(() => _service.SetGameSpeed(speed));
    public Task<string> SaveCameraStateAsync(int slot) => RunAsync(() => _service.SaveCameraState(slot));
    public Task<string> PlayCameraStatesAsync(int[] slots) => RunAsync(() => _service.PlayCameraStates(slots));
    public Task<(string Status, CameraTrackKeyframe? Keyframe)> CaptureTrackKeyframeAsync() => RunCaptureAsync();
    public Task<string> PlayCameraTrackAsync(IReadOnlyList<CameraTrackPlaybackPoint> points, bool loop)
    {
        if (_disposed) return Task.FromResult("CAMERA SERVICE CLOSED");
        var antiCheat = AntiCheatVerifier.Check();
        if (antiCheat.State != AntiCheatState.Disabled)
            return Task.FromResult($"{antiCheat.Label} — MEMORY TOOLS BLOCKED");

        // Track playback is intentionally long-running (and may loop forever).
        // It must never own the short-command gate used by tools, pause,
        // possession, freecam toggles, or capture actions.
        return Task.Run(() => PlayCameraTrack(points, loop));
    }
    public string StopCameraTrackPlayback()
    {
        lock (_trackPlaybackGate)
        {
            if (_trackPlaybackCancellation is null) return "NO CAMERA TRACK PLAYING";
            _trackPlaybackCancellation.Cancel();
            return "STOPPING CAMERA PLAYBACK";
        }
    }
    public Task<string> ToggleHudAsync() => RunAsync(_service.ToggleHud);
    public Task<string> ToggleInputBlockAsync() => RunAsync(_service.ToggleInputBlock);
    public Task<string> ToggleActionKeyboardOwnerAsync() => RunAsync(() =>
    {
        _actionKeyboardControlsCamera = !_actionKeyboardControlsCamera;
        ApplyActionCameraInputPolicy();
        return _actionKeyboardControlsCamera ? "KEYBOARD → CAMERA" : "KEYBOARD → CHARACTER";
    }, requireVerifiedSession: false);
    public Task<string> ToggleActionKbmOwnerAsync() => RunAsync(() =>
    {
        var controlsCamera = !ActionKbmControlsCamera;
        _actionKeyboardControlsCamera = controlsCamera;
        _actionMouseControlsCamera = controlsCamera;
        ApplyActionCameraInputPolicy();
        return controlsCamera ? "KBM → CAMERA" : "KBM → PLAYER";
    }, requireVerifiedSession: false);
    public Task<string> SetActionKbmOwnerAsync(bool controlsCamera) => RunAsync(() =>
    {
        _actionKeyboardControlsCamera = controlsCamera;
        _actionMouseControlsCamera = controlsCamera;
        ApplyActionCameraInputPolicy();
        return controlsCamera ? "KBM → CAMERA" : "KBM → PLAYER";
    }, requireVerifiedSession: false);
    public Task<string> ToggleActionMouseOwnerAsync() => RunAsync(() =>
    {
        _actionMouseControlsCamera = !_actionMouseControlsCamera;
        ApplyActionCameraInputPolicy();
        return _actionMouseControlsCamera ? "MOUSE → CAMERA" : "MOUSE → GAME";
    }, requireVerifiedSession: false);
    public Task<string> ToggleActionControllerOwnerAsync() => RunAsync(() =>
    {
        _actionControllerControlsPlayer = !_actionControllerControlsPlayer;
        ApplyActionCameraInputPolicy();
        if (IsSubjectLockEnabled)
        {
            if (_actionControllerControlsPlayer) _service.EnsurePlayerInputEnabled();
            else _service.EnsurePlayerInputBlocked();
        }
        return _actionControllerControlsPlayer ? "CONTROLLER → PLAYER" : "CONTROLLER → CAMERA";
    }, requireVerifiedSession: false);
    public Task<string> SetActionControllerOwnerAsync(bool controlsPlayer) => RunAsync(() =>
    {
        _actionControllerControlsPlayer = controlsPlayer;
        ApplyActionCameraInputPolicy();
        if (IsSubjectLockEnabled)
        {
            if (controlsPlayer) _service.EnsurePlayerInputEnabled();
            else _service.EnsurePlayerInputBlocked();
        }
        return controlsPlayer ? "CONTROLLER → PLAYER" : "CONTROLLER → CAMERA";
    }, requireVerifiedSession: false);
    public Task<string> ToggleHigherLodAsync() => RunAsync(_service.ToggleHigherLods);
    public Task<string> ToggleDropRateAsync() => RunAsync(() => _service.TogglePracticeFlag("DROP RATE X10"));
    public Task<bool?> GetDropRateStateAsync() => Task.Run(() => _service.GetPracticeFlagState("DROP RATE X10"));
    public Task<string> ToggleAiDisableAsync() => RunAsync(() => _service.TogglePracticeFlag("AI DISABLE"));
    public Task<bool?> GetAiDisableStateAsync() => Task.Run(() => _service.GetPracticeFlagState("AI DISABLE"));
    public Task<string> ToggleNoDamageAsync() => RunAsync(() => _service.TogglePracticeFlag("NO DAMAGE"));

    public Task<string> HealPlayerAsync(float fraction = 0.35f) => RunAsync(() => HealPlayer(fraction));
    public Task<string> DrainPlayerHealthAsync(float fraction = 0.15f) => RunAsync(() => DrainPlayerHealth(fraction));
    public Task<string> TickPlayerDamageAsync(float fraction = 0.02f) => RunAsync(() => TickPlayerDamage(fraction));
    public Task<string> ApplyPlayerEffectAsync(int effectId) => RunAsync(() => _service.ApplyPlayerEffect(effectId));
    public Task<string> ApplyNinjaSetAsync() => RunAsync(_service.ApplyNinjaSet);
    public Task<string> ApplyConfessorSetAsync() => RunAsync(_service.ApplyConfessorSet);
    public async Task<string> ApplySmolCharacterAsync(TimeSpan duration)
    {
        PlayerScaleSnapshot? snapshot = null;
        var applied = await RunAsync(() =>
        {
            snapshot = CaptureAndSetPlayerScale(0.5f);
            return $"SMOL CHARACTER ACTIVE — {Math.Max(1, (int)Math.Round(duration.TotalSeconds))}s";
        }).ConfigureAwait(false);
        if (snapshot is null || !applied.StartsWith("SMOL CHARACTER ACTIVE", StringComparison.OrdinalIgnoreCase))
            return applied;

        await Task.Delay(duration).ConfigureAwait(false);
        return await RunAsync(() => RestorePlayerScale(snapshot.Value)).ConfigureAwait(false);
    }
    public async Task<string> ApplyInvisibleCharacterAsync(TimeSpan duration)
    {
        PlayerRenderSnapshot? snapshot = null;
        try
        {
            var seconds = Math.Clamp(duration.TotalSeconds, 1, 120);
            var applied = await RunAsync(() =>
            {
                snapshot = CaptureAndDisablePlayerRendering();
                return $"INVISIBLE CHARACTER ACTIVE — VIEWER ONLY · {Math.Round(seconds):0}s";
            }).ConfigureAwait(false);
            if (snapshot is null || !applied.StartsWith("INVISIBLE CHARACTER ACTIVE", StringComparison.OrdinalIgnoreCase))
                return applied;

            await Task.Delay(TimeSpan.FromSeconds(seconds)).ConfigureAwait(false);
            return await RunAsync(() => RestorePlayerRendering(snapshot.Value)).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            if (snapshot is { } captured)
            {
                try { _ = RestorePlayerRendering(captured); }
                catch { }
            }
            return $"INVISIBLE CHARACTER FAILED: {exception.Message}";
        }
    }

    private readonly record struct PlayerRenderSnapshot(ulong Player, ulong Identity, bool RenderWasEnabled);

    private static PlayerRenderSnapshot CaptureAndDisablePlayerRendering()
    {
        using var memory = new global::EnemyIntelReader.MemoryReader(ProcessName);
        var (_, player) = ResolveWorldAndPlayer(memory);
        var identity = memory.ReadUInt64(player + 0x08);
        if (identity is 0 or ulong.MaxValue)
            throw new InvalidOperationException("PLAYER IDENTITY IS INVALID");

        var flags = memory.ReadBytes(player + ChrInsRenderFlagsOffset, 1)[0];
        var renderWasEnabled = (flags & ChrInsEnableRenderMask) != 0;
        if (renderWasEnabled)
            memory.WriteBytes(player + ChrInsRenderFlagsOffset, [(byte)(flags & ~ChrInsEnableRenderMask)]);
        var verified = memory.ReadBytes(player + ChrInsRenderFlagsOffset, 1)[0];
        if ((verified & ChrInsEnableRenderMask) != 0)
            throw new InvalidOperationException("PLAYER RENDER FLAG DID NOT CLEAR");
        return new PlayerRenderSnapshot(player, identity, renderWasEnabled);
    }

    private static string RestorePlayerRendering(PlayerRenderSnapshot snapshot)
    {
        if (!snapshot.RenderWasEnabled)
            return "INVISIBLE CHARACTER COMPLETE — PREVIOUS HIDDEN STATE PRESERVED";
        using var memory = new global::EnemyIntelReader.MemoryReader(ProcessName);
        var (_, player) = ResolveWorldAndPlayer(memory);
        if (player != snapshot.Player || memory.ReadUInt64(player + 0x08) != snapshot.Identity)
            return "INVISIBLE CHARACTER ENDED — PLAYER CHANGED; STALE RESTORE SKIPPED";

        var flags = memory.ReadBytes(player + ChrInsRenderFlagsOffset, 1)[0];
        memory.WriteBytes(player + ChrInsRenderFlagsOffset, [(byte)(flags | ChrInsEnableRenderMask)]);
        var verified = memory.ReadBytes(player + ChrInsRenderFlagsOffset, 1)[0];
        if ((verified & ChrInsEnableRenderMask) == 0)
            throw new InvalidOperationException("PLAYER RENDER FLAG DID NOT RESTORE");
        return "INVISIBLE CHARACTER COMPLETE — PLAYER VISIBLE";
    }
    public Task<bool?> GetNoDamageStateAsync() => Task.Run(() => _service.GetPracticeFlagState("NO DAMAGE"));
    public Task<string> ToggleSteadyPlayerAsync() => RunAsync(() => _service.TogglePracticeFlag("STEADY PLAYER"));
    public Task<bool?> GetSteadyPlayerStateAsync() => Task.Run(() => _service.GetPracticeFlagState("STEADY PLAYER"));
    public Task<string> ToggleWeaponHitboxAsync() => RunAsync(() => _service.TogglePracticeFlag("WEAPON HITBOX"));
    public Task<bool?> GetWeaponHitboxStateAsync() => Task.Run(() => _service.GetPracticeFlagState("WEAPON HITBOX"));
    public Task<string> ToggleWorldVisibilityAsync() => RunAsync(() => _service.TogglePracticeFlag("SHOW MAP"));
    public Task<bool?> GetWorldVisibilityStateAsync() => Task.Run(() => _service.GetPracticeFlagState("SHOW MAP"));
    public Task<string> TogglePlayerHiddenAsync() => ApplyInvisibleCharacterAsync(TimeSpan.FromSeconds(20));
    public Task<string> SpawnEnemyAsync(EnemySpawnPreset preset, EnemySpawnScalingMode scalingMode, float lateralOffset = 0) =>
        RunAsync(() => SpawnEnemy(preset, scalingMode, lateralOffset));
    public Task<string> SpawnPlayerCloneAsync(bool ally) => RunAsync(() => SpawnPlayerClone(ally));
    public Task<string> ToggleSubjectLockAsync(ulong targetAddress) => RunAsync(() => ToggleSubjectLock(targetAddress));
    public Task<string> ToggleAttachedSubjectAsync(ulong targetAddress) => RunAsync(() => ToggleSubjectLock(targetAddress, true));
    public Task<string> ToggleBodyCameraAsync(ulong targetAddress) => RunAsync(() => ToggleSubjectLock(targetAddress, true, true));
    public Task<string> StopSubjectCameraAsync() => RunAsync(StopSubjectCamera, requireVerifiedSession: false);
    public Task<(string Status, ulong Address)> AcquireCameraSubjectAsync() => RunSubjectAcquireAsync();
    public Task<(string Status, ulong Address)> AcquireAndAttachCameraSubjectAsync() => RunSubjectAcquireAndAttachAsync();
    public Task<string> ToggleDialogueMotionAsync() => RunAsync(() =>
    {
        _dialogueMotionEnabled = !_dialogueMotionEnabled;
        return _dialogueMotionEnabled ? "DIALOGUE MOTION ARMED" : "DIALOGUE MOTION OFF";
    });
    public Task<string> ToggleMovementShakeAsync() => RunAsync(() =>
    {
        _movementShakeEnabled = !_movementShakeEnabled;
        return _movementShakeEnabled ? "MOVEMENT SHAKE ARMED" : "MOVEMENT SHAKE OFF";
    });
    public Task<string> ToggleAutoOrbitAsync() => RunAsync(() =>
    {
        _autoOrbitEnabled = !_autoOrbitEnabled;
        return _autoOrbitEnabled ? "AUTO 360 ARMED" : "AUTO 360 OFF";
    });

    public void SetAutoOrbitSpeedMultiplier(double multiplier) =>
        Volatile.Write(ref _autoOrbitSpeedMultiplier, Math.Clamp(multiplier, 0.1, 4.0));

    public double GetMovementSpeed()
    {
        var path = GetConfigPath();
        if (!File.Exists(path)) return 10.0;
        foreach (var line in File.ReadLines(path))
        {
            var trimmed = line.Trim();
            if (!trimmed.StartsWith("default_speed", StringComparison.OrdinalIgnoreCase)) continue;
            var separator = trimmed.IndexOf('=');
            if (separator >= 0 && double.TryParse(trimmed[(separator + 1)..].Trim(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var value))
                return value;
        }
        return 10.0;
    }

    public string SetMovementSpeed(double speed)
    {
        speed = Math.Clamp(speed, 0.5, 50.0);
        var path = GetConfigPath();
        if (!File.Exists(path)) return "SPEED FAILED: FREECAM CONFIG MISSING";
        var lines = File.ReadAllLines(path);
        var replaced = false;
        for (var index = 0; index < lines.Length; index++)
        {
            if (!lines[index].TrimStart().StartsWith("default_speed", StringComparison.OrdinalIgnoreCase)) continue;
            lines[index] = $"default_speed = {speed.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture)}";
            replaced = true;
            break;
        }
        if (!replaced) return "SPEED FAILED: DEFAULT SPEED KEY MISSING";
        File.WriteAllLines(path, lines);
        if (!IsGameRunning) return $"MOVEMENT SPEED {speed:0.0} SAVED";

        var reloadStatus = _service.ReloadFreecamConfig();
        return reloadStatus.Contains("FAILED", StringComparison.OrdinalIgnoreCase)
            ? $"MOVEMENT SPEED {speed:0.0} SAVED · {reloadStatus}"
            : $"MOVEMENT SPEED {speed:0.0} APPLIED LIVE";
    }

    public Task<string> SetCameraTransitionAsync(double seconds) => RunAsync(() => SetCameraTransition(seconds));

    private string SetCameraTransition(double seconds)
    {
        seconds = Math.Clamp(seconds, 0.1, 30.0);
        var path = GetConfigPath();
        if (!File.Exists(path)) return "TRANSITION FAILED: FREECAM CONFIG MISSING";
        var lines = File.ReadAllLines(path);
        var replaced = false;
        for (var index = 0; index < lines.Length; index++)
        {
            if (!lines[index].TrimStart().StartsWith("interpolation_time", StringComparison.OrdinalIgnoreCase)) continue;
            lines[index] = $"interpolation_time = {seconds.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture)}";
            replaced = true;
            break;
        }
        if (!replaced) return "TRANSITION FAILED: CONFIG KEY MISSING";
        File.WriteAllLines(path, lines);
        var reload = _service.ReloadFreecamConfig();
        return reload.Contains("FAILED", StringComparison.OrdinalIgnoreCase)
            ? reload
            : $"CAMERA TRANSITION {seconds:0.0}s";
    }

    private static string GetConfigPath()
    {
        var activeProfileConfig = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "garyttierney", "me3", "config", "profiles", "eldenring-mods",
            "Release", "Freecam", "config.ini");
        return File.Exists(activeProfileConfig)
            ? activeProfileConfig
            : Path.Combine(AppContext.BaseDirectory, "native", "EnemyIntelFreecam", "Freecam", "config.ini");
    }

    public CameraTrackDocument LoadCameraTrack()
    {
        try
        {
            var path = GetCameraTrackPath();
            if (!File.Exists(path)) return new CameraTrackDocument(10, [], 3.0, false, "Smooth", 0, "Untitled Shot");
            return JsonSerializer.Deserialize<CameraTrackDocument>(File.ReadAllText(path))
                ?? new CameraTrackDocument(10, [], 3.0, false, "Smooth", 0, "Untitled Shot");
        }
        catch { return new CameraTrackDocument(10, [], 3.0, false, "Smooth", 0, "Untitled Shot"); }
    }

    public void SaveCameraTrack(int capacity, IReadOnlyDictionary<int, CameraTrackKeyframe> points, bool loop, string name, IReadOnlyDictionary<int, CameraPointSettings> pointSettings)
    {
        var path = GetCameraTrackPath();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var document = new CameraTrackDocument(
            capacity,
            points.ToDictionary(pair => pair.Key, pair => pair.Value),
            3.0,
            loop,
            "Smooth",
            0,
            string.IsNullOrWhiteSpace(name) ? "Untitled Shot" : name.Trim(),
            pointSettings.ToDictionary(pair => pair.Key, pair => pair.Value));
        File.WriteAllText(path, JsonSerializer.Serialize(document, new JsonSerializerOptions { WriteIndented = true }));
    }

    public IReadOnlyList<string> ListNamedCameraTracks()
    {
        try
        {
            var folder = GetNamedTrackFolder();
            if (!Directory.Exists(folder)) return [];
            return Directory.GetFiles(folder, "*.json").Select(Path.GetFileNameWithoutExtension).OrderBy(name => name).ToArray()!;
        }
        catch { return []; }
    }

    public string SaveNamedCameraTrack(string name)
    {
        try
        {
            name = SanitizeTrackName(name);
            var source = GetCameraTrackPath();
            if (!File.Exists(source)) return "SAVE SHOT FAILED: CURRENT TRACK MISSING";
            var folder = GetNamedTrackFolder();
            Directory.CreateDirectory(folder);
            File.Copy(source, Path.Combine(folder, name + ".json"), true);
            return $"SHOT SAVED — {name.ToUpperInvariant()}";
        }
        catch (Exception exception) { return $"SAVE SHOT FAILED: {exception.Message}"; }
    }

    public (string Status, CameraTrackDocument? Document) LoadNamedCameraTrack(string name)
    {
        try
        {
            name = SanitizeTrackName(name);
            var path = Path.Combine(GetNamedTrackFolder(), name + ".json");
            if (!File.Exists(path)) return ($"LOAD SHOT FAILED: {name} NOT FOUND", null);
            var document = JsonSerializer.Deserialize<CameraTrackDocument>(File.ReadAllText(path));
            return document is null ? ("LOAD SHOT FAILED: INVALID FILE", null) : ($"SHOT LOADED — {name.ToUpperInvariant()}", document with { Name = name });
        }
        catch (Exception exception) { return ($"LOAD SHOT FAILED: {exception.Message}", null); }
    }

    private static string GetCameraTrackPath() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "EldenIntel", "camera-track.json");
    private static string GetNamedTrackFolder() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "EldenIntel", "CameraTracks");
    private static string SanitizeTrackName(string name)
    {
        name = string.IsNullOrWhiteSpace(name) ? "Untitled Shot" : name.Trim();
        foreach (var invalid in Path.GetInvalidFileNameChars()) name = name.Replace(invalid, '-');
        return name.Length > 60 ? name[..60] : name;
    }

    private async Task<(string Status, CameraTrackKeyframe? Keyframe)> RunCaptureAsync()
    {
        if (_disposed) return ("CAMERA SERVICE CLOSED", null);
        await _commandGate.WaitAsync().ConfigureAwait(false);
        try
        {
            return await Task.Run<(string Status, CameraTrackKeyframe? Keyframe)>(() =>
            {
                try
                {
                    using var memory = new global::EnemyIntelReader.MemoryReader(ProcessName);
                    var state = ReadDebugCamera(memory);
                    var subjectAddress = unchecked((ulong)Interlocked.Read(ref _activeSubjectAddress));
                    if (memory.IsLikelyPointer(subjectAddress))
                    {
                        try
                        {
                            var anchor = ReadSubjectPosition(memory, subjectAddress);
                            state = state with { AnchorX = anchor.X, AnchorY = anchor.Y, AnchorZ = anchor.Z };
                        }
                        catch { }
                    }
                    return ("CAMERA POINT CAPTURED", state);
                }
                catch (Exception exception) { return ($"CAMERA CAPTURE FAILED: {exception.Message}", null); }
            }).ConfigureAwait(false);
        }
        finally { _commandGate.Release(); }
    }

    private async Task<(string Status, ulong Address)> RunSubjectAcquireAsync()
    {
        if (_disposed) return ("CAMERA SERVICE CLOSED", 0);
        await _commandGate.WaitAsync().ConfigureAwait(false);
        try
        {
            return await Task.Run(() =>
            {
                try { return AcquireCameraSubject(); }
                catch (Exception exception) { return ($"SUBJECT ACQUIRE FAILED: {exception.Message}", 0UL); }
            }).ConfigureAwait(false);
        }
        finally { _commandGate.Release(); }
    }

    private async Task<(string Status, ulong Address)> RunSubjectAcquireAndAttachAsync()
    {
        if (_disposed) return ("CAMERA SERVICE CLOSED", 0);
        var antiCheat = AntiCheatVerifier.Check();
        if (antiCheat.State != AntiCheatState.Disabled)
            return ($"{antiCheat.Label} — MEMORY TOOLS BLOCKED", 0);
        await _commandGate.WaitAsync().ConfigureAwait(false);
        try
        {
            return await Task.Run(() =>
            {
                try
                {
                    var acquired = AcquireCameraSubject();
                    if (acquired.Address == 0) return acquired;

                    var status = ToggleSubjectLock(acquired.Address, true);
                    if (status.Equals("ACTION CAM RELEASED", StringComparison.OrdinalIgnoreCase))
                        status = ToggleSubjectLock(acquired.Address, true);
                    return (status, acquired.Address);
                }
                catch (Exception exception)
                {
                    return ($"ACTION CAM ACQUIRE FAILED: {exception.Message}", 0UL);
                }
            }).ConfigureAwait(false);
        }
        finally { _commandGate.Release(); }
    }

    private string PlayCameraTrack(IReadOnlyList<CameraTrackPlaybackPoint> points, bool loop)
    {
        if (points.Count < 2) return "ADD AT LEAST TWO CAMERA POINTS";
        if (points.Any(point => !IsValidCameraPoint(point.Camera)))
            return "CAMERA TRACK BLOCKED: INVALID CAMERA POINT";
        CancellationTokenSource cancellation;
        lock (_trackPlaybackGate)
        {
            _trackPlaybackCancellation?.Cancel();
            cancellation = new CancellationTokenSource();
            _trackPlaybackCancellation = cancellation;
        }
        var resumeInteractiveFreecam = IsDebugCameraActive();
        var trackSyncEnabled = false;
        try
        {
            // Subject/action camera may remain active to preserve target sync,
            // but only the rail is allowed to write camera transforms here.
            Interlocked.Exchange(ref _cameraTransformOwner, (int)CameraTransformOwner.CameraTrack);
            Thread.Sleep(20);
            if (resumeInteractiveFreecam)
            {
                var syncStatus = _service.BeginSynchronizedActionCamera(absolutePosition: true);
                if (IsFailure(syncStatus)) return $"CAMERA TRACK FAILED: {syncStatus}";
                trackSyncEnabled = true;
            }

            using var memory = new global::EnemyIntelReader.MemoryReader(ProcessName);
            var (gameRend, camera) = ResolveCameraPointers(memory);
            memory.WriteBytes(gameRend + GameRendFreeCameraModeOffset, BitConverter.GetBytes(FixedDebugCameraMode));
            const double frameSeconds = 1.0 / 120.0;

            var firstPoint = ResolveAnchoredCamera(memory, points[0].Camera);
            WriteTrackCamera(memory, camera, firstPoint, trackSyncEnabled);
            if (!VerifyAppliedCameraPoint(memory, camera, firstPoint, 1, out var firstFailure))
                return $"CAMERA TRACK BLOCKED: {firstFailure}";
            do
            {
                var segmentCount = loop ? points.Count : points.Count - 1;
                for (var index = 0; index < segmentCount; index++)
                {
                    var startPoint = points[index].Camera;
                    var endIndex = (index + 1) % points.Count;
                    var endPoint = points[endIndex];
                    var endStored = endPoint.Camera;
                    var previousStored = points[loop
                        ? (index - 1 + points.Count) % points.Count
                        : Math.Max(0, index - 1)].Camera;
                    var followingStored = points[loop
                        ? (endIndex + 1) % points.Count
                        : Math.Min(points.Count - 1, endIndex + 1)].Camera;
                    var segmentDuration = Math.Clamp(endPoint.Settings.TransitionSeconds, 0.1, 30.0);
                    AppendPlaybackSessionDiagnostic(segmentDuration, loop, points.Count);
                    var timer = System.Diagnostics.Stopwatch.StartNew();
                    while (timer.Elapsed.TotalSeconds < segmentDuration)
                    {
                        if (cancellation.IsCancellationRequested) return "CAMERA PLAYBACK STOPPED";
                        var linear = Math.Clamp(timer.Elapsed.TotalSeconds / segmentDuration, 0, 1);
                        var eased = ApplyEasing(linear, endPoint.Settings.Easing);
                        var pathSmoothing = Math.Clamp(endPoint.Settings.PathSmoothing, 0, 1);
                        // At higher smoothing, retain velocity through the waypoint instead of
                        // forcing the easing curve to brake to zero at every numbered point.
                        var travel = eased + (linear - eased) * pathSmoothing;
                        memory.WriteBytes(gameRend + GameRendFreeCameraModeOffset, BitConverter.GetBytes(FixedDebugCameraMode));
                        var previous = ResolveAnchoredCamera(memory, previousStored);
                        var start = ResolveAnchoredCamera(memory, startPoint);
                        var resolvedEnd = ResolveAnchoredCamera(memory, endStored);
                        var following = ResolveAnchoredCamera(memory, followingStored);
                        var baseCamera = InterpolatePath(
                            previous,
                            start,
                            resolvedEnd,
                            following,
                            (float)travel,
                            (float)pathSmoothing);
                        WriteTrackCamera(memory, camera, ApplyMotion(baseCamera, endPoint.Settings, timer.Elapsed.TotalSeconds, linear, false), trackSyncEnabled);
                        var remaining = frameSeconds - timer.Elapsed.TotalSeconds % frameSeconds;
                        if (remaining > 0.002) Thread.Sleep(Math.Max(1, (int)(remaining * 1000) - 1));
                        else Thread.SpinWait(64);
                    }
                    var end = ResolveAnchoredCamera(memory, endStored);
                    WriteTrackCamera(memory, camera, end, trackSyncEnabled);
                    if (!VerifyAppliedCameraPoint(memory, camera, end, endIndex + 1, out var pointFailure))
                        return $"CAMERA TRACK STOPPED: {pointFailure}";
                    var holdTimer = System.Diagnostics.Stopwatch.StartNew();
                    while (holdTimer.Elapsed.TotalSeconds < Math.Clamp(endPoint.Settings.HoldSeconds, 0, 30))
                    {
                        if (cancellation.IsCancellationRequested) return "CAMERA PLAYBACK STOPPED";
                        memory.WriteBytes(gameRend + GameRendFreeCameraModeOffset, BitConverter.GetBytes(FixedDebugCameraMode));
                        end = ResolveAnchoredCamera(memory, endStored);
                        WriteTrackCamera(memory, camera, ApplyMotion(end, endPoint.Settings, holdTimer.Elapsed.TotalSeconds, 1, true), trackSyncEnabled);
                        Thread.Sleep(8);
                    }
                }
            } while (loop && !cancellation.IsCancellationRequested);

            var finalStoredPoint = points[^1].Camera;
            var finalSettings = points[^1].Settings;
            var finalTimer = System.Diagnostics.Stopwatch.StartNew();
            while (!cancellation.IsCancellationRequested)
            {
                memory.WriteBytes(gameRend + GameRendFreeCameraModeOffset, BitConverter.GetBytes(FixedDebugCameraMode));
                var finalPoint = ResolveAnchoredCamera(memory, finalStoredPoint);
                WriteTrackCamera(memory, camera, ApplyMotion(finalPoint, finalSettings, finalTimer.Elapsed.TotalSeconds, 1, true), trackSyncEnabled);
                Thread.Sleep(8);
            }
            return $"CAMERA PLAYBACK STOPPED — {points.Count} POINTS";
        }
        catch (Exception exception) { return $"CAMERA TRACK FAILED: {exception.Message}"; }
        finally
        {
            lock (_trackPlaybackGate)
            {
                if (ReferenceEquals(_trackPlaybackCancellation, cancellation)) _trackPlaybackCancellation = null;
            }
            cancellation.Dispose();
            if (trackSyncEnabled) _service.EndSynchronizedActionCamera();
            if (!resumeInteractiveFreecam) ForceDebugCameraMode(0);
            lock (_subjectLockGate)
            {
                Interlocked.Exchange(
                    ref _cameraTransformOwner,
                    _subjectLockCancellation is not null
                        ? (int)CameraTransformOwner.SubjectCamera
                        : (int)CameraTransformOwner.InteractiveFreecam);
            }
            if (resumeInteractiveFreecam)
            {
                Thread.Sleep(20);
                if (!WaitForAnyDebugCameraMode(750))
                {
                    // Never leave the game stranded in fixed debug-camera mode.
                    ForceDebugCameraMode(0);
                }
            }
        }
    }

    private void WriteTrackCamera(
        global::EnemyIntelReader.MemoryReader memory,
        ulong camera,
        CameraTrackKeyframe point,
        bool synchronized)
    {
        if (synchronized)
        {
            // The native hook applies synchronized rail poses on the game's camera tick.
            // Writing the same pose directly here creates a second, differently paced
            // producer and presents as visible micro-jitter during track playback.
            _service.WriteSynchronizedActionCamera(
                Right(point), Up(point), Forward(point), Position(point), point.Fov);
            return;
        }

        WriteDebugCamera(memory, camera, point);
    }

    private CameraTrackKeyframe ResolveAnchoredCamera(
        global::EnemyIntelReader.MemoryReader memory,
        CameraTrackKeyframe point)
    {
        if (point.AnchorX is not float anchorX ||
            point.AnchorY is not float anchorY ||
            point.AnchorZ is not float anchorZ)
            return point;

        var subjectAddress = unchecked((ulong)Interlocked.Read(ref _activeSubjectAddress));
        if (!memory.IsLikelyPointer(subjectAddress)) return point;
        try
        {
            var liveAnchor = ReadSubjectPosition(memory, subjectAddress);
            var capturedAnchor = new Vector3(anchorX, anchorY, anchorZ);
            var translated = Position(point) + liveAnchor - capturedAnchor;
            return point with
            {
                PositionX = translated.X,
                PositionY = translated.Y,
                PositionZ = translated.Z
            };
        }
        catch { return point; }
    }

    private static bool IsValidCameraPoint(CameraTrackKeyframe point)
    {
        var values = new[]
        {
            point.RightX, point.RightY, point.RightZ,
            point.UpX, point.UpY, point.UpZ,
            point.ForwardX, point.ForwardY, point.ForwardZ,
            point.PositionX, point.PositionY, point.PositionZ, point.Fov
        };
        if (values.Any(value => !float.IsFinite(value))) return false;
        if (Position(point).LengthSquared() > 4_000_000_000f) return false;
        if (point.Fov is < 0.05f or > 3.0f) return false;
        var right = Right(point);
        var up = Up(point);
        var forward = Forward(point);
        return right.LengthSquared() is > 0.5f and < 1.5f &&
               up.LengthSquared() is > 0.5f and < 1.5f &&
               forward.LengthSquared() is > 0.5f and < 1.5f;
    }

    private static double ApplyEasing(double value, string easing) => easing.ToUpperInvariant() switch
    {
        "LINEAR" => value,
        "EASE IN" => value * value * value,
        "EASE OUT" => 1.0 - Math.Pow(1.0 - value, 3),
        "ACTION" => value < 0.7 ? 0.75 * Math.Pow(value / 0.7, 2) : 0.75 + 0.25 * Math.Sqrt((value - 0.7) / 0.3),
        _ => value * value * (3.0 - 2.0 * value)
    };

    private static CameraTrackKeyframe ApplyMotion(CameraTrackKeyframe camera, CameraPointSettings settings, double time, double progress, bool holding)
    {
        var effect = settings.MotionEffect?.ToUpperInvariant() ?? "NONE";
        var amount = Math.Clamp(settings.MotionAmount, 0, 1);
        if (effect == "NONE" || amount <= 0) return camera;

        var speed = Math.Clamp(settings.MotionSpeed, 0.2, 10);
        var phase = time * speed * Math.PI * 2 + settings.MotionSeed * 1.61803398875;
        var fade = Math.Clamp(settings.MotionFade, 0, 0.5);
        var envelope = holding ? 1.0 : fade <= 0 ? 1.0 : Math.Min(1.0, Math.Min(progress / fade, (1.0 - progress) / fade));
        double x = 0, y = 0, yaw = 0, pitch = 0, roll = 0;

        switch (effect)
        {
            case "SHAKE":
                x = Math.Sin(phase * 1.73) * 0.10;
                y = Math.Sin(phase * 2.31 + 1.2) * 0.11;
                yaw = Math.Sin(phase * 2.07 + 0.4) * 0.018;
                pitch = Math.Sin(phase * 2.83 + 2.1) * 0.016;
                roll = Math.Sin(phase * 1.37 + 0.8) * 0.012;
                break;
            case "BOB":
                x = Math.Cos(phase) * 0.045;
                y = Math.Sin(phase * 2) * 0.12;
                pitch = Math.Sin(phase * 2) * 0.006;
                roll = Math.Cos(phase) * 0.012;
                break;
            case "HANDHELD":
                x = (Math.Sin(phase * 0.37) + Math.Sin(phase * 0.91 + 1.7) * 0.35) * 0.07;
                y = (Math.Sin(phase * 0.43 + 2.4) + Math.Sin(phase * 1.13) * 0.25) * 0.065;
                yaw = Math.Sin(phase * 0.31 + 0.7) * 0.014;
                pitch = Math.Sin(phase * 0.29 + 2.2) * 0.012;
                roll = Math.Sin(phase * 0.23 + 1.1) * 0.009;
                break;
            case "IMPACT":
                var impactTime = holding ? time : (progress - 0.88) * 4.0;
                var pulse = holding ? Math.Exp(-time * 7.0) : Math.Exp(-Math.Pow((progress - 0.9) / 0.075, 2));
                envelope = pulse;
                x = Math.Sin(phase * 3.1) * 0.16;
                y = Math.Sin(phase * 4.3 + 1.1) * 0.13;
                yaw = Math.Sin(phase * 3.7) * 0.028;
                pitch = Math.Sin(phase * 4.9 + impactTime) * 0.024;
                roll = Math.Sin(phase * 2.9) * 0.018;
                break;
        }

        var scale = amount * envelope;
        var right = Right(camera);
        var up = Up(camera);
        var forward = Forward(camera);
        var position = Position(camera) + right * (float)(x * scale) + up * (float)(y * scale);
        var perturbedForward = SafeNormalize(forward + right * (float)(yaw * scale) + up * (float)(pitch * scale), forward);
        var perturbedRight = SafeNormalize(right - up * (float)(roll * scale), right);
        perturbedRight = SafeNormalize(perturbedRight - perturbedForward * Vector3.Dot(perturbedRight, perturbedForward), right);
        var perturbedUp = SafeNormalize(Vector3.Cross(perturbedForward, perturbedRight), up);
        perturbedRight = SafeNormalize(Vector3.Cross(perturbedUp, perturbedForward), perturbedRight);
        return new CameraTrackKeyframe(
            perturbedRight.X, perturbedRight.Y, perturbedRight.Z,
            perturbedUp.X, perturbedUp.Y, perturbedUp.Z,
            perturbedForward.X, perturbedForward.Y, perturbedForward.Z,
            position.X, position.Y, position.Z, camera.Fov);
    }

    private static CameraTrackKeyframe Interpolate(CameraTrackKeyframe start, CameraTrackKeyframe end, float amount)
    {
        var forward = SafeNormalize(Vector3.Lerp(Forward(start), Forward(end), amount), Forward(end));
        var blendedRight = SafeNormalize(Vector3.Lerp(Right(start), Right(end), amount), Right(end));
        var right = SafeNormalize(blendedRight - forward * Vector3.Dot(blendedRight, forward), Right(end));
        var up = SafeNormalize(Vector3.Cross(forward, right), Up(end));
        right = SafeNormalize(Vector3.Cross(up, forward), right);
        var position = Vector3.Lerp(Position(start), Position(end), amount);
        return new CameraTrackKeyframe(
            right.X, right.Y, right.Z,
            up.X, up.Y, up.Z,
            forward.X, forward.Y, forward.Z,
            position.X, position.Y, position.Z,
            float.Lerp(start.Fov, end.Fov, amount));
    }

    private static CameraTrackKeyframe InterpolatePath(
        CameraTrackKeyframe previous,
        CameraTrackKeyframe start,
        CameraTrackKeyframe end,
        CameraTrackKeyframe following,
        float amount,
        float smoothing)
    {
        var linear = Interpolate(start, end, amount);
        if (smoothing <= 0.0001f) return linear;

        var position = Vector3.Lerp(
            Position(linear),
            CatmullRom(Position(previous), Position(start), Position(end), Position(following), amount),
            smoothing);
        var forward = SafeNormalize(Vector3.Lerp(
            Forward(linear),
            CatmullRom(Forward(previous), Forward(start), Forward(end), Forward(following), amount),
            smoothing), Forward(linear));
        var blendedRight = SafeNormalize(Vector3.Lerp(
            Right(linear),
            CatmullRom(Right(previous), Right(start), Right(end), Right(following), amount),
            smoothing), Right(linear));
        var right = SafeNormalize(blendedRight - forward * Vector3.Dot(blendedRight, forward), Right(linear));
        var up = SafeNormalize(Vector3.Cross(forward, right), Up(linear));
        right = SafeNormalize(Vector3.Cross(up, forward), right);
        return new CameraTrackKeyframe(
            right.X, right.Y, right.Z,
            up.X, up.Y, up.Z,
            forward.X, forward.Y, forward.Z,
            position.X, position.Y, position.Z,
            float.Lerp(start.Fov, end.Fov, amount));
    }

    private static Vector3 CatmullRom(Vector3 p0, Vector3 p1, Vector3 p2, Vector3 p3, float amount)
    {
        var amount2 = amount * amount;
        var amount3 = amount2 * amount;
        return 0.5f * ((2f * p1) +
                       (-p0 + p2) * amount +
                       (2f * p0 - 5f * p1 + 4f * p2 - p3) * amount2 +
                       (-p0 + 3f * p1 - 3f * p2 + p3) * amount3);
    }

    private static Vector3 Position(CameraTrackKeyframe point) => new(point.PositionX, point.PositionY, point.PositionZ);
    private static Vector3 Right(CameraTrackKeyframe point) => new(point.RightX, point.RightY, point.RightZ);
    private static Vector3 Up(CameraTrackKeyframe point) => new(point.UpX, point.UpY, point.UpZ);
    private static Vector3 Forward(CameraTrackKeyframe point) => new(point.ForwardX, point.ForwardY, point.ForwardZ);
    private static Vector3 SafeNormalize(Vector3 value, Vector3 fallback) =>
        value.LengthSquared() > 0.000001f ? Vector3.Normalize(value) : Vector3.Normalize(fallback);

    private static CameraTrackKeyframe ReadDebugCamera(global::EnemyIntelReader.MemoryReader memory)
    {
        var bytes = memory.ReadBytes(ResolveCameraPointers(memory).DebugCamera + CameraMatrixOffset, 0x44);
        return new CameraTrackKeyframe(
            ReadFloat(bytes, 0x00), ReadFloat(bytes, 0x04), ReadFloat(bytes, 0x08),
            ReadFloat(bytes, 0x10), ReadFloat(bytes, 0x14), ReadFloat(bytes, 0x18),
            ReadFloat(bytes, 0x20), ReadFloat(bytes, 0x24), ReadFloat(bytes, 0x28),
            ReadFloat(bytes, 0x30), ReadFloat(bytes, 0x34), ReadFloat(bytes, 0x38),
            ReadFloat(bytes, 0x40));
    }

    private static (ulong GameRend, ulong DebugCamera) ResolveCameraPointers(global::EnemyIntelReader.MemoryReader memory)
    {
        var fieldArea = global::EnemyIntelReader.MainForm.ResolveGlobalPointerOrZero(memory, FieldAreaPattern);
        if (!memory.IsLikelyPointer(fieldArea)) throw new InvalidOperationException("FIELD AREA NOT READY");
        var gameRend = memory.ReadUInt64(fieldArea + FieldAreaGameRendOffset);
        if (!memory.IsLikelyPointer(gameRend)) throw new InvalidOperationException("GAME RENDERER NOT READY");
        var camera = memory.ReadUInt64(gameRend + GameRendDebugCameraOffset);
        if (!memory.IsLikelyPointer(camera)) throw new InvalidOperationException("DEBUG CAMERA NOT READY");
        return (gameRend, camera);
    }

    private bool IsDebugCameraActive()
    {
        if (_service.IsStandaloneFreecamEnabled()) return true;
        try
        {
            using var memory = new global::EnemyIntelReader.MemoryReader(ProcessName);
            var (gameRend, _) = ResolveCameraPointers(memory);
            return memory.ReadInt32(gameRend + GameRendFreeCameraModeOffset) != 0;
        }
        catch { return false; }
    }

    private static bool WaitForDebugCameraMode(int desiredMode, int timeoutMilliseconds)
    {
        try
        {
            using var memory = new global::EnemyIntelReader.MemoryReader(ProcessName);
            var (gameRend, _) = ResolveCameraPointers(memory);
            var timer = System.Diagnostics.Stopwatch.StartNew();
            while (timer.ElapsedMilliseconds < timeoutMilliseconds)
            {
                if (memory.ReadInt32(gameRend + GameRendFreeCameraModeOffset) == desiredMode) return true;
                Thread.Sleep(15);
            }
            return memory.ReadInt32(gameRend + GameRendFreeCameraModeOffset) == desiredMode;
        }
        catch { return false; }
    }

    private static bool WaitForAnyDebugCameraMode(int timeoutMilliseconds)
    {
        try
        {
            using var memory = new global::EnemyIntelReader.MemoryReader(ProcessName);
            var (gameRend, _) = ResolveCameraPointers(memory);
            var timer = System.Diagnostics.Stopwatch.StartNew();
            while (timer.ElapsedMilliseconds < timeoutMilliseconds)
            {
                if (memory.ReadInt32(gameRend + GameRendFreeCameraModeOffset) != 0) return true;
                Thread.Sleep(15);
            }
            return memory.ReadInt32(gameRend + GameRendFreeCameraModeOffset) != 0;
        }
        catch { return false; }
    }

    private static void ForceDebugCameraMode(int mode)
    {
        try
        {
            using var memory = new global::EnemyIntelReader.MemoryReader(ProcessName);
            var (gameRend, _) = ResolveCameraPointers(memory);
            memory.WriteBytes(gameRend + GameRendFreeCameraModeOffset, BitConverter.GetBytes(mode));
        }
        catch { }
    }

    private static void WriteDebugCamera(
        global::EnemyIntelReader.MemoryReader memory,
        ulong camera,
        CameraTrackKeyframe state,
        byte[]? reusableBuffer = null)
    {
        var bytes = reusableBuffer ?? new byte[0x44];
        WriteVector(bytes, 0x00, state.RightX, state.RightY, state.RightZ, 0);
        WriteVector(bytes, 0x10, state.UpX, state.UpY, state.UpZ, 0);
        WriteVector(bytes, 0x20, state.ForwardX, state.ForwardY, state.ForwardZ, 0);
        WriteVector(bytes, 0x30, state.PositionX, state.PositionY, state.PositionZ, 1);
        BitConverter.TryWriteBytes(bytes.AsSpan(0x40, sizeof(float)), state.Fov);
        memory.WriteBytes(camera + CameraMatrixOffset, bytes);
    }

    private static void WriteDebugCamera(
        global::EnemyIntelReader.MemoryReader memory,
        ulong camera,
        Vector3 right,
        Vector3 up,
        Vector3 forward,
        Vector3 position,
        float fov,
        byte[] reusableBuffer)
    {
        WriteVector(reusableBuffer, 0x00, right.X, right.Y, right.Z, 0);
        WriteVector(reusableBuffer, 0x10, up.X, up.Y, up.Z, 0);
        WriteVector(reusableBuffer, 0x20, forward.X, forward.Y, forward.Z, 0);
        WriteVector(reusableBuffer, 0x30, position.X, position.Y, position.Z, 1);
        BitConverter.TryWriteBytes(reusableBuffer.AsSpan(0x40, sizeof(float)), fov);
        memory.WriteBytes(camera + CameraMatrixOffset, reusableBuffer);
    }

    private static bool VerifyAppliedCameraPoint(
        global::EnemyIntelReader.MemoryReader memory,
        ulong camera,
        CameraTrackKeyframe expected,
        int pointNumber,
        out string failure)
    {
        Thread.Sleep(20);
        var bytes = memory.ReadBytes(camera + CameraMatrixOffset, 0x44);
        var actualForward = SafeNormalize(new Vector3(
            ReadFloat(bytes, 0x20), ReadFloat(bytes, 0x24), ReadFloat(bytes, 0x28)),
            Vector3.UnitZ);
        var actualPosition = new Vector3(
            ReadFloat(bytes, 0x30), ReadFloat(bytes, 0x34), ReadFloat(bytes, 0x38));
        var expectedForward = SafeNormalize(Forward(expected), Vector3.UnitZ);
        var alignment = Vector3.Dot(actualForward, expectedForward);
        var positionError = Vector3.Distance(actualPosition, Position(expected));
        AppendPlaybackDiagnostic(pointNumber, expectedForward, actualForward, alignment);
        if (alignment >= 0.999f && positionError <= 0.10f)
        {
            failure = string.Empty;
            return true;
        }

        failure = $"CAMERA POINT {pointNumber:00} REJECTED — AIM {alignment:0.000} · POSITION ERROR {positionError:0.00}";
        return false;
    }

    private static void AppendPlaybackDiagnostic(int pointNumber, Vector3 expected, Vector3 actual, float alignment)
    {
        try
        {
            var path = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "EldenIntel", "camera-playback.log");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.AppendAllText(path,
                $"{DateTime.Now:O} P{pointNumber:00} expected=({expected.X:0.0000},{expected.Y:0.0000},{expected.Z:0.0000}) " +
                $"actual=({actual.X:0.0000},{actual.Y:0.0000},{actual.Z:0.0000}) alignment={alignment:0.000000}{Environment.NewLine}");
        }
        catch { }
    }

    private static void AppendPlaybackSessionDiagnostic(double transitionSeconds, bool loop, int pointCount)
    {
        try
        {
            var path = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "EldenIntel", "camera-playback.log");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.AppendAllText(path,
                $"{DateTime.Now:O} PLAY transition={transitionSeconds:0.000}s loop={loop} points={pointCount}{Environment.NewLine}");
        }
        catch { }
    }

    private static float ReadFloat(byte[] bytes, int offset) => BitConverter.ToSingle(bytes, offset);
    private static void WriteVector(byte[] bytes, int offset, float x, float y, float z, float w)
    {
        BitConverter.TryWriteBytes(bytes.AsSpan(offset, sizeof(float)), x);
        BitConverter.TryWriteBytes(bytes.AsSpan(offset + 4, sizeof(float)), y);
        BitConverter.TryWriteBytes(bytes.AsSpan(offset + 8, sizeof(float)), z);
        BitConverter.TryWriteBytes(bytes.AsSpan(offset + 12, sizeof(float)), w);
    }

    private static ulong ResolvePlayerPhysicsPositionAddress(global::EnemyIntelReader.MemoryReader memory)
    {
        var world = ResolveWorldChrMan(memory);
        if (!memory.IsLikelyPointer(world)) throw new InvalidOperationException("WORLD CHARACTER MANAGER NOT READY");
        var players = memory.ReadUInt64(world + PlayersOffset);
        if (!memory.IsLikelyPointer(players)) throw new InvalidOperationException("PLAYER LIST NOT READY");
        var player = memory.ReadUInt64(players);
        if (!memory.IsLikelyPointer(player)) throw new InvalidOperationException("PLAYER NOT READY");
        var modules = memory.ReadUInt64(player + ChrInsModulesOffset);
        if (!memory.IsLikelyPointer(modules)) throw new InvalidOperationException("PLAYER MODULES NOT READY");
        var physics = memory.ReadUInt64(modules + ChrModulesPhysicsOffset);
        if (!memory.IsLikelyPointer(physics)) throw new InvalidOperationException("PLAYER PHYSICS NOT READY");
        return physics + ChrPhysicsLocalPositionOffset;
    }

    private string SpawnPlayerClone(bool ally)
    {
        VerifyEldenIntelCloneRowsLoaded();
        ConfigureRuntimeDebugCloneAi();

        var clone = new EnemySpawnPreset(
            "Seth Clone",
            "c0000",
            EldenIntelCloneNpcParamId,
            EldenIntelCloneThinkParamId,
            SuppressDeathRewards: true,
            CharacterInitId: EldenIntelCloneCharacterInitId);

        return SpawnEnemy(
            clone,
            EnemySpawnScalingMode.Authentic,
            lateralOffset: 0,
            teamTypeOverride: ally ? (byte)47 : (byte)6,
            displayName: ally ? "Ally Clone · Seth + Seth" : "Enemy Clone · Seth vs Seth",
            spawnAsPlayer: true,
            copyPlayerAppearance: false);
    }

    private static void ConfigureRuntimeDebugCloneAi()
    {
        using var memory = new global::EnemyIntelReader.MemoryReader(ProcessName);
        var row = ResolveRuntimeParamRow(memory, "NpcThinkParam", 200000011);

        // This actor is created by WorldChrMan's debug constructor, not the
        // native Buddy constructor. Leaving isBuddyAI enabled makes the logic
        // wait for a Buddy owner that this actor will never receive: teams can
        // perceive it, but it never chooses or attacks a target itself.
        memory.WriteBytes(row + 0x9B, [0]);                    // isBuddyAI
        memory.WriteBytes(row + 0x3E, BitConverter.GetBytes((ushort)9999)); // maxBackhomeDist
        memory.WriteBytes(row + 0x40, BitConverter.GetBytes((ushort)9999)); // backhomeDist
        memory.WriteBytes(row + 0x42, BitConverter.GetBytes((ushort)999));  // backhomeBattleDist
        memory.WriteBytes(row + 0x3C, BitConverter.GetBytes((ushort)30));   // nose_dist
        memory.WriteBytes(row + 0x36, BitConverter.GetBytes((ushort)30));   // eye_dist
        memory.WriteBytes(row + 0x4E, BitConverter.GetBytes((ushort)20));   // BattleStartDist
        memory.WriteBytes(row + 0x22, BitConverter.GetBytes(9999f));        // SightTargetForgetTime
        memory.WriteBytes(row + 0x4A, BitConverter.GetBytes(9999f));        // SoundTargetForgetTime
        memory.WriteBytes(row + 0xA2, BitConverter.GetBytes(99f));          // MemoryTargetForgetTime

        if (memory.ReadBytes(row + 0x9B, 1)[0] != 0)
            throw new InvalidOperationException("CLONE COMBAT AI PATCH DID NOT APPLY");
    }

    private static void VerifyEldenIntelCloneRowsLoaded()
    {
        using var memory = new global::EnemyIntelReader.MemoryReader(ProcessName);
        try
        {
            _ = ResolveRuntimeParamRow(memory, "NpcParam", 200000011);
            _ = ResolveRuntimeParamRow(memory, "NpcThinkParam", 200000011);
        }
        catch (InvalidOperationException exception)
        {
            throw new InvalidOperationException(
                "ELDENINTEL CLONE DATA IS NOT LOADED — RESTART ELDEN RING THROUGH THE ELDENINTEL ME3 PROFILE",
                exception);
        }
    }

    private static void EnableRuntimeMimicBuddyAi()
    {
        using var memory = new global::EnemyIntelReader.MemoryReader(ProcessName);
        var row = ResolveRuntimeParamRow(memory, "NpcThinkParam", 90603100);

        // Exact offsets from Spirit Battler's bundled NpcThinkParam Paramdef.
        // These changes live only in the loaded regulation tables and vanish
        // when Elden Ring exits; regulation.bin is never modified.
        memory.WriteBytes(row + 0x9B, [1]);                    // isBuddyAI
        memory.WriteBytes(row + 0x3E, BitConverter.GetBytes((ushort)9999)); // maxBackhomeDist
        memory.WriteBytes(row + 0x40, BitConverter.GetBytes((ushort)9999)); // backhomeDist
        memory.WriteBytes(row + 0x42, BitConverter.GetBytes((ushort)999));  // backhomeBattleDist
        memory.WriteBytes(row + 0x3C, BitConverter.GetBytes((ushort)30));   // nose_dist
        memory.WriteBytes(row + 0x36, BitConverter.GetBytes((ushort)30));   // eye_dist
        memory.WriteBytes(row + 0x4E, BitConverter.GetBytes((ushort)20));   // BattleStartDist
        memory.WriteBytes(row + 0x22, BitConverter.GetBytes(9999f));        // SightTargetForgetTime
        memory.WriteBytes(row + 0x4A, BitConverter.GetBytes(9999f));        // SoundTargetForgetTime
        memory.WriteBytes(row + 0xA2, BitConverter.GetBytes(99f));          // MemoryTargetForgetTime

        if (memory.ReadBytes(row + 0x9B, 1)[0] != 1)
            throw new InvalidOperationException("MIMIC BUDDY AI PATCH DID NOT APPLY");
    }

    private static ulong ResolveRuntimeParamRow(
        global::EnemyIntelReader.MemoryReader memory,
        string paramName,
        int rowId)
    {
        var manager = global::EnemyIntelReader.MainForm.ResolveGlobalPointerOrZero(
            memory,
            CsRegulationManagerPattern);
        if (!memory.IsLikelyPointer(manager))
            throw new InvalidOperationException("REGULATION MANAGER IS NOT READY");
        var master = manager + RegulationManagerParamMasterOffset;
        var start = memory.ReadUInt64(master);
        var end = memory.ReadUInt64(master + sizeof(ulong));
        if (!memory.IsLikelyPointer(start) || !memory.IsLikelyPointer(end) || end <= start)
            throw new InvalidOperationException("PARAM MASTER IS NOT READY");
        var count = (end - start) / sizeof(ulong);
        if (count is < 100 or > 1000)
            throw new InvalidOperationException("PARAM TABLE COUNT IS INVALID");

        for (ulong index = 0; index < count; index++)
        {
            var entry = memory.ReadUInt64(start + index * sizeof(ulong));
            if (!memory.IsLikelyPointer(entry) ||
                !string.Equals(ReadRuntimeParamName(memory, entry), paramName, StringComparison.Ordinal))
                continue;
            var root = memory.ReadUInt64(entry + ParamEntryDataRootOffset);
            if (!memory.IsLikelyPointer(root)) break;
            root = memory.ReadUInt64(root + ParamDataRootOffset);
            if (!memory.IsLikelyPointer(root)) break;
            var rowCount = BitConverter.ToUInt16(memory.ReadBytes(root + ParamRowCountOffset, 2), 0);
            for (var rowIndex = 0; rowIndex < rowCount; rowIndex++)
            {
                var rowEntry = root + ParamRowVectorOffset + (ulong)(rowIndex * ParamRowEntrySize);
                if (memory.ReadInt32(rowEntry) != rowId) continue;
                var offset = BitConverter.ToInt64(memory.ReadBytes(rowEntry + ParamRowEntryOffsetOffset, 8), 0);
                return (ulong)((long)root + offset);
            }
            break;
        }
        throw new InvalidOperationException($"{paramName} ROW {rowId} WAS NOT FOUND");
    }

    private static string ReadRuntimeParamName(
        global::EnemyIntelReader.MemoryReader memory,
        ulong entry)
    {
        var length = memory.ReadUInt64(entry + ParamEntryNameLengthOffset);
        var address = length <= 7 ? entry + ParamEntryNameOffset : memory.ReadUInt64(entry + ParamEntryNameOffset);
        if (!memory.IsLikelyPointer(address)) return string.Empty;
        var capacity = length is > 0 and < 90 ? (int)length + 1 : 90;
        var bytes = memory.ReadBytes(address, capacity * sizeof(ushort));
        for (var offset = 0; offset + 1 < bytes.Length; offset += 2)
            if (bytes[offset] == 0 && bytes[offset + 1] == 0)
                return System.Text.Encoding.Unicode.GetString(bytes, 0, offset);
        return string.Empty;
    }

    private string SpawnEnemy(
        EnemySpawnPreset preset,
        EnemySpawnScalingMode scalingMode,
        float lateralOffset,
        byte? teamTypeOverride = null,
        string? displayName = null,
        bool spawnAsPlayer = false,
        bool copyPlayerAppearance = false)
    {
        using var memory = new global::EnemyIntelReader.MemoryReader(ProcessName);
        var world = ResolveWorldChrMan(memory);
        if (!memory.IsLikelyPointer(world))
            throw new InvalidOperationException("WORLD CHARACTER MANAGER NOT READY");

        var request = memory.ReadUInt64(world + DebugCharacterSpawnerOffset);
        if (!memory.IsLikelyPointer(request))
            throw new InvalidOperationException("ENEMY SPAWNER IS NOT AVAILABLE FOR THIS GAME BUILD");

        var players = memory.ReadUInt64(world + PlayersOffset);
        var player = memory.IsLikelyPointer(players) ? memory.ReadUInt64(players) : 0;
        if (!memory.IsLikelyPointer(player))
            throw new InvalidOperationException("PLAYER NOT READY");
        if (!TryReadSubjectBasis(memory, player, out var playerBasis))
            throw new InvalidOperationException("PLAYER FACING DIRECTION IS UNAVAILABLE");

        var playerPosition = ReadVector3(memory, ResolvePlayerPhysicsPositionAddress(memory));
        var forward = SafeNormalize(
            new Vector3(-playerBasis.Forward.X, 0, -playerBasis.Forward.Z),
            Vector3.UnitZ);
        var right = SafeNormalize(Vector3.Cross(Vector3.UnitY, forward), Vector3.UnitX);
        // The debug request does not snap a new character to terrain before its
        // first physics tick. Give the capsule room to settle instead of
        // initializing inside terrain that rises between the player and spawn.
        var origin = playerPosition +
                     forward * EnemySpawnForwardDistance +
                     right * lateralOffset +
                     Vector3.UnitY * EnemySpawnVerticalClearance;
        var playerPhysics = memory.ReadUInt64(memory.ReadUInt64(player + ChrInsModulesOffset) + ChrModulesPhysicsOffset);
        if (!memory.IsLikelyPointer(playerPhysics))
            throw new InvalidOperationException("PLAYER ORIENTATION IS UNAVAILABLE");
        var playerOrientation = ReadQuaternion(memory, playerPhysics + ChrPhysicsOrientationOffset);
        var spawnOrientation = Quaternion.Normalize(
            Quaternion.CreateFromAxisAngle(Vector3.UnitY, MathF.PI) * playerOrientation);

        WaitForSpawnRequest(memory, request);
        var existingCharacters = SnapshotCharacterAddresses(memory, world);
        var spawnRecord = ReserveDebugSpawnRecord(memory, world);
        WriteVector3(memory, request + SpawnRequestPositionOffset, origin);
        memory.WriteBytes(request + SpawnRequestNpcParamOffset, BitConverter.GetBytes(preset.NpcParamId));
        memory.WriteBytes(request + SpawnRequestThinkParamOffset, BitConverter.GetBytes(preset.ThinkParamId));
        memory.WriteBytes(request + SpawnRequestEventEntityOffset, BitConverter.GetBytes(-1));
        memory.WriteBytes(request + SpawnRequestTalkIdOffset, BitConverter.GetBytes(-1));
        memory.WriteBytes(request + SpawnRequestCharacterInitOffset, BitConverter.GetBytes(preset.CharacterInitId));
        // The debug PlayerIns constructor requires manipulator type 5. Using
        // the enemy/default value (0) with c0000 leaves its model descriptor
        // malformed and can crash while the game resolves its UTF-16 model.
        memory.WriteBytes(
            request + SpawnRequestManipulatorOffset,
            BitConverter.GetBytes(spawnAsPlayer ? 5 : 0));
        // The debug creator has two distinct construction paths. c0000 sent
        // through the enemy path produces an EnemyIns with no player assembly,
        // which exists and can be targeted but renders invisible. The player
        // path builds the PlayerIns payload and copies the local model/cloth.
        memory.WriteBytes(request + SpawnRequestEnemyTypeOffset, [spawnAsPlayer ? (byte)1 : (byte)0]);

        // This descriptor is a fixed five-wide-character model field. Match
        // the game's debug table exactly: writing a larger zero-filled buffer
        // overwrites the fields that follow it.
        if (preset.Model.Length != 5)
            throw new InvalidOperationException($"MODEL ID MUST BE EXACTLY 5 CHARACTERS ({preset.Model})");
        var modelBytes = System.Text.Encoding.Unicode.GetBytes(preset.Model);
        memory.WriteBytes(request + SpawnRequestModelOffset, modelBytes);
        memory.WriteBytes(request + SpawnRequestCreateFlagOffset, [1]);
        // The game's debug constructor uses byte +0x0B of its corresponding
        // 0x10-byte result record to choose the EnemyIns/PlayerIns creation
        // path. The working debug table stamps this immediately after raising
        // the create flag; omitting it produces invisible or inert actors.
        memory.WriteBytes(spawnRecord + 0x0B, [spawnAsPlayer ? (byte)1 : (byte)0]);
        WaitForSpawnRequest(memory, request);

        var spawnedCharacter = WaitForDebugSpawnRecord(memory, spawnRecord);
        if (!memory.IsLikelyPointer(spawnedCharacter))
            spawnedCharacter = FindNewCharacter(memory, world, existingCharacters, origin);
        if ((spawnAsPlayer || copyPlayerAppearance) && spawnedCharacter != 0)
        {
            // Clone actors can inherit NoUpdate (-2) cadence while their
            // assembly finishes. Force normal simulation for both cadence
            // overrides so hostile clones can perceive and act.
            memory.WriteBytes(spawnedCharacter + ChrInsOmissionModeOverrideOffset, BitConverter.GetBytes(0));
            memory.WriteBytes(spawnedCharacter + ChrInsOmissionModeOverrideOffset + sizeof(int), BitConverter.GetBytes(0));
            if (spawnAsPlayer)
            {
                // The integrated native appearance detour owns body, face,
                // equipment, loose-part aliases, and renderer reconstruction.
                // Do not race it with CharaInit/ChrAsm memory imitation.
                System.Diagnostics.Debug.WriteLine($"[EldenIntel] Spawned clone at 0x{spawnedCharacter:X}, calling ApplyCharacterEffect(-2)");
                var result = _service.ApplyCharacterEffect(-2, spawnedCharacter);
                System.Diagnostics.Debug.WriteLine($"[EldenIntel] ApplyCharacterEffect result: {result}");
            }
        }
        var rewardsSuppressed = !preset.SuppressDeathRewards ||
                                (spawnedCharacter != 0 &&
                                 SuppressSpawnedCharacterRewards(memory, spawnedCharacter));
        var facingApplied = spawnedCharacter != 0 &&
                            ApplySpawnFacing(memory, spawnedCharacter, spawnOrientation);
        var teamApplied = teamTypeOverride is null ||
                          (spawnedCharacter != 0 &&
                           ApplySpawnTeamType(memory, spawnedCharacter, teamTypeOverride.Value));
        (string Label, float HpMultiplier, float DamageMultiplier)? scaling = null;
        var scalingPending = false;
        if (spawnedCharacter != 0)
        {
            try
            {
                scaling = ApplySpawnScaling(memory, player, spawnedCharacter, scalingMode);
            }
            catch (InvalidOperationException exception) when (
                exception.Message.Contains("SCALING DID NOT STABILIZE", StringComparison.OrdinalIgnoreCase))
            {
                // The engine can replace the spawned character's ChrData after
                // the debug request has already completed. The enemy exists and
                // is usable; failure to verify optional balancing must never turn
                // a successful spawn into an application-fatal exception.
                scalingPending = true;
            }
        }
        if (preset.SuppressDeathRewards && !rewardsSuppressed)
            throw new InvalidOperationException("SAFE CLONE CREATED, BUT REWARD ISOLATION COULD NOT BE VERIFIED");
        return $"SPAWNED {(displayName ?? preset.Name).ToUpperInvariant()}  •  FACING {(facingApplied ? "SET" : "PENDING")}" +
               (teamTypeOverride is not null
                   ? $"  •  TEAM {(teamApplied ? teamTypeOverride.Value == 47 ? "SPIRIT ALLY" : "ENEMY" : "PENDING")}" 
                   : "") +
               (preset.SuppressDeathRewards ? "  •  REWARDS BLOCKED" : "") +
               (scaling is not null
                   ? $"  •  {scaling.Value.Label} HP {scaling.Value.HpMultiplier:P0} / DMG {scaling.Value.DamageMultiplier:P0}"
                   : scalingPending ? "  •  SCALING PENDING — AUTHENTIC STATS" : "") +
               (spawnAsPlayer ? "  •  TRANSMOG INVENTORY RESOLVED" : "") +
               "  •  SESSION ONLY";
    }

    private static ulong ReserveDebugSpawnRecord(
        global::EnemyIntelReader.MemoryReader memory,
        ulong world)
    {
        var listOwner = memory.ReadUInt64(world + DebugCharacterListOffset);
        if (!memory.IsLikelyPointer(listOwner))
            throw new InvalidOperationException("DEBUG CHARACTER LIST IS NOT READY");
        var entries = memory.ReadUInt64(listOwner + DebugCharacterListEntriesOffset);
        if (!memory.IsLikelyPointer(entries))
            throw new InvalidOperationException("DEBUG CHARACTER RECORDS ARE NOT READY");

        const int entrySize = 0x10;
        const int maximumEntries = 256;
        for (var index = 0; index < maximumEntries; index++)
        {
            var entry = entries + (ulong)(index * entrySize);
            if (memory.ReadUInt64(entry) == 0) return entry;
        }

        throw new InvalidOperationException("DEBUG CHARACTER RECORD TABLE IS FULL");
    }

    private static ulong WaitForDebugSpawnRecord(
        global::EnemyIntelReader.MemoryReader memory,
        ulong spawnRecord)
    {
        for (var attempt = 0; attempt < 80; attempt++)
        {
            var character = memory.ReadUInt64(spawnRecord);
            if (memory.IsLikelyPointer(character)) return character;
            Thread.Sleep(25);
        }

        return 0;
    }

    private static bool ApplySpawnTeamType(
        global::EnemyIntelReader.MemoryReader memory,
        ulong spawnedCharacter,
        byte teamType)
    {
        const int teamTypeOffset = 0x6C;
        // Mimic Tear initializes and transforms over several game ticks. Keep
        // the requested allegiance authoritative through that handoff instead
        // of allowing its original boss team to win the race.
        for (var attempt = 0; attempt < 8; attempt++)
        {
            if (!memory.IsLikelyPointer(spawnedCharacter)) return false;
            memory.WriteBytes(spawnedCharacter + teamTypeOffset, [teamType]);
            Thread.Sleep(125);
        }

        return memory.ReadBytes(spawnedCharacter + teamTypeOffset, 1)[0] == teamType;
    }

    private string HealPlayer(float fraction)
    {
        using var memory = new global::EnemyIntelReader.MemoryReader(ProcessName);
        var world = ResolveWorldChrMan(memory);
        if (!memory.IsLikelyPointer(world)) throw new InvalidOperationException("WORLD CHARACTER MANAGER NOT READY");
        var players = memory.ReadUInt64(world + PlayersOffset);
        var player = memory.IsLikelyPointer(players) ? memory.ReadUInt64(players) : 0;
        if (!memory.IsLikelyPointer(player)) throw new InvalidOperationException("PLAYER NOT READY");
        var modules = memory.ReadUInt64(player + ChrInsModulesOffset);
        var data = memory.IsLikelyPointer(modules) ? memory.ReadUInt64(modules) : 0;
        if (!memory.IsLikelyPointer(data)) throw new InvalidOperationException("PLAYER VITALS NOT READY");

        var health = memory.ReadBytes(data + ChrDataHpOffset, 8);
        var current = BitConverter.ToInt32(health, 0);
        var maximum = BitConverter.ToInt32(health, 4);
        if (maximum is < 1 or > 100_000 || current < 0 || current > maximum)
            throw new InvalidOperationException("PLAYER HP FAILED VALIDATION");
        if (current == 0) return "HEAL PLAYER SKIPPED — PLAYER IS DEAD";

        var amount = Math.Max(1, (int)MathF.Round(maximum * Math.Clamp(fraction, 0.01f, 1f)));
        var healed = Math.Min(maximum, current + amount);
        if (healed == current) return $"HEAL PLAYER COMPLETE — ALREADY FULL ({current}/{maximum})";
        memory.WriteBytes(data + ChrDataHpOffset, BitConverter.GetBytes(healed));
        return $"HEAL PLAYER COMPLETE — {current} → {healed} / {maximum}";
    }

    private string DrainPlayerHealth(float fraction)
    {
        using var memory = new global::EnemyIntelReader.MemoryReader(ProcessName);
        var world = ResolveWorldChrMan(memory);
        if (!memory.IsLikelyPointer(world)) throw new InvalidOperationException("WORLD CHARACTER MANAGER NOT READY");
        var players = memory.ReadUInt64(world + PlayersOffset);
        var player = memory.IsLikelyPointer(players) ? memory.ReadUInt64(players) : 0;
        if (!memory.IsLikelyPointer(player)) throw new InvalidOperationException("PLAYER NOT READY");
        var modules = memory.ReadUInt64(player + ChrInsModulesOffset);
        var data = memory.IsLikelyPointer(modules) ? memory.ReadUInt64(modules) : 0;
        if (!memory.IsLikelyPointer(data)) throw new InvalidOperationException("PLAYER VITALS NOT READY");

        var health = memory.ReadBytes(data + ChrDataHpOffset, 8);
        var current = BitConverter.ToInt32(health, 0);
        var maximum = BitConverter.ToInt32(health, 4);
        if (maximum is < 1 or > 100_000 || current < 0 || current > maximum)
            throw new InvalidOperationException("PLAYER HP FAILED VALIDATION");
        if (current == 0) return "LIFE STEAL SKIPPED — PLAYER IS DEAD";

        var amount = Math.Max(1, (int)MathF.Round(maximum * Math.Clamp(fraction, 0.01f, 0.9f)));
        var remaining = Math.Max(1, current - amount);
        if (remaining == current) return $"LIFE STEAL COMPLETE — PLAYER PROTECTED AT {current}/{maximum}";
        memory.WriteBytes(data + ChrDataHpOffset, BitConverter.GetBytes(remaining));
        return $"LIFE STEAL COMPLETE — {current} → {remaining} / {maximum}";
    }

    private string TickPlayerDamage(float fraction)
    {
        using var memory = new global::EnemyIntelReader.MemoryReader(ProcessName);
        var world = ResolveWorldChrMan(memory);
        if (!memory.IsLikelyPointer(world)) throw new InvalidOperationException("WORLD CHARACTER MANAGER NOT READY");
        var players = memory.ReadUInt64(world + PlayersOffset);
        var player = memory.IsLikelyPointer(players) ? memory.ReadUInt64(players) : 0;
        if (!memory.IsLikelyPointer(player)) throw new InvalidOperationException("PLAYER NOT READY");
        var modules = memory.ReadUInt64(player + ChrInsModulesOffset);
        var data = memory.IsLikelyPointer(modules) ? memory.ReadUInt64(modules) : 0;
        if (!memory.IsLikelyPointer(data)) throw new InvalidOperationException("PLAYER VITALS NOT READY");

        var health = memory.ReadBytes(data + ChrDataHpOffset, 8);
        var current = BitConverter.ToInt32(health, 0);
        var maximum = BitConverter.ToInt32(health, 4);
        if (maximum is < 1 or > 100_000 || current < 0 || current > maximum)
            throw new InvalidOperationException("PLAYER HP FAILED VALIDATION");
        if (current == 0) return "DAMAGE TICK ENDED — PLAYER IS DEAD";

        var amount = Math.Max(1, (int)MathF.Round(maximum * Math.Clamp(fraction, 0.0001f, 0.5f)));
        var remaining = Math.Max(0, current - amount);
        memory.WriteBytes(data + ChrDataHpOffset, BitConverter.GetBytes(remaining));
        return $"DAMAGE TICK — {current} → {remaining} / {maximum}";
    }

    private readonly record struct PlayerScaleSnapshot(ulong Player, ulong Controller, float Width, float Height, float HeightZ);

    private static (ulong World, ulong Player) ResolveWorldAndPlayer(global::EnemyIntelReader.MemoryReader memory)
    {
        var world = ResolveWorldChrMan(memory);
        if (!memory.IsLikelyPointer(world)) throw new InvalidOperationException("WORLD CHARACTER MANAGER NOT READY");
        var players = memory.ReadUInt64(world + PlayersOffset);
        var player = memory.IsLikelyPointer(players) ? memory.ReadUInt64(players) : 0;
        if (!memory.IsLikelyPointer(player)) throw new InvalidOperationException("PLAYER NOT READY");
        return (world, player);
    }

    private static PlayerScaleSnapshot CaptureAndSetPlayerScale(float scale)
    {
        using var memory = new global::EnemyIntelReader.MemoryReader(ProcessName);
        var (_, player) = ResolveWorldAndPlayer(memory);
        var controller = memory.ReadUInt64(player + ChrInsChrCtrlOffset);
        if (!memory.IsLikelyPointer(controller)) throw new InvalidOperationException("PLAYER MODEL CONTROLLER NOT READY");
        var width = memory.ReadSingle(controller + ChrCtrlModelWidthOffset);
        var height = memory.ReadSingle(controller + ChrCtrlModelHeightOffset);
        var heightZ = memory.ReadSingle(controller + ChrCtrlModelHeightZOffset);
        if (!ValidModelScale(width) || !ValidModelScale(height) || !ValidModelScale(heightZ))
            throw new InvalidOperationException("PLAYER MODEL SCALE FAILED VALIDATION");
        var bytes = BitConverter.GetBytes(Math.Clamp(scale, 0.1f, 3f));
        memory.WriteBytes(controller + ChrCtrlModelWidthOffset, bytes);
        memory.WriteBytes(controller + ChrCtrlModelHeightOffset, bytes);
        memory.WriteBytes(controller + ChrCtrlModelHeightZOffset, bytes);
        return new PlayerScaleSnapshot(player, controller, width, height, heightZ);
    }

    private static string RestorePlayerScale(PlayerScaleSnapshot snapshot)
    {
        using var memory = new global::EnemyIntelReader.MemoryReader(ProcessName);
        var (_, player) = ResolveWorldAndPlayer(memory);
        if (player != snapshot.Player)
            return "SMOL CHARACTER ENDED — PLAYER CHANGED; STALE RESTORE SKIPPED";
        var controller = memory.ReadUInt64(player + ChrInsChrCtrlOffset);
        if (controller != snapshot.Controller)
            return "SMOL CHARACTER ENDED — MODEL CHANGED; STALE RESTORE SKIPPED";
        memory.WriteBytes(controller + ChrCtrlModelWidthOffset, BitConverter.GetBytes(snapshot.Width));
        memory.WriteBytes(controller + ChrCtrlModelHeightOffset, BitConverter.GetBytes(snapshot.Height));
        memory.WriteBytes(controller + ChrCtrlModelHeightZOffset, BitConverter.GetBytes(snapshot.HeightZ));
        return "SMOL CHARACTER COMPLETE — NORMAL SIZE RESTORED";
    }

    private static bool ValidModelScale(float value) => float.IsFinite(value) && value is >= 0.05f and <= 5f;

    private (string Label, float HpMultiplier, float DamageMultiplier)? ApplySpawnScaling(
        global::EnemyIntelReader.MemoryReader memory,
        ulong player,
        ulong character,
        EnemySpawnScalingMode mode)
    {
        if (mode == EnemySpawnScalingMode.Authentic)
            return ("AUTHENTIC", 1f, 1f);

        var playerGameData = memory.ReadUInt64(player + ChrInsPlayerGameDataOffset);
        if (!memory.IsLikelyPointer(playerGameData))
            throw new InvalidOperationException("PLAYER PROGRESSION DATA IS UNAVAILABLE");
        var level = memory.ReadUInt32(playerGameData + PlayerGameDataLevelOffset);
        var vigor = memory.ReadUInt32(playerGameData + PlayerGameDataVigorOffset);
        var maxHp = memory.ReadUInt32(playerGameData + PlayerGameDataMaxHpOffset);
        if (level is < 1 or > 713 || vigor is < 1 or > 99 || maxHp is < 100 or > 20_000)
            throw new InvalidOperationException("PLAYER PROGRESSION DATA FAILED VALIDATION");

        var levelProgress = Math.Clamp((level + 20f) / 170f, 0.12f, 1f);
        var vigorProgress = Math.Clamp((vigor + 5f) / 65f, 0.12f, 1f);
        var progress = Math.Clamp(levelProgress * 0.65f + vigorProgress * 0.35f, 0.12f, 1f);
        // Balance against the player's actual survivability rather than merely
        // multiplying the boss's original HP. A DLC boss at a small percentage
        // of its native health can still be wildly inappropriate for RL1.
        var targetHp = maxHp * 6.5f + level * 25f;
        var hpMultiplier = 1f;
        var damageMultiplier = MathF.Pow(progress, 0.55f);
        var maxHitFraction = 0.40f;
        var label = $"AUTO RL{level} VIG{vigor}";
        if (mode == EnemySpawnScalingMode.Cinematic)
        {
            targetHp = maxHp * 8f + level * 30f;
            damageMultiplier = Math.Clamp(damageMultiplier * 0.65f, 0.18f, 0.65f);
            maxHitFraction = 0.25f;
            label = $"CINEMATIC RL{level} VIG{vigor}";
        }

        var modules = memory.ReadUInt64(character + ChrInsModulesOffset);
        var data = memory.IsLikelyPointer(modules) ? memory.ReadUInt64(modules) : 0;
        if (!memory.IsLikelyPointer(data))
            throw new InvalidOperationException("SPAWNED CHARACTER VITALS ARE UNAVAILABLE");
        var health = memory.ReadBytes(data + ChrDataHpOffset, 16);
        var originalMax = BitConverter.ToInt32(health, 4);
        if (originalMax <= 0 || originalMax > 10_000_000)
            throw new InvalidOperationException("SPAWNED CHARACTER HP FAILED VALIDATION");
        // The first ChrData object can be a short-lived initialization shell
        // whose max HP is lower than the final boss object. Do not clamp the
        // desired profile to that transient value; the persistent guard owns
        // the requested session balance across later object replacements.
        var scaledMax = Math.Clamp((int)MathF.Round(targetHp), 1, 10_000_000);
        hpMultiplier = scaledMax / (float)originalMax;
        var scaledHealth = new byte[16];
        for (var offset = 0; offset < 16; offset += 4)
            BitConverter.GetBytes(scaledMax).CopyTo(scaledHealth, offset);

        var fieldHandle = memory.ReadUInt64(character + 0x08);
        RegisterScaledSpawn(
            fieldHandle,
            new ScaledSpawnProfile(character, damageMultiplier, maxHitFraction, scaledMax, DateTime.UtcNow));

        // Character initialization can replace ChrData or rewrite its derived
        // health fields for several frames after the debug spawn completes.
        // Reacquire the module on every pass and hold the requested profile
        // long enough for the final runtime object to inherit it.
        ulong finalData = 0;
        for (var pass = 0; pass < 12; pass++)
        {
            modules = memory.ReadUInt64(character + ChrInsModulesOffset);
            data = memory.IsLikelyPointer(modules) ? memory.ReadUInt64(modules) : 0;
            if (memory.IsLikelyPointer(data))
            {
                memory.WriteBytes(data + ChrDataHpOffset, scaledHealth);
                finalData = data;
            }
            Thread.Sleep(20);
        }
        // A mismatch here is no longer fatal or "authentic stats" fallback.
        // The registered guard will reacquire the final ChrData and enforce
        // the profile once Elden Ring finishes replacing its spawn objects.
        return (label, hpMultiplier, damageMultiplier);
    }

    private void RegisterScaledSpawn(ulong fieldHandle, ScaledSpawnProfile profile)
    {
        if (fieldHandle == ulong.MaxValue || fieldHandle == 0) return;
        lock (_spawnScalingGate)
        {
            _scaledSpawnsByHandle[fieldHandle] = profile;
            if (_spawnScalingCancellation is not null) return;
            StartSpawnDamageGuardLocked();
        }
    }

    private void StartSpawnDamageGuardLocked()
    {
        _spawnScalingCancellation = new CancellationTokenSource();
        var cancellationToken = _spawnScalingCancellation.Token;
        _spawnScalingThread = new Thread(() => RunSpawnDamageGuard(cancellationToken))
        {
            IsBackground = true,
            Name = "EnemyIntel Spawn Damage Guard",
            Priority = ThreadPriority.AboveNormal
        };
        _spawnScalingThread.Start();
    }

    private void RunSpawnDamageGuard(CancellationToken cancellationToken)
    {
        try
        {
            using var memory = new global::EnemyIntelReader.MemoryReader(ProcessName);
            var player = ResolveLocalPlayer(memory);
            var modules = memory.ReadUInt64(player + ChrInsModulesOffset);
            var data = memory.IsLikelyPointer(modules) ? memory.ReadUInt64(modules) : 0;
            if (!memory.IsLikelyPointer(data)) return;

            var hpAddress = data + ChrDataHpOffset;
            var previousHp = memory.ReadInt32(hpAddress);
            var pruneCounter = 0;
            while (!cancellationToken.IsCancellationRequested)
            {
                var currentHp = memory.ReadInt32(hpAddress);
                if (currentHp < previousHp)
                {
                    var lastHitBy = memory.ReadUInt64(player + ChrInsLastHitByOffset);
                    ScaledSpawnProfile? profile = null;
                    lock (_spawnScalingGate)
                    {
                        if (_scaledSpawnsByHandle.TryGetValue(lastHitBy, out var scaledSpawn))
                            profile = scaledSpawn;
                        else
                            profile = _scaledSpawnsByHandle.Values.FirstOrDefault(
                                spawn => spawn.Character == lastHitBy);
                    }
                    if (profile is { DamageMultiplier: > 0 and < 1 } scaled)
                    {
                        var maxPlayerHp = memory.ReadInt32(hpAddress + 4);
                        var rawDamage = previousHp - currentHp;
                        var scaledDamage = Math.Max(1, (int)MathF.Round(rawDamage * scaled.DamageMultiplier));
                        var hitCap = Math.Max(1, (int)MathF.Round(maxPlayerHp * scaled.MaxHitFraction));
                        var adjustedHp = previousHp - Math.Min(scaledDamage, hitCap);
                        // Post-hit damage correction must never resurrect the HP
                        // bar after Elden Ring has already committed a death. Both
                        // the actual HP value and ChrIns death flag are authoritative.
                        var playerFlags = memory.ReadBytes(player + ChrInsRenderFlagsOffset, 1)[0];
                        var gameCommittedDeath = currentHp <= 0 ||
                                                 (playerFlags & ChrInsDeathFlagMask) != 0;
                        if (!gameCommittedDeath && adjustedHp > 0)
                        {
                            currentHp = Math.Min(adjustedHp, maxPlayerHp);
                            memory.WriteBytes(hpAddress, BitConverter.GetBytes(currentHp));
                        }
                    }
                }
                previousHp = currentHp;
                if (++pruneCounter >= 250)
                {
                    pruneCounter = 0;
                    KeyValuePair<ulong, ScaledSpawnProfile>[] spawns;
                    lock (_spawnScalingGate) spawns = [.. _scaledSpawnsByHandle];
                    foreach (var spawn in spawns)
                    {
                        var alive = false;
                        try
                        {
                            var spawnModules = memory.ReadUInt64(spawn.Value.Character + ChrInsModulesOffset);
                            var spawnData = memory.IsLikelyPointer(spawnModules)
                                ? memory.ReadUInt64(spawnModules)
                                : 0;
                            alive = memory.IsLikelyPointer(spawnData) &&
                                    memory.ReadInt32(spawnData + ChrDataHpOffset) > 0 &&
                                    memory.ReadUInt64(spawn.Value.Character + 0x08) == spawn.Key;
                            if (alive)
                            {
                                var hp = memory.ReadInt32(spawnData + ChrDataHpOffset);
                                var runtimeMax = memory.ReadInt32(spawnData + ChrDataHpOffset + 4);
                                if (runtimeMax != spawn.Value.ScaledMaxHp)
                                {
                                    var requestedMax = spawn.Value.ScaledMaxHp;
                                    memory.WriteBytes(spawnData + ChrDataHpOffset, BitConverter.GetBytes(Math.Min(hp, requestedMax)));
                                    for (var offset = 4; offset < 16; offset += 4)
                                        memory.WriteBytes(spawnData + ChrDataHpOffset + (ulong)offset, BitConverter.GetBytes(requestedMax));
                                }
                            }
                        }
                        catch { }
                        // Spawn construction can temporarily detach or replace
                        // ChrData. Keep the profile through that handoff rather
                        // than permanently losing scaling on the first miss.
                        if (!alive && DateTime.UtcNow - spawn.Value.RegisteredAtUtc >= TimeSpan.FromSeconds(12))
                        {
                            lock (_spawnScalingGate) _scaledSpawnsByHandle.Remove(spawn.Key);
                        }
                    }
                    lock (_spawnScalingGate)
                    {
                        if (_scaledSpawnsByHandle.Count == 0) break;
                    }
                }
                Thread.Sleep(2);
            }
        }
        catch { }
        finally
        {
            lock (_spawnScalingGate)
            {
                _spawnScalingCancellation?.Dispose();
                _spawnScalingCancellation = null;
                _spawnScalingThread = null;
                if (!_disposed && _scaledSpawnsByHandle.Count > 0)
                    StartSpawnDamageGuardLocked();
            }
        }
    }

    private readonly record struct ScaledSpawnProfile(
        ulong Character,
        float DamageMultiplier,
        float MaxHitFraction,
        int ScaledMaxHp,
        DateTime RegisteredAtUtc);

    private static bool SuppressSpawnedCharacterRewards(
        global::EnemyIntelReader.MemoryReader memory,
        ulong character)
    {
        try
        {
            var address = character + ChrInsDeathRewardFlagsOffset;
            var flags = memory.ReadBytes(address, 1)[0];
            memory.WriteBytes(address, [(byte)(flags | ChrInsDeathRewardsHandledMask)]);
            return (memory.ReadBytes(address, 1)[0] & ChrInsDeathRewardsHandledMask) ==
                   ChrInsDeathRewardsHandledMask;
        }
        catch
        {
            return false;
        }
    }

    private static void WaitForSpawnRequest(global::EnemyIntelReader.MemoryReader memory, ulong request)
    {
        for (var attempt = 0; attempt < 40; attempt++)
        {
            if (memory.ReadBytes(request + SpawnRequestCreateFlagOffset, 1)[0] == 0) return;
            Thread.Sleep(25);
        }

        throw new InvalidOperationException("GAME DID NOT ACCEPT THE PREVIOUS SPAWN REQUEST");
    }

    private static HashSet<ulong> SnapshotCharacterAddresses(
        global::EnemyIntelReader.MemoryReader memory,
        ulong world)
    {
        var result = new HashSet<ulong>();
        var begin = memory.ReadUInt64(world + CharacterListBeginOffset);
        var end = memory.ReadUInt64(world + CharacterListEndOffset);
        if (!memory.IsLikelyPointer(begin) || end < begin || end - begin > 100_000 * 8UL)
            return result;
        for (var cursor = begin; cursor < end; cursor += 8)
        {
            try
            {
                var character = memory.ReadUInt64(cursor);
                if (memory.IsLikelyPointer(character)) result.Add(character);
            }
            catch { }
        }
        return result;
    }

    private static ulong FindNewCharacter(
        global::EnemyIntelReader.MemoryReader memory,
        ulong world,
        HashSet<ulong> existingCharacters,
        Vector3 expectedPosition,
        int attempts = 30,
        float maxDistanceSquared = 25f)
    {
        for (var attempt = 0; attempt < attempts; attempt++)
        {
            ulong best = 0;
            var bestDistance = maxDistanceSquared;
            foreach (var candidate in SnapshotCharacterAddresses(memory, world))
            {
                if (existingCharacters.Contains(candidate)) continue;
                try
                {
                    var distance = Vector3.DistanceSquared(ReadSubjectPosition(memory, candidate), expectedPosition);
                    if (distance >= bestDistance) continue;
                    bestDistance = distance;
                    best = candidate;
                }
                catch { }
            }
            if (best != 0) return best;
            Thread.Sleep(50);
        }
        return 0;
    }

    private static bool ApplySpawnFacing(
        global::EnemyIntelReader.MemoryReader memory,
        ulong character,
        Quaternion orientation)
    {
        var wroteOrientation = false;
        var bytes = new byte[16];
        BitConverter.GetBytes(orientation.X).CopyTo(bytes, 0);
        BitConverter.GetBytes(orientation.Y).CopyTo(bytes, 4);
        BitConverter.GetBytes(orientation.Z).CopyTo(bytes, 8);
        BitConverter.GetBytes(orientation.W).CopyTo(bytes, 12);

        // These are the authoritative current and interpolation-target
        // quaternions used to construct the character matrices. Character
        // initialization can replace the physics module for several frames,
        // so reacquire it on every pass.
        for (var frame = 0; frame < 18; frame++)
        {
            try
            {
                var modules = memory.ReadUInt64(character + ChrInsModulesOffset);
                var physics = memory.IsLikelyPointer(modules)
                    ? memory.ReadUInt64(modules + ChrModulesPhysicsOffset)
                    : 0;
                if (memory.IsLikelyPointer(physics))
                {
                    memory.WriteBytes(physics + ChrPhysicsOrientationOffset, bytes);
                    memory.WriteBytes(physics + ChrPhysicsInterpolatedOrientationOffset, bytes);
                    wroteOrientation = true;
                }
            }
            catch { }
            Thread.Sleep(20);
        }
        return wroteOrientation;
    }

    private static Quaternion ReadQuaternion(
        global::EnemyIntelReader.MemoryReader memory,
        ulong address)
    {
        var bytes = memory.ReadBytes(address, 16);
        var value = new Quaternion(
            ReadFloat(bytes, 0),
            ReadFloat(bytes, 4),
            ReadFloat(bytes, 8),
            ReadFloat(bytes, 12));
        if (!float.IsFinite(value.X) || !float.IsFinite(value.Y) ||
            !float.IsFinite(value.Z) || !float.IsFinite(value.W) ||
            value.LengthSquared() < 0.25f)
            throw new InvalidOperationException("CHARACTER ORIENTATION IS INVALID");
        return Quaternion.Normalize(value);
    }

    private static Vector3 ReadVector3(global::EnemyIntelReader.MemoryReader memory, ulong address)
    {
        var bytes = memory.ReadBytes(address, 12);
        return new Vector3(ReadFloat(bytes, 0), ReadFloat(bytes, 4), ReadFloat(bytes, 8));
    }

    private static void WriteVector3(global::EnemyIntelReader.MemoryReader memory, ulong address, Vector3 value)
    {
        var bytes = new byte[12];
        BitConverter.GetBytes(value.X).CopyTo(bytes, 0);
        BitConverter.GetBytes(value.Y).CopyTo(bytes, 4);
        BitConverter.GetBytes(value.Z).CopyTo(bytes, 8);
        memory.WriteBytes(address, bytes);
    }

    private (string Status, ulong Address) AcquireCameraSubject()
    {
        if (!IsDebugCameraActive()) return ("SUBJECT ACQUIRE FAILED: ENABLE FREECAM FIRST", 0);
        using var memory = new global::EnemyIntelReader.MemoryReader(ProcessName);
        var camera = ReadDebugCamera(memory);
        var cameraPosition = Position(camera);
        var cameraForward = SafeNormalize(Forward(camera), Vector3.UnitZ);

        var world = ResolveWorldChrMan(memory);
        if (!memory.IsLikelyPointer(world)) throw new InvalidOperationException("WORLD CHARACTER MANAGER NOT READY");
        var players = memory.ReadUInt64(world + PlayersOffset);
        var player = memory.IsLikelyPointer(players) ? memory.ReadUInt64(players) : 0;
        var begin = memory.ReadUInt64(world + CharacterListBeginOffset);
        var end = memory.ReadUInt64(world + CharacterListEndOffset);
        if (!memory.IsLikelyPointer(begin) || end < begin || end - begin > 100_000 * 8UL)
            throw new InvalidOperationException("CHARACTER LIST NOT READY");

        ulong bestAddress = 0;
        var bestScore = float.MaxValue;
        const float maximumAcquireDistance = 300f;
        const float minimumAcquireAlignment = 0.94f;
        void ConsiderCandidate(ulong candidate)
        {
            if (!memory.IsLikelyPointer(candidate)) return;
            try
            {
                // The local player's controller model matrix is not a stable
                // acquisition source while freecam owns the view. Tracking
                // already uses the player's authoritative physics position;
                // use the same source while choosing a subject.
                var position = (candidate == player
                    ? ReadPlayerPhysicsPosition(memory, candidate)
                    : ReadSubjectPosition(memory, candidate)) + Vector3.UnitY * SubjectAimHeight;
                var offset = position - cameraPosition;
                var distance = offset.Length();
                if (distance < 0.5f || distance > maximumAcquireDistance) return;
                var direction = offset / distance;
                var alignment = Vector3.Dot(cameraForward, direction);
                // Keep acquisition inside a narrow, visible forward cone. The
                // old score made tiny alignment differences worth hundreds of
                // units of range, so a distant character could beat the clear
                // subject directly in front of the camera.
                if (alignment < minimumAcquireAlignment) return;
                var score = (1f - alignment) * 20f + distance * 0.05f;
                if (score >= bestScore) return;
                bestScore = score;
                bestAddress = candidate;
            }
            catch { }
        }

        // The player is not guaranteed to appear in the ordinary character
        // list, so evaluate the canonical player pointer explicitly.
        ConsiderCandidate(player);
        for (var cursor = begin; cursor < end; cursor += 8)
        {
            ulong candidate;
            try { candidate = memory.ReadUInt64(cursor); }
            catch { continue; }
            if (candidate == player) continue;
            ConsiderCandidate(candidate);
        }

        return bestAddress == 0
            ? ("NO CAMERA SUBJECT NEAR VIEW CENTER", 0)
            : ("CAMERA SUBJECT ACQUIRED", bestAddress);
    }

    private static ulong ResolveWorldChrMan(global::EnemyIntelReader.MemoryReader memory)
    {
        var world = global::EnemyIntelReader.MainForm.ResolveGlobalPointerOrZero(
            memory,
            WorldChrManPattern);
        if (!memory.IsLikelyPointer(world))
            throw new InvalidOperationException("WORLD CHARACTER MANAGER NOT READY");
        return world;
    }

    private static bool IsPlayerCharacterAddress(ulong address)
    {
        if (address == 0) return false;
        try
        {
            using var memory = new global::EnemyIntelReader.MemoryReader(ProcessName);
            var world = ResolveWorldChrMan(memory);
            if (!memory.IsLikelyPointer(world)) return false;
            var players = memory.ReadUInt64(world + PlayersOffset);
            if (!memory.IsLikelyPointer(players)) return false;
            return memory.ReadUInt64(players) == address;
        }
        catch
        {
            return false;
        }
    }

    private string ToggleSubjectLock(ulong targetAddress, bool attachToSubject = false, bool bodyCamera = false)
    {
        var switchingModes = false;
        CameraTrackKeyframe? preservedCamera = null;
        lock (_subjectLockGate)
        {
            if (_subjectLockCancellation is not null)
            {
                switchingModes = _subjectLockAttached != attachToSubject ||
                                 _subjectBodyOrientationEnabled != bodyCamera;
                if (switchingModes)
                {
                    // Snapshot the transform while the outgoing subject mode
                    // still owns it. Its cleanup releases synchronized-camera
                    // ownership and can briefly expose the native/player camera;
                    // reading after that handoff caused mode-switch teleports.
                    try
                    {
                        using var memory = new global::EnemyIntelReader.MemoryReader(ProcessName);
                        preservedCamera = ReadDebugCamera(memory);
                    }
                    catch
                    {
                        // Fall back to the normal post-release read if the live
                        // snapshot is temporarily unavailable.
                    }
                }
                _subjectLockCancellation.Cancel();
                var thread = _subjectLockThread;
                Monitor.Exit(_subjectLockGate);
                try { thread?.Join(1500); }
                finally { Monitor.Enter(_subjectLockGate); }
                if (thread?.IsAlive == true) return "CAMERA MODE IS STILL RELEASING — TRY AGAIN";
                if (!switchingModes)
                    return bodyCamera ? "BODY CAM RELEASED" :
                           attachToSubject ? "ACTION CAM RELEASED" : "SUBJECT LOCK RELEASED";
            }
        }

        if (targetAddress == 0) return "SUBJECT LOCK NEEDS A CURRENT OR RECENT ENEMY";
        if (!IsDebugCameraActive()) return "ENABLE FREECAM BEFORE SUBJECT LOCK";
        try
        {
            CameraTrackKeyframe camera;
            using (var memory = new global::EnemyIntelReader.MemoryReader(ProcessName))
            {
                if (!memory.IsLikelyPointer(targetAddress)) return "SUBJECT LOCK TARGET IS NO LONGER VALID";
                _ = ReadSubjectPosition(memory, targetAddress);
                camera = preservedCamera ?? ReadDebugCamera(memory);
            }

            var isPlayerTarget = IsPlayerCharacterAddress(targetAddress);

            ApplyActionCameraInputPolicy();
            // Subject focus publishes a complete smoothed world-space pose. Keep position
            // and orientation under the same owner so the native camera cannot accumulate
            // an independent position baseline and later snap back into alignment.
            var syncStatus = _service.BeginSynchronizedActionCamera(absolutePosition: true);
            if (IsFailure(syncStatus)) return $"SUBJECT LOCK FAILED: {syncStatus}";
            ApplyActionCameraInputPolicy();
            if (IsRealPaused()) SetRealPaused(false);
            _freecamAppliedRealPause = false;
            // Subject lock must honor the same controller ownership policy as
            // attached Action Cam. Dialogue interaction (controller Y) is a
            // player input even when the camera itself is only tracking the NPC.
            var restorePlayerInputBlock = _service.IsPlayerInputBlocked;
            if (_actionControllerControlsPlayer) _service.EnsurePlayerInputEnabled();
            else _service.EnsurePlayerInputBlocked();

            var cancellation = new CancellationTokenSource();
            lock (_subjectLockGate)
            {
                _subjectLockCancellation = cancellation;
                _subjectLockAttached = attachToSubject;
                _subjectBodyOrientationEnabled = bodyCamera;
                if ((CameraTransformOwner)Volatile.Read(ref _cameraTransformOwner) != CameraTransformOwner.CameraTrack)
                    Interlocked.Exchange(ref _cameraTransformOwner, (int)CameraTransformOwner.SubjectCamera);
                Interlocked.Exchange(ref _activeSubjectAddress, unchecked((long)targetAddress));
                _playerTargetSyncThread = isPlayerTarget ? null : new Thread(() => PlayerTargetSyncLoop(targetAddress, cancellation.Token))
                {
                    IsBackground = true,
                    Name = "EnemyIntelPlayerTargetSync"
                };
                _subjectLockThread = new Thread(() => SubjectLockLoop(
                    targetAddress,
                    camera,
                    cancellation,
                    attachToSubject,
                    restorePlayerInputBlock,
                    isPlayerTarget))
                {
                    IsBackground = true,
                    Name = "EnemyIntelSubjectLock"
                };
                _playerTargetSyncThread?.Start();
                _subjectLockThread.Start();
            }
            return bodyCamera
                ? "BODY CAM MOUNTED — SUBJECT-RELATIVE ORIENTATION"
                : attachToSubject
                ? "ACTION CAM ATTACHED — LIVE ACTION UNPAUSED"
                : "SUBJECT LOCK ACTIVE — LIVE ACTION UNPAUSED";
        }
        catch (Exception exception)
        {
            _service.EndSynchronizedActionCamera();
            return $"SUBJECT LOCK FAILED: {exception.Message}";
        }
    }

    private string StopSubjectCamera()
    {
        Thread? thread;
        lock (_subjectLockGate)
        {
            _subjectLockCancellation?.Cancel();
            thread = _subjectLockThread;
        }

        if (thread is not null && thread != Thread.CurrentThread)
            thread.Join(1500);
        _service.StopActionCameraInput();
        _service.EndSynchronizedActionCamera();
        return thread?.IsAlive == true
            ? "CAMERA SUBJECT RELEASE IS STILL FINISHING"
            : "CAMERA SUBJECT RELEASED";
    }

    private void SubjectLockLoop(
        ulong targetAddress,
        CameraTrackKeyframe camera,
        CancellationTokenSource cancellation,
        bool attachToSubject,
        bool restorePlayerInputBlock,
        bool isPlayerTarget)
    {
        int? originalOmissionOverride = null;
        ulong targetIdentity = 0;
        try
        {
            using var memory = new global::EnemyIntelReader.MemoryReader(ProcessName);
            targetIdentity = memory.ReadUInt64(targetAddress + 0x08);
            if (targetIdentity == 0 || targetIdentity == ulong.MaxValue)
                throw new InvalidOperationException("SUBJECT IDENTITY IS INVALID");
            if (isPlayerTarget)
            {
                // A previous player-focus build accidentally maintained the
                // enemy omission override on the local player. Repair only that
                // exact residue; normal player state is the engine default -2.
                if (memory.ReadInt32(targetAddress + ChrInsOmissionModeOverrideOffset) == 0)
                    memory.WriteBytes(
                        targetAddress + ChrInsOmissionModeOverrideOffset,
                        BitConverter.GetBytes(-2));
            }
            else
            {
                originalOmissionOverride = memory.ReadInt32(targetAddress + ChrInsOmissionModeOverrideOffset);
                memory.WriteBytes(
                    targetAddress + ChrInsOmissionModeOverrideOffset,
                    BitConverter.GetBytes(0));
                if (memory.ReadInt32(targetAddress + ChrInsOmissionModeOverrideOffset) != 0)
                    throw new InvalidOperationException("SUBJECT ANIMATION OVERRIDE DID NOT APPLY");
            }
            Vector3 ReadTrackedPosition() => isPlayerTarget
                ? ReadPlayerPhysicsPosition(memory, targetAddress)
                : ReadSubjectPosition(memory, targetAddress);
            var smoothedSubject = ReadTrackedPosition() + Vector3.UnitY * SubjectAimHeight;
            var fixedCameraPosition = Position(camera);
            var smoothedCameraPosition = fixedCameraPosition;
            var smoothedAimSubject = smoothedSubject;
            var subjectFollowVelocity = Vector3.Zero;
            var cameraFollowVelocity = Vector3.Zero;
            var aimFollowVelocity = Vector3.Zero;
            var subjectOffset = fixedCameraPosition - smoothedSubject;
            var orbitRadius = MathF.Max(subjectOffset.Length(), 0.35f);
            var orbitYaw = MathF.Atan2(subjectOffset.X, subjectOffset.Z);
            var orbitPitch = MathF.Asin(Math.Clamp(subjectOffset.Y / orbitRadius, -1f, 1f));
            var verticalCameraOffset = 0f;
            var aimPitch = 0f;
            var aimYaw = 0f;
            var liveFov = camera.Fov;
            SubjectBasis? smoothedBodyBasis = null;
            SubjectBasis? enteringBodyBasis = null;
            var enteringCameraBasis = new SubjectBasis(Right(camera), Up(camera), Forward(camera));
            var bodyOrientationActive = false;
            var lastUpdate = System.Diagnostics.Stopwatch.GetTimestamp();
            var nextCameraUpdate = lastUpdate;
            var lastValidSubject = smoothedSubject;
            var lastValidSubjectAt = Environment.TickCount64;
            var estimatedGroundHeight = smoothedSubject.Y - SubjectAimHeight;
            var previousSubjectRootHeight = estimatedGroundHeight;
            var mouseCameraInputLocked = false;
            var middleMouseWasDown = IsKeyDown(VirtualKeyMiddleMouse);
            var dialoguePhase = 0f;
            var dialogueBlend = 0f;
            var shakePhase = 0f;
            var shakeStrength = 0f;
            var previousOutputPosition = fixedCameraPosition;
            var lastCadenceValidationAt = Environment.TickCount64;
            if (attachToSubject) _service.StartActionCameraInput();
            while (!cancellation.IsCancellationRequested && !memory.Process.HasExited)
            {
                var now = System.Diagnostics.Stopwatch.GetTimestamp();
                var nowMilliseconds = Environment.TickCount64;
                var elapsedSeconds = Math.Clamp(
                    (float)System.Diagnostics.Stopwatch.GetElapsedTime(lastUpdate, now).TotalSeconds,
                    0.001f,
                    0.05f);
                lastUpdate = now;
                if (nowMilliseconds - lastCadenceValidationAt >= 250)
                {
                    if (memory.ReadUInt64(targetAddress + 0x08) != targetIdentity) break;
                    if (isPlayerTarget &&
                        memory.ReadInt32(targetAddress + ChrInsOmissionModeOverrideOffset) == 0)
                    {
                        memory.WriteBytes(
                            targetAddress + ChrInsOmissionModeOverrideOffset,
                            BitConverter.GetBytes(-2));
                    }
                    else if (!isPlayerTarget &&
                        memory.ReadInt32(targetAddress + ChrInsOmissionModeOverrideOffset) != 0)
                    {
                        memory.WriteBytes(
                            targetAddress + ChrInsOmissionModeOverrideOffset,
                            BitConverter.GetBytes(0));
                    }
                    lastCadenceValidationAt = nowMilliseconds;
                }
                dialoguePhase += elapsedSeconds;
                var dialogueBlendTarget = attachToSubject && _dialogueMotionEnabled ? 1f : 0f;
                dialogueBlend += (dialogueBlendTarget - dialogueBlend) *
                                 (1f - MathF.Exp(-1.8f * elapsedSeconds));
                var subject = lastValidSubject;
                var subjectReadSucceeded = false;
                try
                {
                    var candidate = ReadTrackedPosition() + Vector3.UnitY * SubjectAimHeight;
                    if (Vector3.Distance(candidate, lastValidSubject) <= MaximumSubjectFrameDisplacement)
                    {
                        var subjectRootHeight = candidate.Y - SubjectAimHeight;
                        var verticalSpeed = MathF.Abs(
                            (subjectRootHeight - previousSubjectRootHeight) /
                            MathF.Max(elapsedSeconds, 0.001f));
                        if (subjectRootHeight < estimatedGroundHeight)
                        {
                            // Descending terrain should lower the safety plane
                            // immediately so the camera does not float.
                            estimatedGroundHeight = subjectRootHeight;
                        }
                        else if (verticalSpeed <= GroundedVerticalSpeedThreshold)
                        {
                            // Only follow rising terrain when the subject is moving
                            // vertically slowly; jumps must not lift the floor.
                            var groundBlend =
                                1f - MathF.Exp(-GroundEstimateResponse * elapsedSeconds);
                            estimatedGroundHeight +=
                                (subjectRootHeight - estimatedGroundHeight) * groundBlend;
                        }
                        previousSubjectRootHeight = subjectRootHeight;
                        subject = candidate;
                        lastValidSubject = candidate;
                        lastValidSubjectAt = nowMilliseconds;
                        subjectReadSucceeded = true;
                    }
                }
                catch
                {
                    // Character controllers can disappear for a few frames during
                    // streaming, landing transitions, and large scripted jumps.
                }

                if (!subjectReadSucceeded && nowMilliseconds - lastValidSubjectAt > SubjectReadGraceMilliseconds)
                    throw new InvalidOperationException("SUBJECT LIVE POSITION WAS LOST");

                // Exponential smoothing filters animation bob without depending on
                // loop frequency. Increase response for large moves so dodges and
                // charges remain easy to follow.
                if (subjectReadSucceeded)
                {
                    var trackingError = Vector3.Distance(smoothedSubject, subject);
                    // Body Cam should feel physically mounted instead of being
                    // pulled behind its wearer. Keep a small damping window to
                    // suppress animation noise, but avoid stacking the much
                    // softer Action Cam subject lag with camera-position lag.
                    var followTime = _subjectBodyOrientationEnabled
                        ? Math.Clamp(0.055f - trackingError * 0.004f, 0.028f, 0.055f)
                        : attachToSubject
                            ? Math.Clamp(0.22f - trackingError * 0.012f, 0.085f, 0.22f)
                            : Math.Clamp(0.14f - trackingError * 0.008f, 0.065f, 0.14f);
                    smoothedSubject = SmoothDamp(
                        smoothedSubject,
                        subject,
                        ref subjectFollowVelocity,
                        followTime,
                        elapsedSeconds);
                    var aimError = Vector3.Distance(smoothedAimSubject, subject);
                    var aimFollowTime = Math.Clamp(0.075f - aimError * 0.009f, 0.022f, 0.075f);
                    smoothedAimSubject = SmoothDamp(
                        smoothedAimSubject,
                        subject,
                        ref aimFollowVelocity,
                        aimFollowTime,
                        elapsedSeconds);
                }
                else
                {
                    var velocityDecay = MathF.Exp(-8f * elapsedSeconds);
                    subjectFollowVelocity *= velocityDecay;
                    aimFollowVelocity *= velocityDecay;
                }

                Vector3 cameraPosition;
                var orbitInputActive = false;
                var referenceUp = Vector3.UnitY;
                if (attachToSubject)
                {
                    var input = _service.TakeActionCameraInput();
                    var middleMouseDown = IsKeyDown(VirtualKeyMiddleMouse);
                    if (middleMouseDown && !middleMouseWasDown)
                        mouseCameraInputLocked = !mouseCameraInputLocked;
                    middleMouseWasDown = middleMouseDown;
                    var mouseOwnsCamera = _actionMouseControlsCamera;
                    var mouseX = mouseCameraInputLocked || !mouseOwnsCamera ? 0 : input.X;
                    var mouseY = mouseCameraInputLocked || !mouseOwnsCamera ? 0 : input.Y;
                    var mouseWheel = mouseCameraInputLocked || !mouseOwnsCamera ? 0f : input.Wheel;
                    orbitInputActive = mouseX != 0 || mouseY != 0;
                    orbitYaw += mouseX * 0.0024f;
                    if (_autoOrbitEnabled)
                        orbitYaw += elapsedSeconds * 0.28f * (float)AutoOrbitSpeedMultiplier;
                    orbitPitch = Math.Clamp(orbitPitch + mouseY * 0.0024f, -1.48f, 1.48f);
                    var keyboardOwnsCamera = _actionKeyboardControlsCamera;
                    var controlDown = keyboardOwnsCamera && IsKeyDown(VirtualKeyControl);
                    var shiftDown = keyboardOwnsCamera && IsKeyDown(VirtualKeyShift);
                    var independentYawGesture = controlDown && shiftDown;
                    var verticalInput = independentYawGesture
                        ? 0f
                        : (keyboardOwnsCamera && IsKeyDown(VirtualKeySpace) ? 1f : 0f) - (shiftDown ? 1f : 0f);
                    if (verticalInput != 0)
                    {
                        var verticalSpeed = Math.Clamp(orbitRadius * 0.65f, 1.5f, 12f);
                        verticalCameraOffset = Math.Clamp(
                            verticalCameraOffset + verticalInput * verticalSpeed * elapsedSeconds,
                            -100f,
                            100f);
                    }
                    if (Math.Abs(mouseWheel) > 0.001f)
                    {
                        if (independentYawGesture)
                            aimYaw += mouseWheel * 0.055f;
                        else if (controlDown)
                            aimPitch = Math.Clamp(aimPitch - mouseWheel * 0.045f, -1.25f, 1.25f);
                        else if (IsKeyDown(VirtualKeyAlt))
                            liveFov = Math.Clamp(liveFov - mouseWheel * 0.035f, 0.05f, 2.7f);
                        else
                            orbitRadius = Math.Clamp(orbitRadius * MathF.Exp(-mouseWheel * 0.12f), 0.3f, 80f);
                    }

                    var bodyOrientationRequested = _subjectBodyOrientationEnabled;
                    if (bodyOrientationRequested &&
                        TryReadBodyCameraBasis(memory, targetAddress, out var liveSubjectBasis))
                    {
                        smoothedBodyBasis = SmoothSubjectBasis(
                            smoothedBodyBasis,
                            liveSubjectBasis,
                            elapsedSeconds);
                    }

                    if (bodyOrientationRequested &&
                        !bodyOrientationActive &&
                        smoothedBodyBasis is SubjectBasis enteringBasis)
                    {
                        // Treat the subject heading at activation as zero. Body
                        // Cam must preserve the exact current camera position and
                        // viewing direction, then inherit only subsequent subject
                        // heading changes. Adopting the actor's absolute basis here
                        // caused the activation 180 and inverted-feeling controls.
                        var currentWorldOffset =
                            smoothedCameraPosition -
                            smoothedSubject -
                            Vector3.UnitY * verticalCameraOffset;
                        SetOrbitFromOffset(
                            currentWorldOffset,
                            ref orbitRadius,
                            ref orbitYaw,
                            ref orbitPitch);
                        enteringBodyBasis = enteringBasis;
                        enteringCameraBasis = new SubjectBasis(
                            Right(camera),
                            Up(camera),
                            Forward(camera));
                        cameraFollowVelocity = Vector3.Zero;
                        bodyOrientationActive = true;
                    }
                    else if (!bodyOrientationRequested && bodyOrientationActive)
                    {
                        // Preserve the exact current world position when leaving
                        // BODY ROLL instead of reinterpreting local orbit angles.
                        var currentWorldOffset =
                            smoothedCameraPosition -
                            smoothedSubject -
                            Vector3.UnitY * verticalCameraOffset;
                        SetOrbitFromOffset(
                            currentWorldOffset,
                            ref orbitRadius,
                            ref orbitYaw,
                            ref orbitPitch);
                        cameraFollowVelocity = Vector3.Zero;
                        bodyOrientationActive = false;
                        smoothedBodyBasis = null;
                        enteringBodyBasis = null;
                    }

                    // Slow, layered movement avoids an obvious repeating circle.
                    // These offsets never mutate the operator's stored orbit, so
                    // disabling the mode eases precisely back to manual framing.
                    var dialogueYaw = dialogueBlend *
                        (MathF.Sin(dialoguePhase * 0.31f) * 0.085f +
                         MathF.Sin(dialoguePhase * 0.13f + 1.7f) * 0.035f);
                    var dialoguePitch = dialogueBlend * MathF.Sin(dialoguePhase * 0.23f + 0.8f) * 0.022f;
                    var dialogueRadiusScale = 1f + dialogueBlend *
                        (MathF.Sin(dialoguePhase * 0.19f + 2.2f) * 0.045f +
                         MathF.Sin(dialoguePhase * 0.07f) * 0.018f);
                    var dialogueHeight = dialogueBlend * orbitRadius *
                        MathF.Sin(dialoguePhase * 0.17f + 0.4f) * 0.018f;
                    var renderedYaw = orbitYaw + dialogueYaw;
                    var renderedPitch = Math.Clamp(orbitPitch + dialoguePitch, -1.48f, 1.48f);
                    var renderedRadius = Math.Clamp(orbitRadius * dialogueRadiusScale, 0.3f, 80f);
                    var cosPitch = MathF.Cos(renderedPitch);
                    var localOffset = new Vector3(
                        MathF.Sin(renderedYaw) * cosPitch,
                        MathF.Sin(renderedPitch),
                        MathF.Cos(renderedYaw) * cosPitch) * renderedRadius;
                    if (bodyOrientationActive && smoothedBodyBasis is SubjectBasis subjectBasis)
                    {
                        var headingDelta = BodyHeadingDelta(enteringBodyBasis, subjectBasis);
                        var headingRotation = Quaternion.CreateFromAxisAngle(Vector3.UnitY, headingDelta);
                        // localOffset is intentionally expressed in EldenIntel's
                        // normal world-camera convention. Rotate it only by the
                        // subject's change in heading so mouse orbit remains native.
                        subjectOffset = Vector3.Transform(localOffset, headingRotation);
                        // Preserve the body's dramatic lean without making the
                        // horizon feel mechanically welded to every animation.
                        referenceUp = SafeNormalize(
                            Vector3.Lerp(Vector3.UnitY, subjectBasis.Up, BodyHorizonInfluence),
                            subjectBasis.Up);
                    }
                    else
                    {
                        subjectOffset = localOffset;
                    }
                    var desiredCameraPosition =
                        smoothedSubject +
                        subjectOffset +
                        Vector3.UnitY * (verticalCameraOffset + dialogueHeight);
                    desiredCameraPosition.Y = MathF.Max(
                        desiredCameraPosition.Y,
                        estimatedGroundHeight + CameraFloorClearance);
                    var cameraSmoothTime = bodyOrientationActive
                        ? (orbitInputActive ? 0.028f : 0.040f)
                        : (orbitInputActive ? 0.055f : 0.11f);
                    smoothedCameraPosition = SmoothDamp(
                        smoothedCameraPosition,
                        desiredCameraPosition,
                        ref cameraFollowVelocity,
                        cameraSmoothTime,
                        elapsedSeconds);
                    smoothedCameraPosition.Y = MathF.Max(
                        smoothedCameraPosition.Y,
                        estimatedGroundHeight + CameraFloorClearance);
                    if (smoothedCameraPosition.Y <=
                        estimatedGroundHeight + CameraFloorClearance + 0.0001f)
                    {
                        cameraFollowVelocity.Y = MathF.Max(0f, cameraFollowVelocity.Y);
                    }
                    cameraPosition = smoothedCameraPosition;
                }
                else cameraPosition = fixedCameraPosition;
                // A body camera faces outward with the subject. Other subject
                // modes retain the familiar look-at framing.
                Vector3 forward;
                Vector3 right;
                Vector3 up;
                if (bodyOrientationActive && smoothedBodyBasis is SubjectBasis mountedBasis)
                {
                    var headingDelta = BodyHeadingDelta(enteringBodyBasis, mountedBasis);
                    var headingRotation = Quaternion.CreateFromAxisAngle(Vector3.UnitY, headingDelta);
                    forward = SafeNormalize(
                        Vector3.Transform(enteringCameraBasis.Forward, headingRotation),
                        enteringCameraBasis.Forward);
                    right = SafeNormalize(
                        Vector3.Transform(enteringCameraBasis.Right, headingRotation),
                        enteringCameraBasis.Right);
                    up = SafeNormalize(Vector3.Cross(forward, right), enteringCameraBasis.Up);
                }
                else
                {
                    forward = SafeNormalize(smoothedAimSubject - cameraPosition, Forward(camera));
                    right = SafeNormalize(Vector3.Cross(referenceUp, forward), Right(camera));
                    up = SafeNormalize(Vector3.Cross(forward, right), Vector3.UnitY);
                }
                if (attachToSubject && MathF.Abs(aimYaw) > 0.0001f)
                {
                    // Pan independently of the orbit while preserving the camera
                    // position, subject attachment, tracking, and FOV.
                    var yawRotation = Quaternion.CreateFromAxisAngle(referenceUp, aimYaw);
                    forward = SafeNormalize(Vector3.Transform(forward, yawRotation), forward);
                    right = SafeNormalize(Vector3.Cross(referenceUp, forward), right);
                    up = SafeNormalize(Vector3.Cross(forward, right), up);
                }
                if (attachToSubject && MathF.Abs(aimPitch) > 0.0001f)
                {
                    // Offset where the camera aims without changing its orbit,
                    // distance, subject tracking, or FOV.
                    var pitchRotation = Quaternion.CreateFromAxisAngle(right, aimPitch);
                    forward = SafeNormalize(Vector3.Transform(forward, pitchRotation), forward);
                    up = SafeNormalize(Vector3.Cross(forward, right), up);
                }
                if (attachToSubject)
                {
                    var cameraTravelSpeed = Vector3.Distance(cameraPosition, previousOutputPosition) /
                                            MathF.Max(elapsedSeconds, 0.001f);
                    previousOutputPosition = cameraPosition;
                    var normalizedSpeed = Math.Clamp((cameraTravelSpeed - 0.08f) / 7.0f, 0f, 1f);
                    normalizedSpeed = normalizedSpeed * normalizedSpeed * (3f - 2f * normalizedSpeed);
                    // Following the local player naturally moves the camera as
                    // the player runs. Do not reinterpret that follow velocity
                    // as handheld operator motion; it feels like another system
                    // is tugging or resetting the camera. Manual/orbit movement
                    // and non-player action subjects retain movement shake.
                    var shakeTarget = _movementShakeEnabled && !isPlayerTarget
                        ? normalizedSpeed
                        : 0f;
                    var shakeResponse = shakeTarget > shakeStrength ? 5.0f : 2.4f;
                    shakeStrength += (shakeTarget - shakeStrength) *
                                     (1f - MathF.Exp(-shakeResponse * elapsedSeconds));
                    shakePhase += elapsedSeconds * (7.5f + shakeStrength * 4.5f);

                    if (shakeStrength > 0.0001f)
                    {
                        // Small rotational noise reads as handheld camera inertia
                        // without displacing the camera or endangering floor clearance.
                        var amplitude = shakeStrength * 0.0065f;
                        var shakeYaw = (MathF.Sin(shakePhase * 1.07f) +
                                        MathF.Sin(shakePhase * 2.31f + 0.8f) * 0.35f) * amplitude;
                        var shakePitch = (MathF.Sin(shakePhase * 1.43f + 1.9f) +
                                          MathF.Sin(shakePhase * 2.73f) * 0.28f) * amplitude * 0.72f;
                        var shakeRoll = MathF.Sin(shakePhase * 0.91f + 2.4f) * amplitude * 0.38f;
                        forward = SafeNormalize(
                            Vector3.Transform(forward, Quaternion.CreateFromAxisAngle(up, shakeYaw)),
                            forward);
                        right = SafeNormalize(Vector3.Cross(up, forward), right);
                        forward = SafeNormalize(
                            Vector3.Transform(forward, Quaternion.CreateFromAxisAngle(right, shakePitch)),
                            forward);
                        up = SafeNormalize(Vector3.Cross(forward, right), up);
                        var rollRotation = Quaternion.CreateFromAxisAngle(forward, shakeRoll);
                        right = SafeNormalize(Vector3.Transform(right, rollRotation), right);
                        up = SafeNormalize(Vector3.Cross(forward, right), up);
                    }
                }
                if ((CameraTransformOwner)Volatile.Read(ref _cameraTransformOwner) == CameraTransformOwner.SubjectCamera)
                {
                    _service.WriteSynchronizedActionCamera(
                        right,
                        up,
                        forward,
                        cameraPosition,
                        liveFov);
                }
                PaceLiveCamera(ref nextCameraUpdate);
            }
        }
        catch { }
        finally
        {
            try { cancellation.Cancel(); } catch { }
            if (originalOmissionOverride is int restoreOverride)
                TryRestoreSubjectOmissionOverride(targetAddress, targetIdentity, restoreOverride);
            var targetSyncThread = _playerTargetSyncThread;
            if (targetSyncThread is not null && targetSyncThread != Thread.CurrentThread)
                targetSyncThread.Join(750);
            if (attachToSubject) _service.StopActionCameraInput();
            if (restorePlayerInputBlock) _service.EnsurePlayerInputBlocked();
            else _service.EnsurePlayerInputEnabled();
            _service.EndSynchronizedActionCamera();
            lock (_subjectLockGate)
            {
                if (ReferenceEquals(_subjectLockCancellation, cancellation)) _subjectLockCancellation = null;
                _subjectLockAttached = false;
                _subjectBodyOrientationEnabled = false;
                _subjectLockThread = null;
                _playerTargetSyncThread = null;
                Interlocked.CompareExchange(
                    ref _cameraTransformOwner,
                    (int)CameraTransformOwner.InteractiveFreecam,
                    (int)CameraTransformOwner.SubjectCamera);
                Interlocked.CompareExchange(ref _activeSubjectAddress, 0, unchecked((long)targetAddress));
            }
            cancellation.Dispose();
        }
    }

    private static void PaceLiveCamera(ref long nextUpdate)
    {
        var interval = Math.Max(1L, Stopwatch.Frequency / LiveCameraUpdatesPerSecond);
        nextUpdate += interval;
        var now = Stopwatch.GetTimestamp();

        // If a game-memory read or a scheduling interruption ran long, resume
        // from the current frame instead of trying to replay missed updates.
        if (now >= nextUpdate)
        {
            nextUpdate = now;
            return;
        }

        while (now < nextUpdate)
        {
            var remaining = nextUpdate - now;
            if (remaining > Stopwatch.Frequency / 500)
                Thread.Sleep(1);
            else
                Thread.SpinWait(32);
            now = Stopwatch.GetTimestamp();
        }
    }

    private static void TryRestoreSubjectOmissionOverride(
        ulong targetAddress,
        ulong targetIdentity,
        int originalOverride)
    {
        try
        {
            using var memory = new global::EnemyIntelReader.MemoryReader(ProcessName);
            if (!memory.IsLikelyPointer(targetAddress) ||
                memory.ReadUInt64(targetAddress + 0x08) != targetIdentity)
                return;
            var overrideAddress = targetAddress + ChrInsOmissionModeOverrideOffset;
            // Restore only while this subject-camera worker still owns the
            // forced value. Never overwrite a newer system's deliberate state.
            if (memory.ReadInt32(overrideAddress) == 0)
                memory.WriteBytes(overrideAddress, BitConverter.GetBytes(originalOverride));
        }
        catch { }
    }

    private static void PlayerTargetSyncLoop(ulong targetAddress, CancellationToken cancellationToken)
    {
        ulong playerAddress = 0;
        ulong originalHandle = 0;
        ulong desiredHandle = 0;
        try
        {
            using var memory = new global::EnemyIntelReader.MemoryReader(ProcessName);
            playerAddress = ResolveLocalPlayer(memory);
            if (!memory.IsLikelyPointer(playerAddress) || !memory.IsLikelyPointer(targetAddress)) return;
            desiredHandle = memory.ReadUInt64(targetAddress + 0x08);
            if (desiredHandle == 0 || desiredHandle == ulong.MaxValue) return;
            var playerTargetAddress = playerAddress + 0x6B0;
            originalHandle = memory.ReadUInt64(playerTargetAddress);

            while (!cancellationToken.IsCancellationRequested && !memory.Process.HasExited)
            {
                if (!memory.IsLikelyPointer(targetAddress)) break;
                var liveHandle = memory.ReadUInt64(targetAddress + 0x08);
                if (liveHandle == 0 || liveHandle == ulong.MaxValue) break;
                desiredHandle = liveHandle;
                if (memory.ReadUInt64(playerTargetAddress) != desiredHandle)
                    memory.WriteBytes(playerTargetAddress, BitConverter.GetBytes(desiredHandle));
                cancellationToken.WaitHandle.WaitOne(33);
            }

            // Restore only if this synchronizer still owns the value. Never
            // overwrite a target the player deliberately selected afterward.
            if (!memory.Process.HasExited && memory.ReadUInt64(playerTargetAddress) == desiredHandle)
                memory.WriteBytes(playerTargetAddress, BitConverter.GetBytes(originalHandle));
        }
        catch { }
    }

    private static Vector3 ReadSubjectPosition(global::EnemyIntelReader.MemoryReader memory, ulong targetAddress)
    {
        var chrCtrl = memory.ReadUInt64(targetAddress + ChrInsChrCtrlOffset);
        if (chrCtrl < 0x10000)
            throw new InvalidOperationException("TARGET CONTROLLER IS UNAVAILABLE");

        // ChrIns + 0x80 is only the character's map-chunk placement. The live
        // locomotion position is the translation row of ChrCtrl's model matrix.
        if (TryReadMatrixPosition(memory, chrCtrl + ChrCtrlModelMatrixOffset, out var position) ||
            TryReadMatrixPosition(memory, chrCtrl + ChrCtrlPhysicsModelMatrixOffset, out position))
            return position;

        throw new InvalidOperationException("TARGET LIVE POSITION IS INVALID");
    }

    private static Vector3 ReadPlayerPhysicsPosition(
        global::EnemyIntelReader.MemoryReader memory,
        ulong playerAddress)
    {
        var modules = memory.ReadUInt64(playerAddress + ChrInsModulesOffset);
        if (!memory.IsLikelyPointer(modules))
            throw new InvalidOperationException("PLAYER MODULES ARE UNAVAILABLE");
        var physics = memory.ReadUInt64(modules + ChrModulesPhysicsOffset);
        if (!memory.IsLikelyPointer(physics))
            throw new InvalidOperationException("PLAYER PHYSICS IS UNAVAILABLE");
        var bytes = memory.ReadBytes(physics + ChrPhysicsLocalPositionOffset, 12);
        var position = new Vector3(ReadFloat(bytes, 0), ReadFloat(bytes, 4), ReadFloat(bytes, 8));
        if (!float.IsFinite(position.X) || !float.IsFinite(position.Y) ||
            !float.IsFinite(position.Z) || position.LengthSquared() >= 1e12f)
            throw new InvalidOperationException("PLAYER PHYSICS POSITION IS INVALID");
        return position;
    }

    private static bool TryReadSubjectBasis(
        global::EnemyIntelReader.MemoryReader memory,
        ulong targetAddress,
        out SubjectBasis basis)
    {
        basis = default;
        try
        {
            var chrCtrl = memory.ReadUInt64(targetAddress + ChrInsChrCtrlOffset);
            if (!memory.IsLikelyPointer(chrCtrl)) return false;
            var bytes = memory.ReadBytes(chrCtrl + ChrCtrlModelMatrixOffset, 0x30);
            var right = new Vector3(ReadFloat(bytes, 0), ReadFloat(bytes, 4), ReadFloat(bytes, 8));
            var up = new Vector3(ReadFloat(bytes, 0x10), ReadFloat(bytes, 0x14), ReadFloat(bytes, 0x18));
            var forward = new Vector3(ReadFloat(bytes, 0x20), ReadFloat(bytes, 0x24), ReadFloat(bytes, 0x28));
            if (right.LengthSquared() < 0.1f || up.LengthSquared() < 0.1f || forward.LengthSquared() < 0.1f) return false;
            basis = new SubjectBasis(Vector3.Normalize(right), Vector3.Normalize(up), Vector3.Normalize(forward));
            return true;
        }
        catch { return false; }
    }

    private static bool TryReadBodyCameraBasis(
        global::EnemyIntelReader.MemoryReader memory,
        ulong targetAddress,
        out SubjectBasis basis)
    {
        basis = default;
        if (!TryReadSubjectBasis(memory, targetAddress, out var modelBasis))
            return false;

        // Elden Ring's model-matrix forward row points back through the
        // character.  Spawn placement already compensates for this convention;
        // Body Cam must do the same or it looks back at its wearer.
        var facing = SafeNormalize(
            new Vector3(-modelBasis.Forward.X, 0f, -modelBasis.Forward.Z),
            Vector3.UnitZ);

        // Keep a cinematic horizon while retaining a small amount of the
        // actor's physical lean.  Re-orthogonalizing also filters scale/shear
        // noise that appears in animated model matrices.
        var upHint = SafeNormalize(
            Vector3.Lerp(Vector3.UnitY, modelBasis.Up, BodyHorizonInfluence),
            Vector3.UnitY);
        var right = SafeNormalize(Vector3.Cross(upHint, facing), Vector3.UnitX);
        var up = SafeNormalize(Vector3.Cross(facing, right), Vector3.UnitY);
        basis = new SubjectBasis(right, up, facing);
        return true;
    }

    private static SubjectBasis SmoothSubjectBasis(SubjectBasis? current, SubjectBasis target, float elapsedSeconds)
    {
        if (current is null) return target;
        var blend = 1f - MathF.Exp(-BodyOrientationResponse * elapsedSeconds);
        var forward = SafeNormalize(Vector3.Lerp(current.Value.Forward, target.Forward, blend), target.Forward);
        var upHint = SafeNormalize(Vector3.Lerp(current.Value.Up, target.Up, blend), target.Up);
        var right = SafeNormalize(Vector3.Cross(upHint, forward), target.Right);
        var up = SafeNormalize(Vector3.Cross(forward, right), target.Up);
        return new SubjectBasis(right, up, forward);
    }

    private static float BodyHeadingDelta(SubjectBasis? entering, SubjectBasis current)
    {
        if (entering is not SubjectBasis origin) return 0f;
        var originYaw = MathF.Atan2(origin.Forward.X, origin.Forward.Z);
        var currentYaw = MathF.Atan2(current.Forward.X, current.Forward.Z);
        var delta = currentYaw - originYaw;
        while (delta > MathF.PI) delta -= MathF.Tau;
        while (delta < -MathF.PI) delta += MathF.Tau;
        return delta;
    }

    private static void SetOrbitFromOffset(
        Vector3 offset,
        ref float radius,
        ref float yaw,
        ref float pitch)
    {
        radius = MathF.Max(offset.Length(), 0.35f);
        yaw = MathF.Atan2(offset.X, offset.Z);
        pitch = MathF.Asin(Math.Clamp(offset.Y / radius, -1f, 1f));
    }

    private static Vector3 SmoothDamp(
        Vector3 current,
        Vector3 target,
        ref Vector3 velocity,
        float smoothTime,
        float elapsedSeconds)
    {
        smoothTime = MathF.Max(0.025f, smoothTime);
        var omega = 2f / smoothTime;
        var x = omega * elapsedSeconds;
        var decay = 1f / (1f + x + 0.48f * x * x + 0.235f * x * x * x);
        var displacement = current - target;
        var temporary = (velocity + omega * displacement) * elapsedSeconds;
        velocity = (velocity - omega * temporary) * decay;
        var output = target + (displacement + temporary) * decay;

        // Avoid an overshoot when a target stops abruptly after a large move.
        if (Vector3.Dot(target - current, output - target) > 0f)
        {
            output = target;
            velocity = Vector3.Zero;
        }

        return output;
    }

    private static bool TryReadMatrixPosition(
        global::EnemyIntelReader.MemoryReader memory,
        ulong matrixAddress,
        out Vector3 position)
    {
        var bytes = memory.ReadBytes(matrixAddress + ModelMatrixTranslationOffset, 12);
        position = new Vector3(ReadFloat(bytes, 0), ReadFloat(bytes, 4), ReadFloat(bytes, 8));
        return float.IsFinite(position.X) &&
               float.IsFinite(position.Y) &&
               float.IsFinite(position.Z) &&
               position.LengthSquared() < 1e12f;
    }

    private static bool IsKeyDown(int virtualKey) => (GetAsyncKeyState(virtualKey) & 0x8000) != 0;

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int virtualKey);

    private readonly record struct SubjectBasis(Vector3 Right, Vector3 Up, Vector3 Forward);

    private string ToggleFreecamWithRealPause()
    {
        var freecamActuallyEnabled = IsDebugCameraActive();
        if (!freecamActuallyEnabled)
        {
            var status = _service.ToggleFreecam();
            _freecamAppliedRealPause = false;
            return IsFailure(status) ? status : status + " — LIVE ACTION";
        }

        StopSubjectCamera();

        // Restore the cached player transform while the debug camera still owns
        // the view. Only then return rendering to the normal player camera.
        // The cached physics position is restored while the engine is paused.
        // Give Elden Ring one update tick to reconcile ChrCtrl/model transforms
        // and its normal player camera before releasing the debug camera.
        var pausedBeforeReconcile = IsRealPaused();
        var preserveManualPause = pausedBeforeReconcile && !_freecamAppliedRealPause;
        if (pausedBeforeReconcile)
        {
            SetRealPaused(false);
            Thread.Sleep(20);
            if (preserveManualPause) SetRealPaused(true);
        }

        var disableStatus = _service.ToggleFreecam();
        if (IsFailure(disableStatus)) return disableStatus;
        if (!WaitForDebugCameraMode(0, 500))
        {
            // A stale native/action-camera writer can reclaim mode 3 after the
            // normal F1 toggle. Unload that writer, then explicitly return the
            // renderer to the player camera.
            var suspendStatus = _service.SuspendFreecamForTrack();
            if (IsFailure(suspendStatus))
                return $"FREECAM FORCE RELEASE FAILED: {suspendStatus}";
            ForceDebugCameraMode(0);
            _service.StopActionCameraInput();
            _service.EndSynchronizedActionCamera();
            disableStatus = "FREECAM FORCE RELEASED";
        }
        if (_freecamAppliedRealPause && IsRealPaused()) TrySetRealPaused(false);
        _freecamAppliedRealPause = false;
        return disableStatus + " — " + (IsRealPaused() ? "REAL PAUSE LEFT ON" : "REAL PAUSE OFF");
    }

    private string ToggleRealPause()
    {
        var enable = !IsRealPaused();
        SetRealPaused(enable);
        // A manual pause decision always supersedes freecam's temporary ownership.
        _freecamAppliedRealPause = false;
        return enable ? "REAL PAUSE ON" : "REAL PAUSE OFF";
    }

    private static string StepRealPausedFrame()
    {
        using var memory = new global::EnemyIntelReader.MemoryReader(ProcessName);
        var address = ResolveRealPauseAddress(memory);
        if (!memory.ReadBytes(address, 3).SequenceEqual(RealPauseOnBytes)) return "STEP REQUIRES REAL PAUSE";

        memory.WriteExecutableBytes(address, RealPauseOffBytes);
        try
        {
            var timer = System.Diagnostics.Stopwatch.StartNew();
            while (timer.Elapsed.TotalMilliseconds < 15.5)
            {
                if (timer.Elapsed.TotalMilliseconds < 14) Thread.Sleep(1);
                else Thread.SpinWait(64);
            }
        }
        finally
        {
            memory.WriteExecutableBytes(address, RealPauseOnBytes);
        }
        return "ONE FRAME ADVANCED — REAL PAUSE ON";
    }

    private static bool IsRealPaused()
    {
        using var memory = new global::EnemyIntelReader.MemoryReader(ProcessName);
        var address = ResolveRealPauseAddress(memory);
        return memory.ReadBytes(address, 3).SequenceEqual(RealPauseOnBytes);
    }

    private static void SetRealPaused(bool paused)
    {
        using var memory = new global::EnemyIntelReader.MemoryReader(ProcessName);
        var address = ResolveRealPauseAddress(memory);
        var desired = paused ? RealPauseOnBytes : RealPauseOffBytes;
        if (!memory.ReadBytes(address, 3).SequenceEqual(desired)) memory.WriteExecutableBytes(address, desired);
    }

    private static void TrySetRealPaused(bool paused) { try { SetRealPaused(paused); } catch { } }

    private static ulong ResolveRealPauseAddress(global::EnemyIntelReader.MemoryReader memory)
    {
        lock (RealPauseAddressGate)
        {
            if (_cachedRealPauseModuleBase == memory.ModuleBase && _cachedRealPauseAddress != 0)
            {
                try
                {
                    var current = memory.ReadBytes(_cachedRealPauseAddress, 3);
                    if (current.SequenceEqual(RealPauseOffBytes) || current.SequenceEqual(RealPauseOnBytes))
                        return _cachedRealPauseAddress;
                }
                catch { }
            }

            ulong address;
            try { address = memory.ScanModule(RealPauseOffPattern); }
            catch
            {
                try { address = memory.ScanModule(RealPauseOnPattern); }
                catch { throw new InvalidOperationException("REAL ENGINE PAUSE SIGNATURE NOT FOUND"); }
            }
            _cachedRealPauseModuleBase = memory.ModuleBase;
            _cachedRealPauseAddress = address;
            return address;
        }
    }

    private static void PrewarmRealPauseAddress()
    {
        try
        {
            using var memory = new global::EnemyIntelReader.MemoryReader(ProcessName);
            _ = ResolveRealPauseAddress(memory);
        }
        catch { }
    }

    private static bool IsFailure(string status) => status.Contains("FAILED", StringComparison.OrdinalIgnoreCase) || status.Contains("NOT RUNNING", StringComparison.OrdinalIgnoreCase) || status.Contains("MISSING", StringComparison.OrdinalIgnoreCase);

    private static ulong ResolveLocalPlayer(global::EnemyIntelReader.MemoryReader memory)
    {
        var world = ResolveWorldChrMan(memory);
        if (!memory.IsLikelyPointer(world)) throw new InvalidOperationException("WORLD CHARACTER MANAGER NOT READY");
        var players = memory.ReadUInt64(world + PlayersOffset);
        if (!memory.IsLikelyPointer(players)) throw new InvalidOperationException("PLAYER LIST NOT READY");
        var player = memory.ReadUInt64(players);
        if (!memory.IsLikelyPointer(player)) throw new InvalidOperationException("LOCAL PLAYER NOT READY");
        return player;
    }

    private static void DisableFreecamFreezeFlags()
    {
        try
        {
            var path = GetConfigPath();
            if (!File.Exists(path)) return;
            var lines = File.ReadAllLines(path);
            for (var index = 0; index < lines.Length; index++)
            {
                var key = lines[index].TrimStart();
                if (key.StartsWith("freeze_game", StringComparison.OrdinalIgnoreCase)) lines[index] = "freeze_game = false";
                else if (key.StartsWith("freeze_entities", StringComparison.OrdinalIgnoreCase)) lines[index] = "freeze_entities = false";
                else if (key.StartsWith("freeze_player", StringComparison.OrdinalIgnoreCase)) lines[index] = "freeze_player = false";
            }
            File.WriteAllLines(path, lines);
        }
        catch (UnauthorizedAccessException) { }
        catch (IOException) { }
    }

    private async Task<string> RunAsync(Func<string> command, bool requireVerifiedSession = true)
    {
        if (_disposed) return "CAMERA SERVICE CLOSED";
        if (requireVerifiedSession)
        {
            var antiCheat = AntiCheatVerifier.Check();
            if (antiCheat.State != AntiCheatState.Disabled)
                return $"{antiCheat.Label} — MEMORY TOOLS BLOCKED";
        }
        await _commandGate.WaitAsync().ConfigureAwait(false);
        try
        {
            return await Task.Run(command).ConfigureAwait(false);
        }
        finally
        {
            _commandGate.Release();
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        lock (_spawnScalingGate) _spawnScalingCancellation?.Cancel();
        _spawnScalingThread?.Join(1000);
        lock (_subjectLockGate) _subjectLockCancellation?.Cancel();
        _subjectLockThread?.Join(1000);
        if (_freecamAppliedRealPause) TrySetRealPaused(false);
        _service.Dispose();
        _commandGate.Dispose();
    }
}
