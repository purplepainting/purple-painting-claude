namespace PurpleDashboard.Integrations.JobTread;

/// <summary>
/// Fixed ids in Purple Painting's JobTread account. What each one means is in docs/JOBTREAD.md.
/// </summary>
public static class JobTreadIds
{
    public const string OrganizationId = "22PWNY9u7qZd";

    public static class JobFields
    {
        public const string ProductionStage = "22PWsEy5ZSXL";
        public const string JobType = "22PWsEVVW4aj";
        public const string ProjectManager = "22PXCNYAFP5P";
        public const string SalesPerson = "22PXCPYP4HNM";
        public const string HowBidCameIn = "22PWsDkAPRYB";
        public const string FolderLink = "22PYNFyRMxqa";       // "OneDrive Folder Link" (actually the SharePoint job folder)
        public const string JobWalkBidStage = "22PWRx62raQg";
        public const string DigitalBidStage = "22PWNYKhXZxr";
    }

    public static class CustomerFields
    {
        public const string LeftReview = "22PXCpKpiwSy";       // "Left Review": "Left a Review" / "No Review"
        public const string LeadSource = "22PWNYKhTU6S";       // "Lead Source": set by the website form (Google Ad, Meta, NextDoor, Website Form) or by hand
    }

    public static string JobUrl(string jobId) => $"https://app.jobtread.com/jobs/{jobId}";

    /// <summary>A customer account's page in JobTread (customer accounts live under Customers).</summary>
    public static string CustomerUrl(string accountId) => $"https://app.jobtread.com/customers/{accountId}";
}
