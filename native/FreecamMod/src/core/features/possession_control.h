#pragma once

#include <cstdint>
#include <windows.h>

// Clean-room character-control bridge derived from the architecture documented
// by AlpinDale/er_enemy_control (Apache-2.0). EldenIntel owns the IPC, lifetime,
// validation, and restoration behavior in this implementation.
class PossessionControl {
public:
#pragma pack(push, 1)
    struct SharedState {
        std::uint32_t magic;
        std::uint32_t version;
        volatile LONG requestSequence;
        volatile LONG command;       // 1 = possess targetAddress, 2 = release
        volatile LONG resultSequence;
        volatile LONG resultCode;
        volatile LONG active;
        volatile LONG reserved;
        std::uint64_t targetAddress;
    };
#pragma pack(pop)

    bool Initialize();
    void Update(void* worldChrMan);
    void Dispose(void* worldChrMan);
    bool IsActive() const { return active_; }

private:
    static constexpr std::uint32_t Magic = 0x53534F50; // "POSS"
    static constexpr std::uint32_t Version = 1;
    static constexpr wchar_t MappingName[] = L"Local\\EldenIntelPossessionControlV1";

    HANDLE mapping_{};
    SharedState* state_{};
    LONG lastRequest_{};
    bool active_{};
    uintptr_t target_{};
    uintptr_t player_{};
    uintptr_t playerRoot_{};
    uintptr_t actorManager_{};
    uintptr_t actorController_{};

    uintptr_t originalLinkA_{};
    uintptr_t originalLinkB_{};
    uintptr_t originalLinkAChild_{};
    uintptr_t originalTargetBackLink_{};
    std::uint8_t originalActorFlags0_{};
    std::uint8_t originalActorFlags1_{};
    std::uint8_t originalInnerFlags_{};
    std::uint8_t originalTransformFlag_{};
    std::int32_t originalActorState_{};
    bool haveInnerFlags_{};
    bool haveTransformFlag_{};
    uintptr_t playerController_{};
    uintptr_t playerControllerModifier_{};
    std::uint32_t originalPlayerActionFlags_{};
    std::uint32_t originalPlayerControllerFlags_{};
    bool havePlayerActionFlags_{};
    bool havePlayerControllerFlags_{};

    std::uint8_t originalPlayerTeam_{};
    std::uint8_t originalTargetTeam_{};
    bool haveTeamState_{};

    bool Attach(void* worldChrMan, uintptr_t target);
    void Release(void* worldChrMan);
    bool ResolveControlGraph(void* worldChrMan);
    void ApplyControlFlags();
    void ApplyPlayerControlOverride();
    void RestorePlayerControlOverride();
    void SyncPlayerTransform();
    static bool IsValidCharacter(uintptr_t character);
    static bool Read(uintptr_t address, void* value, size_t size);
    static bool Write(uintptr_t address, const void* value, size_t size);
    template <typename T> static T ReadValue(uintptr_t address, T fallback = {}) {
        T value{};
        return Read(address, &value, sizeof(value)) ? value : fallback;
    }
    template <typename T> static bool WriteValue(uintptr_t address, const T& value) {
        return Write(address, &value, sizeof(value));
    }
};
