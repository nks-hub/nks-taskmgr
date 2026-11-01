#include <phnt_windows.h>
#include <phnt.h>
#include "etw_collector.hpp"
#include "boot_identity.hpp"
#include <evntrace.h>
#include <evntcons.h>
#include <tdh.h>
#include <objbase.h>
#include <algorithm>
#include <array>
#include <atomic>
#include <condition_variable>
#include <cstring>
#include <cwchar>
#include <limits>
#include <stdexcept>
#include <thread>

namespace nks {
namespace {

std::atomic<bool> sessionLease{};
constexpr GUID processProvider{0x3d6fa8d0, 0xfe05, 0x11d0, {0x9d, 0xda, 0x00, 0xc0, 0x4f, 0xd7, 0xba, 0x7c}};
constexpr ULONG maximumSchemaBytes = 64 * 1024;

LONG NTAPI systemQuery(ULONG kind, void* buffer, ULONG size, ULONG* used) {
    return NtQuerySystemInformation(static_cast<SYSTEM_INFORMATION_CLASS>(kind), buffer, size, used);
}

bool pinModule() {
    HMODULE module{};
    return GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS | GET_MODULE_HANDLE_EX_FLAG_PIN,
        reinterpret_cast<LPCWSTR>(&pinModule), &module) != FALSE;
}

ProcessEventMonitorState errorState(ULONG error) {
    if (error == ERROR_ACCESS_DENIED || error == ERROR_PRIVILEGE_NOT_HELD) return ProcessEventMonitorState::accessDenied;
    if (error == ERROR_NOT_SUPPORTED || error == ERROR_CALL_NOT_IMPLEMENTED) return ProcessEventMonitorState::unsupported;
    return ProcessEventMonitorState::error;
}

bool validText(const std::wstring& value) {
    if (value.size() > ProcessEventStore::maximumNameChars) return false;
    for (std::size_t i = 0; i < value.size(); ++i) {
        const auto unit = static_cast<std::uint16_t>(value[i]);
        if (!unit) return false;
        if (unit >= 0xd800 && unit <= 0xdbff) {
            if (++i == value.size() || value[i] < 0xdc00 || value[i] > 0xdfff) return false;
        } else if (unit >= 0xdc00 && unit <= 0xdfff) return false;
    }
    return true;
}

class EventProperties {
public:
    explicit EventProperties(EVENT_RECORD& record) : event(record) {
        const bool narrow = (event.EventHeader.Flags & EVENT_HEADER_FLAG_32_BIT_HEADER) != 0;
        const bool wide = (event.EventHeader.Flags & EVENT_HEADER_FLAG_64_BIT_HEADER) != 0;
        if (narrow == wide) return;
        context.ParameterType = TDH_CONTEXT_POINTERSIZE; context.ParameterValue = narrow ? 4 : 8;
        ULONG size = 0;
        if (TdhGetEventInformation(&event, 1, &context, nullptr, &size) != ERROR_INSUFFICIENT_BUFFER
            || size < offsetof(TRACE_EVENT_INFO, EventPropertyInfoArray) || size > maximumSchemaBytes) return;
        schema.resize(size);
        if (TdhGetEventInformation(&event, 1, &context, reinterpret_cast<TRACE_EVENT_INFO*>(schema.data()), &size) != ERROR_SUCCESS
            || size < offsetof(TRACE_EVENT_INFO, EventPropertyInfoArray) || size > schema.size()) return;
        auto* value = reinterpret_cast<TRACE_EVENT_INFO*>(schema.data());
        const auto header = offsetof(TRACE_EVENT_INFO, EventPropertyInfoArray);
        if (value->PropertyCount > 256 || value->TopLevelPropertyCount > value->PropertyCount
            || value->PropertyCount > (size - header) / sizeof(EVENT_PROPERTY_INFO)) return;
        used = size; info = value;
    }

