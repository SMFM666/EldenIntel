#include "core/input/input.h"

#include <iostream>

#include "utils/debug.h"
#include "utils/types.h"

Input::Input() {
    instance = this;
    InterlockedExchange(&shutdownStarted, 0);
}

LRESULT __stdcall Input::hkWndProc(HWND hWnd, UINT uMsg, WPARAM wParam, LPARAM lParam) {
    if (!instance) {
        return CallWindowProcW((WNDPROC)Input::origWndProc, hWnd, uMsg, wParam, lParam);
    }

    // Signal teardown before Elden Ring begins destroying camera/world state.
    // The callback only flips atomic state and releases camera ownership; the
    // worker thread performs the actual unhook outside this WndProc callback.
    // Elden Ring's in-game Quit flow can destroy the render window without
    // delivering WM_CLOSE to this subclass. WM_DESTROY/WM_NCDESTROY are the
    // last reliable notifications before the camera/world objects disappear.
    // Signal on both; OnGameShutdown is intentionally idempotent.
    if ((uMsg == WM_CLOSE || uMsg == WM_DESTROY || uMsg == WM_NCDESTROY ||
         uMsg == WM_QUERYENDSESSION ||
         (uMsg == WM_ENDSESSION && wParam != 0)) && shutdownCallback) {
        shutdownCallback();
    }

    // WM_NCDESTROY is the final message for this window. Restore Elden Ring's
    // original procedure before forwarding it so no mod callback remains in
    // the window teardown chain after the native render window is destroyed.
    if (uMsg == WM_NCDESTROY) {
        const LONG_PTR original = origWndProc;
        if (original) {
            SetWindowLongPtrW(hWnd, GWLP_WNDPROC, original);
            origWndProc = 0;
        }
        return CallWindowProcW((WNDPROC)original, hWnd, uMsg, wParam, lParam);
    }

    if (InterlockedCompareExchange(&shutdownStarted, 0, 0) != 0) {
        return CallWindowProcW((WNDPROC)origWndProc, hWnd, uMsg, wParam, lParam);
    }

    instance->UpdateKeyboard(hWnd, uMsg, wParam, lParam);

    const auto isCameraKey = [](WPARAM key) {
        return key == 'W' || key == 'A' || key == 'S' || key == 'D' ||
            key == 'Q' || key == 'E' || key == VK_SPACE || key == VK_SHIFT;
    };
    // Capture camera-bound keyboard input locally, but do not forward it into
    // Elden Ring's character action map when the camera owns the keyboard.
    if (instance->suppressFreecamKeyboard &&
        isCameraKey(wParam) &&
        (uMsg == WM_KEYDOWN || uMsg == WM_KEYUP ||
         uMsg == WM_SYSKEYDOWN || uMsg == WM_SYSKEYUP)) {
        return 0;
    }

    return CallWindowProcW((WNDPROC)Input::origWndProc, hWnd, uMsg, wParam, lParam);
}

UINT WINAPI Input::hkGetRawInputData(HRAWINPUT hRawInput, UINT uiCommand, LPVOID pData, PUINT pcbSize, UINT cbSizeHeader) {
    UINT orig = origGetRawInputData(hRawInput, uiCommand, pData, pcbSize, cbSizeHeader);

    // The MinHook detour intentionally remains installed during process exit,
    // but must become a pure pass-through before game-owned input state is
    // dismantled.
    if (InterlockedCompareExchange(&shutdownStarted, 0, 0) != 0) return orig;

    if (instance && instance->isShouldGetInput) {
        if (orig != (UINT)-1 && uiCommand == RID_INPUT && pData) {
            RAWINPUT* raw = (RAWINPUT*)pData;

            if (raw->header.dwType == RIM_TYPEMOUSE) {
                instance->mouseDelta.x += raw->data.mouse.lLastX;
                instance->mouseDelta.y += raw->data.mouse.lLastY;
                if (instance->suppressFreecamMouse) {
                    raw->data.mouse.lLastX = 0;
                    raw->data.mouse.lLastY = 0;
                }
            }
            else if (instance->suppressFreecamKeyboard &&
                     raw->header.dwType == RIM_TYPEKEYBOARD &&
                     (raw->data.keyboard.VKey == 'W' || raw->data.keyboard.VKey == 'A' ||
                      raw->data.keyboard.VKey == 'S' || raw->data.keyboard.VKey == 'D' ||
                      raw->data.keyboard.VKey == 'Q' || raw->data.keyboard.VKey == 'E' ||
                      raw->data.keyboard.VKey == VK_SPACE || raw->data.keyboard.VKey == VK_SHIFT)) {
                // Elden Ring also consumes raw keyboard input. Neutralize the
                // packet after our WndProc state has captured it for freecam.
                raw->data.keyboard.VKey = 0;
                raw->data.keyboard.MakeCode = 0;
            }
        }
    }
    
    return orig;
}

bool Input::HookWndProc(HWND hWnd) {
    LOG_INFO("Hooking WndProc...");
    if (!hWnd) {
        LOG_ERROR("Failed to hook WndProc: Invalid window handle %p", hWnd);
        return false;
    }

    origWndProc = SetWindowLongPtr(hWnd, GWLP_WNDPROC, (LONG_PTR)Input::hkWndProc);
    return true;
}

