using System;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Media3D;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace AIfootball.App;

public partial class StartupWindow : Window
{
    private readonly DispatcherTimer _finishTimer = new();
    private readonly Stopwatch _animationClock = new();
    private AxisAngleRotation3D _keeperDiveRotation = null!;
    private AxisAngleRotation3D _keeperArmRotation = null!;
    private TranslateTransform3D _keeperOffset = null!;
    private PathFigure _flightPathFigure = null!;
    private Point _lastFlightPathPoint;
    private bool _isFinishing;

    public event EventHandler? StartupCompleted;

    public StartupWindow()
    {
        InitializeComponent();
        InitializeFlightPath();
        BuildKeeperModel();
        Loaded += OnLoaded;
        _finishTimer.Interval = TimeSpan.FromSeconds(8);
        _finishTimer.Tick += (_, _) => Finish();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        LayoutScene();
        _animationClock.Restart();
        CompositionTarget.Rendering += AnimateKeeper3D;
        Animate(IntroCopy, UIElement.OpacityProperty, 0d, 1d, 650, 120);
        Animate(GoalScene, UIElement.OpacityProperty, 0d, 1d, 700, 360);
        Animate(Telemetry, UIElement.OpacityProperty, 0d, 1d, 450, 820);
        Animate(Footer, UIElement.OpacityProperty, 0d, 1d, 450, 920);
        Animate(SkipHint, UIElement.OpacityProperty, 0d, 0.78d, 450, 1250);
        _finishTimer.Start();
    }

    private void InitializeFlightPath()
    {
        _flightPathFigure = new PathFigure
        {
            StartPoint = new Point(591d, 641d),
            IsClosed = false,
            IsFilled = false
        };
        FlightPath.Data = new PathGeometry(new[] { _flightPathFigure });
        _lastFlightPathPoint = _flightPathFigure.StartPoint;
    }

    private void StartupRoot_SizeChanged(object sender, SizeChangedEventArgs e) => LayoutScene();

    private void LayoutScene()
    {
        if (StartupRoot.ActualWidth <= 0d || StartupRoot.ActualHeight <= 0d)
            return;

        double scale = Math.Min(StartupRoot.ActualWidth / 1600d, StartupRoot.ActualHeight / 900d);
        SceneCanvas.LayoutTransform = new ScaleTransform(scale, scale);
        SceneCanvas.Margin = new Thickness(0d);
    }

    private void AnimateKeeper3D(object? sender, EventArgs e)
    {
        // The reference save is a full sequence: set, push-off, airborne reach, landing, recovery.
        double progress = Math.Clamp((_animationClock.Elapsed.TotalSeconds - 1.35d) / 3.8d, 0d, 1d);
        double eased = 1d - Math.Pow(1d - progress, 3d);

        _keeperDiveRotation.Angle = 74d * eased;
        _keeperArmRotation.Angle = 52d * eased;
        _keeperOffset.OffsetX = 1.52d * eased;
        _keeperOffset.OffsetY = 0.28d * Math.Sin(progress * Math.PI);

        double travel = progress < 0.9d
            ? SmoothStep(Math.Min(progress / 0.68d, 1d))
            : 1d - SmoothStep((progress - 0.9d) / 0.1d);
        Canvas.SetLeft(AnimatedKeeper, 285d + 108d * travel);
        // The standing pose's feet are at y=205 inside the control; this baseline
        // places them on the goal-line floor (about y=370 in GoalScene).
        Canvas.SetTop(AnimatedKeeper, 165d - 56d * Math.Sin(Math.Min(progress / 0.68d, 1d) * Math.PI) * (progress < 0.9d ? 1d : 0d));
        AnimatedKeeper.Progress = progress;

        double ballProgress = Math.Clamp((_animationClock.Elapsed.TotalSeconds - 1.15d) / 3.2d, 0d, 1d);
        double ballX = 591d + 654d * ballProgress;
        double ballY = 641d - 411d * ballProgress - 105d * Math.Sin(Math.PI * ballProgress);
        Canvas.SetLeft(Ball, ballX);
        Canvas.SetTop(Ball, ballY);
        Canvas.SetLeft(BallGlow, ballX - 6d);
        Canvas.SetTop(BallGlow, ballY - 6d);
        Ball.Opacity = ballProgress <= 0d ? 0d : 1d;
        BallGlow.Opacity = ballProgress <= 0d ? 0d : 0.9d;

        UpdateFlightPath(ballProgress, ballX, ballY);
        // The old particle canvas contained the complete predicted trajectory, so it
        // could appear ahead of the ball. The path above is now the live trail.
        ParticleTrail.Opacity = 0d;
        CoordinateX.Text = $"X      +{2.782d * ballProgress:0.000} m";
        CoordinateY.Text = $"Y      +{11d * (1d - ballProgress):0.000} m";
        CoordinateZ.Text = $"Z      +{0.1d + 1.803d * ballProgress:0.000} m";

        if (_isFinishing)
            CompositionTarget.Rendering -= AnimateKeeper3D;
    }