    bool unsigned32(const wchar_t* name, std::uint32_t& value) {
        const auto* property = find(name);
        if (!property || (property->nonStructType.InType != TDH_INTYPE_UINT32 && property->nonStructType.InType != TDH_INTYPE_HEXINT32)) return false;
        std::vector<BYTE> bytes;
        if (!read(*property, bytes, sizeof(value)) || bytes.size() != sizeof(value)) return false;
        std::memcpy(&value, bytes.data(), sizeof(value)); return true;
    }
    bool signed32(const wchar_t* name, std::int32_t& value) {
        const auto* property = find(name);
        if (!property || property->nonStructType.InType != TDH_INTYPE_INT32) return false;
        std::vector<BYTE> bytes;
        if (!read(*property, bytes, sizeof(value)) || bytes.size() != sizeof(value)) return false;
        std::memcpy(&value, bytes.data(), sizeof(value)); return true;
    }
    bool image(std::wstring& value) {
        const auto* property = find(L"ImageFileName");
        if (!property) return false;
        const auto type = property->nonStructType.InType;
        if (type != TDH_INTYPE_UNICODESTRING && type != TDH_INTYPE_ANSISTRING) return false;
        std::vector<BYTE> bytes;
        if (!read(*property, bytes, 2 * (ProcessEventStore::maximumNameChars + 1)) || bytes.empty()) return false;
        if (type == TDH_INTYPE_UNICODESTRING) {
            if (bytes.size() % sizeof(wchar_t) || bytes.size() < sizeof(wchar_t)) return false;
            value.resize(bytes.size() / sizeof(wchar_t)); std::memcpy(value.data(), bytes.data(), bytes.size());
            if (value.back()) return false;
            value.pop_back();
        } else {
            if (bytes.back() || bytes.size() > ProcessEventStore::maximumNameChars + 1
                || std::find(bytes.begin(), bytes.end() - 1, BYTE{}) != bytes.end() - 1) return false;
            const auto length = static_cast<int>(bytes.size() - 1);
            if (!length) { value.clear(); return true; }
            const auto chars = MultiByteToWideChar(CP_ACP, MB_ERR_INVALID_CHARS, reinterpret_cast<const char*>(bytes.data()), length, nullptr, 0);
            if (chars <= 0 || static_cast<std::size_t>(chars) > ProcessEventStore::maximumNameChars) return false;
            value.resize(static_cast<std::size_t>(chars));
            if (MultiByteToWideChar(CP_ACP, MB_ERR_INVALID_CHARS, reinterpret_cast<const char*>(bytes.data()), length, value.data(), chars) != chars) return false;
        }
        return validText(value);
    }
private:
    const wchar_t* propertyName(const EVENT_PROPERTY_INFO& property) const {
        const auto offset = property.NameOffset;
        if (offset % sizeof(wchar_t) || offset < offsetof(TRACE_EVENT_INFO, EventPropertyInfoArray) + info->PropertyCount * sizeof(EVENT_PROPERTY_INFO)
            || offset >= used || used - offset < sizeof(wchar_t)) return nullptr;
        const auto* name = reinterpret_cast<const wchar_t*>(schema.data() + offset);
        const auto chars = (used - offset) / sizeof(wchar_t);
        const auto* end = std::find(name, name + (std::min)(chars, std::size_t{128}), L'\0');
        return end == name + (std::min)(chars, std::size_t{128}) ? nullptr : name;
    }
    const EVENT_PROPERTY_INFO* find(const wchar_t* wanted) const {
        if (!info) return nullptr;
        const EVENT_PROPERTY_INFO* found = nullptr;
        for (ULONG i = 0; i < info->TopLevelPropertyCount; ++i) {
            const auto& property = info->EventPropertyInfoArray[i];
            const auto* name = propertyName(property);
            if (!name || CompareStringOrdinal(name, -1, wanted, -1, TRUE) != CSTR_EQUAL) continue;
            if (found || property.Flags || property.count != 1) return nullptr;
            found = &property;
        }
        return found;
    }
    bool read(const EVENT_PROPERTY_INFO& property, std::vector<BYTE>& bytes, std::size_t maximum) {
        PROPERTY_DATA_DESCRIPTOR descriptor{};
        descriptor.PropertyName = reinterpret_cast<ULONGLONG>(propertyName(property)); descriptor.ArrayIndex = std::numeric_limits<ULONG>::max();
        ULONG size = 0;
        if (TdhGetPropertySize(&event, 1, &context, 1, &descriptor, &size) != ERROR_SUCCESS || !size || size > maximum) return false;
        bytes.resize(size);
        return TdhGetProperty(&event, 1, &context, 1, &descriptor, size, bytes.data()) == ERROR_SUCCESS;
    }
    EVENT_RECORD& event;
    TDH_CONTEXT context{};
    std::vector<std::byte> schema;
    TRACE_EVENT_INFO* info{};
    ULONG used{};
};

struct Properties {
    EVENT_TRACE_PROPERTIES value{};
    wchar_t name[128]{};
};

}

