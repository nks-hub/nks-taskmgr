#pragma once

#include "core/service.hpp"
#include <commctrl.h>
#include <array>
#include <memory>

namespace nks {

class ServiceTable {
public:
    ~ServiceTable() { if (images) ImageList_Destroy(images); }
    void create(HWND parent, HFONT font);
    void setFont(HFONT font);
    void resizeColumns();
    void update(std::shared_ptr<const ServiceSnapshot> value);
    void filter(ServiceFilter value, std::wstring query);
    void select(const std::wstring& name);
    void setVisible(bool value);
    LRESULT notify(NMHDR* notification);
    const ServiceRecord* record(std::size_t row) const;
    const ServiceRecord* selected() const;
    std::size_t count() const { return rows.size(); }
    ServiceFilter filterMode() const { return mode; }
    int sortIndex() const { return sortColumn; }
    bool sortDescending() const { return descending; }
    bool updating() const { return rebuilding; }
    HWND handle() const { return window; }
    HWND header() const { return ListView_GetHeader(window); }
private:
    void rebuild();
    void restoreScroll();
    std::wstring cell(std::size_t row, int column) const;
    static LRESULT CALLBACK procedure(HWND window, UINT message, WPARAM wp, LPARAM lp, UINT_PTR id, DWORD_PTR data);
    static LRESULT CALLBACK headerProcedure(HWND window, UINT message, WPARAM wp, LPARAM lp, UINT_PTR id, DWORD_PTR data);
    HWND window{};
    HIMAGELIST images{};
    std::shared_ptr<const ServiceSnapshot> snapshot;
    std::vector<std::size_t> rows;
    ServiceFilter mode{ServiceFilter::all};
    std::wstring query, anchor;
    int sortColumn{}, sizedWidth{};
    bool descending{}, rebuilding{};
};

}
