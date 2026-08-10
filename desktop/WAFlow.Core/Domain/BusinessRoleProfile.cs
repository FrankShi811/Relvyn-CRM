using System.Text;

namespace WAFlow.Core.Domain;

public sealed class BusinessRoleProfile
{
    public const string DefaultRoleName = "通用销售";
    public const string DefaultRoleSkillDescription =
        "识别客户需求和商业机会，结合可核验信息提出下一步建议；对外沟通、报价和承诺由人工确认。";

    public string OrganizationName { get; set; } = "";
    public string BusinessDescription { get; set; } = "";
    public string RoleName { get; set; } = DefaultRoleName;
    public string RoleSkillDescription { get; set; } = DefaultRoleSkillDescription;

    public static BusinessRoleProfile Normalize(BusinessRoleProfile? source) => new()
    {
        OrganizationName = Clean(source?.OrganizationName, 120),
        BusinessDescription = Clean(source?.BusinessDescription, 800),
        RoleName = Clean(source?.RoleName, 80, DefaultRoleName),
        RoleSkillDescription = Clean(
            source?.RoleSkillDescription,
            1200,
            DefaultRoleSkillDescription)
    };

    private static string Clean(string? value, int maximumLength, string fallback = "")
    {
        var builder = new StringBuilder();
        foreach (var character in value?.Trim() ?? "")
        {
            if (!char.IsControl(character) || character is '\r' or '\n' or '\t')
                builder.Append(character);
        }

        var normalized = builder.ToString().Trim();
        if (normalized.Length > maximumLength)
            normalized = normalized[..maximumLength].TrimEnd();
        return string.IsNullOrWhiteSpace(normalized) ? fallback : normalized;
    }
}
