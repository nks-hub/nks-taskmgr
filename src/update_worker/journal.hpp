#pragma once
#include "cache.hpp"
#include "scope.hpp"

namespace nks::update {
struct Proof { Hash hash{}; std::uint64_t size{}; FILE_ID_INFO id{}; };
struct Plan {
    PlanId id{};
    Action action{Action::update};
    std::uint32_t flags{};
    FILE_ID_INFO root{};
    PlanId recovery{};
    bool legacy{};
    std::uint64_t created{}, expires{};
    Proof package, installed, stage;
    ProcessIdentity gui;
    std::uint64_t guiCreated{};
    std::array<std::wstring, 6> strings;
    const std::wstring& installDir() const { return strings[0]; }
    const std::wstring& stagePath() const { return strings[1]; }
    const std::wstring& selectedVersion() const { return strings[2]; }
    const std::wstring& currentVersion() const { return strings[3]; }
    const std::wstring& packagePath() const { return strings[4]; }
    InstallMode mode() const { return flags & 4 ? InstallMode::installed : flags & 8 ? InstallMode::portable : InstallMode::unknown; }
};
struct JournalEntry {
    State state{};
    unsigned step{};
    DWORD error{}, installerExit{};
    std::uint64_t time{};
    ProcessIdentity worker;
    ProcessIdentity installer;
    std::uint64_t installerCreated{};
    bool legacy{}, installerKnown{}, resultVerified{};
    Proof installedResult;
    std::wstring resultVersion;
    Plan plan;
};
Proof proof(const Image& image);
bool matches(const Proof& proof, const Image& image) noexcept;
void keyBytes(std::span<std::byte> bytes, std::size_t at, const ProcessIdentity& key);
ProcessIdentity readKey(std::span<const std::byte> bytes, std::size_t at);
void idBytes(std::span<std::byte> bytes, std::size_t at, const FILE_ID_INFO& id);
FILE_ID_INFO readId(std::span<const std::byte> bytes, std::size_t at);
std::vector<std::byte> encodePlan(const Plan& plan);
Plan decodePlan(std::span<const std::byte> bytes);
std::vector<std::byte> packageRow(const Image& image, std::uint32_t flags);
void journal(Cache& cache, const Plan& plan, State state, unsigned step, DWORD error = 0, DWORD installerExit = 0,
    const ProcessIdentity* installer = nullptr, const Image* verifiedResult = nullptr);
std::vector<JournalEntry> journalEntries(const Cache& cache);
bool belongs(const Plan& plan, const Scope& scope);
bool unresolved(const JournalEntry& entry);
PlanId resolution(const JournalEntry& entry, const std::vector<JournalEntry>& all);
bool resolved(const JournalEntry& entry, const std::vector<JournalEntry>& all);
std::wstring previousVersion(const std::vector<JournalEntry>& entries, const Image& installed, const Scope& scope);
State observedState(const std::vector<JournalEntry>& entries, const Scope& scope);
}