class EtwCollector::Run : public std::enable_shared_from_this<Run> {
public:
    explicit Run(std::shared_ptr<ProcessEventStore> value) : events(std::move(value)) {}
    void stop() noexcept {
        stopRequested = true; ++stopVersion; events->requestStop(); wake.notify_all();
    }
    void control() noexcept;
    std::atomic<bool> complete{};
private:
    static void WINAPI eventCallback(EVENT_RECORD* record) noexcept {
        auto* self = static_cast<Run*>(record->UserContext);
        if (!self || !IsEqualGUID(record->EventHeader.ProviderId, processProvider)) return;
        const auto opcode = record->EventHeader.EventDescriptor.Opcode;
        if (opcode != EVENT_TRACE_TYPE_START && opcode != EVENT_TRACE_TYPE_END) return;
        try {
            EventProperties data(*record);
            ProcessEventRecord event;
            if (!data.unsigned32(L"ProcessId", event.pid)) { self->events->decodeFailure(); return; }
            event.kind = opcode == EVENT_TRACE_TYPE_START ? ProcessEventKind::started : ProcessEventKind::stopped;
            event.timestampQpc = record->EventHeader.TimeStamp.QuadPart;
            if (event.timestampQpc < 0) { self->events->decodeFailure(); return; }
            event.observedAtNs = measurementTime(std::chrono::steady_clock::now());
            if (data.unsigned32(L"ParentId", event.parentPid)) event.flags |= eventParentPid;
            else event.flags |= eventPartial;
            if (event.kind == ProcessEventKind::stopped && data.signed32(L"ExitStatus", event.exitStatus)) event.flags |= eventExitStatus;
            if (data.image(event.imageName)) event.flags |= eventImageName;
            else { event.imageName.clear(); event.flags |= eventPartial; }
            if (event.flags & eventPartial) self->events->decodeFailure();
            self->events->push(std::move(event));
        } catch (...) { self->events->decodeFailure(); }
    }
    Properties properties() const {
        Properties result;
        result.value.Wnode.BufferSize = sizeof(result); result.value.Wnode.Guid = guid; result.value.Wnode.Flags = WNODE_FLAG_TRACED_GUID;
        result.value.LoggerNameOffset = offsetof(Properties, name);
        result.value.LogFileMode = EVENT_TRACE_SYSTEM_LOGGER_MODE | EVENT_TRACE_REAL_TIME_MODE | EVENT_TRACE_NO_PER_PROCESSOR_BUFFERING;
        wcscpy_s(result.name, name.data()); return result;
    }
    void statistics(const Properties& properties) {
        const auto& current = properties.value;
        events->lost(static_cast<ULONG>(current.EventsLost - lastEventsLost),
            static_cast<ULONG>(current.RealTimeBuffersLost - lastBuffersLost));
        lastEventsLost = current.EventsLost; lastBuffersLost = current.RealTimeBuffersLost;
    }
    bool stopSession(TRACEHANDLE session) {
        auto data = properties();
        const auto status = ControlTraceW(session, nullptr, &data.value, EVENT_TRACE_CONTROL_STOP);
        if (status == ERROR_SUCCESS) { statistics(data); return true; }
        if (status == ERROR_MORE_DATA) { events->decodeFailure(); return true; }
        if (status == ERROR_WMI_INSTANCE_NOT_FOUND) return true;
        events->state(ProcessEventMonitorState::cleanupFailed, status, true); return false;
    }
    std::shared_ptr<ProcessEventStore> events;
    std::atomic<bool> stopRequested{}, consumerDone{};
    std::atomic<std::uint64_t> stopVersion{};
    std::mutex mutex;
    std::condition_variable wake;
    GUID guid{};
    std::array<wchar_t, 128> name{};
    ULONG lastEventsLost{}, lastBuffersLost{}, consumerStatus{};
};

