using MailClient.Models;
using MailClient.Services;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.UI;

namespace MailClient;

public sealed partial class MainWindow
{
    private enum CalView { Month, Week, WorkWeek, ThreeDay, Day }

    private const double HourHeight = 44;
    private const int DayStartHour = 6;
    private const int DayEndHour = 22;
    private static readonly int[] DurationMinutes = { 15, 30, 60, 120, 240 };

    private bool _calendarMode;
    private CalView _calView = CalView.Week;
    private DateTime _calAnchor = DateTime.Today;
    private DateTime _calSelected = DateTime.Today;
    private string? _calEditingId;

    private void InitCalendar()
    {
        _calView = Enum.TryParse(AppSettings.Current.CalendarViewMode, out CalView v) ? v : CalView.Week;
        CalViewTabs.SelectedItem = CalViewTabs.Items[(int)_calView];
        ResetCalEditor(_calSelected);
    }

    private void CalendarMode_Click(object sender, RoutedEventArgs e) => SetCalendarMode(!_calendarMode);

    private void SetCalendarMode(bool on)
    {
        _calendarMode = on;

        MailListPane.Visibility = on ? Visibility.Collapsed : Visibility.Visible;
        ReadingPane.Visibility = on ? Visibility.Collapsed : Visibility.Visible;
        CalendarEditor.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        CalendarGridPane.Visibility = on ? Visibility.Visible : Visibility.Collapsed;

        if (on)
        {
            RenderCalendarGrid();
            RefreshCalAgenda();
        }
    }

    // ----- navigation / view -----

    private static DateTime MondayOf(DateTime d) => d.Date.AddDays(-(((int)d.DayOfWeek + 6) % 7));

    private (DateTime Start, int Days, bool Month) Period()
    {
        return _calView switch
        {
            CalView.Month => (MondayOf(new DateTime(_calAnchor.Year, _calAnchor.Month, 1)), 42, true),
            CalView.Week => (MondayOf(_calAnchor), 7, false),
            CalView.WorkWeek => (MondayOf(_calAnchor), 5, false),
            CalView.ThreeDay => (_calAnchor.Date, 3, false),
            _ => (_calAnchor.Date, 1, false),
        };
    }

    private void CalPrev_Click(object sender, RoutedEventArgs e) => ShiftCalendar(-1);
    private void CalNext_Click(object sender, RoutedEventArgs e) => ShiftCalendar(1);

    private void CalToday_Click(object sender, RoutedEventArgs e)
    {
        _calAnchor = DateTime.Today;
        _calSelected = DateTime.Today;
        CalDatePicker.Date = _calSelected;
        RenderCalendarGrid();
        RefreshCalAgenda();
    }

    private void ShiftCalendar(int direction)
    {
        _calAnchor = _calView switch
        {
            CalView.Month => _calAnchor.AddMonths(direction),
            CalView.Week or CalView.WorkWeek => _calAnchor.AddDays(7 * direction),
            CalView.ThreeDay => _calAnchor.AddDays(3 * direction),
            _ => _calAnchor.AddDays(direction),
        };
        RenderCalendarGrid();
    }

    private void CalView_SelectionChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
    {
        var index = sender.Items.IndexOf(sender.SelectedItem);
        if (index < 0)
        {
            return;
        }

        _calView = (CalView)index;
        AppSettings.Update(s => s.CalendarViewMode = _calView.ToString());
        RenderCalendarGrid();
    }

    // ----- grid rendering -----

    private void RenderCalendarGrid()
    {
        if (!_calendarMode)
        {
            return;
        }

        var (start, days, month) = Period();
        CalRangeLabel.Text = month
            ? _calAnchor.ToString("MMMM yyyy")
            : days == 1
                ? start.ToString("dddd d MMMM yyyy")
                : $"{start:d MMM} – {start.AddDays(days - 1):d MMM yyyy}";

        CalBody.Children.Clear();
        CalBody.RowDefinitions.Clear();
        CalBody.ColumnDefinitions.Clear();

        if (month)
        {
            BuildMonth(start);
        }
        else
        {
            BuildTimeGrid(start, days);
        }
    }

