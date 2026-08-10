using System.Diagnostics;
using System.Drawing.Drawing2D;

namespace IdleShutdown.AgentApp;

internal enum WarningCancellationReason
{
    None,
    LocalInput,
    ContinueButton,
    MachineActivity
}

internal sealed class WarningForm : Form
{
    private readonly int _warningSeconds;
    private readonly uint? _inputTickAtStart;
    private readonly Stopwatch _countdown = Stopwatch.StartNew();
    private readonly System.Windows.Forms.Timer _timer;
    private readonly UiStrings _ui;
    private readonly UiPalette _palette;
    private readonly long _machineActivityVersion;
    private TimeSpan _lastTopmostRefreshAt;

    private readonly Label _countdownLabel;
    private readonly Label _statusLabel;
    private readonly Panel _progressTrack;
    private readonly Panel _progressFill;
    private readonly Button _continueButton;
    private readonly Panel _iconCircle;

    public WarningForm(
        int warningSeconds,
        uint? inputTickAtStart)
    {
        _warningSeconds = Math.Max(1, warningSeconds);
        _inputTickAtStart = inputTickAtStart;
        _ui = LocalizedText.Current;
        _palette = UiTheme.Current;
        _machineActivityVersion = MachineActivitySignal.ReadVersion();

        AutoScaleMode = AutoScaleMode.Dpi;
        BackColor = Color.Magenta;
        TransparencyKey = Color.Magenta;
        ClientSize = new Size(580, 370);
        FormBorderStyle = FormBorderStyle.None;
        KeyPreview = true;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowIcon = true;
        ShowInTaskbar = true;
        Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
        StartPosition = FormStartPosition.CenterScreen;
        Text = _ui.WindowTitle;
        TopMost = true;
        DoubleBuffered = true;

        var shell = new Panel
        {
            Dock = DockStyle.Fill,
            BackColor = Color.Magenta,
            Padding = new Padding(0)
        };

        var card = new Panel
        {
            Dock = DockStyle.Fill,
            BackColor = _palette.CardBackground,
            Padding = new Padding(44, 34, 44, 28)
        };

        shell.Controls.Add(card);
        Controls.Add(shell);

        _iconCircle = new Panel
        {
            BackColor = _palette.IconBackground,
            Location = new Point(44, 34),
            Size = new Size(58, 58)
        };

        _iconCircle.Paint += DrawSleepIcon;
        card.Controls.Add(_iconCircle);

        var title = new Label
        {
            AutoSize = false,
            Font = new Font("Segoe UI", 20F, FontStyle.Bold),
            ForeColor = _palette.PrimaryText,
            Location = new Point(120, 34),
            Size = new Size(410, 40),
            Text = _ui.Title
        };

        card.Controls.Add(title);

        _statusLabel = new Label
        {
            AutoSize = false,
            Font = new Font("Segoe UI", 10.5F, FontStyle.Regular),
            ForeColor = _palette.SecondaryText,
            Location = new Point(120, 75),
            Size = new Size(408, 50),
            Text = _ui.Status
        };

        card.Controls.Add(_statusLabel);

        var countdownCaption = new Label
        {
            AutoSize = false,
            Font = new Font("Segoe UI", 10F, FontStyle.Regular),
            ForeColor = _palette.MutedText,
            Location = new Point(44, 138),
            Size = new Size(492, 24),
            Text = _ui.CountdownCaption,
            TextAlign = ContentAlignment.MiddleCenter
        };

        card.Controls.Add(countdownCaption);

        _countdownLabel = new Label
        {
            AutoSize = false,
            Font = new Font("Segoe UI", 40F, FontStyle.Bold),
            ForeColor = _palette.Accent,
            Location = new Point(44, 158),
            Size = new Size(492, 72),
            Text = FormatTime(_warningSeconds),
            TextAlign = ContentAlignment.MiddleCenter
        };

        card.Controls.Add(_countdownLabel);

        _progressTrack = new Panel
        {
            BackColor = _palette.ProgressTrack,
            Location = new Point(44, 238),
            Size = new Size(492, 10)
        };

        _progressFill = new Panel
        {
            BackColor = _palette.Accent,
            Location = new Point(0, 0),
            Size = new Size(492, 10)
        };

        _progressTrack.Controls.Add(_progressFill);
        card.Controls.Add(_progressTrack);

        var hint = new Label
        {
            AutoSize = false,
            Font = new Font("Segoe UI", 9.5F, FontStyle.Regular),
            ForeColor = _palette.MutedText,
            Location = new Point(44, 260),
            Size = new Size(492, 42),
            Text = _ui.Hint,
            TextAlign = ContentAlignment.MiddleCenter
        };

        card.Controls.Add(hint);

        _continueButton = new Button
        {
            BackColor = _palette.Accent,
            Cursor = Cursors.Hand,
            FlatStyle = FlatStyle.Flat,
            Font = new Font("Segoe UI", 11F, FontStyle.Bold),
            ForeColor = _palette.ButtonText,
            Location = new Point(152, 307),
            Size = new Size(276, 48),
            TabIndex = 0,
            Text = _ui.ContinueButton,
            UseVisualStyleBackColor = false
        };

        _continueButton.FlatAppearance.BorderSize = 0;
        _continueButton.FlatAppearance.MouseOverBackColor =
            _palette.AccentHover;
        _continueButton.FlatAppearance.MouseDownBackColor =
            _palette.AccentHover;
        _continueButton.Click += (_, _) =>
            CancelShutdown(
                WarningCancellationReason.ContinueButton);
        card.Controls.Add(_continueButton);

        var versionLabel = new Label
        {
            AutoSize = false,
            Font = new Font("Segoe UI", 7F, FontStyle.Regular),
            ForeColor = _palette.MutedText,
            Location = new Point(476, 348),
            Size = new Size(82, 14),
            TabStop = false,
            Text = GetVersionText(),
            TextAlign = ContentAlignment.BottomRight
        };

        card.Controls.Add(versionLabel);

        AcceptButton = _continueButton;
        CancelButton = _continueButton;

        _timer = new System.Windows.Forms.Timer
        {
            Interval = 100
        };

        _timer.Tick += TimerOnTick;

        Shown += (_, _) =>
        {
            ApplyRoundedRegions(
                shell,
                card,
                _iconCircle,
                _progressTrack,
                _progressFill,
                _continueButton);
            PopupWindowManager.BringToForeground(this);
            BeginInvoke(() => PopupWindowManager.BringToForeground(this));
            _continueButton.Focus();
            _timer.Start();
        };

        Resize += (_, _) =>
        {
            ApplyRoundedRegions(
                shell,
                card,
                _iconCircle,
                _progressTrack,
                _progressFill,
                _continueButton);
        };

        KeyDown += (_, e) =>
        {
            e.SuppressKeyPress = true;
            CancelShutdown(
                WarningCancellationReason.LocalInput);
        };
    }

