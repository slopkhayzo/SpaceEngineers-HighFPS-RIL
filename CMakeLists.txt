cmake_minimum_required(VERSION 3.24)

project(
    SpaceEngineersHighFpsFix
    VERSION 1.0.0
    DESCRIPTION "High-frame-rate presentation fix for Space Engineers"
    LANGUAGES C CXX
)

if(NOT WIN32 OR NOT MSVC)
    message(FATAL_ERROR "SpaceEngineers.HighFPSFix supports Windows/MSVC only")
endif()

if(NOT CMAKE_SIZEOF_VOID_P EQUAL 8)
    message(FATAL_ERROR
        "Configure an x64 build environment; SpaceEngineers.exe is a 64-bit process")
endif()

set(SEHFF_NAME "SpaceEngineers.HighFPSFix")

# ---------------------------------------------------------------------------
# Native plugin (the .asi the loader maps; hosts MinHook, starts the managed part)

set(BUILD_SHARED_LIBS OFF CACHE BOOL "Build static dependencies" FORCE)
add_subdirectory(third_party/minhook EXCLUDE_FROM_ALL)
set_property(TARGET minhook PROPERTY
    MSVC_RUNTIME_LIBRARY "MultiThreaded$<$<CONFIG:Debug>:Debug>"
)

add_library(SpaceEngineersHighFpsFix MODULE
    src/native/bootstrap.cpp
    src/native/clr_host.cpp
    src/native/config.cpp
    src/native/dllmain.cpp
    src/native/logger.cpp
    src/native/native_api.cpp
)
target_compile_features(SpaceEngineersHighFpsFix PRIVATE cxx_std_20)
target_compile_definitions(SpaceEngineersHighFpsFix PRIVATE
    WIN32_LEAN_AND_MEAN
    NOMINMAX
    UNICODE
    _UNICODE
)
target_compile_options(SpaceEngineersHighFpsFix PRIVATE
    /W4
    /WX
    /permissive-
    /utf-8
    /Zc:preprocessor
    /EHsc
)
set_property(TARGET SpaceEngineersHighFpsFix PROPERTY
    MSVC_RUNTIME_LIBRARY "MultiThreaded$<$<CONFIG:Debug>:Debug>"
)
target_link_options(SpaceEngineersHighFpsFix PRIVATE
    /DYNAMICBASE
    /NXCOMPAT
    /INCREMENTAL:NO
)
target_link_libraries(SpaceEngineersHighFpsFix PRIVATE minhook shell32 ole32)
set_target_properties(SpaceEngineersHighFpsFix PROPERTIES
    OUTPUT_NAME "${SEHFF_NAME}"
    PREFIX ""
    SUFFIX ".asi"
    LIBRARY_OUTPUT_DIRECTORY "${CMAKE_BINARY_DIR}/bin"
    RUNTIME_OUTPUT_DIRECTORY "${CMAKE_BINARY_DIR}/bin"
)

add_custom_command(TARGET SpaceEngineersHighFpsFix POST_BUILD
    COMMAND "${CMAKE_COMMAND}" -E copy_if_different
            "${CMAKE_SOURCE_DIR}/config/${SEHFF_NAME}.ini"
            "$<TARGET_FILE_DIR:SpaceEngineersHighFpsFix>/${SEHFF_NAME}.ini"
    VERBATIM
)

# ---------------------------------------------------------------------------
# Managed part (C#, .NET Framework 4.8), compiled with the Roslyn compiler that
# ships with Visual Studio against the game's own assemblies.

set(SEHFF_GAME_BIN64 "D:/Program Files/Games/Steam/steamapps/common/SpaceEngineers/Bin64"
    CACHE PATH "Space Engineers Bin64 folder (compile-time references of the managed part)")
set(SEHFF_NETFX_REFERENCE_DIR
    "C:/Program Files (x86)/Reference Assemblies/Microsoft/Framework/.NETFramework/v4.8"
    CACHE PATH ".NET Framework 4.8 reference assemblies")

