using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using GullProxy.Ui;

namespace GullProxy;

public partial class MainWindow : Window
{
    private MainViewModel? _vm;

    public MainWindow()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        PreviewKeyDown += OnPreviewKeyDown;
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (_vm is not null)
            ((INotifyCollectionChanged)_vm.Rows).CollectionChanged -= OnRowsChanged;
        _vm = e.NewValue as MainViewModel;
        if (_vm is not null)
            ((INotifyCollectionChanged)_vm.Rows).CollectionChanged += OnRowsChanged;
    }

    private void OnRowsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action != NotifyCollectionChangedAction.Add || _vm is null || !_vm.AutoScroll) return;
        if (RequestGrid.Items.Count == 0) return;
        var last = RequestGrid.Items[RequestGrid.Items.Count - 1];
        // Only follow the tail if the user hasn't scrolled up to inspect something.
        RequestGrid.ScrollIntoView(last);
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        bool ctrl = (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control;
        if (ctrl && e.Key == Key.F)
        {
            SearchBox.Focus();
            SearchBox.SelectAll();
            e.Handled = true;
        }
        else if (ctrl && e.Key == Key.Enter && _vm is not null)
        {
            if (_vm.SelectedTab == 1 && _vm.Talon.SendCommand.CanExecute(null))
            {
                _vm.Talon.SendCommand.Execute(null);
                e.Handled = true;
            }
        }
    }

    private DocsWindow? _docs;

    private void OnDocsClick(object sender, RoutedEventArgs e)
    {
        if (_docs is null)
        {
            _docs = new DocsWindow { Owner = this };
            _docs.Closed += (_, _) => _docs = null;
            _docs.Show();
        }
        else
        {
            _docs.Activate();
        }
    }

    /// <summary>Select the row under the cursor on right-click so context-menu commands act on it.</summary>
    private void OnGridRightClick(object sender, MouseButtonEventArgs e)
    {
        for (DependencyObject? d = e.OriginalSource as DependencyObject; d is not null; d = VisualTreeHelper.GetParent(d))
        {
            if (d is DataGridRow row)
            {
                row.IsSelected = true;
                break;
            }
        }
    }
}
