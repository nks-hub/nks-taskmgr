#include "service.hpp"

namespace nks {

bool serviceNameEqual(const std::wstring& a, const std::wstring& b)
{
    return CompareStringOrdinal(a.data(), static_cast<int>(a.size()), b.data(), static_cast<int>(b.size()), TRUE) == CSTR_EQUAL;
}

std::optional<ProcessIdentity> serviceHostIdentity(const ServiceRecord& service, const ServiceDetail& detail, const ProcessSnapshot& processes)
{
    if (!serviceNameEqual(service.name, detail.name) || service.state != SERVICE_RUNNING || detail.state != SERVICE_RUNNING
        || service.pid != detail.pid || !detail.pid || detail.statusError || detail.hostError || !detail.hostCreated100ns) return {};
    for (const auto& process : processes.processes)
        if (process.identity.pid == detail.pid && process.identity.generationKind == GenerationKind::creationTime
            && process.identity.generation == detail.hostCreated100ns) return process.identity;
    return {};
}

bool serviceMatches(const ServiceRecord& record, ServiceFilter filter, const std::wstring& query)
{
    if ((filter == ServiceFilter::running && record.state != SERVICE_RUNNING)
        || (filter == ServiceFilter::stopped && record.state != SERVICE_STOPPED)
        || (filter == ServiceFilter::other && (record.state == SERVICE_RUNNING || record.state == SERVICE_STOPPED))) return false;
    if (query.empty()) return true;
    const auto contains = [&](const std::wstring& text) {
        return FindStringOrdinal(FIND_FROMSTART, text.data(), static_cast<int>(text.size()), query.data(), static_cast<int>(query.size()), TRUE) >= 0;
    };
    return contains(record.name) || contains(record.title) || contains(record.config.account)
        || (record.pid && contains(std::to_wstring(record.pid)));
}

std::wstring serviceStatus(DWORD state)
{
    switch (state) {
    case SERVICE_STOPPED: return L"Zastaveno";
    case SERVICE_START_PENDING: return L"Spouští se";
    case SERVICE_STOP_PENDING: return L"Zastavuje se";
    case SERVICE_RUNNING: return L"Běží";
    case SERVICE_CONTINUE_PENDING: return L"Obnovuje se";
    case SERVICE_PAUSE_PENDING: return L"Pozastavuje se";
    case SERVICE_PAUSED: return L"Pozastaveno";
    default: return L"Neznámý stav " + std::to_wstring(state);
    }
}

std::wstring serviceStartup(const ServiceConfig& config)
{
    if (config.error) return config.error == ERROR_ACCESS_DENIED ? L"Chybí oprávnění" : L"Nedostupné";
    switch (config.startType) {
    case SERVICE_AUTO_START: return config.delayed ? L"Automaticky (odloženě)" : config.delayedError ? L"Automaticky (?)" : L"Automaticky";
    case SERVICE_DEMAND_START: return L"Ručně";
    case SERVICE_DISABLED: return L"Zakázáno";
    case SERVICE_BOOT_START: return L"Při bootu";
    case SERVICE_SYSTEM_START: return L"Systém";
    default: return L"Neznámé";
    }
}

}
