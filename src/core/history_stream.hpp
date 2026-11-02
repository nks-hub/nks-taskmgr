#pragma once

#include "process.hpp"
#include "resource.hpp"
#include <atomic>
#include <memory>
#include <mutex>
#include <unordered_map>

namespace nks {

enum class HistoryChannel : std::uint32_t { primary, upload, dedicated, shared, engine, processor, pageFile, diskRead, diskWrite };

struct HistoryDefinition {
    std::uint32_t id{};
    HistoryChannel channel{};
    std::uint32_t group{}, index{};
    std::wstring resourceId, detailId, name;
};

struct CollectedHistoryPoint {
    std::uint64_t sequence{};
    std::int64_t timeNs{};
    std::uint32_t series{}, reserved{};
    Metric<double> metric;
};

struct HistoryBatch {
    std::vector<HistoryDefinition> definitions;
    std::vector<CollectedHistoryPoint> points;
    std::uint64_t lostPoints{}, captureErrors{};
    bool morePending{};
};

class HistoryStream {
public:
    static constexpr std::size_t maximumSeries = 512;
    static constexpr std::size_t capacity = 262144;
    static constexpr std::size_t maximumBatch = 4096;
    static constexpr std::size_t definitionBudget = 1024 * 1024;
    void append(const ProcessSnapshot& snapshot);
    void append(const ResourceSnapshot& snapshot);
    void appendGap(bool processes, std::chrono::steady_clock::time_point time);
    void failed() noexcept { errors.fetch_add(1, std::memory_order_relaxed); }
    std::unique_ptr<HistoryBatch> take();

private:
    struct Key {
        HistoryChannel channel{};
        std::uint32_t group{}, index{};
        std::wstring resource, detail;
        bool operator==(const Key&) const = default;
    };
    struct KeyHash { std::size_t operator()(const Key& key) const noexcept; };
    struct Entry { HistoryDefinition definition; std::int64_t lastTime{}; };
    void append(HistoryChannel channel, const std::wstring& resource, const std::wstring& detail,
        const std::wstring& name, std::uint32_t group, std::uint32_t index, std::int64_t time, Metric<double> metric);
    void push(Entry& entry, std::int64_t time, Metric<double> metric);
    std::mutex mutex;
    std::unordered_map<Key, std::size_t, KeyHash> indexes;
    std::vector<Entry> entries;
    std::unique_ptr<CollectedHistoryPoint[]> points;
    std::size_t begin{}, length{}, definitionBytes{};
    std::uint64_t nextSequence{1}, lost{};
    std::atomic<std::uint64_t> errors{};
};

static_assert(sizeof(CollectedHistoryPoint) * HistoryStream::capacity
    + 4 * HistoryStream::definitionBudget < 16 * 1024 * 1024);

}
