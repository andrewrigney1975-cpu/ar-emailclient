using MailClient.Services;
using MailClient.Services.Ai;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace MailClient.Views;

public sealed partial class AiBriefingDialog : ContentDialog
{
    private CancellationTokenSource? _cts;

    public AiBriefingDialog()
    {
        InitializeComponent();
        Loaded += (_, _) => _ = ShowTabAsync(regenerate: false);
        Closed += (_, _) => _cts?.Cancel();
    }

    private bool IsToday => Tabs.SelectedItem == TodayTab;

    private void Tabs_SelectionChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args) =>
        _ = ShowTabAsync(regenerate: false);

    private void Regen_Click(object sender, RoutedEventArgs e) => _ = ShowTabAsync(regenerate: true);

    private async Task ShowTabAsync(bool regenerate)
    {
        _cts?.Cancel();

        var today = DateTime.Today.ToString("yyyy-MM-dd");
        var settings = AppSettings.Current;
        var cached = IsToday
            ? (settings.BriefTodayDate == today ? settings.BriefTodayText : null)
            : (settings.BriefWeekDate == today ? settings.BriefWeekText : null);

        if (!regenerate && !string.IsNullOrWhiteSpace(cached))
        {
            BriefText.Text = cached;
            RegenButton.IsEnabled = Ai.Service.IsReady;
            return;
        }

        if (!Ai.Service.IsReady)
        {
            BriefText.Text = "Turn on on-device AI (the sparkle button) to generate briefings.";
            RegenButton.IsEnabled = false;
            return;
        }

        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        var wantToday = IsToday;

        Busy.IsActive = true;
        Busy.Visibility = Visibility.Visible;
        RegenButton.IsEnabled = false;
        BriefText.Text = "Working…";

        try
        {
            var items = wantToday ? BriefingBuilder.Today() : BriefingBuilder.Week();
            var prompt = wantToday ? AiPrompts.TodayBrief(items) : AiPrompts.WeeklyDigest(items);

            var builder = new System.Text.StringBuilder();
            await foreach (var piece in Ai.Service.StreamAsync(prompt, ct))
            {
                builder.Append(piece);
                BriefText.Text = builder.ToString();
            }

            var text = builder.ToString().Trim();
            if (text.Length > 0 && !ct.IsCancellationRequested)
            {
                AppSettings.Update(s =>
                {
                    if (wantToday)
                    {
                        s.BriefTodayDate = today;
                        s.BriefTodayText = text;
                    }
                    else
                    {
                        s.BriefWeekDate = today;
                        s.BriefWeekText = text;
                    }
                });
            }
        }
        catch (Exception ex)
        {
            LoggingService.Warn("AiBriefingDialog.ShowTabAsync", ex);
            BriefText.Text = "Couldn't generate: " + ex.Message;
        }
        finally
        {
            Busy.IsActive = false;
            Busy.Visibility = Visibility.Collapsed;
            RegenButton.IsEnabled = Ai.Service.IsReady;
        }
    }
}
