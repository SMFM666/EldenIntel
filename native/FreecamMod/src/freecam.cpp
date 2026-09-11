#include "freecam.h"

#include <algorithm>
#include <functional>
#include <cstdint>
#include <unordered_set>
#include <vector>
#include <cstring>
#include <cmath>

#include "ModUtils.h"

#include "core/features/path_recorder.h"
#include "core/game_data_manager.h"
#include "core/settings_backup.h"
#include "core/hook/code_cave.h"
#include "utils/time.h"
#include "utils/memory.h"
#include "utils/types.h"
#include "utils/debug.h"

Freecam::Freecam(HMODULE hModule) : hModule(hModule) {
    instance = this;
    InterlockedExchange(&shutdownRequested, 0);
    pendingCloneCopyTarget = nullptr;
    pendingCloneCopyPlayerAddress = 0;
    pendingCloneCopySource = nullptr;
    pendingCloneCopyAttempts = 0;
    pendingCloneCopyFrameDelay = 0;
}

bool Freecam::Initialize() {
    if (!config.Initialize(hModule, actionMgr)) return false;
    config.Reload();

    ModUtils::AttemptToGetWindowHandle();
    if (!ModUtils::muWindow) return false;

    if (!GameDataManager::Init()) return false;
    if (!input.HookWndProc(ModUtils::muWindow)) return false;
    input.SetShutdownCallback(&Freecam::OnGameShutdown);

    if (!hookManager.Initialize()) return false;
    if (!hookManager.Hook(&GameDataManager::UpdateCameraMatrixFunc, &hkUpdateCameraMatrix, (void**)&origUpdateCameraMatrix))
        return false;
    if (!hookManager.Hook(&GetRawInputData, &Input::hkGetRawInputData, (void**)&Input::origGetRawInputData))
        return false;

    speedhack.Initialize(hookManager);

    if (!hookManager.EnableAll()) return false;

    if (!hookManager.GetDaytimeUpdateCave().Hook(GameDataManager::DaytimeUpdateFunc.address)) return false;
    if (GameDataManager::FollowCameraRead.address &&
        !hookManager.GetFollowCameraCave().Hook(GameDataManager::FollowCameraRead.address)) {
        LOG_WARN("Follow camera capture unavailable; Active View Assist will remain inactive");
    }
    if (GameDataManager::CameraPositionWrite.address &&
        !hookManager.GetCameraPositionCave().Hook(GameDataManager::CameraPositionWrite.address)) {
        LOG_WARN("Camera position capture unavailable; Active View Assist will remain inactive");
    }

    SettingsBackup::SetFolderPath(config.GetConfigDirPath());

    freeCamera.Initialize();
    InitializeActionCameraSync();
    InitializeDayCycleControl();
    InitializeAnimationLab();
    InitializeActiveView();
    InitializeEffectControl();
    InitializeDropRateControl();
    possessionControl.Initialize();

    config.AddReloadCallback([this]() { freeCamera.OnConfigReload(); });

    return true;
}

void Freecam::Run() {
    if (!Initialize()) return;

    while (isRunning && InterlockedCompareExchange(&shutdownRequested, 0, 0) == 0) {
        // Backstop the WndProc notifications. Some exit paths tear down or
        // replace the game window before our subclass receives WM_CLOSE.
        // Stop touching game-owned state as soon as that window disappears.
        if (!ModUtils::muWindow || !IsWindow(ModUtils::muWindow)) {
            OnGameShutdown();
            break;
        }
        if (actionMgr.IsPressed(ActionType::ExitMod, input)) break;
        UpdateAnimationResidencyBubble();
        Sleep(10);
    }
}

void Freecam::ProcessInput(GameData::GameRend* gameRend, float deltaTime) {
    using enum ActionType;
    const float scrollDelta = input.GetScrollDelta();

    if (IsJustPressed(Toggle)) {
        config.Reload();
        freeCamera.Toggle(gameRend);
    }
    const bool actionCameraActive = IsActionCameraSyncEnabled();
    const bool hasInputPolicy = actionCameraSync &&
        actionCameraSync->magic == ActionCameraSyncMagic &&
        actionCameraSync->version == ActionCameraSyncVersion;
    const bool keyboardOwnsCamera = !hasInputPolicy ||
        InterlockedCompareExchange(&actionCameraSync->keyboardControlsCamera, 0, 0) != 0;
    const bool mouseOwnsCamera = !hasInputPolicy ||
        InterlockedCompareExchange(&actionCameraSync->mouseControlsCamera, 0, 0) != 0;
    input.SetSuppressFreecamKeyboard(freeCamera.IsEnabled() && keyboardOwnsCamera);
    // Freecam owns camera rotation. Capture raw mouse locally and prevent the
    // game's follow-camera pan from applying a second, conflicting rotation.
    input.SetSuppressFreecamMouse(
        freeCamera.IsEnabled() && mouseOwnsCamera && !mouseCameraInputLocked);
    if (freeCamera.IsEnabled()) {
        const bool controllerDrivesPlayer = actionCameraSync &&
            InterlockedCompareExchange(
                &actionCameraSync->controllerControlsPlayer, 0, 0) != 0;
        // EnableFreecam expects "disable player controls", while the shared
        // policy stores the positive "controller controls player" meaning.
        // Passing it through directly inverted ownership for part of every
        // frame and could leave movement suppressed even though face buttons
        // still reached the game.
        gameRend->EnableFreecam(!controllerDrivesPlayer);
    }

    // TeleportToCamera
    if (IsJustPressed(TeleportToCamera)) {
        config.Reload();
        freeCamera.Toggle(gameRend);

        if (!freeCamera.IsEnabled()) {
            if (GameData::ChrIns* player = GameDataManager::GetPlayer()) {
                if (GameData::Camera* cam = gameRend->csDebugCam) {
                    player->chrModules->chrPhysics->localPos = cam->matrix.position();
                }
            }
        }
    }

    if (IsJustPressed(CycleWeatherTime)) {
        hookManager.GetDaytimeUpdateCave().ToggleCycleWeatherTime();
    }

    // FrameStepper
    if (IsPressed(StepFrames) || input.IsGamepadPressed(XINPUT_GAMEPAD_DPAD_DOWN)) {
        constexpr float holdWaitTime = 1.0f;
        if (frameStepperTimePressed <= 0 || frameStepperTimePressed >= holdWaitTime) {
            freeCamera.StepFrames();
        }
        frameStepperTimePressed += deltaTime;
    }
    else {
        frameStepperTimePressed = 0.0f;
    }

    // Speedhack
    if (IsJustPressed(ToggleSpeedhack)) speedhack.IsEnabled() ? speedhack.Disable() : speedhack.Enable();
    if (IsPressed(ScrollSpeedhackModifier) && speedhack.IsEnabled()) speedhack.AddTimeScale(scrollDelta * 0.05f);
    if (IsJustPressed(ResetSpeedhackSpeed)) speedhack.SetTimeScale(1.0);

    // Free camera only
    if (!freeCamera.IsEnabled()) return;

    // The EldenIntel action/subject camera is the sole transform owner while
    // its synchronized state is active. Do not let native mouse, keyboard, or
    // controller inertia fight the externally supplied matrix between writes.
    if (IsActionCameraSyncEnabled()) {
        freeCamera.ClearManualInput();
        mouseCameraInputLocked = false;
        return;
    }

    // Middle mouse owns only the camera's mouse channel. Keyboard movement,
    // controller input, tracking, and every other camera rule remain live.
    if (input.IsJustPressed(VK_MBUTTON)) {
        mouseCameraInputLocked = !mouseCameraInputLocked;
    }

    const bool controllerControlsCamera = ControllerControlsCamera();
    freeCamera.SetMouseDelta(
        mouseCameraInputLocked || !mouseOwnsCamera ? int2{} : input.GetMouseDelta());
    freeCamera.SetGamepadDelta(
        controllerControlsCamera ? input.GetThumbRight() : float2{});

    freeCamera.SetIsSprinting(IsPressed(Sprint) ||
        (controllerControlsCamera && input.IsGamepadPressed(XINPUT_GAMEPAD_X)));
    if (IsJustPressed(ToggleFreeze)) freeCamera.ToggleFreeze();
    if (IsJustPressed(ResetSettings) ||
        (controllerControlsCamera && input.IsGamepadJustPressed(XINPUT_GAMEPAD_Y))) freeCamera.ResetCameraState(gameRend);
    if (IsJustPressed(ReloadConfig)) config.Reload();

    if (controllerControlsCamera && input.IsGamepadJustPressed(XINPUT_GAMEPAD_DPAD_UP)) {
        hookManager.GetDaytimeUpdateCave().ToggleCycleWeatherTime();
    }

    if (IsJustPressed(StartEndRecording) ||
        (controllerControlsCamera && input.IsGamepadJustPressed(XINPUT_GAMEPAD_DPAD_LEFT))) freeCamera.GetPathRecorder().Record();
    if (IsJustPressed(StartEndPlayingRecording) ||
        (controllerControlsCamera && input.IsGamepadJustPressed(XINPUT_GAMEPAD_DPAD_RIGHT))) freeCamera.GetPathRecorder().PlayRecord();

    const float2 thumbLeft = controllerControlsCamera ? input.GetThumbLeft() : float2{};
    const float moveForward = IsPressed(MoveForward) - IsPressed(MoveBackward) + thumbLeft.y;
    const float moveRight   = IsPressed(MoveRight)   - IsPressed(MoveLeft)     + thumbLeft.x;
    const float moveUp      = IsPressed(MoveUp)      - IsPressed(MoveDown) 
                            + (controllerControlsCamera && input.IsGamepadPressed(XINPUT_GAMEPAD_A))
                            - (controllerControlsCamera && input.IsGamepadPressed(XINPUT_GAMEPAD_B));
	freeCamera.AddVelocity(float3(moveRight, moveUp, moveForward));

    freeCamera.AddRollVelocity(IsPressed(TiltLeft) - IsPressed(TiltRight) 
        + (controllerControlsCamera && input.IsGamepadPressed(XINPUT_GAMEPAD_LEFT_SHOULDER))
        - (controllerControlsCamera && input.IsGamepadPressed(XINPUT_GAMEPAD_RIGHT_SHOULDER)));

    freeCamera.AddZoomVelocity(IsPressed(ZoomOut) - IsPressed(ZoomIn)
        + (controllerControlsCamera ? input.GetLeftTrigger() - input.GetRightTrigger() : 0.0f)
        - (!mouseCameraInputLocked && IsPressed(ScrollZoomModifier) ? scrollDelta : 0.0f));

    if (!mouseCameraInputLocked && IsPressed(ScrollCameraSpeedModifier)) freeCamera.AddSpeed(scrollDelta);

    // Number keys row
    if (input.IsPressed(VK_CONTROL)) {
        // Save states
        GameData::Camera* activeCamera = gameRend->GetActiveCamera();
        if (activeCamera) {
            for (int key = 0; key < 10; ++key) {
                int keyCode = key + (int)'0';

                if (input.IsJustPressed(keyCode)) {
                    freeCamera.GetCameraStateManager().SaveState(activeCamera, key, freeCamera.GetEuler());
                }
            }
        }
    }
    else {
        // Load states
        auto keysToProcess = input.GetReleasedNumkeys();
        if (!keysToProcess.empty()) {
            GameData::Camera* activeCamera = gameRend->GetActiveCamera();
            if (activeCamera) {
                freeCamera.GetCameraStateManager().StartLerpBetweenSlots(activeCamera, keysToProcess);
            }
        }
    }
}
    
