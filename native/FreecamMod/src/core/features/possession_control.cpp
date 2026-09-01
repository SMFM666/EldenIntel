#include "possession_control.h"
#include "core/game_data_manager.h"

#include <cstring>

namespace {
constexpr uintptr_t WorldDataOffset = 0x10EF8;
constexpr uintptr_t MainPlayerOffset = 0x1E508;
constexpr uintptr_t ActorControllerOffset = 0x190;
constexpr uintptr_t ActorTransformOffset = 0x68;
constexpr uintptr_t ActorStateOffset = 0x68;
constexpr uintptr_t ActorFlags0Offset = 0x530;
constexpr uintptr_t ActorFlags1Offset = 0x1C5;
constexpr uintptr_t LinkAOffset = 0xA8;
constexpr uintptr_t LinkBOffset = 0xB8;
constexpr uintptr_t BackLinkOffset = 0x3B0;
constexpr uintptr_t CharacterControllerOffset = 0x58;
constexpr uintptr_t ControllerOwnerOffset = 0x10;
constexpr uintptr_t ControllerModifierOffset = 0xC8;
constexpr uintptr_t ModifierActionFlagsOffset = 0x18;
constexpr uintptr_t ControllerFlagsOffset = 0xF0;
constexpr uintptr_t InnerControllerFlagOffset = 0x19B;
constexpr uintptr_t TransformFlagOffset = 0x1D3;
constexpr uintptr_t TeamOffset = 0x6C;
constexpr uintptr_t HandleOffset = 0x8;
constexpr uintptr_t DeathFlagOffset = 0x1C5;
constexpr std::uint8_t DeathFlag = 0x80;
constexpr uintptr_t PositionOffset = 0x70;
constexpr float PlayerVerticalOffset = -0.875f;
}

bool PossessionControl::Read(uintptr_t address, void* value, size_t size) {
    if (!address || !value || !size) return false;
    __try { std::memcpy(value, reinterpret_cast<const void*>(address), size); return true; }
    __except (EXCEPTION_EXECUTE_HANDLER) { return false; }
}

bool PossessionControl::Write(uintptr_t address, const void* value, size_t size) {
    if (!address || !value || !size) return false;
    __try { std::memcpy(reinterpret_cast<void*>(address), value, size); return true; }
    __except (EXCEPTION_EXECUTE_HANDLER) { return false; }
}

bool PossessionControl::IsValidCharacter(uintptr_t character) {
    if (!character) return false;
    const auto handle = ReadValue<std::uint32_t>(character + HandleOffset);
    if (((handle >> 28) & 0xF) != 1) return false;
    const auto controller = ReadValue<uintptr_t>(character + CharacterControllerOffset);
    return controller && ReadValue<uintptr_t>(controller + ControllerOwnerOffset) == character;
}

bool PossessionControl::Initialize() {
    mapping_ = CreateFileMappingW(INVALID_HANDLE_VALUE, nullptr, PAGE_READWRITE, 0,
        sizeof(SharedState), MappingName);
    if (!mapping_) return false;
    state_ = static_cast<SharedState*>(MapViewOfFile(mapping_, FILE_MAP_ALL_ACCESS,
        0, 0, sizeof(SharedState)));
    if (!state_) { CloseHandle(mapping_); mapping_ = nullptr; return false; }
    if (state_->magic != Magic || state_->version != Version) {
        std::memset(state_, 0, sizeof(SharedState));
        state_->magic = Magic;
        state_->version = Version;
    }
    lastRequest_ = InterlockedCompareExchange(&state_->resultSequence, 0, 0);
    InterlockedExchange(&state_->active, 0);
    return true;
}

bool PossessionControl::ResolveControlGraph(void* worldChrMan) {
    const auto world = reinterpret_cast<uintptr_t>(worldChrMan);
    player_ = ReadValue<uintptr_t>(world + MainPlayerOffset);
    playerRoot_ = reinterpret_cast<uintptr_t>(GameDataManager::PlayerRoot.Get());
    const auto worldData = ReadValue<uintptr_t>(world + WorldDataOffset);
    actorManager_ = ReadValue<uintptr_t>(worldData);
    actorController_ = ReadValue<uintptr_t>(actorManager_ + ActorControllerOffset);
    // PlayerRoot is the engine's distinct control-ownership root, not a ChrIns.
    // The working EnemyControl implementation uses its A8/B8 links directly;
    // applying ChrIns handle/controller validation to it rejects every attach.
    return IsValidCharacter(player_) && playerRoot_ && actorManager_ && actorController_;
}

