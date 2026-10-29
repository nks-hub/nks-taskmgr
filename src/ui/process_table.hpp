#pragma once

#include "core/process.hpp"
#include <windows.h>
#include <commctrl.h>
#include <memory>
#include <array>
#include <optional>

namespace nks {

enum class ProcessMode { all, active, tree };

class ProcessTable {
public:
    void create(HWND parent, HFONT font);
    void update(std::shared_ptr<const ProcessSnapshot> snapshot);
    void filter(std::wstring text);
    void select(const ProcessIdentity& identity);
    void setStale(bool value) { stale = value; InvalidateRect(window, nullptr, FALSE); }
    void setFont(HFONT font);
    void resizeColumns();
    void setMode(ProcessMode value);
    ProcessMode mode() const { return displayMode; }
    const ProcessRecord* record(std::size_t row) const;
    unsigned depth(std::size_t row) const { return row < depths.size() ? depths[row] : 0; }
    bool hasChildren(std::size_t row) const { return row < branches.size() && branches[row]; }
    bool isCollapsed(std::size_t row) const;
    void toggle(std::size_t row);
    std::wstring sortDescription() const;
    int sortIndex() const { return sortColumn; }
    bool sortDescending() const { return descending; }
    LRESULT notify(NMHDR* notification);
    HWND handle() const { return window; }
    HWND header() const { return ListView_GetHeader(window); }
    const ProcessRecord* selected() const;
    std::size_t count() const { return rows.size(); }
private:
    void rebuild();
    void buildTree();
    static LRESULT CALLBACK procedure(HWND window, UINT message, WPARAM wparam, LPARAM lparam, UINT_PTR id, DWORD_PTR data);
    static LRESULT CALLBACK headerProcedure(HWND window, UINT message, WPARAM wparam, LPARAM lparam, UINT_PTR id, DWORD_PTR data);
    std::wstring cell(std::size_t row, int column) const;
    HWND window{};
    HIMAGELIST images{};
    std::shared_ptr<const ProcessSnapshot> snapshot;
    std::vector<std::size_t> rows;
    std::vector<unsigned> depths;
    std::vector<bool> branches;
    std::vector<ProcessIdentity> collapsed;
    std::wstring search;
    int sortColumn{};
    bool descending{};
    bool stale{};
    ProcessMode displayMode{ProcessMode::all};
    UINT lastDpi{};
    int sizedClientWidth{};
public:
    ~ProcessTable() { if (images) ImageList_Destroy(images); }
};

}
