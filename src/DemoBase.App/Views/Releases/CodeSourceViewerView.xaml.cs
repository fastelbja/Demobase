using DemoBase.App.Services;
using DemoBase.App.ViewModels;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;

namespace DemoBase.App.Views.Releases;

public partial class CodeSourceViewerView : UserControl
{
    public CodeSourceViewerView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    // TreeView.SelectedItem n'a pas de setter (pas de binding TwoWay natif possible,
    // contrairement à ListBox.SelectedItem utilisé par GraphicsViewerView) — on répercute
    // manuellement la sélection dans le ViewModel via l'événement routé SelectedItemChanged.
    private void Tree_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (DataContext is CodeSourceViewerViewModel vm)
            vm.SelectedNode = e.NewValue as CodeSourceTreeNode;
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.OldValue is INotifyPropertyChanged oldVm)
            oldVm.PropertyChanged -= OnViewModelPropertyChanged;
        if (e.NewValue is INotifyPropertyChanged newVm)
            newVm.PropertyChanged += OnViewModelPropertyChanged;

        RenderCurrentText();
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(CodeSourceViewerViewModel.CurrentText))
            RenderCurrentText();
    }

    private void RenderCurrentText()
    {
        var text = (DataContext as CodeSourceViewerViewModel)?.CurrentText ?? string.Empty;
        Preview.Document = SimpleCodeHighlighter.Build(text);
    }
}