bool PossessionControl::Attach(void* worldChrMan, uintptr_t target) {
    if (active_) Release(worldChrMan);
    if (!ResolveControlGraph(worldChrMan)) {
        LOG_WARN("Possession attach rejected: control graph unavailable (player=%p root=%p actorManager=%p actorController=%p)",
            player_, playerRoot_, actorManager_, actorController_);
        return false;
    }
    if (!IsValidCharacter(target) || target == player_) {
        LOG_WARN("Possession attach rejected: invalid target %p (player=%p)", target, player_);
        return false;
    }

    originalLinkA_ = ReadValue<uintptr_t>(playerRoot_ + LinkAOffset);
    originalLinkB_ = ReadValue<uintptr_t>(playerRoot_ + LinkBOffset);
    // B8 is normally null before the first attachment. The working
    // EnemyControl routine conditionally clears it when populated, then writes
    // the selected target. Only the A8 controller link is required up front.
    if (!originalLinkA_) {
        LOG_WARN("Possession attach rejected: A8 ownership link unavailable (root=%p A8=%p B8=%p)",
            playerRoot_, originalLinkA_, originalLinkB_);
        return false;
    }
    originalLinkAChild_ = originalLinkA_ ? ReadValue<uintptr_t>(originalLinkA_ + LinkAOffset) : 0;
    const auto targetController = ReadValue<uintptr_t>(target + CharacterControllerOffset);
    originalTargetBackLink_ = targetController ? ReadValue<uintptr_t>(targetController + BackLinkOffset) : 0;

    originalActorFlags0_ = ReadValue<std::uint8_t>(actorManager_ + ActorFlags0Offset);
    originalActorFlags1_ = ReadValue<std::uint8_t>(actorManager_ + ActorFlags1Offset);
    originalActorState_ = ReadValue<std::int32_t>(actorManager_ + ActorStateOffset);
    const auto inner = ReadValue<uintptr_t>(actorController_);
    haveInnerFlags_ = inner && Read(inner + InnerControllerFlagOffset, &originalInnerFlags_, sizeof(originalInnerFlags_));
    const auto transform = ReadValue<uintptr_t>(actorController_ + ActorTransformOffset);
    haveTransformFlag_ = transform && Read(transform + TransformFlagOffset, &originalTransformFlag_, sizeof(originalTransformFlag_));

    // Do not rewrite either character's team.  Team reassignment is not
    // required for input ownership and, if a possession session is interrupted,
    // it can leave the local player outside normal aggro, damage, and lock-on
    // eligibility for the remainder of the game process.
    haveTeamState_ = false;

    if (originalLinkB_) {
        const auto oldController = ReadValue<uintptr_t>(originalLinkB_ + CharacterControllerOffset);
        if (oldController) WriteValue<uintptr_t>(oldController + BackLinkOffset, 0);
    }
    if (originalLinkA_) WriteValue<uintptr_t>(originalLinkA_ + LinkAOffset, target);
    if (targetController) WriteValue<uintptr_t>(targetController + BackLinkOffset, originalLinkA_);
    if (!WriteValue<uintptr_t>(playerRoot_ + LinkBOffset, target)) return false;

    target_ = target;
    active_ = true;
    ApplyPlayerControlOverride();
    ApplyControlFlags();
    InterlockedExchange(&state_->active, 1);
    return true;
}

void PossessionControl::ApplyPlayerControlOverride() {
    havePlayerActionFlags_ = false;
    havePlayerControllerFlags_ = false;
    playerController_ = ReadValue<uintptr_t>(player_ + CharacterControllerOffset);
    if (!playerController_ || ReadValue<uintptr_t>(playerController_ + ControllerOwnerOffset) != player_) return;

    playerControllerModifier_ = ReadValue<uintptr_t>(playerController_ + ControllerModifierOffset);
    // EldenIntel does not alter the player's persistent controller/action flags.
    // The dedicated Enemy Control module owns enemy action routing; our bridge
    // keeps only its launch-safe camera and IPC behavior.
}

void PossessionControl::RestorePlayerControlOverride() {
    if (playerControllerModifier_ && havePlayerActionFlags_)
        WriteValue<std::uint32_t>(playerControllerModifier_ + ModifierActionFlagsOffset, originalPlayerActionFlags_);
    if (playerController_ && havePlayerControllerFlags_)
        WriteValue<std::uint32_t>(playerController_ + ControllerFlagsOffset, originalPlayerControllerFlags_);
    playerController_ = playerControllerModifier_ = 0;
    havePlayerActionFlags_ = havePlayerControllerFlags_ = false;
}

void PossessionControl::ApplyControlFlags() {
    if (!active_ || !actorManager_ || !actorController_) return;
    // Exact inert-player state used by the working EnemyControl DLL. These
    // three writes prevent the physical player body from consuming the same
    // movement stream after ownership has moved to the selected character.
    // Every original value is snapshotted in Attach and restored in Release.
    WriteValue<std::uint8_t>(actorManager_ + ActorFlags0Offset,
        static_cast<std::uint8_t>(ReadValue<std::uint8_t>(actorManager_ + ActorFlags0Offset) | 0x30));
    WriteValue<std::uint8_t>(actorManager_ + ActorFlags1Offset,
        static_cast<std::uint8_t>(ReadValue<std::uint8_t>(actorManager_ + ActorFlags1Offset) & ~0x08));
    WriteValue<std::int32_t>(actorManager_ + ActorStateOffset, 5);
    const auto inner = ReadValue<uintptr_t>(actorController_);
    if (inner) WriteValue<std::uint8_t>(inner + InnerControllerFlagOffset,
        static_cast<std::uint8_t>(ReadValue<std::uint8_t>(inner + InnerControllerFlagOffset) | 1));
    const auto transform = ReadValue<uintptr_t>(actorController_ + ActorTransformOffset);
    if (transform) WriteValue<std::uint8_t>(transform + TransformFlagOffset, 1);
}

