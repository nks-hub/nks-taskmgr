#include "core/monitor.hpp"
#include "collectors/process_identity.hpp"
#include <windows.h>
#include <algorithm>
#include <iostream>
#include <stdexcept>

namespace {
void require(bool condition, const char* message) { if (!condition) throw std::runtime_error(message); }

void latestSample()
{
    nks::SnapshotSlot<nks::ProcessSnapshot> slot;
    for (unsigned i = 1; i <= 3; ++i) {
        nks::ProcessSnapshot snapshot;
        snapshot.logicalProcessors = i;
        slot.publish(std::move(snapshot));
    }
    std::wstring error;
    auto latest = slot.take(error);
    require(latest && latest->logicalProcessors == 3, "Mailbox queued old samples instead of retaining the newest");
    require(!slot.take(error), "Mailbox returned a consumed sample twice");
    slot.fail("Collection failed");
    require(!slot.take(error) && error == L"Collection failed", "Mailbox lost its error");
    slot.publish(nks::ProcessSnapshot{});
    require(slot.take(error) && error.empty(), "A successful sample did not clear its collection error");
    slot.publish(nks::ProcessSnapshot{}); slot.fail("Old selection"); slot.clear();
    require(!slot.take(error) && error.empty(), "Invalidation retained the old selection or error");
}

void liveMonitor()
{
    using namespace std::chrono_literals;
    nks::Monitor monitor;
    monitor.setInterval(250);
    bool rejected = false;
    try { monitor.setInterval(0); } catch (const std::invalid_argument&) { rejected = true; }
    require(rejected && monitor.interval() == 250, "An invalid refresh interval changed the monitor cadence");
    monitor.setPaused(true);
    monitor.start();
    std::this_thread::sleep_for(300ms);
    std::wstring processError, resourceError;
    require(!monitor.takeProcesses(processError) && !monitor.takeResources(resourceError), "A paused monitor published a sample");
    monitor.setPaused(false);
    std::shared_ptr<const nks::ProcessSnapshot> processes;
    std::shared_ptr<const nks::ResourceSnapshot> resources;
    const auto deadline = std::chrono::steady_clock::now() + 8s;
    while (std::chrono::steady_clock::now() < deadline) {
        if (auto sample = monitor.takeProcesses(processError)) processes = std::move(sample);
        if (auto sample = monitor.takeResources(resourceError)) resources = std::move(sample);
        require(processError.empty() && resourceError.empty(), "The live monitor reported a collection failure");
        if (processes && processes->systemCpuPercent.valid() && resources) break;
        std::this_thread::sleep_for(20ms);
    }
    require(processes && processes->systemCpuPercent.valid() && resources, "The monitor did not publish both streams after resuming");
    const auto own = std::find_if(processes->processes.begin(), processes->processes.end(), [](const auto& process) { return process.identity.pid == GetCurrentProcessId(); });
    require(own != processes->processes.end(), "The monitor did not retain the real process identity");
    // Builds with SystemBasicProcessInformation publish sequence numbers; older builds publish creation times.
    const auto kind = own->identity.generationKind;
    const auto expected = nks::queryProcessIdentity(GetCurrentProcess(), kind);
    require((kind == nks::GenerationKind::creationTime || kind == nks::GenerationKind::sequenceNumber) && own->identity.generation != 0
        && expected.state == nks::SampleState::valid && expected.identity.generation == own->identity.generation,
        "The monitor did not retain the real process identity");
    rejected = false;
    try { monitor.start(); } catch (const std::logic_error&) { rejected = true; }
    require(rejected, "Starting the same monitor twice created duplicate workers");
    monitor.stop();
    monitor.takeProcesses(processError); monitor.takeResources(resourceError);
    std::this_thread::sleep_for(300ms);
    require(!monitor.takeProcesses(processError) && !monitor.takeResources(resourceError), "A stopped monitor continued publishing samples");
}
}

int main()
{
    try {
        latestSample(); liveMonitor();
        std::cout << "Mailbox coalescing, pause, resume, real process identity and shutdown passed\n";
        return 0;
    } catch (const std::exception& error) {
        std::cerr << error.what() << '\n';
        return 1;
    }
}
