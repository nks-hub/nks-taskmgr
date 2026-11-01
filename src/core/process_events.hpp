#pragma once

#include "process.hpp"
#include <deque>
#include <mutex>

namespace nks {

enum class ProcessEventMonitorState : std::uint32_t {
    idle, starting, running, stopping, stopped, accessDenied, unsupported, error, busy, stopTimedOut, cleanupFailed
};
enum class ProcessEventKind : std::uint32_t { started = 1, stopped = 2 };
enum ProcessEventFlag : std::uint32_t { eventExitStatus = 1, eventImageName = 2, eventPartial = 4, eventParentPid = 8 };
enum ProcessEventFrameFlag : std::uint32_t { eventsResyncRequired = 1, eventsCleanupOutstanding = 2 };

struct ProcessEventRecord {
    std::uint64_t sequence{};
    std::int64_t timestampQpc{}, observedAtNs{};
    ProcessEventKind kind{};
    std::uint32_t pid{}, parentPid{};
    SampleState identityState{SampleState::unavailable};
    GenerationKind identityKind{GenerationKind::missing};
    std::uint64_t generation{};
    std::uint32_t flags{};
    std::int32_t exitStatus{};
    std::wstring imageName;
};

struct ProcessEventBatchInfo {
    ProcessEventMonitorState state{};
    std::uint32_t win32Error{}, flags{};
    BootIdentity boot;
    std::int64_t qpcFrequency{};
    std::uint64_t providerEventsLost{}, providerBuffersLost{}, queueDropped{}, decodeErrors{}, ambiguous{}, resyncVersion{};
};
struct ProcessEventBatch {
    ProcessEventBatchInfo info;
    std::vector<ProcessEventRecord> events;
};

class ProcessEventStore {
public:
    static constexpr std::size_t capacity = 4096, maximumFrameEvents = 512, maximumNameChars = 1024;
    void initialize(BootIdentity boot, std::int64_t qpcFrequency);
    void state(ProcessEventMonitorState value, std::uint32_t error = 0, bool cleanupOutstanding = false);
    void requestStop();
    void push(ProcessEventRecord record);
    void decodeFailure();
    void lost(std::uint64_t events, std::uint64_t buffers);
    void acknowledgeResync(std::uint64_t version);
    ProcessEventBatch read();
private:
    void resync();
    std::mutex mutex;
    std::deque<ProcessEventRecord> records;
    ProcessEventBatchInfo info;
    std::uint64_t sequence{};
    std::chrono::steady_clock::time_point stopRequestedAt{};
};

}
