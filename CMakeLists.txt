cmake_minimum_required(VERSION 3.31)
project(NksTaskManager VERSION 0.1.0 LANGUAGES C CXX)
if(MSVC AND NOT MSVC_VERSION EQUAL 1944)
    message(FATAL_ERROR "This baseline requires the MSVC 14.44 toolset.")
endif()

if(NOT WIN32)
    message(FATAL_ERROR "NKS Task Manager targets Windows 10/11.")
endif()
if(NOT CMAKE_SIZEOF_VOID_P EQUAL 8)
    message(FATAL_ERROR "Build the main application for x64.")
endif()

set(CMAKE_CXX_STANDARD 20)
set(CMAKE_CXX_STANDARD_REQUIRED ON)
set(CMAKE_CXX_EXTENSIONS OFF)
set(CMAKE_MSVC_RUNTIME_LIBRARY "MultiThreaded$<$<CONFIG:Debug>:Debug>DLL")
add_compile_definitions(UNICODE _UNICODE NOMINMAX WIN32_LEAN_AND_MEAN WINVER=0x0A00 _WIN32_WINNT=0x0A00)

add_library(nks_native STATIC
    src/collectors/process_query.c
    src/collectors/process_parser.cpp
    src/collectors/process_collector.cpp
    src/collectors/processor_query.cpp
    src/core/process_sampler.cpp
    src/core/resource.cpp
    src/core/monitor.cpp
    src/collectors/pdh_counters.cpp
    src/collectors/resource_collector.cpp
)
target_include_directories(nks_native PUBLIC src)
target_include_directories(nks_native SYSTEM PRIVATE third_party/phnt/include)
target_compile_definitions(nks_native PRIVATE PHNT_VERSION=117)
target_link_libraries(nks_native PUBLIC ntdll psapi pdh iphlpapi setupapi dxgi dxguid uuid)
target_compile_options(nks_native PRIVATE /W4 /WX /utf-8 $<$<COMPILE_LANGUAGE:CXX>:/permissive->)

add_library(nks-monitor SHARED src/interop/monitor_api.cpp)
target_include_directories(nks-monitor PUBLIC src)
target_compile_definitions(nks-monitor PRIVATE NKS_MONITOR_EXPORTS)
target_compile_options(nks-monitor PRIVATE /W4 /WX /utf-8 /permissive-)
target_link_libraries(nks-monitor PRIVATE nks_native)

add_executable(nks-sample src/app/sample_main.cpp)
target_link_libraries(nks-sample PRIVATE nks_native)
target_compile_options(nks-sample PRIVATE /W4 /WX /utf-8 /permissive-)

add_executable(nks-resource-sample src/app/resource_sample.cpp)
target_link_libraries(nks-resource-sample PRIVATE nks_native)
target_compile_options(nks-resource-sample PRIVATE /W4 /WX /utf-8 /permissive-)

add_executable(nks-taskmgr WIN32 src/app/desktop_main.cpp src/ui/desktop.cpp src/ui/desktop_paint.cpp src/ui/control_paint.cpp src/ui/performance_paint.cpp src/ui/process_paint.cpp src/ui/process_table.cpp)
target_link_libraries(nks-taskmgr PRIVATE nks_native d2d1 dwrite dwmapi comctl32 uxtheme)
target_compile_options(nks-taskmgr PRIVATE /W4 /WX /utf-8 /permissive-)

include(CTest)
if(BUILD_TESTING)
    add_executable(nks-core-tests tests/core_tests.cpp)
    target_include_directories(nks-core-tests SYSTEM PRIVATE third_party/phnt/include)
    target_compile_definitions(nks-core-tests PRIVATE PHNT_VERSION=117)
    target_compile_options(nks-core-tests PRIVATE /W4 /WX /utf-8 /permissive-)
    target_link_libraries(nks-core-tests PRIVATE nks_native)
    add_test(NAME core COMMAND nks-core-tests)
    add_test(NAME live-sampling COMMAND nks-sample --verify)
    set_tests_properties(live-sampling PROPERTIES TIMEOUT 20)
    add_executable(nks-resource-tests tests/resource_tests.cpp)
    target_link_libraries(nks-resource-tests PRIVATE nks_native)
    target_compile_options(nks-resource-tests PRIVATE /W4 /WX /utf-8 /permissive-)
    add_test(NAME resource-calculations COMMAND nks-resource-tests)
    add_test(NAME live-resources COMMAND nks-resource-sample)
    set_tests_properties(live-resources PROPERTIES TIMEOUT 20)
    add_executable(nks-process-view-tests tests/process_view_tests.cpp src/ui/process_table.cpp)
    target_link_libraries(nks-process-view-tests PRIVATE nks_native comctl32 uxtheme)
    target_compile_options(nks-process-view-tests PRIVATE /W4 /WX /utf-8 /permissive-)
    add_test(NAME process-view COMMAND nks-process-view-tests)
    add_executable(nks-monitor-tests tests/monitor_tests.cpp)
    target_link_libraries(nks-monitor-tests PRIVATE nks_native)
    target_compile_options(nks-monitor-tests PRIVATE /W4 /WX /utf-8 /permissive-)
    add_test(NAME monitor COMMAND nks-monitor-tests)
    set_tests_properties(monitor PROPERTIES TIMEOUT 20)
    add_executable(nks-api-tests tests/api_tests.cpp tests/c_api_header.c)
    set_target_properties(nks-api-tests PROPERTIES C_STANDARD 17)
    target_link_libraries(nks-api-tests PRIVATE nks-monitor)
    target_compile_options(nks-api-tests PRIVATE /W4 /WX /utf-8 $<$<COMPILE_LANGUAGE:CXX>:/permissive->)
    add_test(NAME monitor-c-api COMMAND nks-api-tests)
    set_tests_properties(monitor-c-api PROPERTIES TIMEOUT 20)
endif()

add_executable(nks-collect-bench benchmarks/collect_bench.cpp)
target_link_libraries(nks-collect-bench PRIVATE nks_native)
target_compile_options(nks-collect-bench PRIVATE /W4 /WX /utf-8 /permissive-)
