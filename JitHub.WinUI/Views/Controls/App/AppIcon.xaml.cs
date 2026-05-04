using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;

namespace JitHub.WinUI.Views.Controls.App;

public enum AppIconKind
{
    Star,
    Watch,
    Fork,
    PublicRepository,
    PrivateRepository
}

public sealed partial class AppIcon : UserControl
{
    public static readonly DependencyProperty IconKindProperty = DependencyProperty.Register(
        nameof(IconKind), typeof(AppIconKind), typeof(AppIcon), new PropertyMetadata(AppIconKind.Star, OnIconPropertyChanged));

    public static readonly DependencyProperty IsSelectedProperty = DependencyProperty.Register(
        nameof(IsSelected), typeof(bool), typeof(AppIcon), new PropertyMetadata(false, OnIconPropertyChanged));

    public static readonly DependencyProperty IconSizeProperty = DependencyProperty.Register(
        nameof(IconSize), typeof(double), typeof(AppIcon), new PropertyMetadata(18d));

    public static readonly DependencyProperty IconBrushProperty = DependencyProperty.Register(
        nameof(IconBrush), typeof(Brush), typeof(AppIcon), new PropertyMetadata(null, OnIconPropertyChanged));

    public static readonly DependencyProperty SelectedBrushProperty = DependencyProperty.Register(
        nameof(SelectedBrush), typeof(Brush), typeof(AppIcon), new PropertyMetadata(null, OnIconPropertyChanged));

    public static readonly DependencyProperty StrokeThicknessProperty = DependencyProperty.Register(
        nameof(StrokeThickness), typeof(double), typeof(AppIcon), new PropertyMetadata(1.6d, OnIconPropertyChanged));

    public AppIcon()
    {
        InitializeComponent();
        ActualThemeChanged += (_, _) => UpdateIcon();
        Loaded += (_, _) => UpdateIcon();
    }

    public AppIconKind IconKind
    {
        get => (AppIconKind)GetValue(IconKindProperty);
        set => SetValue(IconKindProperty, value);
    }

    public bool IsSelected
    {
        get => (bool)GetValue(IsSelectedProperty);
        set => SetValue(IsSelectedProperty, value);
    }

    public double IconSize
    {
        get => (double)GetValue(IconSizeProperty);
        set => SetValue(IconSizeProperty, value);
    }

    public Brush? IconBrush
    {
        get => (Brush?)GetValue(IconBrushProperty);
        set => SetValue(IconBrushProperty, value);
    }

    public Brush? SelectedBrush
    {
        get => (Brush?)GetValue(SelectedBrushProperty);
        set => SetValue(SelectedBrushProperty, value);
    }

    public double StrokeThickness
    {
        get => (double)GetValue(StrokeThicknessProperty);
        set => SetValue(StrokeThicknessProperty, value);
    }

