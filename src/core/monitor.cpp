#include "monitor.hpp"
#include "collector_client.hpp"
#include "process_sampler.hpp"
#include <windows.h>
#include <stdexcept>

namespace nks {

Monitor::~Monitor() { stop(); }

void Monitor::start()
{
    if (started) throw std::logic_error("Monitor has already been started");
    started = true;
    try {
        processThread = std::jthread([this](std::stop_token stop) {
            SetThreadDescription(GetCurrentThread(), L"NKS process collector");
            try {
                CollectorClient collector(collector::Kind::processes);
                // System CPU deltas follow every read; process deltas only follow full reads.
                ProcessSampler sampler, systemSampler;
                std::chrono::steady_clock::time_point lastFull{};
                while (!stop.stop_requested()) {
                    if (!paused()) {
                        try {
                            // Slack below one interval keeps the full read on the third tick despite timer jitter.
                            const bool systemOnly = background() && lastFull != std::chrono::steady_clock::time_point{}
                                && std::chrono::steady_clock::now() - lastFull < backgroundProcessInterval - std::chrono::milliseconds(interval() / 2);
                            auto sample = collector.processes(stop, systemOnly);
                            if (stop.stop_requested()) break;
                            auto processes = std::move(sample.processes);
                            auto system = sample;
                            sample.processes = std::move(processes);
                            systemSampler.apply(system);
                            try { history.append(system); } catch (const std::exception&) { history.failed(); }
                            if (systemOnly) { wait(stop); continue; }
                            sampler.apply(sample);
                            sample.systemCpuPercent = system.systemCpuPercent; sample.processors = system.processors;
                            sample.measurements.systemCpu = system.measurements.systemCpu; sample.measurements.logicalCpu = system.measurements.logicalCpu;
                            lastFull = sample.measuredAt;
                            processSlot.publish(std::move(sample));
                        } catch (const std::exception& error) {
                            try { history.appendGap(true, std::chrono::steady_clock::now()); } catch (const std::exception&) { history.failed(); }
                            processSlot.fail(error.what());
                        }
                    }
                    wait(stop);
                }
            } catch (const std::exception& error) { processSlot.fail(error.what()); }
        });
        resourceThread = std::jthread([this](std::stop_token stop) {
            SetThreadDescription(GetCurrentThread(), L"NKS resource collector");
            try {
                CollectorClient collector(collector::Kind::resources);
                NetworkTransferTotals transfers;
                while (!stop.stop_requested()) {
                    if (!paused()) {
                        try {
                            auto sample = collector.resources(stop, cpuSensorsState.load());
                            if (stop.stop_requested()) break;
                            transfers.apply(sample, std::chrono::duration_cast<std::chrono::nanoseconds>(
                                std::chrono::system_clock::now().time_since_epoch()).count());
                            try { history.append(sample); } catch (const std::exception&) { history.failed(); }
                            resourceSlot.publish(std::move(sample));
                        } catch (const std::exception& error) {
                            try { history.appendGap(false, std::chrono::steady_clock::now()); } catch (const std::exception&) { history.failed(); }
                            resourceSlot.fail(error.what());
                        }
                    }
                    wait(stop);
                }
            } catch (const std::exception& error) { resourceSlot.fail(error.what()); }
        });
    } catch (const std::system_error&) {
        stop();
        throw;
    } catch (const std::bad_alloc&) {
        stop();
        throw;
    }
}

void Monitor::stop()
{
    processThread.request_stop();
    resourceThread.request_stop();
    wake.notify_all();
    if (processThread.joinable()) processThread.join();
    if (resourceThread.joinable()) resourceThread.join();
}

void Monitor::wait(std::stop_token stop)
{
    std::unique_lock lock(waitMutex);
    const auto version = wakeVersion;
    wake.wait_for(lock, stop, std::chrono::milliseconds(interval()), [&] { return wakeVersion != version; });
}

void Monitor::setBackground(bool value)
{
    if (backgroundState.exchange(value) == value) return;
    { std::lock_guard lock(waitMutex); ++wakeVersion; }
    // Returning to the foreground collects a full process list immediately instead of after the next interval.
    wake.notify_all();
}

void Monitor::setInterval(unsigned milliseconds)
{
    if (milliseconds != 250 && milliseconds != 1000) throw std::invalid_argument("Refresh interval must be 250 or 1000 milliseconds");
    intervalMilliseconds.store(milliseconds);
}

}