if(NOT SEHFF_CSC)
    # cl.exe lives in <VS>/VC/Tools/MSVC/<version>/bin/Host<arch>/<arch>.
    get_filename_component(sehff_vs_root "${CMAKE_CXX_COMPILER}" DIRECTORY)
    foreach(level RANGE 1 7)
        get_filename_component(sehff_vs_root "${sehff_vs_root}" DIRECTORY)
    endforeach()
    find_program(SEHFF_CSC csc.exe
        HINTS "${sehff_vs_root}/MSBuild/Current/Bin/Roslyn"
        NO_DEFAULT_PATH)
endif()
if(NOT SEHFF_CSC)
    message(FATAL_ERROR "Roslyn csc.exe not found; set SEHFF_CSC")
endif()
foreach(required IN ITEMS "${SEHFF_NETFX_REFERENCE_DIR}/mscorlib.dll" "${SEHFF_GAME_BIN64}/VRage.Render.dll")
    if(NOT EXISTS "${required}")
        message(FATAL_ERROR "Missing ${required}; set SEHFF_NETFX_REFERENCE_DIR / SEHFF_GAME_BIN64")
    endif()
endforeach()

set(SEHFF_MANAGED_SOURCES
    src/managed/DebugAutomation.cs
    src/managed/Detour.cs
    src/managed/FrameStats.cs
    src/managed/GameReflection.cs
    src/managed/Interpolation/PresentationClock.cs
    src/managed/Interpolation/RenderAccess.cs
    src/managed/Interpolation/SampleRing.cs
    src/managed/Interpolation/SceneInterpolator.cs
    src/managed/Interpolation/TransformMath.cs
    src/managed/Log.cs
    src/managed/MouseLook/LookMath.cs
    src/managed/MouseLook/MouseAutomation.cs
    src/managed/MouseLook/MouseInput.cs
    src/managed/MouseLook/MouseLead.cs
    src/managed/MouseLook/MouseLookSetup.cs
    src/managed/MouseLook/TickCapture.cs
    src/managed/MouseLook/TickInfo.cs
    src/managed/NativeApi.cs
    src/managed/Plugin.cs
    src/managed/RenderHooks.cs
    src/managed/RenderRate.cs
    src/managed/Settings.cs
)
set(SEHFF_GAME_REFERENCES
    Sandbox.Common.dll
    Sandbox.Game.dll
    Sandbox.Graphics.dll
    SharpDX.dll
    SharpDX.DirectInput.dll
    SpaceEngineers.Game.dll
    VRage.dll
    VRage.Game.dll
    VRage.Input.dll
    VRage.Library.dll
    VRage.Math.dll
    VRage.Render.dll
)

set(sehff_managed_rsp "${CMAKE_BINARY_DIR}/managed.rsp")
set(sehff_managed_dll "${CMAKE_BINARY_DIR}/bin/${SEHFF_NAME}.Managed.dll")
set(sehff_rsp_lines
    "-nologo"
    "-nostdlib+"
    "-target:library"
    "-unsafe+"
    "-optimize+"
    "-deterministic+"
    "-langversion:latest"
    "-warnaserror+"
    "-debug:portable"
    "-out:\"${sehff_managed_dll}\""
    "-reference:\"${SEHFF_NETFX_REFERENCE_DIR}/mscorlib.dll\""
    "-reference:\"${SEHFF_NETFX_REFERENCE_DIR}/System.dll\""
    "-reference:\"${SEHFF_NETFX_REFERENCE_DIR}/System.Core.dll\""
)
file(GLOB sehff_facades "${SEHFF_NETFX_REFERENCE_DIR}/Facades/*.dll")
foreach(facade IN LISTS sehff_facades)
    list(APPEND sehff_rsp_lines "-reference:\"${facade}\"")
endforeach()
foreach(reference IN LISTS SEHFF_GAME_REFERENCES)
    list(APPEND sehff_rsp_lines "-reference:\"${SEHFF_GAME_BIN64}/${reference}\"")
