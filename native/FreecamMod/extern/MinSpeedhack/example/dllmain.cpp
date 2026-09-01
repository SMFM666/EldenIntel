#include <windows.h>

#include "MinSpeedhack.h"
#include "MinHook.h"

#include "config.h"

DWORD WINAPI MainThread(LPVOID lpParam) {
	// Initialize MinHook
    if (MH_Initialize() != MH_OK) return 0;

    // Install all hooks from MinSpeedhack
    size_t hookCount = 0;
    const auto* hooks = MS::GetHooks(hookCount);
    for (size_t i = 0; i < hookCount; ++i) {
        if (MH_CreateHook(hooks[i].target, hooks[i].detour, hooks[i].original) != MH_OK) {
            MH_Uninitialize();
            return 0;
        }
    }

	// Enable all created hooks
    if (MH_EnableHook(MH_ALL_HOOKS) != MH_OK) {
        MH_Uninitialize();
        return 0;
    }

	// Load config
    cfg::load((HMODULE)lpParam);

	bool isSpeedhackEnabled = false;
    // Main loop
    while (true) {
        // Reload config on reloadKey press
		if (GetAsyncKeyState(cfg::reloadKey) & 1) {
            cfg::load((HMODULE)lpParam);
            if (isSpeedhackEnabled) {
                // Set speedhack speed
                MS::SetSpeed(cfg::speed);
            }
        }

		// Toggle speedhack on toggleKey press
        if (GetAsyncKeyState(cfg::toggleKey) & 1) {
            isSpeedhackEnabled = !isSpeedhackEnabled;
            // Set speedhack speed
            MS::SetSpeed(isSpeedhackEnabled ? cfg::speed : 1.0);
        }

        Sleep(1);
	}

    return 0;
}

BOOL WINAPI DllMain(HINSTANCE module, DWORD reason, LPVOID reserved) {
    if (reason == DLL_PROCESS_ATTACH) {
        DisableThreadLibraryCalls(module);
        CreateThread(0, 0, MainThread, module, 0, NULL);
    }

    return TRUE;
}