    private void UpdateFlightPath(double ballProgress, double ballX, double ballY)
    {
        FlightPath.Opacity = ballProgress <= 0d ? 0d : 0.9d;
        if (ballProgress <= 0d)
            return;

        var currentPoint = new Point(ballX, ballY);
        if ((currentPoint - _lastFlightPathPoint).Length < 1d)
            return;

        _flightPathFigure.Segments.Add(new LineSegment(currentPoint, true));
        _lastFlightPathPoint = currentPoint;
    }

    private static double SmoothStep(double value)
    {
        value = Math.Clamp(value, 0d, 1d);
        return value * value * (3d - 2d * value);
    }

    private void BuildKeeperModel()
    {
        var root = new Model3DGroup();
        root.Children.Add(new AmbientLight(Color.FromRgb(32, 78, 92)));
        root.Children.Add(new DirectionalLight(Color.FromRgb(180, 246, 255), new Vector3D(-0.4, -0.8, -1)));
        root.Children.Add(new PointLight(Color.FromRgb(53, 220, 235), new Point3D(0, 2, 3)) { Range = 7 });

        _keeperDiveRotation = new AxisAngleRotation3D(new Vector3D(0, 0, 1), 0);
        _keeperOffset = new TranslateTransform3D();
        var keeperTransform = new Transform3DGroup();
        keeperTransform.Children.Add(new RotateTransform3D(_keeperDiveRotation, new Point3D(0, 1.05, 0)));
        keeperTransform.Children.Add(_keeperOffset);
        root.Transform = keeperTransform;

        Color kit = Color.FromRgb(29, 139, 151);
        Color limb = Color.FromRgb(117, 224, 237);
        Color dark = Color.FromRgb(8, 29, 39);
        AddBox(root, new Vector3D(0.78, 0.92, 0.42), new Point3D(0, 1.36, 0), kit, null);
        AddSphere(root, 0.26, new Point3D(0, 2.08, 0), dark, null);
        AddBox(root, new Vector3D(0.38, 0.35, 0.36), new Point3D(0, 1.84, 0), limb, null);

        _keeperArmRotation = new AxisAngleRotation3D(new Vector3D(0, 0, 1), 0);
        var leftArm = new Transform3DGroup();
        leftArm.Children.Add(new RotateTransform3D(_keeperArmRotation, new Point3D(-0.36, 1.65, 0)));
        AddBox(root, new Vector3D(0.20, 0.88, 0.20), new Point3D(-0.46, 1.30, 0), limb, leftArm);

        var rightArm = new Transform3DGroup();
        rightArm.Children.Add(new RotateTransform3D(new AxisAngleRotation3D(new Vector3D(0, 0, 1), -24), new Point3D(0.36, 1.65, 0)));
        rightArm.Children.Add(new RotateTransform3D(_keeperArmRotation, new Point3D(0.36, 1.65, 0)));
        AddBox(root, new Vector3D(0.20, 0.88, 0.20), new Point3D(0.46, 1.30, 0), limb, rightArm);
        AddBox(root, new Vector3D(0.24, 1.05, 0.25), new Point3D(-0.24, 0.48, 0), limb, null);
        AddBox(root, new Vector3D(0.24, 1.05, 0.25), new Point3D(0.24, 0.48, 0), limb, null);
        AddBox(root, new Vector3D(0.42, 0.13, 0.35), new Point3D(-0.30, -0.08, 0.05), kit, null);
        AddBox(root, new Vector3D(0.42, 0.13, 0.35), new Point3D(0.30, -0.08, 0.05), kit, null);

        KeeperViewport.Children.Add(new ModelVisual3D { Content = root });
    }

