using System.Windows.Media;
using WAFlow.Core.Domain;

namespace WAFlow.Desktop;

/// <summary>
/// Presentation model shared by the WhatsApp conversation list and customer grid.
/// The WhatsApp color is kept as a semantic border while text and surfaces continue
/// to use the active desktop theme, so labels stay legible in both light and dark mode.
/// </summary>
public sealed class WhatsAppLabelChip
{
    private static readonly Brush[] AccentBrushes =
    [
        FrozenBrush("#C43D4B"), FrozenBrush("#C25A19"), FrozenBrush("#9A6A00"), FrozenBrush("#2E7D4F"),
        FrozenBrush("#0B7C72"), FrozenBrush("#08749A"), FrozenBrush("#2F69B1"), FrozenBrush("#5451A6"),
        FrozenBrush("#7A43A6"), FrozenBrush("#A33F76"), FrozenBrush("#9A4D55"), FrozenBrush("#A7652B"),
        FrozenBrush("#8A7424"), FrozenBrush("#4F7E43"), FrozenBrush("#397C78"), FrozenBrush("#39728B"),
        FrozenBrush("#52688D"), FrozenBrush("#6E5B91"), FrozenBrush("#895770"), FrozenBrush("#66736E")
    ];

    public WhatsAppLabelChip(string id, string accountId, string name, int color)
    {
        Id = id;
        AccountId = accountId;
        Name = name;
        Color = Math.Clamp(color, 0, AccentBrushes.Length - 1);
    }

    public string Id { get; }
    public string AccountId { get; }
    public string Name { get; }
    public int Color { get; }
    public Brush AccentBrush => AccentBrushes[Color];
    public string AccessibleName => $"WhatsApp 标签：{Name}";
    public string ToolTip => string.IsNullOrWhiteSpace(AccountId) ? Name : $"{Name}\nWhatsApp 账号：{AccountId}";

    public static WhatsAppLabelChip From(WhatsAppLabel label) =>
        new(label.Id, label.AccountId, label.Name, label.Color);

    private static Brush FrozenBrush(string value)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(value));
        brush.Freeze();
        return brush;
    }
}
