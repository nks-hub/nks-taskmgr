#include "startup_api.h"
#include "collectors/startup_items.hpp"
#include <memory>
#include <new>

struct NksStartupList {
    std::vector<nks::StartupItem> source;
    std::vector<NksStartupItem> items;
    NksStartupView view{};
};

static_assert(sizeof(NksStartupItem) == 80 && sizeof(NksStartupView) == 16);

namespace {
NksString16 string(const std::wstring& value) { return {value.data(), static_cast<uint32_t>(value.size()), 0}; }
}

NksResult __cdecl nks_startup_read(uint32_t version, NksStartupList** result) noexcept
{
    if (!result) return NKS_INVALID_ARGUMENT;
    *result = nullptr;
    if (version != NKS_STARTUP_ABI_VERSION) return NKS_ABI_MISMATCH;
    try {
        auto list = std::make_unique<NksStartupList>();
        list->source = nks::readStartupItems(nks::defaultStartupLocations());
        list->items.reserve(list->source.size());
        for (const auto& item : list->source)
            list->items.push_back({string(item.name), string(item.command), string(item.location), string(item.target),
                static_cast<uint32_t>(item.source), static_cast<uint32_t>(item.state), item.changed});
        list->view = {NKS_STARTUP_ABI_VERSION, static_cast<uint32_t>(list->items.size()), list->items.data()};
        *result = list.release();
        return NKS_OK;
    } catch (const std::bad_alloc&) { return NKS_OUT_OF_MEMORY; }
    catch (...) { return NKS_FAILED; }
}

NksResult __cdecl nks_startup_get_view(const NksStartupList* list, uint32_t size, NksStartupView* result) noexcept
{
    if (!list || !result) return NKS_INVALID_ARGUMENT;
    if (size != sizeof(NksStartupView)) return NKS_ABI_MISMATCH;
    *result = list->view;
    return NKS_OK;
}

void __cdecl nks_startup_release(NksStartupList* list) noexcept { delete list; }

NksResult __cdecl nks_startup_set_enabled(uint32_t source, const wchar_t* name, uint32_t length, int32_t enabled, uint32_t* win32Error) noexcept
{
    if (!win32Error) return NKS_INVALID_ARGUMENT;
    *win32Error = 0;
    if (!name || !length || length > 16383
        || (source != NKS_STARTUP_USER_RUN && source != NKS_STARTUP_USER_FOLDER)) return NKS_INVALID_ARGUMENT;
    try {
        *win32Error = nks::setStartupItemEnabled(nks::defaultStartupLocations(), static_cast<nks::StartupSource>(source),
            std::wstring_view(name, length), enabled != 0);
        return *win32Error ? NKS_FAILED : NKS_OK;
    } catch (const std::bad_alloc&) { return NKS_OUT_OF_MEMORY; }
    catch (...) { return NKS_FAILED; }
}