void Freecam::Update(GameData::GameRend* gameRend) {
    // Skip all updates during loading - wait for valid world and player
    GameData::WorldChrMan* world = GameDataManager::WorldChrMan.Get();
    if (!world) return;
    void* player = Memory::RPM<void*>(reinterpret_cast<uintptr_t>(world) + 0x1E508);
    if (!player) return;

    currentGameRend = gameRend;
    if (input.IsWindowJustGetFocused()) {
        config.Reload();
    }

    float deltaTime = std::clamp(Time::DeltaTime(), 0.0f, 0.4f);
    const bool externalCameraOwned = IsActionCameraSyncEnabled();
    if (externalCameraOwned != externalCameraOwnedPreviousFrame) {
        input.Reset();
        freeCamera.ClearManualInput();
        mouseCameraInputLocked = false;
        externalCameraOwnedPreviousFrame = externalCameraOwned;
    }
    input.UpdateGamepad();
    
    __try { ProcessInput(gameRend, deltaTime); } __except (EXCEPTION_EXECUTE_HANDLER) { LOG_ERROR("Crash in ProcessInput"); return; }

    __try { ApplyDayCycleControl(); } __except (EXCEPTION_EXECUTE_HANDLER) { LOG_ERROR("Crash in ApplyDayCycleControl"); return; }

    __try { ApplyAnimationLab(); } __except (EXCEPTION_EXECUTE_HANDLER) { LOG_ERROR("Crash in ApplyAnimationLab"); return; }

    __try { ApplyEffectControl(); } __except (EXCEPTION_EXECUTE_HANDLER) { LOG_ERROR("Crash in ApplyEffectControl"); return; }

    __try { ApplyDropRateControl(); } __except (EXCEPTION_EXECUTE_HANDLER) { LOG_ERROR("Crash in ApplyDropRateControl"); return; }

    __try { ApplyPendingCloneCopy(); } __except (EXCEPTION_EXECUTE_HANDLER) { LOG_ERROR("Crash in ApplyPendingCloneCopy"); return; }

    __try { possessionControl.Update(world); } __except (EXCEPTION_EXECUTE_HANDLER) { LOG_ERROR("Crash in possessionControl.Update"); }

    __try { PrepareActiveView(gameRend); } __except (EXCEPTION_EXECUTE_HANDLER) { LOG_ERROR("Crash in PrepareActiveView"); return; }

    __try { freeCamera.Update(gameRend, deltaTime); } __except (EXCEPTION_EXECUTE_HANDLER) { LOG_ERROR("Crash in freeCamera.Update"); return; }

    __try { ApplyActionCameraSync(gameRend); } __except (EXCEPTION_EXECUTE_HANDLER) { LOG_ERROR("Crash in ApplyActionCameraSync"); return; }

    __try { ApplyActiveView(gameRend); } __except (EXCEPTION_EXECUTE_HANDLER) { LOG_ERROR("Crash in ApplyActiveView"); return; }

    __try { LogResidencyTelemetry(gameRend); } __except (EXCEPTION_EXECUTE_HANDLER) { LOG_ERROR("Crash in LogResidencyTelemetry"); return; }
    
    input.Reset();
}

void Freecam::OnGameShutdown() {
    InterlockedExchange(&shutdownRequested, 1);
    Freecam* current = instance;
    if (!current) return;

    current->input.BeginShutdown();

    // These writes are deliberately minimal and safe from WndProc. They stop
    // all camera ownership before the game's teardown invalidates its objects.
    current->isRunning = false;
    current->hookManager.GetCameraPositionCave().SetFrozen(false);
    if (current->actionCameraSync) {
        InterlockedExchange(&current->actionCameraSync->enabled, 0);
    }
    if (current->activeView) {
        InterlockedExchange(&current->activeView->actualEnabled, 0);
    }
}

bool Freecam::IsActionCameraSyncEnabled() const {
    return actionCameraSync &&
        actionCameraSync->magic == ActionCameraSyncMagic &&
        actionCameraSync->version == ActionCameraSyncVersion &&
        InterlockedCompareExchange(
            const_cast<volatile LONG*>(&actionCameraSync->enabled), 0, 0) != 0;
}

bool Freecam::ControllerControlsCamera() const {
    // When EldenIntel owns the shared policy, controller input must have one
    // destination. A value of 1 routes it exclusively to the character; 0
    // enables the native freecam controller bindings.
    if (!actionCameraSync ||
        actionCameraSync->magic != ActionCameraSyncMagic ||
        actionCameraSync->version != ActionCameraSyncVersion) {
        return true;
    }
    return InterlockedCompareExchange(
        const_cast<volatile LONG*>(&actionCameraSync->controllerControlsPlayer), 0, 0) == 0;
}

