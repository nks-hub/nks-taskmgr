#include "desktop.hpp"
#include <stdexcept>

namespace nks {
namespace {
std::wstring seriesKey(const HistoryDefinition& definition)
{
    switch (definition.channel) {
    case HistoryChannel::primary: return definition.resourceId;
    case HistoryChannel::upload: return definition.resourceId + L":up";
    case HistoryChannel::dedicated: return definition.resourceId + L":dedicated";
    case HistoryChannel::shared: return definition.resourceId + L":shared";
    case HistoryChannel::engine: return definition.resourceId + definition.detailId;
    case HistoryChannel::processor: return L"cpu:" + std::to_wstring(definition.group) + L":" + std::to_wstring(definition.index);
    case HistoryChannel::pageFile: return L"pagefile";
    }
    throw std::logic_error("Unknown history channel");
}
}

bool Desktop::readHistory()
{
    try {
        auto batch = monitor.takeHistory();
        if (!batch) return false;
        if (batch->lostPoints || batch->captureErrors) history.interrupt();
        historyLost += batch->lostPoints; historyErrors += batch->captureErrors;
        for (const auto& point : batch->points) {
            if (point.sequence <= historySequence) continue;
            if (point.series == 0 || point.series > batch->definitions.size()) throw std::logic_error("Unknown history series");
            if (historySequence && point.sequence != historySequence + 1) history.interrupt();
            historySequence = point.sequence;
            const auto time = std::chrono::steady_clock::time_point(std::chrono::nanoseconds(point.timeNs));
            history.append(seriesKey(batch->definitions[point.series - 1]), {time, point.metric});
        }
        return true;
    } catch (const std::exception&) {
        ++historyErrors; history.interrupt();
        return true;
    }
}

static_assert(sizeof(HistorySeries) * ResourceHistory::maximumSeries
    + sizeof(CollectedHistoryPoint) * HistoryStream::capacity + 4 * HistoryStream::definitionBudget < 64 * 1024 * 1024);

}