    public WarningCancellationReason CancellationReason
    {
        get;
        private set;
    }

    protected override void OnFormClosing(
        FormClosingEventArgs e)
    {
        _timer.Stop();
        base.OnFormClosing(e);
    }

    private void TimerOnTick(
        object? sender,
        EventArgs e)
    {
        var currentMachineActivityVersion =
            MachineActivitySignal.ReadVersion();

        if (
            currentMachineActivityVersion != 0 &&
            currentMachineActivityVersion != _machineActivityVersion)
        {
            CancelShutdown(
                WarningCancellationReason.MachineActivity);
            return;
        }

        uint? currentInputTick =
            NativeMethods.TryGetLastInputTick(out var observedInputTick)
                ? observedInputTick
                : null;

        if (WarningDisplayPolicy.HasVerifiedLocalInput(
                _inputTickAtStart,
                currentInputTick))
        {
            CancelShutdown(
                WarningCancellationReason.LocalInput);
            return;
        }

        if (
            _countdown.Elapsed - _lastTopmostRefreshAt >=
            TimeSpan.FromMilliseconds(500))
        {
            PopupWindowManager.KeepTopmostWithoutActivation(this);
            _lastTopmostRefreshAt = _countdown.Elapsed;
        }

        var remaining =
            TimeSpan.FromSeconds(_warningSeconds) -
            _countdown.Elapsed;

        if (remaining <= TimeSpan.Zero)
        {
            _timer.Stop();
            DialogResult = DialogResult.OK;
            Close();
            return;
        }

        var remainingSeconds =
            Math.Max(
                1,
                (int)Math.Ceiling(
                    remaining.TotalSeconds));

        _countdownLabel.Text =
            FormatTime(remainingSeconds);

        var fraction =
            Math.Clamp(
                remaining.TotalSeconds /
                _warningSeconds,
                0.0,
                1.0);

        _progressFill.Width =
            Math.Max(
                1,
                (int)Math.Round(
                    _progressTrack.ClientSize.Width *
                    fraction));

        if (remainingSeconds <= 5)
        {
            _countdownLabel.ForeColor =
                _palette.Danger;

            _progressFill.BackColor =
                _palette.Danger;

            _statusLabel.Text = _ui.UrgentStatus;
        }
    }

