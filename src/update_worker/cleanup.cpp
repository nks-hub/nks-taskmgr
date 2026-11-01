#include "cleanup.hpp"
#include <aclapi.h>
#include <bcrypt.h>
#include <algorithm>
#include <map>
#include <set>

namespace nks::update {
namespace {
constexpr std::size_t maxCleanupRows = 128, maxRoots = 64;
struct RootRecord { FILE_ID_INFO id{}; InstallMode mode{}; std::wstring path; };
struct Group { Plan plan; const JournalEntry* entry{}; CleanupItem item; bool valid{true}; };
struct Captured { Proof proof; std::vector<std::byte> data; };
Hash hashBytes(std::span<const std::byte> bytes)
{
    BCRYPT_ALG_HANDLE algorithm = nullptr; Hash digest{};
    if (BCryptOpenAlgorithmProvider(&algorithm, BCRYPT_SHA256_ALGORITHM, nullptr, 0) != 0) throw Error(Status::io, "Cleanup SHA-256 is unavailable");
    const auto status = BCryptHash(algorithm, nullptr, 0, reinterpret_cast<PUCHAR>(const_cast<std::byte*>(bytes.data())),
        static_cast<ULONG>(bytes.size()), reinterpret_cast<PUCHAR>(digest.data()), static_cast<ULONG>(digest.size()));
    BCryptCloseAlgorithmProvider(algorithm, 0);
    if (status != 0) throw Error(Status::io, "Cannot seal cleanup proof"); return digest;
}
bool generated(const std::wstring& value, std::size_t digits, const wchar_t* suffix = L"")
{
    return value.size() == digits + wcslen(suffix) && value.substr(digits) == suffix
        && std::all_of(value.begin(), value.begin() + static_cast<std::ptrdiff_t>(digits), [](wchar_t c) { return (c >= L'0' && c <= L'9') || (c >= L'a' && c <= L'f'); });
}
std::wstring rootKey(const FILE_ID_INFO& id)
{
    std::array<std::byte, 24> bytes{}; idBytes(bytes, 0, id); return hex(bytes);
}
bool gone(Life value) { return value == Life::exited || value == Life::otherBoot; }
broker::Handle openDirectory(const std::wstring& path, const Context& context, FILE_ID_INFO& id)
{
    broker::Handle file(CreateFileW(path.c_str(), FILE_READ_ATTRIBUTES | FILE_TRAVERSE | READ_CONTROL | SYNCHRONIZE, FILE_SHARE_READ,
        nullptr, OPEN_EXISTING, FILE_FLAG_BACKUP_SEMANTICS | FILE_FLAG_OPEN_REPARSE_POINT, nullptr));
    FILE_ATTRIBUTE_TAG_INFO attributes{};
    if (!file || file.get() == INVALID_HANDLE_VALUE || !GetFileInformationByHandleEx(file.get(), FileAttributeTagInfo, &attributes, sizeof(attributes))
        || (attributes.FileAttributes & (FILE_ATTRIBUTE_DIRECTORY | FILE_ATTRIBUTE_REPARSE_POINT)) != FILE_ATTRIBUTE_DIRECTORY
        || !GetFileInformationByHandleEx(file.get(), FileIdInfo, &id, sizeof(id))) throw Error(Status::rejected, "Cannot hold cleanup directory", GetLastError());
    PSECURITY_DESCRIPTOR descriptor = nullptr; PSID owner = nullptr;
    const DWORD status = GetSecurityInfo(file.get(), SE_FILE_OBJECT, OWNER_SECURITY_INFORMATION, &owner, nullptr, nullptr, nullptr, &descriptor);
    const bool valid = !status && owner && EqualSid(owner, const_cast<std::byte*>(context.gui.token.user.data()));
    if (descriptor) LocalFree(descriptor); if (!valid) throw Error(Status::rejected, "Cleanup directory has a different owner"); return file;
}
Captured capture(const std::wstring& path, const Context& context, bool data)
{
    broker::Handle file(CreateFileW(path.c_str(), GENERIC_READ | FILE_EXECUTE | READ_CONTROL, FILE_SHARE_READ, nullptr, OPEN_EXISTING, FILE_FLAG_OPEN_REPARSE_POINT, nullptr));
    FILE_ATTRIBUTE_TAG_INFO attributes{}; FILE_STANDARD_INFO standard{}; FILE_ID_INFO id{};
    if (!file || file.get() == INVALID_HANDLE_VALUE || !GetFileInformationByHandleEx(file.get(), FileAttributeTagInfo, &attributes, sizeof(attributes))
        || (attributes.FileAttributes & (FILE_ATTRIBUTE_DIRECTORY | FILE_ATTRIBUTE_REPARSE_POINT))
        || !GetFileInformationByHandleEx(file.get(), FileStandardInfo, &standard, sizeof(standard)) || standard.NumberOfLinks != 1 || standard.EndOfFile.QuadPart <= 0
        || !GetFileInformationByHandleEx(file.get(), FileIdInfo, &id, sizeof(id))) throw Error(Status::rejected, "Cleanup file has no exclusive regular-file proof", GetLastError());
    PSECURITY_DESCRIPTOR descriptor = nullptr; PSID owner = nullptr;
    const DWORD status = GetSecurityInfo(file.get(), SE_FILE_OBJECT, OWNER_SECURITY_INFORMATION, &owner, nullptr, nullptr, nullptr, &descriptor);
    const bool owns = !status && owner && EqualSid(owner, const_cast<std::byte*>(context.gui.token.user.data()));
    if (descriptor) LocalFree(descriptor); if (!owns) throw Error(Status::rejected, "Cleanup file has a different owner");
    Captured result; result.proof.id = id; result.proof.size = static_cast<std::uint64_t>(standard.EndOfFile.QuadPart);
    if (data) {
        if (result.proof.size > maxPayload) throw Error(Status::unknown, "Cleanup metadata exceeds its bound");
        result.data.resize(static_cast<std::size_t>(result.proof.size)); DWORD count = 0;
        if (!ReadFile(file.get(), result.data.data(), static_cast<DWORD>(result.data.size()), &count, nullptr) || count != result.data.size()) throw Error(Status::unknown, "Cleanup metadata is incomplete");
        result.proof.hash = hashBytes(result.data);
    } else {
        broker::Peer image; image.image = std::move(file);
        if (!broker::verifyImageFile(image)) throw Error(Status::rejected, "Cleanup image publisher cannot be verified");
        result.proof.hash = hashFile(image.image.get(), maxPackageSize);
    }
    return result;
}
const wchar_t* detail(CleanupReason reason)
{
    switch (reason) {
    case CleanupReason::unused: return L"Verified obsolete cache data; deletion is available only after confirmation.";
    case CleanupReason::current: return L"Required by the actual current signed release of a managed root.";
    case CleanupReason::previous: return L"Required for the verified previous release of a managed root.";
    case CleanupReason::liveWorker: return L"The full recorded worker identity is still alive.";
    case CleanupReason::liveInstaller: return L"The full recorded installer identity is still alive.";
    case CleanupReason::lifetimeUnknown: return L"An old process lifetime or an unexpired plan cannot be proven inactive.";
    case CleanupReason::unresolved: return L"Unresolved installation evidence and its repair packages are retained.";
    case CleanupReason::resolutionProof: return L"This record is still needed to prove a repair resolution chain.";
    case CleanupReason::rootAnchor: return L"Retained as the installation-root and rollback-history anchor.";
    case CleanupReason::unknownRoot: return L"A managed root cannot be reverified; its retention evidence is preserved.";
    case CleanupReason::stageInUse: return L"The stage image is in use or cannot be safely released; no force or termination is used.";
    case CleanupReason::changed: return L"The confirmed file or retention proof changed; it was skipped.";
    case CleanupReason::denied: return L"Ordinary access did not allow verified deletion.";
    case CleanupReason::alreadyAbsent: return L"Already absent; no file or byte deletion is claimed.";
    default: return L"Unknown, orphan, redirected, malformed or incomplete data is preserved.";
    }
}
void keep(CleanupItem& item, CleanupReason reason)
{
    item.outcome = CleanupOutcome::kept; item.reason = reason; item.detail = detail(reason);
}
std::vector<std::byte> rootBytes(const RootRecord& root)
{
    std::vector<std::byte> bytes(48); write(bytes, 0, 0x31524b4e, 4); write(bytes, 4, 1, 2); write(bytes, 6, 48, 2);
    write(bytes, 8, static_cast<unsigned>(root.mode), 4); write(bytes, 12, root.path.size(), 4); idBytes(bytes, 16, root.id); appendText(bytes, root.path); return bytes;
}
RootRecord readRoot(std::span<const std::byte> bytes)
{
    if (bytes.size() < 48 || read(bytes, 0, 4) != 0x31524b4e || read(bytes, 4, 2) != 1 || read(bytes, 6, 2) != 48
        || read(bytes, 8, 4) < 1 || read(bytes, 8, 4) > 2 || read(bytes, 40, 8)) throw Error(Status::unknown, "Invalid managed-root retention record");
    RootRecord root; root.mode = static_cast<InstallMode>(read(bytes, 8, 4)); root.id = readId(bytes, 16);
    std::size_t at = 48; root.path = text(bytes, at, static_cast<std::size_t>(read(bytes, 12, 4)));
    if (at != bytes.size() || !sameRoot(root.id, root.id) || !root.path.starts_with(L"\\\\?\\Volume{")) throw Error(Status::unknown, "Invalid root retention binding"); return root;
}
void addVersion(std::map<std::wstring, CleanupReason>& versions, const std::wstring& value, CleanupReason why)
{
    if (!value.empty()) versions.try_emplace(value, why);
}
void addObject(CleanupItem& item, CachePartition part, const std::wstring& leaf, const Captured& file)
{
    item.objects.push_back({part, leaf, file.proof, true}); ++item.files; item.bytes += file.proof.size;
}
bool sameItem(const CleanupItem& left, const CleanupItem& right)
{
    if (left.id != right.id || left.category != right.category || left.plan != right.plan || left.sizeKnown != right.sizeKnown
        || left.bytes != right.bytes || left.files != right.files || left.directories != right.directories || left.path != right.path
        || left.hasStage != right.hasStage || (left.hasStage && !equalId(left.stageId, right.stageId)) || left.objects.size() != right.objects.size()) return false;
    for (std::size_t i = 0; i < left.objects.size(); ++i) {
        const auto& a = left.objects[i]; const auto& b = right.objects[i];
        if (a.partition != b.partition || a.leaf != b.leaf || a.present != b.present || a.expected.hash != b.expected.hash
            || a.expected.size != b.expected.size || !equalId(a.expected.id, b.expected.id)) return false;
    }
    return true;
}
}

void rememberRoot(Cache& cache, const Scope& scope)
{
    if (scope.mode == InstallMode::unknown) return;
    requireScope(scope); const RootRecord root{scope.id, scope.mode, scope.path}; const auto bytes = rootBytes(root);
    const auto path = cache.path() + L"\\Roots\\" + hex(hashBytes(bytes)) + L".bin";
    if (GetFileAttributesW(path.c_str()) != INVALID_FILE_ATTRIBUTES) {
        if (readData(path) != bytes) throw Error(Status::unknown, "Managed-root catalog is inconsistent"); return;
    }
    if (directoryEntries(cache.path() + L"\\Roots", maxRoots).size() >= maxRoots) throw Error(Status::cacheFull, "Managed-root retention catalog is full");
    writeNew(path, bytes);
}

PlanId rollbackJournalAnchor(const std::vector<JournalEntry>& entries, const std::wstring& currentVersion,
    const std::wstring& previous, const Scope& scope)
{
    if (previous.empty()) return {};
    const JournalEntry* anchor = nullptr;
    for (const auto& entry : entries) {
        if (entry.legacy || !belongs(entry.plan, scope) || entry.plan.mode() != scope.mode || entry.state != State::verified
            || !entry.resultVerified || entry.plan.action == Action::repair || entry.plan.selectedVersion() != currentVersion
            || entry.plan.currentVersion() != previous) continue;
        if (!anchor || entry.time > anchor->time || (entry.time == anchor->time && entry.plan.id > anchor->plan.id)) anchor = &entry;
    }
    return anchor ? anchor->plan.id : PlanId{};
}

CleanupPlan prepareCleanup(Cache& cache, const Context& context, ULONGLONG requestDeadline)
{
    CleanupPlan preview; preview.root = cache.rootId();
    std::map<std::wstring, Group> groups;
    std::map<std::wstring, RootRecord> roots;
    std::map<std::wstring, CleanupReason> versions;
    std::set<std::wstring> protectedPackages, retainedRecords, uncertainGroups;
    std::vector<JournalEntry> entries;
    std::size_t unknown = 0; bool freezePackages = false, malformedJournal = false;
    const auto rootReference = [&](const Plan& plan) {
        const auto key = plan.legacy ? L"legacy:" + plan.installDir() : rootKey(plan.root);
        roots.try_emplace(key, RootRecord{plan.root, plan.mode(), plan.installDir()});
    };
    try { entries = journalEntries(cache, maxJournalFiles * 2); }
    catch (const Error&) { malformedJournal = true; freezePackages = true; ++unknown; }
    for (const auto& entry : entries) {
        auto& group = groups[hex(entry.plan.id)]; group.plan = entry.plan; group.entry = &entry; rootReference(entry.plan);
    }
    for (const auto& name : directoryEntries(cache.path() + L"\\Plans", maxPlans * 4)) {
        if (!generated(name, 32, L".bin")) { ++unknown; freezePackages = true; continue; }
        try {
            const auto file = capture(cache.path() + L"\\Plans\\" + name, context, true); const auto plan = decodePlan(file.data);
            if (hex(plan.id) + L".bin" != name) throw Error(Status::unknown, "Plan name binding differs");
            auto& group = groups[hex(plan.id)];
            if (group.entry && encodePlan(group.plan) != encodePlan(plan)) group.valid = false;
            group.plan = plan; addObject(group.item, CachePartition::plans, name, file); rootReference(plan);
        } catch (const Error&) { ++unknown; freezePackages = true; }
    }
    for (const auto& name : cache.journalFiles(maxJournalFiles * 2)) {
        const bool normal = name.size() == 38 && generated(name.substr(0, 32), 32) && name[32] == L'-' && name[33] >= L'0' && name[33] <= L'4' && name.substr(34) == L".bin";
        if (!normal) {
            ++unknown;
            if (name.size() >= 32 && generated(name.substr(0, 32), 32)) uncertainGroups.insert(name.substr(0, 32)); else freezePackages = true;
            continue;
        }
        const auto found = groups.find(name.substr(0, 32));
        if (found == groups.end() || !found->second.entry) { ++unknown; freezePackages = true; continue; }
        try { addObject(found->second.item, CachePartition::journal, name, capture(cache.path() + L"\\Journal\\" + name, context, true)); }
        catch (const Error&) { found->second.valid = false; ++unknown; }
    }
    for (const auto& name : directoryEntries(cache.path() + L"\\Stages", maxPlans * 4)) {
        const auto found = groups.find(name);
        if (!generated(name, 32) || found == groups.end()) { ++unknown; freezePackages = true; continue; }
        auto& group = found->second;
        try {
            auto directory = openDirectory(cache.path() + L"\\Stages\\" + name, context, group.item.stageId);
            const auto children = directoryEntries(cache.path() + L"\\Stages\\" + name, 2);
            if (children.size() > 1 || (!children.empty() && children[0] != L"nks-update-worker.exe")) throw Error(Status::unknown, "Stage contains unknown content");
            group.item.hasStage = true; group.item.directories = 1;
            if (!children.empty()) {
                const auto file = capture(cache.stagePath(group.plan.id), context, false);
                if (file.proof.hash != group.plan.stage.hash || file.proof.size != group.plan.stage.size || !equalId(file.proof.id, group.plan.stage.id))
                    throw Error(Status::unknown, "Stage image differs from its immutable plan proof");
                addObject(group.item, CachePartition::stages, L"nks-update-worker.exe", file);
            } else group.item.objects.push_back({CachePartition::stages, L"nks-update-worker.exe", group.plan.stage, false});
        } catch (const Error&) { group.valid = false; ++unknown; }
    }
    for (const auto& name : directoryEntries(cache.path() + L"\\Roots", maxRoots * 2)) {
        if (!generated(name, 64, L".bin")) { ++unknown; freezePackages = true; continue; }
        try {
            const auto file = capture(cache.path() + L"\\Roots\\" + name, context, true);
            if (hex(file.proof.hash) + L".bin" != name) throw Error(Status::unknown, "Root catalog hash differs");
            const auto root = readRoot(file.data); roots[rootKey(root.id) + L":" + root.path] = root;
        } catch (const Error&) { ++unknown; freezePackages = true; }
    }
    roots[L"current"] = {context.scope.id, context.scope.mode, context.scope.path};
    addVersion(versions, context.installed.version, CleanupReason::current);
    std::set<std::wstring> validRoots, badRoots;
    for (const auto& [key, root] : roots) {
        static_cast<void>(key); const auto identity = sameRoot(root.id, root.id) ? rootKey(root.id) : L"legacy:" + root.path;
        try {
            auto application = openImage(root.path + L"\\NksTaskMgr.WinUI.exe", Role::application);
            broker::Peer imageScope; imageScope.path = application.lease.path; imageScope.token = context.gui.token;
            auto scope = classifyScope(imageScope); requireScope(scope);
            if ((sameRoot(root.id, root.id) && !sameRoot(root.id, scope.id)) || (root.mode != InstallMode::unknown && root.mode != scope.mode))
                throw Error(Status::unknown, "Managed root no longer matches its recorded identity and mode");
            validRoots.insert(identity); addVersion(versions, application.version, CleanupReason::current);
            const auto previous = previousVersion(entries, application, scope);
            addVersion(versions, previous, CleanupReason::previous);
            const auto rollbackAnchor = rollbackJournalAnchor(entries, application.version, previous, scope);
            if (std::any_of(rollbackAnchor.begin(), rollbackAnchor.end(), [](auto value) { return value != std::byte{}; }))
                retainedRecords.insert(hex(rollbackAnchor));
            const JournalEntry* anchor = nullptr;
            for (const auto& entry : entries) if (belongs(entry.plan, scope)) {
                const bool exact = entry.state == State::verified && entry.resultVerified && entry.plan.selectedVersion() == application.version;
                const bool oldExact = anchor && anchor->state == State::verified && anchor->resultVerified && anchor->plan.selectedVersion() == application.version;
                if (!anchor || (exact && !oldExact) || (exact == oldExact && hex(entry.plan.id) < hex(anchor->plan.id))) anchor = &entry;
            }
            if (anchor) retainedRecords.insert(hex(anchor->plan.id));
            preview.rootGuards.push_back(std::move(application));
        } catch (const Error&) { badRoots.insert(identity); }
    }
    for (const auto& root : badRoots) if (!validRoots.contains(root)) { freezePackages = true; ++unknown; }
    std::set<std::wstring> resolutionRecords;
    for (const auto& entry : entries) if (unresolved(entry) && resolved(entry, entries)) {
        const auto repairId = resolution(entry, entries); resolutionRecords.insert(hex(repairId));
        auto current = std::find_if(entries.begin(), entries.end(), [&](const auto& value) { return value.plan.id == repairId; });
        for (std::size_t depth = 0; current != entries.end() && depth < maxPlans; ++depth) {
            const auto prior = std::find_if(entries.begin(), entries.end(), [&](const auto& value) { return value.plan.id == current->plan.recovery; });
            if (prior == entries.end() || prior->plan.id == entry.plan.id) break;
            resolutionRecords.insert(hex(prior->plan.id)); current = prior;
        }
    }
    for (auto& [id, group] : groups) {
        auto& item = group.item; item.category = CleanupCategory::plan; item.plan = group.plan.id;
        std::copy(group.plan.id.begin(), group.plan.id.end(), item.id.begin()); item.scope = group.plan.root;
        item.name = L"Plan " + id; item.path = cache.planPath(group.plan.id); item.sizeKnown = group.valid;
        item.outcome = CleanupOutcome::eligible; item.reason = CleanupReason::unused; item.detail = detail(item.reason);
        const auto identity = group.plan.legacy ? L"legacy:" + group.plan.installDir() : rootKey(group.plan.root);
        if (!group.valid || malformedJournal || uncertainGroups.contains(id)) keep(item, CleanupReason::unknownData);
        else if (!group.entry) keep(item, CleanupReason::missingProof);
        else if (!validRoots.contains(identity) && badRoots.contains(identity)) keep(item, CleanupReason::unknownRoot);
        else if (!gone(lifetime(group.entry->worker, context.gui.key.boot))) keep(item, CleanupReason::liveWorker);
        else if (group.entry->installerKnown && !gone(lifetime(group.entry->installer, context.gui.key.boot))) keep(item, CleanupReason::liveInstaller);
        else if (unresolved(*group.entry) && !resolved(*group.entry, entries)) keep(item, CleanupReason::unresolved);
        else if (resolutionRecords.contains(id)) keep(item, CleanupReason::resolutionProof);
        else if (retainedRecords.contains(id)) keep(item, CleanupReason::rootAnchor);
        else if (group.entry->state == State::planned && group.plan.gui.boot == context.gui.key.boot && group.plan.expires > GetTickCount64()) keep(item, CleanupReason::lifetimeUnknown);
        else if (group.entry->legacy && group.entry->state == State::verified && group.entry->worker.boot == context.gui.key.boot) keep(item, CleanupReason::legacy);
        else if (group.item.hasStage && equalId(group.plan.stage.id, context.self.fileId)) keep(item, CleanupReason::stageInUse);
        else if (group.entry->state != State::planned && group.entry->step != 4) keep(item, CleanupReason::missingProof);
        if (item.outcome == CleanupOutcome::kept) {
            protectedPackages.insert(hex(group.plan.package.hash)); addVersion(versions, group.plan.currentVersion(), CleanupReason::unresolved);
            addVersion(versions, group.plan.selectedVersion(), CleanupReason::unresolved); addVersion(versions, group.plan.strings[5], CleanupReason::unresolved);
        }
        std::sort(item.objects.begin(), item.objects.end(), [](const auto& a, const auto& b) {
            const auto rank = [](CachePartition part) { return part == CachePartition::stages ? 0 : part == CachePartition::plans ? 1 : 2; };
            return rank(a.partition) != rank(b.partition) ? rank(a.partition) < rank(b.partition) : a.leaf < b.leaf;
        });
        preview.items.push_back(std::move(item));
    }
    for (const auto& name : directoryEntries(cache.path() + L"\\Packages", maxPackages * 4)) {
        if (!generated(name, 64, L".exe")) { ++unknown; continue; }
        CleanupItem item; item.category = CleanupCategory::package; item.path = cache.path() + L"\\Packages\\" + name;
        try {
            auto package = openImage(item.path, Role::installer); const auto file = capture(item.path, context, false);
            if (hex(package.hash) + L".exe" != name || file.proof.hash != package.hash || !equalId(file.proof.id, package.lease.fileId)) throw Error(Status::unknown, "Package proof changed");
            item.id = package.hash; item.name = package.version; item.sizeKnown = true; addObject(item, CachePartition::packages, name, file);
            item.outcome = CleanupOutcome::eligible; item.reason = CleanupReason::unused; item.detail = detail(item.reason);
            if (freezePackages) keep(item, CleanupReason::unknownRoot);
            else if (const auto required = versions.find(package.version); required != versions.end()) keep(item, required->second);
            else if (protectedPackages.contains(hex(package.hash))) keep(item, CleanupReason::unresolved);
        } catch (const Error&) { item.id = hashBytes({reinterpret_cast<const std::byte*>(name.data()), name.size() * sizeof(wchar_t)}); item.name = name; keep(item, CleanupReason::unknownData); ++unknown; }
        preview.items.push_back(std::move(item));
    }
    if (unknown) {
        CleanupItem item; item.category = CleanupCategory::other; item.id[0] = std::byte{0xff}; item.name = L"Preserved unknown data";
        item.detail = std::to_wstring(unknown) + L" unverified, orphan, incomplete or unresolvable entries were preserved."; item.reason = CleanupReason::unknownData;
        preview.items.push_back(std::move(item)); preview.complete = false;
    }
    if (preview.items.size() > maxCleanupRows) throw Error(Status::cacheFull, "Cleanup preview row bound exceeded");
    const auto now = GetTickCount64();
    if (requestDeadline && requestDeadline <= now) throw Error(Status::rejected, "Cleanup preview request expired before confirmation");
    preview.deadline = requestDeadline ? std::min<ULONGLONG>(requestDeadline, now + 30000) : now + 30000;
    preview.manifest = hashBytes(cleanupPayload(preview)); return preview;
}

void applyCleanup(Cache& cache, const Context& context, CleanupPlan& plan)
{
    const auto budget = GetTickCount64() + 110000;
    if (!plan.confirmed || !plan.mayHaveDeleted || !equalId(plan.root, cache.rootId()) || !broker::peerAlive(context.gui))
        throw Error(Status::rejected, "Cleanup confirmation or cache-root identity is no longer valid");
    // Fresh root image leases keep every current/previous retention proof stable during deletion.
    auto fresh = prepareCleanup(cache, context);
    for (auto& item : plan.items) {
        if (item.outcome != CleanupOutcome::eligible) continue;
        const auto verified = std::find_if(fresh.items.begin(), fresh.items.end(), [&](const auto& value) { return value.id == item.id && value.category == item.category; });
        if (verified == fresh.items.end() || verified->outcome != CleanupOutcome::eligible || !sameItem(item, *verified)) {
            item.outcome = CleanupOutcome::skipped; item.reason = CleanupReason::changed; item.detail = detail(item.reason); continue;
        }
        bool stopped = false;
        for (const auto& file : item.objects) {
            if (GetTickCount64() >= budget || !broker::peerAlive(context.gui)) {
                item.outcome = CleanupOutcome::skipped; item.reason = CleanupReason::cancelled;
                item.detail = L"Cleanup stopped at its bound or after the original GUI exited. No retry is performed."; item.windows = ERROR_TIMEOUT; stopped = true; break;
            }
            CacheDeleteTarget target; target.cacheRoot = cache.rootHandle(); target.rootId = plan.root;
            target.id = file.expected.id; target.size = file.expected.size; target.hash = file.expected.hash; target.leaf = file.leaf;
            broker::Handle stageParent;
            if (file.partition == CachePartition::stages) {
                target.parentRelative = L"Stages\\" + hex(item.plan);
                try { stageParent = openDirectory(cache.path() + L"\\" + target.parentRelative, context, target.parentId); }
                catch (const Error& error) { item.outcome = CleanupOutcome::skipped; item.reason = CleanupReason::changed; item.windows = error.windows; stopped = true; break; }
                target.parent = stageParent.get();
            } else {
                target.parentRelative = file.partition == CachePartition::packages ? L"Packages" : file.partition == CachePartition::plans ? L"Plans" : L"Journal";
                target.parent = cache.parentHandle(file.partition); target.parentId = cache.parentId(file.partition);
            }
            const auto removed = deleteCacheFile(target);
            item.deletedFiles += removed.files; item.deletedDirectories += removed.directories; item.deletedBytes += removed.bytes;
            if (removed.state != CacheDeleteState::deleted && removed.state != CacheDeleteState::absent) {
                item.outcome = removed.state == CacheDeleteState::error ? CleanupOutcome::failed : CleanupOutcome::skipped;
                item.reason = file.partition == CachePartition::stages ? CleanupReason::stageInUse
                    : removed.state == CacheDeleteState::changed ? CleanupReason::changed : removed.state == CacheDeleteState::denied ? CleanupReason::denied : CleanupReason::io;
                item.windows = removed.windows; item.ntStatus = removed.ntStatus; stopped = true; break;
            }
            if (file.partition == CachePartition::stages && item.hasStage) {
                stageParent.reset();
                CacheDeleteTarget directory; directory.cacheRoot = cache.rootHandle(); directory.rootId = plan.root;
                directory.parent = cache.parentHandle(CachePartition::stages); directory.parentId = cache.parentId(CachePartition::stages);
                directory.parentRelative = L"Stages"; directory.leaf = hex(item.plan); directory.id = item.stageId;
                const auto removedDirectory = deleteEmptyCacheStage(directory, removed);
                item.deletedDirectories += removedDirectory.directories;
                if (removedDirectory.state != CacheDeleteState::deleted && removedDirectory.state != CacheDeleteState::absent) {
                    item.outcome = CleanupOutcome::skipped; item.reason = CleanupReason::stageInUse;
                    item.windows = removedDirectory.windows; item.ntStatus = removedDirectory.ntStatus; stopped = true; break;
                }
            }
        }
        if (!stopped) {
            item.outcome = item.deletedFiles || item.deletedDirectories ? CleanupOutcome::deleted : CleanupOutcome::skipped;
            item.reason = item.outcome == CleanupOutcome::deleted ? CleanupReason::unused : CleanupReason::alreadyAbsent;
            item.detail = item.outcome == CleanupOutcome::deleted ? L"Only the confirmed verified obsolete objects were removed." : detail(item.reason);
        } else {
            item.detail = detail(item.reason);
            if (item.deletedFiles || item.deletedDirectories) item.detail += L" Some objects were removed before this group stopped; its terminal evidence remains preserved.";
        }
    }
}

std::vector<std::byte> cleanupPayload(const CleanupPlan& plan, bool accepted, Status status, DWORD windows)
{
    if (plan.items.size() > maxCleanupRows) throw Error(Status::io, "Cleanup result row limit exceeded");
    std::vector<std::byte> bytes(80); write(bytes, 0, static_cast<unsigned>(status), 4); write(bytes, 4, windows, 4);
    write(bytes, 8, accepted ? 0 : plan.items.size(), 4);
    write(bytes, 12, (plan.complete ? 1u : 0) | (plan.confirmed ? 2u : 0) | (plan.mayHaveDeleted ? 4u : 0), 4);
    std::copy(plan.manifest.begin(), plan.manifest.end(), bytes.begin() + 16); write(bytes, 48, plan.deadline, 8); idBytes(bytes, 56, plan.root);
    if (accepted) return bytes;
    for (const auto& item : plan.items) {
        std::vector<std::byte> row(160); std::copy(item.id.begin(), item.id.end(), row.begin());
        write(row, 32, static_cast<unsigned>(item.category), 4); write(row, 36, static_cast<unsigned>(item.outcome), 4);
        write(row, 40, static_cast<unsigned>(item.reason), 4); write(row, 44, item.sizeKnown ? 1 : 0, 4); write(row, 48, item.sizeKnown ? item.bytes : 0, 8);
        write(row, 56, item.files, 4); write(row, 60, item.directories, 4); write(row, 64, item.deletedFiles, 4); write(row, 68, item.deletedDirectories, 4);
        write(row, 72, item.deletedBytes, 8); write(row, 80, item.windows, 4); write(row, 84, static_cast<std::uint32_t>(item.ntStatus), 4);
        idBytes(row, 88, item.scope); std::copy(item.plan.begin(), item.plan.end(), row.begin() + 112);
        const std::array strings{item.name, item.detail, item.path};
        for (std::size_t i = 0; i < strings.size(); ++i) { write(row, 128 + i * 4, strings[i].size(), 4); appendText(row, strings[i]); }
        if (bytes.size() + row.size() > maxPayload) throw Error(Status::io, "Cleanup result exceeds its payload bound");
        bytes.insert(bytes.end(), row.begin(), row.end());
    }
    return bytes;
}
}
