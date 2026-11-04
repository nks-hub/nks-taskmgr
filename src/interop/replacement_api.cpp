#include "replacement_api.h"
#include "core/taskmgr_replacement.hpp"
#include <algorithm>
#include <new>

NksResult __cdecl nks_replacement_read(const wchar_t* gui_path, uint32_t* status, uint32_t* flags, uint32_t* win32_error,
    wchar_t* debugger, uint32_t debugger_capacity, uint32_t* debugger_length) noexcept
{
    if (!gui_path || !status || !flags || !win32_error || !debugger_length || (!debugger && debugger_capacity)) return NKS_INVALID_ARGUMENT;
    try {
        const auto plan = nks::replacement::systemPlan(gui_path);
        *status = static_cast<uint32_t>(plan.status); *win32_error = static_cast<uint32_t>(plan.error);
        *flags = (plan.hasBackup ? NKS_REPLACEMENT_HAS_BACKUP : 0u) | (plan.hasBackup && plan.backupSafe ? NKS_REPLACEMENT_BACKUP_RESTORABLE : 0u);
        *debugger_length = static_cast<uint32_t>(plan.debugger.size());
        std::copy_n(plan.debugger.begin(), std::min<std::size_t>(plan.debugger.size(), debugger_capacity), debugger);
        return NKS_OK;
    } catch (const std::bad_alloc&) { return NKS_OUT_OF_MEMORY; }
    catch (...) { return NKS_FAILED; }
}
