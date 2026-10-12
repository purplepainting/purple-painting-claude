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
    public decimal ActualCost { get; init; }       // time entries + approved/pending vendor bills
    public decimal InvoicedNet { get; init; }      // approved/pending customer invoices, after retention held back
    public decimal RetentionHeld { get; init; }    // retention withheld on those invoices (earned, collected at the end)

    /// <summary>Earned and billed: invoices plus the retention held back on them.</summary>
    public decimal Billed => InvoicedNet + RetentionHeld;
    public DateOnly? Start { get; init; }          // first scheduled task
    public DateOnly? End { get; init; }            // last scheduled task
    public DateOnly? LastTimeEntry { get; init; }  // most recent clock-in on the job
    public DateOnly? NextScheduled { get; init; }  // first scheduled task today or later

    public bool IsInternal => HowBidCameIn == "Internal Job";

    public bool IsPreConstruction => Stage == "Pre-Construction / Color Submittals";

    /// <summary>
    /// Days between today and the nearest activity: the last clock-in (past) or the next scheduled
    /// day (future). Smaller = more active. Null when the job has neither.
    /// </summary>
    public int? ActivityDistance(DateOnly today)
    {
        int? last = LastTimeEntry is { } l ? today.DayNumber - l.DayNumber : null;
        int? next = NextScheduled is { } n ? n.DayNumber - today.DayNumber : null;
        return (last, next) switch
        {
            (null, null) => null,
            ({ } a, null) => a,
            (null, { } b) => b,
            ({ } a, { } b) => Math.Min(a, b),
        };
    }

    /// <summary>Billed to date (including retention) ÷ contract (the ring). Null when there's no contract.</summary>
    public decimal? BilledPercent => Contract > 0 ? Billed / Contract * 100 : null;

    /// <summary>Contract not yet billed. Retention already billed is not counted here.</summary>
    public decimal? LeftToBill => Contract is { } c ? c - Billed : null;

    /// <summary>Contract − costs to date: the job's profit if no more costs come in. Null without a contract.</summary>
    public decimal? Profit => Contract is { } c ? c - ActualCost : null;

    /// <summary>(Contract − costs) ÷ contract. Null without a contract.</summary>
    public decimal? ProfitPercent => Contract > 0 ? Profit / Contract * 100 : null;

    /// <summary>Invoiced (after retention) − costs: cash position on the job so far.</summary>
    public decimal CashPosition => InvoicedNet - ActualCost;

    /// <summary>Costs so far are more than what's been invoiced.</summary>
    public bool CostsOverBilled => ActualCost > Billed && ActualCost > 0;
}

/// <summary>Costs on one cost code. Codes on the job's estimate with no costs yet are listed with zeros.</summary>
public sealed record CostCodeLine(string? Number, string Name, decimal Labor, decimal Bills, decimal Hours)
{
    public decimal Actual => Labor + Bills;
    public bool HasCosts => Actual != 0;
}

public sealed record ProjectDetail(string JobId, IReadOnlyList<CostCodeLine> CostCodes)
{
    public decimal TotalActual => CostCodes.Sum(c => c.Actual);
    public decimal TotalHours => CostCodes.Sum(c => c.Hours);

    public decimal? ShareOfCost(CostCodeLine line) => TotalActual > 0 ? line.Actual / TotalActual * 100 : null;
}

/// <summary>Profit % color bands. Values come from the "Projects:Margin" settings.</summary>
public sealed class MarginBands
{
    public decimal Strong { get; set; } = 30;
    public decimal Okay { get; set; } = 25;
    public decimal Baseline { get; set; } = 20;

    public (string Label, string Css) For(decimal? profitPercent) => profitPercent switch
    {
        null => ("No contract", "none"),
        var p when p >= Strong => ("Strong", "strong"),
        var p when p >= Okay => ("Okay", "okay"),
        var p when p >= Baseline => ("Baseline", "baseline"),
        _ => ("Below baseline", "below"),
    };
}
