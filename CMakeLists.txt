cmake_minimum_required(VERSION 3.31)
project(NksTaskManager VERSION 0.1.0 LANGUAGES C CXX)
find_package(Git QUIET)
string(REPEAT "0" 40 nks_build_revision)
if(GIT_FOUND)
    execute_process(COMMAND "${GIT_EXECUTABLE}" rev-parse HEAD WORKING_DIRECTORY "${PROJECT_SOURCE_DIR}"
        RESULT_VARIABLE git_result OUTPUT_VARIABLE git_revision OUTPUT_STRIP_TRAILING_WHITESPACE ERROR_QUIET)
    string(LENGTH "${git_revision}" git_revision_length)
    if(git_result EQUAL 0 AND git_revision_length EQUAL 40 AND git_revision MATCHES "^[0-9a-fA-F]+$")
        set(nks_build_revision "${git_revision}")
        execute_process(COMMAND "${GIT_EXECUTABLE}" symbolic-ref --quiet HEAD WORKING_DIRECTORY "${PROJECT_SOURCE_DIR}"
            OUTPUT_VARIABLE git_ref OUTPUT_STRIP_TRAILING_WHITESPACE ERROR_QUIET)
        foreach(ref_name HEAD "${git_ref}")
            if(NOT ref_name STREQUAL "")
                execute_process(COMMAND "${GIT_EXECUTABLE}" rev-parse --git-path "${ref_name}" WORKING_DIRECTORY "${PROJECT_SOURCE_DIR}"
                    OUTPUT_VARIABLE ref_path OUTPUT_STRIP_TRAILING_WHITESPACE ERROR_QUIET)
                if(NOT ref_path STREQUAL "")
                    get_filename_component(ref_path "${ref_path}" ABSOLUTE BASE_DIR "${PROJECT_SOURCE_DIR}")
                    set_property(DIRECTORY APPEND PROPERTY CMAKE_CONFIGURE_DEPENDS "${ref_path}")
                endif()
            endif()
        endforeach()
    endif()
endif()
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

set(NKS_BROKER_PUBLISHER_SPKI_SHA256 "" CACHE STRING "SHA-256 of the permitted broker publisher public key")
set(NKS_UPDATE_PUBLISHER_SPKI_SHA256 "" CACHE STRING "SHA-256 of the permitted update publisher public key")
foreach(pin_variable NKS_BROKER_PUBLISHER_SPKI_SHA256 NKS_UPDATE_PUBLISHER_SPKI_SHA256)
    if(NOT "${${pin_variable}}" STREQUAL "")
        string(LENGTH "${${pin_variable}}" pin_length)
        if(NOT pin_length EQUAL 64 OR NOT "${${pin_variable}}" MATCHES "^[0-9a-fA-F]+$")
            message(FATAL_ERROR "${pin_variable} must be empty or 64 hexadecimal characters.")
        endif()
    endif()
endforeach()

add_library(nks_native STATIC
    src/collectors/process_query.c
    src/collectors/process_parser.cpp
    src/collectors/process_collector.cpp
    src/collectors/process_identity.cpp
    src/collectors/process_details.cpp
    src/collectors/process_memory.cpp
    src/collectors/process_handles.cpp
    src/collectors/process_signature.cpp
    src/collectors/boot_identity.cpp
    src/collectors/processor_query.cpp
    src/collectors/memory_query.cpp
    src/core/process_sampler.cpp
    src/core/process_delta.cpp
    src/core/measurement.cpp
    src/core/resource.cpp
    src/core/hardware.cpp
    src/collectors/hardware_windows.cpp
    src/collectors/ups_hardware.cpp
    src/core/monitor.cpp
    src/core/collector_client.cpp
    src/collector_worker/snapshot_codec.cpp
    src/collector_worker/service_codec.cpp
    src/core/history_stream.cpp
    src/core/process_events.cpp
    src/core/event_identity.cpp
    src/core/process_traffic.cpp
    src/collectors/etw_collector.cpp
    src/core/service.cpp
    src/core/service_monitor.cpp
    src/collectors/service_collector.cpp
    src/collectors/pdh_counters.cpp
    src/collectors/resource_collector.cpp
    src/collectors/cpu_sensors.cpp
    src/collectors/gpu_telemetry.cpp
    src/collectors/disk_telemetry.cpp
)
target_include_directories(nks_native PUBLIC src)
target_include_directories(nks_native SYSTEM PRIVATE third_party/phnt/include)
target_compile_definitions(nks_native PRIVATE PHNT_VERSION=117)
target_link_libraries(nks_native PUBLIC ntdll psapi pdh iphlpapi setupapi dxgi dxguid uuid advapi32 gdi32 wintrust crypt32 bcrypt tdh ole32 winhttp)
target_compile_options(nks_native PRIVATE /W4 /WX /utf-8 $<$<COMPILE_LANGUAGE:CXX>:/permissive->)

