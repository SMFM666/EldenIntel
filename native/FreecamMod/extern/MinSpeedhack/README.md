# MinSpeedhack

Small library for controlling time scale in a target process via function hooking.

## Requirements
- Hooking backend (e.g. MinHook)
- CMake 3.16+
  
## Usage (CMake)
```cmake
add_subdirectory(minspeedhack)
target_link_libraries(project PRIVATE minspeedhack)
```

## Example
You can find an example of a small speedhack program using MinSpeedhack in `example/`.

```cpp
    // Install all hooks from MinSpeedhack
    // You can use any hooking library instead of MinHook
    size_t hookCount = 0;
    const auto* hooks = MS::GetHooks(hookCount);
    for (size_t i = 0; i < hookCount; ++i) {
        if (MH_CreateHook(hooks[i].target, hooks[i].detour, hooks[i].original) != MH_OK) {
            MH_Uninitialize();
            return 0;
        }
    }

    // Set speedhack speed
    MS::SetSpeed(1.0);

    // Get speedhack speed
    double speed = MS::GetSpeed();

    // By default, all modules are affected by the speedhack.

    // Whitelist specific modules - once any module is included,
    // only included modules will be affected (excludelist is ignored).
    MS::IncludeModule(GetModuleHandleW(L"eldenring.exe"));

    // Blacklist specific modules - only used when no modules are included.
    MS::ExcludeModule(GetModuleHandleW(L"graphics-hook64.dll")); // e.g. exclude OBS game capture
```

## Build
Build static with `DMINSPEEDHACK_SHARED=OFF`. Build dll with `DMINSPEEDHACK_SHARED=ON`.
```bash
cmake -S . -B build -DMINSPEEDHACK_SHARED=OFF -DCMAKE_BUILD_TYPE=Release
cmake --build build --config Release
```