void Freecam::FindWorldBackReadInstances() {
    if (!worldBackReadBackups.empty()) return;
    const uintptr_t vtable = GameDataManager::WorldBackReadVtable.Get();
    if (!vtable) return;

    SYSTEM_INFO systemInfo{};
    GetSystemInfo(&systemInfo);
    uintptr_t cursor = reinterpret_cast<uintptr_t>(systemInfo.lpMinimumApplicationAddress);
    const uintptr_t maximum = reinterpret_cast<uintptr_t>(systemInfo.lpMaximumApplicationAddress);
    MEMORY_BASIC_INFORMATION memoryInfo{};

    while (cursor < maximum &&
           VirtualQuery(reinterpret_cast<void*>(cursor), &memoryInfo, sizeof(memoryInfo)) == sizeof(memoryInfo)) {
        const DWORD protection = memoryInfo.Protect & 0xFF;
        const bool writable =
            protection == PAGE_READWRITE ||
            protection == PAGE_WRITECOPY ||
            protection == PAGE_EXECUTE_READWRITE ||
            protection == PAGE_EXECUTE_WRITECOPY;
        const uintptr_t base = reinterpret_cast<uintptr_t>(memoryInfo.BaseAddress);
        const size_t size = memoryInfo.RegionSize;

        if (memoryInfo.State == MEM_COMMIT && writable && !(memoryInfo.Protect & PAGE_GUARD) &&
            size >= 0x3DC0 && size <= 64 * 1024 * 1024) {
            const uintptr_t end = base + size - 0x3DC0;
            for (uintptr_t candidate = base; candidate <= end; candidate += sizeof(uintptr_t)) {
                if (*reinterpret_cast<const uintptr_t*>(candidate) != vtable) continue;

                const float nearPrimary = *reinterpret_cast<const float*>(candidate + 0x48);
                const float farPrimary = *reinterpret_cast<const float*>(candidate + 0x4C);
                const float nearSecondary = *reinterpret_cast<const float*>(candidate + 0x1EF8);
                const float farSecondary = *reinterpret_cast<const float*>(candidate + 0x1EFC);
                const bool plausible =
                    std::isfinite(nearPrimary) && std::isfinite(farPrimary) &&
                    std::isfinite(nearSecondary) && std::isfinite(farSecondary) &&
                    nearPrimary > 0.0f && farPrimary >= nearPrimary &&
                    nearSecondary > 0.0f && farSecondary >= nearSecondary &&
                    farPrimary < 1000.0f && farSecondary < 1000.0f;
                if (!plausible) continue;

                worldBackReadBackups.push_back({
                    candidate,
                    nearPrimary,
                    farPrimary,
                    nearSecondary,
                    farSecondary,
                });
                if (worldBackReadBackups.size() >= 8) break;
            }
        }

        if (worldBackReadBackups.size() >= 8) break;
        const uintptr_t next = base + size;
        if (next <= cursor) break;
        cursor = next;
    }

    LOG_INFO("Found %zu WorldBackRead instance(s)", worldBackReadBackups.size());
}

void Freecam::ApplyAdaptiveWorldBackRead(GameData::GameRend* gameRend) {
    if (!freeCamera.IsEnabled()) {
        RestoreWorldBackRead();
        return;
    }
    if (!gameRend || !gameRend->csDebugCam) return;

    GameData::ChrIns* player = GameDataManager::GetPlayer();
    if (!player || !player->chrModules || !player->chrModules->chrPhysics) return;
    FindWorldBackReadInstances();
    if (worldBackReadBackups.empty()) return;

    const float3 playerPosition = player->chrModules->chrPhysics->localPos;
    const float3 cameraPosition = gameRend->csDebugCam->matrix.position();
    const float distance = (cameraPosition - playerPosition).length();
    if (!std::isfinite(distance)) return;

    // Keep a margin around both endpoints, but never contract the active
    // residency bubble while freecam remains enabled. Shrinking the loader
    // boundary as the camera returns toward the player causes visible unload /
    // reload transitions (often perceived as shader or geometry pop-in).
    // RestoreWorldBackRead returns the exact engine values when freecam exits.
    const float requestedRadius = std::clamp(distance + 120.0f, 120.0f, 2500.0f);
    const float radius = (std::max)(requestedRadius, appliedResidencyRadius);
    if (radius <= appliedResidencyRadius + 2.0f) return;

    const uintptr_t vtable = GameDataManager::WorldBackReadVtable.Get();
    for (const WorldBackReadBackup& backup : worldBackReadBackups) {
        if (*reinterpret_cast<const uintptr_t*>(backup.address) != vtable) continue;
        *reinterpret_cast<float*>(backup.address + 0x48) = (std::max)(backup.nearPrimary, radius);
        *reinterpret_cast<float*>(backup.address + 0x4C) = (std::max)(backup.farPrimary, radius + 10.0f);
        *reinterpret_cast<float*>(backup.address + 0x1EF8) = (std::max)(backup.nearSecondary, radius);
        *reinterpret_cast<float*>(backup.address + 0x1EFC) = (std::max)(backup.farSecondary, radius + 10.0f);
    }
    appliedResidencyRadius = radius;
}

void Freecam::RestoreWorldBackRead() {
    if (worldBackReadBackups.empty()) return;
    const uintptr_t vtable = GameDataManager::WorldBackReadVtable.Get();
    for (const WorldBackReadBackup& backup : worldBackReadBackups) {
        if (*reinterpret_cast<const uintptr_t*>(backup.address) != vtable) continue;
        *reinterpret_cast<float*>(backup.address + 0x48) = backup.nearPrimary;
        *reinterpret_cast<float*>(backup.address + 0x4C) = backup.farPrimary;
        *reinterpret_cast<float*>(backup.address + 0x1EF8) = backup.nearSecondary;
        *reinterpret_cast<float*>(backup.address + 0x1EFC) = backup.farSecondary;
    }
    worldBackReadBackups.clear();
    appliedResidencyRadius = 0.0f;
}

void Freecam::ForceLoadedCharacterAnimationRate() {
    if (!freeCamera.IsEnabled()) return;

    GameData::WorldChrMan* world = GameDataManager::WorldChrMan.Get();
    if (!world || !world->begin || !world->end || world->end < world->begin) return;

    const size_t count = world->GetEntityListLength();
    if (count > 100000) return;

    for (size_t index = 0; index < count; ++index) {
        GameData::ChrIns* entity = world->begin[index];
        if (!entity) continue;

        // Zero is the engine's Normal/full-rate cadence. Touch only the
        // scheduler override: do not change backread state, AI, physics,
        // animation speed, or the engine-selected omission state.
        entity->omissionModeOverride = 0;
    }
}

