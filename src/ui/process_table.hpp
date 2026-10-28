#pragma once

#include "core/process.hpp"
#include <windows.h>
#include <commctrl.h>
#include <memory>
#include <array>
#include <optional>

namespace nks {

class ProcessTable {
public:
    void create(HWND parent, HFONT font);
    void update(std::shared_ptr<const ProcessSnapshot> snapshot);
    void filter(std::wstring text);
    void select(const ProcessIdentity& identity);
    void setStale(bool value) { stale = value; InvalidateRect(window, nullptr, FALSE); }
    void setFont(HFONT font);
    LRESULT notify(NMHDR* notification);
    HWND handle() const { return window; }
    const ProcessRecord* selected() const;
    std::size_t count() const { return rows.size(); }
private:
    void rebuild();
    std::wstring cell(std::size_t row, int column) const;
    HWND window{};
    HIMAGELIST images{};
    std::shared_ptr<const ProcessSnapshot> snapshot;
    std::vector<std::size_t> rows;
    std::wstring search;
    int sortColumn{};
    bool descending{};
    bool stale{};
public:
    ~ProcessTable() { if (images) ImageList_Destroy(images); }
};

}
