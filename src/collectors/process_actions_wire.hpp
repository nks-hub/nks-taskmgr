#pragma once
#include "process_tree_actions.hpp"
#include "process_cpu_sets.hpp"
#include <array>
#include <algorithm>
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
    if (read32(value, 0) != magic || read(value, 4, 2) != version || command > static_cast<unsigned>(Command::taskManager)
        || !count || count > maximumTargets || !read64(value, 16) || (!read64(value, 24) && !read64(value, 32))
        || read32(value, 52) || read64(value, 56)) return false;
    if (command == static_cast<unsigned>(Command::closeHandle)) {
        const auto handle = read64(value, 40); const auto bytes = read32(value, 8);
        return count == 1 && handle && !(handle & 3) && handle < 0xfffffff0 && read32(value, 48) <= verifyHandleName
            && bytes >= targetBytes + handleSpecBytes + 2
            && bytes <= targetBytes + handleSpecBytes + (maximumHandleTypeChars + maximumHandleNameChars) * 2;
    }
    if (command == static_cast<unsigned>(Command::queryCpuSets))
        return count == 1 && read32(value, 8) == targetBytes && !read64(value, 40) && !read32(value, 48);
    // The single target is the requesting GUI; argument 1 enables the replacement, 2 restores the previous registration.
    if (command == static_cast<unsigned>(Command::taskManager))
        return count == 1 && read32(value, 8) == targetBytes && (read64(value, 40) == 1 || read64(value, 40) == 2) && !read32(value, 48);
    if (command == static_cast<unsigned>(Command::cpuSets))
        return count == 1 && read64(value, 40) <= maximumCpuSets && !read32(value, 48)
            && read32(value, 8) == targetBytes + 32 + read64(value, 40) * 4;
    return read32(value, 8) == count * targetBytes && (command != static_cast<unsigned>(Command::terminateTree) || count == 1)
        && (command == static_cast<unsigned>(Command::affinity) ? count == 1 && read64(value, 40) != 0 && read32(value, 48) <= 65535
            : read32(value, 48) == 0 && (command == static_cast<unsigned>(Command::priority) ? priorityAllowed(read64(value, 40)) : read64(value, 40) == 0));
}
inline bool cpuSetsRequest(const Header& header, std::span<const std::byte> payload, CpuSetsHash& baseline, std::vector<std::uint32_t>& selected)
{
    if (read(header, 6, 2) == static_cast<unsigned>(Command::queryCpuSets)) return payload.size() == targetBytes;
    if (read(header, 6, 2) != static_cast<unsigned>(Command::cpuSets) || payload.size() < targetBytes + baseline.size()) return false;
    std::copy_n(payload.begin() + targetBytes, baseline.size(), baseline.begin());
    if (std::all_of(baseline.begin(), baseline.end(), [](auto byte) { return byte == std::byte{}; })) return false;
    const auto count = read64(header, 40);
    if (count > maximumCpuSets || payload.size() != targetBytes + baseline.size() + count * 4) return false;
    selected.reserve(static_cast<std::size_t>(count));
    for (std::size_t i = 0; i < count; ++i) {
        const auto id = read32(payload, targetBytes + baseline.size() + i * 4);
        if (!selected.empty() && selected.back() >= id) return false;
        selected.push_back(id);
    }
    return true;
}
inline bool closeHandleRequest(const Header& header, std::span<const std::byte> payload, HandleSpec& handle)
{
    if (read(header, 6, 2) != static_cast<unsigned>(Command::closeHandle) || payload.size() < targetBytes + handleSpecBytes) return false;
    handle.value = read64(header, 40); handle.flags = read32(header, 48);
    handle.grantedAccess = read32(payload, targetBytes); handle.typeIndex = read32(payload, targetBytes + 4);
    const std::size_t typeChars = read32(payload, targetBytes + 8), nameChars = read32(payload, targetBytes + 12);
    auto& print = handle.fingerprint;
    print.kind = read32(payload, targetBytes + 16); print.id = read32(payload, targetBytes + 20);
    print.value0 = read64(payload, targetBytes + 24); print.value1 = read64(payload, targetBytes + 32); print.value2 = read64(payload, targetBytes + 40);
    using details::FingerprintKind;
    const auto kind = static_cast<FingerprintKind>(print.kind);
    const bool printValid = kind == FingerprintKind::none ? print == details::HandleFingerprint{}
        : kind == FingerprintKind::file ? !print.id && (print.value0 || print.value1 || print.value2)
        : (kind == FingerprintKind::process || kind == FingerprintKind::thread) && print.id && print.value0 && !print.value1 && !print.value2;
    if (!typeChars || typeChars > maximumHandleTypeChars || nameChars > maximumHandleNameChars || !printValid
        || (nameChars && !(handle.flags & verifyHandleName))
        || payload.size() != targetBytes + handleSpecBytes + (typeChars + nameChars) * 2) return false;
    const auto text = [&](std::size_t at, std::size_t chars, std::wstring& value) {
        value.resize(chars);
        for (std::size_t i = 0; i < chars; ++i) if (!(value[i] = static_cast<wchar_t>(read(payload, at + i * 2, 2)))) return false;
        return true;
    };
    if (!text(targetBytes + handleSpecBytes, typeChars, handle.type) || !text(targetBytes + handleSpecBytes + typeChars * 2, nameChars, handle.name))
        return false;
    return kind == FingerprintKind::none || handle.type == (kind == FingerprintKind::process ? L"Process" : kind == FingerprintKind::thread ? L"Thread" : L"File");
}
template<class Write> bool cpuSetsFrame(Write&& output, const Header& request, const CpuSetsResult& result)
{
    const auto& snapshot = result.snapshot;
    if (snapshot.cpus.size() > maximumCpuSets || snapshot.defaults.size() > maximumCpuSets) throw std::invalid_argument("CPU set response exceeds its bound");
    std::vector<std::byte> bytes(resultBytes + cpuSetsPrefixBytes
        + (snapshot.valid ? snapshot.cpus.size() * cpuSetBytes + snapshot.defaults.size() * 4 : 0));
    rowBytes(result.result, std::span(bytes).first(resultBytes));
    if (snapshot.valid) {
        std::copy(snapshot.baseline.begin(), snapshot.baseline.end(), bytes.begin() + resultBytes);
        write(bytes, resultBytes + 32, snapshot.cpus.size(), 4); write(bytes, resultBytes + 36, snapshot.defaults.size(), 4);
        write(bytes, resultBytes + 40, 1, 4);
        std::size_t at = resultBytes + cpuSetsPrefixBytes;
        for (const auto& cpu : snapshot.cpus) {
            write(bytes, at, cpu.id, 4); write(bytes, at + 4, cpu.group, 4); write(bytes, at + 8, cpu.logicalProcessor, 4);
            write(bytes, at + 12, cpu.core, 4); write(bytes, at + 16, cpu.numa, 4); write(bytes, at + 20, cpu.lastLevelCache, 4);
            write(bytes, at + 24, cpu.efficiency, 4); write(bytes, at + 28, cpu.flags, 4); at += cpuSetBytes;
        }
        for (const auto id : snapshot.defaults) { write(bytes, at, id, 4); at += 4; }
    }
    auto header = response(request, FrameKind::cpuSets, 1); write(header, 8, bytes.size(), 4);
    return output(header) && output(bytes);
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
