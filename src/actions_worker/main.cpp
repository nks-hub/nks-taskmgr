#include "protocol.hpp"
#include "collectors/process_actions.hpp"
#include "collectors/process_tree_actions.hpp"
#include "core/helper_policy.hpp"
#include <windows.h>
#include <array>
#include <new>
#include <string_view>

namespace {
using namespace nks::actions;
using Header = std::array<std::byte, headerBytes>;

bool readAll(HANDLE pipe, std::span<std::byte> bytes)
{
    while (!bytes.empty()) {
        DWORD count = 0;
        if (!ReadFile(pipe, bytes.data(), static_cast<DWORD>(bytes.size()), &count, nullptr) || !count) return false;
        bytes = bytes.subspan(count);
    }
    return true;
}
bool writeAll(HANDLE pipe, std::span<const std::byte> bytes)
{
    while (!bytes.empty()) {
        DWORD count = 0;
        if (!WriteFile(pipe, bytes.data(), static_cast<DWORD>(bytes.size()), &count, nullptr) || !count) return false;
        bytes = bytes.subspan(count);
    }
    return true;
}
Header response(const Header& request, FrameKind kind, std::uint32_t count, DWORD error = 0)
{
    Header value{};
    write(value, 0, magic, 4); write(value, 4, version, 2); write(value, 6, read(request, 6, 2), 2);
    write(value, 8, kind == FrameKind::results ? count * resultBytes : 0, 4); write(value, 12, count, 4);
    write(value, 16, read64(request, 16), 8); write(value, 24, read64(request, 24), 8); write(value, 32, read64(request, 32), 8);
    write(value, 40, static_cast<std::uint32_t>(kind), 4); write(value, 44, error, 4); return value;
}
void targetBytesTo(const Target& target, std::span<std::byte> row)
{
    const auto& key = target.key;
    write(row, 0, key.boot.low, 8); write(row, 8, key.boot.high, 8); write(row, 16, key.pid, 4);
    write(row, 20, static_cast<std::uint32_t>(key.generationKind), 4); write(row, 24, key.generation, 8);
    write(row, 32, target.created100ns, 8); write(row, 40, target.window, 8);
}
void rowBytes(const Result& result, std::span<std::byte> row)
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
bool headerValid(const Header& value)
{
    auto command = read(value, 6, 2); auto count = read32(value, 12);
    return read32(value, 0) == magic && read(value, 4, 2) == version && command <= static_cast<unsigned>(Command::terminateTree)
        && count > 0 && count <= maximumTargets && read32(value, 8) == count * targetBytes
        && read64(value, 16) != 0 && (read64(value, 24) || read64(value, 32)) && !read32(value, 52) && !read64(value, 56)
        && (command != static_cast<unsigned>(Command::terminateTree) || count == 1)
        && (command == static_cast<unsigned>(Command::affinity) ? count == 1 && read64(value, 40) != 0 && read32(value, 48) <= 65535
            : read32(value, 48) == 0 && (command == static_cast<unsigned>(Command::priority) ? priorityAllowed(read64(value, 40)) : read64(value, 40) == 0));
}
bool treeFrame(HANDLE output, const Header& request, const TreeResult& tree, FrameKind kind)
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
    return writeAll(output, header) && writeAll(output, bytes);
}
}

int main(int argc, char** argv)
{
    if (!nks::initializeHelperProcess()) return 2;
    if (argc != 2 || std::string_view(argv[1]) != "--stdio") return 2;
    const HANDLE input = GetStdHandle(STD_INPUT_HANDLE), output = GetStdHandle(STD_OUTPUT_HANDLE);
    if (GetFileType(input) != FILE_TYPE_PIPE || GetFileType(output) != FILE_TYPE_PIPE || input == output) return 2;
    Header header{};
    if (!readAll(input, header)) return 2;
    bool accepted = false;
    try {
        if (!headerValid(header)) { writeAll(output, response(header, FrameKind::rejected, 0, ERROR_INVALID_PARAMETER)); return 2; }
        const auto count = read32(header, 12); const auto command = static_cast<Command>(read(header, 6, 2));
        std::array<std::byte, maximumTargets * targetBytes> payload{};
        if (!readAll(input, std::span(payload).first(count * targetBytes))) return 2;
        BYTE extra{}; DWORD received = 0;
        const bool ended = ReadFile(input, &extra, 1, &received, nullptr) != FALSE;
        if (received || (!ended && GetLastError() != ERROR_BROKEN_PIPE && GetLastError() != ERROR_HANDLE_EOF)) return 2;
        std::array<Result, maximumTargets> results{};
        for (std::uint32_t i = 0; i < count; ++i) {
            auto row = std::span<const std::byte>(payload).subspan(i * targetBytes, targetBytes);
            const auto kind = read32(row, 20);
            auto& target = results[i].target;
            target.key = {{read64(row, 0), read64(row, 8)}, read32(row, 16), static_cast<nks::GenerationKind>(kind), read64(row, 24)};
            target.created100ns = read64(row, 32); target.window = read64(row, 40);
            bool duplicate = false;
            for (std::uint32_t before = 0; before < i; ++before) duplicate |= results[before].target.key.pid == target.key.pid;
            if (target.key.boot.empty() || kind < 1 || kind > 2 || !target.key.generation || !target.created100ns || duplicate
                || (command != Command::closeWindows && target.window) || target.window == 0xffff || target.window == UINT64_MAX) {
                writeAll(output, response(header, FrameKind::rejected, 0, ERROR_INVALID_PARAMETER)); return 2;
            }
        }
        if (!writeAll(output, response(header, FrameKind::accepted, count))) return 4;
        accepted = true;
        const auto context = actionContext();
        if (command == Command::terminateTree) {
            bool delivered = true;
            const auto tree = terminateProcessTree(context, results[0].target, [&](const TreeResult& plan) {
                delivered = treeFrame(output, header, plan, FrameKind::treePlan); return delivered;
            });
            return delivered && treeFrame(output, header, tree, FrameKind::treeResults) ? 0 : 4;
        }
        for (std::uint32_t i = 0; i < count; ++i) {
            try { applyAction(context, command, read64(header, 40), read32(header, 48), results[i]); }
            catch (...) {
                auto& result = results[i]; result.outcome = result.flags & mayHaveApplied ? Outcome::unverified : Outcome::error;
                result.flags &= ~static_cast<std::uint32_t>(effectConfirmed);
                result.reason = Reason::nativeUnavailable; result.error = ERROR_GEN_FAILURE;
            }
        }
        std::array<std::byte, maximumTargets * resultBytes> bytes{};
        for (std::uint32_t i = 0; i < count; ++i) rowBytes(results[i], std::span(bytes).subspan(i * resultBytes, resultBytes));
        return writeAll(output, response(header, FrameKind::results, count)) && writeAll(output, std::span(bytes).first(count * resultBytes)) ? 0 : 4;
    } catch (const std::bad_alloc&) { writeAll(output, response(header, FrameKind::failed, 0, ERROR_NOT_ENOUGH_MEMORY)); return 3; }
    catch (...) { writeAll(output, response(header, accepted ? FrameKind::failed : FrameKind::rejected, 0, ERROR_INVALID_DATA)); return accepted ? 3 : 2; }
}
