#pragma once
#include "core/game_data/game_data.h"

#include "ModUtils.h"

#include "utils/memory.h"
#include "utils/debug.h"

class GameDataManager {
	template<typename T>
	struct PatternEntry {
		int offset;
		bool resolveRip;
		bool required;
		const char* name;
		const char* pattern;

		uintptr_t address{};

		bool Scan() {
			address = Signature(pattern).Scan().Add(offset).Rip(resolveRip).As<uintptr_t>();
			if (!address) {
				if (required) {
					LOG_ERROR("Failed to find %s", name);
				}
				else {
					LOG_WARN("Failed to find %s. Some features may not work", name);
				}
				return false;
			}
			LOG_INFO("Found %s: %p", name, address);
			return true;
		}

		T Get() const {	
			if constexpr (std::is_pointer_v<T>) {
				uintptr_t ptr = Memory::RPM<uintptr_t>(address);
				return ptr ? reinterpret_cast<T>(ptr) : nullptr;
			}
			else if constexpr (std::is_same_v<T, uintptr_t>) {
				return address;
			}
			else {
				return static_cast<T>(address);
			}
		}

		void* operator &() const {
			return reinterpret_cast<void*>(address);
		}
	};

public:
	static bool Init() {
		LOG_INFO("Initializing GameDataManager...");

		if (!FieldArea.Scan()) return false;
		if (!WorldChrMan.Scan()) return false;
		PlayerRoot.Scan();
		if (!GameDataMan.Scan()) return false;
		if (!Window.Scan()) return false;
		if (!FrametimeLimit.Scan()) return false;
		if (!FullscreenLimit.Scan()) return false;
		if (!GamePausePatch.Scan()) return false;
		if (!UpdateCameraMatrixFunc.Scan()) return false;
		if (!DaytimeUpdateFunc.Scan()) return false;
		WorldBackReadVtable.Scan();
		PlayGestureFunc.Scan();
		ApplyEffectFunc.Scan();
		CopyPlayerCharacterDataFunc.Scan();
		AddInventoryFromShopFunc.Scan();
		CSRegulationManager.Scan();
		FollowCameraRead.Scan();
		CameraPositionWrite.Scan();

		return true;
	}

#define REQUIRED true
#define RESOLVE_RIP true
#define NOT_REQUIRED false
#define NOT_RESOLVE_RIP false