add_library(nks-monitor SHARED src/interop/monitor_api.cpp src/interop/services_api.cpp src/interop/history_api.cpp src/interop/events_api.cpp)
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

add_executable(nks-icon-worker src/icon_worker/main.cpp)
target_link_libraries(nks-icon-worker PRIVATE nks_native shell32 ole32 gdi32)
target_compile_options(nks-icon-worker PRIVATE /W4 /WX /utf-8 /permissive-)
target_link_options(nks-icon-worker PRIVATE "/MANIFESTUAC:level='asInvoker' uiAccess='false'" /DEPENDENTLOADFLAG:0xA00)

add_executable(nks-details-worker src/details_worker/main.cpp src/collectors/process_applications.cpp)
target_link_libraries(nks-details-worker PRIVATE nks_native user32 dwmapi)
target_compile_options(nks-details-worker PRIVATE /W4 /WX /utf-8 /permissive-)
target_link_options(nks-details-worker PRIVATE "/MANIFESTUAC:level='asInvoker' uiAccess='false'" /DEPENDENTLOADFLAG:0xA00)

add_executable(nks-actions-worker src/actions_worker/main.cpp src/collectors/process_actions.cpp src/collectors/process_tree_actions.cpp src/collectors/process_cpu_sets.cpp)
target_include_directories(nks-actions-worker SYSTEM PRIVATE third_party/phnt/include)
target_compile_definitions(nks-actions-worker PRIVATE PHNT_VERSION=117)
target_link_libraries(nks-actions-worker PRIVATE nks_native user32)
target_compile_options(nks-actions-worker PRIVATE /W4 /WX /utf-8 /permissive-)
target_link_options(nks-actions-worker PRIVATE "/MANIFESTUAC:level='asInvoker' uiAccess='false'" /DEPENDENTLOADFLAG:0xA00)

add_executable(nks-elevation-broker
    src/elevation_broker/main.cpp
    src/elevation_broker/bridge.cpp
    src/elevation_broker/server.cpp
    src/elevation_broker/consent.cpp
    src/core/broker_trust.cpp
    src/core/broker_pipe.cpp
    src/collectors/process_actions.cpp
    src/collectors/process_tree_actions.cpp
    src/collectors/process_cpu_sets.cpp
)
target_include_directories(nks-elevation-broker SYSTEM PRIVATE third_party/phnt/include)
target_compile_definitions(nks-elevation-broker PRIVATE PHNT_VERSION=117
    NKS_BROKER_PUBLISHER_SPKI_SHA256="${NKS_BROKER_PUBLISHER_SPKI_SHA256}")
target_link_libraries(nks-elevation-broker PRIVATE nks_native user32 comctl32 shell32 ole32 wintrust crypt32 bcrypt advapi32)
target_compile_options(nks-elevation-broker PRIVATE /W4 /WX /utf-8 /permissive-)
target_link_options(nks-elevation-broker PRIVATE "/MANIFESTUAC:level='asInvoker' uiAccess='false'" /DEPENDENTLOADFLAG:0xA00)

add_executable(nks-update-worker
    src/update_worker/main.cpp
    src/update_worker/cache.cpp
    src/update_worker/journal.cpp
    src/update_worker/execute.cpp
    src/update_worker/scope.cpp
    src/update_worker/recovery.cpp
    src/update_worker/install.cpp
    src/update_worker/cleanup.cpp
    src/update_worker/cache_delete.cpp
    src/update_worker/app.rc
    src/core/update_image.cpp
    src/core/broker_trust.cpp
    src/collectors/process_identity.cpp
    src/collectors/boot_identity.cpp
)
set_property(TARGET nks-update-worker PROPERTY MSVC_RUNTIME_LIBRARY "MultiThreaded$<$<CONFIG:Debug>:Debug>")
target_include_directories(nks-update-worker PRIVATE src)
target_include_directories(nks-update-worker SYSTEM PRIVATE third_party/phnt/include)
target_compile_definitions(nks-update-worker PRIVATE PHNT_VERSION=117
    NKS_BROKER_PUBLISHER_SPKI_SHA256="${NKS_UPDATE_PUBLISHER_SPKI_SHA256}"
    NKS_UPDATE_PUBLISHER_SPKI_SHA256="${NKS_UPDATE_PUBLISHER_SPKI_SHA256}"
    NKS_UPDATE_PRODUCT_VERSION=L"${PROJECT_VERSION}+${nks_build_revision}"
    "NKS_UPDATE_FILE_VERSION=${PROJECT_VERSION_MAJOR},${PROJECT_VERSION_MINOR},${PROJECT_VERSION_PATCH},0")
