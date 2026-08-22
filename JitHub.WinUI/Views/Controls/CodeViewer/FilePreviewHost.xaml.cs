using System;
using System.ComponentModel;
using JitHub.Models.CodeViewer;
using JitHub.WinUI.ViewModels.CodeViewer;
using JitHub.WinUI.Views.Controls.CodeViewer.Renderers;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace JitHub.WinUI.Views.Controls.CodeViewer;

public sealed partial class FilePreviewHost : UserControl
{
    private RepoFilePreviewViewModel? _viewModel;
    private RepoFilePreviewKind? _currentRendererKind;

    public event Action<string>? ActionExecuted;
    public event Action<string, string>? ActionCompleted;
    public event EventHandler<RepoFilePreviewAppliedEventArgs>? PreviewApplied;

    public FilePreviewHost()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    public bool FocusPrimary()
    {
        if (RendererHost.Content is CodePreview codePreview)
        {
            return codePreview.FocusEditor();
        }

        return RendererHost.Content is Control control
            ? control.Focus(FocusState.Programmatic)
            : Focus(FocusState.Programmatic);
    }

    public bool OpenFind()
    {
        if (RendererHost.Content is not CodePreview codePreview) return false;
        codePreview.OpenFind();
        return true;
    }

    private void OnDataContextChanged(FrameworkElement sender, DataContextChangedEventArgs args)
    {
        if (_viewModel is not null)
        {
            _viewModel.PropertyChanged -= OnViewModelChanged;
        }

        _viewModel = DataContext as RepoFilePreviewViewModel;

        if (_viewModel is not null)
        {
            _viewModel.PropertyChanged += OnViewModelChanged;
        }

        UpdateState();
    }

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(RepoFilePreviewViewModel.IsLoading)
            or nameof(RepoFilePreviewViewModel.ErrorMessage)
            or nameof(RepoFilePreviewViewModel.CurrentFile))
        {
            if (DispatcherQueue.HasThreadAccess)
            {
                UpdateState();
            }
            else
            {
                DispatcherQueue.TryEnqueue(UpdateState);
            }
        }
    }

    private void UpdateState()
    {
        var vm = _viewModel;
        if (vm is null)
        {
            EmptyState.Visibility = Visibility.Visible;
            LoadingState.Visibility = Visibility.Collapsed;
            ErrorState.Visibility = Visibility.Collapsed;
            ClearRenderer();
            return;
        }

        if (vm.IsLoading)
        {
            EmptyState.Visibility = vm.CurrentFile is null ? Visibility.Visible : Visibility.Collapsed;
            ErrorState.Visibility = Visibility.Collapsed;
            LoadingState.Visibility = Visibility.Visible;
            if (vm.CurrentFile is not null)
            {
                EnsureRenderer(vm);
            }
            return;
        }

        LoadingState.Visibility = Visibility.Collapsed;

        if (!string.IsNullOrEmpty(vm.ErrorMessage))
        {
            EmptyState.Visibility = vm.CurrentFile is null ? Visibility.Visible : Visibility.Collapsed;
            ErrorMessageText.Text = vm.ErrorMessage;
            ErrorState.Visibility = Visibility.Visible;
            if (vm.CurrentFile is not null)
            {
                EnsureRenderer(vm);
            }
            return;
        }

        ErrorState.Visibility = Visibility.Collapsed;

        if (vm.CurrentFile is null)
        {
            EmptyState.Visibility = Visibility.Visible;
            ClearRenderer();
            return;
        }

        EmptyState.Visibility = Visibility.Collapsed;
        EnsureRenderer(vm);
        PreviewApplied?.Invoke(
            this,
            new RepoFilePreviewAppliedEventArgs(vm.CurrentFile.Path));
    }

    private void EnsureRenderer(RepoFilePreviewViewModel vm)
    {
        // Keep the renderer instance stable while the preview kind is unchanged.
        // Markdown selection owns pointer capture; replacing the control during a
        // queued preview refresh drops that capture in packaged builds.
        if (RendererHost.Content is FrameworkElement existing &&
            _currentRendererKind == vm.Kind)
        {
            if (!ReferenceEquals(existing.DataContext, vm))
                existing.DataContext = vm;
            return;
        }

        DetachActionSource(RendererHost.Content as FrameworkElement);
        var renderer = CreateRenderer(vm.Kind);
        renderer.HorizontalAlignment = HorizontalAlignment.Stretch;
        renderer.VerticalAlignment = VerticalAlignment.Stretch;
        renderer.DataContext = vm;
        AttachActionSource(renderer);
        RendererHost.Content = renderer;
        _currentRendererKind = vm.Kind;
    }

    private void ClearRenderer()
    {
        DetachActionSource(RendererHost.Content as FrameworkElement);
        RendererHost.Content = null;
        _currentRendererKind = null;
    }

    private void AttachActionSource(FrameworkElement renderer)
    {
        if (renderer is CodePreview codePreview)
        {
            codePreview.ActionExecuted += OnActionExecuted;
            codePreview.ActionCompleted += OnActionCompleted;
        }
        else if (renderer is UnsupportedPreview unsupportedPreview)
        {
            unsupportedPreview.ActionCompleted += OnActionCompleted;
        }
        else if (renderer is CsvPreview csvPreview)
        {
            csvPreview.ActionCompleted += OnActionCompleted;
        }
    }

    private void DetachActionSource(FrameworkElement? renderer)
    {
        if (renderer is CodePreview codePreview)
        {
            codePreview.ActionExecuted -= OnActionExecuted;
            codePreview.ActionCompleted -= OnActionCompleted;
        }
        else if (renderer is UnsupportedPreview unsupportedPreview)
        {
            unsupportedPreview.ActionCompleted -= OnActionCompleted;
        }
        else if (renderer is CsvPreview csvPreview)
        {
            csvPreview.ActionCompleted -= OnActionCompleted;
        }
    }

    private void OnActionExecuted(string action) => ActionExecuted?.Invoke(action);

    private void OnActionCompleted(string action, string result) =>
        ActionCompleted?.Invoke(action, result);

    private static FrameworkElement CreateRenderer(RepoFilePreviewKind kind)
    {
        return kind switch
        {
            RepoFilePreviewKind.Code => new CodePreview(),
            RepoFilePreviewKind.Markdown => new MarkdownPreview(),
            RepoFilePreviewKind.Csv => new CsvPreview(),
            RepoFilePreviewKind.Json => new JsonPreview(),
            RepoFilePreviewKind.Xml => new XmlPreview(),
            RepoFilePreviewKind.Yaml => new YamlPreview(),
            RepoFilePreviewKind.Image => new ImagePreview(),
            RepoFilePreviewKind.Svg => new SvgPreview(),
            RepoFilePreviewKind.Hex => new HexPreview(),
            RepoFilePreviewKind.Unsupported => new UnsupportedPreview(),
            RepoFilePreviewKind.TooLarge => new UnsupportedPreview(),
            _ => new UnsupportedPreview(),
        };
    }
}

public sealed record RepoFilePreviewAppliedEventArgs(string Path);
