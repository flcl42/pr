using System.Globalization;

namespace PrDesktop;

internal static class TimelineView
{
    public static View CreateStep(JournalStep step, bool isFirst, bool isLast)
    {
        var color = UiColors.ForJournalStep(step.Status);
        var marker = new Grid
        {
            WidthRequest = 22,
            MinimumHeightRequest = 46,
        };
        var line = new BoxView
        {
            Color = UiColors.Border,
            WidthRequest = 2,
            HorizontalOptions = LayoutOptions.Center,
            VerticalOptions = LayoutOptions.Fill,
            Margin = new Thickness(0, isFirst ? 9 : 0, 0, isLast ? 25 : 0),
        };
        marker.Children.Add(line);
        marker.Children.Add(new Border
        {
            WidthRequest = 14,
            HeightRequest = 14,
            Stroke = color,
            StrokeThickness = 2,
            StrokeShape = new Microsoft.Maui.Controls.Shapes.RoundRectangle { CornerRadius = 7 },
            BackgroundColor = step.Status == JournalStepStatus.Pending ? UiColors.Surface : color,
            HorizontalOptions = LayoutOptions.Center,
            VerticalOptions = LayoutOptions.Start,
            Margin = new Thickness(0, 4, 0, 0),
            ZIndex = 1,
        });

        var header = new Grid
        {
            ColumnDefinitions =
            {
                new ColumnDefinition(GridLength.Star),
                new ColumnDefinition(GridLength.Auto),
            },
            ColumnSpacing = 8,
        };
        header.Children.Add(new Label
        {
            Text = step.Title,
            FontAttributes = FontAttributes.Bold,
            FontSize = 13,
            LineBreakMode = LineBreakMode.WordWrap,
        });
        var status = new Label
        {
            Text = StatusText(step),
            TextColor = color,
            FontAttributes = FontAttributes.Bold,
            FontSize = 11,
            HorizontalTextAlignment = TextAlignment.End,
        };
        Grid.SetColumn(status, 1);
        header.Children.Add(status);

        var content = new VerticalStackLayout
        {
            Spacing = 1,
            Padding = new Thickness(0, 0, 0, isLast ? 1 : 8),
            Children =
            {
                header,
                new Label
                {
                    Text = step.Detail ?? DefaultDetail(step.Status),
                    TextColor = step.Status == JournalStepStatus.Failed ? UiColors.Danger : UiColors.Muted,
                    FontSize = 12,
                    LineBreakMode = LineBreakMode.WordWrap,
                },
            },
        };

        var row = new Grid
        {
            ColumnDefinitions =
            {
                new ColumnDefinition(new GridLength(22)),
                new ColumnDefinition(GridLength.Star),
            },
            ColumnSpacing = 8,
        };
        row.Children.Add(marker);
        Grid.SetColumn(content, 1);
        row.Children.Add(content);
        return row;
    }

    public static JournalStep FromAgent(ReviewStageProgress stage, string runningDetail)
    {
        var model = string.IsNullOrWhiteSpace(stage.Effort)
            ? stage.Model
            : $"{stage.Model} / {stage.Effort}";
        var result = stage.Status == ReviewAgentStageStatus.Completed
            ? $"{stage.ReturnedFindings.ToString(CultureInfo.InvariantCulture)} returned, {stage.AcceptedFindings.ToString(CultureInfo.InvariantCulture)} added"
            : stage.Status is ReviewAgentStageStatus.Failed or ReviewAgentStageStatus.Canceled
                ? stage.Error
                : stage.Status == ReviewAgentStageStatus.Running
                    ? runningDetail
                    : "Waiting";
        return new JournalStep(
            $"agent:{stage.AgentName}",
            stage.DisplayName,
            string.IsNullOrWhiteSpace(result) ? model : $"{model} | {result}",
            Map(stage.Status),
            stage.StartedAt,
            stage.CompletedAt);
    }

    private static string StatusText(JournalStep step)
    {
        var status = step.Status switch
        {
            JournalStepStatus.Completed => "Completed",
            JournalStepStatus.Failed => "Failed",
            JournalStepStatus.Canceled => "Canceled",
            JournalStepStatus.Running => "Running",
            _ => "Pending",
        };
        if (step.StartedAt is null || step.CompletedAt is null)
        {
            return status;
        }

        return $"{status}  |  {FormatDuration(step.CompletedAt.Value - step.StartedAt.Value)}";
    }

    private static string DefaultDetail(JournalStepStatus status) => status switch
    {
        JournalStepStatus.Pending => "Waiting",
        JournalStepStatus.Running => "In progress",
        JournalStepStatus.Canceled => "Not reached",
        _ => string.Empty,
    };

    private static JournalStepStatus Map(ReviewAgentStageStatus status) => status switch
    {
        ReviewAgentStageStatus.Running => JournalStepStatus.Running,
        ReviewAgentStageStatus.Completed => JournalStepStatus.Completed,
        ReviewAgentStageStatus.Failed => JournalStepStatus.Failed,
        ReviewAgentStageStatus.Canceled => JournalStepStatus.Canceled,
        _ => JournalStepStatus.Pending,
    };

    internal static string FormatDuration(TimeSpan duration)
    {
        duration = duration < TimeSpan.Zero ? TimeSpan.Zero : duration;
        return duration.TotalHours >= 1
            ? $"{(int)duration.TotalHours}:{duration.Minutes:00}:{duration.Seconds:00}"
            : $"{duration.Minutes}:{duration.Seconds:00}";
    }
}
