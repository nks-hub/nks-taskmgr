#pragma once
#ifdef GetCurrentTime
#undef GetCurrentTime
#endif
#include <winrt/Windows.Foundation.h>
#include <winrt/Windows.Foundation.Collections.h>
#include <winrt/Windows.System.h>
#include <winrt/Windows.UI.h>
#include <winrt/Windows.UI.Text.h>
#include <winrt/Windows.UI.Xaml.Interop.h>
#include <winrt/Microsoft.UI.Dispatching.h>
#include <winrt/Microsoft.UI.Windowing.h>
#include <winrt/Microsoft.UI.Xaml.h>
#include <winrt/Microsoft.UI.Xaml.Automation.h>
#include <winrt/Microsoft.UI.Xaml.Controls.h>
#include <winrt/Microsoft.UI.Xaml.Controls.Primitives.h>
#include <winrt/Microsoft.UI.Xaml.Input.h>
#include <winrt/Microsoft.UI.Xaml.Markup.h>
#include <winrt/Microsoft.UI.Xaml.Media.h>
#include <winrt/Microsoft.UI.Xaml.Shapes.h>

namespace nks::candidate {
namespace xaml = winrt::Microsoft::UI::Xaml;
namespace controls = winrt::Microsoft::UI::Xaml::Controls;
namespace media = winrt::Microsoft::UI::Xaml::Media;
namespace shapes = winrt::Microsoft::UI::Xaml::Shapes;
namespace foundation = winrt::Windows::Foundation;
using winrt::Windows::Foundation::IInspectable;
using Items = winrt::Windows::Foundation::Collections::IObservableVector<IInspectable>;
inline media::SolidColorBrush brush(uint32_t rgb) {
    return media::SolidColorBrush(winrt::Windows::UI::Color{255,
        static_cast<uint8_t>(rgb >> 16), static_cast<uint8_t>(rgb >> 8), static_cast<uint8_t>(rgb)});
}
inline controls::TextBlock label(const std::wstring& text, double size = 12, uint32_t color = 0xe9eef0) {
    controls::TextBlock block;
    block.Text(text); block.FontSize(size); block.Foreground(brush(color));
    block.FontFamily(media::FontFamily(L"Segoe UI Variable"));
    block.VerticalAlignment(xaml::VerticalAlignment::Center);
    block.TextTrimming(xaml::TextTrimming::CharacterEllipsis);
    return block;
}
inline controls::Button button(const std::wstring& text) {
    controls::Button result;
    result.Content(winrt::box_value(text)); result.Height(35); result.MinHeight(0);
    result.FontSize(11); result.Padding({12, 0, 12, 0});
    return result;
}
inline void row(controls::Grid const& grid, double height = 0) {
    controls::RowDefinition definition;
    definition.Height({height, height > 0 ? xaml::GridUnitType::Pixel : xaml::GridUnitType::Star});
    grid.RowDefinitions().Append(definition);
}
inline void column(controls::Grid const& grid, double width = 0) {
    controls::ColumnDefinition definition;
    definition.Width({width, width > 0 ? xaml::GridUnitType::Pixel : xaml::GridUnitType::Star});
    grid.ColumnDefinitions().Append(definition);
}
inline void place(controls::Grid const& grid, xaml::FrameworkElement const& child, int r, int c = 0) {
    controls::Grid::SetRow(child, r); controls::Grid::SetColumn(child, c); grid.Children().Append(child);
}
inline void text(controls::TextBlock const& block, const std::wstring& value) {
    if (block.Text() != value) block.Text(value);
}
inline std::wstring token(IInspectable const& item) {
    return item ? std::wstring(winrt::unbox_value<winrt::hstring>(item)) : std::wstring{};
}
inline controls::ScrollViewer scrollViewer(xaml::DependencyObject const& root) {
    if (auto scroll = root.try_as<controls::ScrollViewer>()) return scroll;
    const auto count = media::VisualTreeHelper::GetChildrenCount(root);
    for (int i = 0; i < count; ++i)
        if (auto result = scrollViewer(media::VisualTreeHelper::GetChild(root, i))) return result;
    return nullptr;
}
inline void setupList(controls::ListView const& list, double height) {
    list.Padding({0, 0, 0, 0}); list.BorderThickness({0, 0, 0, 0});
    list.HorizontalContentAlignment(xaml::HorizontalAlignment::Stretch);
    xaml::Style style(winrt::xaml_typename<controls::ListViewItem>());
    style.Setters().Append(xaml::Setter(xaml::FrameworkElement::MinHeightProperty(), winrt::box_value(height)));
    style.Setters().Append(xaml::Setter(controls::Control::PaddingProperty(), winrt::box_value(xaml::Thickness{0, 0, 0, 0})));
    style.Setters().Append(xaml::Setter(controls::Control::HorizontalContentAlignmentProperty(), winrt::box_value(xaml::HorizontalAlignment::Stretch)));
    list.ItemContainerStyle(style);
    const auto xml = L"<DataTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation'>"
        L"<Grid Height='" + std::to_wstring(height) + L"'/></DataTemplate>";
    list.ItemTemplate(xaml::Markup::XamlReader::Load(xml).as<xaml::DataTemplate>());
    controls::ScrollViewer::SetHorizontalScrollBarVisibility(list, controls::ScrollBarVisibility::Auto);
    controls::ScrollViewer::SetHorizontalScrollMode(list, controls::ScrollMode::Enabled);
}
}
