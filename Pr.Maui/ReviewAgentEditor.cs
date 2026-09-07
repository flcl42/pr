namespace PrDesktop;

internal sealed class ReviewAgentEditor
{
    private const string DefaultChoice = "Default";
    private const string AutoChoice = "Auto";
    private const string CustomChoice = "Custom...";
    private readonly Page _owner;
    private readonly List<string> _models = [];
    private readonly List<string> _efforts = [];
    private readonly CheckBox _checkBox;
    private readonly Label _label;
    private readonly Button _modelButton;
    private readonly Button _effortButton;
    private readonly bool _supportsEffort;
    private ReviewAgentSettings _template;
    private string? _selectedModel;
    private string? _selectedEffort;
    private bool _updating;

    public ReviewAgentEditor(Page owner, ReviewAgentSettings settings)
    {
        _owner = owner;
        _template = settings;
        _selectedModel = settings.Model;
        _selectedEffort = settings.Effort;
        _supportsEffort = ReviewEffortCatalog.SupportsEffort(settings.Agent);
        _checkBox = new CheckBox
        {
            IsChecked = settings.Enabled,
            VerticalOptions = LayoutOptions.Center,
            AutomationId = $"review-agent-{settings.AgentName}-enabled",
        };
        SemanticProperties.SetDescription(_checkBox, $"Enable {settings.AgentDisplayName}");
        _checkBox.CheckedChanged += OnCheckedChanged;
        _label = new Label
        {
            Text = settings.Agent == ReviewAgent.DeepSeek ? "Deep Code" : settings.AgentDisplayName,
            FontSize = 12,
            VerticalTextAlignment = TextAlignment.Center,
            LineBreakMode = LineBreakMode.TailTruncation,
        };
        _modelButton = new Button
        {
            Text = ChoiceFor(_selectedModel),
            FontSize = 12,
            HeightRequest = 32,
            HorizontalOptions = LayoutOptions.Fill,
            Padding = new Thickness(8, 0),
        };
        _modelButton.Clicked += OnModelClicked;
        _effortButton = new Button
        {
            FontSize = 12,
            HeightRequest = 32,
            HorizontalOptions = LayoutOptions.Fill,
            Padding = new Thickness(7, 0),
            IsEnabled = _supportsEffort,
        };
        _effortButton.Clicked += OnEffortClicked;
        ResetModels(ReviewModelCatalog.MergeModels(settings.Agent, settings.Model));
        ResetEfforts(settings);

        var grid = new Grid
        {
            ColumnDefinitions =
            {
                new ColumnDefinition(GridLength.Auto),
                new ColumnDefinition(new GridLength(72)),
                new ColumnDefinition(GridLength.Star),
                new ColumnDefinition(new GridLength(72)),
            },
            ColumnSpacing = 5,
            MinimumHeightRequest = 36,
        };
        grid.Children.Add(_checkBox);
        Grid.SetColumn(_label, 1);
        grid.Children.Add(_label);
        Grid.SetColumn(_modelButton, 2);
        grid.Children.Add(_modelButton);
        Grid.SetColumn(_effortButton, 3);
        grid.Children.Add(_effortButton);
        View = grid;
        UpdateTooltips();
    }

    public event EventHandler? Changed;

    public View View { get; }

    public ReviewAgent Agent => _template.Agent;

    public ReviewAgentSettings Settings => _template with
    {
        Enabled = _checkBox.IsChecked,
        Model = _selectedModel,
        Effort = _supportsEffort ? _selectedEffort : null,
    };

    public bool IsSelected => _checkBox.IsChecked;

    public bool IsInteractive
    {
        set => View.IsEnabled = value;
    }

    public void Update(ReviewAgentSettings settings)
    {
        _updating = true;
        try
        {
            _template = settings;
            _selectedModel = settings.Model;
            _selectedEffort = settings.Effort;
            _checkBox.IsChecked = settings.Enabled;
            EnsureModelChoice(settings.Model);
            EnsureEffortChoice(settings.Effort);
            UpdateModelButton();
            UpdateEffortButton();
            UpdateTooltips();
        }
        finally
        {
            _updating = false;
        }
    }

    public async Task LoadModelsAsync()
    {
        var models = await ReviewModelCatalog.GetModelsAsync(_template);
        ResetModels(models);
    }

    private void ResetModels(IEnumerable<string> models)
    {
        _updating = true;
        try
        {
            _models.Clear();
            _models.Add(DefaultChoice);
            foreach (var model in models)
            {
                if (!string.IsNullOrWhiteSpace(model)
                    && !_models.Contains(model, StringComparer.OrdinalIgnoreCase))
                {
                    _models.Add(model);
                }
            }

            EnsureModelChoice(_selectedModel);
            _models.Add(CustomChoice);
            UpdateModelButton();
        }
        finally
        {
            _updating = false;
        }
    }

