using System.Text.RegularExpressions;
using LegendScenarioAnalyzer;

namespace AIRedirector;

internal sealed class LegendAiOutputBuffer : ILegendAiOutputBridge
{
    static readonly Regex AnsiEscapePattern = new(@"\x1B\[[0-?]*[ -/]*[@-~]", RegexOptions.Compiled);
    static readonly Regex TrainingScorePattern = new(
        @"^(?<train>[速耐力根智])\s*[:：]\s*(?<value>.+)$",
        RegexOptions.Compiled);
    static readonly Regex ActionScorePattern = new(
        @"^(?<action>休息|外出)\s*[:：]\s*(?<value>.+)$",
        RegexOptions.Compiled);
    static readonly Regex SelectionScorePattern = new(
        @"^选(?<color>蓝色|绿色|红色)第\s*(?<ordinal>\d+)\s*个\s*[:：]\s*(?<value>.+)$",
        RegexOptions.Compiled);
    static readonly Regex RecommendationSelectionPattern = new(
        @"^选(?<color>蓝色|绿色|红色)第\s*(?<ordinal>\d+)\s*个\b",
        RegexOptions.Compiled);
    static readonly HashSet<string> ImportantRecommendationActions = new(StringComparer.Ordinal)
    {
        "休息",
        "外出",
        "比赛",
        "普通外出",
        "团队外出1",
        "团队外出2",
        "团队外出3",
        "团队外出4选蓝",
        "团队外出4选绿",
        "团队外出4选红",
        "团队外出5",
        "选红",
        "选绿",
        "选蓝",
        "不选"
    };

    readonly object gate = new();
    readonly IDisposable modifierRegistration;
    readonly Dictionary<LegendTrain, string> trainingScores = [];
    readonly Dictionary<string, string> actionScores = new(StringComparer.Ordinal);
    readonly Dictionary<LegendSelectionKey, string> selectionScores = [];
    readonly List<string> summaries = [];
    string? recommendation;
    LegendTrain? recommendedTrain;
    LegendSelectionKey? recommendedSelection;

    public LegendAiOutputBuffer()
    {
        modifierRegistration = LegendTrainingDisplay.RegisterModifier(ApplyDisplay);
    }

    public bool ProcessLine(string? rawLine)
    {
        if (string.IsNullOrWhiteSpace(rawLine))
            return false;

        var line = StripAnsi(rawLine).Trim();
        if (line.Length == 0)
            return false;

        lock (gate)
        {
            if (IsTurnSeparator(line))
            {
                Clear();
                return true;
            }

            if (TryParseTrainingScore(line, out var train, out var value))
            {
                trainingScores[train] = value;
                return true;
            }

            if (TryParseSelectionScore(line, out var selectionKey, out var selectionValue))
            {
                selectionScores[selectionKey] = selectionValue;
                return true;
            }

            if (IsSelectionHeading(line))
                return true;

            if (TryParseActionScore(line, out var action, out var actionValue))
            {
                actionScores[action] = actionValue;
                return true;
            }

            if (TryParseRecommendation(
                    line,
                    out var recommendationLine,
                    out var recommendationTrain,
                    out var recommendationSelection))
            {
                recommendation = recommendationLine;
                recommendedTrain = recommendationTrain;
                recommendedSelection = recommendationSelection;
                return true;
            }

            if (IsSummaryLine(line))
            {
                AddSummary(line);
                return true;
            }

            return false;
        }
    }

    public bool RefreshCurrentDisplay(CancellationToken cancellationToken)
        => LegendTrainingDisplay.RefreshCurrent(switchToWorkspace: false, cancellationToken);

    public bool ApplyCurrentDisplay(CancellationToken cancellationToken)
        => RefreshCurrentDisplay(cancellationToken);

    public void Dispose() => modifierRegistration.Dispose();

