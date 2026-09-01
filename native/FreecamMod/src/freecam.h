#pragma once
#include <windows.h>
#include <unordered_map>
#include <vector>

#include "core/config/config.h"
#include "core/features/speedhack.h"
#include "core/features/possession_control.h"
#include "core/game_data/game_data.h"
#include "core/input/action_system.h"
#include "core/input/input.h"
#include "core/free_camera.h"
#include "core/hook/hook_manager.h"

class Freecam {
public:
    static inline Freecam* instance = nullptr;
    static inline volatile LONG shutdownRequested = 0;

    Freecam(HMODULE hModule);

    bool Initialize();
    void Run();
    void Dispose();
    static bool IsGameShuttingDown() {
        return InterlockedCompareExchange(&shutdownRequested, 0, 0) != 0;
    }

private:
#pragma pack(push, 1)
    struct ActionCameraSyncState {
        std::uint32_t magic;
        std::uint32_t version;
        volatile LONG enabled;
        volatile LONG sequence;
        float matrix[16];
        float fov;
        volatile LONG keyboardControlsCamera;
        volatile LONG mouseControlsCamera;
        volatile LONG controllerControlsPlayer;
        volatile LONG absolutePosition;
    };

    struct DayCycleControlState {
        std::uint32_t magic;
        std::uint32_t version;
        volatile LONG requestSequence;
        volatile LONG requestedEnabled;
        volatile LONG actualEnabled;
        volatile LONG cycleSpeed;
    };

    struct AnimationLabState {
        std::uint32_t magic;
        std::uint32_t version;
        volatile LONG requestSequence;
        volatile LONG gestureId;
        volatile LONG resultSequence;
        volatile LONG resultCode;
    };
    struct ActiveViewState { std::uint32_t magic; std::uint32_t version; volatile LONG requestedEnabled; volatile LONG actualEnabled; };
    struct EffectControlState {
        std::uint32_t magic;
        std::uint32_t version;
        volatile LONG requestSequence;
        volatile LONG effectId;
        volatile LONG resultSequence;
        volatile LONG resultCode;
        std::uint64_t targetAddress;
    };

    struct DropRateControlState {
        std::uint32_t magic;
        std::uint32_t version;
        volatile LONG requestSequence;
        volatile LONG command;
        volatile LONG resultSequence;
        volatile LONG resultCode;
        volatile LONG changedPoints;
        volatile LONG actualEnabled;
    };

    struct WorldBackReadBackup {
        uintptr_t address{};
        float nearPrimary{};
        float farPrimary{};
        float nearSecondary{};
        float farSecondary{};
    };

    struct AnimationResidencyBackup {
        uintptr_t identity{};
        int32_t originalOverride{};
    };

    struct DropRatePatch {
        std::uint8_t* address{};
        std::uint8_t original[16]{};
        std::uint8_t boosted[16]{};
        LONG changedPoints{};
    };
#pragma pack(pop)

    static constexpr std::uint32_t ActionCameraSyncMagic = 0x41434945; // "EICA"
    static constexpr std::uint32_t ActionCameraSyncVersion = 3;
    static constexpr wchar_t ActionCameraSyncName[] = L"Local\\EnemyIntelActionCameraSyncV1";
    static constexpr std::uint32_t DayCycleControlMagic = 0x43594144; // "DAYC"
    static constexpr std::uint32_t DayCycleControlVersion = 1;
    static constexpr wchar_t DayCycleControlName[] = L"Local\\EnemyIntelDayCycleControlV1";
    static constexpr std::uint32_t AnimationLabMagic = 0x42414C45; // "ELAB"
    static constexpr std::uint32_t AnimationLabVersion = 1;
    static constexpr wchar_t AnimationLabName[] = L"Local\\EldenIntelAnimationLabV1";
    static constexpr std::uint32_t ActiveViewMagic = 0x57495645;
    static constexpr std::uint32_t ActiveViewVersion = 1;
    static constexpr wchar_t ActiveViewName[] = L"Local\\EldenIntelActiveViewV1";
    static constexpr std::uint32_t EffectControlMagic = 0x58464645;
    static constexpr std::uint32_t EffectControlVersion = 2;
    static constexpr wchar_t EffectControlName[] = L"Local\\EldenIntelEffectControlV2";
    static constexpr std::uint32_t DropRateControlMagic = 0x50524445; // "EDRP"
    static constexpr std::uint32_t DropRateControlVersion = 1;
    static constexpr wchar_t DropRateControlName[] = L"Local\\EldenIntelDropRateControlV1";