void EtwCollector::Run::control() noexcept
{
    TRACEHANDLE session = 0;
    TRACEHANDLE consumer = INVALID_PROCESSTRACE_HANDLE;
    std::thread reader;
    ULONG failure = ERROR_SUCCESS;
    bool owned = false;
    try {
        LARGE_INTEGER frequency{};
        if (!QueryPerformanceFrequency(&frequency) || frequency.QuadPart <= 0) throw std::runtime_error("QPC unavailable");
        events->initialize(queryBootIdentity(systemQuery), frequency.QuadPart);
        const auto created = CoCreateGuid(&guid);
        if (FAILED(created)) { failure = created == E_OUTOFMEMORY ? ERROR_NOT_ENOUGH_MEMORY : ERROR_GEN_FAILURE; }
        if (!failure) {
            swprintf_s(name.data(), name.size(), L"NksTaskMgr-Processes-%08lx-%04x-%04x-%02x%02x-%02x%02x%02x%02x%02x%02x",
                guid.Data1, guid.Data2, guid.Data3, guid.Data4[0], guid.Data4[1], guid.Data4[2], guid.Data4[3], guid.Data4[4], guid.Data4[5], guid.Data4[6], guid.Data4[7]);
            if (!stopRequested) {
                auto data = properties(); data.value.Wnode.ClientContext = 1;
                data.value.BufferSize = 64; data.value.MinimumBuffers = 2; data.value.MaximumBuffers = 16;
                data.value.FlushTimer = 1; data.value.EnableFlags = EVENT_TRACE_FLAG_PROCESS;
                failure = StartTraceW(&session, name.data(), &data.value); owned = failure == ERROR_SUCCESS;
            }
        }
        if (owned && !stopRequested) {
            EVENT_TRACE_LOGFILEW log{}; log.LoggerName = name.data(); log.Context = this;
            log.ProcessTraceMode = PROCESS_TRACE_MODE_REAL_TIME | PROCESS_TRACE_MODE_EVENT_RECORD | PROCESS_TRACE_MODE_RAW_TIMESTAMP;
            log.EventRecordCallback = eventCallback;
            consumer = OpenTraceW(&log);
            if (consumer == INVALID_PROCESSTRACE_HANDLE) failure = GetLastError();
            else {
                const auto self = shared_from_this();
                reader = std::thread([self, consumer] {
                    auto handle = consumer;
                    self->consumerStatus = ProcessTrace(&handle, 1, nullptr, nullptr);
                    self->consumerDone = true; self->wake.notify_all();
                });
                if (!stopRequested) events->state(ProcessEventMonitorState::running, 0, true);
                while (!stopRequested && !consumerDone) {
                    std::unique_lock lock(mutex);
                    wake.wait_for(lock, std::chrono::seconds(1), [&] { return stopRequested || consumerDone; });
                    lock.unlock();
                    if (!stopRequested && !consumerDone) {
                        auto data = properties();
                        const auto status = ControlTraceW(session, nullptr, &data.value, EVENT_TRACE_CONTROL_QUERY);
                        if (status == ERROR_SUCCESS) statistics(data);
                        else { failure = status; break; }
                    }
                }
                if (consumerDone && !stopRequested) failure = consumerStatus ? consumerStatus : ERROR_OPERATION_ABORTED;
            }
        }
    } catch (const std::bad_alloc&) { failure = ERROR_NOT_ENOUGH_MEMORY; }
    catch (...) { failure = ERROR_GEN_FAILURE; }
    if (owned) {
        events->requestStop();
        auto attemptedVersion = stopVersion.load();
        owned = !stopSession(session);
        if (consumer != INVALID_PROCESSTRACE_HANDLE) CloseTrace(consumer);
        if (reader.joinable()) reader.join();
        while (owned) {
            std::unique_lock lock(mutex);
            wake.wait(lock, [&] { return stopVersion.load() != attemptedVersion; });
            attemptedVersion = stopVersion.load(); lock.unlock();
            events->requestStop(); owned = !stopSession(session);
        }
    } else if (reader.joinable()) reader.join();
    events->state(failure ? errorState(failure) : ProcessEventMonitorState::stopped, failure);
    complete = true; sessionLease = false;
}

EtwCollector::EtwCollector(std::shared_ptr<ProcessEventStore> value) : events(std::move(value)) {}
EtwCollector::~EtwCollector() { stop(); }

void EtwCollector::start()
{
    std::lock_guard lock(mutex);
    if (run && !run->complete) return;
    if (sessionLease.exchange(true)) { events->state(ProcessEventMonitorState::busy, ERROR_BUSY); return; }
    try {
        if (!pinModule()) { events->state(ProcessEventMonitorState::error, GetLastError()); sessionLease = false; return; }
        run = std::make_shared<Run>(events);
        events->state(ProcessEventMonitorState::starting, 0, true);
        std::thread([active = run] { active->control(); }).detach();
    } catch (...) { run.reset(); sessionLease = false; events->state(ProcessEventMonitorState::error, ERROR_NOT_ENOUGH_MEMORY); throw; }
}

void EtwCollector::stop() noexcept
{
    std::lock_guard lock(mutex);
    if (run && !run->complete) run->stop();
}

}
