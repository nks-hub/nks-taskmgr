using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace NksTaskMgr.WinUI;

public sealed partial class MainWindow
{
    private readonly Grid consumerCards = new() { ColumnSpacing = 10, RowSpacing = 3 };
    private readonly TextBlock consumerCaption = Label("", 11, 0x95a4ad);
    private readonly List<PerformanceConsumerCard> consumerItems = [];
    private (bool Stacked, int Count)? consumerLayout;

    private sealed class PerformanceConsumerCard
    {
        internal readonly Button Button = MainWindow.Button("");
        internal readonly TextBlock Title = Label("", 12, 0xe9eef0), Value = Label("", 12, 0xe9eef0);
        private readonly Border icon = new() { Width = 18, Height = 18, VerticalAlignment = VerticalAlignment.Center };
        private readonly int index;
        private uint? color;
        private ProcessRow? boundRow;
        internal ProcessKey? Key { get; private set; }

        internal PerformanceConsumerCard(int index, Action<PerformanceConsumerCard> clicked)
        {
            this.index = index;
            var content = new Grid { ColumnSpacing = 7 };
            foreach (var width in new[] { GridLength.Auto, new GridLength(1, GridUnitType.Star), GridLength.Auto })
                content.ColumnDefinitions.Add(new ColumnDefinition { Width = width });
            content.Children.Add(icon);
            Title.VerticalAlignment = VerticalAlignment.Center; Title.TextTrimming = TextTrimming.CharacterEllipsis;
            Grid.SetColumn(Title, 1); content.Children.Add(Title);
            Value.VerticalAlignment = VerticalAlignment.Center; Grid.SetColumn(Value, 2); content.Children.Add(Value);
            Button.Content = content; Button.Height = 30; Button.Padding = new Thickness(4, 2, 4, 2);
            Button.BorderThickness = new Thickness(0); Button.CornerRadius = new CornerRadius(0); Button.Background = Display.Brush(0x171d21);
            Button.HorizontalAlignment = HorizontalAlignment.Stretch; Button.HorizontalContentAlignment = HorizontalAlignment.Stretch;
            Button.Click += (_, _) => clicked(this);
        }

        internal void Update(ProcessRow? row, bool memory, uint nextColor)
        {
            boundRow = row; Key = row?.Key;
            var visibility = row is null ? Visibility.Collapsed : Visibility.Visible;
            if (Button.Visibility != visibility) Button.Visibility = visibility;
            if (row is null) return;
            SetConsumerText(Title, row.Name);
            SetConsumerText(Value, memory ? row.WorkingText : row.CpuText);
            string label = $"{index + 1}. {row.Name}, PID {row.PidText}, {Value.Text}";
            if (AutomationProperties.GetName(Button) != label) { AutomationProperties.SetName(Button, label); ToolTipService.SetToolTip(Button, label); }
            if (color != nextColor) { icon.Child = UiIcon.Create(false, nextColor, 18); color = nextColor; }
        }

        internal ProcessRow? Current(MonitorModel source) => boundRow is { } row && Key == row.Key && source.IsCurrent(row) ? row : null;
    }

    private void RefreshConsumers(bool cpu, bool memory, uint color)
    {
        var visibility = cpu || memory ? Visibility.Visible : Visibility.Collapsed;
        if (consumers.Visibility != visibility) consumers.Visibility = visibility;
        if (!cpu && !memory) { if (consumers.Children.Count != 0) consumers.Children.Clear(); return; }
        if (consumerItems.Count == 0) {
            for (int i = 0; i < 3; ++i) {
                var card = new PerformanceConsumerCard(i, OpenPerformanceConsumer);
                consumerItems.Add(card); consumerCards.Children.Add(card.Button);
            }
        }
        if (consumers.Children.Count == 0) {
            consumers.Children.Add(consumerCaption); consumers.Children.Add(consumerCards);
        }
        SetConsumerText(consumerCaption, memory ? Loc.T("Procesy podle paměti") : Loc.T("Procesy podle CPU"));
        string captionHelp = memory ? Loc.T("Pracovní sada včetně sdílených stránek") : "";
        if (ToolTipService.GetToolTip(consumerCaption) as string != captionHelp) ToolTipService.SetToolTip(consumerCaption, captionHelp.Length == 0 ? null : captionHelp);
        var rows = model.TopConsumers(memory).ToArray();
        bool stacked = performanceDetailWidth < 720;
        if (consumerLayout != (stacked, rows.Length)) {
            consumerCards.ColumnDefinitions.Clear(); consumerCards.RowDefinitions.Clear();
            for (int i = 0; i < rows.Length; ++i) {
                if (stacked) consumerCards.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                else consumerCards.ColumnDefinitions.Add(new ColumnDefinition());
            }
            for (int i = 0; i < consumerItems.Count; ++i) {
                var button = consumerItems[i].Button;
                Grid.SetRow(button, stacked ? i : 0); Grid.SetColumn(button, stacked ? 0 : i);
            }
            consumerLayout = (stacked, rows.Length);
        }
        for (int i = 0; i < consumerItems.Count; ++i) consumerItems[i].Update(i < rows.Length ? rows[i] : null, memory, color);
    }

    private void OpenPerformanceConsumer(PerformanceConsumerCard card)
    {
        if (card.Current(model) is not { } row) return;
        search.Text = string.Empty; model.SetMode(ProcessMode.All); ShowView(false);
        SelectProcess(row);
    }

    private static void SetConsumerText(TextBlock label, string text) { if (label.Text != text) label.Text = text; }
}
