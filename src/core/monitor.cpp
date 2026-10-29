#include "monitor.hpp"
#include "collectors/process_collector.hpp"
#include "collectors/resource_collector.hpp"
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
                ProcessCollector collector;
                ProcessSampler sampler;
                while (!stop.stop_requested()) {
                    if (!paused()) {
                        try { auto sample = collector.collect(); sampler.apply(sample); processSlot.publish(std::move(sample)); }
                        catch (const std::exception& error) { processSlot.fail(error.what()); }
                    }
                    wait(stop);
                }
            } catch (const std::exception& error) { processSlot.fail(error.what()); }
        });
        resourceThread = std::jthread([this](std::stop_token stop) {
            SetThreadDescription(GetCurrentThread(), L"NKS resource collector");
            try {
                ResourceCollector collector;
                while (!stop.stop_requested()) {
                    if (!paused()) {
                        try { resourceSlot.publish(collector.collect()); }
                        catch (const std::exception& error) { resourceSlot.fail(error.what()); }
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
    wake.wait_for(lock, stop, std::chrono::milliseconds(interval()), [] { return false; });
}

void Monitor::setInterval(unsigned milliseconds)
{
    if (milliseconds != 250 && milliseconds != 1000) throw std::invalid_argument("Refresh interval must be 250 or 1000 milliseconds");
    intervalMilliseconds.store(milliseconds);
}

}
