using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PurpleDashboard.Auth;
using PurpleDashboard.Data;
using PurpleDashboard.Infrastructure;
using PurpleDashboard.Modules.Hiring;

namespace PurpleDashboard.Pages.Employees.Interviews;

/// <summary>The Interview Questions sheet on a phone or tablet. Saving moves the candidate to Interviewed.</summary>
public class SheetModel(HiringService hiring, CurrentUser me) : PageModel
{
    [BindProperty(SupportsGet = true)] public int Id { get; set; }
    [BindProperty] public InterviewSheet Form { get; set; } = new();
    public HiringCandidate C { get; private set; } = null!;
    public bool CanEdit => hiring.Available;
    public bool IsNew { get; private set; }

    public async Task<IActionResult> OnGetAsync(CancellationToken ct)
    {
        if (await hiring.CandidateAsync(Id, ct) is not { } c) return CanEdit ? NotFound() : RedirectToPage("/Employees/Interviews/Index");
        C = c;
        ViewData["Title"] = $"Interview sheet · {c.Name}";
        ViewData["ModuleName"] = "Hiring";
        var existing = InterviewSheet.FromJson(c.SheetJson);
        IsNew = existing is null;
        if (existing is not null) { Form = existing; return Page(); }

        // A new sheet starts with what we already know.
        var days = await hiring.DaysAsync(ct);
        var booking = (await hiring.BookingsAsync(ct)).Where(b => b.CandidateId == Id)
            .Select(b => days.FirstOrDefault(d => d.Id == b.DayId)).OfType<InterviewDay>().MaxBy(d => d.Date);
        Form = new InterviewSheet
        {
            Date = booking?.Date is { } bd && bd <= LocalClock.Today ? bd : LocalClock.Today,
            Name = c.Name, Phone = c.Phone, Position = c.Position,
            Bilingual = c.Language == "both" ? true : null,
            Interviewer = me.DisplayName,
        };
        return Page();
    }

    public async Task<IActionResult> OnPostAsync(CancellationToken ct)
    {
        if (!CanEdit) return RedirectToPage(new { id = Id });
        if (await hiring.CandidateAsync(Id, ct) is null) return NotFound();
        var f = Form;
        f.Skills = f.Skills.Where(InterviewSheet.SkillOptions.Contains).Distinct().ToList();
        f.Points = f.Points.Distinct().Take(20).ToList();
        if (f.Transportation is not null && !InterviewSheet.TransportOptions.Contains(f.Transportation)) f.Transportation = null;
        if (f.Years is not null && !InterviewSheet.YearsOptions.Contains(f.Years)) f.Years = null;
        if (f.Rating is < 1 or > 5) f.Rating = null;
        foreach (var p in typeof(InterviewSheet).GetProperties().Where(p => p.PropertyType == typeof(string) && p.CanWrite))
        {
            var v = (string?)p.GetValue(f);
            p.SetValue(f, string.IsNullOrWhiteSpace(v) ? null : v.Trim()[..Math.Min(v.Trim().Length, 1000)]);
        }
        if (f.EverFired != true) f.FiredWhy = null;
        await hiring.SaveSheetAsync(Id, f, me.Email ?? me.DisplayName ?? "unknown", ct);
        TempData["Message"] = "Interview sheet saved.";
        return Redirect($"/employees/interviews/candidate/{Id}");
    }
}
