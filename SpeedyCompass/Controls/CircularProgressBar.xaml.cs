namespace SpeedyCompass.Controls;

public partial class CircularProgressBar : ContentView
{
    public static readonly BindableProperty ProgressProperty = BindableProperty.Create(nameof(Progress), typeof(double), typeof(CircularProgressBar), 0.0, propertyChanged: OnUIChanged);
    public static readonly BindableProperty ProgressColorProperty = BindableProperty.Create(nameof(ProgressColor), typeof(Color), typeof(CircularProgressBar), Colors.DodgerBlue, propertyChanged: OnUIChanged);
    public static readonly BindableProperty TrackColorProperty = BindableProperty.Create(nameof(TrackColor), typeof(Color), typeof(CircularProgressBar), Colors.LightGray, propertyChanged: OnUIChanged);
    public static readonly BindableProperty StrokeThicknessProperty = BindableProperty.Create(nameof(StrokeThickness), typeof(float), typeof(CircularProgressBar), 6f, propertyChanged: OnUIChanged);

    public double Progress { get => (double)GetValue(ProgressProperty); set => SetValue(ProgressProperty, value); }
    public Color ProgressColor { get => (Color)GetValue(ProgressColorProperty); set => SetValue(ProgressColorProperty, value); }
    public Color TrackColor { get => (Color)GetValue(TrackColorProperty); set => SetValue(TrackColorProperty, value); }
    public float StrokeThickness { get => (float)GetValue(StrokeThicknessProperty); set => SetValue(StrokeThicknessProperty, value); }

    private readonly CircularProgressDrawable _drawable;

    public CircularProgressBar()
    {
        InitializeComponent();
        _drawable = new CircularProgressDrawable();
        ProgressView.Drawable = _drawable;
    }

    private static void OnUIChanged(BindableObject bindable, object oldValue, object newValue)
    {
        var control = (CircularProgressBar)bindable;
        control._drawable.Progress = control.Progress;
        control._drawable.ProgressColor = control.ProgressColor;
        control._drawable.TrackColor = control.TrackColor;
        control._drawable.StrokeThickness = control.StrokeThickness;

        // THE FIX: Force the GPU redraw safely on the Main Thread
        MainThread.BeginInvokeOnMainThread(() =>
        {
            control.ProgressView.Invalidate();
        });
    }

    public void ProgressTo(double value, uint length, Easing easing)
    {
        // THE FIX: Don't kill the active animation if the value hasn't meaningfully changed!
        if (Math.Abs(Progress - value) < 0.001) return;

        this.AbortAnimation("ProgressAnimation");
        var animation = new Animation(v => Progress = v, Progress, value);
        animation.Commit(this, "ProgressAnimation", 16, length, easing);
    }
}

public class CircularProgressDrawable : IDrawable
{
    public double Progress { get; set; }
    public Color ProgressColor { get; set; } = Colors.DodgerBlue;
    public Color TrackColor { get; set; } = Colors.LightGray;
    public float StrokeThickness { get; set; } = 6f;

    public void Draw(ICanvas canvas, RectF dirtyRect)
    {
        float padding = StrokeThickness / 2f;
        float radius = (Math.Min(dirtyRect.Width, dirtyRect.Height) / 2f) - padding;
        var center = new PointF(dirtyRect.Width / 2f, dirtyRect.Height / 2f);

        canvas.StrokeSize = StrokeThickness;
        canvas.StrokeLineCap = LineCap.Round;

        // Background Track
        canvas.StrokeColor = TrackColor;
        canvas.DrawCircle(center, radius);

        // Foreground Progress Arc
        if (Progress > 0)
        {
            canvas.StrokeColor = ProgressColor;

            // THE FIX: MAUI Bug - DrawArc fails if it sweeps exactly 360 degrees.
            if (Progress >= 1.0)
            {
                canvas.DrawCircle(center, radius);
            }
            else
            {
                float startAngle = 90; // Start at top (12 o'clock)
                float endAngle = 90 - (float)(Progress * 360);
                canvas.DrawArc(center.X - radius, center.Y - radius, radius * 2, radius * 2, startAngle, endAngle, true, false);
            }
        }
    }
}