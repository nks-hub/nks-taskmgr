#include "journal.hpp"
#include <algorithm>
#include <cstring>
#include <map>

namespace nks::update {
Proof proof(const Image& image) { return {image.hash, image.size, image.lease.fileId}; }
bool matches(const Proof& expected, const Image& image) noexcept
{
    return expected.hash == image.hash && expected.size == image.size && equalId(expected.id, image.lease.fileId);
}
void keyBytes(std::span<std::byte> bytes, std::size_t at, const ProcessIdentity& key)
{
    write(bytes, at, key.boot.low, 8); write(bytes, at + 8, key.boot.high, 8); write(bytes, at + 16, key.pid, 4);
    write(bytes, at + 20, static_cast<unsigned>(key.generationKind), 4); write(bytes, at + 24, key.generation, 8);
}
ProcessIdentity readKey(std::span<const std::byte> bytes, std::size_t at)
{
    ProcessIdentity result{{read(bytes, at, 8), read(bytes, at + 8, 8)}, static_cast<std::uint32_t>(read(bytes, at + 16, 4)),
        static_cast<GenerationKind>(read(bytes, at + 20, 4)), read(bytes, at + 24, 8)};
    if (result.boot.empty() || result.pid <= 4 || result.generationKind != GenerationKind::creationTime || !result.generation)
        throw Error(Status::invalidRequest, "Invalid full process identity");
    return result;
}
void idBytes(std::span<std::byte> bytes, std::size_t at, const FILE_ID_INFO& id)
{
    write(bytes, at, id.VolumeSerialNumber, 8);
    std::copy_n(reinterpret_cast<const std::byte*>(id.FileId.Identifier), 16, bytes.begin() + static_cast<std::ptrdiff_t>(at + 8));
}
FILE_ID_INFO readId(std::span<const std::byte> bytes, std::size_t at)
{
    FILE_ID_INFO result{}; result.VolumeSerialNumber = read(bytes, at, 8);
    for (std::size_t i = 0; i < 16; ++i) result.FileId.Identifier[i] = static_cast<BYTE>(read(bytes, at + 8 + i, 1));
    return result;
}
std::vector<std::byte> encodePlan(const Plan& plan)
{
    std::vector<std::byte> bytes(planBytes);
    std::copy(plan.id.begin(), plan.id.end(), bytes.begin()); write(bytes, 16, static_cast<unsigned>(plan.action), 4); write(bytes, 20, 3, 4);
    write(bytes, 24, plan.created, 8); write(bytes, 32, plan.expires, 8);
    std::copy(plan.package.hash.begin(), plan.package.hash.end(), bytes.begin() + 40); write(bytes, 72, plan.package.size, 8); idBytes(bytes, 80, plan.package.id);
    std::copy(plan.installed.hash.begin(), plan.installed.hash.end(), bytes.begin() + 104); idBytes(bytes, 136, plan.installed.id); write(bytes, 160, plan.installed.size, 8);
    std::copy(plan.stage.hash.begin(), plan.stage.hash.end(), bytes.begin() + 168); idBytes(bytes, 200, plan.stage.id); write(bytes, 224, plan.stage.size, 8);
    keyBytes(bytes, 232, plan.gui); write(bytes, 264, plan.guiCreated, 8);
    for (std::size_t i = 0; i < plan.strings.size(); ++i) {
        write(bytes, 272 + i * 4, plan.strings[i].size(), 4); appendText(bytes, plan.strings[i]);
    }
    return bytes;
}
Plan decodePlan(std::span<const std::byte> bytes)
{
    if (bytes.size() < planBytes || bytes.size() > maxPayload || read(bytes, 20, 4) != 3 || read(bytes, 296, 8)) throw Error(Status::unknown, "Invalid update plan layout");
    Plan plan; std::copy_n(bytes.begin(), 16, plan.id.begin());
    const auto action = read(bytes, 16, 4); if (action < 1 || action > 2) throw Error(Status::unknown, "Invalid update plan action");
    plan.action = static_cast<Action>(action); plan.created = read(bytes, 24, 8); plan.expires = read(bytes, 32, 8);
    std::copy_n(bytes.begin() + 40, 32, plan.package.hash.begin()); plan.package.size = read(bytes, 72, 8); plan.package.id = readId(bytes, 80);
    std::copy_n(bytes.begin() + 104, 32, plan.installed.hash.begin()); plan.installed.id = readId(bytes, 136); plan.installed.size = read(bytes, 160, 8);
    std::copy_n(bytes.begin() + 168, 32, plan.stage.hash.begin()); plan.stage.id = readId(bytes, 200); plan.stage.size = read(bytes, 224, 8);
    plan.gui = readKey(bytes, 232); plan.guiCreated = read(bytes, 264, 8);
    if (empty(plan.package.hash) || empty(plan.installed.hash) || empty(plan.stage.hash) || !plan.package.size || plan.package.size > maxPackageSize
        || !plan.installed.size || plan.installed.size > maxPackageSize || !plan.stage.size || plan.stage.size > 64ull * 1024 * 1024
        || !plan.created || !plan.expires || plan.guiCreated != plan.gui.generation) throw Error(Status::unknown, "Invalid update plan proof");
    std::size_t at = planBytes;
    for (std::size_t i = 0; i < plan.strings.size(); ++i) plan.strings[i] = text(bytes, at, static_cast<std::size_t>(read(bytes, 272 + i * 4, 4)));
    if (at != bytes.size() || std::any_of(plan.strings.begin(), plan.strings.begin() + 5, [](const auto& value) { return value.empty(); }))
        throw Error(Status::unknown, "Incomplete update plan strings");
    return plan;
}
std::vector<std::byte> packageRow(const Image& image, std::uint32_t flags)
{
    std::vector<std::byte> bytes(packageBytes); std::copy(image.hash.begin(), image.hash.end(), bytes.begin());
    write(bytes, 32, image.size, 8); idBytes(bytes, 40, image.lease.fileId);
    for (std::size_t i = 0; i < 3; ++i) write(bytes, 64 + i * 4, image.numbers[i], 4);
    write(bytes, 76, flags, 4);
    for (std::size_t i = 0; i < 40; ++i) write(bytes, 80 + i, image.revision[i], 1);
    FILETIME created{};
    if (GetFileTime(image.lease.image.get(), &created, nullptr, nullptr)) {
        const auto raw = (static_cast<std::uint64_t>(created.dwHighDateTime) << 32) | created.dwLowDateTime;
        if (raw >= 116444736000000000ull) write(bytes, 120, (raw - 116444736000000000ull) / 10000, 8);
    }
    write(bytes, 128, image.lease.path.size(), 4); appendText(bytes, image.lease.path); return bytes;
}
void journal(Cache& cache, const Plan& plan, State state, unsigned step, DWORD error, DWORD installerExit)
{
    if (step > 4 || cache.journalFiles().size() >= maxJournalFiles) throw Error(Status::cacheFull, "Update journal limit reached");
    const auto worker = queryProcessIdentity(GetCurrentProcess(), GenerationKind::creationTime);
    if (worker.state != SampleState::valid) throw Error(Status::unknown, "Cannot identify update journal owner");
    const auto encoded = encodePlan(plan); std::vector<std::byte> bytes(96);
    write(bytes, 0, 0x31554a4e, 4); write(bytes, 4, 1, 2); write(bytes, 6, 96, 2); write(bytes, 8, static_cast<unsigned>(state), 4);
    write(bytes, 12, step, 4); write(bytes, 16, error, 4); write(bytes, 20, installerExit, 4); write(bytes, 24, unixMs(), 8);
    keyBytes(bytes, 32, worker.identity); write(bytes, 64, worker.identity.generation, 8); write(bytes, 72, encoded.size(), 4);
    bytes.insert(bytes.end(), encoded.begin(), encoded.end()); writeNew(cache.journalPath(plan.id, step), bytes);
}
std::vector<JournalEntry> journalEntries(const Cache& cache)
{
    std::map<std::wstring, JournalEntry> latest;
    for (const auto& name : cache.journalFiles()) {
        if (name.size() != 38 || name[32] != L'-' || name[33] < L'0' || name[33] > L'4' || name.substr(34) != L".bin")
            throw Error(Status::unknown, "Unrecognized update journal entry");
        const auto bytes = readData(cache.path() + L"\\Journal\\" + name);
        if (bytes.size() < 96 || read(bytes, 0, 4) != 0x31554a4e || read(bytes, 4, 2) != 1 || read(bytes, 6, 2) != 96
            || read(bytes, 72, 4) != bytes.size() - 96 || read(bytes, 76, 4) || read(bytes, 80, 8) || read(bytes, 88, 8)) throw Error(Status::unknown, "Invalid update journal record");
        JournalEntry entry; const auto state = read(bytes, 8, 4);
        if (state > static_cast<unsigned>(State::unknown)) throw Error(Status::unknown, "Invalid update journal state");
        entry.state = static_cast<State>(state); entry.step = static_cast<unsigned>(read(bytes, 12, 4));
        entry.error = static_cast<DWORD>(read(bytes, 16, 4)); entry.installerExit = static_cast<DWORD>(read(bytes, 20, 4)); entry.time = read(bytes, 24, 8);
        entry.worker = readKey(bytes, 32); entry.plan = decodePlan(std::span(bytes).subspan(96));
        if (entry.step != static_cast<unsigned>(name[33] - L'0') || entry.worker.generation != read(bytes, 64, 8)
            || name.substr(0, 32) != hex(entry.plan.id)) throw Error(Status::unknown, "Update journal binding mismatch");
        const auto id = name.substr(0, 32); const auto found = latest.find(id);
        if (found != latest.end() && encodePlan(found->second.plan) != encodePlan(entry.plan)) throw Error(Status::unknown, "Conflicting journal plan");
        if (found == latest.end() || found->second.step < entry.step) latest[id] = std::move(entry);
    }
    std::vector<JournalEntry> result;
    for (auto& [id, entry] : latest) { static_cast<void>(id); result.push_back(std::move(entry)); }
    return result;
}
std::wstring previousVersion(const std::vector<JournalEntry>& entries, const Image& installed)
{
    std::wstring previous;
    for (const auto& entry : entries) if (entry.state == State::verified && entry.plan.selectedVersion() == installed.version) {
        if (!previous.empty() && previous != entry.plan.currentVersion()) throw Error(Status::unknown, "Previous installed package is ambiguous");
        previous = entry.plan.currentVersion();
    }
    return previous;
}
State observedState(const std::vector<JournalEntry>& entries)
{
    const JournalEntry* last = nullptr;
    for (const auto& entry : entries) {
        if (entry.state == State::committed || entry.state == State::waitingGui || entry.state == State::installing) return State::unknown;
        if (!last || last->time < entry.time) last = &entry;
    }
    return last ? last->state : State::none;
}
}