void Freecam::UpdateAnimationResidencyBubble() {
    const ULONGLONG now = GetTickCount64();
    if (now - lastAnimationResidencyAt < 100) return;
    lastAnimationResidencyAt = now;

    // IncreaseAnimationDistance owns the same scheduler/omission state. Do not
    // compete with it: double writers create cadence changes and popping at
    // the edge of their independently moving bubbles.
    externalAnimationDistanceDetected =
        GetModuleHandleW(L"IncreaseAnimationDistance.dll") != nullptr;
    if (externalAnimationDistanceDetected) {
        if (!externalAnimationDistanceLogged) {
            LOG_INFO("IncreaseAnimationDistance.dll detected; EldenIntel animation residency override disabled");
            externalAnimationDistanceLogged = true;
        }
        RestoreAnimationResidencyBubble();
        return;
    }
    externalAnimationDistanceLogged = false;

    GameData::GameRend* gameRend = currentGameRend;
    const bool enabled = gameRend &&
        Memory::RPM<GameData::FreecamMode>(
            reinterpret_cast<uintptr_t>(gameRend) + offsetof(GameData::GameRend, freeCameraMode)) !=
            GameData::FreecamMode::Disabled;
    GameData::Camera* debugCamera = enabled
        ? Memory::RPM<GameData::Camera*>(
            reinterpret_cast<uintptr_t>(gameRend) + offsetof(GameData::GameRend, csDebugCam))
        : nullptr;
    if (!debugCamera) {
        RestoreAnimationResidencyBubble();
        return;
    }

    const matrix4x4 cameraMatrix = Memory::RPM<matrix4x4>(
        reinterpret_cast<uintptr_t>(debugCamera) + offsetof(GameData::Camera, matrix));
    const float3 cameraPosition = cameraMatrix.position();
    if (!std::isfinite(cameraPosition.x) || !std::isfinite(cameraPosition.y) ||
        !std::isfinite(cameraPosition.z)) return;

    GameData::WorldChrMan* world = GameDataManager::WorldChrMan.Get();
    if (!world) return;
    auto** begin = Memory::RPM<GameData::ChrIns**>(
        reinterpret_cast<uintptr_t>(world) + offsetof(GameData::WorldChrMan, begin));
    auto** end = Memory::RPM<GameData::ChrIns**>(
        reinterpret_cast<uintptr_t>(world) + offsetof(GameData::WorldChrMan, end));
    if (!begin || !end || end < begin) return;
    const size_t count = static_cast<size_t>(end - begin);
    if (count > 100000) return;

    // A 200-unit radius gives freecam a 400-unit full-animation diameter.
    // This is intentionally independent of visual LOD: only already-loaded,
    // validated characters are touched.
    constexpr float AnimationRadius = 200.0f;
    constexpr float AnimationRadiusSquared = AnimationRadius * AnimationRadius;
    GameData::ChrIns* player = GameDataManager::GetPlayer();
    std::unordered_set<uintptr_t> active;
    active.reserve((std::min)(count, static_cast<size_t>(1024)));

    for (size_t index = 0; index < count; ++index) {
        GameData::ChrIns* entity = Memory::RPM<GameData::ChrIns*>(
            reinterpret_cast<uintptr_t>(begin + index));
        if (!entity || entity == player) continue;
        const uintptr_t address = reinterpret_cast<uintptr_t>(entity);
        const uintptr_t identity = Memory::RPM<uintptr_t>(address + 0x08);
        if (!identity || identity == UINTPTR_MAX) continue;
        GameData::ChrModules* modules = Memory::RPM<GameData::ChrModules*>(
            address + offsetof(GameData::ChrIns, chrModules));
        if (!modules) continue;
        GameData::ChrPhysics* physics = Memory::RPM<GameData::ChrPhysics*>(
            reinterpret_cast<uintptr_t>(modules) + offsetof(GameData::ChrModules, chrPhysics));
        if (!physics) continue;
        const float3 position = Memory::RPM<float3>(
            reinterpret_cast<uintptr_t>(physics) + offsetof(GameData::ChrPhysics, localPos));
        if (!std::isfinite(position.x) || !std::isfinite(position.y) ||
            !std::isfinite(position.z)) continue;
        const float3 delta = position - cameraPosition;
        const float distanceSquared = delta.x * delta.x + delta.y * delta.y + delta.z * delta.z;
        if (!std::isfinite(distanceSquared) || distanceSquared > AnimationRadiusSquared) continue;

        const uintptr_t overrideAddress = address + offsetof(GameData::ChrIns, omissionModeOverride);
        auto [entry, inserted] = animationResidencyBackups.try_emplace(
            address,
            AnimationResidencyBackup{
                identity,
                Memory::RPM<int32_t>(overrideAddress),
            });
        if (!inserted && entry->second.identity != identity) {
            entry->second = {
                identity,
                Memory::RPM<int32_t>(overrideAddress),
            };
        }
        Memory::WPM<int32_t>(overrideAddress, 0);
        active.insert(address);
    }

    for (auto it = animationResidencyBackups.begin(); it != animationResidencyBackups.end();) {
        if (active.contains(it->first)) {
            ++it;
            continue;
        }
        if (Memory::RPM<uintptr_t>(it->first + 0x08) == it->second.identity) {
            Memory::WPM<int32_t>(
                it->first + offsetof(GameData::ChrIns, omissionModeOverride),
                it->second.originalOverride);
        }
        it = animationResidencyBackups.erase(it);
    }
}

void Freecam::RestoreAnimationResidencyBubble() {
    for (const auto& [address, backup] : animationResidencyBackups) {
        if (Memory::RPM<uintptr_t>(address + 0x08) == backup.identity) {
            Memory::WPM<int32_t>(
                address + offsetof(GameData::ChrIns, omissionModeOverride),
                backup.originalOverride);
        }
    }
    animationResidencyBackups.clear();
}

void Freecam::LogResidencyTelemetry(GameData::GameRend* gameRend) {
    if (!freeCamera.IsEnabled() || !gameRend || !gameRend->csDebugCam) return;
    const ULONGLONG now = GetTickCount64();
    // Periodic diagnostics only. Per-second file writes add no value during
    // ordinary filming and can compete with capture/streaming I/O.
    if (now - lastResidencyTelemetryAt < 10000) return;
    lastResidencyTelemetryAt = now;

    GameData::WorldChrMan* world = GameDataManager::WorldChrMan.Get();
    GameData::ChrIns* player = GameDataManager::GetPlayer();
    if (!world || !player || !player->chrModules || !player->chrModules->chrPhysics) return;
    if (!world->begin || !world->end || world->end < world->begin) return;

    const float3 camera = gameRend->csDebugCam->matrix.position();
    const float3 playerPosition = player->chrModules->chrPhysics->localPos;
    const size_t loadedCharacters = world->GetEntityListLength();
    LOG_INFO(
        "RESIDENCY camera=(%.2f,%.2f,%.2f) player=(%.2f,%.2f,%.2f) loadedCharacters=%zu",
        camera.x, camera.y, camera.z,
        playerPosition.x, playerPosition.y, playerPosition.z,
        loadedCharacters);
}

bool Freecam::InitializeActionCameraSync() {
    actionCameraMapping = CreateFileMappingW(
        INVALID_HANDLE_VALUE,
        nullptr,
        PAGE_READWRITE,
        0,
        sizeof(ActionCameraSyncState),
        ActionCameraSyncName);
    if (!actionCameraMapping) return false;
    const bool created = GetLastError() != ERROR_ALREADY_EXISTS;

    actionCameraSync = static_cast<ActionCameraSyncState*>(MapViewOfFile(
        actionCameraMapping,
        FILE_MAP_ALL_ACCESS,
        0,
        0,
        sizeof(ActionCameraSyncState)));
    if (!actionCameraSync) {
        CloseHandle(actionCameraMapping);
        actionCameraMapping = nullptr;
        return false;
    }

    if (created) {
        std::memset(actionCameraSync, 0, sizeof(ActionCameraSyncState));
    }
    actionCameraSync->magic = ActionCameraSyncMagic;
    actionCameraSync->version = ActionCameraSyncVersion;
    if (created) {
        actionCameraSync->keyboardControlsCamera = 1;
        actionCameraSync->mouseControlsCamera = 1;
        actionCameraSync->controllerControlsPlayer = 1;
    }
    return true;
}

