#include "core/process_events.hpp"
#include <windows.h>
#include <algorithm>
#include <cstddef>
#include <iostream>
#include <stdexcept>
#include <string>
#include <string_view>

namespace {
using namespace nks;
constexpr std::int64_t arrival = 10'000'000'000, delay = 2'000'000'000;
unsigned cases{};
const char* currentCase{};
void require(bool value, const char* message) { if (!value) throw std::runtime_error(message); }
void seed(EventIdentityIndex& identities)
{
    identities.process(10, 10000, 1, true); identities.thread(10, 100, 2, true);
    identities.process(20, 20000, 1, true); identities.thread(20, 200, 2, true);
}
struct Fixture {
    EventIdentityIndex identities;
    ProcessTrafficStore traffic;
    Fixture() { seed(identities); traffic.initialize(100); traffic.providers(SampleState::gap, SampleState::gap, SampleState::valid); }
};
ProcessDiskInitiation init(std::uint64_t irp = 1000, std::int64_t qpc = 10, std::uint32_t tid = 100,
    ProcessTrafficDirection direction = ProcessTrafficDirection::read, std::int64_t observed = arrival)
{
    return {qpc, observed, irp, tid, direction};
}
ProcessTrafficObservation completion(std::uint64_t irp = 1000, std::int64_t qpc = 30, std::uint32_t bytes = 128,
    ProcessTrafficDirection direction = ProcessTrafficDirection::read, std::int64_t observed = arrival + 100)
{
    return {qpc, observed, 200, bytes, ProcessTrafficSource::physicalDisk, direction, false, irp};
}
ProcessTrafficSample single(const ProcessTrafficBatch& batch)
{
    require(batch.samples.size() == 1, "Expected exactly one changed traffic sample"); return batch.samples.front();
}
void issuing(const ProcessTrafficSample& sample, std::uint32_t pid = 10)
{
    require(sample.attribution == ProcessTrafficAttribution::issuingThreadOwner && sample.pid == pid
        && sample.identityKind == GenerationKind::creationTime && sample.generation == (pid == 10 ? 10000u : 20000u)
        && sample.identityState == SampleState::valid, "INIT did not establish its unique historical process");
    require(sample.state == SampleState::gap && (sample.flags & trafficPartial), "Issuing owner claimed a complete traffic stream");
}
void revoked(const ProcessTrafficSample& before, const ProcessTrafficSample& after)
{
    require(after.sampleId == before.sampleId && after.attributionEpoch == before.attributionEpoch, "Revocation changed the UI upsert identity");
    require(after.attribution == ProcessTrafficAttribution::unknown && after.identityState == SampleState::unavailable
        && after.identityKind == GenerationKind::missing && !after.generation && !after.pid
        && after.state == SampleState::gap && (after.flags & trafficPartial)
        && after.observedBytes == before.observedBytes && after.operations == before.operations, "Revocation retained identity or changed captured bytes");
}
bool noIssuing(const ProcessTrafficBatch& batch)
{
    return std::none_of(batch.samples.begin(), batch.samples.end(), [](const auto& row) {
        return row.attribution == ProcessTrafficAttribution::issuingThreadOwner || row.attribution == ProcessTrafficAttribution::direct;
    });
}
ProcessTrafficSample publish(Fixture& fixture)
{
    fixture.traffic.diskInitiated(init(), fixture.identities); fixture.traffic.observe(completion(), fixture.identities);
    const auto row = single(fixture.traffic.read(arrival + delay + 1000, fixture.identities)); issuing(row); return row;
}
void ownerAtInitiation()
{
    Fixture f; f.identities.thread(10, 100, 20, false); f.identities.process(10, 10000, 21, false);
    const auto row = publish(f);
    require(row.pid != 20 && row.observedBytes == 128 && row.operations == 1, "Completion owner replaced the INIT owner");
}
void completionArrivesFirst()
{
    Fixture f; f.traffic.observe(completion(), f.identities);
    f.traffic.diskInitiated(init(1000, 10, 100, ProcessTrafficDirection::read, arrival + 200), f.identities);
    require(f.traffic.read(arrival + delay - 1, f.identities).samples.empty(), "Completion bypassed its 2 s reorder delay");
    issuing(single(f.traffic.read(arrival + delay + 1000, f.identities)));
}
void reuseBeforeDrain(bool reverse)
{
    Fixture f;
    if (reverse) {
        f.traffic.observe(completion(1000, 40, 200), f.identities); f.traffic.diskInitiated(init(1000, 30, 200), f.identities);
        f.traffic.observe(completion(1000, 20, 100), f.identities); f.traffic.diskInitiated(init(), f.identities);
    } else {
        f.traffic.diskInitiated(init(), f.identities); f.traffic.observe(completion(1000, 20, 100), f.identities);
        f.traffic.diskInitiated(init(1000, 30, 200), f.identities); f.traffic.observe(completion(1000, 40, 200), f.identities);
    }
    const auto batch = f.traffic.read(arrival + delay + 1000, f.identities);
    require(batch.samples.size() == 2 && !batch.info.identityConflicts, "Separated reuse conflicted inside the 2 s queue");
    for (const auto& row : batch.samples) {
        issuing(row, row.pid);
        require(row.operations == 1 && row.observedBytes == (row.pid == 10 ? 100u : 200u), "Reuse mixed owners or bytes");
    }
}
void longIo()
{
    Fixture f; f.traffic.diskInitiated(init(), f.identities);
    require(f.traffic.read(arrival + 60 * delay, f.identities).samples.empty(), "An INIT alone created traffic");
    f.traffic.observe(completion(1000, 5000, 128, ProcessTrafficDirection::read, arrival + 60 * delay), f.identities);
    const auto row = single(f.traffic.read(arrival + 61 * delay + 1000, f.identities)); issuing(row);
    require(row.startQpc == 5000, "Long I/O expired its INIT or used the wrong rate window");
}
void distinctOrigins()
{
    Fixture f; f.traffic.diskInitiated(init(), f.identities); f.traffic.observe(completion(), f.identities);
    auto disk = completion(0, 31, 50); disk.pidOrTid = 100; f.traffic.observe(disk, f.identities);
    auto tcp = completion(0, 32, 40, ProcessTrafficDirection::sent); tcp.source = ProcessTrafficSource::tcp; tcp.pidOrTid = 10;
    f.traffic.observe(tcp, f.identities);
    auto udp = tcp; udp.source = ProcessTrafficSource::udp; udp.pidOrTid = 999; f.traffic.observe(udp, f.identities);
    const auto batch = f.traffic.read(arrival + delay + 1000, f.identities);
    require(batch.samples.size() == 4, "Attribution origins collapsed into one aggregate");
    for (const auto& row : batch.samples) {
        if (row.observedBytes == 128) issuing(row);
        else if (row.source == ProcessTrafficSource::tcp) require(row.attribution == ProcessTrafficAttribution::direct
            && row.identityState == SampleState::valid && row.pid == 10 && row.generation == 10000, "A witnessed PID lifetime did not prove TCP ownership");
        else require(row.attribution == (row.source == ProcessTrafficSource::udp ? ProcessTrafficAttribution::unknown
            : ProcessTrafficAttribution::correlated) && row.identityState == SampleState::unavailable, "Correlation was promoted to proven ownership");
    }
}
void missingAndConflictingEvidence()
{
    { Fixture f; f.traffic.observe(completion(), f.identities);
        require(single(f.traffic.read(arrival + delay + 1000, f.identities)).attribution == ProcessTrafficAttribution::unknown,
            "Missing INIT fell back to the completion thread"); }
    { Fixture f; f.traffic.diskInitiated(init(), f.identities);
        f.traffic.observe(completion(1000, 30, 128, ProcessTrafficDirection::write), f.identities);
        const auto batch = f.traffic.read(arrival + delay + 1000, f.identities);
        require(noIssuing(batch) && batch.info.identityConflicts, "Mismatched directions were accepted"); }
    { Fixture f; f.traffic.diskInitiated(init(), f.identities); f.traffic.diskInitiated(init(1000, 15), f.identities);
        f.traffic.observe(completion(), f.identities);
        const auto batch = f.traffic.read(arrival + delay + 1000, f.identities);
        require(noIssuing(batch) && batch.info.identityConflicts, "Overlapping INITs selected an arbitrary owner"); }
    { Fixture f; f.traffic.diskInitiated(init(), f.identities); f.traffic.observe(completion(1000, 10), f.identities);
        require(noIssuing(f.traffic.read(arrival + delay + 1000, f.identities)), "Equal INIT/completion QPC used callback arrival order"); }
    { Fixture f; f.traffic.diskInitiated(init(), f.identities); f.traffic.observe(completion(1000, 20), f.identities);
        f.traffic.diskInitiated(init(1000, 20), f.identities); f.traffic.observe(completion(1000, 30), f.identities);
        require(noIssuing(f.traffic.read(arrival + delay + 1000, f.identities)), "Equal-QPC reuse claimed a strict boundary"); }
}
void duplicateAndLateEvidence()
{
    for (bool duplicateInit : {false, true}) {
        Fixture f; const auto before = publish(f);
        if (duplicateInit) f.traffic.diskInitiated(init(), f.identities); else f.traffic.observe(completion(), f.identities);
        revoked(before, single(f.traffic.read(arrival + delay + 2000, f.identities)));
    }
    { Fixture f; f.traffic.diskInitiated(init(), f.identities); f.traffic.diskInitiated(init(), f.identities);
        f.traffic.observe(completion(), f.identities);
        require(noIssuing(f.traffic.read(arrival + delay + 1000, f.identities)), "Duplicate INIT survived before publication"); }
    { Fixture f; f.traffic.diskInitiated(init(), f.identities); f.traffic.observe(completion(), f.identities);
        f.traffic.observe(completion(), f.identities);
        require(noIssuing(f.traffic.read(arrival + delay + 1000, f.identities)), "Duplicate completion survived before publication"); }
    { Fixture f; const auto before = publish(f); f.traffic.diskInitiated(init(9999, 15), f.identities);
        revoked(before, single(f.traffic.read(arrival + delay + 2000, f.identities))); }
}
void strictLifetimes()
{
    EventIdentityIndex identities;
    identities.process(10, 10000, 10, true); identities.process(10, 10000, 40, false);
    identities.thread(10, 100, 11, true); identities.thread(10, 100, 50, false);
    require(!identities.threadOwnerAt(100, 40).creationTime && identities.threadOwnerAt(100, 12).creationTime == 10000,
        "A process endpoint was accepted as strict ownership");
    identities.clear(); seed(identities); identities.thread(10, 100, 20, false); identities.thread(20, 100, 20, true);
    require(!identities.threadOwnerAt(100, 2).creationTime && !identities.threadOwnerAt(100, 20).creationTime
        && identities.threadAt(100, 20).pid == 20 && identities.threadOwnerAt(100, 21).pid == 20,
        "Inclusive thread correlation replaced strict INIT ownership");
    identities.clear(); seed(identities); identities.process(10, 11111, 5, true);
    require(!identities.threadOwnerAt(100, 10).creationTime, "Overlapping process generations produced a unique owner");
    identities.clear(); seed(identities); identities.thread(20, 100, 5, true);
    require(!identities.threadOwnerAt(100, 10).creationTime, "Overlapping thread generations produced a unique owner");
    identities.clear(); identities.thread(10, 100, 2, true); identities.process(10, 10000, 20, false);
    require(!identities.threadOwnerAt(100, 10).creationTime, "A stop without a witnessed process start established ownership");
    identities.clear(); identities.process(100, 1, 10, true); identities.thread(100, 7, 20, true);
    identities.process(100, 1, 30, false); identities.process(100, 2, 40, true);
    require(!identities.threadOwnerAt(7, 50).creationTime, "An old TID was reassigned to a reused PID's new process generation");
    { Fixture f; f.identities.clear(); f.identities.process(100, 1, 10, true); f.identities.thread(100, 7, 20, true);
        f.identities.process(100, 1, 30, false); f.identities.process(100, 2, 40, true);
        f.traffic.diskInitiated(init(1000, 50, 7), f.identities); f.traffic.observe(completion(1000, 60), f.identities);
        require(noIssuing(f.traffic.read(arrival + delay + 1000, f.identities)), "Traffic paired an old thread with a reused PID"); }
    identities.clear(); identities.process(100, 1, 10, true); identities.thread(100, 7, 10, true);
    require(!identities.threadOwnerAt(7, 20).creationTime, "Thread start exactly on process start established strict ownership");
}
ProcessEventRecord start(std::uint32_t pid = 10, std::uint64_t created = 10000, std::int64_t qpc = 1)
{
    ProcessEventRecord row; row.kind = ProcessEventKind::started; row.pid = pid; row.generation = created;
    row.identityKind = GenerationKind::creationTime; row.timestampQpc = qpc; row.observedAtNs = 1; return row;
}
void seed(ProcessEventStore& store)
{
    BootIdentity boot; boot.id = {1, 2}; boot.state = SampleState::valid;
    store.initialize(boot, 100); store.push(start()); store.thread(10, 100, 2, true);
}
ProcessTrafficSample publish(ProcessEventStore& store)
{
    seed(store); store.diskInitiated(init(1000, 10, 100, ProcessTrafficDirection::read, 1));
    store.traffic(completion(1000, 30, 128, ProcessTrafficDirection::read, 1));
    const auto row = single(store.read().traffic); issuing(row); return row;
}
void lossAndMetadataRevocations()
{
    for (unsigned mode = 0; mode < 4; ++mode) {
        ProcessEventStore store; const auto before = publish(store);
        if (mode == 0) store.lost(1, 0);
        else if (mode == 1) store.decodeFailure();
        else if (mode == 2) store.thread(10, 100, 10, false);
        else store.push(start(10, 11000, 10));
        const auto batch = store.read(); revoked(before, single(batch.traffic));
        require(batch.traffic.info.attributionEpoch != before.attributionEpoch, "Loss/late metadata did not advance epoch");
        if (mode < 2) require(!(batch.traffic.samples.front().flags & trafficIdentityConflict), "Loss became a proven identity conflict");
    }
}
void epochSeparation()
{
    Fixture f; f.traffic.diskInitiated(init(), f.identities); f.traffic.invalidate(false); f.identities.clear(); seed(f.identities);
    f.traffic.observe(completion(), f.identities);
    require(noIssuing(f.traffic.read(arrival + delay + 1000, f.identities)), "An INIT crossed an attribution epoch");
    f.traffic.diskInitiated(init(2000, 50), f.identities); f.traffic.observe(completion(2000, 60), f.identities);
    const auto batch = f.traffic.read(arrival + delay + 1000, f.identities);
    require(std::any_of(batch.samples.begin(), batch.samples.end(), [](const auto& row) {
        return row.attribution == ProcessTrafficAttribution::issuingThreadOwner && row.pid == 10;
    }), "A strict new pair could not recover in a new epoch");
}
void completedStream()
{
    Fixture f; f.traffic.initialize(1000000);
    const auto epoch = f.traffic.info.attributionEpoch;
    for (std::int64_t i = 0; i < 10000; ++i) {
        const auto observed = arrival + i * 3'000'000'000;
        f.traffic.diskInitiated(init(1000 + static_cast<std::uint64_t>(i), 10 + 2 * i, 100, ProcessTrafficDirection::read, observed), f.identities);
        f.traffic.observe(completion(1000 + static_cast<std::uint64_t>(i), 11 + 2 * i, 1,
            ProcessTrafficDirection::read, observed + 100), f.identities);
    }
    const auto row = single(f.traffic.read(arrival + 10000 * 3'000'000'000LL, f.identities)); issuing(row);
    require(row.operations == 10000 && row.observedBytes == 10000 && f.traffic.info.attributionEpoch == epoch
        && !f.traffic.info.pendingDropped, "Completed operations accumulated as open evidence or destroyed running-process attribution");
}
void evidenceLimit()
{
    Fixture f; const auto before = publish(f);
    for (std::size_t i = 0; i <= ProcessTrafficStore::maximumDiskEvidence; ++i)
        f.traffic.diskInitiated(init(10000 + i, 100), f.identities);
    revoked(before, single(f.traffic.read(arrival + delay + 2000, f.identities)));
    require(f.traffic.info.pendingDropped && eventBudgetUsed.load() <= eventBudgetLimit, "Open IRPs exceeded fixed/shared budget limits");
}
void identityEviction()
{
    ProcessEventStore store; const auto before = publish(store);
    for (std::uint32_t i = 0; i <= EventIdentityIndex::maximumProcesses; ++i) store.push(start(1000 + i, 20000 + i, 1000 + i));
    revoked(before, single(store.read().traffic));
}
void sampleEviction()
{
    Fixture f; const auto before = publish(f);
    for (std::size_t i = 1; i <= ProcessTrafficStore::maximumSamples; ++i) {
        const auto qpc = static_cast<std::int64_t>(i * 100);
        f.traffic.diskInitiated(init(10000 + i, qpc + 10), f.identities);
        f.traffic.observe(completion(10000 + i, qpc + 30), f.identities);
    }
    const auto batch = f.traffic.read(arrival + delay + 2000, f.identities);
    const auto found = std::find_if(batch.samples.begin(), batch.samples.end(), [&](const auto& row) { return row.sampleId == before.sampleId; });
    require(found != batch.samples.end(), "History pressure erased a published owner's revocation"); revoked(before, *found);
}
void allocationFailure()
{
    Fixture f; const auto before = publish(f);
    {
        const auto available = eventBudgetLimit - eventBudgetUsed.load();
        require(available > 64, "No allocator headroom for controlled failure");
        EventVector<std::byte> filler(available - 64);
        f.traffic.diskInitiated(init(2000, 50), f.identities);
        require(eventBudgetUsed.load() <= eventBudgetLimit, "Shared allocator exceeded 8 MiB");
    }
    revoked(before, single(f.traffic.read(arrival + delay + 2000, f.identities)));
}
ProcessTrafficObservation tcp(std::uint32_t pid, std::int64_t qpc, std::uint32_t bytes = 100, std::int64_t observed = arrival)
{
    return {qpc, observed, pid, bytes, ProcessTrafficSource::tcp, ProcessTrafficDirection::sent, false, 0};
}
std::uint64_t ownerOf(const ProcessTrafficBatch& batch, std::uint64_t bytes)
{
    const auto found = std::find_if(batch.samples.begin(), batch.samples.end(), [&](const auto& row) { return row.observedBytes == bytes; });
    require(found != batch.samples.end(), "Network sample is missing");
    if (found->attribution == ProcessTrafficAttribution::unknown) {
        require(!found->generation && found->identityState == SampleState::unavailable, "Unknown network traffic kept an identity"); return 0;
    }
    require(found->attribution == ProcessTrafficAttribution::direct && found->identityState == SampleState::valid
        && found->identityKind == GenerationKind::creationTime && found->state == SampleState::gap && (found->flags & trafficPartial),
        "Network traffic claimed something other than a direct observed subset");
    return found->generation;
}
const ProcessTrafficSample& withId(const ProcessTrafficBatch& batch, std::uint64_t id)
{
    const auto found = std::find_if(batch.samples.begin(), batch.samples.end(), [&](const auto& row) { return row.sampleId == id; });
    require(found != batch.samples.end(), "An attributed network sample was not republished"); return *found;
}
void networkPidReuse()
{
    EventIdentityIndex identities; ProcessTrafficStore traffic; traffic.initialize(100);
    require(identities.baseline(10, 1000, 100), "Baseline was rejected");
    identities.process(10, 1000, 200, false); identities.process(10, 2000, 300, true);
    traffic.observe(tcp(10, 150, 11), identities); traffic.observe(tcp(10, 250, 12), identities);
    traffic.observe(tcp(10, 350, 13), identities); traffic.observe(tcp(10, 99, 14), identities);
    const auto batch = traffic.read(arrival + delay + 1, identities);
    require(ownerOf(batch, 11) == 1000 && !ownerOf(batch, 12) && ownerOf(batch, 13) == 2000 && !ownerOf(batch, 14),
        "PID reuse moved traffic across generations or attributed the gap");
}
void networkMissingBaseline()
{
    EventIdentityIndex identities; ProcessTrafficStore traffic; traffic.initialize(100);
    identities.process(20, 2000, 50, true);
    traffic.observe(tcp(10, 150, 11), identities); traffic.observe(tcp(20, 150, 12), identities);
    const auto batch = traffic.read(arrival + delay + 1, identities);
    require(!ownerOf(batch, 11) && ownerOf(batch, 12) == 2000, "Without a baseline a pre-existing PID was attributed");
    identities.process(10, 1000, 200, false);
    traffic.observe(tcp(10, 160, 21, arrival + 10), identities);
    require(!ownerOf(traffic.read(arrival + delay + 20, identities), 21), "A Stop without Start or baseline proved ownership");
}
void networkBeforeStart()
{
    EventIdentityIndex identities; ProcessTrafficStore traffic; traffic.initialize(100);
    identities.process(10, 1000, 300, true); identities.baseline(30, 3000, 500);
    traffic.observe(tcp(10, 250, 11), identities); traffic.observe(tcp(10, 300, 12), identities);
    traffic.observe(tcp(10, 301, 13), identities); traffic.observe(tcp(30, 450, 14), identities);
    traffic.observe(tcp(30, 500, 15), identities); traffic.observe(tcp(30, 501, 16), identities);
    const auto batch = traffic.read(arrival + delay + 1, identities);
    require(!ownerOf(batch, 11) && !ownerOf(batch, 12) && ownerOf(batch, 13) == 1000, "Traffic at or before Start was attributed");
    require(!ownerOf(batch, 14) && !ownerOf(batch, 15) && ownerOf(batch, 16) == 3000, "Traffic at or before the baseline snapshot was attributed");
    require(!identities.baseline(10, 1000, 200) && identities.conflicts == 1, "A Start after the snapshot that listed it was accepted");
}
void networkResetFloor()
{
    EventIdentityIndex identities; ProcessTrafficStore traffic; traffic.initialize(100);
    identities.clear(400); identities.process(10, 1000, 300, true); identities.process(20, 2000, 450, true);
    require(!identities.baseline(30, 3000, 400), "A baseline from before the reset was accepted");
    identities.baseline(10, 1000, 500);
    traffic.observe(tcp(10, 420, 11), identities); traffic.observe(tcp(10, 520, 12), identities);
    traffic.observe(tcp(20, 460, 13), identities);
    const auto batch = traffic.read(arrival + delay + 1, identities);
    require(!ownerOf(batch, 11) && ownerOf(batch, 12) == 1000 && ownerOf(batch, 13) == 2000,
        "A lifetime begun before the loss reset proved ownership without a new baseline");
}
std::int64_t now() { LARGE_INTEGER value{}; QueryPerformanceCounter(&value); return value.QuadPart; }
void baselineStore(ProcessEventStore& store)
{
    BootIdentity boot; boot.id = {1, 2}; boot.state = SampleState::valid;
    store.initialize(boot, 100); store.providers(SampleState::first, SampleState::first, SampleState::first);
}
void networkLossGap()
{
    ProcessEventStore store; baselineStore(store);
    const auto epoch = store.baselineWanted();
    require(epoch.has_value(), "A started ledger did not request a baseline");
    const ProcessBaselineEntry entries[] = {{10, 1000}};
    auto at = now();
    require(store.baseline(*epoch, at, entries) && !store.baselineWanted(), "Baseline was not applied once");
    store.traffic(tcp(10, at + 10, 11, 1));
    const auto first = store.read().traffic;
    require(ownerOf(first, 11) == 1000, "Baseline generation did not own its traffic");
    const auto published = first.samples.front();
    store.lost(1, 0);
    store.traffic(tcp(10, at + 20, 12, 1));
    const auto lost = store.read().traffic;
    revoked(published, withId(lost, published.sampleId));
    require(!ownerOf(lost, 12), "Traffic inside the loss gap was attributed");
    const auto next = store.baselineWanted();
    require(next && *next != *epoch, "Loss did not request a new baseline epoch");
    require(!store.baseline(*epoch, now(), entries), "A baseline from the lost epoch was applied");
    at = now();
    require(store.baseline(*next, at, entries), "A fresh baseline was rejected");
    store.traffic(tcp(10, at + 10, 13, 1));
    require(ownerOf(store.read().traffic, 13) == 1000, "Attribution did not recover after a fresh baseline");
    store.decodeFailure();
    const auto stale = store.baselineWanted();
    require(stale.has_value(), "A decode failure kept the baseline");
    store.lost(0, 1);
    require(!store.baseline(*stale, now(), entries), "A snapshot spanning a reset was applied");
    store.identityProvider(SampleState::unsupported);
    require(!store.baselineWanted(), "A baseline was requested without CreateTime on Start/Stop");
}
void networkOutOfOrder()
{
    ProcessEventStore store; baselineStore(store);
    const ProcessBaselineEntry entries[] = {{10, 1000}};
    const auto at = now();
    require(store.baseline(*store.baselineWanted(), at, entries), "Baseline was rejected");
    store.traffic(tcp(10, at + 450, 13, 1)); store.traffic(tcp(10, at + 50, 11, 1)); store.traffic(tcp(10, at + 250, 12, 1));
    auto stop = start(10, 1000, at + 100); stop.kind = ProcessEventKind::stopped; store.push(stop);
    store.push(start(10, 2000, at + 200));
    const auto batch = store.read().traffic;
    require(ownerOf(batch, 11) == 1000 && ownerOf(batch, 12) == 2000 && ownerOf(batch, 13) == 2000,
        "Delivery order instead of timestamps chose the generation");
    const auto published = *std::find_if(batch.samples.begin(), batch.samples.end(), [](const auto& row) { return row.observedBytes == 12; });
    auto late = start(10, 2000, at + 300); late.kind = ProcessEventKind::stopped; store.push(late);
    const auto& old = withId(store.read().traffic, published.sampleId);
    require(old.flags & trafficIdentityConflict, "Late metadata did not revoke as a conflict");
    revoked(published, old);
}
void networkSettledEviction()
{
    Fixture f; const auto epoch = f.traffic.info.attributionEpoch;
    for (std::size_t i = 0; i <= ProcessTrafficStore::maximumSamples; ++i)
        f.traffic.observe(tcp(10, 100 + static_cast<std::int64_t>(i) * 100, 1), f.identities);
    const auto batch = f.traffic.read(arrival + delay + 1, f.identities);
    require(f.traffic.info.attributionEpoch == epoch && f.traffic.info.evictedSamples == 1 && !f.traffic.info.pendingDropped,
        "Settled network windows forced a revocation instead of eviction");
    require(!batch.samples.empty() && std::all_of(batch.samples.begin(), batch.samples.end(), [](const auto& row) {
        return row.attribution == ProcessTrafficAttribution::direct; }), "Eviction dropped direct ownership");
}
void identityChurn()
{
    ProcessEventStore store; const auto before = publish(store);
    std::int64_t qpc = 100;
    for (std::uint32_t i = 0; i < 2 * EventIdentityIndex::maximumThreads; ++i, ++qpc) {
        store.thread(10, 1000 + i, qpc, true); store.thread(10, 1000 + i, qpc + 1, false);
        if (i >= 2 * EventIdentityIndex::maximumProcesses) continue;
        auto row = start(5000 + i, 50000 + i, qpc); store.push(row);
        row.kind = ProcessEventKind::stopped; row.timestampQpc = qpc + 1; store.push(row);
    }
    const auto quiet = store.read().traffic;
    require(quiet.info.attributionEpoch == before.attributionEpoch && quiet.samples.empty(), "Exited thread/process churn forced a reset");
    store.diskInitiated(init(2000, qpc + 10, 100, ProcessTrafficDirection::read, 1));
    store.traffic(completion(2000, qpc + 20, 64, ProcessTrafficDirection::read, 1)); store.traffic(tcp(10, qpc + 30, 77, 1));
    const auto batch = store.read().traffic;
    const auto disk = std::find_if(batch.samples.begin(), batch.samples.end(), [](const auto& row) { return row.observedBytes == 64; });
    require(disk != batch.samples.end() && batch.info.attributionEpoch == before.attributionEpoch, "Disk traffic after churn is missing");
    issuing(*disk); require(ownerOf(batch, 77) == 10000, "Churn evicted a long-lived low-PID process");
}
void exitedLifetimeCleanup()
{
    EventIdentityIndex identities; identities.retentionQpc = 1000;
    identities.process(10, 10000, 1, true); identities.process(30, 30000, 5, true);
    identities.thread(10, 7, 100, true); identities.thread(10, 7, 200, false); identities.process(30, 30000, 250, false);
    identities.thread(10, 8, 1100, true);
    require(identities.threadOwnerAt(7, 150).creationTime == 10000 && identities.processOwnerAt(30, 100).creationTime == 30000,
        "An exited lifetime was dropped inside its retention window");
    identities.thread(10, 9, 2500, true);
    require(!identities.threadOwnerAt(7, 150).creationTime && !identities.processOwnerAt(30, 100).creationTime,
        "Exited lifetimes outlived their retention window");
    require(identities.threadOwnerAt(8, 2600).creationTime == 10000 && !identities.evictions, "Cleanup dropped a live lifetime");
    EventIdentityIndex full; full.process(10, 10000, 1, true); full.thread(10, 5, 2, true);
    for (std::uint32_t i = 0; i < EventIdentityIndex::maximumThreads; ++i) {
        full.thread(10, 100 + i, 10 + 2 * i, true); full.thread(10, 100 + i, 12 + 2 * i, false);
    }
    constexpr auto last = static_cast<std::uint32_t>(EventIdentityIndex::maximumThreads - 1);
    require(!full.evictions && full.threadOwnerAt(5, 40000).creationTime == 10000 && full.threadOwnerAt(100 + last, 11 + 2 * last).creationTime == 10000,
        "A full ledger evicted a live or the most recent exited thread before older exited ones");
}
void staleInitiationExpiry()
{
    Fixture f; const auto before = publish(f);
    constexpr auto count = 2 * ProcessTrafficStore::maximumDiskEvidence;
    for (std::size_t i = 0; i < count; ++i) f.traffic.diskInitiated(init(10000 + i, 100 + static_cast<std::int64_t>(i)), f.identities);
    require(f.traffic.info.pendingDropped >= ProcessTrafficStore::maximumDiskEvidence
        && f.traffic.read(arrival + delay + 2000, f.identities).samples.empty()
        && f.traffic.info.attributionEpoch == before.attributionEpoch && !f.traffic.info.identityConflicts,
        "INITs without completion forced a reset instead of expiring");
    const auto qpc = 100 + static_cast<std::int64_t>(count);
    f.traffic.diskInitiated(init(1000, qpc), f.identities); f.traffic.observe(completion(1000, qpc + 10, 64), f.identities);
    f.traffic.observe(completion(10000, qpc + 20, 32), f.identities);
    const auto batch = f.traffic.read(arrival + delay + 3000, f.identities);
    const auto fresh = std::find_if(batch.samples.begin(), batch.samples.end(), [](const auto& row) { return row.observedBytes == 64; });
    const auto stale = std::find_if(batch.samples.begin(), batch.samples.end(), [](const auto& row) { return row.observedBytes == 32; });
    require(fresh != batch.samples.end() && stale != batch.samples.end() && batch.info.attributionEpoch == before.attributionEpoch,
        "Disk traffic after INIT expiry is missing");
    issuing(*fresh); require(stale->attribution == ProcessTrafficAttribution::unknown, "A completion of an expired INIT gained an owner");
}
void run(const char* name, void (*check)()) { currentCase = name; check(); ++cases; }
}

int main(int argc, char** argv)
{
    if (argc == 2 && std::string_view(argv[1]) == "--wait-for-stdin") {
        std::cout << "READY: waiting for run\n" << std::flush;
        std::string command;
        if (!std::getline(std::cin, command) || command != "run") { std::cerr << "Expected stdin line run\n"; return 2; }
    } else if (argc != 1) { std::cerr << "Usage: nks-process-traffic-tests [--wait-for-stdin]\n"; return 2; }
    try {
        run("owner at INIT", ownerAtInitiation); run("completion before INIT", completionArrivesFirst);
        run("reuse within delay", [] { reuseBeforeDrain(false); }); run("reverse reuse delivery", [] { reuseBeforeDrain(true); });
        run("long I/O", longIo); run("distinct origins", distinctOrigins); run("missing/conflicting evidence", missingAndConflictingEvidence);
        run("duplicate/late evidence", duplicateAndLateEvidence); run("strict lifetime boundaries", strictLifetimes);
        run("loss/late metadata revocation", lossAndMetadataRevocations); run("epoch separation", epochSeparation);
        run("completed stream", completedStream); run("evidence cap", evidenceLimit); run("identity eviction", identityEviction);
        run("sample eviction", sampleEviction); run("allocation failure", allocationFailure);
        run("network PID reuse", networkPidReuse); run("network missing baseline", networkMissingBaseline);
        run("network before start", networkBeforeStart); run("network reset floor", networkResetFloor);
        run("network loss gap", networkLossGap); run("network out-of-order", networkOutOfOrder);
        run("network settled eviction", networkSettledEviction); run("identity churn", identityChurn);
        run("exited lifetime cleanup", exitedLifetimeCleanup); run("stale INIT expiry", staleInitiationExpiry);
        require(eventBudgetUsed.load() == 0, "Traffic tests retained shared-budget allocations");
        std::cout << cases << " process traffic checks passed\n"; return 0;
    } catch (const std::exception& error) { std::cerr << "FAIL " << (currentCase ? currentCase : "startup") << ": " << error.what() << '\n'; return 1; }
}
