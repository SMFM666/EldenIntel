cmake_minimum_required(VERSION 3.16)

project(speedhack_example LANGUAGES CXX)

set(CMAKE_CXX_STANDARD 17)
set(CMAKE_CXX_STANDARD_REQUIRED ON)

add_library(speedhack SHARED
    dllmain.cpp
)

add_subdirectory(thirdparty/MinSpeedhack)
add_subdirectory(thirdparty/MinHook)
add_subdirectory(thirdparty/mINI)

target_link_libraries(speedhack PRIVATE
    minspeedhack
    minhook
    mINI
)

target_include_directories(speedhack PRIVATE
    ${CMAKE_CURRENT_SOURCE_DIR}
)