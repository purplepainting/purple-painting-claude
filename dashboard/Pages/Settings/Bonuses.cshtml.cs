using System.Globalization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PurpleDashboard.Auth;
using PurpleDashboard.Data;
using PurpleDashboard.Modules.Financials;

namespace PurpleDashboard.Pages.Settings;

/// <summary>Settings → Bonus rules: admins edit the bonus rules (saved in the dashboard database, change-logged).</summary>
public class BonusesModel(BonusRulesStore store, Permissions permissions, DatabaseStatus database, CurrentUser me) : PageModel
{
    public BonusOptions Rules { get; private set; } = new();
    public BonusOptions Defaults => store.Defaults;
    public DateTime? UpdatedAt { get; private set; }
    public string? UpdatedBy { get; private set; }
    public bool CanEdit { get; private set; }
    public bool DatabaseReady => database.Available;
    [TempData] public string? Message { get; set; }
    public string? Error { get; private set; }

    public async Task OnGetAsync(CancellationToken ct)
    {
        ViewData["Title"] = "Bonus rules";
        ViewData["ModuleName"] = "Settings";
        var saved = await store.GetAsync(ct);
        Rules = saved.Rules; UpdatedAt = saved.UpdatedAt; UpdatedBy = saved.UpdatedBy;
        CanEdit = database.Available && permissions.Can(Perm.SettingsBonuses);
    }

    public async Task<IActionResult> OnPostAsync(CancellationToken ct)
    {
        if (!database.Available || !permissions.Can(Perm.SettingsBonuses)) { Message = "You can't change the bonus rules."; return RedirectToPage(); }
        var current = (await store.GetAsync(ct)).Rules;
        var f = Request.Form;
        var errors = new List<string>();
        decimal Dec(string name, decimal min, decimal max, decimal fallback)
        {
            if (!decimal.TryParse(f[name].ToString().Replace("%", "").Trim(), NumberStyles.Number, CultureInfo.InvariantCulture, out var v)) { errors.Add($"{name}: enter a number"); return fallback; }
            if (v < min || v > max) { errors.Add($"{name}: between {min} and {max}"); return fallback; }
            return v;
        }
        var r = current.Copy();
        r.SupervisorPercent = Dec("SupervisorPercent", 0, 100, current.SupervisorPercent);
        r.FieldPercent = Dec("FieldPercent", 0, 100, current.FieldPercent);
        r.HoursShare = Dec("HoursShare", 0, 100, current.HoursShare);
        r.EffortShare = 100 - r.HoursShare;
        r.CapMode = f["CapMode"].ToString() is "none" or "effort-multiple" or "hours-share-keep-excess" ? f["CapMode"].ToString() : current.CapMode;
        r.EffortCapMultiple = Dec("EffortCapMultiple", 1, 10, current.EffortCapMultiple);
        r.ForfeitMode = f["ForfeitMode"] == "redistribute" ? "redistribute" : "company";
        r.RetentionPercent = Dec("RetentionPercent", 0, 50, current.RetentionPercent);
        r.RetentionRelease = f["RetentionRelease"] == "job-close" ? "job-close" : "year-end";
        r.NegativeCarryOver = f["NegativeCarryOver"] == "true";
        r.HoldWhenCompanyLoses = f["HoldWhenCompanyLoses"] == "true";
        r.ProbationDays = (int)Dec("ProbationDays", 0, 365, current.ProbationDays);
        r.EffortScaleMax = (int)Dec("EffortScaleMax", 2, 100, current.EffortScaleMax);
        var start = f["StartMonth"].ToString();
        if (DateOnly.TryParseExact(start + "-01", "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _)) r.StartMonth = start; else errors.Add("Start month: pick a month");
        var accounts = f["BonusAccounts"].ToString().Trim();
        if (accounts.Length == 0) errors.Add("Bonus account: enter the account number or name (e.g. 6114)"); else r.BonusAccounts = accounts;
        if (errors.Count > 0) { Message = "Not saved: " + string.Join("; ", errors) + "."; return RedirectToPage(); }
        var n = await store.SaveAsync(r, me.Email ?? me.DisplayName ?? "unknown", ct);
        Message = n == 0 ? "No changes." : $"Saved {n} change{(n == 1 ? "" : "s")} to the bonus rules. They apply straight away (paid & locked months don't change).";
        return RedirectToPage();
    }
}
