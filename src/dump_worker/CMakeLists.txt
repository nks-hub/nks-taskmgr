cmake_minimum_required(VERSION 3.31)
project(NksDumpWorkerX86 LANGUAGES CXX)
if(NOT WIN32 OR NOT CMAKE_SIZEOF_VOID_P EQUAL 4)
    message(FATAL_ERROR "This target builds only the x86 WOW64 dump helper.")
endif()
if(MSVC AND NOT MSVC_VERSION EQUAL 1944)
    message(FATAL_ERROR "This baseline requires the MSVC 14.44 toolset.")
endif()
set(CMAKE_CXX_STANDARD 20)
set(CMAKE_CXX_STANDARD_REQUIRED ON)
set(CMAKE_CXX_EXTENSIONS OFF)
set(CMAKE_MSVC_RUNTIME_LIBRARY "MultiThreaded$<$<CONFIG:Debug>:Debug>")
get_filename_component(NKS_SOURCE_ROOT "${CMAKE_CURRENT_SOURCE_DIR}/.." ABSOLUTE)
add_executable(nks-dump-worker-x86
    main.cpp
    ../collectors/process_dump.cpp
    ../collectors/process_dump_policy.cpp
    ../collectors/process_identity.cpp
    ../collectors/boot_identity.cpp
)
target_include_directories(nks-dump-worker-x86 PRIVATE "${NKS_SOURCE_ROOT}")
target_include_directories(nks-dump-worker-x86 SYSTEM PRIVATE "${NKS_SOURCE_ROOT}/../third_party/phnt/include")
target_compile_definitions(nks-dump-worker-x86 PRIVATE UNICODE _UNICODE NOMINMAX WIN32_LEAN_AND_MEAN WINVER=0x0A00 _WIN32_WINNT=0x0A00 PHNT_VERSION=117)
target_compile_options(nks-dump-worker-x86 PRIVATE /W4 /WX /utf-8 /permissive-)
target_link_options(nks-dump-worker-x86 PRIVATE "/MANIFESTUAC:level='asInvoker' uiAccess='false'" /DEPENDENTLOADFLAG:0x800)
target_link_libraries(nks-dump-worker-x86 PRIVATE ntdll advapi32 bcrypt dbghelp)