void PossessionControl::SyncPlayerTransform() {
    if (!active_) return;
    const auto sourceController = ReadValue<uintptr_t>(target_ + ActorControllerOffset);
    const auto sourceTransform = ReadValue<uintptr_t>(sourceController + ActorTransformOffset);
    const auto destinationTransform = ReadValue<uintptr_t>(actorController_ + ActorTransformOffset);
    if (!sourceTransform || !destinationTransform) return;
    float position[3]{};
    if (!Read(sourceTransform + PositionOffset, position, sizeof(position))) return;
    position[1] += PlayerVerticalOffset;
    Write(destinationTransform + PositionOffset, position, sizeof(position));
}

void PossessionControl::Release(void* worldChrMan) {
    if (!active_) { if (state_) InterlockedExchange(&state_->active, 0); return; }
    ResolveControlGraph(worldChrMan);

    if (playerRoot_) {
        const auto current = ReadValue<uintptr_t>(playerRoot_ + LinkBOffset);
        const auto controller = current ? ReadValue<uintptr_t>(current + CharacterControllerOffset) : 0;
        if (controller) WriteValue<uintptr_t>(controller + BackLinkOffset, originalTargetBackLink_);
        WriteValue<uintptr_t>(playerRoot_ + LinkBOffset, originalLinkB_);
        if (originalLinkA_) WriteValue<uintptr_t>(originalLinkA_ + LinkAOffset, originalLinkAChild_);
    }
    if (haveTeamState_) {
        if (IsValidCharacter(target_)) WriteValue<std::uint8_t>(target_ + TeamOffset, originalTargetTeam_);
        if (IsValidCharacter(player_)) WriteValue<std::uint8_t>(player_ + TeamOffset, originalPlayerTeam_);
    }
    RestorePlayerControlOverride();
    if (actorManager_) {
        WriteValue<std::uint8_t>(actorManager_ + ActorFlags0Offset, originalActorFlags0_);
        WriteValue<std::uint8_t>(actorManager_ + ActorFlags1Offset, originalActorFlags1_);
        WriteValue<std::int32_t>(actorManager_ + ActorStateOffset, originalActorState_);
    }
    const auto inner = ReadValue<uintptr_t>(actorController_);
    if (inner && haveInnerFlags_) WriteValue<std::uint8_t>(inner + InnerControllerFlagOffset, originalInnerFlags_);
    const auto transform = ReadValue<uintptr_t>(actorController_ + ActorTransformOffset);
    if (transform && haveTransformFlag_) WriteValue<std::uint8_t>(transform + TransformFlagOffset, originalTransformFlag_);

    active_ = false;
    target_ = player_ = playerRoot_ = actorManager_ = actorController_ = 0;
    haveTeamState_ = haveInnerFlags_ = haveTransformFlag_ = false;
    if (state_) InterlockedExchange(&state_->active, 0);
}

void PossessionControl::Update(void* worldChrMan) {
    if (!state_ || state_->magic != Magic || state_->version != Version || !worldChrMan) return;
    const LONG request = InterlockedCompareExchange(&state_->requestSequence, 0, 0);
    if (request && request != lastRequest_) {
        lastRequest_ = request;
        const LONG command = InterlockedCompareExchange(&state_->command, 0, 0);
        LONG result = -1;
        if (command == 1) result = Attach(worldChrMan, static_cast<uintptr_t>(state_->targetAddress)) ? 1 : -2;
        else if (command == 2) { Release(worldChrMan); result = 1; }
        InterlockedExchange(&state_->resultCode, result);
        InterlockedExchange(&state_->resultSequence, request);
    }

    if (!active_) return;
    if (!IsValidCharacter(target_) || (ReadValue<std::uint8_t>(target_ + DeathFlagOffset) & DeathFlag)) {
        Release(worldChrMan);
        return;
    }
    ApplyControlFlags();
    // Position synchronization is intentionally withheld until the corrected
    // PlayerIns ownership link is validated. A failed ownership handoff must
    // never drag or warp the local player to the selected subject.
}

void PossessionControl::Dispose(void* worldChrMan) {
    Release(worldChrMan);
    if (state_) { UnmapViewOfFile(state_); state_ = nullptr; }
    if (mapping_) { CloseHandle(mapping_); mapping_ = nullptr; }
}