    private void CancelShutdown(
        WarningCancellationReason reason)
    {
        _timer.Stop();
        CancellationReason = reason;
        DialogResult = DialogResult.Cancel;
        Close();
    }

    private static string FormatTime(
        int totalSeconds)
    {
        var seconds = Math.Max(0, totalSeconds);
        return $"{seconds / 60:00}:{seconds % 60:00}";
    }

    private static string GetVersionText()
    {
        var version = typeof(WarningForm).Assembly.GetName().Version;

        return version is null
            ? string.Empty
            : $"v{version.Major}.{version.Minor}.{version.Build}";
    }

    private void DrawSleepIcon(
        object? sender,
        PaintEventArgs e)
    {
        e.Graphics.SmoothingMode =
            SmoothingMode.AntiAlias;

        using var moonBrush =
            new SolidBrush(_palette.Accent);

        using var cutoutBrush =
            new SolidBrush(
                _palette.IconBackground);

        using var zBrush =
            new SolidBrush(_palette.Accent);

        using var largeFont =
            new Font(
                "Segoe UI",
                11F,
                FontStyle.Bold);

        using var smallFont =
            new Font(
                "Segoe UI",
                8F,
                FontStyle.Bold);

        e.Graphics.FillEllipse(
            moonBrush,
            10,
            15,
            29,
            29);

        e.Graphics.FillEllipse(
            cutoutBrush,
            20,
            10,
            29,
            29);

        e.Graphics.DrawString(
            "Z",
            largeFont,
            zBrush,
            34F,
            8F);

        e.Graphics.DrawString(
            "z",
            smallFont,
            zBrush,
            43F,
            23F);

        e.Graphics.DrawString(
            "z",
            smallFont,
            zBrush,
            46F,
            34F);
    }
    private static void ApplyRoundedRegions(
        Panel shell,
        Panel card,
        Panel iconCircle,
        Panel progressTrack,
        Panel progressFill,
        Button button)
    {
        shell.Region = null;

        card.Region = CreateRoundedRegion(
            card.ClientRectangle,
            20);

        iconCircle.Region = CreateRoundedRegion(
            iconCircle.ClientRectangle,
            29);

        progressTrack.Region = CreateRoundedRegion(
            progressTrack.ClientRectangle,
            5);

        progressFill.Region = CreateRoundedRegion(
            progressFill.ClientRectangle,
            5);

        button.Region = CreateRoundedRegion(
            button.ClientRectangle,
            12);
    }

    private static Region CreateRoundedRegion(
        Rectangle bounds,
        int radius)
    {
        if (bounds.Width <= 0 || bounds.Height <= 0)
        {
            return new Region();
        }

        var diameter = Math.Max(2, radius * 2);
        var arc = new Rectangle(
            bounds.Location,
            new Size(diameter, diameter));

        using var path = new GraphicsPath();

        path.AddArc(arc, 180, 90);

        arc.X = bounds.Right - diameter;
        path.AddArc(arc, 270, 90);

        arc.Y = bounds.Bottom - diameter;
        path.AddArc(arc, 0, 90);

        arc.X = bounds.Left;
        path.AddArc(arc, 90, 90);

        path.CloseFigure();

        return new Region(path);
    }
}
