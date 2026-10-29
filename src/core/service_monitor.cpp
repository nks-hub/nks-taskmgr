#include "service_monitor.hpp"
#include "collectors/service_collector.hpp"
#include <stdexcept>

namespace nks {

ServiceMonitor::~ServiceMonitor() { stop(); }

void ServiceMonitor::start()
{
    if (worker.joinable()) throw std::logic_error("Service monitor already started");
    worker = std::jthread([this](std::stop_token stop) {
        ServiceCollector collector;
        while (!stop.stop_requested()) {
            std::wstring name;
            {
                std::unique_lock lock(mutex);
                wake.wait(lock, stop, [&] { return active && !paused; });
                if (stop.stop_requested()) break;
                name = selected; changed = false;
            }
            try {
                auto snapshot = collector.collect(stop);
                if (!stop.stop_requested()) snapshots.publish(std::move(snapshot));
            } catch (const std::exception& error) { if (!stop.stop_requested()) snapshots.fail(error.what()); }
            if (!stop.stop_requested() && !name.empty()) {
                try { details.publish(collector.detail(name)); }
                catch (const std::exception& error) { details.fail(error.what()); }
            }
            std::unique_lock lock(mutex);
            wake.wait_for(lock, stop, std::chrono::seconds(1), [&] { return changed || !active || paused; });
        }
    });
}

void ServiceMonitor::stop()
{
    worker.request_stop(); wake.notify_all();
    if (worker.joinable()) worker.join();
}

void ServiceMonitor::setActive(bool value)
{
    { std::lock_guard lock(mutex); active = value; }
    wake.notify_all();
}

void ServiceMonitor::setPaused(bool value)
{
    { std::lock_guard lock(mutex); paused = value; }
    wake.notify_all();
}

void ServiceMonitor::select(std::wstring name)
{
    { std::lock_guard lock(mutex); selected = std::move(name); changed = true; }
    wake.notify_all();
}

}
