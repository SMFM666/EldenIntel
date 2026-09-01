#include "core/hook/code_cave.h"
#include "utils/memory.h"

namespace CodeCaveAsm {
    namespace DaytimeUpdateAsm {
        extern "C" {
            void DaytimeUpdateFunc();
            uintptr_t returnAddress = 0;
            uintptr_t funcAddress = 0;
            bool freeze_time_day = 0;
            bool set_morning = 0;
            // A filming-friendly sky cycle. The upstream value (1,000,000)
            // races through an entire lighting change too quickly to frame.
            int cycle_speed = 75000;
        }
    }
    namespace FollowCameraAsm {
        extern "C" {
            void FollowCameraCapture();
            uintptr_t followCameraReturnAddress = 0;
            void* capturedFollowCamera = nullptr;
        }
    }
    namespace CameraPositionAsm {
        extern "C" {
            void CameraPositionCapture();
            uintptr_t cameraPositionReturnAddress = 0;
            uintptr_t cameraPositionZeroTarget = 0;
            void* capturedCameraPosition = nullptr;
            bool freezeCameraPosition = false;
        }
    }
}

bool CodeCave::Hook(uintptr_t hkAddress) {
    if (!hkAddress) return false;
    hookAddress = hkAddress;

    SaveOriginalBytes(hookAddress);
    ModUtils::Hook(hookAddress, GetDestinationAddress(), caveSize - 14);
    return true;
}

void CodeCave::Unhook() {
    if (hookAddress) {
        ModUtils::ToggleMemoryProtection(false, hookAddress, caveSize);
        memcpy((void*)hookAddress, originalBytes, caveSize);
        ModUtils::ToggleMemoryProtection(true, hookAddress, caveSize);

        FlushInstructionCache(GetCurrentProcess(), (void*)hookAddress, caveSize);

        if (originalBytes) {
            delete[] originalBytes;
            originalBytes = nullptr;
        }

        hookAddress = 0;
    }
}

void DaytimeUpdateCave::SaveOriginalBytes(uintptr_t hkAddress) {
    using namespace CodeCaveAsm;

    caveSize = 16;

    originalBytes = new uint8_t[caveSize];
    ModUtils::ToggleMemoryProtection(false, hkAddress, caveSize);
    memcpy(originalBytes, (void*)hkAddress, caveSize);
    ModUtils::ToggleMemoryProtection(true, hkAddress, caveSize);

    uintptr_t callInstruction = hkAddress + 11;
    DaytimeUpdateAsm::funcAddress = Memory::GetCallTargetAddress(callInstruction);
    DaytimeUpdateAsm::returnAddress = hkAddress + caveSize;

    isDayTimeFrozen = &DaytimeUpdateAsm::freeze_time_day;
    isCycleWeatherTime = &DaytimeUpdateAsm::set_morning;
    cycleSpeed = &DaytimeUpdateAsm::cycle_speed;
}

uintptr_t DaytimeUpdateCave::GetDestinationAddress() {
    return (uintptr_t)&CodeCaveAsm::DaytimeUpdateAsm::DaytimeUpdateFunc;
}

void FollowCameraCave::SaveOriginalBytes(uintptr_t hkAddress) {
    caveSize = 16; // Two complete 8-byte movss instructions.
    originalBytes = new uint8_t[caveSize];
    ModUtils::ToggleMemoryProtection(false, hkAddress, caveSize);
    memcpy(originalBytes, reinterpret_cast<void*>(hkAddress), caveSize);
    ModUtils::ToggleMemoryProtection(true, hkAddress, caveSize);
    CodeCaveAsm::FollowCameraAsm::followCameraReturnAddress = hkAddress + caveSize;
}

uintptr_t FollowCameraCave::GetDestinationAddress() {
    return reinterpret_cast<uintptr_t>(&CodeCaveAsm::FollowCameraAsm::FollowCameraCapture);
}

void* FollowCameraCave::GetCamera() const {
    return CodeCaveAsm::FollowCameraAsm::capturedFollowCamera;
}

void CameraPositionCave::SaveOriginalBytes(uintptr_t hkAddress) {
    // movaps [rdi],xmm6 + cmp + short je + xorps = 15 bytes. The cave
    // reconstructs the conditional branch explicitly.
    caveSize = 15;
    originalBytes = new uint8_t[caveSize];
    ModUtils::ToggleMemoryProtection(false, hkAddress, caveSize);
    memcpy(originalBytes, reinterpret_cast<void*>(hkAddress), caveSize);
    ModUtils::ToggleMemoryProtection(true, hkAddress, caveSize);
    CodeCaveAsm::CameraPositionAsm::cameraPositionReturnAddress = hkAddress + caveSize;
    const std::int8_t displacement = *reinterpret_cast<std::int8_t*>(hkAddress + 11);
    CodeCaveAsm::CameraPositionAsm::cameraPositionZeroTarget = hkAddress + 12 + displacement;
    freezePosition = &CodeCaveAsm::CameraPositionAsm::freezeCameraPosition;
}

uintptr_t CameraPositionCave::GetDestinationAddress() {
    return reinterpret_cast<uintptr_t>(&CodeCaveAsm::CameraPositionAsm::CameraPositionCapture);
}

void* CameraPositionCave::GetCameraPosition() const {
    return CodeCaveAsm::CameraPositionAsm::capturedCameraPosition;
}

void CameraPositionCave::SetFrozen(bool frozen) {
    if (freezePosition) *freezePosition = frozen;
}