void Freecam::ApplyActionCameraSync(GameData::GameRend* gameRend) {
    if (!actionCameraSync || !gameRend || !gameRend->csDebugCam) return;
    if (!freeCamera.IsEnabled()) {
        // Action Cam is subordinate to freecam. Never let a stale producer
        // re-enable debug-camera mode after F1 has disabled freecam.
        InterlockedExchange(&actionCameraSync->enabled, 0);
        actionCameraWasActive = false;
        return;
    }

    const bool enabled =
        actionCameraSync->magic == ActionCameraSyncMagic &&
        actionCameraSync->version == ActionCameraSyncVersion &&
        InterlockedCompareExchange(&actionCameraSync->enabled, 0, 0) != 0;
    if (!enabled) {
        if (actionCameraWasActive) {
            freeCamera.AdoptCurrentCamera(gameRend);
            actionCameraWasActive = false;
        }
        actionCameraRebaseValid = false;
        return;
    }

    ActionCameraSyncState snapshot{};
    bool stable = false;
    for (int attempt = 0; attempt < 3; ++attempt) {
        const LONG before = InterlockedCompareExchange(&actionCameraSync->sequence, 0, 0);
        if ((before & 1) != 0) continue;
        MemoryBarrier();
        std::memcpy(snapshot.matrix, actionCameraSync->matrix, sizeof(snapshot.matrix));
        snapshot.fov = actionCameraSync->fov;
        MemoryBarrier();
        const LONG after = InterlockedCompareExchange(&actionCameraSync->sequence, 0, 0);
        if (before == after && (after & 1) == 0) {
            snapshot.sequence = after;
            snapshot.absolutePosition = InterlockedCompareExchange(
                &actionCameraSync->absolutePosition, 0, 0);
            stable = true;
            break;
        }
    }
    if (!stable) return;

    // The producer can observe the resulting game camera on its next update.
    // Applying a permanent absolute offset therefore creates a feedback loop.
    // Keep native position as the authority and consume each new producer pose
    // once as a relative delta. Orientation/FOV remain absolute.
    auto* nativePosition = static_cast<float*>(
        hookManager.GetCameraPositionCave().GetCameraPosition());
    const float3 sourcePosition(
        snapshot.matrix[12], snapshot.matrix[13], snapshot.matrix[14]);
    const bool absolutePosition = snapshot.absolutePosition != 0;
    if (absolutePosition) {
        actionCameraRebaseValid = false;
        actionCameraLastSourcePosition = sourcePosition;
        actionCameraLastAppliedSequence = snapshot.sequence;
    }
    else if (!actionCameraWasActive) {
        actionCameraRebaseValid = false;
        if (nativePosition &&
            std::isfinite(sourcePosition.x) &&
            std::isfinite(sourcePosition.y) &&
            std::isfinite(sourcePosition.z)) {
            __try {
                actionCameraNativePosition = float3(
                    nativePosition[0], nativePosition[1], nativePosition[2]);
                actionCameraLastSourcePosition = sourcePosition;
                actionCameraLastAppliedSequence = snapshot.sequence;
                actionCameraRebaseValid = true;
            }
            __except (EXCEPTION_EXECUTE_HANDLER) {
                actionCameraRebaseValid = false;
            }
        }
    }
    else if (actionCameraRebaseValid &&
             snapshot.sequence != actionCameraLastAppliedSequence &&
             std::isfinite(sourcePosition.x) &&
             std::isfinite(sourcePosition.y) &&
             std::isfinite(sourcePosition.z)) {
        const float3 delta = sourcePosition - actionCameraLastSourcePosition;
        const float distanceSquared =
            delta.x * delta.x + delta.y * delta.y + delta.z * delta.z;
        // A legitimate smoothed lock update is small. A large discontinuity is
        // an origin/pointer handoff; absorb it as a new baseline, not movement.
        if (std::isfinite(distanceSquared) && distanceSquared <= 400.0f) {
            actionCameraNativePosition += delta;
        }
        actionCameraLastSourcePosition = sourcePosition;
        actionCameraLastAppliedSequence = snapshot.sequence;
    }

    gameRend->freeCameraMode =
        InterlockedCompareExchange(&actionCameraSync->controllerControlsPlayer, 0, 0) != 0
            ? GameData::FreecamMode::Fixed
            : GameData::FreecamMode::EnabledUpdating;
    std::memcpy(&gameRend->csDebugCam->matrix, snapshot.matrix, sizeof(snapshot.matrix));
    if (absolutePosition) {
        // Camera tracks already publish world-space coordinates. Apply them
        // exactly on the game camera update instead of treating a long rail
        // segment as an invalid action-camera displacement.
        gameRend->csDebugCam->matrix.c3.x = sourcePosition.x;
        gameRend->csDebugCam->matrix.c3.y = sourcePosition.y;
        gameRend->csDebugCam->matrix.c3.z = sourcePosition.z;
    }
    else if (actionCameraRebaseValid) {
        gameRend->csDebugCam->matrix.c3.x = actionCameraNativePosition.x;
        gameRend->csDebugCam->matrix.c3.y = actionCameraNativePosition.y;
        gameRend->csDebugCam->matrix.c3.z = actionCameraNativePosition.z;
    }
    gameRend->csDebugCam->fov = snapshot.fov;
    if (gameRend->csPersCam1 &&
        std::isfinite(gameRend->csPersCam1->renderDistance) &&
        gameRend->csPersCam1->renderDistance > 0.0f) {
        // The relocated native view focus moves Elden Ring's update/render
        // perimeter to freecam, but its radius otherwise remains sized for the
        // player camera. Give cinematography a wider bubble without modifying
        // normal gameplay or requesting an unbounded world set.
        constexpr float FreecamRenderRadiusScale = 1.75f;
        constexpr float FreecamRenderRadiusCap = 5000.0f;
        gameRend->csDebugCam->renderDistance = std::clamp(
            gameRend->csPersCam1->renderDistance * FreecamRenderRadiusScale,
            gameRend->csPersCam1->renderDistance,
            FreecamRenderRadiusCap);
    }
    actionCameraWasActive = true;
}

void Freecam::DisposeActionCameraSync() {
    if (actionCameraSync) {
        actionCameraSync->enabled = 0;
        UnmapViewOfFile(actionCameraSync);
        actionCameraSync = nullptr;
    }
    if (actionCameraMapping) {
        CloseHandle(actionCameraMapping);
        actionCameraMapping = nullptr;
    }
    actionCameraWasActive = false;
    actionCameraRebaseValid = false;
    actionCameraLastAppliedSequence = -1;
}

bool Freecam::InitializeDayCycleControl() {
    dayCycleControlMapping = CreateFileMappingW(
        INVALID_HANDLE_VALUE,
        nullptr,
        PAGE_READWRITE,
        0,
        sizeof(DayCycleControlState),
        DayCycleControlName);
    if (!dayCycleControlMapping) return false;

    dayCycleControl = static_cast<DayCycleControlState*>(MapViewOfFile(
        dayCycleControlMapping,
        FILE_MAP_ALL_ACCESS,
        0,
        0,
        sizeof(DayCycleControlState)));
    if (!dayCycleControl) {
        CloseHandle(dayCycleControlMapping);
        dayCycleControlMapping = nullptr;
        return false;
    }

    std::memset(dayCycleControl, 0, sizeof(DayCycleControlState));
    dayCycleControl->magic = DayCycleControlMagic;
    dayCycleControl->version = DayCycleControlVersion;
    dayCycleControl->cycleSpeed = 75000;
    lastDayCycleRequestSequence = 0;
    return true;
}

void Freecam::ApplyDayCycleControl() {
    if (!dayCycleControl ||
        dayCycleControl->magic != DayCycleControlMagic ||
        dayCycleControl->version != DayCycleControlVersion) return;

    DaytimeUpdateCave& daytime = hookManager.GetDaytimeUpdateCave();
    const LONG requestSequence =
        InterlockedCompareExchange(&dayCycleControl->requestSequence, 0, 0);
    if (requestSequence != lastDayCycleRequestSequence) {
        const LONG requestedSpeed =
            InterlockedCompareExchange(&dayCycleControl->cycleSpeed, 0, 0);
        daytime.SetCycleSpeed(std::clamp(static_cast<int>(requestedSpeed), 1000, 1000000));
        daytime.SetCycleWeatherTime(
            InterlockedCompareExchange(&dayCycleControl->requestedEnabled, 0, 0) != 0);
        lastDayCycleRequestSequence = requestSequence;
    }

    InterlockedExchange(
        &dayCycleControl->actualEnabled,
        daytime.IsCycleWeatherTime() ? 1 : 0);
    InterlockedExchange(&dayCycleControl->cycleSpeed, daytime.GetCycleSpeed());
}

void Freecam::DisposeDayCycleControl() {
    if (dayCycleControl) {
        UnmapViewOfFile(dayCycleControl);
        dayCycleControl = nullptr;
    }
    if (dayCycleControlMapping) {
        CloseHandle(dayCycleControlMapping);
        dayCycleControlMapping = nullptr;
    }
}

bool Freecam::InitializeAnimationLab() {
    animationLabMapping = CreateFileMappingW(INVALID_HANDLE_VALUE, nullptr, PAGE_READWRITE,
        0, sizeof(AnimationLabState), AnimationLabName);
    if (!animationLabMapping) return false;
    animationLab = static_cast<AnimationLabState*>(MapViewOfFile(animationLabMapping,
        FILE_MAP_ALL_ACCESS, 0, 0, sizeof(AnimationLabState)));
    if (!animationLab) { CloseHandle(animationLabMapping); animationLabMapping = nullptr; return false; }
    std::memset(animationLab, 0, sizeof(AnimationLabState));
    animationLab->magic = AnimationLabMagic;
    animationLab->version = AnimationLabVersion;
    return true;
}

void Freecam::ApplyAnimationLab() {
    if (!animationLab || animationLab->magic != AnimationLabMagic ||
        animationLab->version != AnimationLabVersion) return;
    const LONG sequence = InterlockedCompareExchange(&animationLab->requestSequence, 0, 0);
    if (sequence == lastAnimationLabRequestSequence) return;
    lastAnimationLabRequestSequence = sequence;
    const int id = InterlockedCompareExchange(&animationLab->gestureId, 0, 0);
    const uintptr_t address = GameDataManager::PlayGestureFunc.Get();
    LONG result = -1;
    if (address && id > 0 && id <= 110) {
        alignas(16) std::uint8_t request[64]{};
        *reinterpret_cast<std::int32_t*>(request + 0x10) = id;
        using PlayGesture = void(__fastcall*)(void*);
        __try { reinterpret_cast<PlayGesture>(address)(request); result = 1; }
        __except (EXCEPTION_EXECUTE_HANDLER) { result = -2; }
    }
    InterlockedExchange(&animationLab->resultCode, result);
    InterlockedExchange(&animationLab->resultSequence, sequence);
}