    HMODULE hModule{};
    FreeCamera freeCamera{};
    Config config{};
    Input input{};
    ActionManager actionMgr{};
    HookManager hookManager{};
    Speedhack speedhack{};
    PossessionControl possessionControl{};
    HANDLE actionCameraMapping{};
    ActionCameraSyncState* actionCameraSync{};
    HANDLE dayCycleControlMapping{};
    DayCycleControlState* dayCycleControl{};
    HANDLE animationLabMapping{};
    AnimationLabState* animationLab{};
    LONG lastAnimationLabRequestSequence = 0;
HANDLE activeViewMapping{};
    ActiveViewState* activeView{};
    HANDLE effectControlMapping{};
    EffectControlState* effectControl{};
    LONG lastEffectRequestSequence = 0;
    HANDLE dropRateControlMapping{};
    DropRateControlState* dropRateControl{};
    LONG lastDropRateRequestSequence = 0;
    std::vector<DropRatePatch> dropRatePatches{};
    bool dropRateEnabled = false;
    void* pendingCloneCopyTarget = nullptr;
    uintptr_t pendingCloneCopyPlayerAddress = 0;
    void* pendingCloneCopySource = nullptr;
    int pendingCloneCopyAttempts = 0;
    int pendingCloneCopyFrameDelay = 0;
    LONG lastDayCycleRequestSequence = 0;
    bool actionCameraWasActive = false;
    bool actionCameraRebaseValid = false;
    float3 actionCameraLastSourcePosition{};
    float3 actionCameraNativePosition{};
    LONG actionCameraLastAppliedSequence = -1;
    bool externalCameraOwnedPreviousFrame = false;
    bool mouseCameraInputLocked = false;
    std::vector<WorldBackReadBackup> worldBackReadBackups{};
    float appliedResidencyRadius = 0.0f;
    bool externalAnimationDistanceDetected = false;
    bool externalAnimationDistanceLogged = false;
    ULONGLONG lastResidencyTelemetryAt = 0;
    ULONGLONG lastAnimationResidencyAt = 0;
    std::unordered_map<uintptr_t, AnimationResidencyBackup> animationResidencyBackups{};
    GameData::GameRend* currentGameRend = nullptr;
    void* activeViewFramePosition = nullptr;

    bool isRunning = true;

    void Update(GameData::GameRend* gameRend);
    void ProcessInput(GameData::GameRend* gameRend, float deltaTime);
    bool InitializeActionCameraSync();
    bool InitializeDayCycleControl();
    void ApplyDayCycleControl();
    void ApplyActionCameraSync(GameData::GameRend* gameRend);
    bool IsActionCameraSyncEnabled() const;
    bool ControllerControlsCamera() const;
    void ApplyAdaptiveWorldBackRead(GameData::GameRend* gameRend);
    void FindWorldBackReadInstances();
    void RestoreWorldBackRead();
    void ForceLoadedCharacterAnimationRate();
    void UpdateAnimationResidencyBubble();
    void RestoreAnimationResidencyBubble();
    void LogResidencyTelemetry(GameData::GameRend* gameRend);
    void DisposeActionCameraSync();
    void DisposeDayCycleControl();
    bool InitializeAnimationLab();
    void ApplyAnimationLab();
    void DisposeAnimationLab();
    bool InitializeActiveView();
    void PrepareActiveView(GameData::GameRend* gameRend);
    void ApplyActiveView(GameData::GameRend* gameRend);
    void DisposeActiveView();
bool InitializeEffectControl();
    void ApplyEffectControl();
    void ApplyPendingCloneCopy();
    void DisposeEffectControl();
    bool InitializeDropRateControl();
    void ApplyDropRateControl();
    LONG BuildDropRatePatchCache();
    void DisposeDropRateControl();

    bool IsPressed(ActionType actionType) const { return actionMgr.IsPressed(actionType, input); }
    bool IsJustPressed(ActionType actionType) const { return actionMgr.IsJustPressed(actionType, input); }

    float frameStepperTimePressed = 0.0f;

    using updateCameraMatrix_t = void(__fastcall*)(void*, void*, void*, void*);
    static inline updateCameraMatrix_t origUpdateCameraMatrix{};
    static void __fastcall hkUpdateCameraMatrix(GameData::GameRend* gameRend, void* rdx, void* r8, void* r9);
    static void OnGameShutdown();
};