    private void EnsureModelChoice(string? model)
    {
        if (string.IsNullOrWhiteSpace(model)
            || _models.Contains(model, StringComparer.OrdinalIgnoreCase))
        {
            return;
        }

        var customIndex = _models.IndexOf(CustomChoice);
        if (customIndex >= 0)
        {
            _models.Insert(customIndex, model);
        }
        else
        {
            _models.Add(model);
        }
    }

    private string ChoiceFor(string? model)
        => string.IsNullOrWhiteSpace(model) ? DefaultChoice : model;

    private void ResetEfforts(ReviewAgentSettings settings)
    {
        _efforts.Clear();
        if (!_supportsEffort)
        {
            UpdateEffortButton();
            return;
        }

        _efforts.Add(DefaultChoice);
        _efforts.Add(AutoChoice);
        foreach (var effort in ReviewEffortCatalog.For(settings.Agent))
        {
            if (!_efforts.Contains(effort, StringComparer.OrdinalIgnoreCase))
            {
                _efforts.Add(effort);
            }
        }

        EnsureEffortChoice(settings.Effort);
        _efforts.Add(CustomChoice);
        UpdateEffortButton();
    }

    private void EnsureEffortChoice(string? effort)
    {
        if (!_supportsEffort
            || string.IsNullOrWhiteSpace(effort)
            || _efforts.Contains(effort, StringComparer.OrdinalIgnoreCase))
        {
            return;
        }

        var customIndex = _efforts.IndexOf(CustomChoice);
        if (customIndex >= 0)
        {
            _efforts.Insert(customIndex, effort);
        }
        else
        {
            _efforts.Add(effort);
        }
    }

    private void OnCheckedChanged(object? sender, CheckedChangedEventArgs args)
    {
        if (!_updating)
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    private async void OnModelClicked(object? sender, EventArgs args)
    {
        if (_updating)
        {
            return;
        }

        var selected = await _owner.DisplayActionSheetAsync(
            $"{_template.AgentDisplayName} model",
            "Cancel",
            null,
            [.. _models]);
        if (string.IsNullOrWhiteSpace(selected))
        {
            return;
        }

        if (string.Equals(selected, CustomChoice, StringComparison.Ordinal))
        {
            var custom = await _owner.DisplayPromptAsync(
                "Review model",
                $"Model identifier for {_template.AgentDisplayName}",
                "Use",
                "Cancel",
                initialValue: _selectedModel);
            if (string.IsNullOrWhiteSpace(custom))
            {
                return;
            }

            _selectedModel = custom.Trim();
            EnsureModelChoice(_selectedModel);
        }
        else
        {
            _selectedModel = string.Equals(selected, DefaultChoice, StringComparison.Ordinal)
                ? ReviewAgentSettings.DefaultFor(_template.Agent).Model
                : selected;
        }

        UpdateModelButton();
        UpdateTooltips();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private async void OnEffortClicked(object? sender, EventArgs args)
    {
        if (_updating || !_supportsEffort)
        {
            return;
        }

        var selected = await _owner.DisplayActionSheetAsync(
            $"{_template.AgentDisplayName} effort",
            "Cancel",
            null,
            [.. _efforts]);
        if (string.IsNullOrWhiteSpace(selected))
        {
            return;
        }

        if (string.Equals(selected, CustomChoice, StringComparison.Ordinal))
        {
            var custom = await _owner.DisplayPromptAsync(
                "Review effort",
                $"Effort or variant for {_template.AgentDisplayName}",
                "Use",
                "Cancel",
                initialValue: _selectedEffort);
            if (string.IsNullOrWhiteSpace(custom))
            {
                return;
            }

            _selectedEffort = custom.Trim();
            EnsureEffortChoice(_selectedEffort);
        }
        else if (string.Equals(selected, AutoChoice, StringComparison.Ordinal))
        {
            _selectedEffort = null;
        }
        else
        {
            _selectedEffort = string.Equals(selected, DefaultChoice, StringComparison.Ordinal)
                ? ReviewAgentSettings.DefaultFor(_template.Agent).Effort
                : selected;
        }

        UpdateEffortButton();
        UpdateTooltips();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void UpdateTooltips()
    {
        var descriptor = Settings.Descriptor;
        ToolTipProperties.SetText(_label, descriptor);
        ToolTipProperties.SetText(_modelButton, descriptor);
        ToolTipProperties.SetText(
            _effortButton,
            _supportsEffort
                ? $"{_template.AgentDisplayName} review effort: {_selectedEffort ?? "automatic"}"
                : $"{_template.AgentDisplayName} does not expose an effort option");
    }

    private void UpdateModelButton()
    {
        _modelButton.Text = ChoiceFor(_selectedModel);
    }

    private void UpdateEffortButton()
    {
        _effortButton.Text = _supportsEffort
            ? _selectedEffort ?? "Auto"
            : "N/A";
    }
}