endforeach()
foreach(source IN LISTS SEHFF_MANAGED_SOURCES)
    list(APPEND sehff_rsp_lines "\"${CMAKE_SOURCE_DIR}/${source}\"")
endforeach()
list(JOIN sehff_rsp_lines "\n" sehff_rsp_text)
file(WRITE "${sehff_managed_rsp}" "${sehff_rsp_text}\n")

add_custom_command(
    OUTPUT "${sehff_managed_dll}"
    COMMAND "${CMAKE_COMMAND}" -E make_directory "${CMAKE_BINARY_DIR}/bin"
    COMMAND "${SEHFF_CSC}" -noconfig "@${sehff_managed_rsp}"
    DEPENDS ${SEHFF_MANAGED_SOURCES} "${sehff_managed_rsp}"
    COMMENT "Compiling ${SEHFF_NAME}.Managed.dll"
    VERBATIM
)
add_custom_target(SpaceEngineersHighFpsFixManaged ALL DEPENDS "${sehff_managed_dll}")
add_dependencies(SpaceEngineersHighFpsFix SpaceEngineersHighFpsFixManaged)

# ---------------------------------------------------------------------------
# Game-free tests of the interpolation math and the presentation clock.

enable_testing()
set(sehff_tests_rsp "${CMAKE_BINARY_DIR}/managed_tests.rsp")
set(sehff_tests_exe "${CMAKE_BINARY_DIR}/tests/SpaceEngineers.HighFPSFix.Tests.exe")
set(SEHFF_TEST_SOURCES
    tests/ManagedTests.cs
    src/managed/Interpolation/PresentationClock.cs
    src/managed/Interpolation/SampleRing.cs
    src/managed/Interpolation/TransformMath.cs
    src/managed/MouseLook/LookMath.cs
    src/managed/MouseLook/MouseLead.cs
    src/managed/MouseLook/TickInfo.cs
)
set(sehff_tests_rsp_lines
    "-nologo"
    "-nostdlib+"
    "-target:exe"
    "-unsafe+"
    "-optimize+"
    "-langversion:latest"
    "-warnaserror+"
    "-out:\"${sehff_tests_exe}\""
    "-reference:\"${SEHFF_NETFX_REFERENCE_DIR}/mscorlib.dll\""
    "-reference:\"${SEHFF_NETFX_REFERENCE_DIR}/System.dll\""
    "-reference:\"${SEHFF_NETFX_REFERENCE_DIR}/System.Core.dll\""
    "-reference:\"${SEHFF_GAME_BIN64}/VRage.Math.dll\""
)
foreach(facade IN LISTS sehff_facades)
    list(APPEND sehff_tests_rsp_lines "-reference:\"${facade}\"")
endforeach()
foreach(source IN LISTS SEHFF_TEST_SOURCES)
    list(APPEND sehff_tests_rsp_lines "\"${CMAKE_SOURCE_DIR}/${source}\"")
endforeach()
list(JOIN sehff_tests_rsp_lines "\n" sehff_tests_rsp_text)
file(WRITE "${sehff_tests_rsp}" "${sehff_tests_rsp_text}\n")

add_custom_command(
    OUTPUT "${sehff_tests_exe}"
    COMMAND "${CMAKE_COMMAND}" -E make_directory "${CMAKE_BINARY_DIR}/tests"
    COMMAND "${SEHFF_CSC}" -noconfig "@${sehff_tests_rsp}"
    # The tests load the game's math library from their own folder.
    COMMAND "${CMAKE_COMMAND}" -E copy_if_different "${SEHFF_GAME_BIN64}/VRage.Math.dll"
            "${CMAKE_BINARY_DIR}/tests/VRage.Math.dll"
    DEPENDS ${SEHFF_TEST_SOURCES} "${sehff_tests_rsp}"
    COMMENT "Compiling SpaceEngineers.HighFPSFix.Tests.exe"
    VERBATIM
)
add_custom_target(SpaceEngineersHighFpsFixTests ALL DEPENDS "${sehff_tests_exe}")
add_test(NAME managed_tests COMMAND "${sehff_tests_exe}")

