using System.Collections.Specialized;
using System.Windows.Controls;
using UCut.Desktop.ViewModels;

namespace UCut.Desktop.Views;

public partial class LogView : UserControl
{
    public LogView()
    {
        InitializeComponent();
        DataContextChanged += (_, e) =>
        {
            if (e.OldValue is LogViewModel o)
            {
                o.Lines.CollectionChanged -= OnLines;
            }

            if (e.NewValue is LogViewModel n)
            {
                n.Lines.CollectionChanged += OnLines;
                ScrollToEnd();
            }
        };
    }

    private void OnLines(object? sender, NotifyCollectionChangedEventArgs e) => ScrollToEnd();

    private void ScrollToEnd()
    {
        if (List.Items.Count > 0)
        {
            List.ScrollIntoView(List.Items[^1]);
        }
    }
}
