#pragma once
#include "journal.hpp"

namespace nks::update {
struct Context;
struct Inspection {
    JournalEntry entry;
    Proof original;
    PlanId resolving{};
    FILE_ID_INFO root{};
    InstallMode mode{};
    Safety safety{Safety::lifetimeUnknown};
    Life worker{}, installer{};
    std::wstring detail, originalVersion, installDirectory;
};
Life lifetime(const ProcessIdentity& key, BootIdentifier currentBoot);
std::vector<Inspection> inspections(Cache& cache, const Context& context, const PlanId* selected = nullptr);
Plan prepareRepair(Cache& cache, Context& context, const Header& request, std::span<const std::byte> payload);
void recheckRepair(Cache& cache, const Context& context, const Plan& plan);
std::vector<std::byte> inventoryBytes(const Context& context, const std::vector<Inspection>& candidates);
std::vector<std::byte> inspectionRow(const Inspection& inspection);
}
