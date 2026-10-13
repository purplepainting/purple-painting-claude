namespace PurpleDashboard.Modules.Projects;

/// <summary>One open JobTread job with the numbers the Projects list needs.</summary>
public sealed record ProjectSummary
{
    public required string Id { get; init; }
    public required string Number { get; init; }
    public required string Name { get; init; }
    public string? Customer { get; init; }
    /// <summary>The JobTread customer account id (the job location's account). Reviews are tracked per customer by this id.</summary>
    public string? CustomerId { get; init; }
    public string? Address { get; init; }
    /// <summary>The customer's "Left Review" field in JobTread: true = "Left a Review", false = "No Review", null = blank.</summary>
    public bool? CustomerLeftReview { get; init; }
    /// <summary>The customer account's "Lead Source" field in JobTread (e.g. "Google Ad", "Meta", "NextDoor", "Client Referral"); null when blank.</summary>
    public string? CustomerLeadSource { get; init; }
    /// <summary>When the customer account was created in JobTread (Pacific date). Ties sold jobs back to the new leads of a period.</summary>
    public DateOnly? CustomerCreatedOn { get; init; }
    public string? Stage { get; init; }            // Production Stage custom field
    public string? BidStage { get; init; }         // Job Walk or Digital Takeoff bid stage
    public string? JobType { get; init; }
    public string? ProjectManager { get; init; }
    public string? SalesPerson { get; init; }
    public string? HowBidCameIn { get; init; }
    public string? FolderUrl { get; init; }
    /// <summary>When the job was closed in JobTread (closedOn); null while open. Bonus retention is released then.</summary>
    public DateOnly? ClosedOn { get; init; }
    public decimal? ApprovedContract { get; init; } // approved customer orders (JobTread projectedPrice)
    /// <summary>Issue date of the job's first approved customer order (the proposal it was sold on). JobTread doesn't keep when it was approved.</summary>
    public DateOnly? FirstApprovedOrderOn { get; init; }

    /// <summary>
    /// Contract used everywhere: approved orders plus pending change orders, which are usually
    /// agreed and just not signed yet. On a bid (no production stage) pending is the proposal, so it's left out.
    /// </summary>
    public decimal? Contract => Stage is null || PendingOrders == 0
        ? ApprovedContract
        : (ApprovedContract ?? 0) + PendingOrders;

    public bool ContractIncludesPending => Stage is not null && PendingOrders > 0;

    public decimal PendingOrders { get; init; }    // customer orders sent, awaiting approval (pending change orders; for a bid, the proposal)
    public decimal UnsentChangeOrders { get; init; } // draft change orders not sent to the customer yet
    public int UnsentChangeOrderCount { get; init; }
    public decimal ActualCost { get; init; }       // time entries + approved/pending vendor bills
    public decimal InvoicedNet { get; init; }      // approved/pending customer invoices, after retention held back
    public decimal Unpaid { get; init; }           // balance still owed on approved/pending invoices
    public decimal RetentionHeld { get; init; }    // retention withheld on those invoices (earned, collected at the end)

    /// <summary>Earned and billed: invoices plus the retention held back on them.</summary>
    public decimal Billed => InvoicedNet + RetentionHeld;
    public DateOnly? Start { get; init; }          // first scheduled task
    public DateOnly? End { get; init; }            // last scheduled task
    public DateOnly? LastTimeEntry { get; init; }  // most recent clock-in on the job
    public DateOnly? NextScheduled { get; init; }  // first scheduled task today or later

    public bool IsInternal => HowBidCameIn == "Internal Job";

    /// <summary>Job type in one or two words for the main line.</summary>
    public string? JobTypeShort => JobType switch
    {
        "Residential - Standard Home" => "Residential",
        "Residential - Custom House" => "Custom Home",
        "Prevailing Wage" => "Public Works",
        "Property Management / Production" => "Property Mgmt",
        var t => t,
    };

