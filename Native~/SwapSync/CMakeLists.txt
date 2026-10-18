cmake_minimum_required(VERSION 3.20)
project(UniCAVESwapSync LANGUAGES CXX)

set(NVAPI_DIR "" CACHE PATH "NVAPI SDK root (https://github.com/NVIDIA/nvapi)")
set(UNITY_PLUGIN_API_DIR "C:/Program Files/Unity/Hub/Editor/6000.3.11f1/Editor/Data/PluginAPI" CACHE PATH "Unity Editor/Data/PluginAPI folder")

if(NOT EXISTS "${NVAPI_DIR}/nvapi.h")
    message(FATAL_ERROR "Set NVAPI_DIR to the NVAPI SDK root (nvapi.h and amd64/nvapi64.lib).")
endif()
if(NOT EXISTS "${UNITY_PLUGIN_API_DIR}/IUnityGraphicsD3D12.h")
    message(FATAL_ERROR "Set UNITY_PLUGIN_API_DIR to <Unity install>/Editor/Data/PluginAPI.")
endif()

add_library(UniCAVESwapSync SHARED UniCAVESwapSync.cpp)
target_compile_features(UniCAVESwapSync PRIVATE cxx_std_17)
target_compile_definitions(UniCAVESwapSync PRIVATE WIN32_LEAN_AND_MEAN NOMINMAX)
target_include_directories(UniCAVESwapSync PRIVATE "${NVAPI_DIR}" "${UNITY_PLUGIN_API_DIR}")
target_link_libraries(UniCAVESwapSync PRIVATE "${NVAPI_DIR}/amd64/nvapi64.lib" d3d12 dxgi user32)
set_property(TARGET UniCAVESwapSync PROPERTY MSVC_RUNTIME_LIBRARY "MultiThreaded$<$<CONFIG:Debug>:Debug>")

add_custom_command(TARGET UniCAVESwapSync POST_BUILD
    COMMAND ${CMAKE_COMMAND} -E copy_if_different $<TARGET_FILE:UniCAVESwapSync>
            "${CMAKE_CURRENT_SOURCE_DIR}/../../Plugins/x86_64/UniCAVESwapSync.dll")
