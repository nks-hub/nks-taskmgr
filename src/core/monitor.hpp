#pragma once

#include "process.hpp"
#include "resource.hpp"
#include "history_stream.hpp"
#include <atomic>
#include <condition_variable>
#include <memory>
#include <mutex>
#include <thread>
#include <utility>

namespace nks {

template<class T>
struct SnapshotSlot {
    std::mutex mutex;
    std::shared_ptr<const T> latest;
    std::wstring error;
    std::uint64_t overwritten{};

    void publish(T snapshot) {
        auto value = std::make_shared<const T>(std::move(snapshot));
        std::lock_guard lock(mutex);
        if (latest) ++overwritten;
        latest = std::move(value);
        error.clear();
    }
    void fail(const std::string& message) {
        std::lock_guard lock(mutex);
        error.assign(message.begin(), message.end());
    }
    // *dropped receives the snapshots published and replaced since the previous take.
    std::shared_ptr<const T> take(std::wstring& failure, std::uint64_t* dropped = nullptr) {
        std::lock_guard lock(mutex);
        failure = error;
        const auto lost = std::exchange(overwritten, 0);
        if (dropped) *dropped = lost;
        return std::exchange(latest, {});
    }
    void clear() {
        std::lock_guard lock(mutex);
        latest.reset(); error.clear(); overwritten = 0;
    }
};

class Monitor {
public:
    ~Monitor();
    void start();
    void stop();
    void setPaused(bool value) { pausedState.store(value); }
    bool paused() const { return pausedState.load(); }
    void setInterval(unsigned milliseconds);
    unsigned interval() const { return intervalMilliseconds.load(); }
    void setCpuSensors(bool value) noexcept { cpuSensorsState.store(value); }
    std::shared_ptr<const ProcessSnapshot> takeProcesses(std::wstring& error, std::uint64_t* dropped = nullptr) { return processSlot.take(error, dropped); }
    std::shared_ptr<const ResourceSnapshot> takeResources(std::wstring& error) { return resourceSlot.take(error); }
    std::unique_ptr<HistoryBatch> takeHistory() { return history.take(); }
    void historyDeliveryFailed() noexcept { history.failed(); }

private:
    void wait(std::stop_token stop);
    SnapshotSlot<ProcessSnapshot> processSlot;
    SnapshotSlot<ResourceSnapshot> resourceSlot;
    HistoryStream history;
    std::atomic<bool> pausedState{};
    std::atomic<bool> cpuSensorsState{};
    std::atomic<unsigned> intervalMilliseconds{1000};
    bool started{};
    std::mutex waitMutex;
    std::condition_variable_any wake;
    std::jthread processThread, resourceThread;
};

}
