#include "recovery.hpp"
#include "execute.hpp"
#include <algorithm>
#include <bcrypt.h>

namespace nks::update {
namespace {
bool gone(Life value) { return value == Life::exited || value == Life::otherBoot; }
Hash requestHash(const Header& request) { Hash result{}; std::copy_n(request.begin() + 88, 32, result.begin()); return result; }
const wchar_t* reason(Safety value)
{
    switch (value) {
    case Safety::ready: return L"The old worker and installer are proven gone. A verified original setup is available for explicit repair.";
    case Safety::workerAlive: return L"The original update worker is still alive. Repair is refused.";
    case Safety::installerAlive: return L"The original installer is still alive. Repair is refused.";
    case Safety::originalMissing: return L"The signed setup of the original exact version and revision is missing or ambiguous.";
    case Safety::scopeMismatch: return L"The installation root or its installed/portable mode is not proven.";
    case Safety::resolved: return L"The original journal is preserved and linked to a successful verified repair transaction.";
    case Safety::legacySameBoot: return L"The legacy installer has no recorded lifetime. Repair is refused until a different verified boot.";
    default: return L"An old process lifetime cannot be proven ended. No repair is allowed.";
    }
}
void requireAllGone(const std::vector<Inspection>& candidates)
{
    for (const auto& candidate : candidates) if (candidate.safety != Safety::resolved && (!gone(candidate.worker) || !gone(candidate.installer)))
        throw Error(Status::conflict, "An unresolved worker or installer in this root may still be alive");
}
}

Life lifetime(const ProcessIdentity& key, BootIdentifier currentBoot)
{
    if (key.boot.empty() || currentBoot.empty() || !key.pid || key.generationKind != GenerationKind::creationTime || !key.generation) return Life::unknown;
    if (key.boot != currentBoot) return Life::otherBoot;
    broker::Handle process(OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION | SYNCHRONIZE, FALSE, key.pid));
    if (!process) {
        const auto error = GetLastError();
        return error == ERROR_INVALID_PARAMETER || error == ERROR_NOT_FOUND ? Life::exited : error == ERROR_ACCESS_DENIED ? Life::denied : Life::unknown;
    }
    const auto actual = queryProcessIdentity(process.get(), key.generationKind);
    if (actual.state != SampleState::valid) return actual.state == SampleState::accessDenied ? Life::denied : Life::unknown;
    if (actual.identity != key) return Life::exited;
    const auto wait = WaitForSingleObject(process.get(), 0);
    return wait == WAIT_OBJECT_0 ? Life::exited : wait == WAIT_TIMEOUT ? Life::live : Life::unknown;
}
std::vector<Inspection> inspections(Cache& cache, const Context& context, const PlanId* selected)
{
    const auto entries = journalEntries(cache); auto packages = cache.packages(); std::vector<Inspection> result;
    for (const auto& entry : entries) {
        if (!unresolved(entry) || (selected && entry.plan.id != *selected) || (!selected && !belongs(entry.plan, context.scope))) continue;
        Inspection value; value.entry = entry;
        value.installDirectory = entry.plan.installDir();
        value.mode = entry.plan.mode(); value.root = entry.plan.root;
        value.originalVersion = entry.plan.action == Action::repair ? entry.plan.selectedVersion() : entry.plan.currentVersion();
        value.worker = lifetime(entry.worker, context.gui.key.boot);
        value.installer = entry.installerKnown ? lifetime(entry.installer, context.gui.key.boot)
            : entry.worker.boot != context.gui.key.boot ? Life::otherBoot : entry.legacy ? Life::legacyBlocked : Life::unknown;
        const Image* original = nullptr; bool ambiguous = false;
        for (const auto& package : packages) if (package.version == value.originalVersion) {
            if (original) ambiguous = true; else original = &package;
        }
        if (original && !ambiguous) value.original = proof(*original);
        if (belongs(entry.plan, context.scope)) { value.root = context.scope.id; value.mode = context.scope.mode; value.installDirectory = context.scope.path; }
        if (resolved(entry, entries)) {
            value.safety = Safety::resolved;
            value.resolving = resolution(entry, entries);
        } else if (!belongs(entry.plan, context.scope) || context.scope.mode == InstallMode::unknown
            || (!entry.plan.legacy && entry.plan.mode() != context.scope.mode)) value.safety = Safety::scopeMismatch;
        else if (value.worker == Life::live) value.safety = Safety::workerAlive;
        else if (value.installer == Life::live) value.safety = Safety::installerAlive;
        else if (value.installer == Life::legacyBlocked) value.safety = Safety::legacySameBoot;
        else if (!gone(value.worker) || !gone(value.installer)) value.safety = Safety::lifetimeUnknown;
        else if (empty(value.original.hash)) value.safety = Safety::originalMissing;
        else value.safety = Safety::ready;
        value.detail = reason(value.safety); result.push_back(std::move(value));
    }
    return result;
}
Plan prepareRepair(Cache& cache, Context& context, const Header& request, std::span<const std::byte> payload)
{
    requireScope(context.scope);
    if (payload.size() != 16) throw Error(Status::invalidRequest, "Repair requires an inspected transaction identity");
    PlanId id{}; std::copy(payload.begin(), payload.end(), id.begin());
    const auto all = inspections(cache, context); requireAllGone(all);
    const auto found = std::find_if(all.begin(), all.end(), [&](const auto& item) { return item.entry.plan.id == id; });
    if (found == all.end() || found->safety != Safety::ready || found->original.hash != requestHash(request))
        throw Error(Status::rejected, "The inspected repair candidate or original setup proof changed");
    auto package = openImage(cache.packagePath(found->original.hash), Role::installer);
    if (!matches(found->original, package) || package.version != found->originalVersion) throw Error(Status::rejected, "Original repair setup is not verified");
    Plan plan; plan.action = Action::repair; plan.flags = 1 | 16 | (context.scope.mode == InstallMode::installed ? 4u : 8u);
    plan.root = context.scope.id; plan.recovery = id; plan.created = unixMs(); plan.expires = GetTickCount64() + 600000;
    if (BCryptGenRandom(nullptr, reinterpret_cast<PUCHAR>(plan.id.data()), static_cast<ULONG>(plan.id.size()), BCRYPT_USE_SYSTEM_PREFERRED_RNG) != 0)
        throw Error(Status::io, "Cannot generate repair plan identity");
    auto stage = cache.stage(context.worker, plan.id);
    plan.package = proof(package); plan.installed = proof(context.installed); plan.stage = proof(stage);
    plan.gui = context.gui.key; plan.guiCreated = context.gui.key.generation;
    plan.strings = {context.installDir, stage.lease.path, package.version, context.installed.version, package.lease.path, L""};
    writeNew(cache.planPath(plan.id), encodePlan(plan)); journal(cache, plan, State::planned, 0); return plan;
}
void recheckRepair(Cache& cache, const Context& context, const Plan& plan)
{
    if (plan.action != Action::repair) return;
    const auto all = inspections(cache, context); requireAllGone(all);
    const auto found = std::find_if(all.begin(), all.end(), [&](const auto& item) { return item.entry.plan.id == plan.recovery; });
    if (found == all.end() || found->safety != Safety::ready || !sameRoot(found->root, plan.root)
        || found->original.hash != plan.package.hash || found->originalVersion != plan.selectedVersion())
        throw Error(Status::rejected, "Repair safety evidence changed before commit");
}
std::vector<std::byte> inventoryBytes(const Context& context, const std::vector<Inspection>& candidates)
{
    std::vector<std::byte> bytes(64);
    write(bytes, 0, static_cast<unsigned>(context.scope.mode), 4);
    const bool unresolved = std::any_of(candidates.begin(), candidates.end(), [](const auto& value) { return value.safety != Safety::resolved; });
    write(bytes, 4, (!sameVersion(context.worker, context.installed) ? 1u : 0) | (unresolved ? 2u : 0), 4); idBytes(bytes, 8, context.scope.id);
    const std::array strings{context.scope.path, context.installed.version, context.worker.version};
    for (std::size_t i = 0; i < strings.size(); ++i) { write(bytes, 32 + i * 4, strings[i].size(), 4); appendText(bytes, strings[i]); }
    return bytes;
}
std::vector<std::byte> inspectionRow(const Inspection& value)
{
    std::vector<std::byte> bytes(inspectionBytes); std::copy(value.entry.plan.id.begin(), value.entry.plan.id.end(), bytes.begin());
    write(bytes, 16, static_cast<unsigned>(value.entry.state), 4); write(bytes, 20, static_cast<unsigned>(value.safety), 4);
    write(bytes, 24, (value.entry.legacy ? 1u : 0) | (value.entry.installerKnown ? 2u : 0) | (value.safety == Safety::resolved ? 4u : 0), 4);
    write(bytes, 28, static_cast<unsigned>(value.mode), 4); idBytes(bytes, 32, value.root);
    keyBytes(bytes, 56, value.entry.worker); write(bytes, 88, value.entry.worker.generation, 8);
    if (value.entry.installerKnown) { keyBytes(bytes, 96, value.entry.installer); write(bytes, 128, value.entry.installerCreated, 8); }
    std::copy(value.original.hash.begin(), value.original.hash.end(), bytes.begin() + 136); write(bytes, 168, value.original.size, 8);
    std::copy(value.resolving.begin(), value.resolving.end(), bytes.begin() + 176);
    const std::array strings{value.detail, value.originalVersion, value.entry.plan.selectedVersion(), value.installDirectory};
    for (std::size_t i = 0; i < strings.size(); ++i) { write(bytes, 192 + i * 4, strings[i].size(), 4); appendText(bytes, strings[i]); }
    write(bytes, 208, static_cast<unsigned>(value.worker), 4); write(bytes, 212, static_cast<unsigned>(value.installer), 4); return bytes;
}
}
