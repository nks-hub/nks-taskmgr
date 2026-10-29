#include "core/service_monitor.hpp"
#include "collectors/service_collector.hpp"
#include <algorithm>
#include <iostream>
#include <stdexcept>

namespace {
void require(bool value, const char* message) { if (!value) throw std::runtime_error(message); }

void filtering()
{
    nks::ServiceRecord record;
    record.name = L"DemoSvc"; record.title = L"Síťová služba"; record.pid = 314;
    record.state = SERVICE_RUNNING; record.config.account = L"LocalService";
    require(nks::serviceMatches(record, nks::ServiceFilter::running, L"SÍŤOVÁ"), "Unicode service search failed");
    require(nks::serviceMatches(record, nks::ServiceFilter::all, L"314"), "Service PID search failed");
    require(nks::serviceMatches(record, nks::ServiceFilter::all, L"localservice"), "Service account search failed");
    require(!nks::serviceMatches(record, nks::ServiceFilter::stopped, L""), "Running service passed stopped filter");
    record.state = SERVICE_PAUSED;
    require(!nks::serviceMatches(record, nks::ServiceFilter::stopped, L""), "Paused service was mislabeled stopped");
    require(nks::serviceMatches(record, nks::ServiceFilter::other, L""), "Paused service disappeared from other states");
    record.config.error = ERROR_ACCESS_DENIED;
    require(nks::serviceStartup(record.config) == L"Chybí oprávnění", "Access denial became a startup value");
    nks::ProcessSnapshot processes;
    nks::ProcessRecord host; host.identity = {1, 314, nks::GenerationKind::creationTime, 11};
    processes.processes.push_back(host);
    nks::ServiceDetail detail; detail.name = L"demosvc"; detail.state = SERVICE_RUNNING; detail.pid = 314; detail.hostCreated100ns = 10;
    record.state = SERVICE_RUNNING;
    require(!nks::serviceHostIdentity(record, detail, processes), "A recycled host PID opened a different process");
    processes.processes[0].identity.generation = 10;
    require(nks::serviceHostIdentity(record, detail, processes).has_value(), "Case-insensitive service identity lost a verified host");
    detail.hostError = ERROR_ACCESS_DENIED;
    require(!nks::serviceHostIdentity(record, detail, processes), "An unverified host became a process target");
}

void liveServices()
{
    nks::ServiceCollector collector;
    const auto snapshot = collector.collect();
    require(!snapshot.services.empty(), "SCM returned no accessible services");
    const auto candidate = std::find_if(snapshot.services.begin(), snapshot.services.end(), [](const auto& record) {
        return record.state == SERVICE_RUNNING && record.pid && !record.config.error;
    });
    require(candidate != snapshot.services.end(), "No queryable running service found");
    const auto detail = collector.detail(candidate->name);
    require(detail.name == candidate->name && !detail.config.error && !detail.statusError, "Real service detail could not be read");
    require(!detail.config.account.empty() && !detail.config.command.empty(), "Service configuration lost account or command");
    require(!detail.descriptionError, "The description query rejected a readable service");
    if (!detail.hostError && detail.state == SERVICE_RUNNING)
        require(detail.hostCreated100ns != 0 && detail.pid != 0, "Service host creation time was lost");
    const auto missing = collector.detail(L"NksTaskMgr-Nonexistent-Service-Check");
    require(missing.config.error == ERROR_SERVICE_DOES_NOT_EXIST && missing.statusError == ERROR_SERVICE_DOES_NOT_EXIST,
        "A missing service became an empty successful detail");
    bool rejected = false;
    try { collector.detail(std::wstring(L"a\0b", 3)); } catch (const std::invalid_argument&) { rejected = true; }
    require(rejected, "An embedded null truncated the service target");
    std::cout << "SCM services=" << snapshot.services.size() << "; collection_ms="
        << std::chrono::duration<double, std::milli>(snapshot.collectionTime).count() << '\n';
}

void backgroundServices()
{
    using namespace std::chrono_literals;
    nks::ServiceMonitor monitor;
    monitor.start();
    std::wstring error;
    std::this_thread::sleep_for(100ms);
    require(!monitor.take(error), "Inactive service view collected data");
    monitor.setActive(true);
    std::shared_ptr<const nks::ServiceSnapshot> snapshot;
    const auto deadline = std::chrono::steady_clock::now() + 8s;
    while (std::chrono::steady_clock::now() < deadline && !snapshot) {
        snapshot = monitor.take(error);
        require(error.empty(), "Service background collection failed");
        std::this_thread::sleep_for(20ms);
    }
    require(snapshot && !snapshot->services.empty(), "Service view did not receive a background snapshot");
    monitor.select(snapshot->services.front().name);
    std::shared_ptr<const nks::ServiceDetail> detail;
    while (std::chrono::steady_clock::now() < deadline && !detail) {
        detail = monitor.takeDetail(error);
        require(error.empty(), "Service background detail failed");
        std::this_thread::sleep_for(20ms);
    }
    require(detail && detail->name == snapshot->services.front().name, "Background selection detail was lost");
    monitor.stop();
    monitor.take(error); monitor.takeDetail(error);
    std::this_thread::sleep_for(100ms);
    require(!monitor.take(error) && !monitor.takeDetail(error), "Stopped service worker kept publishing");
}
}

int main()
{
    try { filtering(); liveServices(); backgroundServices(); return 0; }
    catch (const std::exception& error) { std::cerr << error.what() << '\n'; return 1; }
}