void Input::UnhookWndProc(HWND hWnd) {
    LOG_INFO("Unhooking WndProc...");
    if (!origWndProc || !hWnd) {
        LOG_WARN("Failed to unhook WndProc: No original WndProc or invalid window handle");
        return;
    }

    SetWindowLongPtrW(hWnd, GWLP_WNDPROC, origWndProc);
}

void Input::UpdateGamepad() {
    prevState = state;

    ZeroMemory(&state, sizeof(XINPUT_STATE));
    if (XInputGetState(0, &state) != ERROR_SUCCESS) {
        thumbLeft = float2{};
        thumbRight = float2{};
        leftTrigger = 0.0f;
        rightTrigger = 0.0f;
        return;
    }

    auto normalizeTrigger = [](BYTE trigger) -> float { return trigger / 255.0f; };
    auto normalizeStick = [](float2 stick) -> float2 {
        stick = stick / 32767.0f;

        const float deadzone = 0.1f;
        const float lenght = stick.length();
        if (lenght < deadzone) return float2(0);

        const float scale = (lenght - deadzone) / (lenght * (1.0f - deadzone));
        return stick * scale;
    };

    thumbLeft = normalizeStick(float2(state.Gamepad.sThumbLX, state.Gamepad.sThumbLY));
    thumbRight = normalizeStick(float2(state.Gamepad.sThumbRX, -state.Gamepad.sThumbRY));
    leftTrigger = normalizeTrigger(state.Gamepad.bLeftTrigger);
    rightTrigger = normalizeTrigger(state.Gamepad.bRightTrigger);
}

void Input::UpdateKeyboard(HWND hWnd, UINT uMsg, WPARAM wParam, LPARAM lParam) {
    int key = -1;
    switch (uMsg) {
        case WM_KILLFOCUS:
            OnWindowFocus(false, 0);
            return;

        case WM_ACTIVATEAPP:
            OnWindowFocus(wParam != 0, wParam);
            return;

        case WM_SETCURSOR:
            isShouldGetInput = !IsCursorVisible() && isWindowFocused;
            break;

        case WM_KEYDOWN:
        case WM_SYSKEYDOWN:
        case WM_KEYUP:
        case WM_SYSKEYUP:
            key = (int)wParam;
            break;

        case WM_LBUTTONDOWN: case WM_LBUTTONUP: key = VK_LBUTTON; break;
        case WM_RBUTTONDOWN: case WM_RBUTTONUP: key = VK_RBUTTON; break;
        case WM_MBUTTONDOWN: case WM_MBUTTONUP: key = VK_MBUTTON; break;
        case WM_XBUTTONDOWN: case WM_XBUTTONUP:
            key = (HIWORD(wParam) == XBUTTON1) ? VK_XBUTTON1 : VK_XBUTTON2;
            break;

        case WM_MOUSEWHEEL:
            scrollDelta += (float)GET_WHEEL_DELTA_WPARAM(wParam) / WHEEL_DELTA;
            return;
    }

    if (key == -1 || key >= 256) return;

    bool isDown = (uMsg == WM_KEYDOWN || uMsg == WM_SYSKEYDOWN ||
        uMsg == WM_LBUTTONDOWN || uMsg == WM_RBUTTONDOWN ||
        uMsg == WM_MBUTTONDOWN || uMsg == WM_XBUTTONDOWN);

    keyStates[key].Update(isDown);
}

void Input::OnWindowFocus(bool getFocused, WPARAM wParam) {
    isWindowFocused = getFocused;
    isWindowJustFocused = getFocused;
    isShouldGetInput = getFocused && !IsCursorVisible();

    if (!getFocused) {
        Reset();
        for (KeyState& key : keyStates) {
            key.down = false;
        }
        ZeroMemory(&state, sizeof(state));
        ZeroMemory(&prevState, sizeof(prevState));
        thumbLeft = float2{};
        thumbRight = float2{};
        leftTrigger = 0.0f;
        rightTrigger = 0.0f;
    }
}

bool Input::IsCursorVisible() {
    CURSORINFO ci = {};
    ci.cbSize = sizeof(ci);
    return GetCursorInfo(&ci) && (ci.flags & CURSOR_SHOWING);
}

void Input::Reset() {
    for (KeyState& key : keyStates) {
        key.pressed = false;
        key.released = false;
    }
    scrollDelta = 0.0f;
    mouseDelta = 0;
    isWindowJustFocused = false;
}

Input::ReleasedNumkeys Input::GetReleasedNumkeys() {
    bool isAnyPressed = false;
    for (int key = 0; key < NUM_KEYS_COUNT; ++key) {
        int keyCode = key + (int)'0';

        if (IsReleased(keyCode)) {
            numRowKeys[key].wasRecentlyReleased = true;
        }

        if (IsPressed(keyCode)) {
            isAnyPressed = true;
            numRowKeys[key].wasRecentlyReleased = false;

            if (IsJustPressed(keyCode)) {
                numRowKeys[key].pressId = ++id;
            }
        }
    }

    if (isAnyPressed || id == 0) return {};

    ReleasedNumkeys result{};
    for (int key = 0; key < NUM_KEYS_COUNT; ++key) {
        if (numRowKeys[key].wasRecentlyReleased) {
            numRowKeys[key].wasRecentlyReleased = false;
            result.push_back(key);
        }
    }

    if (!result.empty()) {
        std::sort(result.begin(), result.end(), [this](uint8_t a, uint8_t b) {
                return numRowKeys[a].pressId < numRowKeys[b].pressId;
            }
        );
    }

    id = 0;

    return result;
}
