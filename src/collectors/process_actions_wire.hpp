#pragma once
#include "process_tree_actions.hpp"
#include <array>
namespace nks::actions::wire {
using Header = std::array<std::byte, headerBytes>;
inline Header response(const Header& request, FrameKind kind, std::uint32_t count, DWORD error = 0)
{
    Header value{};
    write(value, 0, magic, 4); write(value, 4, version, 2); write(value, 6, read(request, 6, 2), 2);
    write(value, 8, kind == FrameKind::results ? count * resultBytes : 0, 4); write(value, 12, count, 4);
    write(value, 16, read64(request, 16), 8); write(value, 24, read64(request, 24), 8); write(value, 32, read64(request, 32), 8);
    write(value, 40, static_cast<std::uint32_t>(kind), 4); write(value, 44, error, 4); return value;
}
inline void targetBytesTo(const Target& target, std::span<std::byte> row)
{
    const auto& key = target.key;
    write(row, 0, key.boot.low, 8); write(row, 8, key.boot.high, 8); write(row, 16, key.pid, 4);
    write(row, 20, static_cast<std::uint32_t>(key.generationKind), 4); write(row, 24, key.generation, 8);
    write(row, 32, target.created100ns, 8); write(row, 40, target.window, 8);
}
inline void rowBytes(const Result& result, std::span<std::byte> row)
{
    const auto& key = result.target.key;
    write(row, 0, key.boot.low, 8); write(row, 8, key.boot.high, 8); write(row, 16, key.pid, 4);
    write(row, 20, static_cast<std::uint32_t>(key.generationKind), 4); write(row, 24, key.generation, 8);
    write(row, 32, result.target.created100ns, 8); write(row, 40, result.observedCreated100ns, 8);
    write(row, 48, static_cast<std::uint32_t>(result.outcome), 4); write(row, 52, static_cast<std::uint32_t>(result.reason), 4);
    write(row, 56, result.error, 4); write(row, 60, static_cast<std::uint32_t>(result.ntStatus), 4);
    write(row, 64, result.flags, 4); write(row, 68, result.priority, 4); write(row, 72, result.processMask, 8); write(row, 80, result.systemMask, 8);
    write(row, 88, result.group, 4); write(row, 92, result.priorityError, 4); write(row, 96, result.affinityError, 4);
    write(row, 100, result.windowsPosted, 4); write(row, 104, result.windowsFailed, 4);
}
inline bool headerValid(const Header& value)
{
    auto command = read(value, 6, 2); auto count = read32(value, 12);
    return read32(value, 0) == magic && read(value, 4, 2) == version && command <= static_cast<unsigned>(Command::terminateTree)
        && count > 0 && count <= maximumTargets && read32(value, 8) == count * targetBytes
        && read64(value, 16) != 0 && (read64(value, 24) || read64(value, 32)) && !read32(value, 52) && !read64(value, 56)
        && (command != static_cast<unsigned>(Command::terminateTree) || count == 1)
        && (command == static_cast<unsigned>(Command::affinity) ? count == 1 && read64(value, 40) != 0 && read32(value, 48) <= 65535
            : read32(value, 48) == 0 && (command == static_cast<unsigned>(Command::priority) ? priorityAllowed(read64(value, 40)) : read64(value, 40) == 0));
}
template<class Write> bool treeFrame(Write&& output, const Header& request, const TreeResult& tree, FrameKind kind)
{
    std::vector<std::byte> bytes(treePrefixBytes + tree.nodes.size() * treeNodeBytes
        + (kind == FrameKind::treeResults ? tree.nodes.size() * resultBytes : 0));
    write(bytes, 0, tree.ordinal, 4); write(bytes, 4, tree.flags, 4); targetBytesTo(tree.root, std::span(bytes).subspan(8, targetBytes));
    write(bytes, 56, tree.passes, 4); write(bytes, 60, tree.residualCount, 4);
    for (std::size_t i = 0; i < tree.nodes.size(); ++i) {
        const auto& node = tree.nodes[i]; auto row = std::span(bytes).subspan(treePrefixBytes + i * treeNodeBytes, treeNodeBytes);
        targetBytesTo(node.result.target, row.first(targetBytes)); write(row, 48, node.parent, 4); write(row, 52, node.depth, 4); write(row, 56, node.execution, 4);
        if (kind == FrameKind::treeResults) rowBytes(node.result, std::span(bytes).subspan(treePrefixBytes + tree.nodes.size() * treeNodeBytes + i * resultBytes, resultBytes));
    }
    auto header = response(request, kind, static_cast<std::uint32_t>(tree.nodes.size())); write(header, 8, bytes.size(), 4);
    return output(header) && output(bytes);
}
}