target_compile_options(nks-update-worker PRIVATE /W4 /WX /utf-8 /permissive-)
target_link_options(nks-update-worker PRIVATE "/MANIFESTUAC:level='asInvoker' uiAccess='false'" /DEPENDENTLOADFLAG:0x800)
target_link_libraries(nks-update-worker PRIVATE ntdll advapi32 bcrypt crypt32 wintrust version shell32 ole32)

add_executable(nks-service-actions-worker
    src/service_actions_worker/main.cpp
    src/collectors/service_actions.cpp
    src/collectors/service_actions_plan.cpp
    src/collectors/service_actions_execute.cpp
)
target_link_libraries(nks-service-actions-worker PRIVATE nks_native)
target_compile_options(nks-service-actions-worker PRIVATE /W4 /WX /utf-8 /permissive-)
target_link_options(nks-service-actions-worker PRIVATE "/MANIFESTUAC:level='asInvoker' uiAccess='false'" /DEPENDENTLOADFLAG:0xA00)

add_executable(nks-dump-worker
    src/dump_worker/main.cpp
    src/collectors/process_dump.cpp
    src/collectors/process_dump_policy.cpp
    src/collectors/process_identity.cpp
    src/collectors/boot_identity.cpp
)
set_property(TARGET nks-dump-worker PROPERTY MSVC_RUNTIME_LIBRARY "MultiThreaded$<$<CONFIG:Debug>:Debug>")
target_include_directories(nks-dump-worker PRIVATE src)
target_include_directories(nks-dump-worker SYSTEM PRIVATE third_party/phnt/include)
target_compile_definitions(nks-dump-worker PRIVATE PHNT_VERSION=117)
target_compile_options(nks-dump-worker PRIVATE /W4 /WX /utf-8 /permissive-)
target_link_options(nks-dump-worker PRIVATE "/MANIFESTUAC:level='asInvoker' uiAccess='false'" /DEPENDENTLOADFLAG:0x800)
target_link_libraries(nks-dump-worker PRIVATE ntdll advapi32 bcrypt dbghelp)

add_executable(nks-collector-worker src/collector_worker/main.cpp)
target_link_libraries(nks-collector-worker PRIVATE nks_native)
target_compile_options(nks-collector-worker PRIVATE /W4 /WX /utf-8 /permissive-)
target_link_options(nks-collector-worker PRIVATE "/MANIFESTUAC:level='asInvoker' uiAccess='false'" /DEPENDENTLOADFLAG:0xA00)
add_dependencies(nks-monitor nks-collector-worker)

add_executable(nks-taskmgr WIN32 src/app/desktop_main.cpp src/app/app.rc src/ui/desktop.cpp src/ui/desktop_paint.cpp src/ui/history_view.cpp src/ui/control_paint.cpp src/ui/performance_paint.cpp src/ui/process_paint.cpp src/ui/process_table.cpp src/ui/process_table_changes.cpp src/ui/service_table.cpp src/ui/service_view.cpp src/ui/service_paint.cpp)
target_sources(nks-taskmgr PRIVATE src/ui/composition_renderer.cpp)
target_link_libraries(nks-taskmgr PRIVATE nks_native d2d1 dwrite dwmapi comctl32 uxtheme d3d11 dxgi dcomp)
target_compile_options(nks-taskmgr PRIVATE /W4 /WX /utf-8 /permissive-)
add_dependencies(nks-taskmgr nks-collector-worker)