# ---------------------------------------------------------------------------
# Release archives ship packaging/SpaceEngineers.HighFPSFix.ini, with every
# validated feature on. config/SpaceEngineers.HighFPSFix.ini is the development
# default (diagnostics on) and stays beside the built plugin.

set(SEHFF_PACKAGE_SCRIPT "${CMAKE_SOURCE_DIR}/cmake/package.cmake")
set(SEHFF_ULTIMATE_ASI_LOADER_DLL "" CACHE FILEPATH
    "Optional x64 Ultimate ASI Loader (v9.7.4) DLL used for the loader-bundled package")
# File name the loader is shipped under; see third_party/ultimate-asi-loader/UPSTREAM.md.
unset(SEHFF_LOADER_NAME CACHE)
set(SEHFF_LOADER_NAME "dinput8.dll")

add_custom_target(package_plugin_only
    COMMAND "${CMAKE_COMMAND}"
            -DMODE=plugin-only
            -DVERSION=${PROJECT_VERSION}
            -DPLUGIN_FILE=$<TARGET_FILE:SpaceEngineersHighFpsFix>
            -DMANAGED_FILE=${sehff_managed_dll}
            -DINI_FILE=${CMAKE_SOURCE_DIR}/packaging/${SEHFF_NAME}.ini
            -DREADME_FILE=${CMAKE_SOURCE_DIR}/packaging/README.txt
            -DMINHOOK_LICENSE_FILE=${CMAKE_SOURCE_DIR}/third_party/minhook/LICENSE.txt
            -DOUTPUT_DIR=${CMAKE_SOURCE_DIR}/dist/release/${SEHFF_NAME}-${PROJECT_VERSION}-plugin-only
            -DARCHIVE_FILE=${CMAKE_SOURCE_DIR}/dist/${SEHFF_NAME}-${PROJECT_VERSION}-plugin-only.zip
            -P "${SEHFF_PACKAGE_SCRIPT}"
    DEPENDS SpaceEngineersHighFpsFix SpaceEngineersHighFpsFixManaged
    VERBATIM
)

if(SEHFF_ULTIMATE_ASI_LOADER_DLL AND EXISTS "${SEHFF_ULTIMATE_ASI_LOADER_DLL}")
    add_custom_target(package_loader_bundled
        COMMAND "${CMAKE_COMMAND}"
                -DMODE=loader-bundled
                -DVERSION=${PROJECT_VERSION}
                -DPLUGIN_FILE=$<TARGET_FILE:SpaceEngineersHighFpsFix>
                -DMANAGED_FILE=${sehff_managed_dll}
                -DINI_FILE=${CMAKE_SOURCE_DIR}/packaging/${SEHFF_NAME}.ini
                -DREADME_FILE=${CMAKE_SOURCE_DIR}/packaging/README-loader.txt
                -DMINHOOK_LICENSE_FILE=${CMAKE_SOURCE_DIR}/third_party/minhook/LICENSE.txt
                -DLOADER_FILE=${SEHFF_ULTIMATE_ASI_LOADER_DLL}
                -DLOADER_NAME=${SEHFF_LOADER_NAME}
                -DLOADER_LICENSE_FILE=${CMAKE_SOURCE_DIR}/third_party/ultimate-asi-loader/LICENSE.txt
                -DOUTPUT_DIR=${CMAKE_SOURCE_DIR}/dist/release/${SEHFF_NAME}-${PROJECT_VERSION}-with-Ultimate-ASI-Loader
                -DARCHIVE_FILE=${CMAKE_SOURCE_DIR}/dist/${SEHFF_NAME}-${PROJECT_VERSION}-with-Ultimate-ASI-Loader.zip
                -P "${SEHFF_PACKAGE_SCRIPT}"
        DEPENDS SpaceEngineersHighFpsFix SpaceEngineersHighFpsFixManaged
        VERBATIM
    )
endif()
