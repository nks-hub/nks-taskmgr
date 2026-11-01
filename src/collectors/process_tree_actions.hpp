#pragma once
#include "process_actions.hpp"
#include <functional>

namespace nks::actions {
struct TreeNode {
    Result result;
    Handle process;
    std::uint32_t parent{UINT32_MAX}, depth{}, execution{};
};
struct TreeResult {
    Target root;
    std::vector<TreeNode> nodes;
    std::uint32_t ordinal{}, flags{nonAtomic}, passes{}, residualCount{};
};
using PublishTreePlan = std::function<bool(const TreeResult&)>;
TreeResult terminateProcessTree(const Context& context, const Target& root, const PublishTreePlan& publish, HANDLE heldRoot = nullptr);
}