    /// <summary>Money waiting on us (unsent change orders, finished job not fully billed or paid): a dot on the main line.</summary>
    public bool NeedsAttention => (IsCompleted && HasMoneyOutstanding) || UnsentChangeOrderCount > 0;

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

    /// <summary>Some of billed is retention held back (earned, not collected yet). Shown in orange.</summary>
    public bool BilledIncludesRetention => RetentionHeld >= 1;

    /// <summary>Billed to date (including retention) ÷ contract (the ring). Null when there's no contract.</summary>
    public decimal? BilledPercent => Contract > 0 ? Billed / Contract * 100 : null;

    /// <summary>Contract not yet billed. Retention already billed is not counted here.</summary>
    public decimal? LeftToBill => Contract is { } c ? c - Billed : null;

    /// <summary>
    /// Profit = billed − costs to date. Billed includes retention held (earned, collected at the end),
    /// so the PM sees money earned even when it isn't invoiced yet.
    /// </summary>
    public decimal Profit => Billed - ActualCost;

    /// <summary>(Billed − costs) ÷ billed. Null until something is billed.</summary>
    public decimal? ProfitPercent => Billed > 0 ? Profit / Billed * 100 : null;

    /// <summary>Contract − costs to date: where the job lands if no more costs come in. Reference only.</summary>
    public decimal? ProfitAtContract => Contract is { } c ? c - ActualCost : null;

    /// <summary>Invoiced (after retention) − costs: cash position on the job so far.</summary>
    public decimal CashPosition => InvoicedNet - ActualCost;

    /// <summary>Contract left to bill, unpaid invoices, or retention not collected yet.</summary>
    public bool HasMoneyOutstanding => LeftToBill >= 1 || Unpaid >= 1 || RetentionHeld >= 1;

    /// <summary>Total still to come in: left to bill + unpaid invoices + retention held.</summary>
    public decimal Outstanding => Math.Max(LeftToBill ?? 0, 0) + Unpaid + RetentionHeld;

    public bool IsCompleted => Stage == "Completed";

    /// <summary>Costs so far are more than what's been invoiced.</summary>
    public bool CostsOverBilled => ActualCost > Billed && ActualCost > 0;
}

/// <summary>Costs on one cost code. Codes on the job's estimate with no costs yet are listed with zeros.</summary>
public sealed record CostCodeLine(string? Number, string Name, decimal Labor, decimal Bills, decimal Hours)
{
    public decimal Actual => Labor + Bills;
    public bool HasCosts => Actual != 0;
}

/// <summary>Someone who has clocked time on the job. Pay rate is used only to pick the foreman; it's never shown.</summary>
public sealed record CrewMember(string Name, decimal Hours, DateOnly? LastWorked, bool IsSupervisor, decimal Rate);

public sealed record ProjectDetail(string JobId, IReadOnlyList<CostCodeLine> CostCodes, IReadOnlyList<CrewMember> Crew)
{
    /// <summary>
    /// Foreman = the highest-paid field person on the job (supervisors left out); ties go to
    /// whoever has the most hours. JobTread has no foreman field, so this is inferred.
    /// </summary>
    public CrewMember? Foreman => Crew.Where(c => !c.IsSupervisor && c.Rate > 0)
        .OrderByDescending(c => c.Rate).ThenByDescending(c => c.Hours).FirstOrDefault();

    /// <summary>Field crew, most hours first. Supervisors are listed separately.</summary>
    public IEnumerable<CrewMember> FieldCrew => Crew.Where(c => !c.IsSupervisor).OrderByDescending(c => c.Hours);
    public IEnumerable<CrewMember> Supervisors => Crew.Where(c => c.IsSupervisor).OrderByDescending(c => c.Hours);

    public decimal TotalActual => CostCodes.Sum(c => c.Actual);
    public decimal TotalHours => CostCodes.Sum(c => c.Hours);

    public decimal? ShareOfCost(CostCodeLine line) => TotalActual > 0 ? line.Actual / TotalActual * 100 : null;
}
