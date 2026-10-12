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
    public decimal Billed { get; init; }           // approved/pending customer invoices
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

    /// <summary>Billed to date ÷ contract (the ring). Null when there's no contract.</summary>
    public decimal? BilledPercent => Contract > 0 ? Billed / Contract * 100 : null;

    /// <summary>Contract not yet invoiced.</summary>
    public decimal? LeftToBill => Contract is { } c ? c - Billed : null;

    /// <summary>Billed − costs, in dollars.</summary>
    public decimal ProfitOnBilled => Billed - ActualCost;

    /// <summary>(Billed − costs) ÷ billed. Null until something is billed.</summary>
    public decimal? ProfitPercent => Billed > 0 ? ProfitOnBilled / Billed * 100 : null;

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
        null => ("Not billed", "none"),
        var p when p >= Strong => ("Strong", "strong"),
        var p when p >= Okay => ("Okay", "okay"),
        var p when p >= Baseline => ("Baseline", "baseline"),
        _ => ("Below baseline", "below"),
    };
}
