using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace MailWidget;

public partial class MainWindow : Window
{
    private const int CompactLayoutVersion = 2;
    private const int CurrentSettingsVersion = 4;

    private readonly SettingsStore _settingsStore = new();
    private readonly GmailMailService _gmailService = new();
    private readonly UpdateService _updateService = new();
    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private readonly SemaphoreSlim _updateCheckGate = new(1, 1);
    private readonly DispatcherTimer _timer;
    private readonly DispatcherTimer _notesSaveTimer;
    private readonly System.Windows.Forms.NotifyIcon _trayIcon;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly AppSettings _settings;

    private bool _exitRequested;
    private bool _hasRenderedCount;
    private int _lastNotifiedCount;
    private bool _isRestoringWindow;
    private bool _isInitializing;
    private bool _isDragging;
    private bool _needsSettingsSave;
    private Point _dragStartCursor;
    private double _dragStartLeft;
    private double _dragStartTop;
    private DateTimeOffset? _lastUpdateCheckUtc;
    private WidgetUpdate? _availableUpdate;

    public MainWindow()
    {
        _isInitializing = true;
        InitializeComponent();

        _settings = _settingsStore.Load();
        _settings.Notes ??= new List<NoteItem>();
        StartWithWindowsCheckBox.IsChecked = _settings.StartWithWindows;
        _isInitializing = false;

        var interval = Math.Clamp(_settings.PollingIntervalSeconds, 30, 600);
        _timer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(interval)
        };
        _timer.Tick += Timer_Tick;

