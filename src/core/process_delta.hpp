#pragma once
#include "process.hpp"
#include <memory>

namespace nks {

enum class ProcessChangeKind : std::uint32_t { added = 1, updated = 2, removed = 3 };
struct ProcessChange {
    ProcessChangeKind kind{};
    std::uint32_t index{}, pid{};
    GenerationKind generationKind{};
    std::uint64_t generation{};
};
struct ProcessChanges {
    bool reset{};
    std::vector<ProcessChange> entries;
};

ProcessChanges processChanges(const ProcessSnapshot* before, const ProcessSnapshot& current);

}