    private static void OnIconPropertyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is AppIcon icon)
        {
            icon.UpdateIcon();
        }
    }

    private void UpdateIcon()
    {
        if (PrimaryPath is null || SecondaryPath is null)
        {
            return;
        }

        Brush normalBrush = IconBrush ?? GetBrush("AppInkMutedBrush");
        Brush selectedBrush = SelectedBrush ?? GetDefaultSelectedBrush();
        Brush activeBrush = IsSelected ? selectedBrush : normalBrush;

        PrimaryPath.Data = CreatePrimaryGeometry(IconKind);
        SecondaryPath.Data = CreateSecondaryGeometry(IconKind);
        SecondaryPath.Visibility = SecondaryPath.Data is null ? Visibility.Collapsed : Visibility.Visible;

        PrimaryPath.StrokeThickness = StrokeThickness;
        SecondaryPath.StrokeThickness = StrokeThickness;
        PrimaryPath.Stroke = activeBrush;
        SecondaryPath.Stroke = activeBrush;
        PrimaryPath.Fill = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        SecondaryPath.Fill = new SolidColorBrush(Microsoft.UI.Colors.Transparent);

        if (IsSelected)
        {
            ApplySelectedFill(activeBrush);
        }
    }

    private void ApplySelectedFill(Brush activeBrush)
    {
        switch (IconKind)
        {
            case AppIconKind.Star:
                PrimaryPath.Fill = activeBrush;
                break;
            case AppIconKind.Watch:
                PrimaryPath.Fill = GetBrush("AppSurfaceSubtleBrush");
                SecondaryPath.Fill = activeBrush;
                break;
            case AppIconKind.PrivateRepository:
                PrimaryPath.Fill = GetBrush("AppSurfaceSubtleBrush");
                break;
        }
    }

    private Brush GetDefaultSelectedBrush()
    {
        return IconKind switch
        {
            AppIconKind.Watch => GetBrush("AppAccentBrush"),
            AppIconKind.PrivateRepository => GetBrush("AppWarmAccentBrush"),
            _ => GetBrush("AppWarmAccentBrush")
        };
    }

    private static Geometry CreatePrimaryGeometry(AppIconKind iconKind)
    {
        return iconKind switch
        {
            AppIconKind.Watch => CreateWatchGeometry(),
            AppIconKind.Fork => CreateForkLineGeometry(),
            AppIconKind.PublicRepository => CreatePublicRepositoryGeometry(),
            AppIconKind.PrivateRepository => CreatePrivateRepositoryGeometry(),
            _ => CreateStarGeometry()
        };
    }

    private static Geometry? CreateSecondaryGeometry(AppIconKind iconKind)
    {
        return iconKind switch
        {
            AppIconKind.Watch => CreateCircleGeometry(10, 10, 2.25),
            AppIconKind.Fork => CreateForkNodeGeometry(),
            _ => null
        };
    }

    private static PathGeometry CreateStarGeometry()
    {
        return CreateGeometry(new PathFigure
        {
            StartPoint = new Point(10, 2.6),
            IsClosed = true,
            Segments =
            {
                new LineSegment { Point = new Point(12.24, 7.12) },
                new LineSegment { Point = new Point(17.25, 7.85) },
                new LineSegment { Point = new Point(13.62, 11.38) },
                new LineSegment { Point = new Point(14.48, 16.36) },
                new LineSegment { Point = new Point(10, 14) },
                new LineSegment { Point = new Point(5.52, 16.36) },
                new LineSegment { Point = new Point(6.38, 11.38) },
                new LineSegment { Point = new Point(2.75, 7.85) },
                new LineSegment { Point = new Point(7.76, 7.12) }
            }
        });
    }

    private static PathGeometry CreateWatchGeometry()
    {
        return CreateGeometry(new PathFigure
        {
            StartPoint = new Point(1.75, 10),
            IsClosed = true,
            Segments =
            {
                new BezierSegment { Point1 = new Point(3.55, 6.65), Point2 = new Point(6.35, 5), Point3 = new Point(10, 5) },
                new BezierSegment { Point1 = new Point(13.65, 5), Point2 = new Point(16.45, 6.65), Point3 = new Point(18.25, 10) },
                new BezierSegment { Point1 = new Point(16.45, 13.35), Point2 = new Point(13.65, 15), Point3 = new Point(10, 15) },
                new BezierSegment { Point1 = new Point(6.35, 15), Point2 = new Point(3.55, 13.35), Point3 = new Point(1.75, 10) }
            }
        });
    }

    private static PathGeometry CreateForkLineGeometry()
    {
        PathGeometry geometry = new PathGeometry();
        geometry.Figures.Add(new PathFigure
        {
            StartPoint = new Point(6, 6),
            Segments =
            {
                new LineSegment { Point = new Point(6, 8.25) },
                new BezierSegment { Point1 = new Point(6, 10.35), Point2 = new Point(7.65, 12), Point3 = new Point(9.75, 12) },
                new LineSegment { Point = new Point(10.25, 12) },
                new BezierSegment { Point1 = new Point(12.35, 12), Point2 = new Point(14, 13.65), Point3 = new Point(14, 15.75) }
            }
        });
        geometry.Figures.Add(new PathFigure
        {
            StartPoint = new Point(14, 6),
            Segments =
            {
                new LineSegment { Point = new Point(14, 8.25) },
                new BezierSegment { Point1 = new Point(14, 10.35), Point2 = new Point(12.35, 12), Point3 = new Point(10.25, 12) }
            }
        });
        return geometry;
    }

    private static PathGeometry CreateForkNodeGeometry()
    {
        PathGeometry geometry = new PathGeometry();
        AddCircleFigure(geometry, 6, 4, 2);
        AddCircleFigure(geometry, 14, 4, 2);
        AddCircleFigure(geometry, 14, 17, 2);
        return geometry;
    }

    private static PathGeometry CreatePublicRepositoryGeometry()
    {
        PathGeometry geometry = new PathGeometry();
        geometry.Figures.Add(new PathFigure
        {
            StartPoint = new Point(5, 3.5),
            IsClosed = true,
            Segments =
            {
                new LineSegment { Point = new Point(13.5, 3.5) },
                new BezierSegment { Point1 = new Point(14.6, 3.5), Point2 = new Point(15.5, 4.4), Point3 = new Point(15.5, 5.5) },
                new LineSegment { Point = new Point(15.5, 16.5) },
                new LineSegment { Point = new Point(5.5, 16.5) },
                new BezierSegment { Point1 = new Point(4.4, 16.5), Point2 = new Point(3.5, 15.6), Point3 = new Point(3.5, 14.5) },
                new LineSegment { Point = new Point(3.5, 5) },
                new BezierSegment { Point1 = new Point(3.5, 4.17), Point2 = new Point(4.17, 3.5), Point3 = new Point(5, 3.5) }
            }
        });
        geometry.Figures.Add(CreateLineFigure(5.5, 13.5, 15.5, 13.5));
        geometry.Figures.Add(CreateLineFigure(6.25, 7, 12.75, 7));
        return geometry;
    }

    private static PathGeometry CreatePrivateRepositoryGeometry()
    {
        PathGeometry geometry = new PathGeometry();
        geometry.Figures.Add(new PathFigure
        {
            StartPoint = new Point(5.25, 8.5),
            IsClosed = true,
            Segments =
            {
                new LineSegment { Point = new Point(14.75, 8.5) },
                new BezierSegment { Point1 = new Point(15.44, 8.5), Point2 = new Point(16, 9.06), Point3 = new Point(16, 9.75) },
                new LineSegment { Point = new Point(16, 16.25) },
                new BezierSegment { Point1 = new Point(16, 16.94), Point2 = new Point(15.44, 17.5), Point3 = new Point(14.75, 17.5) },
                new LineSegment { Point = new Point(5.25, 17.5) },
                new BezierSegment { Point1 = new Point(4.56, 17.5), Point2 = new Point(4, 16.94), Point3 = new Point(4, 16.25) },
                new LineSegment { Point = new Point(4, 9.75) },
                new BezierSegment { Point1 = new Point(4, 9.06), Point2 = new Point(4.56, 8.5), Point3 = new Point(5.25, 8.5) }
            }
        });
        geometry.Figures.Add(new PathFigure
        {
            StartPoint = new Point(6.75, 8.5),
            Segments =
            {
                new LineSegment { Point = new Point(6.75, 6.75) },
                new BezierSegment { Point1 = new Point(6.75, 4.96), Point2 = new Point(8.21, 3.5), Point3 = new Point(10, 3.5) },
                new BezierSegment { Point1 = new Point(11.79, 3.5), Point2 = new Point(13.25, 4.96), Point3 = new Point(13.25, 6.75) },
                new LineSegment { Point = new Point(13.25, 8.5) }
            }
        });
        geometry.Figures.Add(CreateLineFigure(10, 12, 10, 14));
        return geometry;
    }

    private static PathGeometry CreateCircleGeometry(double centerX, double centerY, double radius)
    {
        PathGeometry geometry = new PathGeometry();
        AddCircleFigure(geometry, centerX, centerY, radius);
        return geometry;
    }

    private static void AddCircleFigure(PathGeometry geometry, double centerX, double centerY, double radius)
    {
        geometry.Figures.Add(new PathFigure
        {
            StartPoint = new Point(centerX - radius, centerY),
            IsClosed = true,
            Segments =
            {
                new ArcSegment
                {
                    Point = new Point(centerX + radius, centerY),
                    Size = new Size(radius, radius),
                    IsLargeArc = true,
                    SweepDirection = SweepDirection.Clockwise
                },
                new ArcSegment
                {
                    Point = new Point(centerX - radius, centerY),
                    Size = new Size(radius, radius),
                    IsLargeArc = true,
                    SweepDirection = SweepDirection.Clockwise
                }
            }
        });
    }

    private static PathFigure CreateLineFigure(double startX, double startY, double endX, double endY)
    {
        return new PathFigure
        {
            StartPoint = new Point(startX, startY),
            Segments =
            {
                new LineSegment { Point = new Point(endX, endY) }
            }
        };
    }

    private static PathGeometry CreateGeometry(PathFigure figure)
    {
        PathGeometry geometry = new PathGeometry();
        geometry.Figures.Add(figure);
        return geometry;
    }

    private static Brush GetBrush(string resourceKey)
    {
        return Application.Current.Resources.TryGetValue(resourceKey, out object value) && value is Brush brush
            ? brush
            : new SolidColorBrush(Microsoft.UI.Colors.White);
    }
}
