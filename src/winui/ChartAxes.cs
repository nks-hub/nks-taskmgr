using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace NksTaskMgr.WinUI;

public enum ChartUnit { Percent, Bytes, BitsPerSecond }

public sealed partial class ChartView
{
    private readonly Grid scale = new() { Width = 54, Margin = new Thickness(0, 0, 8, 0) };
    private readonly Grid timeline = new() { Height = 22, Margin = new Thickness(0, 3, 0, 0) };
    private readonly TextBlock upper = Caption(), middle = Caption(), lower = Caption();
    private readonly TextBlock startTime = Caption(), endTime = Caption();
    private readonly TextBlock legend = Caption();
    private ChartUnit unit;
    private string primaryName = "Využití", secondaryName = "Odesílání";
    private bool axes = true;

    private static TextBlock Caption() => new() {
        FontFamily = UiTypography.Font, FontSize = 11, Foreground = Display.Brush(0x95a4ad), IsHitTestVisible = false
    };

    private void BuildAxes()
    {
        foreach (var label in new[] { upper, middle, lower }) {
            label.HorizontalAlignment = HorizontalAlignment.Right; scale.Children.Add(label);
        }
        upper.VerticalAlignment = VerticalAlignment.Top;
        middle.VerticalAlignment = VerticalAlignment.Center;
        lower.VerticalAlignment = VerticalAlignment.Bottom;
        startTime.HorizontalAlignment = HorizontalAlignment.Left;
        endTime.HorizontalAlignment = HorizontalAlignment.Right;
        legend.HorizontalAlignment = HorizontalAlignment.Center;
        timeline.Children.Add(startTime); timeline.Children.Add(legend); timeline.Children.Add(endTime);
        Children.Add(scale); Children.Add(timeline); Grid.SetRow(timeline, 1); Grid.SetColumn(timeline, 1);
    }

    public void Configure(string name, ChartUnit valueUnit = ChartUnit.Percent, bool showAxes = true, string secondName = "Odesílání", bool interactive = true)
    {
        if (primaryName != name || unit != valueUnit) SeriesChanged();
        primaryName = name; secondaryName = secondName; unit = valueUnit; axes = showAxes;
        scale.Visibility = timeline.Visibility = axes ? Visibility.Visible : Visibility.Collapsed;
        IsTabStop = interactive; IsHitTestVisible = interactive;
        AutomationProperties.SetName(this, name + ". Šipky procházejí historii, Enter připne čas. Shift a kliknutí nebo Shift a Enter vyberou dva konce intervalu. Escape výběr zruší.");
        UpdateAxes();
    }

    private string Format(double value)
    {
        if (unit == ChartUnit.Percent) return Display.Number(value, " %");
        double divisor = unit == ChartUnit.Bytes ? 1024 : 1000;
        string[] suffixes = unit == ChartUnit.Bytes ? [" B", " KiB", " MiB", " GiB", " TiB"] : [" bit/s", " kbit/s", " Mbit/s", " Gbit/s", " Tbit/s"];
        int index = 0; double limit = maximum;
        while (limit >= divisor && index < suffixes.Length - 1) { limit /= divisor; value /= divisor; ++index; }
        return Display.Number(value, suffixes[index]);
    }

    private void UpdateAxes()
    {
        if (!axes) return;
        upper.Text = Format(maximum); middle.Text = Format(maximum / 2); lower.Text = Format(0);
        startTime.Text = seconds >= 60 ? seconds / 60 + " min" : seconds + " s";
        endTime.Text = "nyní";
        legend.Text = secondary is not null ? "↓ příjem · ↑ odesílání" : "";
    }
}
