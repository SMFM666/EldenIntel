#include "MinSpeedhack.h"
#include <intrin.h>

namespace MS {
    static double speed = 1.0;

    static DWORD base32 = 0, last32 = 0;
    static ULONGLONG base64 = 0, last64 = 0;
    static LARGE_INTEGER baseQpc{}, lastQpc{};

    static double elapsed32 = 0;
    static double elapsed64 = 0;
    static double elapsedQpc = 0;

    static DWORD(WINAPI* origGetTickCount)();
    static ULONGLONG(WINAPI* origGetTickCount64)();
    static BOOL(WINAPI* origQPC)(LARGE_INTEGER*);

    static constexpr size_t MAX_MODULES = 16;
    static constexpr size_t MAX_CACHE = 32;

    static size_t excludedCount = 0;
    static HMODULE excludedModules[MAX_MODULES]{};

    static size_t includedCount = 0;
    static HMODULE includedModules[MAX_MODULES]{};

    struct CallerCache {
        void* address;
        bool shouldHack;
    };

    static size_t cacheCount = 0;
    static CallerCache cache[MAX_CACHE]{};

    void ExcludeModule(HMODULE module) {
        if (excludedCount < MAX_MODULES)
            excludedModules[excludedCount++] = module;
    }

    void IncludeModule(HMODULE module) {
        if (includedCount < MAX_MODULES)
            includedModules[includedCount++] = module;
    }

    bool ShouldSpeedhack(void* address) {
        if (!excludedCount && !includedCount) return true;

        for (size_t i = 0; i < cacheCount; ++i) {
            if (cache[i].address == address) {
                return cache[i].shouldHack;
            }
        }

        HMODULE callerModule;
        constexpr DWORD flags = GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS | GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT;
        if (!GetModuleHandleExW(flags, reinterpret_cast<LPCWSTR>(address), &callerModule)) return false;

        bool result = false;
        if (includedCount) {
            for (size_t i = 0; i < includedCount; ++i) {
                if (includedModules[i] == callerModule) {
                    result = true;
                    break;
                }
            }
        }
        else {
            result = true;
            for (size_t i = 0; i < excludedCount; ++i) {
                if (excludedModules[i] == callerModule) {
                    result = false;
                    break;
                }
            }
        }

        if (cacheCount < MAX_CACHE) {
            cache[cacheCount++] = { address, result };
        }

        return result;
    }

    static DWORD WINAPI hkGetTickCount() {
        const DWORD now = origGetTickCount();
        if (!ShouldSpeedhack(_ReturnAddress())) return now;

        if (!base32) {
            base32 = now;
            last32 = now;
            return now;
        }

        elapsed32 += (now - last32) * speed;
        last32 = now;

        return base32 + static_cast<DWORD>(elapsed32);
    }

    static ULONGLONG WINAPI hkGetTickCount64() {
        const ULONGLONG now = origGetTickCount64();
        if (!ShouldSpeedhack(_ReturnAddress())) return now;

        if (!base64) {
            base64 = now;
            last64 = now;
            return now;
        }

        elapsed64 += (now - last64) * speed;
        last64 = now;

        return base64 + static_cast<ULONGLONG>(elapsed64);
    }

    static BOOL WINAPI hkQueryPerformanceCounter(LARGE_INTEGER* lp) {
        LARGE_INTEGER now;
        const BOOL result = origQPC(&now);
        if (!ShouldSpeedhack(_ReturnAddress())) {
            *lp = now;
            return result;
        }

        if (!baseQpc.QuadPart) {
            baseQpc = now;
            lastQpc = now;
            *lp = now;
            return result;
        }

        elapsedQpc += (now.QuadPart - lastQpc.QuadPart) * speed;
        lastQpc = now;

        lp->QuadPart = baseQpc.QuadPart + static_cast<LONGLONG>(elapsedQpc);

        return result;
    }

    static Hook hooks[] = {
        { (void*)&GetTickCount, (void*)&hkGetTickCount, (void**)&origGetTickCount },
        { (void*)&GetTickCount64, (void*)&hkGetTickCount64, (void**)&origGetTickCount64 },
        { (void*)&QueryPerformanceCounter, (void*)&hkQueryPerformanceCounter, (void**)&origQPC },
    };

    void SetSpeed(double value) {
        speed = (value > 0.00001) ? value : 0.00001;
    }

    double GetSpeed() {
        return speed;
    }

    const Hook* GetHooks(size_t& count) {
        count = _countof(hooks);
        return hooks;
    }
}