void Freecam::DisposeAnimationLab() {
    if (animationLab) { UnmapViewOfFile(animationLab); animationLab = nullptr; }
    if (animationLabMapping) { CloseHandle(animationLabMapping); animationLabMapping = nullptr; }
}

bool Freecam::InitializeActiveView() {
    activeViewMapping = CreateFileMappingW(INVALID_HANDLE_VALUE, nullptr, PAGE_READWRITE, 0, sizeof(ActiveViewState), ActiveViewName);
    if (!activeViewMapping) return false;
    activeView = static_cast<ActiveViewState*>(MapViewOfFile(activeViewMapping, FILE_MAP_ALL_ACCESS, 0, 0, sizeof(ActiveViewState)));
    if (!activeView) { CloseHandle(activeViewMapping); activeViewMapping = nullptr; return false; }
    std::memset(activeView, 0, sizeof(ActiveViewState)); activeView->magic = ActiveViewMagic; activeView->version = ActiveViewVersion;
    return true;
}

void Freecam::ApplyActiveView(GameData::GameRend* gameRend) {
    const bool enabled = freeCamera.IsEnabled();
    bool applied = false;
    auto& positionCave = hookManager.GetCameraPositionCave();
    if (!enabled) {
        // Release native ownership as part of the normal disable transition.
        // Previously this flag was cleared only during DLL disposal, leaving
        // Elden Ring's position writer frozen at the final freecam pose.
        positionCave.SetFrozen(false);
        activeViewFramePosition = nullptr;
        actionCameraWasActive = false;
        actionCameraRebaseValid = false;
        actionCameraLastAppliedSequence = -1;
        if (activeView) InterlockedExchange(&activeView->actualEnabled, 0);
        return;
    }
    if (enabled && gameRend && gameRend->csDebugCam) {
        auto* position = static_cast<float*>(positionCave.GetCameraPosition());
        // The AOB can be reached by more than one camera object. Only publish
        // into the exact native camera instance sampled in PrepareActiveView;
        // never read one instance and write the resulting pose into another.
        if (position && position == activeViewFramePosition) {
            __try {
                auto* controller = gameRend->csDebugCam;
                const float3 moved = controller->matrix.position();
                if (!std::isfinite(moved.x) || !std::isfinite(moved.y) ||
                    !std::isfinite(moved.z)) {
                    activeViewFramePosition = nullptr;
                    if (activeView) InterlockedExchange(&activeView->actualEnabled, 0);
                    return;
                }
                position[0] = moved.x;
                position[1] = moved.y;
                position[2] = moved.z;
                // Publish our actual camera orientation into the native
                // follow-camera object too. This restores proper yaw/pitch
                // controls and makes the engine's render/update focus cone
                // follow what the freecam is visibly looking at.
                const EulerAngles& rotation = freeCamera.GetEuler();
                // Both camera objects expose the same world-facing matrix
                // basis here. Preserve the exact visible yaw. Adding PI at
                // this bridge produced a reproducible 180-degree snap on
                // both activation and release.
                position[0xB4 / sizeof(float)] = rotation.yaw;
                position[0xB8 / sizeof(float)] = rotation.pitch;
                positionCave.SetFrozen(true);
                applied = true;
            }
            __except (EXCEPTION_EXECUTE_HANDLER) {
                applied = false;
            }
        }
    }
    activeViewFramePosition = nullptr;
    if (activeView) InterlockedExchange(&activeView->actualEnabled, applied ? 1 : 0);
}

void Freecam::PrepareActiveView(GameData::GameRend* gameRend) {
    activeViewFramePosition = nullptr;
    if (!freeCamera.IsEnabled() || !gameRend || !gameRend->csDebugCam) return;
    auto* position = static_cast<float*>(hookManager.GetCameraPositionCave().GetCameraPosition());
    if (!position) return;
    __try {
        // The debug/free camera is the transform owner. Do not pull the
        // player-centered native follow-camera pose back into it here: doing
        // so recreates an orbit pivot and makes A/D feel like camera panning.
        // Retain only the validated native instance for ApplyActiveView,
        // which publishes our completed pose outward after input is applied.
        const float x = position[0];
        const float y = position[1];
        const float z = position[2];
        if (!std::isfinite(x) || !std::isfinite(y) || !std::isfinite(z)) return;
        activeViewFramePosition = position;
    }
    __except (EXCEPTION_EXECUTE_HANDLER) {}
}

void Freecam::DisposeActiveView() {
    hookManager.GetCameraPositionCave().SetFrozen(false);
    if (activeView) { activeView->requestedEnabled = 0; UnmapViewOfFile(activeView); activeView = nullptr; }
    if (activeViewMapping) { CloseHandle(activeViewMapping); activeViewMapping = nullptr; }
}

bool Freecam::InitializeEffectControl() {
    effectControlMapping = CreateFileMappingW(INVALID_HANDLE_VALUE, nullptr, PAGE_READWRITE, 0,
        sizeof(EffectControlState), EffectControlName);
    if (!effectControlMapping) return false;
    effectControl = static_cast<EffectControlState*>(MapViewOfFile(effectControlMapping,
        FILE_MAP_ALL_ACCESS, 0, 0, sizeof(EffectControlState)));
    if (!effectControl) { CloseHandle(effectControlMapping); effectControlMapping = nullptr; return false; }
    if (effectControl->magic != EffectControlMagic || effectControl->version != EffectControlVersion) {
        std::memset(effectControl, 0, sizeof(EffectControlState));
        effectControl->magic = EffectControlMagic;
        effectControl->version = EffectControlVersion;
    }
    lastEffectRequestSequence = InterlockedCompareExchange(&effectControl->resultSequence, 0, 0);
    return true;
}

namespace {
    struct EffectWorkItem {
        uintptr_t applyAddress;
        uintptr_t copyPlayerAddress;
        void* effectContext;
        void* effectOwner;
        void* sourcePlayer;
        LONG effectId;
        void* control;
        LONG sequence;
        HMODULE module;
    };

    DWORD WINAPI ApplyEffectThreadProc(LPVOID parameter) {
        auto* work = static_cast<EffectWorkItem*>(parameter);
        LONG result = -2;
        __try {
            if (work->effectId == -2) {
                // Native PlayerIns copy used by Mimic Tear. When Transmogrify
                // is loaded its detour also attaches the matching appearance
                // VFX, so face, body type, equipment and loose-part aliases
                // stay in the same assembly path as the source player.
                using CopyPlayerFn = void(__fastcall*)(void*, void*);
                reinterpret_cast<CopyPlayerFn>(work->copyPlayerAddress)(
                    work->effectOwner, work->sourcePlayer);
            } else {
                // Match the verified Cheat Engine wrapper on a dedicated thread.
                using ApplyEffectFn = int(__fastcall*)(void*, unsigned int, bool);
                result = reinterpret_cast<ApplyEffectFn>(work->applyAddress)(
                    work->effectOwner, static_cast<unsigned int>(work->effectId), false);
            }
            result = 1;
        }
        __except (EXCEPTION_EXECUTE_HANDLER) {
            result = -2;
        }

        struct SharedEffectControl {
            std::uint32_t magic;
            std::uint32_t version;
            volatile LONG requestSequence;
            volatile LONG effectId;
            volatile LONG resultSequence;
            volatile LONG resultCode;
            std::uint64_t targetAddress;
        };
        auto* control = static_cast<SharedEffectControl*>(work->control);
        if (control && control->magic == 0x58464645 && control->version == 2) {
            InterlockedExchange(&control->resultCode, result);
            InterlockedExchange(&control->resultSequence, work->sequence);
        }
        const HMODULE module = work->module;
        delete work;
        FreeLibraryAndExitThread(module, 0);
        return 0;
    }
}

