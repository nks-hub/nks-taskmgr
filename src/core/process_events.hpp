#pragma once

#include "process.hpp"
#include "process_traffic.hpp"
#include <deque>
#include <mutex>
#include <optional>
#include <span>

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
    EventText imageName;
};

struct ProcessEventBatchInfo {
    ProcessEventMonitorState state{};
    std::uint32_t win32Error{}, flags{};
    BootIdentity boot;
    std::int64_t qpcFrequency{};
    std::uint64_t providerEventsLost{}, providerBuffersLost{}, queueDropped{}, decodeErrors{}, ambiguous{}, resyncVersion{};
};
struct ProcessBaselineEntry { std::uint32_t pid{}; std::uint64_t creationTime{}; };
struct ProcessEventBatch {
    ProcessEventBatchInfo info;
    EventVector<ProcessEventRecord> events;
    ProcessTrafficBatch traffic;
};

class ProcessEventStore {
public:
    static constexpr std::size_t capacity = 4096, maximumFrameEvents = 512, maximumNameChars = 1024;
    void initialize(BootIdentity boot, std::int64_t qpcFrequency);
    void state(ProcessEventMonitorState value, std::uint32_t error = 0, bool cleanupOutstanding = false);
    void requestStop();
    void push(ProcessEventRecord record);
    void thread(std::uint32_t pid, std::uint32_t tid, std::int64_t qpc, bool start);
    void traffic(ProcessTrafficObservation observation);
    void diskInitiated(ProcessDiskInitiation initiation);
    void providers(SampleState disk, SampleState network, SampleState identity);
    void identityProvider(SampleState state);
    void decodeFailure();
    void lost(std::uint64_t events, std::uint64_t buffers);
    void acknowledgeResync(std::uint64_t version);
    /// The attribution epoch a new live-process snapshot must be taken in, when the ledger has no baseline for it.
    std::optional<std::uint64_t> baselineWanted();
    /// Applies a snapshot that completed at qpc only if no reset happened since baselineWanted returned epoch.
    bool baseline(std::uint64_t epoch, std::int64_t qpc, std::span<const ProcessBaselineEntry> entries);
    ProcessEventBatch read();
private:
    void resync();
    void invalidate(bool conflict);
    void identityChanges(std::uint64_t evictions, std::uint64_t conflicts);
    void trafficChanges(std::uint64_t epoch, std::uint64_t dropped, std::uint64_t evicted);
    std::mutex mutex;
    std::deque<ProcessEventRecord, EventAllocator<ProcessEventRecord>> records;
    EventIdentityIndex identities;
    ProcessTrafficStore transfers;
    ProcessEventBatchInfo info;
    std::uint64_t sequence{}, baselineEpoch{UINT64_MAX};
    std::chrono::steady_clock::time_point stopRequestedAt{};
};

}
