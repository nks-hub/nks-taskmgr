#include "update_worker/cleanup.hpp"
#include <algorithm>
#include <iostream>
#include <stdexcept>

namespace {
using namespace nks::update;
void require(bool value, const char* message) { if (!value) throw std::runtime_error(message); }
Scope scope(unsigned id, InstallMode mode = InstallMode::installed)
{
    Scope value; value.mode = mode; value.id.VolumeSerialNumber = 1; value.id.FileId.Identifier[0] = static_cast<BYTE>(id);
    value.path = L"\\\\?\\Volume{11111111-1111-1111-1111-111111111111}\\Application";
    return value;
}
JournalEntry entry(unsigned id, std::uint64_t time, const Scope& root, Action action)
{
    JournalEntry value; value.plan.id[0] = static_cast<std::byte>(id); value.time = time;
    value.state = State::verified; value.resultVerified = true; value.plan.action = action; value.plan.root = root.id;
    value.plan.flags = root.mode == InstallMode::portable ? 11u : 7u;
    value.plan.strings[0] = root.path; value.plan.strings[2] = L"2.0.0+bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
    value.plan.strings[3] = L"1.0.0+aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    return value;
}
void verify()
{
    const auto root = scope(1);
    Image installed; installed.version = L"2.0.0+bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
    const auto normal = entry(20, 20, root, Action::update);
    const auto newer = entry(30, 30, root, Action::rollback);
    auto repair = entry(1, 40, root, Action::repair);
    repair.plan.strings[3] = L"3.0.0+cccccccccccccccccccccccccccccccccccccccc";
    std::vector<JournalEntry> entries{repair, normal, newer};
    const auto previous = previousVersion(entries, installed, root);
    const auto anchor = rollbackJournalAnchor(entries, installed.version, previous, root);
    require(anchor == newer.plan.id, "A lexically earlier verified repair displaced the actual rollback journal");
    std::erase_if(entries, [&](const auto& value) { return value.plan.id != anchor && value.plan.action != Action::repair; });
    require(previousVersion(entries, installed, root) == previous, "Retaining the rollback anchor lost the previous version after cleanup");
    require(rollbackJournalAnchor(entries, installed.version, previous, root) == anchor, "A subsequent cleanup changed the necessary rollback anchor");

    auto foreign = entry(40, 100, scope(2), Action::update);
    auto differentMode = entry(50, 110, scope(1, InstallMode::portable), Action::update);
    auto unverified = entry(60, 120, root, Action::update); unverified.resultVerified = false;
    auto incomplete = entry(70, 130, root, Action::update); incomplete.state = State::installing;
    auto legacy = entry(80, 140, root, Action::update); legacy.legacy = true;
    auto unrelated = entry(90, 150, root, Action::update); unrelated.plan.strings[2] = L"4.0.0+dddddddddddddddddddddddddddddddddddddddd";
    entries.insert(entries.end(), {foreign, differentMode, unverified, incomplete, legacy, unrelated});
    require(rollbackJournalAnchor(entries, installed.version, previous, root) == anchor, "An unrelated or unproven journal became the rollback anchor");
    require(rollbackJournalAnchor(entries, installed.version, L"", root) == PlanId{}, "A missing previous version invented a rollback anchor");
    require(rollbackJournalAnchor({repair}, installed.version, previous, root) == PlanId{}, "Repair-only history invented a rollback predecessor");
}
}
int main()
{
    try { verify(); std::cout << "Update cleanup retention checks passed\n"; return 0; }
    catch (const std::exception& error) { std::cerr << error.what() << '\n'; return 1; }
}