include(CTest)
if(BUILD_TESTING)
    add_executable(nks-cpu-sensor-tests tests/cpu_sensor_tests.cpp)
    target_link_libraries(nks-cpu-sensor-tests PRIVATE nks_native ws2_32)
    target_compile_options(nks-cpu-sensor-tests PRIVATE /W4 /WX /utf-8 /permissive-)
    add_test(NAME cpu-sensors COMMAND nks-cpu-sensor-tests)
    set_tests_properties(cpu-sensors PROPERTIES TIMEOUT 30 SKIP_RETURN_CODE 77 RESOURCE_LOCK cpu_sensor_http_8085)
    get_target_property(nks_update_test_sources nks-update-worker SOURCES)
    list(REMOVE_ITEM nks_update_test_sources src/update_worker/main.cpp src/update_worker/app.rc)
    add_executable(nks-update-cleanup-tests tests/update_cleanup_tests.cpp ${nks_update_test_sources})
    foreach(property INCLUDE_DIRECTORIES COMPILE_DEFINITIONS COMPILE_OPTIONS LINK_LIBRARIES LINK_OPTIONS MSVC_RUNTIME_LIBRARY)
        get_target_property(value nks-update-worker ${property})
        if(NOT value STREQUAL "value-NOTFOUND")
            set_property(TARGET nks-update-cleanup-tests PROPERTY ${property} "${value}")
        endif()
    endforeach()
    target_include_directories(nks-update-cleanup-tests SYSTEM PRIVATE third_party/phnt/include)
    add_test(NAME update-cleanup-retention COMMAND nks-update-cleanup-tests)
    set_tests_properties(update-cleanup-retention PROPERTIES TIMEOUT 20)
    add_executable(nks-memory-query-tests tests/memory_query_tests.cpp)
    target_include_directories(nks-memory-query-tests SYSTEM PRIVATE third_party/phnt/include)
    target_compile_definitions(nks-memory-query-tests PRIVATE PHNT_VERSION=117)
    target_compile_options(nks-memory-query-tests PRIVATE /W4 /WX /utf-8 /permissive-)
    target_link_libraries(nks-memory-query-tests PRIVATE nks_native)
    add_test(NAME memory-statistics COMMAND nks-memory-query-tests)
    add_executable(nks-icon-worker-tests tests/icon_worker_tests.cpp)
    target_link_libraries(nks-icon-worker-tests PRIVATE nks_native advapi32)
    target_compile_options(nks-icon-worker-tests PRIVATE /W4 /WX /utf-8 /permissive-)
    add_dependencies(nks-icon-worker-tests nks-icon-worker)
    add_test(NAME icon-worker COMMAND nks-icon-worker-tests)
    set_tests_properties(icon-worker PROPERTIES TIMEOUT 15)
    add_executable(nks-boot-identity-tests tests/boot_identity_tests.cpp)
    target_include_directories(nks-boot-identity-tests SYSTEM PRIVATE third_party/phnt/include)
    target_compile_definitions(nks-boot-identity-tests PRIVATE PHNT_VERSION=117)
    target_link_libraries(nks-boot-identity-tests PRIVATE nks_native)
    target_compile_options(nks-boot-identity-tests PRIVATE /W4 /WX /utf-8 /permissive-)
    add_test(NAME boot-identity COMMAND nks-boot-identity-tests)
    add_executable(nks-hardware-tests tests/hardware_tests.cpp)
    target_link_libraries(nks-hardware-tests PRIVATE nks_native)
    target_compile_options(nks-hardware-tests PRIVATE /W4 /WX /utf-8 /permissive-)
    add_test(NAME hardware COMMAND nks-hardware-tests)
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
    add_executable(nks-process-view-tests tests/process_view_tests.cpp src/ui/process_table.cpp src/ui/process_table_changes.cpp)
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
    add_executable(nks-service-api-tests tests/service_api_tests.cpp tests/c_api_header.c)
    set_target_properties(nks-service-api-tests PROPERTIES C_STANDARD 17)
    target_link_libraries(nks-service-api-tests PRIVATE nks-monitor)
    target_compile_options(nks-service-api-tests PRIVATE /W4 /WX /utf-8 $<$<COMPILE_LANGUAGE:CXX>:/permissive->)
    add_test(NAME services-c-api COMMAND nks-service-api-tests)
    set_tests_properties(services-c-api PROPERTIES TIMEOUT 20)
    add_executable(nks-service-tests tests/service_tests.cpp)
    target_link_libraries(nks-service-tests PRIVATE nks_native)
    target_compile_options(nks-service-tests PRIVATE /W4 /WX /utf-8 /permissive-)
    add_test(NAME services COMMAND nks-service-tests)
    set_tests_properties(services PROPERTIES TIMEOUT 20)
    add_executable(nks-service-view-tests tests/service_view_tests.cpp src/ui/service_table.cpp)
    target_link_libraries(nks-service-view-tests PRIVATE nks_native comctl32 uxtheme)
    target_compile_options(nks-service-view-tests PRIVATE /W4 /WX /utf-8 /permissive-)
    add_test(NAME service-view COMMAND nks-service-view-tests)
endif()

add_executable(nks-collect-bench benchmarks/collect_bench.cpp)
target_link_libraries(nks-collect-bench PRIVATE nks_native)
target_compile_options(nks-collect-bench PRIVATE /W4 /WX /utf-8 /permissive-)