    private Brush AccentBrush => (Brush)Application.Current.Resources["AccentFillColorDefaultBrush"];
    private Brush CardBrush => (Brush)Application.Current.Resources["CardBackgroundFillColorDefaultBrush"];
    private Brush DividerBrush => (Brush)Application.Current.Resources["DividerStrokeColorDefaultBrush"];

    private void BuildMonth(DateTime start)
    {
        for (var c = 0; c < 7; c++)
        {
            CalBody.ColumnDefinitions.Add(new ColumnDefinition());
        }

        CalBody.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        for (var r = 0; r < 6; r++)
        {
            CalBody.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        }

        for (var c = 0; c < 7; c++)
        {
            var header = new TextBlock
            {
                Text = start.AddDays(c).ToString("ddd"),
                FontSize = 11,
                Opacity = 0.6,
                Margin = new Thickness(6, 2, 0, 2),
            };
            Grid.SetColumn(header, c);
            Grid.SetRow(header, 0);
            CalBody.Children.Add(header);
        }

        var today = DateTime.Today;
        for (var i = 0; i < 42; i++)
        {
            var day = start.AddDays(i);
            var inMonth = day.Month == _calAnchor.Month;

            var panel = new StackPanel { Spacing = 2 };
            var dayNumber = new TextBlock
            {
                Text = day.Day.ToString(),
                FontSize = 12,
                FontWeight = day.Date == today ? Microsoft.UI.Text.FontWeights.Bold : Microsoft.UI.Text.FontWeights.Normal,
                Opacity = inMonth ? 1 : 0.35,
            };
            if (day.Date == today)
            {
                dayNumber.Foreground = AccentBrush;
            }

            panel.Children.Add(dayNumber);

            var dayEvents = CalendarStore.Between(day, day.AddDays(1));
            foreach (var ev in dayEvents.Take(3))
            {
                panel.Children.Add(EventChip(ev, compact: true));
            }

            if (dayEvents.Count > 3)
            {
                panel.Children.Add(new TextBlock { Text = $"+{dayEvents.Count - 3} more", FontSize = 10, Opacity = 0.6 });
            }

            var cell = new Border
            {
                BorderBrush = DividerBrush,
                BorderThickness = new Thickness(0.5),
                Background = day.Date == _calSelected ? new SolidColorBrush(Color.FromArgb(24, 128, 128, 128)) : null,
                Padding = new Thickness(4),
                Child = panel,
                Tag = day,
            };
            cell.Tapped += CalDayCell_Tapped;

            Grid.SetColumn(cell, i % 7);
            Grid.SetRow(cell, 1 + i / 7);
            CalBody.Children.Add(cell);
        }
    }

