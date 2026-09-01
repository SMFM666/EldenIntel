#pragma once
#include <windows.h>

namespace MS {
    struct Hook {
        void* target;
        void* detour;
        void** original;
    };

    void IncludeModule(HMODULE module);
    void ExcludeModule(HMODULE module);

    void SetSpeed(double value);
    double GetSpeed();

    const Hook* GetHooks(size_t& count);
}