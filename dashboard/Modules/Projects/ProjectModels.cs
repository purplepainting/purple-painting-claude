namespace PurpleDashboard.Modules.Projects;

/// <summary>One open JobTread job with the numbers the Projects list needs.</summary>
public sealed record ProjectSummary
{
    public required string Id { get; init; }
    public required string Number { get; init; }
    public required string Name { get; init; }
    public string? Customer { get; init; }
    public string? Address { get; init; }
    public string? Stage { get; init; }            // Production Stage custom field
    public string? BidStage { get; init; }         // Job Walk or Digital Takeoff bid stage
    public string? JobType { get; init; }
    public string? ProjectManager { get; init; }
    public string? SalesPerson { get; init; }
    public string? HowBidCameIn { get; init; }
    public string? FolderUrl { get; init; }
    public decimal? Contract { get; init; }        // approved customer orders (JobTread projectedPrice)
    public decimal BudgetCost { get; init; }       // cost of the job budget lines
    public decimal ActualCost { get; init; }       // time entries + approved/pending vendor bills
    public decimal Billed { get; init; }           // approved/pending customer invoices
    public DateOnly? Start { get; init; }          // first scheduled task
    public DateOnly? End { get; init; }            // last scheduled task

    public bool IsInternal => HowBidCameIn == "Internal Job";

    /// <summary>Costs to date ÷ budgeted cost. Null when there's no budget.</summary>
    public decimal? PercentComplete => BudgetCost > 0 ? ActualCost / BudgetCost * 100 : null;

    /// <summary>Billed to date ÷ contract. Null when there's no contract.</summary>
    public decimal? BilledPercent => Contract > 0 ? Billed / Contract * 100 : null;

    /// <summary>(Billed − costs) ÷ billed. Null until something is billed.</summary>
    public decimal? ProfitPercent => Billed > 0 ? (Billed - ActualCost) / Billed * 100 : null;

    public bool OverBudget => BudgetCost > 0 && ActualCost > BudgetCost;

    /// <summary>Billing trails the work by more than 10 points.</summary>
    public bool UnderBilled => PercentComplete is { } done && BilledPercent is { } billed && billed < Math.Min(done, 100) - 10;
}

public sealed record CostCodeLine(string? Number, string Name, decimal Budget, decimal Labor, decimal Bills, decimal Hours)
{
    public decimal Actual => Labor + Bills;
    public decimal Remaining => Budget - Actual;
    public decimal? PercentUsed => Budget > 0 ? Actual / Budget * 100 : null;
    public bool HasCosts => Actual != 0;
}

public sealed record ProjectDetail(string JobId, IReadOnlyList<CostCodeLine> CostCodes)
{
    public decimal TotalBudget => CostCodes.Sum(c => c.Budget);
    public decimal TotalActual => CostCodes.Sum(c => c.Actual);
    public decimal TotalHours => CostCodes.Sum(c => c.Hours);
}

/// <summary>Profit % color bands. Values come from the "Projects:Margin" settings.</summary>
public sealed class MarginBands
{
    public decimal Strong { get; set; } = 30;
    public decimal Okay { get; set; } = 25;
    public decimal Baseline { get; set; } = 20;

    public (string Label, string Css) For(decimal? profitPercent) => profitPercent switch
    {
        null => ("Not billed", "none"),
        var p when p >= Strong => ("Strong", "strong"),
        var p when p >= Okay => ("Okay", "okay"),
        var p when p >= Baseline => ("Baseline", "baseline"),
        _ => ("Below baseline", "below"),
    };
}