    void ApplyDisplay(LegendTrainingDisplayContext context, LegendTrainingDisplayEditor display)
    {
        lock (gate)
        {
            if (context.ResponseData.Stage == LegendScenarioStage.BuffSelection
                && context.DataSet.obtainable_buff_id_array is { Length: > 0 })
            {
                ApplyBuffSelectionDisplay(display);
            }
            else
            {
                ApplyTrainingDisplay(context, display);
            }

            foreach (var summary in summaries)
                display.Extra.AddText(summary);
        }
    }

    void ApplyTrainingDisplay(LegendTrainingDisplayContext context, LegendTrainingDisplayEditor display)
    {
        var isTraining = context.ResponseData.Stage == LegendScenarioStage.Training;
        foreach (var (train, score) in trainingScores.OrderBy(x => (int)x.Key))
        {
            if (isTraining)
            {
                display.Training.Modify(train, card => card.AddText($"AI评分: {score}"));
            }
            else
            {
                display.Extra.AddText($"{TrainLabel(train)} AI评分: {score}");
            }
        }

        foreach (var action in new[] { "休息", "外出" })
        {
            if (actionScores.TryGetValue(action, out var score))
                display.Extra.AddText($"{action}: {score}");
        }

        if (recommendation is { } recommendationLine)
        {
            if (isTraining && recommendedTrain is { } train)
            {
                display.Training.Modify(train, card =>
                {
                    card.Highlight();
                    card.AddText(recommendationLine);
                });
            }
            else
            {
                AddRecommendationFallback(display, recommendationLine);
            }
        }
    }

    void ApplyBuffSelectionDisplay(LegendTrainingDisplayEditor display)
    {
        foreach (var (selection, score) in selectionScores.OrderBy(x => x.Key.Color).ThenBy(x => x.Key.OrdinalWithinColor))
        {
            TryModifySelection(display, selection, card => card.AddText($"AI评分: {score}"));
        }

        if (recommendation is { } recommendationLine)
        {
            if (recommendedSelection is { } selection
                && TryModifySelection(display, selection, card =>
                {
                    card.Highlight();
                    card.AddText(recommendationLine);
                }))
            {
                return;
            }

            AddRecommendationFallback(display, recommendationLine);
        }
    }

    void Clear()
    {
        trainingScores.Clear();
        actionScores.Clear();
        selectionScores.Clear();
        summaries.Clear();
        recommendation = null;
        recommendedTrain = null;
        recommendedSelection = null;
    }

    static string StripAnsi(string text)
        => AnsiEscapePattern.Replace(text, string.Empty);

    static bool IsTurnSeparator(string line)
        => line.Length >= 20 && line.All(ch => ch == '-' || char.IsWhiteSpace(ch));

    static bool TryParseTrainingScore(
        string line,
        out LegendTrain train,
        out string value)
    {
        train = default;
        value = string.Empty;

        var match = TrainingScorePattern.Match(line);
        if (!match.Success || !TryParseTrain(match.Groups["train"].Value[0], out train))
            return false;

        value = match.Groups["value"].Value.Trim();
        return value.Length != 0;
    }

    static bool TryParseSelectionScore(
        string line,
        out LegendSelectionKey selection,
        out string value)
    {
        selection = default;
        value = string.Empty;

        var match = SelectionScorePattern.Match(line);
        if (!match.Success
            || !TryParseBuffColor(match.Groups["color"].Value, out var color)
            || !int.TryParse(match.Groups["ordinal"].Value, out var ordinalWithinColor))
        {
            return false;
        }

        value = match.Groups["value"].Value.Trim();
        if (ordinalWithinColor <= 0 || value.Length == 0)
            return false;

        selection = new(color, ordinalWithinColor);
        return true;
    }

    static bool TryParseActionScore(
        string line,
        out string action,
        out string value)
    {
        action = string.Empty;
        value = string.Empty;

        var match = ActionScorePattern.Match(line);
        if (!match.Success)
            return false;

        action = match.Groups["action"].Value;
        value = match.Groups["value"].Value.Trim();
        return value.Length != 0;
    }

