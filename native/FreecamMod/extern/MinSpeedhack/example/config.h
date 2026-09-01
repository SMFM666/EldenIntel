#pragma once

#include <windows.h>
#include <filesystem>
#include <string>
#include "mini/ini.h"

namespace cfg {
    inline std::filesystem::path findDllPath(HMODULE hModule) {
        char path[MAX_PATH];
        GetModuleFileNameA(hModule, path, sizeof(path));

        std::filesystem::path p(path);
        return p.parent_path();
    }

    inline std::filesystem::path configPath(HMODULE hModule) {
        return findDllPath(hModule) / "config.ini";
    }

    inline float speed = 0.5f;
    inline int toggleKey = VK_F1;
    inline int reloadKey = VK_F5;

    inline void load(HMODULE hModule) {
        mINI::INIFile file(configPath(hModule).string());
        mINI::INIStructure ini;

        if (!file.read(ini)) {
            ini["speedhack"]["speed"] = std::to_string(speed);
            ini["keybind"]["toggle_speedhack"] = std::to_string(toggleKey);
            ini["keybind"]["reload_config"] = std::to_string(reloadKey);
            file.write(ini, true);
        }

        speed = std::stof(ini["speedhack"]["speed"]);
        toggleKey = std::stoi(ini["keybind"]["toggle_speedhack"]);
        reloadKey = std::stoi(ini["keybind"]["reload_config"]);
    }
}