void Freecam::ApplyEffectControl() {
    if (!effectControl || effectControl->magic != EffectControlMagic ||
        effectControl->version != EffectControlVersion) return;
    const LONG sequence = InterlockedCompareExchange(&effectControl->requestSequence, 0, 0);
    if (sequence == 0 || sequence == lastEffectRequestSequence) return;
    lastEffectRequestSequence = sequence;

    const LONG effectId = InterlockedCompareExchange(&effectControl->effectId, 0, 0);
    const uintptr_t applyAddress = GameDataManager::ApplyEffectFunc.Get();
    const uintptr_t copyPlayerAddress = GameDataManager::CopyPlayerCharacterDataFunc.Get();
    const uintptr_t addInventoryFromShopAddress = GameDataManager::AddInventoryFromShopFunc.Get();
    GameData::WorldChrMan* world = GameDataManager::WorldChrMan.Get();
    if (world && (effectId == -3 || effectId == -4)) {
        const bool isNinjaSet = effectId == -3;
        const char* outfitName = isNinjaSet ? "Ninja Set" : "Confessor Set";
        LONG result = -1;
        __try {
            if (!GetModuleHandleW(L"ertransmogrify.dll")) {
                LOG_ERROR("%s rejected: ertransmogrify.dll is not loaded", outfitName);
                result = -10;
            }
            else if (!addInventoryFromShopAddress) {
                LOG_ERROR("%s rejected: AddInventoryFromShopFunc was not resolved", outfitName);
                result = -11;
            }
            else {
                using AddInventoryFromShopFn = bool(__fastcall*)(int*, int);
                const auto addInventoryFromShop = reinterpret_cast<AddInventoryFromShopFn>(addInventoryFromShopAddress);
                constexpr int NinjaProtectorIds[] = { 5190000, 5190100, 5190200, 5190300 };
                constexpr int ConfessorProtectorIds[] = { 880000, 880100, 880200, 880300 };
                const int* protectorIds = isNinjaSet ? NinjaProtectorIds : ConfessorProtectorIds;
                bool allAccepted = true;
                for (int index = 0; index < 4; ++index) {
                    const int protectorId = protectorIds[index];
                    const int goodsId = 6900000 + (protectorId / 100);
                    int encodedItemId = static_cast<int>(0x40000000u + static_cast<unsigned int>(goodsId));
                    const bool accepted = addInventoryFromShop(&encodedItemId, 1);
                    LOG_INFO("%s protector=%d goods=%d accepted=%d", outfitName, protectorId, goodsId, accepted ? 1 : 0);
                    allAccepted = allAccepted && accepted;
                }
                result = allAccepted ? 1 : -12;
            }
        }
        __except (EXCEPTION_EXECUTE_HANDLER) {
            LOG_ERROR("%s crashed while invoking Transmogrify inventory path", outfitName);
            result = -13;
        }
        InterlockedExchange(&effectControl->resultCode, result);
        InterlockedExchange(&effectControl->resultSequence, sequence);
        return;
    }
    if (world && ((effectId >= 0 && applyAddress) || (effectId == -2 && copyPlayerAddress))) {
        __try {
            const uintptr_t requestedTarget = effectControl->targetAddress;
            void* effectOwner = requestedTarget
                ? reinterpret_cast<void*>(requestedTarget)
                : Memory::RPM<void*>(reinterpret_cast<uintptr_t>(world) + 0x1E508);
            void* sourcePlayer = Memory::RPM<void*>(reinterpret_cast<uintptr_t>(world) + 0x1E508);
            if (effectId == -2 && effectOwner && sourcePlayer) {
                // Immediate copy
                using CopyPlayerFn = void(__fastcall*)(void*, void*);
                reinterpret_cast<CopyPlayerFn>(copyPlayerAddress)(effectOwner, sourcePlayer);
                LOG_INFO("Clone player-copy invoked target=%p source=%p (Transmogrify owns appearance)", effectOwner, sourcePlayer);

                // Schedule deferred retries on render thread (Update loop).
                // Clone's renderer/modules need time to initialize after spawn.
                // Transmogrify's detour re-applies SpEffects on game update task;
                // we only re-call the copy function to propagate appearance to renderer.
                pendingCloneCopyTarget = effectOwner;
                pendingCloneCopyPlayerAddress = copyPlayerAddress;
                pendingCloneCopySource = sourcePlayer;
                pendingCloneCopyAttempts = 8;
                pendingCloneCopyFrameDelay = 30; // ~500ms at 60fps before first retry

                InterlockedExchange(&effectControl->resultCode, 1);
                InterlockedExchange(&effectControl->resultSequence, sequence);
                return;
            }
            if (effectOwner && sourcePlayer) {
                HMODULE module = nullptr;
                if (!GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS,
                    reinterpret_cast<LPCWSTR>(&ApplyEffectThreadProc), &module)) {
                    throw 0;
                }
                auto* work = new EffectWorkItem{
                    applyAddress, copyPlayerAddress, nullptr, effectOwner, sourcePlayer, effectId,
                    effectControl, sequence, module
                };
                HANDLE thread = CreateThread(nullptr, 0, ApplyEffectThreadProc, work, 0, nullptr);
                if (thread) {
                    CloseHandle(thread);
                    return;
                }
                delete work;
                FreeLibrary(module);
            }
        }
        __except (EXCEPTION_EXECUTE_HANDLER) {}
    }
    InterlockedExchange(&effectControl->resultCode, -1);
    InterlockedExchange(&effectControl->resultSequence, sequence);
}

void Freecam::ApplyPendingCloneCopy() {
    if (!pendingCloneCopyTarget || pendingCloneCopyAttempts <= 0) return;
    if (--pendingCloneCopyFrameDelay > 0) return;

    // Verify game is in a valid state (not loading)
    GameData::WorldChrMan* world = GameDataManager::WorldChrMan.Get();
    if (!world) return;
    void* sourcePlayer = Memory::RPM<void*>(reinterpret_cast<uintptr_t>(world) + 0x1E508);
    if (!sourcePlayer) return;

    // Verify target is still valid (basic pointer check)
    if (!pendingCloneCopyTarget) {
        pendingCloneCopyTarget = nullptr;
        pendingCloneCopyAttempts = 0;
        return;
    }

    using CopyPlayerFn = void(__fastcall*)(void*, void*);
    auto copyFn = reinterpret_cast<CopyPlayerFn>(pendingCloneCopyPlayerAddress);
    if (!copyFn || !pendingCloneCopySource) {
        pendingCloneCopyTarget = nullptr;
        pendingCloneCopyAttempts = 0;
        return;
    }

    __try {
        copyFn(pendingCloneCopyTarget, pendingCloneCopySource);
        LOG_INFO("Clone deferred copy attempt %d target=%p", 9 - pendingCloneCopyAttempts, pendingCloneCopyTarget);
    }
    __except (EXCEPTION_EXECUTE_HANDLER) {
        pendingCloneCopyTarget = nullptr;
        pendingCloneCopyAttempts = 0;
        return;
    }

    if (--pendingCloneCopyAttempts <= 0) {
        pendingCloneCopyTarget = nullptr;
        return;
    }
    pendingCloneCopyFrameDelay = 15; // ~250ms between retries
}

void Freecam::DisposeEffectControl() {
    if (effectControl) { UnmapViewOfFile(effectControl); effectControl = nullptr; }
    if (effectControlMapping) { CloseHandle(effectControlMapping); effectControlMapping = nullptr; }
}

bool Freecam::InitializeDropRateControl() {
    dropRateControlMapping = CreateFileMappingW(INVALID_HANDLE_VALUE, nullptr, PAGE_READWRITE, 0,
        sizeof(DropRateControlState), DropRateControlName);
    if (!dropRateControlMapping) return false;
    dropRateControl = static_cast<DropRateControlState*>(MapViewOfFile(dropRateControlMapping,
        FILE_MAP_ALL_ACCESS, 0, 0, sizeof(DropRateControlState)));
    if (!dropRateControl) {
        CloseHandle(dropRateControlMapping);
        dropRateControlMapping = nullptr;
        return false;
    }
    if (dropRateControl->magic != DropRateControlMagic ||
        dropRateControl->version != DropRateControlVersion) {
        std::memset(dropRateControl, 0, sizeof(DropRateControlState));
        dropRateControl->magic = DropRateControlMagic;
        dropRateControl->version = DropRateControlVersion;
    }
    lastDropRateRequestSequence = InterlockedCompareExchange(&dropRateControl->resultSequence, 0, 0);
    return true;
}

