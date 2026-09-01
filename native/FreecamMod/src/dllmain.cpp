#include <windows.h>

#include "freecam.h"

DWORD WINAPI MainThread(LPVOID lpParam) {
    Sleep(500);

    // Allocate explicitly so process shutdown can intentionally leave the
    // already-pass-through hooks resident. Removing MinHook detours while
    // Elden Ring is terminating suspends its threads and can substantially
    // delay or deadlock exit. Windows will reclaim this allocation/module with
    // the process. Explicit Exit Mod still follows the full cleanup path.
    Freecam* freecam = new Freecam((HMODULE)lpParam);
    freecam->Run();
    if (Freecam::IsGameShuttingDown()) {
        return 0;
    }
    freecam->Dispose();
    delete freecam;

    Sleep(500);
    FreeLibraryAndExitThread((HMODULE)lpParam, 0);
    return 0;
}

BOOL WINAPI DllMain(HINSTANCE module, DWORD reason, LPVOID reserved) {
    if (reason == DLL_PROCESS_ATTACH) {
        DisableThreadLibraryCalls(module);
        CreateThread(0, 0, MainThread, module, 0, NULL);
    }
    // Never unhook or join worker-owned state from DLL_PROCESS_DETACH. During
    // normal game shutdown this callback runs under the Windows loader lock
    // and can race MainThread's explicit Dispose(), producing a shutdown-only
    // access violation. Explicit mod unload is already handled by MainThread;
    // process termination safely reclaims the remaining module resources.
    return TRUE;
}
