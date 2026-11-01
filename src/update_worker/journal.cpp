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
    std::vector<std::byte> bytes(plan.legacy ? 304 : planBytes);
    std::copy(plan.id.begin(), plan.id.end(), bytes.begin()); write(bytes, 16, static_cast<unsigned>(plan.action), 4); write(bytes, 20, plan.flags, 4);
    write(bytes, 24, plan.created, 8); write(bytes, 32, plan.expires, 8);
    std::copy(plan.package.hash.begin(), plan.package.hash.end(), bytes.begin() + 40); write(bytes, 72, plan.package.size, 8); idBytes(bytes, 80, plan.package.id);
    std::copy(plan.installed.hash.begin(), plan.installed.hash.end(), bytes.begin() + 104); idBytes(bytes, 136, plan.installed.id); write(bytes, 160, plan.installed.size, 8);
    std::copy(plan.stage.hash.begin(), plan.stage.hash.end(), bytes.begin() + 168); idBytes(bytes, 200, plan.stage.id); write(bytes, 224, plan.stage.size, 8);
    keyBytes(bytes, 232, plan.gui); write(bytes, 264, plan.guiCreated, 8);
    if (!plan.legacy) { idBytes(bytes, 304, plan.root); std::copy(plan.recovery.begin(), plan.recovery.end(), bytes.begin() + 328); }
    for (std::size_t i = 0; i < plan.strings.size(); ++i) {
        write(bytes, 272 + i * 4, plan.strings[i].size(), 4); appendText(bytes, plan.strings[i]);
    }
    return bytes;
}
Plan decodePlan(std::span<const std::byte> bytes)
{
    if (bytes.size() < 304 || bytes.size() > maxPayload || read(bytes, 296, 8)) throw Error(Status::unknown, "Invalid update plan layout");
    Plan plan; plan.flags = static_cast<std::uint32_t>(read(bytes, 20, 4)); plan.legacy = plan.flags == 3;
    if ((!plan.legacy && (bytes.size() < planBytes || read(bytes, 344, 8)))
        || (plan.flags != 3 && plan.flags != 7 && plan.flags != 11 && plan.flags != 21 && plan.flags != 25)) throw Error(Status::unknown, "Invalid update mode flags");
    std::copy_n(bytes.begin(), 16, plan.id.begin());
    const auto action = read(bytes, 16, 4); if (action < 1 || action > 3) throw Error(Status::unknown, "Invalid update plan action");
    plan.action = static_cast<Action>(action); plan.created = read(bytes, 24, 8); plan.expires = read(bytes, 32, 8);
    std::copy_n(bytes.begin() + 40, 32, plan.package.hash.begin()); plan.package.size = read(bytes, 72, 8); plan.package.id = readId(bytes, 80);
    std::copy_n(bytes.begin() + 104, 32, plan.installed.hash.begin()); plan.installed.id = readId(bytes, 136); plan.installed.size = read(bytes, 160, 8);
    std::copy_n(bytes.begin() + 168, 32, plan.stage.hash.begin()); plan.stage.id = readId(bytes, 200); plan.stage.size = read(bytes, 224, 8);
    plan.gui = readKey(bytes, 232); plan.guiCreated = read(bytes, 264, 8);
    if (!plan.legacy) { plan.root = readId(bytes, 304); std::copy_n(bytes.begin() + 328, 16, plan.recovery.begin()); }
    if (empty(plan.package.hash) || empty(plan.installed.hash) || empty(plan.stage.hash) || !plan.package.size || plan.package.size > maxPackageSize
        || !plan.installed.size || plan.installed.size > maxPackageSize || !plan.stage.size || plan.stage.size > 64ull * 1024 * 1024
        || !plan.created || !plan.expires || plan.guiCreated != plan.gui.generation) throw Error(Status::unknown, "Invalid update plan proof");
    std::size_t at = plan.legacy ? 304 : planBytes;
    for (std::size_t i = 0; i < plan.strings.size(); ++i) plan.strings[i] = text(bytes, at, static_cast<std::size_t>(read(bytes, 272 + i * 4, 4)));
    if (at != bytes.size() || std::any_of(plan.strings.begin(), plan.strings.begin() + 5, [](const auto& value) { return value.empty(); }))
        throw Error(Status::unknown, "Incomplete update plan strings");
    const bool recovery = plan.action == Action::repair;
    const bool hasRecoveryId = std::any_of(plan.recovery.begin(), plan.recovery.end(), [](auto value) { return value != std::byte{}; });
    if ((!plan.legacy && !sameRoot(plan.root, plan.root)) || (recovery != ((plan.flags & 16) != 0)) || recovery != hasRecoveryId
        || (recovery ? !plan.strings[5].empty() : !plan.legacy && plan.strings[5] != plan.currentVersion()))
        throw Error(Status::unknown, "Invalid recovery or root binding");
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
void journal(Cache& cache, const Plan& plan, State state, unsigned step, DWORD error, DWORD installerExit,
    const ProcessIdentity* installer, const Image* verifiedResult)
{
    if (plan.legacy || step > 4 || cache.journalFiles().size() >= maxJournalFiles) throw Error(Status::cacheFull, "Update journal limit reached");
    const auto worker = queryProcessIdentity(GetCurrentProcess(), GenerationKind::creationTime);
    if (worker.state != SampleState::valid) throw Error(Status::unknown, "Cannot identify update journal owner");
    const auto encoded = encodePlan(plan); std::vector<std::byte> bytes(192);
    write(bytes, 0, 0x31554a4e, 4); write(bytes, 4, 2, 2); write(bytes, 6, 192, 2); write(bytes, 8, static_cast<unsigned>(state), 4);
    write(bytes, 12, step, 4); write(bytes, 16, error, 4); write(bytes, 20, installerExit, 4); write(bytes, 24, unixMs(), 8);
    keyBytes(bytes, 32, worker.identity); write(bytes, 64, worker.identity.generation, 8); write(bytes, 72, encoded.size(), 4);
    write(bytes, 76, (installer ? 1u : 0) | (verifiedResult ? 2u : 0), 4);
    if (installer) { keyBytes(bytes, 80, *installer); write(bytes, 112, installer->generation, 8); }
    if (verifiedResult) {
        std::copy(verifiedResult->hash.begin(), verifiedResult->hash.end(), bytes.begin() + 120); idBytes(bytes, 152, verifiedResult->lease.fileId);
        write(bytes, 176, verifiedResult->size, 8); write(bytes, 184, verifiedResult->version.size(), 4);
    }
    bytes.insert(bytes.end(), encoded.begin(), encoded.end());
    if (verifiedResult) appendText(bytes, verifiedResult->version);
    writeNew(cache.journalPath(plan.id, step), bytes);
}
std::vector<JournalEntry> journalEntries(const Cache& cache)
{
    std::map<std::wstring, JournalEntry> latest;
    std::map<std::wstring, ProcessIdentity> installers;
    for (const auto& name : cache.journalFiles()) {
        if (name.size() == 75 && name.substr(38, 5) == L".tmp-"
            && std::all_of(name.begin() + 43, name.end(), [](wchar_t c) { return (c >= L'0' && c <= L'9') || (c >= L'a' && c <= L'f'); })) continue;
        if (name.size() != 38 || name[32] != L'-' || name[33] < L'0' || name[33] > L'4' || name.substr(34) != L".bin")
            throw Error(Status::unknown, "Unrecognized update journal entry");
        const auto bytes = readData(cache.path() + L"\\Journal\\" + name);
        if (bytes.size() < 96 || read(bytes, 0, 4) != 0x31554a4e) throw Error(Status::unknown, "Invalid update journal record");
        const auto version = read(bytes, 4, 2), header = read(bytes, 6, 2), length = read(bytes, 72, 4);
        const bool legacy = version == 1 && header == 96;
        if ((!legacy && (version != 2 || header != 192)) || header > bytes.size() || length > bytes.size() - header)
            throw Error(Status::unknown, "Invalid update journal version");
        JournalEntry entry; const auto state = read(bytes, 8, 4);
        if (state > static_cast<unsigned>(State::unknown)) throw Error(Status::unknown, "Invalid update journal state");
        entry.state = static_cast<State>(state); entry.step = static_cast<unsigned>(read(bytes, 12, 4));
        entry.error = static_cast<DWORD>(read(bytes, 16, 4)); entry.installerExit = static_cast<DWORD>(read(bytes, 20, 4)); entry.time = read(bytes, 24, 8);
        entry.worker = readKey(bytes, 32); entry.plan = decodePlan(std::span(bytes).subspan(static_cast<std::size_t>(header), static_cast<std::size_t>(length)));
        entry.legacy = legacy;
        if (entry.plan.legacy != legacy || entry.worker.boot != entry.plan.gui.boot || entry.step != static_cast<unsigned>(name[33] - L'0')
            || entry.worker.generation != read(bytes, 64, 8) || name.substr(0, 32) != hex(entry.plan.id)) throw Error(Status::unknown, "Update journal binding mismatch");
        if (legacy) {
            if (header + length != bytes.size() || read(bytes, 76, 4) || read(bytes, 80, 8) || read(bytes, 88, 8)) throw Error(Status::unknown, "Invalid legacy journal layout");
        } else {
            const auto flags = read(bytes, 76, 4); if (flags & ~3ull || read(bytes, 188, 4)) throw Error(Status::unknown, "Invalid journal proof flags");
            entry.installerKnown = (flags & 1) != 0; entry.resultVerified = (flags & 2) != 0;
            if (entry.installerKnown) {
                entry.installer = readKey(bytes, 80); entry.installerCreated = read(bytes, 112, 8);
                if (entry.installer.boot != entry.worker.boot || entry.installerCreated != entry.installer.generation) throw Error(Status::unknown, "Invalid installer identity");
                const auto key = name.substr(0, 32); const auto prior = installers.find(key);
                if (prior != installers.end() && prior->second != entry.installer) throw Error(Status::unknown, "Conflicting installer identities");
                installers[key] = entry.installer;
            } else for (std::size_t i = 80; i < 120; ++i) if (bytes[i] != std::byte{}) throw Error(Status::unknown, "Unmarked installer identity");
            std::copy_n(bytes.begin() + 120, 32, entry.installedResult.hash.begin()); entry.installedResult.id = readId(bytes, 152); entry.installedResult.size = read(bytes, 176, 8);
            auto at = static_cast<std::size_t>(header + length); entry.resultVersion = text(bytes, at, static_cast<std::size_t>(read(bytes, 184, 4)));
            if (at != bytes.size() || (entry.resultVerified && (entry.state != State::verified || !entry.installerKnown || entry.installerExit != 0
                || empty(entry.installedResult.hash) || !entry.installedResult.size || entry.installedResult.size > maxPackageSize
                || !sameRoot(entry.installedResult.id, entry.installedResult.id) || entry.resultVersion != entry.plan.selectedVersion()))
                || (entry.state == State::verified && !entry.resultVerified)) throw Error(Status::unknown, "Unproven successful update result");
        }
        const auto id = name.substr(0, 32); const auto found = latest.find(id);
        if (found != latest.end() && encodePlan(found->second.plan) != encodePlan(entry.plan)) throw Error(Status::unknown, "Conflicting journal plan");
        if (found == latest.end() || found->second.step < entry.step) latest[id] = std::move(entry);
    }
    std::vector<JournalEntry> result;
    for (auto& [id, entry] : latest) {
        if (!entry.installerKnown) if (const auto installer = installers.find(id); installer != installers.end()) {
            entry.installer = installer->second; entry.installerCreated = entry.installer.generation; entry.installerKnown = true;
        }
        result.push_back(std::move(entry));
    }
    return result;
}
bool belongs(const Plan& plan, const Scope& scope)
{
    return plan.legacy ? _wcsicmp(plan.installDir().c_str(), scope.path.c_str()) == 0 : sameRoot(plan.root, scope.id);
}
bool unresolved(const JournalEntry& entry)
{
    return entry.state == State::committed || entry.state == State::waitingGui || entry.state == State::installing
        || entry.state == State::unknown || entry.state == State::installerFailed || entry.state == State::verificationFailed;
}
PlanId resolution(const JournalEntry& entry, const std::vector<JournalEntry>& all)
{
    for (const auto& repair : all) if (!repair.legacy && repair.plan.action == Action::repair && repair.state == State::verified
        && repair.resultVerified && repair.installerKnown) {
        auto target = repair.plan.recovery;
        for (std::size_t depth = 0; depth < maxPlans; ++depth) {
            const auto prior = std::find_if(all.begin(), all.end(), [&](const auto& value) { return value.plan.id == target; });
            if (prior == all.end() || repair.plan.selectedVersion() != (prior->plan.action == Action::repair ? prior->plan.selectedVersion() : prior->plan.currentVersion())
                || (prior->plan.legacy ? _wcsicmp(prior->plan.installDir().c_str(), repair.plan.installDir().c_str()) != 0
                    : !sameRoot(prior->plan.root, repair.plan.root) || prior->plan.mode() != repair.plan.mode())) break;
            if (prior->plan.id == entry.plan.id) return repair.plan.id;
            if (prior->plan.action != Action::repair) break;
            target = prior->plan.recovery;
        }
    }
    return {};
}
bool resolved(const JournalEntry& entry, const std::vector<JournalEntry>& all)
{
    const auto repair = resolution(entry, all);
    return std::any_of(repair.begin(), repair.end(), [](auto value) { return value != std::byte{}; });
}
std::wstring previousVersion(const std::vector<JournalEntry>& entries, const Image& installed, const Scope& scope)
{
    std::wstring previous;
    for (const auto& entry : entries) if (!entry.legacy && belongs(entry.plan, scope) && entry.plan.mode() == scope.mode && entry.state == State::verified && entry.resultVerified
        && entry.plan.action != Action::repair && entry.plan.selectedVersion() == installed.version) {
        if (!previous.empty() && previous != entry.plan.currentVersion()) throw Error(Status::unknown, "Previous installed package is ambiguous");
        previous = entry.plan.currentVersion();
    }
    return previous;
}
State observedState(const std::vector<JournalEntry>& entries, const Scope& scope)
{
    const JournalEntry* last = nullptr;
    for (const auto& entry : entries) if (belongs(entry.plan, scope)) {
        if (unresolved(entry) && !resolved(entry, entries)) return State::unknown;
        if (!last || last->time < entry.time) last = &entry;
    }
    return last ? last->state : State::none;
}
}
