using MudBlazor;

namespace ParcInfo.Web.Components;

/// <summary>Thème aux couleurs Vitalrest (bleu du logo, vert de la vague).</summary>
public static class Theme
{
    public static readonly MudTheme Vitalrest = new()
    {
        PaletteLight = new PaletteLight
        {
            Primary = "#233d8e",
            Secondary = "#7cb342",
            Tertiary = "#2e7dbe",
            AppbarBackground = "#ffffff",
            AppbarText = "#1f2a44",
            Background = "#f4f6fb",
            DrawerBackground = "#ffffff",
            DrawerText = "#1f2a44",
            DrawerIcon = "#233d8e",
            TextPrimary = "#1f2a44",
            TextSecondary = "#5b6478",
            LinesDefault = "#e3e7ef",
            TableLines = "#e3e7ef",
        },
        Typography = new Typography
        {
            Default = new DefaultTypography { FontFamily = ["Inter", "system-ui", "Segoe UI", "sans-serif"] },
            H4 = new H4Typography { FontFamily = ["Inter", "system-ui", "sans-serif"], FontWeight = "700", FontSize = "1.6rem" },
            H5 = new H5Typography { FontFamily = ["Inter", "system-ui", "sans-serif"], FontWeight = "700", FontSize = "1.3rem" },
            H6 = new H6Typography { FontFamily = ["Inter", "system-ui", "sans-serif"], FontWeight = "600", FontSize = "1.05rem" },
        },
        LayoutProperties = new LayoutProperties { DefaultBorderRadius = "8px", DrawerWidthLeft = "250px" },
    };
}