    static bool TryParseRecommendation(
        string line,
        out string recommendationLine,
        out LegendTrain? train,
        out LegendSelectionKey? selection)
    {
        const string prefix = "AI建议";
        if (!line.StartsWith(prefix, StringComparison.Ordinal))
        {
            recommendationLine = string.Empty;
            train = null;
            selection = null;
            return false;
        }

        var separatorIndex = line.IndexOfAny([':', '：']);
        if (separatorIndex < 0 || separatorIndex == line.Length - 1)
        {
            recommendationLine = string.Empty;
            train = null;
            selection = null;
            return false;
        }

        var body = line[(separatorIndex + 1)..].Trim();
        if (body.Length == 0)
        {
            recommendationLine = string.Empty;
            train = null;
            selection = null;
            return false;
        }

        recommendationLine = $"AI建议：{body}";
        train = TryParseRecommendationTrain(body);
        selection = TryParseRecommendationSelection(body);
        return true;
    }

    static LegendTrain? TryParseRecommendationTrain(string body)
    {
        foreach (var ch in body)
        {
            if (char.IsWhiteSpace(ch))
                continue;

            return TryParseTrain(ch, out var train) ? train : null;
        }

        return null;
    }

    static LegendSelectionKey? TryParseRecommendationSelection(string body)
    {
        var match = RecommendationSelectionPattern.Match(body);
        if (!match.Success
            || !TryParseBuffColor(match.Groups["color"].Value, out var color)
            || !int.TryParse(match.Groups["ordinal"].Value, out var ordinalWithinColor)
            || ordinalWithinColor <= 0)
        {
            return null;
        }

        return new(color, ordinalWithinColor);
    }

    static bool TryParseTrain(char value, out LegendTrain train)
    {
        train = value switch
        {
            '速' => LegendTrain.Speed,
            '耐' => LegendTrain.Stamina,
            '力' => LegendTrain.Power,
            '根' => LegendTrain.Guts,
            '智' => LegendTrain.Wiz,
            _ => default
        };
        return train != default;
    }

    static bool TryParseBuffColor(string value, out LegendBuffColor color)
    {
        color = value switch
        {
            "蓝色" => LegendBuffColor.Blue,
            "绿色" => LegendBuffColor.Green,
            "红色" => LegendBuffColor.Red,
            _ => default
        };
        return value is "蓝色" or "绿色" or "红色";
    }

    static string TrainLabel(LegendTrain train)
        => train switch
        {
            LegendTrain.Speed => "速",
            LegendTrain.Stamina => "耐",
            LegendTrain.Power => "力",
            LegendTrain.Guts => "根",
            LegendTrain.Wiz => "智",
            _ => train.ToString()
        };

    static bool IsSelectionHeading(string line)
        => line.StartsWith("选择心得中", StringComparison.Ordinal);

    static bool IsSummaryLine(string line)
        => line.Contains("运气指标", StringComparison.Ordinal)
            || line.Contains("评分预测", StringComparison.Ordinal)
            || line.Contains("比赛亏损", StringComparison.Ordinal);

    static void AddRecommendationFallback(
        LegendTrainingDisplayEditor display,
        string recommendationLine)
    {
        if (IsImportantRecommendation(recommendationLine))
            display.Important.AddText(recommendationLine);
        else
            display.Extra.AddText(recommendationLine);
    }

    static bool IsImportantRecommendation(string recommendationLine)
    {
        const string prefix = "AI建议：";
        var body = recommendationLine.StartsWith(prefix, StringComparison.Ordinal)
            ? recommendationLine[prefix.Length..].TrimStart()
            : recommendationLine.TrimStart();

        return ImportantRecommendationActions.Contains(body);
    }

    void AddSummary(string line)
    {
        if (summaries.Count == 0 || summaries[^1] != line)
            summaries.Add(line);
    }

    static bool TryModifySelection(
        LegendTrainingDisplayEditor display,
        LegendSelectionKey selection,
        Action<LegendSelectionCardEditor> modifier)
    {
        try
        {
            display.Selection.Modify(selection.Color, selection.OrdinalWithinColor, modifier);
            return true;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    readonly record struct LegendSelectionKey(LegendBuffColor Color, int OrdinalWithinColor);
}