    private static void AddBox(Model3DGroup group, Vector3D size, Point3D center, Color color, Transform3D? transform)
    {
        double x = size.X / 2d, y = size.Y / 2d, z = size.Z / 2d;
        var mesh = new MeshGeometry3D
        {
            Positions = new Point3DCollection
            {
                new(-x, -y, -z), new(x, -y, -z), new(x, y, -z), new(-x, y, -z),
                new(-x, -y, z), new(x, -y, z), new(x, y, z), new(-x, y, z)
            },
            TriangleIndices = new Int32Collection
            {
                0,2,1, 0,3,2, 4,5,6, 4,6,7, 0,1,5, 0,5,4,
                3,7,6, 3,6,2, 1,2,6, 1,6,5, 0,4,7, 0,7,3
            }
        };
        var material = new MaterialGroup();
        material.Children.Add(new DiffuseMaterial(new SolidColorBrush(color)));
        material.Children.Add(new EmissiveMaterial(new SolidColorBrush(Color.FromArgb(72, color.R, color.G, color.B))));
        var model = new GeometryModel3D(mesh, material) { BackMaterial = material };
        var placement = new Transform3DGroup();
        if (transform != null)
            placement.Children.Add(transform);
        placement.Children.Add(new TranslateTransform3D(center.X, center.Y, center.Z));
        model.Transform = placement;
        group.Children.Add(model);
    }

    private static void AddSphere(Model3DGroup group, double radius, Point3D center, Color color, Transform3D? transform)
    {
        const int rings = 12;
        const int segments = 16;
        var mesh = new MeshGeometry3D();
        for (int ring = 0; ring <= rings; ring++)
        {
            double phi = Math.PI * ring / rings;
            for (int segment = 0; segment <= segments; segment++)
            {
                double theta = Math.PI * 2d * segment / segments;
                mesh.Positions.Add(new Point3D(radius * Math.Sin(phi) * Math.Cos(theta), radius * Math.Cos(phi), radius * Math.Sin(phi) * Math.Sin(theta)));
            }
        }
        for (int ring = 0; ring < rings; ring++)
        for (int segment = 0; segment < segments; segment++)
        {
            int a = ring * (segments + 1) + segment;
            int b = a + segments + 1;
            mesh.TriangleIndices.Add(a); mesh.TriangleIndices.Add(b); mesh.TriangleIndices.Add(a + 1);
            mesh.TriangleIndices.Add(a + 1); mesh.TriangleIndices.Add(b); mesh.TriangleIndices.Add(b + 1);
        }
        var material = new DiffuseMaterial(new SolidColorBrush(color));
        var model = new GeometryModel3D(mesh, material) { BackMaterial = material };
        var placement = new Transform3DGroup();
        if (transform != null)
            placement.Children.Add(transform);
        placement.Children.Add(new TranslateTransform3D(center.X, center.Y, center.Z));
        model.Transform = placement;
        group.Children.Add(model);
    }

    private static void Animate(DependencyObject target, DependencyProperty property, double from, double to, int milliseconds, int delay)
    {
        var animation = new DoubleAnimation(from, to, TimeSpan.FromMilliseconds(milliseconds))
        {
            BeginTime = TimeSpan.FromMilliseconds(delay),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        var storyboard = new Storyboard();
        storyboard.Children.Add(animation);
        Storyboard.SetTarget(animation, target);
        Storyboard.SetTargetProperty(animation, new PropertyPath(property));
        storyboard.Begin();
    }

    private void AnimateText(System.Windows.Controls.TextBlock target, string value, int delay)
    {
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(delay) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            target.Text = value;
        };
        timer.Start();
    }

    private void StartupWindow_MouseLeftButtonUp(object sender, System.Windows.Input.MouseButtonEventArgs e) => Finish();

    private void Finish()
    {
        if (_isFinishing)
            return;

        _isFinishing = true;
        _finishTimer.Stop();
        var fade = new DoubleAnimation(1d, 0d, TimeSpan.FromMilliseconds(320));
        fade.Completed += (_, _) => StartupCompleted?.Invoke(this, EventArgs.Empty);
        BeginAnimation(OpacityProperty, fade);
    }
}