	static inline PatternEntry<GameData::FieldArea*> FieldArea { 3, RESOLVE_RIP, REQUIRED, "FieldArea", 
		"48 8B 3D ? ? ? ? 49 8B D8 48 8B F2 4C 8B F1 48 85 FF" };
	static inline PatternEntry<GameData::WorldChrMan*> WorldChrMan { 3, RESOLVE_RIP, REQUIRED, "WorldChrMan", 
		"48 8B 05 ? ? ? ? 48 85 C0 74 0F 48 39 88" };
	// Distinct global PlayerIns root used by Elden Ring's native character
	// control links. This is not interchangeable with WorldChrMan::mainPlayer.
	static inline PatternEntry<GameData::ChrIns*> PlayerRoot { 3, RESOLVE_RIP, NOT_REQUIRED, "PlayerRoot",
		"48 8B 0D ? ? ? ? 89 5C 24 20 48 85 C9 74 12 B8 ? ? ? ? 8B D8" };
	static inline PatternEntry<GameData::GameDataMan*> GameDataMan { 3, RESOLVE_RIP, NOT_REQUIRED, "GameDataMan", 
		"48 8B 05 ? ? ? ? 48 85 C0 74 05 48 8B 40 58 C3 C3" };
	static inline PatternEntry<GameData::Window*> Window { 3, RESOLVE_RIP, NOT_REQUIRED, "Window",
		"48 8B 0D ? ? ? ? 48 85 C9 74 ? 48 83 C1 ? 48 8D 45" };
	static inline PatternEntry<uintptr_t> FrametimeLimit { 3, NOT_RESOLVE_RIP, NOT_REQUIRED, "FrametimeLimit", 
		"C7 ? ? ? ? ? ? EB ? 89 ? 18 EB ? 89 ? 18 C7" };
	static inline PatternEntry<uintptr_t> FullscreenLimit { 0, NOT_RESOLVE_RIP, NOT_REQUIRED, "FullscreenLimit", 
		"C7 ? EF ? 00 00 00 C7 ? F3 01 00 00 00 8B 87" };
	static inline PatternEntry<uintptr_t> GamePausePatch { 1, NOT_RESOLVE_RIP, NOT_REQUIRED, "GamePausePatch", 
		"0F 84 ? ? ? ? C6 ? ? ? ? ? 00 ? 8D ? ? ? ? ? ? 89 ? ? 89 ? ? ? 8B ? ? ? ? ? ? 85 ? 75" };
	static inline PatternEntry<void*> UpdateCameraMatrixFunc { 0, NOT_RESOLVE_RIP, REQUIRED, "UpdateCameraMatrixFunc", 
		"4C 8B 49 18 4C 8B D1 8B 42 50 41 89 41 50 8B 42" };
	static inline PatternEntry<uintptr_t> DaytimeUpdateFunc { 0, NOT_RESOLVE_RIP, NOT_REQUIRED, "DaytimeUpdateFunc", 
		"F3 0F 2C D0 85 D2 7E" };
	static inline PatternEntry<uintptr_t> WorldBackReadVtable { 3, RESOLVE_RIP, NOT_REQUIRED, "WorldBackReadVtable",
		"48 8D 05 ? ? ? ? 48 89 01 48 89 51 08 0F 28 05 ? ? ? ? 0F 11 41 10" };
	static inline PatternEntry<uintptr_t> PlayGestureFunc { 0, NOT_RESOLVE_RIP, NOT_REQUIRED, "PlayGestureFunc",
		"40 57 48 83 EC 20 48 8B 05 ? ? ? ? 48 8B F9 48 85 C0 75 2E 48 8D 0D" };
	// Resolve the exact ChrIns::ApplyEffect call used by Transmogrify's
	// players::apply_speffect wrapper: (ChrIns*, unsigned int, bool) -> int.
	static inline PatternEntry<uintptr_t> ApplyEffectFunc { 12, RESOLVE_RIP, NOT_REQUIRED, "ApplyEffectFunc",
		"45 33 C0 BA AE 10 00 00 48 8B CF E8 ? ? ? ? EB 16 E8 ? ? ? ? 84 C0 74 0D BA AE 10 00 00 48 8B CF E8 ? ? ? ?" };
	static inline PatternEntry<uintptr_t> CopyPlayerCharacterDataFunc { -216, NOT_RESOLVE_RIP, NOT_REQUIRED, "CopyPlayerCharacterDataFunc",
		"C7 44 24 30 00 00 00 00 48 8D 54 24 28 48 8B 8B 80 05 00 00 E8 ? ? ? ?" };
	// Function entry used by Transmogrify's shop-inventory detour. Calling the
	// hooked entry preserves Transmogrify as the sole owner of player appearance.
	static inline PatternEntry<uintptr_t> AddInventoryFromShopFunc { -173, NOT_RESOLVE_RIP, NOT_REQUIRED, "AddInventoryFromShopFunc",
		"E8 ? ? ? ? 84 C0 74 D8 B0 01" };
	static inline PatternEntry<void*> CSRegulationManager { 3, RESOLVE_RIP, NOT_REQUIRED, "CSRegulationManager",
		"48 8B 0D ? ? ? ? 48 85 C9 74 0B 4C 8B C0 48 8B D7" };
	static inline PatternEntry<uintptr_t> FollowCameraRead { 0, NOT_RESOLVE_RIP, NOT_REQUIRED, "FollowCameraRead",
		"F3 0F 10 93 60 01 00 00 F3 0F 10 9B 50 01 00 00" };
	static inline PatternEntry<uintptr_t> CameraPositionWrite { 0, NOT_RESOLVE_RIP, NOT_REQUIRED, "CameraPositionWrite",
		"0F 29 37 80 BB 15 03 00 00 00 74 ? 0F 57 C9" };

	static GameData::ChrIns* GetPlayer() {
		GameData::WorldChrMan* world = WorldChrMan.Get();
		if (!world) return nullptr;

		GameData::Players* players = world->players;
		if (!players) return nullptr;

		return players->player0;
	}

	static GameData::OptionData* GetOptionData() {
		GameData::GameDataMan* gameDataMan = GameDataMan.Get();
		if (!gameDataMan) return nullptr;

		return gameDataMan->optionData;
	}

	static void PauseGame(bool enabled) {
		static bool isPaused = false;

		if (!GamePausePatch.address) return;
		if (isPaused == enabled) return;

		if (enabled) {
			ModUtils::ReplaceExpectedBytesAtAddress(GamePausePatch.address, "84", "85");
		}
		else {
			ModUtils::ReplaceExpectedBytesAtAddress(GamePausePatch.address, "85", "84");
		}

		isPaused = enabled;
	}
};