        _notesSaveTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(450)
        };
        _notesSaveTimer.Tick += NotesSaveTimer_Tick;
        BuildNotesPanel();

        _trayIcon = new System.Windows.Forms.NotifyIcon
        {
            Icon = System.Drawing.SystemIcons.Information,
            Visible = true,
            Text = "Gmail sur le bureau"
        };
        _trayIcon.DoubleClick += (_, _) => ShowWidget();

        var menu = new System.Windows.Forms.ContextMenuStrip();
        menu.Items.Add("Ouvrir", null, (_, _) => Dispatcher.Invoke(ShowWidget));
        menu.Items.Add("Actualiser", null, (_, _) => Dispatcher.BeginInvoke(new Action(RefreshFromTray)));
        menu.Items.Add("Rechercher une mise à jour", null, (_, _) => Dispatcher.BeginInvoke(new Action(CheckForUpdateFromTray)));
        menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
        menu.Items.Add("Quitter", null, (_, _) => Dispatcher.Invoke(ExitApplication));
        _trayIcon.ContextMenuStrip = menu;
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        try
        {
            RestoreOrPlaceWindow();

            if (_settings.StartWithWindows)
            {
                try
                {
                    StartupManager.SetEnabled(true);
                }
                catch (Exception ex)
                {
                    StatusText.Text = $"Démarrage automatique indisponible : {ex.Message}";
                }
            }

            if (_needsSettingsSave)
            {
                _needsSettingsSave = false;
                await _settingsStore.SaveAsync(_settings, _lifetime.Token);
            }

            _timer.Start();
            await RefreshAsync(true);
            await CheckForUpdateAsync(true);
        }
        catch (Exception ex)
        {
            // Keep the widget open if the desktop host or the first Gmail
            // connection fails. The error remains visible in the mail panel.
            StatusText.Text = FriendlyError(ex);
        }
    }

    // Kept as a no-op for compatibility with older MainWindow.xaml files
    // that still declare the Deactivated event. The widget is intentionally
    // not reattached to Explorer here because that caused it to disappear.
    private void Window_Deactivated(object? sender, EventArgs e)
    {
    }

    private async void Timer_Tick(object? sender, EventArgs e)
    {
        await RefreshAsync(false);
        await CheckForUpdateAsync(false);
    }

    private async void RefreshFromTray()
    {
        await RefreshAsync(false);
    }

    private async void CheckForUpdateFromTray()
    {
        await CheckForUpdateAsync(true);
    }

    private async Task CheckForUpdateAsync(bool notify)
    {
        if (_lastUpdateCheckUtc.HasValue
            && DateTimeOffset.UtcNow - _lastUpdateCheckUtc.Value < TimeSpan.FromMinutes(10))
        {
            return;
        }

        if (!await _updateCheckGate.WaitAsync(0))
        {
            return;
        }

        try
        {
            _lastUpdateCheckUtc = DateTimeOffset.UtcNow;
            var update = await _updateService.CheckAsync(_lifetime.Token);
            if (update is null)
            {
                return;
            }

            var isNewUpdate = _availableUpdate?.VersionText != update.VersionText;
            _availableUpdate = update;
            UpdateButton.Visibility = Visibility.Visible;

            if (notify && isNewUpdate)
            {
                _trayIcon.ShowBalloonTip(
                    6000,
                    "Widget personnalisé",
                    "Une mise à jour est disponible. Clique sur la flèche du widget pour l'installer.",
                    System.Windows.Forms.ToolTipIcon.Info);
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
            // Normal shutdown.
        }
        finally
        {
            _updateCheckGate.Release();
        }
    }

    private void BuildNotesPanel()
    {
        NotesPanel.Children.Clear();

        if (_settings.Notes.Count == 0)
        {
            NotesPanel.Children.Add(new TextBlock
            {
                Text = "Aucune case pour le moment.\nClique sur « Ajouter une case ». ",
                Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(185, 198, 216)),
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(2, 6, 2, 0)
            });
            return;
        }

        for (var index = 0; index < _settings.Notes.Count; index++)
        {
            var note = _settings.Notes[index];
            var card = new Border
            {
                Background = new SolidColorBrush(System.Windows.Media.Color.FromArgb(34, 255, 255, 255)),
                BorderBrush = new SolidColorBrush(System.Windows.Media.Color.FromArgb(48, 255, 255, 255)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(8),
                Margin = new Thickness(0, 0, 0, 8)
            };

            var cardLayout = new Grid();
            cardLayout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            cardLayout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var noteHeader = new Grid { Margin = new Thickness(2, 0, 0, 4) };
            noteHeader.ColumnDefinitions.Add(new ColumnDefinition());
            noteHeader.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var noteTitle = new TextBlock
            {
                Text = $"Case {index + 1}",
                FontSize = 11,
                FontWeight = FontWeights.SemiBold,
                Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(221, 230, 242)),
                VerticalAlignment = VerticalAlignment.Center
            };
            noteHeader.Children.Add(noteTitle);

            var removeButton = new System.Windows.Controls.Button
            {
                Content = "×",
                Style = (Style)FindResource("IconButton"),
                Width = 22,
                Height = 22,
                FontSize = 15,
                Tag = note,
                ToolTip = "Supprimer cette case"
            };
            removeButton.Click += RemoveNoteButton_Click;
            Grid.SetColumn(removeButton, 1);
            noteHeader.Children.Add(removeButton);

            var textBox = new System.Windows.Controls.TextBox
            {
                Text = note.Text,
                AcceptsReturn = true,
                TextWrapping = TextWrapping.Wrap,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Background = new SolidColorBrush(System.Windows.Media.Color.FromArgb(30, 255, 255, 255)),
                Foreground = System.Windows.Media.Brushes.White,
                CaretBrush = System.Windows.Media.Brushes.White,
                BorderBrush = new SolidColorBrush(System.Windows.Media.Color.FromArgb(58, 255, 255, 255)),
                BorderThickness = new Thickness(1),
                Padding = new Thickness(8, 7, 8, 7),
                MinHeight = 62,
                FontSize = 13,
                Tag = note,
                ToolTip = "Écris ta note ici…"
            };
            textBox.TextChanged += NoteTextBox_TextChanged;

            cardLayout.Children.Add(noteHeader);
            Grid.SetRow(noteHeader, 0);
            cardLayout.Children.Add(textBox);
            Grid.SetRow(textBox, 1);
            card.Child = cardLayout;
            NotesPanel.Children.Add(card);
        }
    }

    private void NoteTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (sender is System.Windows.Controls.TextBox textBox && textBox.Tag is NoteItem note)
        {
            note.Text = textBox.Text;
            ScheduleNotesSave();
        }
    }

    private void AddNoteButton_Click(object sender, RoutedEventArgs e)
    {
        if (_settings.Notes.Count >= 20)
        {
            StatusText.Text = "Tu peux avoir jusqu'à 20 cases dans le pense-bête.";
            return;
        }

        _settings.Notes.Add(new NoteItem());
        BuildNotesPanel();
        ScheduleNotesSave();
    }

    private void RemoveNoteButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.Button { Tag: NoteItem note })
        {
            _settings.Notes.Remove(note);
            BuildNotesPanel();
            ScheduleNotesSave();
        }
    }

    private void ScheduleNotesSave()
    {
        _notesSaveTimer.Stop();
        _notesSaveTimer.Start();
    }

    private async void NotesSaveTimer_Tick(object? sender, EventArgs e)
    {
        _notesSaveTimer.Stop();
        try
        {
            await _settingsStore.SaveAsync(_settings, _lifetime.Token);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
            // Normal shutdown.
        }
        catch (Exception ex)
        {
            StatusText.Text = FriendlyError(ex);
        }
    }

    private async Task RefreshAsync(bool firstRefresh)
    {
        if (!await _refreshGate.WaitAsync(0))
        {
            return;
        }

        try
        {
            RefreshButton.IsEnabled = false;
            MarkCheckedButton.IsEnabled = false;
            StatusText.Text = "Vérification de Gmail…";

            if (!_settings.LastCheckedUtc.HasValue)
            {
                // Authorize once on first run, then establish the baseline.
                // Existing messages are intentionally not counted as new.
                var baseline = DateTimeOffset.UtcNow;
                await _gmailService.CountIncomingSinceAsync(baseline.AddSeconds(-1), _lifetime.Token);
                _settings.LastCheckedUtc = baseline;
                await _settingsStore.SaveAsync(_settings, _lifetime.Token);
                _lastNotifiedCount = 0;
                RenderCount(0);
                await RenderRecentMailsAsync();
                StatusText.Text = "Surveillance active.";
                _hasRenderedCount = true;
            }
            else
            {
                var count = await _gmailService.CountIncomingSinceAsync(
                    _settings.LastCheckedUtc.Value,
                    _lifetime.Token);

                RenderCount(count);
                await RenderRecentMailsAsync();
                StatusText.Text = count switch
                {
                    0 => "Aucun nouveau message depuis ton dernier check.",
                    1 => "Un nouveau message attend ta lecture.",
                    _ => $"{count} nouveaux messages attendent ta lecture."
                };

                if (count > _lastNotifiedCount && (firstRefresh || _hasRenderedCount))
                {
                    var message = count == 1
                        ? "Tu as reçu 1 nouveau mail."
                        : $"Tu as reçu {count} nouveaux mails.";
                    _trayIcon.ShowBalloonTip(
                        5000,
                        "Nouveaux mails Gmail",
                        message,
                        System.Windows.Forms.ToolTipIcon.Info);
                }

                _lastNotifiedCount = count;
                _hasRenderedCount = true;
            }

            LastCheckedText.Text = FormatLastChecked(_settings.LastCheckedUtc);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
            // Normal shutdown.
        }
        catch (FileNotFoundException)
        {
            StatusText.Text = "Ajoute credentials.json à côté de l'application, puis actualise.";
        }
        catch (Exception ex)
        {
            StatusText.Text = FriendlyError(ex);
        }
        finally
        {
            RefreshButton.IsEnabled = true;
            MarkCheckedButton.IsEnabled = true;
            _refreshGate.Release();
        }
    }

    private async void MarkCheckedButton_Click(object sender, RoutedEventArgs e)
    {
        await _refreshGate.WaitAsync();
        try
        {
            _settings.LastCheckedUtc = DateTimeOffset.UtcNow;
            _lastNotifiedCount = 0;
            RenderCount(0);
            StatusText.Text = "C'est noté : le compteur repart de zéro.";
            await _settingsStore.SaveAsync(_settings, _lifetime.Token);
            LastCheckedText.Text = FormatLastChecked(_settings.LastCheckedUtc);
            await RenderRecentMailsAsync();
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
            // Normal shutdown.
        }
        catch (Exception ex)
        {
            StatusText.Text = FriendlyError(ex);
        }
        finally
        {
            _refreshGate.Release();
        }
    }

    private async void RefreshButton_Click(object sender, RoutedEventArgs e)
    {
        await RefreshAsync(false);
    }

    private async void StartWithWindowsCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsInitialized || _isRestoringWindow || _isInitializing)
        {
            return;
        }

        var enabled = StartWithWindowsCheckBox.IsChecked == true;
        try
        {
            StartupManager.SetEnabled(enabled);
            _settings.StartWithWindows = enabled;
            await _settingsStore.SaveAsync(_settings, _lifetime.Token);
        }
        catch (Exception ex)
        {
            StartWithWindowsCheckBox.IsChecked = !enabled;
            StatusText.Text = FriendlyError(ex);
        }
    }

    private void OpenGmailButton_Click(object sender, RoutedEventArgs e)
    {
        Process.Start(new ProcessStartInfo("https://mail.google.com")
        {
            UseShellExecute = true
        });
    }

    private void UpdateButton_Click(object sender, RoutedEventArgs e)
    {
        if (_availableUpdate is null)
        {
            return;
        }

        UpdateButton.IsEnabled = false;
        if (!_updateService.TryStartInstaller(_availableUpdate, out var error))
        {
            UpdateButton.IsEnabled = true;
            StatusText.Text = error;
            return;
        }

        _exitRequested = true;
        Close();
    }

    private void HideButton_Click(object sender, RoutedEventArgs e)
    {
        Hide();
    }

    private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left || e.Handled
            || !DesktopWindowHost.TryGetCursorPosition(out _dragStartCursor))
        {
            return;
        }

        _isDragging = true;
        _dragStartLeft = Left;
        _dragStartTop = Top;
        if (sender is UIElement element)
        {
            element.CaptureMouse();
        }
        e.Handled = true;
    }

    private void Header_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_isDragging)
        {
            return;
        }

        if (e.LeftButton != MouseButtonState.Pressed)
        {
            Header_MouseLeftButtonUp(sender, new MouseButtonEventArgs(Mouse.PrimaryDevice, e.Timestamp, MouseButton.Left));
            return;
        }

        if (DesktopWindowHost.TryGetCursorPosition(out var cursorPosition))
        {
            var dpi = VisualTreeHelper.GetDpi(this);
            Left = _dragStartLeft + (cursorPosition.X - _dragStartCursor.X) / dpi.DpiScaleX;
            Top = _dragStartTop + (cursorPosition.Y - _dragStartCursor.Y) / dpi.DpiScaleY;
        }
    }

    private void Header_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_isDragging || e.ChangedButton != MouseButton.Left)
        {
            return;
        }

        _isDragging = false;
        if (sender is UIElement element)
        {
            element.ReleaseMouseCapture();
        }

        SaveWindowGeometry();
        e.Handled = true;
    }

    private void Window_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (!_isRestoringWindow && IsLoaded)
        {
            SaveWindowGeometry();
        }
    }

    private void ResizeThumb_DragDelta(object sender, DragDeltaEventArgs e)
    {
        var newWidth = Math.Max(MinWidth, Width + e.HorizontalChange);
        var newHeight = Math.Max(MinHeight, Height + e.VerticalChange);

        Width = newWidth;
        Height = newHeight;
    }

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (!_exitRequested)
        {
            e.Cancel = true;
            Hide();
            return;
        }

        SaveWindowGeometry();
        _lifetime.Cancel();
        _timer.Stop();
        _trayIcon.Visible = false;
        _trayIcon.Dispose();
        _gmailService.Dispose();
        _lifetime.Dispose();
        _refreshGate.Dispose();
        _updateCheckGate.Dispose();
    }

    private void ExitApplication()
    {
        _exitRequested = true;
        Close();
        System.Windows.Application.Current.Shutdown();
    }

    private void ShowWidget()
    {
        Show();
        WindowState = WindowState.Normal;
        Activate();
    }

    private void RestoreOrPlaceWindow()
    {
        const double defaultWidth = 300;
        const double defaultHeight = 190;
        var needsCompactMigration = _settings.LayoutVersion < CompactLayoutVersion;
        var needsSettingsMigration = _settings.LayoutVersion < CurrentSettingsVersion;
        _isRestoringWindow = true;
        try
        {
            var workArea = SystemParameters.WorkArea;
            var hasSavedGeometry = _settings.WindowLeft.HasValue
                                   && _settings.WindowTop.HasValue
                                   && _settings.WindowWidth.HasValue
                                   && _settings.WindowHeight.HasValue;

            if (!needsCompactMigration && hasSavedGeometry && IsWithinWorkArea(
                    _settings.WindowLeft!.Value,
                    _settings.WindowTop!.Value,
                    _settings.WindowWidth!.Value,
                    _settings.WindowHeight!.Value,
                    workArea))
            {
                Left = _settings.WindowLeft.Value;
                Top = _settings.WindowTop.Value;
                Width = _settings.WindowWidth.Value;
                Height = _settings.WindowHeight.Value;
            }
            else
            {
                // Compact layout: right aligned with a small edge margin and
                // below the upper-right desktop icon area.
                const double rightMargin = 18;
                const double upperIconGap = 145;

                Width = defaultWidth;
                Height = defaultHeight;
                Left = workArea.Right - Width - rightMargin;
                Top = workArea.Top + upperIconGap;
            }

            if (needsSettingsMigration)
            {
                // Version 3 enables the requested Windows startup behavior.
                // Existing local settings remain untouched apart from this
                // one-time preference migration.
                _settings.StartWithWindows = true;
                _settings.LayoutVersion = CurrentSettingsVersion;

                if (needsCompactMigration)
                {
                    _settings.WindowLeft = Left;
                    _settings.WindowTop = Top;
                    _settings.WindowWidth = Width;
                    _settings.WindowHeight = Height;
                }
                else if (_settings.WindowHeight.HasValue && _settings.WindowHeight.Value <= 190)
                {
                    // Give the mailbox list a little more room when migrating
                    // from the old count-only compact layout. Custom sizes
                    // larger than the old default remain untouched.
                    _settings.WindowHeight = defaultHeight;
                    Height = defaultHeight;
                }
            }
        }
        finally
        {
            _isRestoringWindow = false;
        }

        _needsSettingsSave = needsSettingsMigration;
    }

    private static bool IsWithinWorkArea(
        double left,
        double top,
        double width,
        double height,
        Rect workArea)
    {
        return width >= 220
               && height >= 150
               && left + 40 >= workArea.Left
               && top + 40 >= workArea.Top
               && left <= workArea.Right - 40
               && top <= workArea.Bottom - 40;
    }

    private async void SaveWindowGeometry()
    {
        if (!IsLoaded || _isRestoringWindow)
        {
            return;
        }

        _settings.WindowLeft = Left;
        _settings.WindowTop = Top;
        _settings.WindowWidth = Width;
        _settings.WindowHeight = Height;

        try
        {
            await _settingsStore.SaveAsync(_settings, _lifetime.Token);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
            // Normal shutdown.
        }
    }

    private void RenderCount(int count)
    {
        PendingSummaryText.Text = count == 0
            ? "0 nouveau"
            : count == 1
                ? "1 nouveau"
                : $"{count.ToString(CultureInfo.InvariantCulture)} nouveaux";
        PendingSummaryText.Foreground = new SolidColorBrush(
            count == 0
                ? System.Windows.Media.Color.FromRgb(22, 131, 91)
                : System.Windows.Media.Color.FromRgb(197, 107, 22));
    }

    private async Task RenderRecentMailsAsync()
    {
        var messages = await _gmailService.GetRecentIncomingAsync(_lifetime.Token);
        MailListPanel.Children.Clear();

        if (messages.Count == 0)
        {
            MailListPanel.Children.Add(new TextBlock
            {
                Text = "Aucun message dans la boîte de réception.",
                Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(107, 114, 128)),
                FontSize = 11,
                Margin = new Thickness(4, 12, 4, 12),
                HorizontalAlignment = HorizontalAlignment.Center
            });
            return;
        }

        foreach (var message in messages)
        {
            var isNew = _settings.LastCheckedUtc.HasValue
                        && message.ReceivedAt > _settings.LastCheckedUtc.Value;
            var card = new Border
            {
                Background = new SolidColorBrush(isNew
                    ? System.Windows.Media.Color.FromArgb(68, 197, 107, 22)
                    : System.Windows.Media.Color.FromArgb(28, 255, 255, 255)),
                BorderBrush = new SolidColorBrush(isNew
                    ? System.Windows.Media.Color.FromArgb(100, 197, 107, 22)
                    : System.Windows.Media.Color.FromArgb(42, 255, 255, 255)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(8, 5, 7, 5),
                Margin = new Thickness(0, 0, 0, 4),
                Cursor = Cursors.Hand,
                ToolTip = "Ouvrir ce message dans Gmail"
            };

            var layout = new Grid();
            layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            layout.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var sender = new TextBlock
            {
                Text = message.Sender,
                FontSize = 11,
                FontWeight = message.IsUnread ? FontWeights.Bold : FontWeights.SemiBold,
                Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(226, 232, 240)),
                TextTrimming = TextTrimming.CharacterEllipsis
            };
            layout.Children.Add(sender);

            var time = new TextBlock
            {
                Text = FormatMailTime(message.ReceivedAt),
                FontSize = 9,
                Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(185, 198, 216)),
                Margin = new Thickness(8, 1, 0, 0),
                VerticalAlignment = VerticalAlignment.Top
            };
            Grid.SetColumn(time, 1);
            layout.Children.Add(time);

            var subject = new TextBlock
            {
                Text = message.Subject,
                FontSize = 10,
                FontWeight = message.IsUnread ? FontWeights.SemiBold : FontWeights.Normal,
                Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(203, 213, 225)),
                TextTrimming = TextTrimming.CharacterEllipsis,
                Margin = new Thickness(0, 2, 0, 0)
            };
            Grid.SetRow(subject, 1);
            layout.Children.Add(subject);

            card.Child = layout;
            card.MouseLeftButtonUp += (_, _) => OpenMailMessage(message.Id);
            MailListPanel.Children.Add(card);
        }
    }

    private void OpenMailMessage(string messageId)
    {
        Process.Start(new ProcessStartInfo(
            $"https://mail.google.com/mail/u/0/#all/{Uri.EscapeDataString(messageId)}")
        {
            UseShellExecute = true
        });
    }

    private static string FormatMailTime(DateTimeOffset timestamp)
    {
        var local = timestamp.ToLocalTime();
        return local.Date == DateTime.Today
            ? local.ToString("HH:mm", CultureInfo.CurrentCulture)
            : local.ToString("dd/MM", CultureInfo.CurrentCulture);
    }

    private static string FormatLastChecked(DateTimeOffset? timestamp)
    {
        return timestamp.HasValue
            ? $"Dernier check : {timestamp.Value.ToLocalTime():dd/MM/yyyy à HH:mm}"
            : "Dernier check : jamais";
    }

    private static string FriendlyError(Exception ex)
    {
        var message = ex.Message;
        if (message.Contains("access_denied", StringComparison.OrdinalIgnoreCase))
        {
            return "Google refuse l'accès. Ajoute cette adresse comme utilisateur test dans le projet OAuth, puis clique sur Actualiser.";
        }

        if (message.Contains("cancel", StringComparison.OrdinalIgnoreCase))
        {
            return "Connexion Gmail annulée. Clique sur Actualiser pour réessayer.";
        }

        if (message.Length > 180)
        {
            message = message[..177] + "…";
        }

        return $"Impossible de vérifier Gmail : {message}";
    }
}