    private void BuildTimeGrid(DateTime start, int days)
    {
        CalBody.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(52) });
        for (var c = 0; c < days; c++)
        {
            CalBody.ColumnDefinitions.Add(new ColumnDefinition());
        }

        CalBody.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // day headers
        CalBody.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // all-day
        CalBody.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // hours

        var today = DateTime.Today;
        var totalHeight = (DayEndHour - DayStartHour) * HourHeight;

        // hour labels
        var labels = new Canvas { Height = totalHeight, Width = 52 };
        for (var h = DayStartHour; h <= DayEndHour; h++)
        {
            var t = new TextBlock { Text = new DateTime(1, 1, 1, h, 0, 0).ToString("h tt"), FontSize = 10, Opacity = 0.6 };
            Canvas.SetTop(t, (h - DayStartHour) * HourHeight - 6);
            Canvas.SetLeft(t, 4);
            labels.Children.Add(t);
        }

        Grid.SetRow(labels, 2);
        Grid.SetColumn(labels, 0);
        CalBody.Children.Add(labels);

        for (var c = 0; c < days; c++)
        {
            var day = start.AddDays(c);

            var headerText = new TextBlock
            {
                Text = day.ToString("ddd d"),
                FontWeight = day.Date == today ? Microsoft.UI.Text.FontWeights.Bold : Microsoft.UI.Text.FontWeights.SemiBold,
                FontSize = 12,
            };
            if (day.Date == today)
            {
                headerText.Foreground = AccentBrush;
            }

            var header = new Button
            {
                Content = headerText,
                Background = new SolidColorBrush(Colors.Transparent),
                BorderThickness = new Thickness(0),
                Padding = new Thickness(4, 2, 4, 4),
                HorizontalAlignment = HorizontalAlignment.Stretch,
                Tag = day,
            };
            header.Click += CalDayHeader_Click;
            Grid.SetRow(header, 0);
            Grid.SetColumn(header, c + 1);
            CalBody.Children.Add(header);

            // all-day chips
            var allDay = new StackPanel { Spacing = 2, Margin = new Thickness(2) };
            foreach (var ev in CalendarStore.Between(day, day.AddDays(1)).Where(e => e.AllDay))
            {
                allDay.Children.Add(EventChip(ev, compact: true));
            }

            Grid.SetRow(allDay, 1);
            Grid.SetColumn(allDay, c + 1);
            CalBody.Children.Add(allDay);

            // hour cells + events
            var column = new Grid { Height = totalHeight };
            var hourStack = new StackPanel();
            for (var h = DayStartHour; h < DayEndHour; h++)
            {
                var slot = new Border
                {
                    Height = HourHeight,
                    BorderBrush = DividerBrush,
                    BorderThickness = new Thickness(c == 0 ? 0.5 : 0, 0.5, 0.5, 0),
                    Tag = day.AddHours(h),
                };
                slot.Tapped += CalSlot_Tapped;
                hourStack.Children.Add(slot);
            }

            column.Children.Add(hourStack);

            var overlay = new Grid();
            foreach (var ev in CalendarStore.Between(day, day.AddDays(1)).Where(e => !e.AllDay))
            {
                var startMin = Math.Max(0, (ev.StartLocal - day.Date.AddHours(DayStartHour)).TotalMinutes);
                var endMin = Math.Min((DayEndHour - DayStartHour) * 60, (ev.EndLocal - day.Date.AddHours(DayStartHour)).TotalMinutes);
                var chip = EventChip(ev, compact: false);
                chip.VerticalAlignment = VerticalAlignment.Top;
                chip.HorizontalAlignment = HorizontalAlignment.Stretch;
                chip.Height = Math.Max(16, (endMin - startMin) / 60.0 * HourHeight - 2);
                chip.Margin = new Thickness(1, startMin / 60.0 * HourHeight, 2, 0);
                overlay.Children.Add(chip);
            }

            column.Children.Add(overlay);
            Grid.SetRow(column, 2);
            Grid.SetColumn(column, c + 1);
            CalBody.Children.Add(column);
        }
    }

    private Border EventChip(CalendarEvent ev, bool compact)
    {
        var text = new TextBlock
        {
            Text = compact ? ev.Title : $"{ev.Title}\n{ev.TimeRangeDisplay}",
            FontSize = compact ? 10 : 11,
            Foreground = new SolidColorBrush(Colors.White),
            TextTrimming = TextTrimming.CharacterEllipsis,
            TextWrapping = compact ? TextWrapping.NoWrap : TextWrapping.Wrap,
            MaxLines = compact ? 1 : 3,
        };

        if (ev.Done)
        {
            text.TextDecorations = Windows.UI.Text.TextDecorations.Strikethrough;
        }

        var chip = new Border
        {
            Background = AccentBrush,
            Opacity = ev.Done ? 0.5 : 1,
            CornerRadius = new CornerRadius(3),
            Padding = new Thickness(4, 1, 4, 1),
            Margin = new Thickness(0, 0, 1, 1),
            Child = text,
            Tag = ev.Id,
        };
        chip.Tapped += CalEventChip_Tapped;
        return chip;
    }

    // ----- interaction -----

    private void CalDayCell_Tapped(object sender, TappedRoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: DateTime day })
        {
            _calSelected = day;
            CalDatePicker.Date = day;
            RenderCalendarGrid();
            RefreshCalAgenda();
        }
    }

    private void CalDayHeader_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: DateTime day })
        {
            _calSelected = day;
            _calAnchor = day;
            _calView = CalView.Day;
            CalViewTabs.SelectedItem = CalViewTabs.Items[(int)CalView.Day];
            AppSettings.Update(s => s.CalendarViewMode = "Day");
            CalDatePicker.Date = day;
            RenderCalendarGrid();
            RefreshCalAgenda();
        }
    }

    private void CalSlot_Tapped(object sender, TappedRoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: DateTime slot })
        {
            _calSelected = slot.Date;
            ResetCalEditor(slot.Date);
            CalStartTime.Time = slot.TimeOfDay;
            CalTitleBox.Focus(FocusState.Programmatic);
        }
    }

    private void CalEventChip_Tapped(object sender, TappedRoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string id } && CalendarStore.Find(id) is { } ev)
        {
            e.Handled = true;
            LoadCalEditor(ev);
        }
    }

    private void CalAgendaItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string id } && CalendarStore.Find(id) is { } ev)
        {
            LoadCalEditor(ev);
        }
    }

    // ----- editor -----

    private void ResetCalEditor(DateTime date)
    {
        _calEditingId = null;
        CalEditorHeading.Text = "New event";
        CalTitleBox.Text = string.Empty;
        CalNotesBox.Text = string.Empty;
        CalAllDayBox.IsChecked = false;
        CalDatePicker.Date = date;
        CalStartTime.Time = new TimeSpan(9, 0, 0);
        CalDurationBox.SelectedIndex = 2;
        CalDeleteButton.Visibility = Visibility.Collapsed;
        CalStartTime.IsEnabled = CalDurationBox.IsEnabled = true;
    }

    private void LoadCalEditor(CalendarEvent ev)
    {
        _calEditingId = ev.Id;
        CalEditorHeading.Text = "Edit event";
        CalTitleBox.Text = ev.Title;
        CalNotesBox.Text = ev.Notes;
        CalAllDayBox.IsChecked = ev.AllDay;
        CalDatePicker.Date = ev.StartLocal.Date;
        CalStartTime.Time = ev.StartLocal.TimeOfDay;
        var durIndex = Array.IndexOf(DurationMinutes, ev.DurationMinutes);
        CalDurationBox.SelectedIndex = durIndex >= 0 ? durIndex : 2;
        CalDeleteButton.Visibility = Visibility.Visible;
        CalStartTime.IsEnabled = CalDurationBox.IsEnabled = !ev.AllDay;
    }

    private void CalAllDay_Changed(object sender, RoutedEventArgs e)
    {
        var allDay = CalAllDayBox.IsChecked == true;
        CalStartTime.IsEnabled = CalDurationBox.IsEnabled = !allDay;
    }

    private void CalNew_Click(object sender, RoutedEventArgs e) => ResetCalEditor(_calSelected);

    private void CalSave_Click(object sender, RoutedEventArgs e)
    {
        var title = CalTitleBox.Text.Trim();
        if (title.Length == 0)
        {
            CalTitleBox.Focus(FocusState.Programmatic);
            return;
        }

        var date = (CalDatePicker.Date ?? DateTimeOffset.Now).LocalDateTime.Date;
        var allDay = CalAllDayBox.IsChecked == true;
        var start = allDay ? date : date + (CalStartTime.Time);

        var existing = _calEditingId is not null ? CalendarStore.Find(_calEditingId) : null;
        var entry = existing ?? new CalendarEvent();
        entry.Title = title;
        entry.Notes = CalNotesBox.Text.Trim();
        entry.AllDay = allDay;
        entry.Date = new DateTimeOffset(start);
        entry.DurationMinutes = allDay ? 0 : DurationMinutes[Math.Clamp(CalDurationBox.SelectedIndex, 0, DurationMinutes.Length - 1)];

        if (existing is null)
        {
            CalendarStore.Add(entry);
        }
        else
        {
            CalendarStore.Update(entry);
        }

        _calSelected = date;
        ResetCalEditor(date);
    }

    private void CalDelete_Click(object sender, RoutedEventArgs e)
    {
        if (_calEditingId is { } id)
        {
            CalendarStore.Remove(id);
            ResetCalEditor(_calSelected);
        }
    }

    private void RefreshCalAgenda()
    {
        CalAgendaHeader.Text = _calSelected.ToString("dddd d MMMM");
        CalAgendaList.ItemsSource = CalendarStore.Between(_calSelected, _calSelected.AddDays(1));
    }
}
