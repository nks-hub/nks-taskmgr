#include "collectors/process_parser.hpp"
#include <algorithm>
#include <iostream>
#include <stdexcept>

namespace {

constexpr nks::BootIdentifier boot{0x1020304050607080ull, 0x9080706050403020ull};
unsigned cases{};

void require(bool value, const char* message) { if (!value) throw std::runtime_error(message); }

nks::ProcessRecord process(std::uint32_t pid, std::uint64_t created = 0x1dea00000000000ull)
{
    nks::ProcessRecord result;
    result.identity = {boot, pid, created ? nks::GenerationKind::creationTime : nks::GenerationKind::missing, created};
    result.created100ns = created;
    result.name = L"Process";
    return result;
}

void rejected(std::vector<nks::ProcessRecord> processes, std::span<const nks::BasicProcessIdentity> before,
    std::span<const nks::BasicProcessIdentity> after, const char* message)
{
    const auto original = processes;
    require(!nks::applySequenceIdentities(processes, before, after), message);
    require(processes.size() == original.size(), "Rejected match changed the process count");
    for (std::size_t i = 0; i < processes.size(); ++i) {
        require(processes[i].identity == original[i].identity, "Rejected match partially committed an identity");
        require(processes[i].created100ns == original[i].created100ns, "Rejected match changed raw creation time");
    }
    ++cases;
}

void matched(std::vector<nks::ProcessRecord> processes, std::span<const nks::BasicProcessIdentity> before,
    std::span<const nks::BasicProcessIdentity> after, std::span<const std::uint64_t> expected)
{
    const auto original = processes;
    require(expected.size() == processes.size(), "Expected sequence count differs");
    require(nks::applySequenceIdentities(processes, before, after), "Stable process identities were rejected");
    require(processes.size() == original.size(), "Successful match changed the process count");
    for (std::size_t i = 0; i < processes.size(); ++i) {
        const auto& result = processes[i];
        require(result.identity.boot == original[i].identity.boot && result.identity.pid == original[i].identity.pid,
            "Successful match changed boot or PID");
        require(result.identity.generation == expected[i], "Sequence was assigned to another process or truncated");
        require(result.identity.generationKind == (expected[i] ? nks::GenerationKind::sequenceNumber : nks::GenerationKind::missing),
            "Sequence zero has the wrong generation kind");
        require(result.created100ns == original[i].created100ns, "Successful match replaced raw creation time with sequence");
    }
    ++cases;
}

void indexChanges()
{
    const std::vector<nks::BasicProcessIdentity> before{{0, 0, 0}, {10, 0, 101}, {20, 10, 202}, {30, 10, 303}, {50, 20, 0xfedcba9876543210ull}};
    const std::vector<nks::ProcessRecord> rows{process(50), process(10), process(20), process(0, 0)};
    const std::vector<std::uint64_t> expected{0xfedcba9876543210ull, 101, 202, 0};
    matched(rows, before, before, expected);

    const std::vector<nks::BasicProcessIdentity> shifted{{0, 0, 0}, {5, 0, 55}, {10, 0, 101}, {20, 10, 202}, {50, 20, 0xfedcba9876543210ull}};
    matched(rows, before, shifted, expected);

    const std::vector<nks::BasicProcessIdentity> shorter{{0, 0, 0}, {10, 0, 101}, {50, 20, 0xfedcba9876543210ull}};
    const std::vector<std::uint64_t> shorterExpected{0xfedcba9876543210ull, 101};
    matched({process(50), process(10)}, before, shorter, shorterExpected);

    const std::vector<nks::BasicProcessIdentity> replacement{{0, 0, 0}, {5, 0, 55}, {30, 10, 202}, {40, 20, 404}, {50, 20, 0xfedcba9876543210ull}};
    rejected({process(50), process(20)}, before, replacement, "Another PID at the same after index was accepted");
}

void lateFailures()
{
    std::vector<nks::ProcessRecord> rows;
    std::vector<nks::BasicProcessIdentity> identities;
    for (std::uint32_t pid = 1; pid <= 512; ++pid) {
        rows.push_back(process(pid, 0x1dea00000000000ull + pid));
        identities.push_back({pid, 0, 0x100000000ull + pid});
    }
    std::reverse(rows.begin(), rows.end());
    rows.push_back(process(3, 0x1deb00000000000ull));
    rejected(rows, identities, identities, "A late duplicate PID with another creation time was accepted");
    rows.pop_back();

    auto changed = identities;
    changed.front().sequenceNumber++;
    rejected(rows, identities, changed, "A late sequence mismatch was accepted");
    changed = identities;
    changed.erase(changed.begin());
    rejected(rows, identities, changed, "A late missing after PID was accepted");
    rejected({process(512), process(999)}, identities, identities, "A missing before PID was accepted");

    rows.back().identity.boot.high ^= 1;
    rejected(rows, identities, identities, "A late boot mismatch was accepted");
    rows.back().identity.boot = boot;
    rows.back().identity.generationKind = nks::GenerationKind::sequenceNumber;
    rejected(rows, identities, identities, "An already converted input identity was accepted");
}

void invalidInputs()
{
    const std::vector<nks::BasicProcessIdentity> ordered{{10, 0, 100}, {20, 10, 200}};
    const std::vector<nks::BasicProcessIdentity> unordered{{20, 10, 200}, {10, 0, 100}};
    const std::vector<nks::BasicProcessIdentity> duplicated{{10, 0, 100}, {10, 0, 100}};
    const std::vector<nks::ProcessRecord> rows{process(10), process(20)};
    rejected({}, ordered, ordered, "Empty process list was accepted");
    rejected(rows, {}, ordered, "Empty before identities were accepted");
    rejected(rows, ordered, {}, "Empty after identities were accepted");
    rejected(rows, unordered, ordered, "Unordered before identities were accepted");
    rejected(rows, ordered, unordered, "Unordered after identities were accepted");
    rejected(rows, duplicated, ordered, "Duplicate before identities were accepted");
    rejected(rows, ordered, duplicated, "Duplicate after identities were accepted");

    auto emptyBoot = rows;
    for (auto& row : emptyBoot) row.identity.boot = {};
    rejected(emptyBoot, ordered, ordered, "Empty boot identity was accepted");
    auto invalidKind = rows;
    invalidKind.back().identity.generationKind = static_cast<nks::GenerationKind>(255);
    rejected(invalidKind, ordered, ordered, "Unknown generation kind was accepted");
}

void zeroSequences()
{
    const std::vector<nks::BasicProcessIdentity> idle{{0, 0, 0}};
    const std::vector<std::uint64_t> zero{0};
    matched({process(0, 0)}, idle, idle, zero);
    rejected({process(0, 0), process(0, 0)}, idle, idle, "Duplicate idle PID was accepted");

    const std::vector<nks::BasicProcessIdentity> nonzeroIdle{{0, 0, 123}};
    const std::vector<std::uint64_t> idleExpected{123};
    matched({process(0, 0)}, nonzeroIdle, nonzeroIdle, idleExpected);

    const std::vector<nks::BasicProcessIdentity> missing{{10, 0, 0}};
    rejected({process(10)}, missing, missing, "A non-idle PID with zero sequence was accepted");
    const std::vector<nks::BasicProcessIdentity> valid{{10, 0, 500}};
    const std::vector<std::uint64_t> expected{500};
    matched({process(10, 0)}, valid, valid, expected);
}

}

int main()
{
    try {
        indexChanges(); lateFailures(); invalidInputs(); zeroSequences();
        std::cout << "Process identity matching passed: " << cases << " cases\n";
        return 0;
    } catch (const std::exception& error) { std::cerr << error.what() << '\n'; return 1; }
}