LONG Freecam::BuildDropRatePatchCache() {
    if (!dropRatePatches.empty()) {
        LONG total = 0;
        for (const auto& patch : dropRatePatches) total += patch.changedPoints;
        return total;
    }

    constexpr uintptr_t ParamMasterOffset = 0x18;
    constexpr uintptr_t EntryNameOffset = 0x18;
    constexpr uintptr_t EntryNameLengthOffset = 0x28;
    constexpr uintptr_t EntryDataRootOffset = 0x80;
    constexpr uintptr_t ParamDataRootOffset = 0x80;
    constexpr uintptr_t RowCountOffset = 0x0A;
    constexpr uintptr_t RowVectorOffset = 0x40;
    constexpr uintptr_t RowEntrySize = 0x18;
    constexpr uintptr_t RowEntryOffsetOffset = 0x08;
    constexpr uintptr_t BasePointOffset = 0x40;

    auto* manager = static_cast<std::uint8_t*>(GameDataManager::CSRegulationManager.Get());
    if (!manager) return -10;

    auto** start = *reinterpret_cast<void***>(manager + ParamMasterOffset);
    auto** end = *reinterpret_cast<void***>(manager + ParamMasterOffset + sizeof(void*));
    if (!start || !end || end <= start) return -11;
    const auto entryCount = static_cast<size_t>(end - start);
    if (entryCount < 100 || entryCount > 1000) return -12;

    std::uint8_t* table = nullptr;
    for (size_t index = 0; index < entryCount; ++index) {
        auto* entry = static_cast<std::uint8_t*>(start[index]);
        if (!entry) continue;
        const auto nameLength = *reinterpret_cast<const std::uint64_t*>(entry + EntryNameLengthOffset);
        const wchar_t* name = nameLength <= 7
            ? reinterpret_cast<const wchar_t*>(entry + EntryNameOffset)
            : *reinterpret_cast<const wchar_t**>(entry + EntryNameOffset);
        constexpr wchar_t EnemyLotName[] = L"ItemLotParam_enemy";
        constexpr size_t EnemyLotNameLength = (sizeof(EnemyLotName) / sizeof(wchar_t)) - 1;
        if (!name || nameLength != EnemyLotNameLength ||
            std::wmemcmp(name, EnemyLotName, EnemyLotNameLength) != 0) continue;
        auto* rootOwner = *reinterpret_cast<std::uint8_t**>(entry + EntryDataRootOffset);
        if (!rootOwner) return -13;
        table = *reinterpret_cast<std::uint8_t**>(rootOwner + ParamDataRootOffset);
        break;
    }
    if (!table) return -14;

    const auto rowCount = *reinterpret_cast<const std::uint16_t*>(table + RowCountOffset);
    if (rowCount == 0 || rowCount > 20000) return -15;
    dropRatePatches.reserve(rowCount);
    LONG totalChanged = 0;
    for (std::uint32_t index = 0; index < rowCount; ++index) {
        const auto* rowEntry = table + RowVectorOffset + index * RowEntrySize;
        const auto rowOffset = *reinterpret_cast<const std::int64_t*>(rowEntry + RowEntryOffsetOffset);
        auto* points = table + rowOffset + BasePointOffset;
        DropRatePatch patch{};
        patch.address = points;
        std::memcpy(patch.original, points, sizeof(patch.original));
        std::memcpy(patch.boosted, points, sizeof(patch.boosted));
        for (int slot = 0; slot < 8; ++slot) {
            auto* value = reinterpret_cast<std::uint16_t*>(patch.boosted + slot * sizeof(std::uint16_t));
            if (*value == 0 || *value >= 1000) continue;
            *value = static_cast<std::uint16_t>(std::min<unsigned int>(1000, *value * 10u));
            ++patch.changedPoints;
        }
        if (patch.changedPoints > 0) {
            totalChanged += patch.changedPoints;
            dropRatePatches.push_back(patch);
        }
    }
    return totalChanged > 0 ? totalChanged : -16;
}

void Freecam::ApplyDropRateControl() {
    if (!dropRateControl || dropRateControl->magic != DropRateControlMagic ||
        dropRateControl->version != DropRateControlVersion) return;
    const LONG sequence = InterlockedCompareExchange(&dropRateControl->requestSequence, 0, 0);
    if (sequence == 0 || sequence == lastDropRateRequestSequence) return;
    lastDropRateRequestSequence = sequence;

    LONG result = -1;
    LONG changed = 0;
    __try {
        const LONG command = InterlockedCompareExchange(&dropRateControl->command, 0, 0);
        if (command == 1) {
            changed = BuildDropRatePatchCache();
            if (changed > 0) {
                for (const auto& patch : dropRatePatches)
                    std::memcpy(patch.address, patch.boosted, sizeof(patch.boosted));
                dropRateEnabled = true;
                result = 1;
            } else result = changed;
        } else if (command == 2) {
            for (const auto& patch : dropRatePatches)
                std::memcpy(patch.address, patch.original, sizeof(patch.original));
            dropRateEnabled = false;
            result = 1;
            for (const auto& patch : dropRatePatches) changed += patch.changedPoints;
        }
    }
    __except (EXCEPTION_EXECUTE_HANDLER) {
        result = -20;
    }
    InterlockedExchange(&dropRateControl->changedPoints, changed);
    InterlockedExchange(&dropRateControl->actualEnabled, dropRateEnabled ? 1 : 0);
    InterlockedExchange(&dropRateControl->resultCode, result);
    InterlockedExchange(&dropRateControl->resultSequence, sequence);
}

void Freecam::DisposeDropRateControl() {
    if (dropRateEnabled && InterlockedCompareExchange(&shutdownRequested, 0, 0) == 0) {
        __try {
            for (const auto& patch : dropRatePatches)
                std::memcpy(patch.address, patch.original, sizeof(patch.original));
        }
        __except (EXCEPTION_EXECUTE_HANDLER) {}
    }
    dropRatePatches.clear();
    dropRateEnabled = false;
    if (dropRateControl) { UnmapViewOfFile(dropRateControl); dropRateControl = nullptr; }
    if (dropRateControlMapping) { CloseHandle(dropRateControlMapping); dropRateControlMapping = nullptr; }
}

void __fastcall Freecam::hkUpdateCameraMatrix(GameData::GameRend* gameRend, void* rdx, void* r8, void* r9) {
    // Once shutdown starts, never touch Freecam/game-owned state again. The
    // original function remains available until the worker removes this hook.
    if (InterlockedCompareExchange(&shutdownRequested, 0, 0) != 0) {
        origUpdateCameraMatrix(gameRend, rdx, r8, r9);
        return;
    }
    // Skip all hooks during loading - wait for valid world and player
    if (instance) {
        GameData::WorldChrMan* world = GameDataManager::WorldChrMan.Get();
        if (!world) {
            origUpdateCameraMatrix(gameRend, rdx, r8, r9);
            return;
        }
        void* player = Memory::RPM<void*>(reinterpret_cast<uintptr_t>(world) + 0x1E508);
        if (!player) {
            origUpdateCameraMatrix(gameRend, rdx, r8, r9);
            return;
        }
    }
    // Feed the synchronized pose into the game's own update first so frustum,
    // shadows, exposure, LOD, and other camera-derived render state are built
    // from the current Action Cam frame rather than the previous one.
    if (instance && gameRend) instance->ApplyActionCameraSync(gameRend);
    origUpdateCameraMatrix(gameRend, rdx, r8, r9);
    
    // Update input/freecam state, then re-assert the synchronized pose as the
    // final camera matrix for this frame.
    if (instance && gameRend) instance->Update(gameRend);
}

void Freecam::Dispose() {
    LOG_INFO("Disposing Freecam...");
    isRunning = false;

    // During process shutdown FieldArea/GameRend may already be invalid. The
    // WndProc close signal has released the native writer, so skip traversal.
    if (InterlockedCompareExchange(&shutdownRequested, 0, 0) == 0) {
        RestoreAnimationResidencyBubble();
        freeCamera.DisableCamera();
    }
    RestoreWorldBackRead();
    DisposeActionCameraSync();
    DisposeDayCycleControl();
    DisposeAnimationLab();
    DisposeActiveView();
    DisposeEffectControl();
    DisposeDropRateControl();
    possessionControl.Dispose(GameDataManager::WorldChrMan.Get());
	speedhack.SetTimeScale(1.0f);
    hookManager.Shutdown();
    input.SetShutdownCallback(nullptr);
    input.UnhookWndProc(ModUtils::muWindow);
    Logger::Shutdown();

    instance = nullptr;
}
